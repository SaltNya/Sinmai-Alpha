using System;
using DB;
using Manager;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified;

// Keep the experimental polar-arc end classification out of the original
// ParametricSlidePath. The disabled path calls that original method unchanged.
public static class UnifiedGeometry
{
    public static SlideType GetEndType(ParametricSlidePath path, OptionMirrorID mirror)
    {
        var last = path.Segments[path.Segments.Length - 1];
        if (last is ParametricSlidePath.PolarArcSegment polar &&
            Math.Abs(polar.StartRadius - polar.EndRadius) < 1e-8 && Math.Abs(polar.DeltaAngle) > 1e-8)
        {
            bool flip = mirror == OptionMirrorID.LR || mirror == OptionMirrorID.UD;
            return (polar.DeltaAngle > 0) != flip ? SlideType.Slide_Circle_L : SlideType.Slide_Circle_R;
        }
        return path.GetEndType(mirror);
    }
}
