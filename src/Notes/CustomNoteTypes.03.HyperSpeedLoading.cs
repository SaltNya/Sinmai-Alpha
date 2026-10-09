using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SinmaiAlpha.Hosting;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes;

// 单音符 HS/流标识读取和音符分类。
public partial class CustomNoteTypes
{

    private static bool TryParseSpeedMultiplier(string text, out float speed)
    {
        speed = 1f;
        if (string.IsNullOrEmpty(text)) return false;

        text = text.Trim();
        if (text.StartsWith("x") || text.StartsWith("X"))
        {
            text = text.Substring(1);
        }

        return float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out speed);
    }

    /// <summary>解析行尾流类型键 s{N}（s1/s2/...，AquaMai ma2 扩展语法：可重叠流局部曲线）。
    /// 仅 s+纯数字 匹配；s 单独、slide 等不会误判。</summary>
    private static bool TryPeekStreamId(MA2Record rec, out string streamId)
    {
        streamId = null;
        if (rec._str.Count == 0) return false;
        var last = rec.getStr((uint)(rec._str.Count - 1))?.Trim();
        if (string.IsNullOrEmpty(last) || last.Length < 2 || last[0] != 's') return false;
        for (var i = 1; i < last.Length; i++)
        {
            if (last[i] < '0' || last[i] > '9') return false;
        }
        streamId = last;
        return true;
    }

    private static bool TryPeekSpeedValue(MA2Record rec, out float speed)
    {
        speed = 1f;
        if (rec._str.Count == 0) return false;
        var last = rec.getStr((uint)(rec._str.Count - 1))?.Trim();
        if (string.IsNullOrEmpty(last)) return false;
        if (!last.StartsWith("x") && !last.StartsWith("X") && !last.StartsWith("-") && !last.Contains(".")) return false;
        return TryParseSpeedMultiplier(last, out speed);
    }

    private static bool TryGetOptionalSpeed(MA2Record rec, out float speed)
    {
        speed = 1f;
        var typeName = rec.getType().getEnumName();

        if (typeName != null && KnownMa2FieldCounts.TryGetValue(typeName, out var expectedCount))
        {
            if (rec._str.Count == expectedCount + 1)
            {
                return TryParseSpeedMultiplier(rec.getStr((uint)(rec._str.Count - 1)), out speed);
            }
            // If the count doesn't match, fall through to the heuristic below.
            // This handles cases where the MA2 parser's field counting is different from our table.
        }

        // Conservative heuristic: if the last field looks like an explicit speed value
        // (x0.5, -1, 0.8, 1.2, ...), treat it as the optional speed field.
        // Normal MA2 note fields are integers, so this should not misread normal notes.
        return TryPeekSpeedValue(rec, out speed);
    }

    private static bool TrySetNoteSpeedField(NoteData note, float speed)
    {
        if (note == null) return false;

        var type = note.GetType();
        foreach (var name in new[] { "speed", "noteSpeed", "Speed", "NoteSpeed", "mSpeed" })
        {
            var field = AccessTools.Field(type, name);
            if (field != null && field.FieldType == typeof(float))
            {
                field.SetValue(note, speed);
                return true;
            }

            var property = AccessTools.Property(type, name);
            if (property != null && property.PropertyType == typeof(float) && property.CanWrite)
            {
                property.SetValue(note, speed);
                return true;
            }
        }

        return false;
    }

    private static void ApplyNoteSpeedMultiplier(ref float speed, Queue<float> pendingQueue)
    {
        if (_currentNoteData != null)
        {
            float multiplier;
            if (NoteSpeedMultipliers.TryGetValue(_currentNoteData, out multiplier) ||
                NoteSpeedByIndex.TryGetValue(_currentNoteData.indexNote, out multiplier))
            {
                multiplier = SanitizeSpeedMultiplier(multiplier);
                speed *= multiplier;
                // Keep the fallback queue in sync even when context works.
                if (pendingQueue.Count > 0) pendingQueue.Dequeue();
                return;
            }
        }

        // Fallback: if we can't correlate the current NoteData, assume GetNoteSpeed/GetTouchSpeed
        // is called once per note in the same order the notes were loaded.
        if (pendingQueue.Count > 0)
        {
            var queued = SanitizeSpeedMultiplier(pendingQueue.Dequeue());
            speed *= queued;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadNote")]
    public static void ApplyOptionalHyperSpeed(NotesReader __instance, NotesData ____note, MA2Record rec, bool __result, int ____playerID)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        // Put back the temporary removed speed field, if any.
        if (_optionalSpeedRemoved && _removedOptionalSpeedString != null)
        {
            rec._str.Add(_removedOptionalSpeedString);
            _optionalSpeedRemoved = false;
            _removedOptionalSpeedString = null;
        }

        RestoreDZoneMarker(rec);
        RestoreFireworkMarker(rec);
        RestoreStarHeadMarker(rec);
        RestoreTouchRadiusMarker(rec);
        RestoreBorrowedMarker(rec);
        RestoreNoteSkinMarker(rec);
        RestoreFakeMarker(rec);
        RestoreVisualMarker(rec);
        // Put back the temporary removed stream-id field, if any.
        if (_optionalStreamRemoved && _removedOptionalStreamString != null)
        {
            rec._str.Add(_removedOptionalStreamString);
            _optionalStreamRemoved = false;
            _removedOptionalStreamString = null;
        }

        if (!__result) return;

        var list = ____note?._noteData;
        if (list == null || list.Count <= _noteCountBeforeLoad) return;

        var note = list[list.Count - 1];
        RuntimeCharts.BindNote(note, RuntimeCharts.Current);
        ApplyDZonePosition(note, ____playerID);
        ApplyFakeMarker(note);
        ApplyBorrowedMarker(note, __instance, rec);
        ApplyTouchRadiusMarker(note);
        ApplyStarHeadMarker(note, __instance, rec);
        ApplyFireworkMarker(note, __instance, rec);
        ApplyNoteSkinMarker(note, __instance, rec);
        ApplyVisualMarker(note, _optionalStreamIdForCurrentLoad);
        _currentNoteData = note;

        // kind 完全由谱面原始类型标签决定（rec._str[0] 是行首类型，重定向只改 rec.type 不改 _str）。
        // 不再使用 findID 队列：findID 的调用次数与 loadNote 并不总是一一对应
        // （touch 链等会二次调用 findID 多入队），队列错位会把普通 touch 误标成地雷。
        var tag = rec._str.Count > 0 ? rec._str[0] : null;
        var kind = CustomNoteKind.None;
        if (tag != null && TryGetNoteKindByTag(tag, out var tagKind))
        {
            kind = tagKind;
        }

        if (kind != CustomNoteKind.None)
        {
            NoteKinds[note.indexNote] = kind;
        }

        // 流类型键（行尾 s{N} 字段）：该音符的曲线类型键 = s{N}（只吃本流曲线）。
        if (_optionalStreamIdForCurrentLoad != null)
        {
            StreamTypeByNoteIndex[note.indexNote] = _optionalStreamIdForCurrentLoad;
        }

        float speedToUse = 1f;
        if (_optionalSpeedForCurrentLoad.HasValue)
        {
            speedToUse = _optionalSpeedForCurrentLoad.Value;
            NoteSpeedMultipliers[note] = speedToUse;
            NoteSpeedByIndex[note.indexNote] = speedToUse;
            TrySetNoteSpeedField(note, speedToUse);
        }

        // Keep the fallback queues aligned with every loaded note.
        PendingNoteSpeedMultipliers.Enqueue(speedToUse);
        PendingTouchSpeedMultipliers.Enqueue(speedToUse);
        _optionalSpeedForCurrentLoad = null;
        _optionalStreamIdForCurrentLoad = null;
    }

    /// <summary>按谱面原始类型标签（rec._str[0]）判定 note 类别。
    /// 分支与 FindIDPrefix 一致：MineToBaseMap → Mine；BRTTP/BRTHO → TouchBreak；
    /// MBTTP/MBTHO → MineTouchBreak；NMSTP/BRSTP → TouchStar。</summary>
    public static bool TryGetNoteKindByTag(string tag, out CustomNoteKind kind)
    {
        kind = CustomNoteKind.None;
        if (string.IsNullOrEmpty(tag)) return false;
        if (MineToBaseMap.TryGetValue(tag, out _))
        {
            kind = CustomNoteKind.Mine;
            return true;
        }

        switch (tag)
        {
            case "BRTTP":
            case "BRTHO":
                kind = CustomNoteKind.TouchBreak;
                return true;
            case "MBTTP":
            case "MBTHO":
                kind = CustomNoteKind.MineTouchBreak;
                return true;
            case "NMSTP":
                kind = CustomNoteKind.TouchStar;
                return true;
            case "BRSTP":
                kind = CustomNoteKind.TouchBreakStar;
                return true;
            case "MNSTP":
                kind = CustomNoteKind.MineTouchStar;
                return true;
            default:
                return false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameManager), "GetNoteSpeed")]
    public static void GetNoteSpeedPostfix(ref float __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

        ApplyNoteSpeedMultiplier(ref __result, PendingNoteSpeedMultipliers);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameManager), "GetTouchSpeed")]
    public static void GetTouchSpeedPostfix(ref float __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

        ApplyNoteSpeedMultiplier(ref __result, PendingTouchSpeedMultipliers);
    }
}
