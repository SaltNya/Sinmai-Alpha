#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SinmaiAlpha.ChartVisuals
{
    public sealed class SubtitleChange
    {
        public double Time;
        public string Text = "", Font = "", Style = "Fade";
        public float Duration = -1, X, Y, Size, Transition;
        public int Index;
        public string Encode() => string.Join("|", "TX1", Convert.ToBase64String(Encoding.UTF8.GetBytes(Text)),
            Duration.ToString("R", CultureInfo.InvariantCulture), X.ToString("R", CultureInfo.InvariantCulture),
            Y.ToString("R", CultureInfo.InvariantCulture), Size.ToString("R", CultureInfo.InvariantCulture),
            Font, Index.ToString(CultureInfo.InvariantCulture), Style, Transition.ToString("R", CultureInfo.InvariantCulture));
    }

    // Matches the reference subtitle parser and its indexed OnGUI timeline.
    // Payload strings are encoded so tabs/newlines never become MA2 columns.
    public static class SubtitleCommands
    {
        private static float Clamp(float value, float minimum, float maximum) => Math.Max(minimum, Math.Min(maximum, value));
        private static bool Number(string text, float minimum, float maximum, out float value)
        {
            if (!VisualValue.Number(text.Trim(), out value)) return false;
            value = Clamp(value, minimum, maximum); return true;
        }
        private static bool Split(string text, out List<string> values)
        {
            values = new List<string>(); var depth = 0; var quoted = false; var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\')) { quoted = !quoted; continue; }
                if (quoted) continue;
                if (c == '(') depth++;
                else if (c == ')') { if (depth == 0) return false; depth--; }
                else if (c == ',' && depth == 0) { values.Add(text.Substring(start, i - start).Trim()); start = i + 1; }
            }
            if (depth != 0 || quoted) return false;
            values.Add(text.Substring(start).Trim()); return true;
        }
        public static bool NormalizeFont(string value, out string normalized)
        {
            var compact = (value ?? "").Trim().Replace(" ", "").Replace("-", "").ToUpperInvariant();
            switch (compact)
            {
                case "DEFAULT": normalized = "Default"; break;
                case "CASCADIAMONO": normalized = "CascadiaMono"; break;
                case "CASCADIACODE": normalized = "CascadiaCode"; break;
                case "YAHEI": case "MICROSOFTYAHEI": normalized = "MicrosoftYaHei"; break;
                case "NOTOSANSSC": normalized = "NotoSansSC"; break;
                case "SIMSUN": normalized = "SimSun"; break;
                case "DENGXIAN": normalized = "DengXian"; break;
                case "NOTOSERIFSC": normalized = "NotoSerifSC"; break;
                case "GLOBALMONOSPACE": normalized = "GlobalMonospace"; break;
                case "AILERON": normalized = "Aileron"; break;
                case "ALLERTA": normalized = "Allerta"; break;
                default: normalized = ""; return false;
            }
            return true;
        }
        public static bool NormalizeStyle(string value, out string normalized)
        {
            var text = (value ?? "").Trim(); normalized = "";
            if (text.Equals("Default", StringComparison.OrdinalIgnoreCase) || text.Equals("Fade", StringComparison.OrdinalIgnoreCase)) normalized = "Fade";
            else if (text.Equals("Typewriter", StringComparison.OrdinalIgnoreCase)) normalized = "Typewriter";
            return normalized.Length != 0;
        }
        private static bool Seconds(string text, float bpm, out float value)
        {
            if (!PresentationCommands.Duration(text, bpm, out value)) return false;
            value = Math.Max(0, value); return true;
        }
        private static bool Option(string text, float bpm, SubtitleChange value)
        {
            var equal = text.IndexOf('='); if (equal <= 0) return false;
            var key = text.Substring(0, equal).Trim().ToLowerInvariant(); var word = text.Substring(equal + 1).Trim();
            switch (key)
            {
                case "x": return Number(word, 0, 1, out value.X);
                case "y": return Number(word, 0, 1, out value.Y);
                case "size": return Number(word, 8, 200, out value.Size);
                case "font": return NormalizeFont(word, out value.Font);
                case "index": return int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out value.Index) && value.Index >= 0;
                case "style": return NormalizeStyle(word, out value.Style);
                case "transition": return Seconds(word, bpm, out value.Transition);
                default: return false;
            }
        }
        public static bool TryParse(string body, float bpm, out SubtitleChange value)
        {
            value = null;
            if (body == null) return false;
            body = body.Trim();
            if (body.Length < 4 || body[0] != '(' || body[body.Length - 1] != ')' ||
                !Split(body.Substring(1, body.Length - 2), out var p) || p[0].Length < 2 || p[0][0] != '"' || p[0][p[0].Length - 1] != '"') return false;
            var result = new SubtitleChange { Text = p[0].Substring(1, p[0].Length - 2).Replace("\\\"", "\"") };
            if (p.Count > 1 && p.Skip(1).All(word => !word.Contains("=")))
            {
                if (p[1].Length != 0 && !Seconds(p[1], bpm, out result.Duration)) return false;
                if (p.Count > 2 && p[2].Length != 0 && !Number(p[2], 0, 1, out result.X)) return false;
                if (p.Count > 3 && p[3].Length != 0 && !Number(p[3], 0, 1, out result.Y)) return false;
                if (p.Count > 4 && p[4].Length != 0 && !Number(p[4], 8, 200, out result.Size)) return false;
                if (p.Count > 5 && p[5].Length != 0 && !NormalizeFont(p[5], out result.Font)) return false;
                if (p.Count > 6 && p[6].Length != 0 && (!int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out result.Index) || result.Index < 0)) return false;
                if (p.Count > 7 && p[7].Length != 0 && !NormalizeStyle(p[7], out result.Style)) return false;
                if (p.Count > 8 && p[8].Length != 0 && !Seconds(p[8], bpm, out result.Transition)) return false;
            }
            else for (var i = 1; i < p.Count; i++)
            {
                if (Option(p[i], bpm, result)) continue;
                if (result.Duration < 0 && Seconds(p[i], bpm, out var duration)) { result.Duration = duration; continue; }
                return false;
            }
            value = result; return true;
        }
        public static bool Decode(string text, double time, out SubtitleChange value)
        {
            value = null; if (text == null) return false;
            var p = text.Split('|'); if (p.Length != 10 || p[0] != "TX1") return false;
            var result = new SubtitleChange { Time = time };
            if (!VisualValue.Number(p[2], out result.Duration) || result.Duration < -1 ||
                !Number(p[3], 0, 1, out result.X) || !Number(p[4], 0, 1, out result.Y) || !VisualValue.Number(p[5], out result.Size) ||
                !int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out result.Index) || result.Index < 0 ||
                !NormalizeStyle(p[8], out result.Style) || !VisualValue.Number(p[9], out result.Transition) || result.Transition < 0) return false;
            if (result.Size != 0) result.Size = Clamp(result.Size, 8, 200);
            if (p[6].Length != 0 && !NormalizeFont(p[6], out result.Font)) return false;
            try { result.Text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(p[1])); }
            catch (FormatException) { return false; }
            catch (DecoderFallbackException) { return false; }
            value = result; return true;
        }
        public static string TypewriterText(string text, float elapsed, float transition)
        {
            if (transition <= 0 || elapsed >= transition) return text;
            var elements = StringInfo.ParseCombiningCharacters(text);
            var count = (int)Clamp((float)Math.Ceiling(elements.Length * elapsed / transition), 0, elements.Length);
            return count == 0 ? "" : text.Substring(0, count == elements.Length ? text.Length : elements[count]);
        }
        public sealed class Timeline
        {
            private readonly List<SubtitleChange> events;
            private readonly SortedDictionary<int, SubtitleChange> active = new SortedDictionary<int, SubtitleChange>();
            private int cursor = -1;
            private double last = double.NegativeInfinity;
            public Timeline(IEnumerable<SubtitleChange> changes)
            { events = changes.ToList(); events.Sort((a, b) => a.Time.CompareTo(b.Time)); }
            public IEnumerable<SubtitleChange> At(double milliseconds)
            {
                if (milliseconds < last) { cursor = -1; active.Clear(); }
                last = milliseconds;
                while (cursor + 1 < events.Count && events[cursor + 1].Time <= milliseconds)
                { var next = events[++cursor]; active[next.Index] = next; }
                return active.Values.Where(ev => ev.Text.Length != 0 && (ev.Duration < 0 || milliseconds <= ev.Time + ev.Duration * 1000));
            }
        }
    }
}
