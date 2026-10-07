#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SinmaiAlpha.ChartVisuals
{
    // Native-selected outline presentation. The selected sprite/style, geometry,
    // hit areas and note timing never change. NULL restores the player's RGBA.
    public static class JudgeLineCommands
    {
        public static bool IsKind(string kind) => kind == "jline" || kind == "judgeline";
        private static bool Hex(string raw, out string value)
        {
            value = raw.Trim().TrimStart('#').ToUpperInvariant();
            return value == "NULL" || (value.Length == 6 || value.Length == 8) && value.All(Uri.IsHexDigit);
        }
        public static bool TryParse(string kind, string body, float bpm, out PresentationChange value)
        {
            value = null;
            if (!IsKind(kind) || string.IsNullOrWhiteSpace(body)) return false;
            body = body.Trim();
            if (body.StartsWith("(") && body.EndsWith(")")) body = body.Substring(1, body.Length - 2);
            else if (body.StartsWith("(") || body.EndsWith(")")) return false;
            var pieces = body.Split(',');
            if (pieces.Length < 1 || pieces.Length > 2) return false;
            float duration = 0;
            if (pieces.Length == 2 && !PresentationCommands.Duration(pieces[1], bpm, out duration)) return false;
            var change = new PresentationChange { Kind = kind, Duration = Math.Max(0, duration) };
            if (kind == "jline")
            {
                if (!Hex(pieces[0], out var color)) return false;
                change.ColorHex = color;
            }
            else
            {
                var raw = pieces[0].Trim();
                if (raw.Equals("False", StringComparison.OrdinalIgnoreCase)) change.Target = 0;
                else if (raw.Equals("True", StringComparison.OrdinalIgnoreCase)) change.Target = 1;
                else if (raw.Equals("NULL", StringComparison.OrdinalIgnoreCase) || raw.Equals("Default", StringComparison.OrdinalIgnoreCase)) change.Target = -1;
                else if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var style) && style >= 0 && style <= 65535) change.Target = style;
                else return false;
            }
            value = change; return true;
        }
        public static string Encode(PresentationChange value) => "JL1|" +
            (value.Kind == "jline" ? value.ColorHex : value.Target.ToString("R", CultureInfo.InvariantCulture)) + "|" +
            value.Duration.ToString("R", CultureInfo.InvariantCulture);
        public static bool Decode(string kind, string text, double milliseconds, out PresentationChange value)
        {
            value = null;
            if (!IsKind(kind) || text == null || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return false;
            var packet = text.Split('|');
            if (packet.Length != 3 || packet[0] != "JL1" || !VisualValue.Number(packet[2], out var duration) || duration < 0) return false;
            var change = new PresentationChange { Kind = kind, Time = milliseconds, Duration = duration };
            if (kind == "jline")
            {
                if (!Hex(packet[1], out var color)) return false;
                change.ColorHex = color;
            }
            else
            {
                if (!VisualValue.Number(packet[1], out var style) || style != (int)style || style < -1 || style > 65535) return false;
                change.Target = style;
            }
            value = change; return true;
        }
        public struct Tint
        {
            public float R, G, B, A;
            public Tint(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
        }
        public sealed class Track
        {
            private sealed class Segment { public double Time; public float Duration; public Tint From, Target; }
            private readonly List<Segment> colors = new List<Segment>(), opacity = new List<Segment>();
            private readonly Tint original;
            public Track(IEnumerable<PresentationChange> changes, Tint initial)
            {
                original = initial;
                foreach (var change in changes.Where(c => IsKind(c.Kind)).OrderBy(c => c.Time))
                {
                    var target = original;
                    var track = change.Kind == "jline" ? colors : opacity;
                    if (change.Kind == "jline" && change.ColorHex != "NULL")
                    {
                        var hex = change.ColorHex.Substring(0, 6);
                        var rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        target.R = ((rgb >> 16) & 255) / 255f;
                        target.G = ((rgb >> 8) & 255) / 255f; target.B = (rgb & 255) / 255f;
                        target.A = change.ColorHex.Length == 8
                            ? int.Parse(change.ColorHex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f : 1;
                    }
                    if (change.Kind == "judgeline") target = new Tint(1, 1, 1, change.Target == 0 ? 0 : 1);
                    track.Add(new Segment { Time = change.Time, Duration = change.Duration,
                        From = Evaluate(track, change.Time, change.Kind == "jline" ? original : FullOpacity), Target = target });
                }
            }
            private static float Clamp(float value) => Math.Max(0, Math.Min(1, value));
            private static Tint Evaluate(List<Segment> track, double milliseconds, Tint initial)
            {
                for (var i = track.Count - 1; i >= 0; i--)
                {
                    var segment = track[i]; if (segment.Time > milliseconds) continue;
                    var p = segment.Duration <= 0 ? 1 : Clamp((float)((milliseconds - segment.Time) / (segment.Duration * 1000)));
                    return new Tint(segment.From.R + (segment.Target.R - segment.From.R) * p,
                        segment.From.G + (segment.Target.G - segment.From.G) * p,
                        segment.From.B + (segment.Target.B - segment.From.B) * p,
                        segment.From.A + (segment.Target.A - segment.From.A) * p);
                }
                return initial;
            }
            public Tint Evaluate(double milliseconds)
            {
                var tint = Evaluate(colors, milliseconds, original);
                tint.A *= Opacity(milliseconds); return tint;
            }
            // The expanded white copy follows JUDGELINE, independently of the
            // original outline's JLINE color alpha, as in AlphaDisplayController.
            public float Opacity(double milliseconds) => Evaluate(opacity, milliseconds, FullOpacity).A;
            private static readonly Tint FullOpacity = new Tint(1, 1, 1, 1);
        }
    }
}
