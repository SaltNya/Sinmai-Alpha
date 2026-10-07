#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SinmaiAlpha.ChartVisuals
{
    // Discrete modes: duration is accepted by the reference grammar but does
    // not interpolate between numeric enum values.
    public static class ComboDisplayCommands
    {
        public const int PlayerDefault = -1;
        private static readonly Dictionary<string, int> Aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "DEFAULT", PlayerDefault }, { "NONE", 0 }, { "OFF", 0 }, { "COMBO", 1 },
            { "SCORE", 2 }, { "SCORECLASSIC", 2 },
            { "ACHIEVEMENT", 3 }, { "ACC", 3 }, { "ACHIEVEMENTCLASSIC", 3 },
            { "ACCDOWN", 4 }, { "ACHIEVEMENTDOWNCLASSIC", 4 },
            { "DXACC", 11 }, { "ACHIEVEMENTDELUXE", 11 },
            { "DXACCDOWN", 12 }, { "ACHIEVEMENTDOWNDELUXE", 12 },
            { "DXSCORE", 13 }, { "SCOREDELUXE", 13 },
            { "CSCORE", 101 }, { "CSCOREDEDX", 101 }, { "CSCOREDEDELUXE", 101 },
            { "CSCOREDEDXDOWN", 102 }, { "CSCOREDOWNDEDELUXE", 102 },
        };
        public static bool Known(int mode) => mode == PlayerDefault || mode >= 0 && mode <= 4 || mode >= 11 && mode <= 13 || mode == 101 || mode == 102;
        public static bool Mode(string text, out int mode)
        {
            mode = 0;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim().Replace(" ", "").Replace("_", "");
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mode)) return Known(mode);
            if (Aliases.TryGetValue(text, out mode)) return true;
            mode = 0; return false;
        }
        public static bool TryParse(string text, float bpm, out PresentationChange value)
        {
            value = null;
            if (text == null || text.Length < 3 || text[0] != '(' || text[text.Length - 1] != ')') return false;
            var pieces = text.Substring(1, text.Length - 2).Split(',');
            if (pieces.Length > 2 || !Mode(pieces[0], out var mode)) return false;
            float duration = 0;
            if (pieces.Length == 2 && !PresentationCommands.Duration(pieces[1], bpm, out duration)) return false;
            value = new PresentationChange { Kind = "combodisplay", Target = mode, Duration = Math.Max(0, duration) };
            return true;
        }
        public static bool Decode(string text, double time, out PresentationChange value)
        {
            value = null;
            if (text == null || double.IsInfinity(time) || double.IsNaN(time)) return false;
            var pieces = text.Split(',');
            if (pieces.Length != 5) return false;
            var fields = new float[5];
            for (var i = 0; i < fields.Length; i++) if (!VisualValue.Number(pieces[i], out fields[i])) return false;
            if (fields[0] != (int)fields[0] || !Known((int)fields[0]) || fields[1] < 0 ||
                fields[2] != 0 || fields[3] != 0 || fields[4] != 0) return false;
            value = new PresentationChange { Kind = "combodisplay", Time = time, Target = fields[0], Duration = fields[1] };
            return true;
        }
        public sealed class Track
        {
            private readonly PresentationChange[] events;
            public Track(IEnumerable<PresentationChange> changes) =>
                events = changes.Where(c => c.Kind == "combodisplay" && c.Target == (int)c.Target && Known((int)c.Target))
                    .OrderBy(c => c.Time).ToArray();
            public int Evaluate(double milliseconds, int initial = PlayerDefault)
            {
                var low = 0; var high = events.Length - 1; var selected = -1;
                while (low <= high)
                {
                    var middle = low + (high - low) / 2;
                    if (events[middle].Time <= milliseconds) { selected = middle; low = middle + 1; }
                    else high = middle - 1;
                }
                return selected < 0 ? initial : (int)events[selected].Target;
            }
        }
    }
}
