using SinmaiAlpha.Notes.Libs;
using System;
using System.Collections.Generic;
using System.Numerics;
using DB;
using MAI2.Util;
using Manager;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified;

/// <summary>
/// A narrow compatibility rule for the two native straight-leg v/V families.
/// Recognition uses geometry, not a dialect or SC spelling. Other paths retain
/// the shared generator; touch/D/E opt-ins are never replaced by an ABC table.
/// </summary>
internal static class NativeVSlide
{
    private const double Tolerance = .0001;

    internal static bool TryBuild(ParametricSlidePath path, OptionMirrorID mirror,
        out SlideType type, out List<SlideDataBuilder.HitAreaData> hits)
    {
        type = SlideType.Slide_None;
        hits = null;
        var points = new List<Complex>();
        foreach (var segment in path.Segments)
        {
            if (segment is not ParametricSlidePath.LineSegment line) return false;
            if (points.Count == 0) points.Add(line.StartPoint);
            else if ((points[points.Count - 1] - line.StartPoint).Magnitude > Tolerance) return false;
            if ((line.EndPoint - line.StartPoint).Magnitude <= Tolerance) continue;
            // A redundant node on the same forward leg must not change the rule.
            if (points.Count >= 2)
            {
                var a = points[points.Count - 1] - points[points.Count - 2];
                var b = line.EndPoint - points[points.Count - 1];
                var cross = a.Real * b.Imaginary - a.Imaginary * b.Real;
                var dot = a.Real * b.Real + a.Imaginary * b.Imaginary;
                if (dot > 0 && Math.Abs(cross) <= Tolerance * a.Magnitude)
                    points.RemoveAt(points.Count - 1);
            }
            points.Add(line.EndPoint);
            if (points.Count > 3) return false;
        }
        if (points.Count != 3) return false;
        var start = KeyAt(points[0]);
        var end = KeyAt(points[2]);
        if (start < 0 || end < 0) return false;
        var span = (end - start + 8) & 7;
        if (points[1].Magnitude <= Tolerance)
        {
            if (span == 4) return false; // A diameter is a straight, not native v.
            type = SlideType.Slide_Corner;
        }
        else
        {
            var corner = KeyAt(points[1]);
            if (corner < 0) return false;
            var turn = (corner - start + 8) & 7;
            if (turn == 6 && span >= 1 && span <= 4) type = SlideType.Slide_Skip_L;
            else if (turn == 2 && span >= 4 && span <= 7) type = SlideType.Slide_Skip_R;
            else return false;
        }

        start = MaiGeometry.MirrorInfo[(int)mirror, start];
        end = MaiGeometry.MirrorInfo[(int)mirror, end];
        if (mirror is OptionMirrorID.LR or OptionMirrorID.UD)
            type = type == SlideType.Slide_Skip_L ? SlideType.Slide_Skip_R
                : type == SlideType.Slide_Skip_R ? SlideType.Slide_Skip_L : type;

        // Ask the running game, so a missing/restored Finale v or an installed
        // table correction is respected. Copy all fields; never mutate its table.
        var native = Singleton<SlideManager>.Instance.GetSlideHitArea(type, start, end, 0);
        if (native == null || native.Count == 0) return false;
        hits = new List<SlideDataBuilder.HitAreaData>(native.Count);
        foreach (var area in native)
        {
            var panels = new int[area.HitPoints.Count];
            for (var i = 0; i < panels.Length; i++) panels[i] = (int)area.HitPoints[i];
            hits.Add(new SlideDataBuilder.HitAreaData(area.PushDistance, area.ReleaseDistance, panels));
        }
        return true;
    }

    private static int KeyAt(Complex point)
    {
        for (var key = 0; key < 8; key++)
            if ((point - MaiGeometry.PointGroupA(key + 1)).Magnitude <= Tolerance) return key;
        return -1;
    }
}
