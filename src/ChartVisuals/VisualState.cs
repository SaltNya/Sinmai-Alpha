#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Shared by conversion and the game adapter. No Unity or preview-player dependency.
namespace SinmaiAlpha.ChartVisuals
{
    public sealed class VisualValue
    {
        public int? Color;
        public float? X, Y, Alpha;
        public static bool Number(string text, out float value) => float.TryParse(text,
            NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        public string Encode() => (Color.HasValue ? Color.Value.ToString("X6") : "-") + "|" +
            (X.HasValue ? X.Value.ToString("R", CultureInfo.InvariantCulture) + "," + Y.Value.ToString("R", CultureInfo.InvariantCulture) : "-") + "|" +
            (Alpha.HasValue ? Alpha.Value.ToString("R", CultureInfo.InvariantCulture) : "-");
        public static VisualValue Decode(string[] parts, int start)
        {
            var value = new VisualValue();
            if (parts[start] != "-") value.Color = int.Parse(parts[start], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (parts[start + 1] != "-")
            {
                var size = parts[start + 1].Split(',');
                value.X = float.Parse(size[0], CultureInfo.InvariantCulture); value.Y = float.Parse(size[1], CultureInfo.InvariantCulture);
            }
            if (parts[start + 2] != "-") value.Alpha = float.Parse(parts[start + 2], CultureInfo.InvariantCulture);
            return value;
        }
    }

    public sealed class VisualNote
    {
        public string Family;
        public bool Each, Break, Mine;
        public bool ReferenceMotion;
        public bool IgnoreSV; // Authored c: keep HS, but use elapsed time instead of SV scroll.
        public int SlideAppearance; // 0=native/legacy SC, 1=SlideDrop, 2=TouchSlideDrop
        public string ReferenceSlide; // Geometry/moving-star presentation only; never a playable judge route.
        public VisualValue Base = new VisualValue(), Guide = new VisualValue();
        public string Encode() => "VS|" + Family + "|" + ((Each ? 1 : 0) | (Break ? 2 : 0) | (Mine ? 4 : 0) |
            (ReferenceMotion ? 8 : 0) | (SlideAppearance << 4) | (IgnoreSV ? 64 : 0)) + "|" + Base.Encode() + "|" + Guide.Encode() +
            (string.IsNullOrEmpty(ReferenceSlide) ? "" : "|" + ReferenceSlide);
        public static VisualNote Decode(string text)
        {
            var p = text.Split('|');
            if ((p.Length != 9 && p.Length != 10) || p[0] != "VS") throw new FormatException("Invalid visual note metadata");
            var flags = int.Parse(p[2], CultureInfo.InvariantCulture);
            return new VisualNote { Family = p[1], Each = (flags & 1) != 0, Break = (flags & 2) != 0, Mine = (flags & 4) != 0,
                ReferenceMotion = (flags & 8) != 0, SlideAppearance = (flags >> 4) & 3, IgnoreSV = (flags & 64) != 0,
                Base = VisualValue.Decode(p, 3), Guide = VisualValue.Decode(p, 6), ReferenceSlide = p.Length == 10 ? p[9] : null };
        }
    }

    public sealed class VisualChange
    {
        public double Time;
        public string Kind, Type;
        public VisualValue Value; // NULL leaves a reset event, not a deleted event.
    }

    public static class VisualState
    {
        private static readonly HashSet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tap", "each", "hold", "slide", "star", "slidestar", "break", "mine", "touch", "touchhold" };
        public static bool IsKind(string kind) => kind == "color" || kind == "size" || kind == "alpha" ||
            kind == "colorv" || kind == "sizev" || kind == "alphav";

        public static bool TryParse(string kind, string text, double time, out List<VisualChange> changes)
        {
            changes = new List<VisualChange>();
            kind = kind.TrimEnd('v');
            var parts = new List<string>(); var depth = 0; var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                if (text[i] == ')' && --depth < 0) return false;
                if (text[i] == ',' && depth == 0) { parts.Add(text.Substring(start, i - start)); start = i + 1; }
            }
            if (depth != 0) return false;
            parts.Add(text.Substring(start));
            bool typed = text.Contains("=");
            foreach (var part in parts)
            {
                var pair = part.Split(new[] { '=' }, 2); var type = "";
                if (typed)
                {
                    if (pair.Length != 2 || !Types.Contains(pair[0].Trim())) return false;
                    type = pair[0].Trim().ToLowerInvariant();
                }
                else if (parts.Count != 1) return false;
                var raw = pair[pair.Length - 1].Trim(); var value = new VisualValue();
                if (!raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                {
                    if (kind == "color")
                    {
                        raw = raw.TrimStart('#');
                        if (raw.Length != 6 || !int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
                        value.Color = rgb;
                    }
                    else if (kind == "alpha")
                    {
                        if (!VisualValue.Number(raw, out var alpha)) return false;
                        value.Alpha = Math.Max(0f, Math.Min(1f, alpha));
                    }
                    else if (kind == "size")
                    {
                        float x, y;
                        if (raw.StartsWith("(") && raw.EndsWith(")"))
                        {
                            var xy = raw.Substring(1, raw.Length - 2).Split(',');
                            if (xy.Length != 2 || !VisualValue.Number(xy[0], out x) || !VisualValue.Number(xy[1], out y)) return false;
                        }
                        else { if (!VisualValue.Number(raw, out x)) return false; y = x; }
                        // Preserve Alpha's legacy zero-axis fallback.
                        var scale = (float)Math.Sqrt(Math.Abs((double)x * y));
                        value.X = x == 0 ? (scale == 0 ? 1 : scale) : x;
                        value.Y = y == 0 ? (scale == 0 ? 1 : scale) : y;
                    }
                    else return false;
                }
                changes.Add(new VisualChange { Time = time, Type = type, Kind = kind, Value = value });
            }
            return true;
        }

        public static VisualValue Resolve(IList<VisualChange> changes, double time, VisualNote note, string family = null)
        {
            family = family ?? note.Family;
            VisualValue Lookup(string kind, string type)
            {
                for (var i = changes.Count - 1; i >= 0; i--)
                    if (changes[i].Time <= time + .000001 && changes[i].Kind == kind && changes[i].Type == type) return changes[i].Value;
                return null;
            }
            IEnumerable<string> Priority(string kind)
            {
                if (note.Mine)
                {
                    yield return "mine";
                    if (kind == "color") yield break;
                }
                if (note.Break) yield return "break";
                if (note.Each) yield return "each";
                yield return family;
                if (kind == "color" && family == "star") yield return "tap";
                if (kind != "size" || family != "slide") yield return "";
            }
            var result = new VisualValue();
            foreach (var type in Priority("color")) { var v = Lookup("color", type); if (v?.Color != null) { result.Color = v.Color; break; } }
            foreach (var type in Priority("size")) { var v = Lookup("size", type); if (v?.X != null) { result.X = v.X; result.Y = v.Y; break; } }
            foreach (var type in Priority("alpha")) { var v = Lookup("alpha", type); if (v?.Alpha != null) { result.Alpha = v.Alpha; break; } }
            return result;
        }
    }
}
