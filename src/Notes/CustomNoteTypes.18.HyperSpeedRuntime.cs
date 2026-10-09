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

// 初始化速度应用及滑条进度计算。
public partial class CustomNoteTypes
{

    [HarmonyPatch]
    public static class NoteSpeedContextPatch
    {
        // 已删除 2026-08-23：全程序集反射 patch（所有 NoteBase 子类全部方法 + 全部含 NoteData
        // 参数方法）只维护 _currentNoteData 静态字段，而该字段只在加载期被 ApplyOptionalHyperSpeed
        // 读取，加载链自身（LoadCustomNote 前缀 / ApplyOptionalHyperSpeed postfix）已自设——
        // 运行时每帧反射开销纯属浪费。SV/HS 精简（2026-08-23）移除。
    }

    private static float SanitizeSpeedMultiplier(float multiplier)
    {
        // The game does not support zero/negative scroll speed.
        // Treat them as normal speed to avoid broken notes.
        return multiplier > 0f ? multiplier : 1f;
    }

    [HarmonyPatch]
    public static class NoteSpeedApplyPatch
    {
        // 已删除 2026-08-23（SV/HS 精简）：反射式宽泛 patch（全部 Initialize/SetData/Init +
        // NoteData 方法）只为应用 x{m} 内嵌速度到 AppearMsec/StarLaunchMsec。
        // 内嵌倍率现统一由 GetNoteSpeed/GetTouchSpeed postfix 缩放 DefaultMsec 实现；
        // 地雷 MineNoteBehaviour 挂载已改为 Mine* 类 Initialize 显式 EnsureMineBehaviour。
    }



    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * 以下内容是为了实现自定义 Slide
     * 
     */

    /*
     * 把 GetSlidePath 和 GetSlideHitArea 和 GetSlideLength 重定向到我可以控制的函数上, 并且多推几个参数进来
     */

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SlideRoot), "Initialize")]
    public static void SlideRootInitApplySpeed(object __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var note = FindNoteDataFromArgs(__instance, __args);
        if (note == null) return;
        TryApplySpeedToNoteObject(__instance, note);
    }

    public static float MajdataSlideHSpeed(NoteData note)
    {
        var state = RuntimeCharts.Note(note);
        return state.NoteSpeedMultipliers.TryGetValue(note, out var speed) ? speed
            : GetCurveMultAt(state.HsCurves, null, state.StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var stream) ? stream : "slide", note.time.msec);
    }

    public static bool MajdataHasSlideSvFor(NoteData note)
    {
        if (SpeedClass(note).IgnoreSV) return false;
        var state = RuntimeCharts.Note(note);
        return state.StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var stream)
            ? state.SvCurves.ContainsKey(stream) : state.SvCurves.ContainsKey("slide");
    }

    public static float MajdataSlideProgress(NoteData note, float start, float duration, float now)
    {
        var previous = RuntimeCharts.Enter(RuntimeCharts.Note(note));
        try
        {
            if (duration <= .001f) return 1;
            if (SpeedClass(note).IgnoreSV) return Mathf.Clamp01((now - start) / duration);
            if (!StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var stream)) return MajdataSlideProgress(start, duration, now);
            var origin = SvIntegral(stream, start);
            var range = SvIntegral(stream, start + duration) - origin;
            return ScrollVisualTiming.Slide(SvIntegral(stream, Mathf.Clamp(now, start, start + duration)) - origin, range, duration);
        }
        finally { RuntimeCharts.Exit(previous); }
    }

    // Typed-only SV: NULL restores 1, and global SV never affects slide progress.
    private static List<(float Msec, float Mult, float Cum)> MajdataSlideSv => RuntimeCharts.Current.MajdataSlideSv;

    public static bool MajdataHasSlideSv => SvCurves.ContainsKey("slide");

    public static float MajdataSlideProgress(float start, float duration, float now)
    {
        if (duration <= .001f) return 1;
        if (!SvCurves.ContainsKey("slide") || MajdataSlideSv.Count == 0) return Mathf.Clamp01((now - start) / duration);
        float Integral(float end)
        {
            if (end < MajdataSlideSv[0].Msec) return end;
            var lo = 0;
            var hi = MajdataSlideSv.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (MajdataSlideSv[mid].Msec <= end) lo = mid;
                else hi = mid - 1;
            }
            var point = MajdataSlideSv[lo];
            return point.Cum + (end - point.Msec) * point.Mult;
        }
        var origin = Integral(start);
        var range = Integral(start + duration) - origin;
        return ScrollVisualTiming.Slide(Integral(Mathf.Clamp(now, start, start + duration)) - origin, range, duration);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StarNote), "Initialize")]
    public static void StarNoteInitApplySpeed(object __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var note = FindNoteDataFromArgs(__instance, __args);
        if (note == null) return;
        TryApplySpeedToNoteObject(__instance, note);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakStarNote), "Initialize")]
    public static void BreakStarNoteInitApplySpeed(object __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var note = FindNoteDataFromArgs(__instance, __args);
        if (note == null) return;
        TryApplySpeedToNoteObject(__instance, note);
    }

    private static NoteData FindNoteDataFromArgs(object __instance, object[] __args)
    {
        if (__args != null)
        {
            foreach (var arg in __args)
            {
                if (arg is NoteData nd) return nd;
            }
        }

        if (__instance != null)
        {
            var traverse = Traverse.Create(__instance);
            foreach (var fieldName in new[] { "noteData", "_noteData", "NoteData", "data", "_data" })
            {
                var field = traverse.Field(fieldName);
                if (field.FieldExists())
                {
                    var data = field.GetValue<NoteData>();
                    if (data != null) return data;
                }
            }
        }

        return null;
    }
}
