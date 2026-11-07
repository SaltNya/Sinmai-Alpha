using System.Collections.Generic;
using System.Linq;
using Manager;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

internal sealed class CompiledJudgeFrame
{
    internal readonly List<SlideManager.HitArea> Areas;
    internal readonly JudgeStep[] Steps;
    internal readonly int[] Sensors;

    internal CompiledJudgeFrame(JudgeStep[] steps)
    {
        Steps = steps;
        Sensors = steps.SelectMany(step => step.Panels).Distinct().ToArray();
        Areas = steps.Select(step => new SlideManager.HitArea
        {
            PushDistance = step.Push,
            ReleaseDistance = step.Release,
            HitPoints = step.Panels.Select(panel => (InputManager.TouchPanelArea)panel).ToList()
        }).ToList();
    }
}

internal sealed class CompiledJudgeNote
{
    internal readonly CompiledJudgeFrame[] Frames;
    internal CompiledJudgeNote(CompiledJudgeFrame[] frames) { Frames = frames; }
}
