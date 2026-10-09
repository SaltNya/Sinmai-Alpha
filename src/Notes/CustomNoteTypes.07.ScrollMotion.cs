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

// Touch 初始化速度、激活时机及 Hold 身体滚动。
public partial class CustomNoteTypes
{

    /// <summary>TouchNoteB 在 base.Initialize 之后自己重写 DefaultMsec/StartMsec
    /// （DefaultMsec = GetTouchSpeedForBeat(GetTouchSpeed())×4——原版有独立的 touch 流速选项；
    /// StartMsec = AppearMsec − DefaultMsec − GetTouchDispTimes()，淡入窗口 = D×0.25）。
    /// touch 系同样按类型分组总倍率 m = mSv×mHs 缩放（等效流速，无 scroll 视觉）。</summary>
    /// <summary>TouchNoteB 飞行窗口记录（等效流速缩放已删除——与 NoteBase 版一致，
    /// DefaultMsec/StartMsec 不缩放，touch 淡入/位置由原版 + scroll 层驱动；
    /// 本 postfix 只填 SvWindowByNoteIndex 供 SvRealTimeVisualPostfix/激活提前量使用）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TouchNoteB), "Initialize")]
    public static void SvApplyNoteSpeedOnTouchInit(TouchNoteB __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (SpeedMultByNoteIndex.Count == 0) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float mult;
            if (!SpeedMultByNoteIndex.TryGetValue(index, out mult)) return;

            var d = tr.Field("DefaultMsec").GetValue<float>();
            float mHs;
            if (!HsMultByNoteIndex.TryGetValue(index, out mHs)) mHs = 1f;
            SvWindowByNoteIndex[index] = mHs == 0 ? float.PositiveInfinity : d / mHs;
        }
        catch
        {
        }
    }

    // Signed scroll changes presentation only; native NoteCheck still owns play.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "GetNoteYPosition")]
    public static void SvRealTimeVisualPostfix(NoteBase __instance, ref float __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (TryRingPresentation(__instance, false, out var presentation))
            __result = RingNativeY(__instance, presentation.Radius);
        BounceNoteVisualPostfix(__instance, ref __result);
    }

    // UpdateCtrl pools notes before Initialize, so this uses load-time data.
    // Native pools impose a 10-second lead ceiling; judgment timing is unchanged.
    private static float SvScaleActivationLead(float lead, float leadDiv, NoteData note)
    {
        lead = BounceHoldActivationLead(lead, note);
        if (SpeedMultByNoteIndex.Count == 0 || note == null) return lead;
        if (note.type.isAllSlide())
        {
            var hs = ResolvePlayableHs(note);
            if (Math.Abs(hs - 1) < .00001f) return lead;
            // Reserve enough time for the earliest slide appearance option.
            // HS uses absolute speed for fade lead; zero speed appears at the head.
            var offset = ScrollVisualTiming.SlideAppearanceOffset(RingViewSpeed(lead / leadDiv) * hs, -1);
            return Math.Min(10000f, Math.Max(lead, -offset));
        }
        if (!HsMultByNoteIndex.TryGetValue(note.indexNote, out var multiplier) ||
            !SvScrollPosByNoteIndex.TryGetValue(note.indexNote, out var target)) return lead;
        if (note.type.isTouch()) return TouchVisualActivationLead(note.indexNote, note.time.msec, lead / leadDiv, lead);
        var visible = GetRingFirstVisibleTimeCached(note, lead / leadDiv);
        return float.IsNaN(visible) ? lead : Math.Min(10000f, Math.Max(lead, note.time.msec - visible));
    }

    /// <summary>把 UpdateCtrl 里 apperMsecTap/apperMsecTouch 的读取替换为
    /// SvScaleActivationLead(apperMsec, 当前NoteData)——激活提前量随 SV 缩放。</summary>
    [HarmonyPatch]
    public static class SvActivationLeadTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return [AccessTools.Method(typeof(GameCtrl), "UpdateCtrl")];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var helper = AccessTools.Method(typeof(CustomNoteTypes), nameof(SvScaleActivationLead));
            var list = instructions.ToList();

            // 找循环里保存当前 NoteData 的局部变量（UpdateCtrl 主循环 V_11，类型 Manager.NoteData）。
            // Harmony 反射模式（MelonLoader 默认）：ldloc/stloc 的 operand 是 int 局部变量索引，
            // 类型必须从 MethodBody.LocalVariables 解析（Cecil VariableDefinition 检查在此模式永远不命中）。
            // Cecil 模式（HarmonyX TranspilerContext）：operand 是 Mono.Cecil VariableDefinition。
            Mono.Cecil.Cil.VariableDefinition noteVar = null;
            var noteVarIdx = -1;
            foreach (var inst in list)
            {
                if ((inst.opcode == OpCodes.Ldloc_S || inst.opcode == OpCodes.Ldloc
                     || inst.opcode == OpCodes.Stloc_S || inst.opcode == OpCodes.Stloc)
                    && inst.operand is Mono.Cecil.Cil.VariableDefinition vd
                    && vd.VariableType.FullName == "Manager.NoteData")
                {
                    noteVar = vd;
                    break;
                }
            }

            if (noteVar == null)
            {
                try
                {
                    var method = AccessTools.Method(typeof(GameCtrl), "UpdateCtrl");
                    foreach (var lv in method.GetMethodBody().LocalVariables)
                    {
                        if (lv.LocalType == typeof(Manager.NoteData))
                        {
                            noteVarIdx = lv.LocalIndex;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[CustomNoteType] SV lead transpiler: cannot resolve NoteData local: {ex.Message}");
                }
            }

            if (noteVar == null && noteVarIdx < 0)
            {
                MelonLogger.Warning("[CustomNoteType] SV lead transpiler: NoteData variable not found in UpdateCtrl; activation lead is NOT scaled by SV/HS (notes may pop in without fade-in)");
                foreach (var inst in list) yield return inst;
                yield break;
            }

            foreach (var inst in list)
            {
                if (inst.opcode == OpCodes.Ldfld && inst.operand is FieldInfo leadFi
                    && (leadFi.Name == "apperMsecTap" || leadFi.Name == "apperMsecTouch"))
                {
                    yield return inst;                                        // ldfld apperMsec*
                    // leadDiv：apperMsecTap = 2×DefaultMsec、apperMsecTouch = 1.25×DefaultMsec
                    // （反推 d = lead/leadDiv 给 SvScaleActivationLead 算 window = d/mHs）。
                    float leadDiv = leadFi.Name == "apperMsecTap" ? 2f : 1.25f;
                    yield return new CodeInstruction(OpCodes.Ldc_R4, leadDiv);
                    if (noteVar != null)
                    {
                        yield return new CodeInstruction(OpCodes.Ldloc_S, noteVar); // Cecil：VariableDefinition
                    }
                    else
                    {
                        yield return new CodeInstruction(OpCodes.Ldloc_S, noteVarIdx); // 反射：int 局部变量索引
                    }

                    yield return new CodeInstruction(OpCodes.Call, helper);   // call SvScaleActivationLead(float,float,NoteData)
                    continue;
                }

                yield return inst;
            }
        }
    }

    /// <summary>HoldNote.Execute 身体进度（原 num4 = 1−(TailMsec−adj−now)/DefaultMsec 纯时间驱动）
    /// 改为 scroll 驱动：p = 1−(s(T+Len)−s(now))/W。SV 交替段身体随 s 摆动（hold 条抽搐）、
    /// SV=0 冻结段身体停住（时停）、0.5 段慢速收束——与 MajdataViewAlpha HoldDrop 一致
    /// （尾 distance = 4.8 − speed×(s(T+LastFor)−s(now))）。
    /// 无 SV/HS 表（普通谱）时精确复刻原版时间公式，行为不变。</summary>
    private static float SvHoldTailProgress(NoteBase note)
    {
        if (TryRingPresentation(note, true, out var presentation))
            return (presentation.Radius - SpawnDefaultRadius) / SpawnRadiusSpan;
        var now = NotesManager.GetCurrentMsec();
        var tail = (float)FTailMsec.GetValue(note) - (float)FMaiBugAdjust.Invoke(note, null);
        return 1f - (Math.Max(tail, now) - now) / (float)FDefaultMsec.GetValue(note);
    }

    /// <summary>HoldNote.Execute：把身体进度 num4 的计算
    /// （`1f * (1f - num3 / DefaultMsec)` 时间驱动）替换为 SvHoldTailProgress(this)（scroll 驱动）。
    /// 匹配模式（实测 IL：HoldNote.Execute IL_0139-IL_014e 与 BreakHoldNote.Execute IL_0139-IL_014e 一致）：
    /// ldc.r4 1, ldc.r4 1, ldloc num3, ldarg.0（ldfld 的实例加载！）, ldfld DefaultMsec, div, sub, mul → stloc num4。
    /// 曾漏掉 ldarg.0 导致模式永不匹配——所有 hold 身体保持时间驱动（SV 交替段无抽搐）。</summary>
    [HarmonyPatch]
    public static class HoldBodyScrollTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // BreakHoldNote 有自己的 Execute（num4 模式相同）——必须一起 patch，
            // 否则 break hold（含 MBHLD 地雷长条）身体仍走原版时间公式。
            return [
                AccessTools.Method(typeof(HoldNote), "Execute"),
                AccessTools.Method(typeof(BreakHoldNote), "Execute")
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var helper = AccessTools.Method(typeof(CustomNoteTypes), nameof(SvHoldTailProgress));
            var list = instructions.ToList();
            var replaced = false;
            for (var i = 0; i < list.Count - 8; i++)
            {
                var isOneA = list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float fa && fa == 1f;
                var isOneB = list[i + 1].opcode == OpCodes.Ldc_R4 && list[i + 1].operand is float fb && fb == 1f;
                var isLdloc = list[i + 2].opcode == OpCodes.Ldloc || list[i + 2].opcode == OpCodes.Ldloc_S;
                // ldfld 需要实例——IL 里 ldfld DefaultMsec 前必有 ldarg.0（实测 IL_0145）。
                // 覆盖全部 Ldarg 变体（Ldarg_0..3 / Ldarg_S / Ldarg）以防未来编译器差异。
                var isLdArg = list[i + 3].opcode == OpCodes.Ldarg_0
                    || list[i + 3].opcode == OpCodes.Ldarg_1
                    || list[i + 3].opcode == OpCodes.Ldarg_2
                    || list[i + 3].opcode == OpCodes.Ldarg_3
                    || list[i + 3].opcode == OpCodes.Ldarg_S
                    || list[i + 3].opcode == OpCodes.Ldarg;
                var isDfl = list[i + 4].opcode == OpCodes.Ldfld
                    && ((list[i + 4].operand is FieldInfo fi && fi.Name == "DefaultMsec")
                        || (list[i + 4].operand is Mono.Cecil.FieldDefinition fdv && fdv.Name == "DefaultMsec"));
                var isTail = list[i + 5].opcode == OpCodes.Div
                    && list[i + 6].opcode == OpCodes.Sub
                    && list[i + 7].opcode == OpCodes.Mul
                    && (list[i + 8].opcode == OpCodes.Stloc || list[i + 8].opcode == OpCodes.Stloc_S);
                if (isOneA && isOneB && isLdloc && isLdArg && isDfl && isTail)
                {
                    // 替换前 8 条指令为 ldarg.0 + call SvHoldTailProgress（stloc [i+8] 槽位保留）。
                    list[i] = new CodeInstruction(OpCodes.Ldarg_0);
                    list[i + 1] = new CodeInstruction(OpCodes.Call, helper);
                    list[i + 2] = new CodeInstruction(OpCodes.Nop);
                    list[i + 3] = new CodeInstruction(OpCodes.Nop);
                    list[i + 4] = new CodeInstruction(OpCodes.Nop);
                    list[i + 5] = new CodeInstruction(OpCodes.Nop);
                    list[i + 6] = new CodeInstruction(OpCodes.Nop);
                    list[i + 7] = new CodeInstruction(OpCodes.Nop);
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
                MelonLogger.Warning("[CustomNoteType] HoldBodyScrollTranspiler: num4 pattern not found in Execute; hold body stays time-driven (SV jitter on hold bars disabled)");
            return list;
        }
    }
}
