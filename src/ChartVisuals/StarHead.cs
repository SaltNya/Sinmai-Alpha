namespace SinmaiAlpha.ChartVisuals;

// Presentation metadata only. It never makes a note Fake or changes its grade.
public static class StarHead
{
    public const float DegreesPerSecond = 400f;
    public static string Encode(bool rotate) => rotate ? "SH1|1" : "SH1|0";
    public static bool Decode(string marker, out bool rotate)
    {
        rotate = marker == "SH1|1";
        return rotate || marker == "SH1|0";
    }
}
