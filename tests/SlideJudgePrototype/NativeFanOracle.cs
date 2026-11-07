namespace SlideJudgePrototype;

// Independent transcription of 1.70 SlideFan.NoteCheck / CheckSlideTouch.
// Source DLL SHA256 DC0E46A4133D4EE4781BA6DEB2FE30063DD3F82EE91C31A5A8A7E61E644D9446.
// Physical C1/C2 are normalized before constructing this reference. Only
// manual input/progress and the LateGood eligibility branch are represented.
sealed class NativeFanOracle(int[][][] areas)
{
    readonly int[] index = new int[3], subIndex = new int[3];
    readonly bool[] hitIn = new bool[3];
    public bool Ready { get; private set; }
    public (int Index, int Selected, bool Entered) State(int lane) => (index[lane], subIndex[lane], hitIn[lane]);
    public bool LateGood
    {
        get
        {
            for (int lane = 0; lane < 3 && index[lane] >= areas[lane].Length - 1; lane++)
                if (lane == 2) return true;
            return false;
        }
    }
    public void ResetLane(int lane, int position, bool inside, int sub = 0)
    { index[lane] = position; hitIn[lane] = inside; subIndex[lane] = sub; Ready = false; }
    public void Advance(ulong pressed)
    {
        int complete = 0;
        for (int lane = 0; lane < 3; lane++)
        {
            if (index[lane] < areas[lane].Length)
            {
                bool flag;
                do
                {
                    flag = Check(index[lane], lane, hitIn[lane], pressed);
                    if (!flag)
                    {
                        int next = index[lane] + 1;
                        if (next < areas[lane].Length) flag = Check(next, lane, false, pressed);
                    }
                    if (index[lane] >= areas[lane].Length) flag = false;
                } while (flag);
            }
            else complete++;
        }
        Ready = complete >= 3;
    }
    bool Check(int position, int lane, bool inside, ulong pressed)
    {
        bool result = false;
        bool Pressed(int panel) => (pressed & (1UL << panel)) != 0;
        if (!inside)
        {
            if (lane == 1 && position >= areas[lane].Length - 2)
            {
                if (Pressed(areas[lane][^1][0]) || Pressed(areas[lane][^2][0]))
                {
                    index[lane] = position; result = true; hitIn[lane] = true;
                    if (position == areas[lane].Length - 1) index[lane] = position + 1;
                }
            }
            else
                for (int sub = 0; sub < areas[lane][position].Length; sub++)
                    if (Pressed(areas[lane][position][sub]))
                    {
                        index[lane] = position; subIndex[lane] = sub; result = true; hitIn[lane] = true;
                        if (position == areas[lane].Length - 1) index[lane] = position + 1;
                    }
        }
        else if (!Pressed(areas[lane][position][subIndex[lane]]))
        {
            index[lane] = position + 1; subIndex[lane] = 0; result = true; hitIn[lane] = false;
        }
        return result;
    }
}
