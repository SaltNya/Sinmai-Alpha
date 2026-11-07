namespace SlideJudgePrototype;

// Fan input policy compiled from the same JudgeStep geometry as ordinary
// slides. Kept offline: no score clock, autoplay, sound, or renderer.
public sealed class FanReplay
{
    sealed record Step(int[] ReleasePanels, ulong EntryMask, bool SharedMiddleTail);
    readonly Step[][] lanes;
    readonly int[] indices = new int[3], selected = new int[3];
    readonly bool[] entered = new bool[3];
    public bool ReadyToJudge { get; private set; }
    public bool AllLanesComplete => indices[0] >= lanes[0].Length && indices[1] >= lanes[1].Length && indices[2] >= lanes[2].Length;
    public bool EligibleForLateGood => indices[0] >= lanes[0].Length - 1 && indices[1] >= lanes[1].Length - 1 && indices[2] >= lanes[2].Length - 1;
    public (int Index, int Selected, bool Entered) State(int lane) => (indices[lane], selected[lane], entered[lane]);

    public FanReplay(IReadOnlyList<IReadOnlyList<JudgeStep>> tables)
    {
        if (tables.Count != 3 || tables.Any(t => t.Count < 2 || t.Any(s => s.Panels.Length == 0 || s.Panels.Any(p => p is < 0 or > 33 or 17))))
            throw new ArgumentException("Fan needs three lanes with at least two nonempty regions each.");
        lanes = new Step[3][];
        for (int lane = 0; lane < 3; lane++)
        {
            var areas = tables[lane];
            lanes[lane] = new Step[areas.Count];
            for (int index = 0; index < areas.Count; index++)
            {
                bool shared = lane == 1 && index >= areas.Count - 2;
                ulong mask = shared ? (1UL << areas[^1].Panels[0]) | (1UL << areas[^2].Panels[0]) : areas[index].Mask;
                lanes[lane][index] = new(areas[index].Panels.ToArray(), mask, shared);
            }
        }
    }
    public void Reset()
    {
        Array.Clear(indices); Array.Clear(selected); Array.Clear(entered); ReadyToJudge = false;
    }
    public void ResetLane(int lane, int index, bool inArea, int subIndex = 0)
    { indices[lane] = index; entered[lane] = inArea; selected[lane] = subIndex; ReadyToJudge = false; }

    public void Advance(ulong pressed)
    {
        int completeBeforeUpdate = 0;
        for (int lane = 0; lane < 3; lane++)
        {
            if (indices[lane] >= lanes[lane].Length) { completeBeforeUpdate++; continue; }
            bool changed;
            do
            {
                changed = Try(lane, indices[lane], entered[lane], pressed);
                if (!changed && indices[lane] + 1 < lanes[lane].Length) changed = Try(lane, indices[lane] + 1, false, pressed);
            } while (changed && indices[lane] < lanes[lane].Length);
        }
        // Native SlideFan counts lanes that were already done at frame entry.
        ReadyToJudge = completeBeforeUpdate == 3;
    }
    bool Try(int lane, int index, bool inside, ulong pressed)
    {
        var area = lanes[lane][index];
        if (!inside)
        {
            if ((pressed & area.EntryMask) == 0) return false;
            if (!area.SharedMiddleTail)
            {
                for (int sub = 0; sub < area.ReleasePanels.Length; sub++)
                    if ((pressed & (1UL << area.ReleasePanels[sub])) != 0) selected[lane] = sub;
            }
            else if (selected[lane] >= area.ReleasePanels.Length)
                selected[lane] = 0; // custom multi-choice predecessor cannot leak an invalid release index
            // Fan checks all candidates; the LAST pressed alternative wins.
            indices[lane] = index; entered[lane] = true;
            if (index == lanes[lane].Length - 1) indices[lane]++;
            return true;
        }
        if ((pressed & (1UL << area.ReleasePanels[selected[lane]])) != 0) return false;
        indices[lane] = index + 1; entered[lane] = false; selected[lane] = 0;
        return true;
    }
}
