using System.Collections.Generic;

namespace SinmaiAlpha.Notes.Libs;

// Adapt polled cabinet input to the ordered On events used by TouchSlideDrop.
// A press remains pending only while held, so adjacent areas entering in the
// same polling interval do not lose their event before becoming the next area.
public sealed class MajdataSlideInput
{
    private ulong previousHeld, previousDown, pending;

    public void Update(MajdataSlideJudge judge, IList<int> route, ulong held, ulong down, bool beforeLaunch)
    {
        pending &= held;
        pending |= (held & ~previousHeld) | (down & ~previousDown);
        previousHeld = held;
        previousDown = down;
        if (beforeLaunch)
        {
            if (route.Count > 0 && (pending & (1UL << route[0])) != 0)
                judge.SensorOn(route[0], true);
            pending = 0;
            return;
        }
        judge.Launch();
        while (judge.Index < route.Count)
        {
            var sensor = route[judge.Index];
            var bit = 1UL << sensor;
            if ((pending & bit) == 0) break;
            pending &= ~bit;
            judge.SensorOn(sensor, false);
        }
    }
}
