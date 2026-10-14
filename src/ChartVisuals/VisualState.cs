#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

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
            { "tap", "each", "hold", "slide", "star", "slidestar", "break", "mine", "touch", "touchhold", "blockarea" };
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
                    if (pair.Length == 2 && NoiseControls.IsTarget(pair[0].Trim())) pair[0] = "blockarea";
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

        // Called per visual-leased note per frame (AfterVisualFrame / UpdateBorrowedNotes) while the GC is
        // disabled, so it allocates only the result: the type priority order used to be an iterator over a
        // closure (5 allocations per call); First walks the same candidates in the same order, eagerly.
        public static VisualValue Resolve(IList<VisualChange> changes, double time, VisualNote note, string family = null)
        {
            return Resolve(changes, time, note, family, new VisualValue());
        }

        // Same, filling a caller-owned value instead of a new one: every field is overwritten exactly as a fresh
        // VisualValue would be filled, so a per-frame scratch (never one of the changes' own values) can be reused.
        public static VisualValue Resolve(IList<VisualChange> changes, double time, VisualNote note, string family, VisualValue into)
        {
            family = family ?? note.Family;
            StreamIndex index = IndexOf(changes);
            into.Color = null;
            into.X = null;
            into.Y = null;
            into.Alpha = null;
            VisualValue visualValue2 = First(changes, index, time, note, family, "color");
            if (visualValue2 != null)
            {
                into.Color = visualValue2.Color;
            }
            visualValue2 = First(changes, index, time, note, family, "size");
            if (visualValue2 != null)
            {
                into.X = visualValue2.X;
                into.Y = visualValue2.Y;
            }
            visualValue2 = First(changes, index, time, note, family, "alpha");
            if (visualValue2 != null)
            {
                into.Alpha = visualValue2.Alpha;
            }
            return into;
        }

        // First value of this kind that is set, trying the types in priority order:
        // mine (and nothing else for a mine's colour), break, each, the family, tap for a star's colour,
        // then the untyped "" (except for a slide's size).
        private static VisualValue First(IList<VisualChange> changes, StreamIndex index, double time, VisualNote note, string family, string kind)
        {
            KindIndex kinds = null;
            if (index != null)
            {
                index.Kinds.TryGetValue(kind, out kinds);
            }
            // Noise zones never inherit untyped or note-family values.
            if (family == "blockarea") return Lookup(changes, index, kinds, time, kind, "blockarea");
            VisualValue visualValue;
            if (note.Mine)
            {
                visualValue = Lookup(changes, index, kinds, time, kind, "mine");
                if (Has(visualValue, kind))
                {
                    return visualValue;
                }
                if (kind == "color")
                {
                    return null;
                }
            }
            if (note.Break)
            {
                visualValue = Lookup(changes, index, kinds, time, kind, "break");
                if (Has(visualValue, kind))
                {
                    return visualValue;
                }
            }
            if (note.Each)
            {
                visualValue = Lookup(changes, index, kinds, time, kind, "each");
                if (Has(visualValue, kind))
                {
                    return visualValue;
                }
            }
            visualValue = Lookup(changes, index, kinds, time, kind, family);
            if (Has(visualValue, kind))
            {
                return visualValue;
            }
            if (kind == "color" && family == "star")
            {
                visualValue = Lookup(changes, index, kinds, time, kind, "tap");
                if (Has(visualValue, kind))
                {
                    return visualValue;
                }
            }
            if (kind != "size" || family != "slide")
            {
                visualValue = Lookup(changes, index, kinds, time, kind, "");
                if (Has(visualValue, kind))
                {
                    return visualValue;
                }
            }
            return null;
        }

        private static bool Has(VisualValue value, string kind)
        {
            if (value == null)
            {
                return false;
            }
            switch (kind)
            {
            case "color":
                return value.Color.HasValue;
            case "size":
                return value.X.HasValue;
            default:
                return value.Alpha.HasValue;
            }
        }

        // The change Lookup returns is the one with the highest list index among those with Time <= time + 1e-6 and this
        // exact Kind and Type (ordinal ==). The stream lists are built once per load, stably sorted by Time
        // (BuildVisualCommands' OrderBy) and never changed afterwards, so each (Kind, Type) subsequence is ascending and
        // its matches are a prefix of it: the backward scan's result is that prefix's last element, found here by binary
        // search instead of a scan over the whole list per type tried. NaN times never match (NaN <= x is false; OrderBy
        // puts them first) and are left out; a list with a null change or a non-ascending subsequence keeps the scan.
        private static VisualValue Lookup(IList<VisualChange> changes, StreamIndex index, KindIndex kinds, double time, string kind, string type)
        {
            if (index == null)
            {
                for (int num = changes.Count - 1; num >= 0; num--)
                {
                    VisualChange visualChange = changes[num];
                    if (visualChange.Time <= time + 1E-06 && visualChange.Kind == kind && visualChange.Type == type)
                    {
                        return visualChange.Value;
                    }
                }
                return null;
            }
            Series value;
            if (kinds == null)
            {
                value = null;
            }
            else if (type == null)
            {
                value = kinds.NullType;
            }
            else
            {
                kinds.Types.TryGetValue(type, out value);
            }
            if (value == null)
            {
                return null;
            }
            double num2 = time + 1E-06;
            double[] times = value.Times;
            int num3 = 0;
            int num4 = value.Count;
            while (num3 < num4)
            {
                int num5 = (num3 + num4) >> 1;
                if (times[num5] <= num2)
                {
                    num3 = num5 + 1;
                }
                else
                {
                    num4 = num5;
                }
            }
            if (num3 != 0)
            {
                return value.Values[num3 - 1];
            }
            return null;
        }

        private sealed class Series
        {
            public double[] Times = new double[4];

            public VisualValue[] Values = new VisualValue[4];

            public int Count;

            public void Add(double time, VisualValue value)
            {
                if (Count == Times.Length)
                {
                    Array.Resize(ref Times, Count * 2);
                    Array.Resize(ref Values, Count * 2);
                }
                Times[Count] = time;
                Values[Count] = value;
                Count++;
            }
        }

        private sealed class KindIndex
        {
            public readonly Dictionary<string, Series> Types = new Dictionary<string, Series>(StringComparer.Ordinal);

            public Series NullType;
        }

        private sealed class StreamIndex
        {
            public IList<VisualChange> Changes;

            public int Count;

            public bool Usable;

            public readonly Dictionary<string, KindIndex> Kinds = new Dictionary<string, KindIndex>(StringComparer.Ordinal);
        }

        private sealed class ReferenceComparer : IEqualityComparer<IList<VisualChange>>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(IList<VisualChange> x, IList<VisualChange> y)
            {
                return x == y;
            }

            public int GetHashCode(IList<VisualChange> obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        // Indexes by list identity. Every caller passes a VisualTimeline stream list, so this holds one entry per stream
        // of the loaded charts; ReleaseIndexes drops them at chart load and release (the cap bounds it regardless). The
        // one-entry front cache serves the usual case of a frame's leases all reading the same stream.
        private static readonly Dictionary<IList<VisualChange>, StreamIndex> Indexes = new Dictionary<IList<VisualChange>, StreamIndex>(ReferenceComparer.Instance);

        private static StreamIndex LastIndex;

        public static void ReleaseIndexes()
        {
            Indexes.Clear();
            LastIndex = null;
        }

        private static StreamIndex IndexOf(IList<VisualChange> changes)
        {
            if (changes == null)
            {
                return null;
            }
            StreamIndex value = LastIndex;
            if (value == null || value.Changes != changes)
            {
                if (!Indexes.TryGetValue(changes, out value))
                {
                    if (Indexes.Count >= 1024)
                    {
                        Indexes.Clear();
                    }
                    value = Build(changes);
                    Indexes.Add(changes, value);
                }
                LastIndex = value;
            }
            if (value.Count != changes.Count)
            {
                value = Build(changes);
                Indexes[changes] = value;
                LastIndex = value;
            }
            if (!value.Usable)
            {
                return null;
            }
            return value;
        }

        private static StreamIndex Build(IList<VisualChange> changes)
        {
            StreamIndex streamIndex = new StreamIndex
            {
                Changes = changes,
                Count = changes.Count,
                Usable = true
            };
            for (int i = 0; i < streamIndex.Count; i++)
            {
                VisualChange visualChange = changes[i];
                if (visualChange == null)
                {
                    streamIndex.Usable = false;
                    break;
                }
                if (visualChange.Kind == null || double.IsNaN(visualChange.Time))
                {
                    continue;
                }
                if (!streamIndex.Kinds.TryGetValue(visualChange.Kind, out var value))
                {
                    value = new KindIndex();
                    streamIndex.Kinds.Add(visualChange.Kind, value);
                }
                Series value2;
                if (visualChange.Type == null)
                {
                    value2 = value.NullType ?? (value.NullType = new Series());
                }
                else if (!value.Types.TryGetValue(visualChange.Type, out value2))
                {
                    value2 = new Series();
                    value.Types.Add(visualChange.Type, value2);
                }
                if (value2.Count > 0 && visualChange.Time < value2.Times[value2.Count - 1])
                {
                    streamIndex.Usable = false;
                    break;
                }
                value2.Add(visualChange.Time, visualChange.Value);
            }
            return streamIndex;
        }
    }
}