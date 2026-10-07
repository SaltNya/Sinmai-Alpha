#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SinmaiAlpha.ChartVisuals
{
    // JUDGELINEEXPAND is a same-size rotated outline copy. Each command
    // restarts its angle from zero and holds the final angle after completion.
    public static class JudgeLineExpansionCommands
    {
        public const string Kind = "judgelineexpand";
        public static bool IsKind(string kind) => kind == Kind;

        private static bool Duration(string text, float bpm, out float seconds)
        {
            seconds = 0;
            text = text.Trim();
            if (text.StartsWith("-", StringComparison.Ordinal)) return false;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                return !float.IsNaN(seconds) && !float.IsInfinity(seconds) && seconds >= 0;
            var pieces = text.Split(':');
            if (pieces.Length != 2 || bpm <= 0 ||
                !int.TryParse(pieces[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var division) || division <= 0 ||
                !int.TryParse(pieces[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0) return false;
            seconds = 60f / bpm * 4f / division * count;
            return !float.IsNaN(seconds) && !float.IsInfinity(seconds);
        }

        public static bool TryParse(string text, float bpm, out PresentationChange change)
        {
            change = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (!text.StartsWith("(", StringComparison.Ordinal) || !text.EndsWith(")", StringComparison.Ordinal)) return false;
            var pieces = text.Substring(1, text.Length - 2).Split(',');
            if (pieces.Length != 2 || !Duration(pieces[0], bpm, out var duration)) return false;
            var direction = pieces[1].Trim().ToUpperInvariant();
            if (direction != "1" && direction != "-1" && direction != "CW" && direction != "CCW") return false;
            change = new PresentationChange { Kind = Kind, Duration = duration, Target = direction == "1" || direction == "CW" ? -22.5f : 22.5f };
            return true;
        }

        public static string Encode(PresentationChange change) => "JX1|" +
            change.Duration.ToString("R", CultureInfo.InvariantCulture) + "|" + change.Target.ToString("R", CultureInfo.InvariantCulture);

        public static bool Decode(string text, double milliseconds, out PresentationChange change)
        {
            change = null;
            if (text == null || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return false;
            var p = text.Split('|');
            if (p.Length != 3 || p[0] != "JX1" || !VisualValue.Number(p[1], out var duration) || duration < 0 ||
                !VisualValue.Number(p[2], out var angle) || angle != -22.5f && angle != 22.5f) return false;
            change = new PresentationChange { Kind = Kind, Time = milliseconds, Duration = duration, Target = angle };
            return true;
        }

        public sealed class Track
        {
            private readonly PresentationChange[] events;
            public Track(IEnumerable<PresentationChange> changes) => events = changes.Where(c => IsKind(c.Kind)).OrderBy(c => c.Time).ToArray();
            public bool Evaluate(double milliseconds, out float angle)
            {
                angle = 0;
                if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return false;
                var time = (float)(milliseconds / 1000d);
                var lo = 0; var hi = events.Length - 1; var found = -1;
                while (lo <= hi)
                {
                    var mid = lo + (hi - lo) / 2;
                    // The reference compares float seconds on both sides.
                    // Converting the clock back to double milliseconds delays
                    // fractional-time events by one update at their boundary.
                    if ((float)(events[mid].Time / 1000d) > time) hi = mid - 1;
                    else { found = mid; lo = mid + 1; }
                }
                if (found < 0) return false;
                var change = events[found];
                var progress = change.Duration <= 0 ? 1f : Math.Max(0f, Math.Min(1f, (time - (float)(change.Time / 1000d)) / change.Duration));
                angle = change.Target * progress;
                return true;
            }
        }
    }
}
