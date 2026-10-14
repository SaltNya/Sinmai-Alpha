#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SinmaiAlpha.ChartVisuals
{
    // Per-stream controls travel with the region, relative to its judgment time.
    // This avoids leaking main-stream state into independent streams or notes.
    public sealed class NoiseControls
    {
        public sealed class Entry { public double Time; public string Kind, Value; }
        public readonly List<Entry> Entries = new List<Entry>();
        private readonly List<VisualChange> normal = new List<VisualChange>(), live = new List<VisualChange>();
        public static bool IsTarget(string value) => value.Equals("blockarea", StringComparison.OrdinalIgnoreCase) || value.Equals("noise", StringComparison.OrdinalIgnoreCase);
        public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", Entries.Select(e =>
            e.Time.ToString("R", CultureInfo.InvariantCulture) + "|" + e.Kind + "|" + e.Value))));
        public static bool Decode(string text, out NoiseControls value)
        {
            value = null;
            try
            {
                var result = new NoiseControls();
                var body = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                if (body.Length != 0) foreach (var line in body.Split('\n'))
                {
                    var p = line.Split(new[] {'|'}, 3);
                    if (p.Length != 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) || double.IsNaN(time) || double.IsInfinity(time)) return false;
                    var kind = p[1];
                    if (kind == "sv" || kind == "hs")
                    {
                        foreach (var part in p[2].Split(','))
                        {
                            var eq = part.IndexOf('=');
                            if (eq >= 0 && !IsTarget(part.Substring(0, eq).Trim())) return false;
                            if (!NoiseZoneSpec.TryMultiplier(eq < 0 ? part : part.Substring(eq+1), out _)) return false;
                        }
                    }
                    else if (VisualState.IsKind(kind) && VisualState.TryParse(kind, p[2], time, out var changes))
                    {
                        if (changes.Any(c => c.Type != "blockarea")) return false;
                        (kind.EndsWith("v", StringComparison.Ordinal) ? result.live : result.normal).AddRange(changes);
                    }
                    else return false;
                    result.Entries.Add(new Entry { Time = time, Kind = kind, Value = p[2] });
                }
                var ordered = result.Entries.OrderBy(e => e.Time).ToArray(); result.Entries.Clear(); result.Entries.AddRange(ordered);
                value = result; return true;
            }
            catch (FormatException) { return false; }
        }
        public float Speed(string kind, double time)
        {
            float global = 1; float? typed = null;
            foreach (var e in Entries)
            {
                if (e.Time > time + 1e-7) break;
                if (e.Kind != kind) continue;
                foreach (var part in e.Value.Split(','))
                {
                    var eq = part.IndexOf('='); var raw = (eq < 0 ? part : part.Substring(eq+1)).Trim();
                    NoiseZoneSpec.TryMultiplier(raw, out var number);
                    if (eq < 0) global = number;
                    else typed = raw.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? (float?)null : number;
                }
            }
            return typed ?? global;
        }
        public double ScrollDistance(double from, double to)
        {
            if (from > to) return -ScrollDistance(to, from);
            var cursor = from; double sum = 0;
            foreach (var e in Entries)
            {
                if (e.Kind != "sv" || e.Time <= from || e.Time >= to) continue;
                sum += (e.Time - cursor) * Speed("sv", cursor); cursor = e.Time;
            }
            return sum + (to - cursor) * Speed("sv", cursor);
        }
        public VisualValue Style(double time)
        {
            var note = new VisualNote { Family = "blockarea" };
            var a = VisualState.Resolve(normal, 0, note); var b = VisualState.Resolve(live, time, note);
            return new VisualValue { Color = b.Color ?? a.Color, X = b.X ?? a.X, Y = b.Y ?? a.Y, Alpha = b.Alpha ?? a.Alpha };
        }
        // Mirrors Alpha060's absolute cumulative-scroll warning threshold.
        public float WarningProgress(double relativeTime, double nativeLead)
        {
            if (relativeTime >= 0) return -1;
            var hs = Speed("hs", 0);
            if (Math.Abs(hs) <= 1e-6f) return 1;
            var duration = nativeLead / Math.Abs(hs);
            var distance = Math.Abs(ScrollDistance(relativeTime, 0));
            return distance >= duration ? -1 : (float)(1 - distance / duration);
        }
    }
}
