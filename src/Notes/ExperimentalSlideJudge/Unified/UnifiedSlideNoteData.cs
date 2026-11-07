using System;
using System.Collections.Generic;
using System.Linq;
using DB;
using Manager;
using UnityEngine;
using LegacyNote = SinmaiAlpha.Notes.Libs.CustomSlideNoteData;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified;

// The game still creates its ORIGINAL CustomSlideNoteData. This adapter builds
// the optional table and publishes into that class's existing fields, so no
// registration, visual, fan or NoteData source file needs changing.
// The derived facade is used by offline tests; runtime calls TryParseInto.
public sealed class UnifiedSlideNoteData : LegacyNote
{
    public SlideTable Table { get; private set; }
    public bool TouchAppearance => Table?.TouchAppearance == true;
    public SlideJudgeProfile JudgeProfile => Table?.JudgeProfile ?? SlideJudgeProfile.StandardAbc;

    private int LaneFor(int end) => !IsFan ? 0 : FanLane(end);
    public List<Vector4> GetSlidePath(int star, int end) => Table.Frames[star].Lanes[LaneFor(end)].Path;
    public List<SlideManager.HitArea> GetSlideHitAreas(int star, int end) => Table.Frames[star].Lanes[LaneFor(end)].HitAreas;
    public List<Vector4> GetSlidePath(int star) => GetSlidePath(star, slideData.targetNote);
    public List<SlideManager.HitArea> GetSlideHitAreas(int star) => GetSlideHitAreas(star, slideData.targetNote);

    public new bool ParseSlideCode(string code, OptionMirrorID mirror)
    {
        Table = null;
        bool success = TryParseInto(this, code, mirror, out var table);
        Table = table;
        return success;
    }

    public static bool TryParseInto(LegacyNote note, string code, OptionMirrorID mirror, out SlideTable table)
    {
        table = null;
        note.SlideCode = code;
        note.SlidePathList.Clear(); note.SlideHitAreasList.Clear();
        note.FanPathLists.Clear(); note.FanHitAreaLists.Clear(); note.FanVisualOffsets.Clear();
        note.SlidePathLength = 0; note.TouchGeometry = null; note.TouchMirror = mirror;
        if (string.IsNullOrEmpty(code)) return false;
        try
        {
            // Build atomically before exposing any table to native consumers.
            table = SlideTable.Build(SlideDefinition.Parse(code), mirror);
            int main = table.IsFan ? 1 : 0;
            foreach (var frame in table.Frames)
            {
                note.SlidePathList.Add(frame.Lanes[main].Path);
                note.SlideHitAreasList.Add(frame.Lanes[main].HitAreas);
                if (!table.IsFan) continue;
                note.FanPathLists.Add(frame.Lanes.Select(lane => lane.Path).ToList());
                note.FanHitAreaLists.Add(frame.Lanes.Select(lane => lane.HitAreas).ToList());
                note.FanVisualOffsets.Add(frame.VisualRotation);
            }
            note.SlidePathLength = table.Length;
            note.TouchGeometry = table.TouchGeometry;
            note.slideData.type = table.EndType;
            if (table.TargetNote.HasValue) note.slideData.targetNote = table.TargetNote.Value;
            return true;
        }
        catch (Exception e) when (e is ArgumentException || e is FormatException)
        {
            table = null;
            MelonLoader.MelonLogger.Error("[Experimental Slide Judge] Invalid custom slide geometry " + code + ": " + e.Message);
            return false;
        }
    }
}
