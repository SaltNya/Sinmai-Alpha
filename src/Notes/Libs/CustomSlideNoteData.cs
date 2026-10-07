using System.Collections.Generic;
using DB;
using Manager;
using UnityEngine;

namespace SinmaiAlpha.Notes.Libs;

public class CustomSlideNoteData: NoteData
{
    public string SlideCode;
    public List<List<Vector4>> SlidePathList = new List<List<Vector4>>();
    public List<List<SlideManager.HitArea>> SlideHitAreasList = new List<List<SlideManager.HitArea>>();
    public float SlidePathLength;
    public ParametricSlidePath TouchGeometry;
    public OptionMirrorID TouchMirror;
    public readonly List<List<List<Vector4>>> FanPathLists = new List<List<List<Vector4>>>();
    public readonly List<List<List<SlideManager.HitArea>>> FanHitAreaLists = new List<List<List<SlideManager.HitArea>>>();
    public readonly List<float> FanVisualOffsets = new List<float>();
    public bool IsFan => FanPathLists.Count == 8;

    public int FanLane(int end) => ((end - slideData.targetNote + 8) & 7) == 7 ? 0
        : ((end - slideData.targetNote + 8) & 7) == 1 ? 2 : 1;

    public bool ParseSlideCode(string slideCode, OptionMirrorID mirrorMode)
    {
        if (string.IsNullOrEmpty(slideCode))
        {
            return false;
        }
        
        SlidePathList.Clear();
        SlideHitAreasList.Clear();
        FanPathLists.Clear();
        FanHitAreaLists.Clear();
        FanVisualOffsets.Clear();
        TouchGeometry = null;
        
        this.SlideCode = slideCode;
        if (slideCode.StartsWith("MV1:") || slideCode.StartsWith("MV2:") ||
            slideCode.StartsWith("MD1:") || slideCode.StartsWith("MD2:"))
        {
            MelonLoader.MelonLogger.Error("[CustomNoteType] This chart uses the retired preview-slide format. Reconvert it with the original SC converter.");
            return false;
        }
        var nativeD = slideCode.StartsWith("DG1:") || slideCode.StartsWith("DG2:");
        var nativeTouch = slideCode.StartsWith("TG1:");
        ParametricSlidePath path;
        try
        {
            if (slideCode.StartsWith("DF1:"))
            {
                NativeDFanGeometry.Build(this, slideCode.Substring(4), mirrorMode);
                return true;
            }
            path = nativeTouch ? NativeTouchSlideGeometry.Build(slideCode.Substring(4))
                : slideCode.StartsWith("DG1:") ? NativeDSlideGeometry.Build(slideCode.Substring(4))
                : SlideCodeParser.Parse(slideCode.StartsWith("DG2:") ? slideCode.Substring(4) : slideCode);
        }
        catch (System.ArgumentException ex)
        {
            MelonLoader.MelonLogger.Error("[CustomNoteType] Invalid custom slide geometry " + slideCode + ": " + ex.Message);
            return false;
        }
        catch (System.FormatException ex)
        {
            MelonLoader.MelonLogger.Error("[CustomNoteType] Invalid custom slide geometry " + slideCode + ": " + ex.Message);
            return false;
        }
        if (path == null)
        {
            return false;
        }

        if (nativeTouch) { TouchGeometry = path; TouchMirror = mirrorMode; }
        var arrowData = nativeTouch ? NativeTouchSlideGeometry.BuildArrows(path) : SlideDataBuilder.BuildArrowData(path);
        SlidePathLength = (float)path.GetPathLength();
        var hitAreaData = SlideDataBuilder.BuildHitAreas(path, outerD: nativeD || nativeTouch);
        for (var i = 0; i < 8; i++)
        {
            var arrows = SlideDataBuilder.ConvertAndRotateArrowData(arrowData, i, mirrorMode);
            if (nativeTouch)
                for (var j = 0; j < arrows.Count; j++)
                { var arrow = arrows[j]; arrow.w += MajdataSlidePath.ReferenceRotation - 180; arrows[j] = arrow; }
            SlidePathList.Add(arrows);
            SlideHitAreasList.Add(SlideDataBuilder.ConvertAndRotateHitAreas(hitAreaData, i, mirrorMode, dRingReflection: nativeD || nativeTouch));
        }

        this.slideData.type = path.GetEndType(mirrorMode);

        return true;
    }
}
