using System;
using System.Collections.Generic;

namespace SinmaiAlpha.Notes;

// Pure presentation math. Times/scroll are milliseconds; speed is radius/ms.
// Mirrors AlphaVisualTiming without importing its preview note lifecycle.
internal static class ScrollVisualTiming
{
    internal const double Epsilon = .000001;
    internal readonly struct Presentation
    {
        internal readonly float Radius, Scale;
        internal readonly bool Running;
        internal Presentation(float radius, float scale, bool running)
        { Radius = radius; Scale = scale; Running = running; }
    }
    internal static Presentation Ring(double target, double current, float speed, float spawn, float destroy, bool everCrossed = false)
    {
        var direction = Math.Abs(destroy - spawn) <= Epsilon ? 1 : Math.Sign(destroy - spawn);
        var radius = destroy - direction * speed * (float)(target - current);
        var pathPosition = direction * (radius - spawn);
        var running = pathPosition >= -Epsilon || everCrossed;
        var scale = running ? 1 : Math.Max(0, Math.Min(1, (pathPosition + 2.5f) / 2.5f));
        return new Presentation(running ? radius : spawn, scale, running);
    }
    internal static float FirstCrossing(IReadOnlyList<(float Msec, float Scroll, float Max)> table, float target, float speed, float spawn, float destroy)
    {
        if (table.Count == 0) return float.NaN;
        if (Math.Abs(speed) <= .000000001) return table[0].Msec;
        var threshold = target - Math.Abs(destroy - spawn) / speed;
        bool Crossed(float scroll) => speed > 0 ? scroll >= threshold : scroll <= threshold;
        for (var i = 0; i < table.Count; i++)
        {
            if (Crossed(table[i].Scroll)) return table[i].Msec;
            if (i + 1 < table.Count && Crossed(table[i + 1].Scroll))
                return table[i].Msec + (table[i + 1].Msec - table[i].Msec) *
                    (threshold - table[i].Scroll) / (table[i + 1].Scroll - table[i].Scroll);
        }
        return float.NaN;
    }
    internal static float Slide(double currentScroll, double totalScroll, double duration)
    {
        if (duration <= .001) return 1;
        // The reference falls back to elapsed duration for a nonpositive total.
        // Dividing by a negative total would turn reverse SV into forward motion.
        var denominator = totalScroll > .001 ? totalScroll : duration;
        return (float)Math.Max(0, Math.Min(1, currentScroll / denominator));
    }
    internal static float FirstViewportEntry(IReadOnlyList<(float Msec, float Scroll, float Max)> table, float target, float speed, float spawn, float destroy, float frameRadius)
    {
        if (table.Count == 0) return float.NaN;
        var direction = Math.Abs(destroy - spawn) <= Epsilon ? 1 : Math.Sign(destroy - spawn);
        bool Visible(float scroll)
        {
            var radius = destroy - direction * speed * (target - scroll);
            var along = direction * (radius - spawn);
            return along >= -2.5f && Math.Abs(along >= 0 ? radius : spawn) <= frameRadius;
        }
        for (var i = 0; i < table.Count; i++)
        {
            if (Visible(table[i].Scroll)) return table[i].Msec;
            if (i + 1 == table.Count) break;
            var left = table[i]; var right = table[i + 1];
            var delta = right.Scroll - left.Scroll;
            if (Math.Abs(delta) <= .00001f || Math.Abs(speed) <= .000000001) continue;
            var fractions = new List<float> { 0, 1 };
            void Split(float scroll)
            {
                var fraction = (scroll - left.Scroll) / delta;
                if (fraction > 0 && fraction < 1) fractions.Add(fraction);
            }
            Split(target - (Math.Abs(destroy - spawn) + 2.5f) / speed);
            Split(target - Math.Abs(destroy - spawn) / speed);
            Split(target + (frameRadius - destroy) / (direction * speed));
            Split(target + (-frameRadius - destroy) / (direction * speed));
            fractions.Sort();
            for (var n = 0; n + 1 < fractions.Count; n++)
                if (Visible(left.Scroll + delta * ((fractions[n] + fractions[n + 1]) * .5f)))
                    return left.Msec + (right.Msec - left.Msec) * fractions[n];
        }
        return float.NaN;
    }

    internal static float SlideAppearanceOffset(float speed, float slideOption)
        => Math.Abs(speed) <= .0001f ? 0 : -3926.913f / Math.Abs(speed) * (1 - Math.Max(-1, Math.Min(1, slideOption)));
    internal readonly struct SlidePresentation
    {
        internal readonly float BodyAlpha, StarAlpha, StarScale;
        internal SlidePresentation(float bodyAlpha, float starAlpha)
        { BodyAlpha = bodyAlpha; StarAlpha = starAlpha; StarScale = starAlpha + .5f; }
    }
    internal static SlidePresentation SlideAppearance(float now, float head, float launch, float speed, float slideOption, bool touch)
    {
        float Fade(float target, float offset)
        {
            var start = target + offset;
            var end = Math.Min(start + 200, target);
            return end <= start ? now >= target ? 1 : 0 : Math.Max(0, Math.Min(1, (now - start) / (end - start)));
        }
        var lead = SlideAppearanceOffset(speed, slideOption);
        var body = Fade(head, lead);
        if (!touch && now < head)
            body = lead >= -.1f ? 0 : now >= head - 50 ? 1 : body * .55f;
        var star = now <= head ? 0 : now >= launch ? 1 : touch ? Fade(launch, lead) :
            Math.Max(0, Math.Min(1, (now - head) / (launch - head)));
        return new SlidePresentation(body, star);
    }

    internal static float TouchDuration(float speed) =>
        3.209385682f * (float)Math.Pow(Math.Max(Math.Abs(speed), .0001f), -.9549621752f);

    internal readonly struct TouchPresentation
    {
        internal readonly float Distance, Alpha, Timing;
        internal readonly bool GaugeVisible;
        internal TouchPresentation(float distance, float alpha, float timing, bool gaugeVisible)
        { Distance = distance; Alpha = alpha; Timing = timing; GaugeVisible = gaugeVisible; }
    }
    internal static TouchPresentation Touch(double targetMsec, double currentMsec, float speed, bool beforeJudge, float lastAlpha, bool lastGaugeVisible)
    {
        var timing = Math.Abs(speed) <= Epsilon ? 0f : Math.Sign(speed) * (float)((currentMsec - targetMsec) / 1000d);
        if (beforeJudge) timing = -Math.Abs(timing);
        var whole = TouchDuration(speed);
        var move = .8f * whole;
        var distance = Math.Max(0f, Math.Min(.4f, -(float)Math.Exp(8 * (timing * .4f / move) - .85f) + .42f));
        var alpha = lastAlpha;
        var gaugeVisible = lastGaugeVisible;
        if (-timing <= whole && -timing > move)
        {
            alpha = Math.Max(0f, Math.Min(1f, (whole + timing) / (.2f * whole)));
            gaugeVisible = false;
        }
        else if (-timing < move)
        {
            alpha = 1;
            gaugeVisible = true;
        }
        return new TouchPresentation(distance, alpha, timing, gaugeVisible);
    }
}
