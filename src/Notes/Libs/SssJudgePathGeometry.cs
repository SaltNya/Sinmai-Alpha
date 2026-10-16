using System;
using System.Numerics;

namespace SinmaiAlpha.Notes.Libs;

internal static class SssJudgePathGeometry
{
    // Same physical sensor centers as the noise-region display, in native pixels.
    // Native C1/C2 are separate inputs but share the same center.
    internal static Vector2 Center(int area)
    {
        if (area < 0 || area >= 34) throw new ArgumentOutOfRangeException(nameof(area));
        if (area == 16 || area == 17) return Vector2.Zero;
        var ring = area < 8 ? 0 : area < 16 ? 8 : area < 26 ? 18 : 26;
        var key = area - ring + 1;
        var radius = ring == 0 ? 400f : ring == 8 ? 220f : ring == 18 ? 410f : 310f;
        var angle = -key * Math.PI / 4 + Math.PI * (ring >= 18 ? 3d / 4 : 5d / 8);
        return new Vector2((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius);
    }

    internal static int PhysicalArea(int relativeArea, int button)
    {
        if (relativeArea < 0 || relativeArea >= 34) throw new ArgumentOutOfRangeException(nameof(relativeArea));
        if (relativeArea == 16 || relativeArea == 17) return relativeArea;
        var ring = relativeArea < 8 ? 0 : relativeArea < 16 ? 8 : relativeArea < 26 ? 18 : 26;
        return ring + ((relativeArea - ring + button) & 7);
    }
}
