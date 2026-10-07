using System;
using System.Collections.Generic;

namespace SinmaiAlpha.Notes.Libs;

public sealed class MajdataRegularJudge
{
    private sealed class Area
    {
        public MajdataRegularPath.AreaSpec Spec;
        public bool On, Off, PartnerOn, PartnerOff;
        public bool Active => On || PartnerOn;
        public bool Finished => Spec.Last ? Active : (On && Off) || (PartnerOn && PartnerOff);
        public void Update(Func<int, bool> pressed)
        {
            if (pressed(Spec.Sensor)) On = true; else if (On) Off = true;
            if (Spec.Partner < 0) return;
            if (pressed(Spec.Partner)) PartnerOn = true; else if (PartnerOn) PartnerOff = true;
        }
    }
    private sealed class Group
    {
        public readonly List<Area> Areas = new List<Area>();
        public int Index;
        public int Remaining => Areas.Count - Index;
    }
    private readonly List<Group> groups = new List<Group>();
    public int HiddenBars { get; private set; }
    public int Remaining => groups[groups.Count - 1].Remaining;
    public bool Complete => Remaining == 0;
    public MajdataRegularJudge(MajdataRegularPath path)
    {
        foreach (var spec in path.Areas)
        {
            while (groups.Count <= spec.Group) groups.Add(new Group());
            groups[spec.Group].Areas.Add(new Area { Spec = spec });
        }
    }
    public void Update(Func<int, bool> pressed)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Remaining == 0 || (i > 0 && groups[i - 1].Remaining > 1)) continue;
            // Advancing the child's queue finishes its pending parent on the next check.
            if (i > 0 && group.Index > 0) groups[i - 1].Index = groups[i - 1].Areas.Count;
            var first = group.Areas[group.Index];
            first.Update(pressed);
            if (group.Remaining >= 2 && (first.Spec.CanSkip || first.Active))
            {
                var second = group.Areas[group.Index + 1];
                second.Update(pressed);
                if (second.Finished || second.Active)
                {
                    HiddenBars = Math.Max(HiddenBars, first.Spec.HideCount);
                    group.Index += second.Finished ? 2 : 1;
                    continue;
                }
            }
            if (first.Finished)
            {
                HiddenBars = Math.Max(HiddenBars, first.Spec.HideCount);
                group.Index++;
            }
        }
    }
}
