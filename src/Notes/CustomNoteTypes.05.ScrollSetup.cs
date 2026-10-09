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

// SV 曲线注入及音符初始化速度。
public partial class CustomNoteTypes
{

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static void LoadMa2MainSvInjectPostfix(NotesReader __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        try
        {
            if (PendingSvSegments.Count == 0 && PendingHsSegments.Count == 0 && PendingRingSegments.Count == 0) return;

            SvCurves.Clear();
            HsCurves.Clear();
            BuildSpeedCurves(__instance, PendingSvSegments, SvCurves, SvClearTimes);
            BuildSpeedCurves(__instance, PendingHsSegments, HsCurves, HsClearTimes);
            BuildSvCumulatives();

            var svCount = SvCurves.Values.Sum(x => x.Count);
            var hsCount = HsCurves.Values.Sum(x => x.Count);
            if (!_loggedSvInject)
            {
                _loggedSvInject = true;
                MelonLogger.Msg($"[CustomNoteType] SV/HS active: {svCount} sv segments, {hsCount} hs segments");
            }

            // 预构建每音符表：类型、判定时刻总倍率 mSv×mHs（等效流速/激活提前量用）。
            try
            {
                var noteList = __instance.GetNoteList();
                if (noteList != null)
                {
                    SvScrollPosByNoteIndex.Clear();
                    SvWindowByNoteIndex.Clear();
                    SvTypeByNoteIndex.Clear();
        IgnoreSvByNoteIndex.Clear();
                    // 注意：StreamTypeByNoteIndex 不可在此 Clear——它在 loadNote 期间（ApplyOptionalHyperSpeed）
                    // 填充，而 loadMa2Main postfix 在 loadNote 循环之后运行；Clear 会丢掉刚填好的流标记，
                    // 导致流内音符回退普通类型（吃全局/类型曲线）。清空只应在 loadMa2 前缀/全量重置处做。
                    SpeedMultByNoteIndex.Clear();
                    foreach (var note in noteList)
                    {
                        if (note == null) continue;
                        if (SpeedClass(note).ReferenceMotion) ReferenceMotionByNoteIndex.Add(note.indexNote);
                        // 流内音符（行尾 s{N}）类型键 = 流键 s{N}：只吃本流曲线；
                        // 否则按音符种类（tap/star/hold/...）查类型曲线，无则全局。
                        var type = StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var streamType)
                            ? streamType
                            : ResolvePlayableSvType(note);
                        SvTypeByNoteIndex[note.indexNote] = type;
                        if (SpeedClass(note).IgnoreSV) IgnoreSvByNoteIndex.Add(note.indexNote);
                        var mHs = ResolvePlayableHs(note);
                        var scrollPos = NoteSvIntegral(note.indexNote, type, note.time.msec);
                        SvScrollPosByNoteIndex[note.indexNote] = scrollPos;
                        // 等效流速只由 HS 决定（MV：speed = noteSpeed×HS；SV 只进 scroll 积分，
                        // 不缩放每音符下落/渐入/激活）——若乘 mSv，SV 段后的音符（如重叠流
                        // 冻结段之后的 7h/2）会被末段倍率（如 0.5）整体放慢，与 MV 不符。
                        SpeedMultByNoteIndex[note.indexNote] = mHs;
                        // HS 单独存：飞行窗口 W = d/mHs（MV：speed = noteSpeed×HS 决定下落速度）。
                        HsMultByNoteIndex[note.indexNote] = mHs;
                        // hold 身体尾 scrollPos = ∫sv(type, T+Len)：身体进度随 scroll 驱动
                        // （原版帧处理后再对齐完整长条显示）。
                        var tailSp = 0f;
                        if (note.end != null)
                        {
                            SvTailScrollPosByNoteIndex[note.indexNote] = NoteSvIntegral(note.indexNote, type, note.end.msec);
                            tailSp = SvTailScrollPosByNoteIndex[note.indexNote];
                        }
                        // 每音符自包含飞行时间表（加载期一次算好）：
                        // 播放路径（位置/出现/渐入/hold 身体）只查本表，零全局查询。
                        BuildNoteScrollTable(note.indexNote, type, note, scrollPos, tailSp);
                    }

                    // Each 双押伙伴映射（原版 loadNote 后处理已填好 eachChild）：
                    // 流速不同的 each 对在 SvApplyNoteSpeedOnInit 里隐藏辅助条。
                    EachChildByNoteIndex.Clear();
                    foreach (var note in noteList)
                    {
                        if (note == null || note.eachChild == null || note.eachChild.Count == 0) continue;
                        var childIndexes = new List<int>(note.eachChild.Count);
                        foreach (var c in note.eachChild)
                            if (c != null) childIndexes.Add(c.indexNote);
                        EachChildByNoteIndex[note.indexNote] = childIndexes;
                    }

                    MelonLogger.Msg($"[CustomNoteType] SV/HS note table built: {SpeedMultByNoteIndex.Count} notes");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomNoteType] SV/HS note table failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CustomNoteType] SV/HS setup failed: {ex.Message}");
        }
    }

    // Keep native DefaultMsec/StartMsec; signed HS affects presentation only.
    // Record the window and hide each guides whose partners use different HS.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "Initialize")]
    public static void SvApplyNoteSpeedOnInit(NoteBase __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (SpeedMultByNoteIndex.Count == 0) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float mult;
            if (!SpeedMultByNoteIndex.TryGetValue(index, out mult)) return;

            // 飞行窗口 W = d/mHs（见类注释；d = 原版 DefaultMsec，永不缩放）。
            var d = tr.Field("DefaultMsec").GetValue<float>();
            float mHs;
            if (!HsMultByNoteIndex.TryGetValue(index, out mHs)) mHs = 1f;
            SvWindowByNoteIndex[index] = mHs == 0 ? float.PositiveInfinity : d / mHs;

            // Each 双押辅助条：与 each 伙伴流速不同（SV/HS 倍率差）→ 隐藏连接线
            // （流速不同时连线两端移动速度不一致，辅助条会错位/拉伸）。
            if (EachChildByNoteIndex.TryGetValue(index, out var childIdxs))
            {
                foreach (var ci in childIdxs)
                {
                    float cm;
                    if (SpeedMultByNoteIndex.TryGetValue(ci, out cm) && Math.Abs(cm - mult) >= 0.001f)
                    {
                        var guide = tr.Field("GuideObj").GetValue<NoteGuide>();
                        if (guide != null) guide.HideEachGuide();
                        break;
                    }
                }
            }
        }
        catch
        {
        }
    }
}
