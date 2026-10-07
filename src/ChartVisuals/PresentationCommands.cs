#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SinmaiAlpha.ChartVisuals
{
    // Chart presentation only. The game clock and physical judge state never use these values.
    public sealed class PresentationChange
    {
        public double Time;
        public string Kind;
        public float Target, Duration, Frequency, Direction;
        public bool HasDirection;
        // Shared effect mode: 0 is a state switch, 1 a legacy short pulse,
        // and 2 an attack/hold/release envelope (including Instant).
        public int ShakeMode;
        public bool ShakeEnabled = true;
        public float Attack, HoldTime, Release;
        public float ParamA, ParamB;
        public string ColorHex;
        public bool Gradient = true;
        public string Encode()
        {
            if (JudgeLineExpansionCommands.IsKind(Kind)) return JudgeLineExpansionCommands.Encode(this);
            if (JudgeLineCommands.IsKind(Kind)) return JudgeLineCommands.Encode(this);
            if (ScreenEffectCommands.IsKind(Kind)) return ScreenEffectCommands.Encode(this);
            var basic = new[] { Target, Duration, Frequency, HasDirection ? 1f : 0f, Direction };
            if (Kind == "flash" || Kind == "tint")
                return "FX1|" + string.Join(",", basic.Concat(new[] { (float)ShakeMode, ShakeEnabled ? 1f : 0f, Attack, HoldTime, Release })
                    .Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "|" + (ColorHex ?? "");
            if (Kind == "shake" && (ShakeMode != 0 || ShakeEnabled && Target == 0))
                return "SH2|" + string.Join(",", basic.Concat(new[] { (float)ShakeMode, ShakeEnabled ? 1f : 0f, Attack, HoldTime, Release })
                    .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            return string.Join(",", basic.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        }
    }
    public static class PresentationCommands
    {
        public static bool IsKind(string kind) => kind == "showjudgeinfo" || kind == "showcomboinfo" || kind == "showjudgetext" || kind == "combodisplay" ||
            JudgeLineExpansionCommands.IsKind(kind) || JudgeLineCommands.IsKind(kind) || kind == "innerbrightness" || kind == "outerbrightness" || kind == "shake" || kind == "flash" || kind == "fade" || kind == "tint" || ScreenEffectCommands.IsKind(kind);
        public const float PlayerDefault = -1;
        public static bool IsPlayerBrightness(string kind) => kind == "innerbrightness" || kind == "outerbrightness";
        private static float Clamp(float v) => Math.Max(0, Math.Min(1, v));
        public static bool Duration(string text, float bpm, out float seconds)
        {
            if (VisualValue.Number(text.Trim(), out seconds)) return true;
            var p = text.Split(':'); seconds = 0;
            if (p.Length != 2 || bpm <= 0 || !int.TryParse(p[0], out var division) || division <= 0 ||
                !int.TryParse(p[1], out var count) || count < 0) return false;
            seconds = 240 / bpm / division * count;
            return !float.IsInfinity(seconds) && !float.IsNaN(seconds);
        }
        public static bool TryParse(string kind, string text, float bpm, out PresentationChange value)
        {
            value = null;
            if (JudgeLineExpansionCommands.IsKind(kind)) return JudgeLineExpansionCommands.TryParse(text, bpm, out value);
            if (JudgeLineCommands.IsKind(kind)) return JudgeLineCommands.TryParse(kind, text, bpm, out value);
            if (kind == "combodisplay") return ComboDisplayCommands.TryParse(text, bpm, out value);
            if (ScreenEffectCommands.IsKind(kind)) return ScreenEffectCommands.TryParse(kind, text, bpm, out value);
            if (!IsKind(kind) || !text.StartsWith("(") || !text.EndsWith(")")) return false;
            var p = text.Substring(1, text.Length - 2).Split(',');
            if (kind == "flash" || kind == "fade" || kind == "tint") return TryParseColorEffect(kind, p, bpm, out value);
            var v = new PresentationChange { Kind = kind };
            if (kind == "shake")
            {
                if (p[0].Trim().Equals("Instant", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Length < 4 || p.Length > 5 || !VisualValue.Number(p[1], out v.Target) ||
                        !VisualValue.Number(p[2], out v.Frequency) || !Duration(p[p.Length - 1], bpm, out v.HoldTime)) return false;
                    if (p.Length == 5)
                    {
                        if (!VisualValue.Number(p[3], out v.Direction)) return false;
                        v.Direction *= (float)Math.PI / 180;
                        v.HasDirection = true;
                    }
                    v.ShakeMode = 2; v.HoldTime = Math.Max(0, v.HoldTime); v.Duration = v.HoldTime;
                    value = v; return true;
                }
                if (!bool.TryParse(p[0].Trim(), out var enabled))
                {
                    if (p.Length == 2)
                    {
                        if (!Duration(p[0], bpm, out v.Duration) || !VisualValue.Number(p[1], out v.Target)) return false;
                        v.ShakeMode = 1; v.Duration = Math.Max(0, v.Duration);
                    }
                    else
                    {
                        if (p.Length < 4 || p.Length > 5 || !Duration(p[0], bpm, out v.Attack) ||
                            !Duration(p[1], bpm, out v.HoldTime) || !Duration(p[2], bpm, out v.Release) ||
                            !VisualValue.Number(p[3], out v.Target)) return false;
                        v.Attack = Math.Max(0, v.Attack); v.HoldTime = Math.Max(0, v.HoldTime); v.Release = Math.Max(0, v.Release);
                        v.Duration = v.Attack + v.HoldTime + v.Release; v.ShakeMode = 2;
                        if (float.IsInfinity(v.Duration)) return false;
                        v.Frequency = p.Length == 5 && VisualValue.Number(p[4], out var frequency) && frequency > 0 ? frequency : 18;
                        // The supplied runtime falls back to 18 for a missing,
                        // nonpositive or invalid legacy envelope frequency.
                    }
                    value = v; return true;
                }
                v.ShakeEnabled = enabled;
                if (!enabled)
                {
                    if (p.Length > 2 || p.Length == 2 && !Duration(p[1], bpm, out v.Duration)) return false;
                }
                else
                {
                    if (p.Length < 3 || p.Length > 5 || !VisualValue.Number(p[1], out v.Target) ||
                        !VisualValue.Number(p[2], out v.Frequency) || v.Frequency <= 0) return false;
                    if (p.Length >= 4 && p[3].Trim().Length != 0)
                    {
                        if (!VisualValue.Number(p[3], out v.Direction)) return false;
                        v.Direction *= (float)Math.PI / 180;
                        v.HasDirection = true;
                    }
                    if (p.Length == 5 && !Duration(p[4], bpm, out v.Duration)) return false;
                }
            }
            else
            {
                if (p.Length > 2) return false;
                var usePlayerDefault = IsPlayerBrightness(kind) && p[0].Trim().Equals("Default", StringComparison.OrdinalIgnoreCase);
                if (kind.StartsWith("show", StringComparison.Ordinal))
                { if (!bool.TryParse(p[0].Trim(), out var enabled)) return false; v.Target = enabled ? 1 : 0; }
                else if (usePlayerDefault) v.Target = PlayerDefault;
                else if (!VisualValue.Number(p[0], out v.Target)) return false;
                if (!usePlayerDefault) v.Target = Clamp(v.Target);
                if (p.Length == 2 && !Duration(p[1], bpm, out v.Duration)) return false;
            }
            v.Duration = Math.Max(0, v.Duration);
            value = v; return true;
        }
        private static bool HexColor(string text, out string color)
        {
            color = text.Trim().TrimStart('#').ToUpperInvariant();
            return color.Length == 6 && color.All(Uri.IsHexDigit);
        }
        private static bool TryParseColorEffect(string kind, string[] p, float bpm, out PresentationChange value)
        {
            value = null;
            var tint = kind == "tint";
            var v = new PresentationChange { Kind = tint ? "tint" : "flash", ColorHex = tint ? "FFFFFF" : null };
            if (p[0].Trim().Equals("Instant", StringComparison.OrdinalIgnoreCase))
            {
                if (p.Length != (tint ? 4 : 3) || tint && !HexColor(p[1], out v.ColorHex) ||
                    !VisualValue.Number(p[tint ? 2 : 1], out v.Target) || !Duration(p[p.Length - 1], bpm, out v.HoldTime)) return false;
                v.ShakeMode = 2; v.HoldTime = Math.Max(0, v.HoldTime); v.Duration = v.HoldTime;
            }
            else if (bool.TryParse(p[0].Trim(), out var enabled))
            {
                v.ShakeEnabled = enabled;
                if (!enabled)
                { if (p.Length > 2 || p.Length == 2 && !Duration(p[1], bpm, out v.Duration)) return false; }
                else
                {
                    var required = tint ? 3 : 2;
                    if (p.Length < required || p.Length > required + 1 || tint && !HexColor(p[1], out v.ColorHex) ||
                        !VisualValue.Number(p[required - 1], out v.Target)) return false;
                    if (p.Length > required && !Duration(p[required], bpm, out v.Duration)) return false;
                }
                v.Duration = Math.Max(0, v.Duration);
            }
            else if (p.Length == 2)
            {
                if (!Duration(p[0], bpm, out v.Duration) || !VisualValue.Number(p[1], out v.Target)) return false;
                v.ShakeMode = 1; v.Duration = Math.Max(0, v.Duration);
            }
            else
            {
                if (p.Length < 4 || p.Length > (tint ? 5 : 4) || !Duration(p[0], bpm, out v.Attack) ||
                    !Duration(p[1], bpm, out v.HoldTime) || !Duration(p[2], bpm, out v.Release) ||
                    !VisualValue.Number(p[3], out v.Target) || tint && p.Length == 5 && !HexColor(p[4], out v.ColorHex)) return false;
                v.Attack = Math.Max(0, v.Attack); v.HoldTime = Math.Max(0, v.HoldTime); v.Release = Math.Max(0, v.Release);
                v.Duration = v.Attack + v.HoldTime + v.Release; v.ShakeMode = 2;
                if (float.IsInfinity(v.Duration)) return false;
            }
            if (kind == "fade") v.Target = -Math.Abs(v.Target);
            value = v; return true;
        }
        public static bool Decode(string kind, string text, double time, out PresentationChange change)
        {
            if (JudgeLineExpansionCommands.IsKind(kind)) return JudgeLineExpansionCommands.Decode(text, time, out change);
            if (JudgeLineCommands.IsKind(kind)) return JudgeLineCommands.Decode(kind, text, time, out change);
            if (ScreenEffectCommands.IsKind(kind)) return ScreenEffectCommands.Decode(kind, text, time, out change);
            if (kind == "flash" || kind == "fade" || kind == "tint")
            {
                change = null; var packet = text.Split('|');
                if (packet.Length != 3 || packet[0] != "FX1" || !DecodeShake(packet[1], time, out var effect)) return false;
                if (kind == "tint") { if (!HexColor(packet[2], out effect.ColorHex)) return false; }
                else if (packet[2].Length != 0) return false;
                effect.Kind = kind == "tint" ? "tint" : "flash";
                if (kind == "fade") effect.Target = -Math.Abs(effect.Target);
                change = effect; return true;
            }
            if (kind == "shake" && text.StartsWith("SH2|", StringComparison.Ordinal))
                return DecodeShake(text.Substring(4), time, out change);
            change = null; var p = text.Split(','); var f = new float[5];
            if (kind == "combodisplay") return ComboDisplayCommands.Decode(text, time, out change);
            if (!IsKind(kind) || p.Length != f.Length) return false;
            for (var i = 0; i < f.Length; i++) if (!VisualValue.Number(p[i], out f[i])) return false;
            if (f[1] < 0 || f[3] != 0 && f[3] != 1) return false;
            change = new PresentationChange { Kind = kind, Time = time, Target = f[0], Duration = f[1],
                Frequency = f[2], HasDirection = f[3] == 1, Direction = f[4], ShakeEnabled = f[0] != 0 };
            return true;
        }
        private static bool DecodeShake(string text, double time, out PresentationChange change)
        {
            change = null; var parts = text.Split(','); var f = new float[10];
            if (parts.Length != f.Length) return false;
            for (var i = 0; i < f.Length; i++) if (!VisualValue.Number(parts[i], out f[i])) return false;
            if (f[1] < 0 || f[3] != 0 && f[3] != 1 || f[5] < 0 || f[5] > 2 || (int)f[5] != f[5] ||
                f[6] != 0 && f[6] != 1 || f[7] < 0 || f[8] < 0 || f[9] < 0) return false;
            if (f[5] == 2 && Math.Abs(f[1] - ((f[7] + f[8]) + f[9])) > .0001f) return false;
            change = new PresentationChange { Kind = "shake", Time = time, Target = f[0], Duration = f[1], Frequency = f[2],
                HasDirection = f[3] == 1, Direction = f[4], ShakeMode = (int)f[5], ShakeEnabled = f[6] == 1,
                Attack = f[7], HoldTime = f[8], Release = f[9] };
            return true;
        }
        public sealed class Track
        {
            private sealed class Segment { public PresentationChange Change, Source; public float From; }
            private readonly List<Segment> segments = new List<Segment>();
            private readonly float initial;
            private readonly ShakeTrack shake;
            private readonly ScreenEffectCommands.Track screenEffect;
            public Track(IEnumerable<PresentationChange> changes, string kind)
            {
                if (ScreenEffectCommands.IsKind(kind)) { screenEffect = new ScreenEffectCommands.Track(changes, kind); return; }
                if (kind == "shake" || kind == "flash" || kind == "tint") { shake = new ShakeTrack(changes.Where(c => c.Kind == kind).OrderBy(c => c.Time)); return; }
                initial = kind.StartsWith("show", StringComparison.Ordinal) ? 1 : 0;
                PresentationChange active = null;
                foreach (var change in changes.Where(c => c.Kind == kind).OrderBy(c => c.Time))
                {
                    var from = Evaluate(change.Time, out _);
                    var source = kind == "shake" && change.Target == 0 ? active : change;
                    segments.Add(new Segment { Change = change, From = from, Source = source });
                    active = change.Target == 0 ? null : change;
                }
            }
            public float Evaluate(double time, out PresentationChange source)
            {
                if (screenEffect != null) return screenEffect.Evaluate(time, out source);
                if (shake != null) return shake.Evaluate(time, out source);
                source = null;
                for (var i = segments.Count - 1; i >= 0; i--)
                {
                    var s = segments[i]; if (s.Change.Time > time) continue;
                    source = s.Source;
                    var p = s.Change.Duration <= 0 ? 1 : Clamp((float)((time - s.Change.Time) / (s.Change.Duration * 1000)));
                    return s.From + (s.Change.Target - s.From) * p;
                }
                return initial;
            }
            // The timeline is shared data; defaults are resolved per native view,
            // so two monitors never borrow each other's player option values.
            public bool IsPlayerBrightnessActive(double time)
            {
                PresentationChange active = null;
                foreach (var segment in segments)
                { if (segment.Change.Time > time) break; active = segment.Change; }
                return active != null && (active.Target != PlayerDefault || time < active.Time + active.Duration * 1000);
            }
            public float EvaluatePlayerBrightness(double time, float nativeValue)
            {
                var from = nativeValue;
                PresentationChange active = null;
                foreach (var segment in segments)
                {
                    if (segment.Change.Time > time) break;
                    from = BrightnessValue(active, from, segment.Change.Time, nativeValue);
                    active = segment.Change;
                }
                return BrightnessValue(active, from, time, nativeValue);
            }
            private static float BrightnessValue(PresentationChange change, float from, double time, float nativeValue)
            {
                if (change == null) return nativeValue;
                var target = change.Target == PlayerDefault ? nativeValue : change.Target;
                var progress = change.Duration <= 0 ? 1 : Clamp((float)((time - change.Time) / (change.Duration * 1000)));
                return from + (target - from) * progress;
            }
            public float Evaluate(double time) => Evaluate(time, out _);
            public void EvaluateVector(double time, out float x, out float y)
            {
                if (screenEffect != null) screenEffect.EvaluateVector(time, out x, out y);
                else { x = 0; y = 0; }
            }
            public bool IsUpcoming(double time, float leadSeconds) => screenEffect != null && screenEffect.IsUpcoming(time, leadSeconds);
        }

        // Mirrors ScreenEffectController.EffectTrack's SHAKE path: legacy
        // envelopes compete by absolute strength, then the state wins only
        // when strictly stronger. A pulse never becomes a state fade's source.
        private sealed class ShakeTrack
        {
            private sealed class State
            {
                public PresentationChange Change, Source;
                public float From, Target;
                public float Value(float seconds)
                {
                    var p = Change.Duration <= 0 ? 1 : Clamp((seconds - (float)(Change.Time * .001)) / Change.Duration);
                    return From + (Target - From) * p;
                }
            }
            private readonly List<PresentationChange> pulses = new List<PresentationChange>();
            private readonly List<State> states = new List<State>();
            public ShakeTrack(IEnumerable<PresentationChange> changes)
            {
                PresentationChange active = null;
                foreach (var change in changes)
                {
                    if (change.ShakeMode != 0) { pulses.Add(change); continue; }
                    var previous = states.Count == 0 ? null : states[states.Count - 1];
                    var source = change.ShakeEnabled ? change : active ?? previous?.Source;
                    states.Add(new State { Change = change, From = previous?.Value((float)(change.Time * .001)) ?? 0,
                        Target = change.ShakeEnabled ? change.Target : 0, Source = source });
                    active = change.ShakeEnabled ? change : null;
                }
            }
            public float Evaluate(double milliseconds, out PresentationChange source)
            {
                source = null; var result = 0f; var seconds = (float)(milliseconds * .001);
                foreach (var pulse in pulses)
                {
                    var start = pulse.Time * .001;
                    if (start > seconds) break;
                    // Reference EffectTrack only skips an expired prefix. A
                    // short pulse following a longer one must also expire.
                    if (start + pulse.Duration < seconds) continue;
                    var elapsed = seconds - (float)start;
                    float envelope;
                    if (pulse.ShakeMode == 1)
                    {
                        var progress = pulse.Duration <= 0 ? 1 : elapsed / pulse.Duration;
                        envelope = 1 - Math.Abs(progress * 2 - 1);
                    }
                    else if (elapsed < pulse.Attack) envelope = pulse.Attack <= 0 ? 1 : elapsed / pulse.Attack;
                    else if (elapsed <= pulse.Attack + pulse.HoldTime) envelope = 1;
                    else envelope = pulse.Release > 0 ? Clamp(1 - (elapsed - pulse.Attack - pulse.HoldTime) / pulse.Release) : 0;
                    var value = pulse.Target * envelope;
                    if (Math.Abs(value) > Math.Abs(result)) { result = value; source = pulse; }
                }
                for (var i = states.Count - 1; i >= 0; i--)
                {
                    var state = states[i];
                    if ((float)(state.Change.Time * .001) > seconds) continue;
                    var value = state.Value(seconds);
                    if (Math.Abs(value) > Math.Abs(result))
                    { result = value; source = Math.Abs(value) > .0001f ? state.Source : null; }
                    break;
                }
                return result;
            }
        }
    }
}
