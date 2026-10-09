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

// 扩展音符识别、记录重定向、谱面读取及读取上下文。
public partial class CustomNoteTypes
{

    private static bool TryRedirectMineRecord(MA2Record rec)
    {
        var name = rec.getType().getEnumName();
        if (!MineToBaseMap.TryGetValue(name, out var baseName)) return false;

        var baseId = Ma2fileRecordID.findID(baseName);
        if (baseId == Ma2fileRecordID.Def.Invalid) return false;

        var traverse = Traverse.Create(rec);
        foreach (var fieldName in new[] { "type", "_type", "m_type", "Type" })
        {
            var field = traverse.Field(fieldName);
            if (field.FieldExists())
            {
                field.SetValue(baseId);
                return true;
            }
        }

        return false;
    }

    private static bool TryRedirectSpecialRecord(MA2Record rec)
    {
        var name = rec.getType().getEnumName();
        string baseName = null;
        var isMine = false;
        var isTouchBreak = false;
        var isTouchStar = false;

        if (MineToBaseMap.TryGetValue(name, out baseName))
        {
            isMine = true;
        }
        else if (name == "BRTTP")
        {
            // 绝赞 touch：判定同普通 touch，但命中强制 Critical Perfect（非地雷）。
            baseName = "NMTTP";
            isTouchBreak = true;
        }
        else if (name == "BRTHO")
        {
            // 绝赞 touchhold（非地雷）：命中强制 Critical Perfect。
            baseName = "NMTHO";
            isTouchBreak = true;
        }
        else if (name == "MBTTP")
        {
            // 地雷绝赞 touch。
            baseName = "NMTTP";
            isMine = true;
            isTouchBreak = true;
        }
        else if (name == "MBTHO")
        {
            // 地雷绝赞 touchhold（素材 touchhold_break_0..3 + touchhold_break）。
            baseName = "NMTHO";
            isMine = true;
            isTouchBreak = true;
        }
        else if (name == "NMSTP")
        {
            // TouchStar：逻辑同普通 touch，贴图五瓣星（touch_star / touch_hit_star）。
            baseName = "NMTTP";
            isTouchStar = true;
        }
        else if (name == "BRSTP")
        {
            // Break TouchStar：逻辑同绝赞 touch（命中强制 CP），贴图 touch_star_break。
            baseName = "NMTTP";
            isTouchBreak = true;
            isTouchStar = true;
        }
        else if (name == "MNSTP")
        {
            // 地雷 TouchStar：逻辑同地雷 touch（命中反转成 Miss），贴图 touch_star_mine 五瓣星。
            baseName = "NMTTP";
            isMine = true;
            isTouchStar = true;
        }
        else
        {
            return false;
        }

        var baseId = Ma2fileRecordID.findID(baseName);
        if (baseId == Ma2fileRecordID.Def.Invalid)
        {
            MelonLogger.Error($"[CustomNoteType] Redirect failed: cannot find base type {baseName} for {name}");
            return false;
        }

        var traverse = Traverse.Create(rec);
        foreach (var fieldName in new[] { "type", "_type", "m_type", "Type" })
        {
            var field = traverse.Field(fieldName);
            if (field.FieldExists())
            {
                field.SetValue(baseId);
                _pendingMineForCurrentLoad = isMine;
                _pendingTouchBreakForCurrentLoad = isTouchBreak;
                _pendingTouchStarForCurrentLoad = isTouchStar;
                return true;
            }
        }

        MelonLogger.Error($"[CustomNoteType] Redirect failed: cannot find type field on MA2Record for {name}");
        return false;
    }



    [HarmonyPrefix]
    [HarmonyPatch(typeof(Ma2fileRecordID), "findID")]
    public static bool FindIDPrefix(string enumName, ref Ma2fileRecordID.Def __result)
    {
        if (!FeaturesEnabled()) { __result = ChartFeatureGate.FindNativeRecord(enumName); return false; }

        // NOTE: Do NOT reset _pendingMineForCurrentLoad / _pendingTouchBreakForCurrentLoad here.
        // findID can be called multiple times before loadNote; resetting here would lose the flag.

        string baseName = null;
        var isMine = false;
        var isTouchBreak = false;
        var isTouchStar = false;

        if (MineToBaseMap.TryGetValue(enumName, out baseName))
        {
            isMine = true;
        }
        else if (enumName == "BRTTP")
        {
            // 绝赞 touch（非地雷，命中强制 CP）。
            baseName = "NMTTP";
            isTouchBreak = true;
        }
        else if (enumName == "BRTHO")
        {
            // 绝赞 touchhold（非地雷，命中强制 CP）。
            baseName = "NMTHO";
            isTouchBreak = true;
        }
        else if (enumName == "MBTTP")
        {
            // 地雷绝赞 touch。
            baseName = "NMTTP";
            isMine = true;
            isTouchBreak = true;
        }
        else if (enumName == "MBTHO")
        {
            // 地雷绝赞 touchhold。
            baseName = "NMTHO";
            isMine = true;
            isTouchBreak = true;
        }
        else if (enumName == "NMSTP")
        {
            // TouchStar：逻辑同普通 touch，贴图五瓣星（touch_star / touch_hit_star）。
            baseName = "NMTTP";
            isTouchStar = true;
        }
        else if (enumName == "BRSTP")
        {
            // Break TouchStar：逻辑同绝赞 touch（命中强制 CP），贴图 touch_star_break。
            baseName = "NMTTP";
            isTouchBreak = true;
            isTouchStar = true;
        }
        else if (enumName == "MNSTP")
        {
            // 地雷 TouchStar：逻辑同地雷 touch（命中反转成 Miss），贴图 touch_star_mine 五瓣星。
            baseName = "NMTTP";
            isMine = true;
            isTouchStar = true;
        }

        if (baseName != null)
        {
            for (var i = 0; i < TotalMa2RecordCount; i++)
            {
                var item = Ma2FileRecordData.GetValue(i);
                if (Traverse.Create(item).Field<string>("enumName").Value == baseName)
                {
                    __result = (Ma2fileRecordID.Def)i;
                    if (AllNoteTypeNames.Contains(enumName))
                    {
                        PendingMineFlags.Enqueue(isMine);
                        PendingTouchBreakFlags.Enqueue(isTouchBreak);
                        PendingTouchStarFlags.Enqueue(isTouchStar);
                    }
                    return false;
                }
            }

            MelonLogger.Error($"[CustomNoteType] findID cannot find base type {baseName} for {enumName}");
            return false;
        }

        // Normal lookup (including custom slide types like NMSSS).
        __result = Ma2fileRecordID.Def.Invalid;
        for (var i = 0; i < TotalMa2RecordCount; i++)
        {
            var item = Ma2FileRecordData.GetValue(i);
            if (Traverse.Create(item).Field<string>("enumName").Value == enumName)
            {
                __result = (Ma2fileRecordID.Def)i;
            }
        }

        if (AllNoteTypeNames.Contains(enumName))
        {
            PendingMineFlags.Enqueue(false);
            PendingTouchBreakFlags.Enqueue(false);
            PendingTouchStarFlags.Enqueue(false);
        }

        return false;
    }

    [HarmonyPatch]
    public static class Ma2RecordValidation
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                // AccessTools.Method(typeof(Ma2fileRecordID), "findID"),
                AccessTools.Method(typeof(Ma2fileRecordID), "clamp"),
                AccessTools.Method(typeof(Ma2fileRecordID), "getClampValue"),
                AccessTools.Method(typeof(Ma2fileRecordID), "isValid"),
                AccessTools.Method(typeof(Ma2fileRecordID_Extension), "isValid"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // Registration is complete before these patches are installed. Keep
            // validation a leaf operation on old cabinet Mono; a cross-assembly
            // static field load here can invalidate optimized native getter calls.
            // Mutate instructions so branch labels and exception blocks survive.
            var nativeCount = (int)Ma2fileRecordID.Def.End;
            foreach (var inst in instructions)
            {
                if (inst.LoadsConstant(nativeCount))
                { inst.opcode = OpCodes.Ldc_I4; inst.operand = TotalMa2RecordCount; }
                else if (inst.LoadsConstant(nativeCount - 1))
                { inst.opcode = OpCodes.Ldc_I4; inst.operand = LastMa2RecordID; }
                yield return inst;
            }
        }
    }

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * 以下内容是给新的 MA2 语法写解析器
     */

    /*
     * 给新建的 noteData 初始化应有的数据, 仅仅是照搬了 NotesReader.loadNote
     */
    public static void PrepareBasicNoteData(NoteData noteData, NotesReader reader,
        MA2Record record, int index, ref int noteIndex, OptionMirrorID mirrorMode)
    {
        noteData.type = new NotesTypeID(record.getType().getNotesTypeId());
        noteData.time.init(record.getBar(), record.getGrid(), reader);
        noteData.end = noteData.time;
        // MirrorInfo[mode, pos] 索引守卫：手写/第三方谱面可能写出 -1 或 ≥17 的 start（如
        // C 区起点 NMSSS 的 start=-1，旧版转换器会产出），越界会 IndexOutOfRange 崩溃（2026-08-25）。
        // 边界用 GetLength(1) 动态读取，镜像表尺寸变化时守卫自动跟随。
        var mirror = (int)mirrorMode is >= 0 and <= 3 ? (int)mirrorMode : 0;
        var pos = record.getPos();
        if (pos < 0 || pos >= MaiGeometry.MirrorInfo.GetLength(1))
        {
            MelonLogger.Error($"[CustomNoteType] MirrorInfo start OOB: pos={pos} mode={mirrorMode} tag={record.getType().getEnumName()} bar={record.getBar()} grid={record.getGrid()} (clamped to 0)");
            pos = 0;
        }
        noteData.startButtonPos = MaiGeometry.MirrorInfo[mirror, pos];
        noteData.index = index;
        var num = record.getGrid() % 96;
        if (num == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType04;
        }
        else if (num % 48 == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType08;
        }
        else if (num % 24 == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType16;
        }
        else if (num % 16 == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType24;
        }
        else
        {
            noteData.beatType = NoteData.BeatType.BeatTypeOther;
        }
        noteData.indexNote = noteIndex;
        ++noteIndex;
    }

    /*
     * 给新建的 noteData 填入基本的 slide 相关数据, 仅仅是照搬了 NotesReader.loadNote
     */
    public static void PrepareBasicSlideData(NoteData noteData, NotesReader reader, MA2Record record, int noteIndex,
        ref int slideIndex, OptionMirrorID mirrorMode)
    {
        noteData.indexSlide = slideIndex++;
        var slideData = noteData.slideData;
        var slideWaitLen = record.getSlideWaitLen();
        var slideShootLen = record.getSlideShootLen();
        // MirrorInfo[mode, endPos] 索引守卫，同上（NMSSS end 字段越界防崩）。
        var mirror = (int)mirrorMode is >= 0 and <= 3 ? (int)mirrorMode : 0;
        var endPos = record.getSlideEndPos();
        if (endPos < 0 || endPos >= MaiGeometry.MirrorInfo.GetLength(1))
        {
            MelonLogger.Error($"[CustomNoteType] MirrorInfo end OOB: endPos={endPos} mode={mirrorMode} tag={record.getType().getEnumName()} bar={record.getBar()} grid={record.getGrid()} (clamped to 0)");
            endPos = 0;
        }
        slideData.targetNote = MaiGeometry.MirrorInfo[mirror, endPos];
        slideData.shoot.time.init(record.getBar(), record.getGrid() + slideWaitLen, reader);
        slideData.shoot.index = noteIndex;
        slideData.arrive.time.init(record.getBar(), record.getGrid() + slideWaitLen + slideShootLen, reader);
        slideData.arrive.index = noteIndex;
        noteData.end = slideData.arrive.time;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesReader), "loadNote")]
    public static bool LoadCustomNote(NotesReader __instance, ref bool __result, NotesData ____note, int ____playerID,
        MA2Record rec, int index, ref int noteIndex, ref int slideIndex)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        _noteCountBeforeLoad = ____note?._noteData?.Count ?? 0;
        _optionalSpeedForCurrentLoad = null;
        _removedOptionalSpeedString = null;
        _optionalSpeedRemoved = false;
        _optionalStreamIdForCurrentLoad = null;
        _removedOptionalStreamString = null;
        _optionalStreamRemoved = false;

        // Note: mine/critical flags are set in FindIDPrefix before loadNote is called.
        // Do NOT reset them here, otherwise the mine marking would be lost.

        // 流类型键 s{N}（行尾字段，AquaMai ma2 扩展语法）：先于速度字段解析。
        // 命中时对内置记录临时移除该字段，避免原生 MA2 解析器多字段错位。
        if (TryPeekStreamId(rec, out var streamId))
        {
            _optionalStreamIdForCurrentLoad = streamId;
            // Custom SSS/STP records also need the trailer removed, otherwise
            // it masks FK/DZ/VS metadata. Restore it after all native/custom reads.
            _removedOptionalStreamString = rec.getStr((uint)(rec._str.Count - 1));
            rec._str.RemoveAt(rec._str.Count - 1);
            _optionalStreamRemoved = true;
        }

        ReadVisualMarker(rec);
        ReadFakeMarker(rec);
        ReadNoteSkinMarker(rec);
        ReadBorrowedMarker(rec);
        ReadTouchRadiusMarker(rec);
        ReadStarHeadMarker(rec);
        ReadFireworkMarker(rec);
        ReadDZoneMarker(rec);
        if (TryPeekSpeedValue(rec, out var optionalSpeed) || TryGetOptionalSpeed(rec, out optionalSpeed))
        {
            _optionalSpeedForCurrentLoad = optionalSpeed;

            // For built-in records, temporarily remove the extra speed field so the original
            // MA2 parser still sees the normal number of fields.
            if (rec.getType() < Ma2fileRecordID.Def.End)
            {
                _removedOptionalSpeedString = rec.getStr((uint)(rec._str.Count - 1));
                rec._str.RemoveAt(rec._str.Count - 1);
                _optionalSpeedRemoved = true;
            }
        }

        if (rec.getType() < Ma2fileRecordID.Def.End)
        {
            // builtin record type
            return true;
        }

        var flag = true;
        switch (rec.getType().getEnumName())
        {
            case "NMSSS":
            case "BRSSS":
                var noteData = new CustomSlideNoteData();
                var mirrorMode = Singleton<GamePlayManager>.Instance.GetGameScore(____playerID).UserOption.MirrorMode;
                PrepareBasicNoteData(noteData, __instance, rec, index, ref noteIndex, mirrorMode);
                PrepareBasicSlideData(noteData, __instance, rec, noteIndex, ref slideIndex, mirrorMode);
                var success = noteData.ParseSlideCode(rec.getStr(7), mirrorMode);
                if (success)
                {
                    ____note._noteData.Add(noteData);
                    _currentNoteData = noteData;

                    // Optional per-note speed on custom slides:
                    // NMSSS/... [slide code] [speed], e.g. ... "x0.5" or "-1"
                    if (rec._str.Count > 8 && TryParseSpeedMultiplier(rec.getStr(8), out var noteSpeed))
                    {
                        NoteSpeedMultipliers[noteData] = noteSpeed;
                        NoteSpeedByIndex[noteData.indexNote] = noteSpeed;
                        TrySetNoteSpeedField(noteData, noteSpeed);
                    }
                }
                else
                {
                    flag = false;
                }
                break;
            default:
                flag = false;
                break;
        }
        __result = flag;
        return false;
    }
}
