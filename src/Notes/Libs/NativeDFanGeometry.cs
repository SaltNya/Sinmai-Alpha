using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using DB;
using Manager;
using Vector4 = UnityEngine.Vector4;
using Mathf = UnityEngine.Mathf;

namespace SinmaiAlpha.Notes.Libs;

/// <summary>Three physical paths and native hit-area queues for a numeric/D wifi.</summary>
public static class NativeDFanGeometry
{
    private static Complex Position(int key, bool dZone)
        => Complex.FromPolarCoordinates(MaiGeometry.MainRadius,
            Math.PI * (5.0 / 8.0 - (key - (dZone ? .5 : 0)) / 4.0));

    private static int MirrorKey(int key, bool dZone, OptionMirrorID mirror)
    {
        if (!dZone) return MaiGeometry.MirrorInfo[(int)mirror, key];
        switch (mirror)
        {
            case OptionMirrorID.LR: return (-key) & 7;
            case OptionMirrorID.UD: return (4 - key) & 7;
            case OptionMirrorID.UDLR: return (key + 4) & 7;
            default: return key;
        }
    }

    public static void Build(CustomSlideNoteData data, string expression, OptionMirrorID mirror)
    {
        var match = Regex.Match(expression, @"^(?<start>[1-8])(?<sd>d?)w(?<end>[1-8])(?<ed>d?)$");
        if (!match.Success) throw new FormatException("Invalid native D wifi: " + expression);
        var start = match.Groups["start"].Value[0] - '0';
        var end = match.Groups["end"].Value[0] - '0';
        var startD = match.Groups["sd"].Value.Length != 0;
        var endD = match.Groups["ed"].Value.Length != 0;
        if (((end - start) & 7) != 4) throw new FormatException("Wifi must end opposite its start.");
        var arrows = new List<SlideDataBuilder.ArrowData>[3];
        var hits = new List<SlideDataBuilder.HitAreaData>[3];
        for (var lane = 0; lane < 3; lane++)
        {
            var target = (end + lane + 6) % 8 + 1;
            var path = new ParametricSlidePath(new[] {
                new ParametricSlidePath.LineSegment(Position(start, startD), Position(target, endD)) });
            arrows[lane] = SlideDataBuilder.BuildArrowData(path);
            hits[lane] = SlideDataBuilder.BuildHitAreas(path, outerD: true);
            if (hits[lane].Count < 2) throw new FormatException("Wifi lane has insufficient native hit areas.");
            if (lane == 1) data.SlidePathLength = (float)path.GetPathLength();
        }
        // Native fan art is rooted at the source key. D starts offset it by
        // 22.5 degrees. Mirror reflections can reverse that offset.
        for (var star = 0; star < 8; star++)
        {
            var paths = new List<List<Vector4>>();
            var areas = new List<List<SlideManager.HitArea>>();
            float offset = 0;
            for (var lane = 0; lane < 3; lane++)
            {
                var native = SlideDataBuilder.ConvertAndRotateArrowData(arrows[lane], star, mirror);
                if (lane == 0)
                    offset = Mathf.DeltaAngle(67.5f, Mathf.Atan2(native[0].y, native[0].x) * Mathf.Rad2Deg);
                var rotor = Complex.FromPolarCoordinates(1, -offset * Math.PI / 180);
                for (var i = 0; i < native.Count; i++)
                {
                    var p = native[i];
                    var xy = new Complex(p.x, p.y) * rotor;
                    native[i] = new Vector4((float)xy.Real, (float)xy.Imaginary, p.z, p.w - offset);
                }
                paths.Add(native);
                areas.Add(SlideDataBuilder.ConvertAndRotateHitAreas(hits[lane], star, mirror, dRingReflection: true));
            }
            data.FanPathLists.Add(paths);
            data.FanHitAreaLists.Add(areas);
            data.FanVisualOffsets.Add(offset);
            data.SlidePathList.Add(paths[1]);
            data.SlideHitAreasList.Add(areas[1]);
        }
        data.slideData.type = SlideType.Slide_Fan;
        data.slideData.targetNote = MirrorKey(end - 1, endD, mirror);
    }
}
