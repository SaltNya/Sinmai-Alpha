#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SinmaiAlpha.ChartVisuals
{
    // Names, bounds and uniforms from the supplied compiled AlphaExtraFilters.
    public static class ExtraScreenFilters
    {
        public sealed class Definition
        {
            public readonly string Kind, Tag, Uniform;
            public readonly float Minimum, Maximum;
            public Definition(string canonical, float minimum = 0, float maximum = 1)
            { Kind = canonical.ToLowerInvariant(); Tag = canonical.ToUpperInvariant(); Uniform = "_" + canonical; Minimum = minimum; Maximum = maximum; }
            public bool Accepts(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= Minimum && value <= Maximum;
        }
        public static readonly Definition[] All = {
            new Definition("Shatter"), new Definition("RadialBlur"), new Definition("Ripple"), new Definition("Pixelate"),
            new Definition("LensDistort", -1, 1), new Definition("Split"), new Definition("Kaleidoscope"),
            new Definition("Bloom", 0, 4), new Definition("Invert"), new Definition("Posterize")
        };
        public static Definition Find(string kind)
        { foreach (var value in All) if (value.Kind == kind) return value; return null; }
    }
    // Remaining Alpha screen effects. Commands and tracks are shared by the
    // converter and native reader; rendering never supplies gameplay results.
    public static class ScreenEffectCommands
    {
        public static bool IsKind(string kind) => kind == "gaussian" || kind == "neon" || kind == "trail" ||
            kind == "brightness" || kind == "saturation" || kind == "contrast" || kind == "rainbow" ||
            kind == "vignette" || kind == "zoom" || kind == "glitch" || kind == "tvnoise" ||
            kind == "hue" || kind == "move" || kind == "rotate" || ExtraScreenFilters.Find(kind) != null;

        private static float Clamp(float value) => Math.Max(0, Math.Min(1, value));
        private static bool Duration(string text, float bpm, out float value)
        {
            if (!PresentationCommands.Duration(text, bpm, out value)) return false;
            value = Math.Max(0, value); return true;
        }

        public static bool TryParse(string kind, string text, float bpm, out PresentationChange change)
        {
            change = null;
            if (!IsKind(kind) || string.IsNullOrEmpty(text) || !text.StartsWith("(") || !text.EndsWith(")")) return false;
            var values = text.Substring(1, text.Length - 2).Split(',');
            var move = kind == "move";
            var result = new PresentationChange { Kind = kind };
            if (kind == "vignette" && values.Length >= 3 && bool.TryParse(values[values.Length - 1].Trim(), out var gradient))
            {
                if (bool.TryParse(values[0].Trim(), out var on) && !on) return false;
                result.Gradient = gradient;
                Array.Resize(ref values, values.Length - 1);
            }
            if (values[0].Trim().Equals("Instant", StringComparison.OrdinalIgnoreCase))
            {
                if (values.Length != (move ? 4 : 3)) return false;
                if (move)
                {
                    if (!VisualValue.Number(values[1], out result.ParamA) || !VisualValue.Number(values[2], out result.ParamB)) return false;
                    result.Target = 1;
                }
                else
                {
                    if (!VisualValue.Number(values[1], out result.Target)) return false;
                    // Absolute zoom is used only by Instant and state forms.
                    // Both legacy forms author a delta from the default scale.
                    if (kind == "zoom") result.Target -= 1;
                }
                if (!Duration(values[values.Length - 1], bpm, out result.HoldTime)) return false;
                result.ShakeMode = 2; result.Duration = result.HoldTime;
            }
            else if (bool.TryParse(values[0].Trim(), out var enabled))
            {
                result.ShakeEnabled = enabled;
                if (!enabled)
                {
                    if (values.Length > 2 || values.Length == 2 && !Duration(values[1], bpm, out result.Duration)) return false;
                }
                else
                {
                    var required = move ? 3 : 2;
                    if (values.Length < required || values.Length > required + 1) return false;
                    if (move)
                    {
                        if (!VisualValue.Number(values[1], out result.ParamA) || !VisualValue.Number(values[2], out result.ParamB)) return false;
                        result.Target = 1;
                    }
                    else
                    {
                        if (!VisualValue.Number(values[1], out result.Target)) return false;
                        if (kind == "zoom") result.Target -= 1;
                    }
                    if (values.Length > required && !Duration(values[required], bpm, out result.Duration)) return false;
                }
            }
            else if (values.Length == 2)
            {
                if (!Duration(values[0], bpm, out result.Duration) || !VisualValue.Number(values[1], out result.Target)) return false;
                result.ShakeMode = 1;
                if (move) { result.ParamA = result.Target; result.Target = 1; }
            }
            else
            {
                if (values.Length < 4 || values.Length > (move ? 5 : 4) ||
                    !Duration(values[0], bpm, out result.Attack) || !Duration(values[1], bpm, out result.HoldTime) ||
                    !Duration(values[2], bpm, out result.Release) || !VisualValue.Number(values[3], out result.Target)) return false;
                result.ShakeMode = 2;
                result.Duration = (result.Attack + result.HoldTime) + result.Release;
                if (float.IsInfinity(result.Duration)) return false;
                if (move)
                {
                    result.ParamA = result.Target; result.Target = 1;
                    if (values.Length == 5 && !VisualValue.Number(values[4], out result.ParamB)) return false;
                }
            }
            var extra = ExtraScreenFilters.Find(kind);
            if (extra != null && !extra.Accepts(result.Target)) return false;
            change = result; return true;
        }

        public static string Encode(PresentationChange change)
        {
            var gradient = change.Kind == "vignette" && !change.Gradient;
            var values = new[] {
            change.Target, change.Duration, change.ParamA, change.ParamB, (float)change.ShakeMode,
            change.ShakeEnabled ? 1f : 0f, change.Attack, change.HoldTime, change.Release
            };
            return (gradient ? "EF2|" : "EF1|") + string.Join(",", (gradient ? values.Concat(new[] {0f}) : values)
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        }

        public static bool Decode(string kind, string text, double time, out PresentationChange change)
        {
            change = null;
            if (!IsKind(kind) || text == null) return false;
            var gradient = kind == "vignette" && text.StartsWith("EF2|", StringComparison.Ordinal);
            if (!gradient && !text.StartsWith("EF1|", StringComparison.Ordinal)) return false;
            var parts = text.Substring(4).Split(','); var values = new float[gradient ? 10 : 9];
            if (parts.Length != values.Length) return false;
            for (var i = 0; i < values.Length; i++) if (!VisualValue.Number(parts[i], out values[i])) return false;
            if (gradient && values[9] != 0 && values[9] != 1) return false;
            var extra = ExtraScreenFilters.Find(kind);
            if (extra != null && !extra.Accepts(values[0])) return false;
            if (values[1] < 0 || values[4] < 0 || values[4] > 2 || (int)values[4] != values[4] ||
                values[5] != 0 && values[5] != 1 || values[6] < 0 || values[7] < 0 || values[8] < 0) return false;
            if (values[4] == 2)
            {
                var sum = (values[6] + values[7]) + values[8];
                if (float.IsInfinity(sum) || Math.Abs(values[1] - sum) > .0001f) return false;
            }
            change = new PresentationChange { Kind = kind, Time = time, Target = values[0], Duration = values[1],
                ParamA = values[2], ParamB = values[3], ShakeMode = (int)values[4], ShakeEnabled = values[5] == 1,
                Attack = values[6], HoldTime = values[7], Release = values[8], Gradient = !gradient || values[9] == 1 };
            return true;
        }

        public sealed class Track
        {
            private sealed class State
            {
                public PresentationChange Change, Source;
                public float From, Target, FromA, FromB, TargetA, TargetB;
                public float Value(float seconds) => Interpolate(From, Target, seconds);
                public float A(float seconds) => Interpolate(FromA, TargetA, seconds);
                public float B(float seconds) => Interpolate(FromB, TargetB, seconds);
                private float Interpolate(float from, float target, float seconds)
                {
                    var progress = Change.Duration <= 0 ? 1 : Clamp((seconds - (float)(Change.Time * .001)) / Change.Duration);
                    return from + (target - from) * progress;
                }
            }
            private readonly List<PresentationChange> pulses = new List<PresentationChange>();
            private readonly List<State> states = new List<State>();
            private readonly List<PresentationChange> allEvents;
            private readonly bool triangular;
            public Track(IEnumerable<PresentationChange> changes, string kind)
            {
                triangular = kind == "vignette" || kind == "zoom" || kind == "hue" || kind == "move" || kind == "rotate" || ExtraScreenFilters.Find(kind) != null;
                allEvents = changes.Where(value => value.Kind == kind).OrderBy(value => value.Time).ToList();
                PresentationChange activeSource = null;
                foreach (var change in allEvents)
                {
                    if (change.ShakeMode != 0) { pulses.Add(change); continue; }
                    var previous = states.Count == 0 ? null : states[states.Count - 1];
                    var seconds = (float)(change.Time * .001);
                    states.Add(new State { Change = change, Source = change.ShakeEnabled ? change : activeSource ?? previous?.Source,
                        From = previous?.Value(seconds) ?? 0, FromA = previous?.A(seconds) ?? 0, FromB = previous?.B(seconds) ?? 0,
                        Target = change.ShakeEnabled ? change.Target : 0,
                        TargetA = change.ShakeEnabled ? change.ParamA : 0, TargetB = change.ShakeEnabled ? change.ParamB : 0 });
                    activeSource = change.ShakeEnabled ? change : null;
                }
            }
            private State StateAt(float seconds)
            {
                // Binary search permits seek/rewind without mutable cursor state.
                var lower = 0; var upper = states.Count;
                while (lower < upper)
                {
                    var mid = lower + (upper - lower) / 2;
                    if ((float)(states[mid].Change.Time * .001) <= seconds) lower = mid + 1;
                    else upper = mid;
                }
                return lower == 0 ? null : states[lower - 1];
            }
            public float Evaluate(double milliseconds, out PresentationChange source)
            {
                source = null; var result = 0f; var seconds = (float)(milliseconds * .001);
                foreach (var pulse in pulses)
                {
                    var start = pulse.Time * .001;
                    if (start > seconds) break;
                    // Independently expire overlaps; the reference's expired-
                    // prefix scan otherwise leaves a short nested pulse active.
                    if (start + pulse.Duration < seconds) continue;
                    var elapsed = seconds - (float)start;
                    float envelope;
                    if (pulse.ShakeMode == 1)
                    {
                        var progress = pulse.Duration <= 0 ? 1 : elapsed / pulse.Duration;
                        envelope = triangular ? 1 - Math.Abs(progress * 2 - 1) : Clamp(Math.Min(progress * 10, (1 - progress) * 10));
                    }
                    else if (elapsed < pulse.Attack) envelope = pulse.Attack <= 0 ? 1 : elapsed / pulse.Attack;
                    else if (elapsed <= pulse.Attack + pulse.HoldTime) envelope = 1;
                    else envelope = pulse.Release > 0 ? Clamp(1 - (elapsed - pulse.Attack - pulse.HoldTime) / pulse.Release) : 0;
                    var value = pulse.Target * envelope;
                    if (Math.Abs(value) > Math.Abs(result)) { result = value; source = pulse; }
                }
                var state = StateAt(seconds);
                if (state != null)
                {
                    var value = state.Value(seconds);
                    if (Math.Abs(value) > Math.Abs(result))
                    { result = value; source = Math.Abs(value) > .0001f ? state.Source : null; }
                }
                return result;
            }
            public void EvaluateVector(double milliseconds, out float x, out float y)
            {
                // The reference prioritizes the current state vector even when
                // a stronger legacy pulse wins scalar evaluation. Do not scale
                // an interpolated state vector by its separate intensity.
                var amount = Evaluate(milliseconds, out var source);
                var state = StateAt((float)(milliseconds * .001));
                if (state != null) { x = state.A((float)(milliseconds * .001)); y = state.B((float)(milliseconds * .001)); }
                else { x = source == null ? 0 : source.ParamA * amount; y = source == null ? 0 : source.ParamB * amount; }
            }
            public bool IsUpcoming(double milliseconds, float leadSeconds)
            {
                var seconds = (float)(milliseconds * .001); var lower = 0; var upper = allEvents.Count;
                while (lower < upper)
                {
                    var mid = lower + (upper - lower) / 2;
                    if (allEvents[mid].Time * .001 < seconds) lower = mid + 1;
                    else upper = mid;
                }
                if (lower == allEvents.Count) return false;
                var delta = (float)(allEvents[lower].Time * .001) - seconds;
                return delta >= 0 && delta <= leadSeconds;
            }
        }
    }
}
