using System.Collections.Generic;

namespace SinmaiAlpha.Notes.Libs;

// TouchSlideDrop.OnSensorChanged and the head/auto transitions from Update.
public sealed class MajdataSlideJudge
{
    private readonly IList<int> route;
    private bool headTriggered;
    public int Index { get; private set; }
    public bool Complete => Index >= route.Count;

    public MajdataSlideJudge(IList<int> route) => this.route = route;

    public void SensorOn(int sensor, bool beforeLaunch)
    {
        if (route.Count == 0) return;
        if (beforeLaunch)
        {
            if (sensor == route[0]) headTriggered = true;
            return;
        }
        if (Index < route.Count && sensor == route[Index]) Index++;
    }

    public void Launch()
    {
        if (headTriggered && Index == 0 && route.Count > 0) Index = 1;
    }

    public void AutoAdvance(int index) => Index = System.Math.Max(Index, index);
}
