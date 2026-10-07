namespace SinmaiAlpha.Notes.Libs;

// SampleScene.unity / CanvasCombo, latest Alpha reference. Logical height 108
// corresponds to the camera's 10.8 units and Sinmai's 1080-unit note frame.
internal static class ComboDisplayLayout
{
    internal const float ReferenceHeight = 108, HeaderY = 5.9534183f;
    internal sealed class Entry
    {
        internal string Name, Label;
        internal int Group, Size;
        internal bool Header;
        internal float R, G, B;
        internal Entry(string name, string label, int group, bool header, float r, float g, float b)
        { Name = name; Label = label; Group = group; Header = header; Size = header ? 4 : 6; R = r; G = g; B = b; }
    }
    internal static readonly Entry[] Texts = {
        new("ComboText", "", 0, false, .7921569f, .24313724f, .48736075f),
        new("ComboTextHeader", "COMBO", 0, true, .7921569f, .24313724f, .48736075f),
        new("ScoreText", "", 1, false, .24705882f, .6901961f, .24705882f),
        new("ScoreTextHeader", "SCORE", 1, true, .24524584f, .6901961f, .24705882f),
        new("AchievementText", "", 2, false, .24705882f, .49803922f, .6901961f),
        new("AchievementTextHeader", "ACHIEVEMENT", 2, true, .2470588f, .49803922f, .6901961f),
        new("DXScoreText", "", 3, false, .24705882f, .6901961f, .24705882f),
        new("DXScoreTextHeader", "でらっくす SCORE", 3, true, .24524584f, .6901961f, .24705882f),
    };
    internal static int Group(int mode)
    {
        switch (mode)
        {
            case 0: return -1;
            case 2: case 101: case 102: return 1;
            case 3: case 4: case 11: case 12: return 2;
            case 13: return 3;
            default: return 0;
        }
    }
    private static readonly float[] Gold = { .78431374f, .6431373f, 0 }, Silver = { .46226418f, .46226418f, .46226418f },
        Bronze = { .67058825f, .21176471f, .11764706f }, Dud = { .12041651f, .45439216f, .6226415f };
    internal static float[] RateColor(decimal rate) => rate >= 100 ? Gold : rate >= 97 ? Silver : rate >= 80 ? Bronze : Dud;
}
