using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

public sealed record JudgeStep(int[] Panels, double Enter, double Exit,
    double Release, bool RequireEntry = false, string GateReason = "",
    ulong RequireRearmMask = 0)
{
    public double Push => Exit - Enter;
    public ulong Mask => Panels.Aggregate(0UL, (mask, panel) => mask | (1UL << panel));
}
public sealed record ChoiceWindow(int[] Panels, double Enter, double Exit, string Reason);

public sealed record IntervalTable(ArcLengthPath Path, IReadOnlyList<SensorVisit> Visits,
    IReadOnlyList<JudgeStep> Steps, IReadOnlyList<string> Diagnostics, IReadOnlyList<ChoiceWindow>? ChoiceWindows = null)
{
    public double Lead => Steps.Count > 0 ? Steps[0].Enter : Path.Length;
    public double Tail => Steps.Count > 0 ? Path.Length - Steps[Steps.Count - 1].Exit : 0;
    public bool CanExportDistances => Path.Length > 1e-7 && Steps.Count > 0 && Lead < 1e-7 && Tail < 1e-7;
    // This is only the distance-based last-wait component, not the game's
    // complete score/grading windows or latency calibration.
    public double? TailWait(double travelMs) => CanExportDistances ? travelMs * Steps[Steps.Count - 1].Push / Path.Length : null;
    public string Queue => string.Join(" → ", Steps.Select(s => string.Join("/", s.Panels.OrderBy(value => value).Select(RegionModels.Name))));
}

public static class IntervalJudge
{
    sealed record Group(int[] Panels, double Enter, double Exit)
    {
        public double Mid => (Enter + Exit) / 2;
    }

    public static IntervalTable Build(ArcLengthPath path, IReadOnlyList<SensorRegion> regions, bool corridorChoices = false)
    {
        var visits = RegionIntersections.Find(path, regions);
        var choices = corridorChoices ? CorridorChoices.FindGapCrossings(path, visits) : new List<ChoiceWindow>();
        var diagnostics = new List<string>();
        if (path.Length <= 1e-7) diagnostics.Add("zero-length: stationary input needs an explicit gameplay policy; no invented distance");
        if (visits.Count == 0 && choices.Count == 0)
            return new(path, visits, [], [.. diagnostics, "no-region: no fabricated C fallback"]);

        // Any-of is deliberately narrow: same-key A/B visits with practically
        // coincident traversal windows. Merely touching/overlapping another
        // region does not make an arbitrary chain of sensors interchangeable.
        var groups = new List<Group>();
        var consumed = new bool[visits.Count];
        for (int i = 0; i < visits.Count; i++)
        {
            if (consumed[i]) continue;
            var a = visits[i]; int match = -1;
            if (a.Panel < 16)
            {
                for (int j = i + 1; j < visits.Count && visits[j].Enter <= a.Exit; j++)
                {
                    if (consumed[j] || visits[j].Panel != (a.Panel ^ 8)) continue;
                    var b = visits[j];
                    double shorter = Math.Min(a.Exit - a.Enter, b.Exit - b.Enter);
                    double overlap = Math.Min(a.Exit, b.Exit) - Math.Max(a.Enter, b.Enter);
                    if (overlap >= .9 * shorter &&
                        Math.Abs(a.Enter - b.Enter) <= .1 * shorter &&
                        Math.Abs(a.Exit - b.Exit) <= .1 * shorter) { match = j; break; }
                }
            }
            if (match < 0) groups.Add(new([a.Panel], a.Enter, a.Exit));
            else
            {
                consumed[match] = true; var b = visits[match];
                groups.Add(new(new[] { a.Panel, b.Panel }.OrderBy(value => value).ToArray(),
                    Math.Min(a.Enter, b.Enter), Math.Max(a.Exit, b.Exit)));
            }
        }
        groups.AddRange(choices.Select(c => new Group(c.Panels, c.Enter, c.Exit)));
        groups = groups.OrderBy(g => g.Mid).ThenBy(g => g.Enter).ThenBy(g => g.Panels[0]).ToList();
        var cuts = new double[Math.Max(0, groups.Count - 1)];
        for (int i = 0; i < cuts.Length; i++)
        {
            var a = groups[i]; var b = groups[i + 1];
            double lo = Math.Max(a.Enter, b.Enter), hi = Math.Min(a.Exit, b.Exit);
            if (hi > lo)
            {
                cuts[i] = Scalar.Clamp((a.Mid + b.Mid) / 2, lo, hi);
                diagnostics.Add("overlap-split: independent visits split at their centre boundary; candidate policy");
            }
            else cuts[i] = (a.Exit + b.Enter) / 2;
        }
        var steps = new List<JudgeStep>();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            double entry = Math.Max(g.Enter, i > 0 ? cuts[i - 1] : 0);
            double exit = Math.Min(g.Exit, i < cuts.Length ? cuts[i] : path.Length);
            if (exit - entry <= 1e-7)
            {
                diagnostics.Add("ambiguous-overlap: a visit lost all width; model needs review");
                continue;
            }
            int[] panels = corridorChoices ? CorridorChoices.Expand(path, g.Panels, entry, exit) : g.Panels;
            if (!panels.SequenceEqual(g.Panels)) choices.Add(new(panels, entry, exit, "nearby-sensor-allowance"));
            steps.Add(new(panels, entry, exit, 0));
        }
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            double release = (i + 1 < steps.Count ? steps[i + 1].Enter : path.Length) - s.Exit;
            if (release < -1e-7) throw new InvalidOperationException("Non-monotone interval partition.");
            steps[i] = s with { Release = Math.Max(0, release),
                RequireRearmMask = i > 0 ? s.Mask & steps[i - 1].Mask : 0 };
        }
        if (steps.Count is > 1 and <= 3)
            steps[steps.Count - 2] = steps[steps.Count - 2] with { RequireEntry = true, GateReason = "short-tail" };

        // Geometry policy experiment: an outer sharp turn protects the middle
        // visit of an adjoining three-visit straight leg. No v/V type/template
        // lookup. This can be compared with native NextCheck without replacing
        // the verified production NativeVSlide compatibility rule.
        foreach (var join in path.Joins.Where(j => j.StraightLegs && j.TurnDegrees >= 30 && j.Position.Magnitude > 360))
        {
            ProtectLeg(join.Before, join.Distance);
            ProtectLeg(join.Distance, join.After);
        }
        void ProtectLeg(double from, double to)
        {
            var indices = Enumerable.Range(0, steps.Count)
                .Where(i => steps[i].Enter < to - 1e-7 && steps[i].Exit > from + 1e-7).ToArray();
            if (indices.Length != 3) return;
            var first = steps[indices[0]]; var last = steps[indices[indices.Length - 1]];
            if (!first.Panels.Any(p => p < 8) || !last.Panels.Any(p => p < 8)) return;
            int middle = indices[1];
            steps[middle] = steps[middle] with { RequireEntry = true, GateReason = "outer-turn-short-leg" };
        }
        if (steps.Count == 0 || steps[0].Enter > 1e-7) diagnostics.Add("uncovered-start: native distance export blocked");
        if (steps.Count == 0 || path.Length - steps[steps.Count - 1].Exit > 1e-7) diagnostics.Add("uncovered-end: native tail wait undefined");
        if (steps.Any(s => s.RequireRearmMask != 0)) diagnostics.Add("repeat-rearm: adjacent visits sharing a sensor need release before reuse");
        if (path.ApproximateSegments > 0) diagnostics.Add("adaptive-geometry: numeric approximation; see convergence checks");
        return new(path, visits, steps, diagnostics.Distinct().ToArray(), choices);
    }

    public static IntervalTable Build(ParametricSlidePath path, SlideJudgeProfile profile,
        string model = RegionModels.Diameter, GeometryOptions? options = null) =>
        Build(new ArcLengthPath(path, options), RegionModels.Create(model, profile), model == RegionModels.Contours);
}
