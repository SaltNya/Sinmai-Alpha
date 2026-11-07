using SinmaiAlpha.Notes.Libs;
using System;
using System.Collections.Generic;
using System.Numerics;
using DB;
using Manager;
using UnityEngine;
using Vector4 = UnityEngine.Vector4;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified;

/// <summary>
/// Per-note tables in native SlideManager format. Built at chart load, including
/// all star frames needed by connected slides. No parsing or sampling in play.
/// </summary>
public sealed class SlideTable
{
    public readonly struct Lane(List<Vector4> path, List<SlideManager.HitArea> hits)
    {
        public List<Vector4> Path { get; } = path;
        public List<SlideManager.HitArea> HitAreas { get; } = hits;
    }

    public readonly struct Frame(Lane[] lanes, float visualRotation)
    {
        public IReadOnlyList<Lane> Lanes { get; } = lanes;
        public float VisualRotation { get; } = visualRotation;
    }

    public bool IsFan => Frames[0].Lanes.Count == 3;
    public SlideJudgeProfile JudgeProfile { get; }
    public bool TouchAppearance { get; }
    public ParametricSlidePath TouchGeometry { get; }
    public OptionMirrorID Mirror { get; }
    public IReadOnlyList<Frame> Frames { get; }
    public float Length { get; }
    public SlideType EndType { get; }
    public int? TargetNote { get; }
    public bool UsesNativeVTable { get; }

    private SlideTable(SlideDefinition definition, OptionMirrorID mirror, Frame[] frames, SlideType? nativeVType)
    {
        JudgeProfile = definition.JudgeProfile;
        TouchAppearance = definition.TouchAppearance;
        Mirror = mirror;
        Frames = frames;
        var path = definition.Paths[definition.MainLane];
        // Only touch SV needs its original geometry during play. Ordinary/D/fan
        // samples can be collected after compilation, as before the refactor.
        TouchGeometry = definition.TouchSamples ? path : null;
        Length = (float)path.GetPathLength();
        UsesNativeVTable = nativeVType.HasValue;
        EndType = nativeVType ?? (definition.IsFan ? SlideType.Slide_Fan : UnifiedGeometry.GetEndType(path, mirror));
        if (definition.IsFan)
        {
            var ring = definition.FanEndD ? 18 : 0;
            TargetNote = SlideDataBuilder.MirrorPanel(ring + definition.FanEndKey, mirror) - ring;
        }
    }

    public static SlideTable Build(SlideDefinition definition, OptionMirrorID mirror)
    {
        var laneCount = definition.Paths.Count;
        var arrows = new List<SlideDataBuilder.ArrowData>[laneCount];
        var hits = new List<SlideDataBuilder.HitAreaData>[laneCount];
        SlideType? nativeVType = null;
        for (var lane = 0; lane < laneCount; lane++)
        {
            var path = definition.Paths[lane];
            arrows[lane] = SlideDataBuilder.BuildArrowData(path, definition.TouchSamples);
            if (!definition.IsFan && definition.JudgeProfile == SlideJudgeProfile.StandardAbc &&
                NativeVSlide.TryBuild(path, mirror, out var vType, out var vHits))
            {
                hits[lane] = vHits;
                nativeVType = vType;
            }
            else hits[lane] = SlideDataBuilder.BuildHitAreas(path, definition.JudgeProfile);
            if (definition.IsFan && hits[lane].Count < 2)
                throw new FormatException("Wifi lane has insufficient hit areas.");
        }

        var frames = new Frame[8];
        for (var star = 0; star < frames.Length; star++)
        {
            var lanes = new Lane[laneCount];
            float offset = 0;
            for (var lane = 0; lane < laneCount; lane++)
            {
                var path = SlideDataBuilder.ConvertAndRotateArrowData(arrows[lane], star, mirror);
                if (definition.TouchSamples)
                    for (var i = 0; i < path.Count; i++)
                    {
                        var arrow = path[i]; arrow.w += MajdataSlidePath.ReferenceRotation - 180; path[i] = arrow;
                    }
                if (definition.IsFan)
                {
                    // Native fan art has its own source-key frame. This affects
                    // rendering only; sensors always remain in the star frame.
                    if (lane == 0)
                        offset = Mathf.DeltaAngle(67.5f, Mathf.Atan2(path[0].y, path[0].x) * Mathf.Rad2Deg);
                    var rotor = Complex.FromPolarCoordinates(1, -offset * Math.PI / 180);
                    for (var i = 0; i < path.Count; i++)
                    {
                        var p = path[i]; var xy = new Complex(p.x, p.y) * rotor;
                        path[i] = new Vector4((float)xy.Real, (float)xy.Imaginary, p.z, p.w - offset);
                    }
                }
                // Native V areas already use the mirrored type/start/end. Only
                // rotate into the star frame here, preserving C1/C2 exactly.
                lanes[lane] = new Lane(path, SlideDataBuilder.ConvertAndRotateHitAreas(hits[lane], star,
                    nativeVType.HasValue ? OptionMirrorID.Normal : mirror));
            }
            frames[star] = new Frame(lanes, offset);
        }
        return new SlideTable(definition, mirror, frames, nativeVType);
    }
}
