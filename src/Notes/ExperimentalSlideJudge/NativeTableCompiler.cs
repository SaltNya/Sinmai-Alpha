using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
using SlideDefinition = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDefinition;
using System;
using System.Collections.Generic;
using System.Linq;
using DB;
using Manager;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

internal static class NativeTableCompiler
{
    private static readonly GeometryOptions Budget = new GeometryOptions(
        MaxPieces: 16384, MaxEvaluations: 131072, MaxDepth: 20);
    private static readonly IReadOnlyList<SensorRegion> Abc = ContourRegions.Create(SlideJudgeProfile.StandardAbc);
    private static readonly IReadOnlyList<SensorRegion> Extended = ContourRegions.Create(SlideJudgeProfile.ExtendedTouch);

    internal static CompiledJudgeNote Compile(string code, OptionMirrorID mirror)
    {
        var definition = SlideDefinition.Parse(code);
        if (definition.IsFan) throw new ArgumentException("Wifi geometry is not enabled for this experiment.");
        var measured = new ArcLengthPath(definition.Paths[0], Budget);
        var table = IntervalJudge.Build(measured,
            definition.JudgeProfile == SlideJudgeProfile.ExtendedTouch ? Extended : Abc, corridorChoices: true);
        if (!table.CanExportDistances)
            throw new ArgumentException("Zero-length path or uncovered endpoint; retaining existing judgment.");
        if (table.Steps.Count > 2048)
            throw new ArgumentException("Judge step budget exceeded; retaining existing judgment.");
        foreach (var step in table.Steps)
            if (!Scalar.Finite(step.Push + step.Release) || step.Push <= 0 || step.Release < 0)
                throw new ArgumentException("Invalid judgment distances.");

        var frames = new CompiledJudgeFrame[8];
        for (int star = 0; star < frames.Length; star++)
        {
            var steps = new JudgeStep[table.Steps.Count];
            for (int i = 0; i < steps.Length; i++)
            {
                var step = table.Steps[i];
                var panels = step.Panels.Select(panel => TransformPanel(panel, mirror, star)).ToArray();
                ulong rearm = 0;
                for (int p = 0; p < step.Panels.Length; p++)
                    if ((step.RequireRearmMask & (1UL << step.Panels[p])) != 0) rearm |= 1UL << panels[p];
                steps[i] = step with { Panels = panels, RequireRearmMask = rearm };
            }
            frames[star] = new CompiledJudgeFrame(steps);
        }
        // Retain only compact input/distance tables. Measured curves, raw
        // crossings, diagnostics and region polygons are not held by notes.
        return new CompiledJudgeNote(frames);
    }

    internal static int TransformPanel(int panel, OptionMirrorID mirror, int star)
    {
        // Mirror before rotating into the native star frame. D/E axes are
        // between keys; C1/C2 refer to the same physical center input.
        if (panel == 16 || panel == 17) return 16;
        int ring = panel < 8 ? 0 : panel < 16 ? 8 : panel < 26 ? 18 : 26;
        int key = panel - ring;
        if (ring < 18)
            key = mirror == OptionMirrorID.LR ? 7 - key : mirror == OptionMirrorID.UD ? (3 - key) & 7
                : mirror == OptionMirrorID.UDLR ? (key + 4) & 7 : key;
        else
            key = mirror == OptionMirrorID.LR ? (-key) & 7 : mirror == OptionMirrorID.UD ? (4 - key) & 7
                : mirror == OptionMirrorID.UDLR ? (key + 4) & 7 : key;
        return ring + ((key - star) & 7);
    }
}
