namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

// The game still owns timing, grade, sound, arrows, autoplay and mine inversion.
// This object only supplies manual sensor progression and the geometry's gates.
internal sealed class RuntimeInputState
{
    internal readonly CompiledJudgeFrame Frame;
    internal ulong Pressed;
    private ulong armed = ulong.MaxValue;

    internal RuntimeInputState(CompiledJudgeFrame frame) { Frame = frame; }

    internal void BeginFrame(ulong pressed) { Pressed = pressed; armed |= ~pressed; }

    internal bool NextCheck(int index, bool entered) => index >= 0 && index < Frame.Steps.Length &&
        (!Frame.Steps[index].RequireEntry || entered);

    internal bool CheckTouch(int target, bool inArea, ref int index, ref int subIndex, ref bool entered)
    {
        if (target < 0 || target >= Frame.Steps.Length) return false;
        var step = Frame.Steps[target];
        if (!inArea)
        {
            for (int i = 0; i < step.Panels.Length; i++)
            {
                ulong bit = 1UL << step.Panels[i];
                if ((Pressed & bit) == 0 || (step.RequireRearmMask & bit) != 0 && (armed & bit) == 0) continue;
                index = target == Frame.Steps.Length - 1 ? target + 1 : target;
                subIndex = i; entered = true; armed &= ~bit;
                return true;
            }
        }
        else if (subIndex < 0 || subIndex >= step.Panels.Length ||
            (Pressed & (1UL << step.Panels[subIndex])) == 0)
        {
            index = target + 1; subIndex = 0; entered = false;
            return true;
        }
        return false;
    }
}
