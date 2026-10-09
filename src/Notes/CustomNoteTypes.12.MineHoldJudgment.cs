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

// 地雷 Hold 持续接触判定、Good 降级及同轨道 Tap 放行。
public partial class CustomNoteTypes
{

    /// <summary>地雷 Hold 自动避开头部、持续到尾部；头部有效按下按 MNTAP 处理，
    /// 持续阶段误碰只记录 Good，由原生尾部流程统一显示和计分。</summary>
    [HarmonyPatch]
    public static class MineHoldAutoPlayTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(HoldNote), "NoteCheck"),
                AccessTools.Method(typeof(BreakHoldNote), "NoteCheck"),
                AccessTools.Method(typeof(TouchHoldC), "NoteCheck"),
                AccessTools.Method(typeof(HoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(BreakHoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(TouchHoldC), "JudgeTotalResult"),
                AccessTools.Method(typeof(HoldNote), "SetAutoPlayJudge"),
                AccessTools.Method(typeof(TouchHoldC), "SetAutoPlayJudge"),
                // ⚠️ BreakHoldNote 的基类是 NoteBase（不是 HoldNote！），它自己 override 了
                // SetAutoPlayJudge——漏掉会导致绝赞地雷 hold 的头自动判定不生效（历史 bug #28）。
                AccessTools.Method(typeof(BreakHoldNote), "SetAutoPlayJudge"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                if (inst.opcode == OpCodes.Call && GetMethodOperandName(inst.operand) == "IsAutoPlay")
                {
                    // call IsAutoPlay() → ldarg.0; call IsAutoPlayOrMineHold(object)
                    // ⚠️ 这里有意匹配所有 IsAutoPlay 调用（包括静态 GameManager.IsAutoPlay）：
                    // MineHold 的判定整体按 autoplay 处理（不碰 = 头自动 CP + 身体自动按满 +
                    // 尾部自动 CP），原版 NoteCheck 的手动按钮检测分支因此关闭，玩家触碰改由
                    // MineHoldTouchCheck（NoteCheck prefix）独立侦测头部 Miss / 持续阶段 Good。
                    // 曾误以为静态调用是 bug 而跳过（修 1），结果"自动按住"效果消失且
                    // JudgeTotalResult 走回原版 JudgeHoldTotal 分支（用户实测回归，2026-08-27）。
                    // ⚠️ 被替换指令可能带分支标签/异常块边界，必须转移到第一条替换指令
                    // （历史 bug #17/#27：否则 "Label #N is not marked" → patch 全挂）。
                    var ldarg = new CodeInstruction(OpCodes.Ldarg_0);
                    ldarg.labels.AddRange(inst.labels);
                    ldarg.blocks.AddRange(inst.blocks);
                    yield return ldarg;
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(IsAutoPlayOrMineHold)));
                    continue;
                }

                if (inst.opcode == OpCodes.Call && GetMethodOperandName(inst.operand) == "AutoJudge")
                {
                    // call AutoJudge() → ldarg.0; call MineHoldAutoJudge(object)
                    var ldarg = new CodeInstruction(OpCodes.Ldarg_0);
                    ldarg.labels.AddRange(inst.labels);
                    ldarg.blocks.AddRange(inst.blocks);
                    yield return ldarg;
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(MineHoldAutoJudge)));
                    continue;
                }

                yield return inst;
            }
        }

        private static string GetMethodOperandName(object operand)
        {
            if (operand is MethodInfo mi) return mi.Name;
            var prop = operand?.GetType().GetProperty("Name");
            return prop?.GetValue(operand) as string;
        }
    }

    /// <summary>缓存当前谱面 NoteData 列表（loadMa2Main 后）。地雷 hold 触碰检测需要
    /// 判定同轨道普通 tap 是否在判定窗口内（点击优先给 tap，地雷 hold 不 Miss）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static void CacheNoteListPostfix(NotesReader __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        try
        {
            _activeNoteList = __instance.GetNoteList();
        }
        catch
        {
        }
    }

    // Record tail-frame contact before native settlement. Only a head hit ends
    // immediately; body contact must keep running the native Hold lifecycle.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HoldNote), "NoteCheck")]
    public static bool MineHoldNoteCheckTouch(HoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        return !MineHoldTouchCheck(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BreakHoldNote), "NoteCheck")]
    public static bool MineBreakHoldNoteCheckTouch(BreakHoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        return !MineHoldTouchCheck(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TouchHoldC), "NoteCheck")]
    public static bool MineTouchHoldNoteCheckTouch(TouchHoldC __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        return !MineHoldTouchCheck(__instance);
    }

    /// <summary>头前使用 MNTAP 的按下窗口；持续阶段误碰只降为 Good，返回 true 表示已收尾。</summary>
    private static bool MineHoldTouchCheck(NoteBase note)
    {
        if (IsFakeNoteOwner(note)) return false;
        try
        {
            if (note is not (MineHoldNote or MineBreakHoldNote or MineTouchHoldC)) return false;
            var mineBehaviour = note.GetComponent<MineNoteBehaviour>();
            if (mineBehaviour != null && !mineBehaviour.IsMine) return false;
            if (MineHoldPenalties.ContainsKey(note) && note.GetJudgeResult() != NoteJudge.ETiming.End) return true;
            if (note.GetJudgeResult() != NoteJudge.ETiming.End) return false;

            var now = NotesManager.GetCurrentMsec();
            var traverse = Traverse.Create(note);
            var tail = traverse.Field("TailMsec").GetValue<float>();
            // AppearMsec is the judgment time; StartMsec is only the visual spawn time.
            var head = traverse.Field("AppearMsec").GetValue<float>();
            if (now > tail) return false;
            // Match MineAutoJudgePostfix's MNTAP cutoff, independent of note speed.
            var beforeHead = now < head - 4.166667f;
            bool down, held;
            var button = (InputManager.ButtonSetting)traverse.Property("ButtonId").GetValue<int>();
            if (note is TouchHoldC)
            {
                down = traverse.Method("IsHoldTriggerDown").GetValue<bool>();
                held = !beforeHead && traverse.Method("IsHoldTriggerPush").GetValue<bool>();
            }
            else
            {
                down = PlayableNoteInput.InGameButtonDown(note.MonitorId, button, note) ||
                       PlayableNoteInput.InGameTouchPanelAreaDown(note.MonitorId, button, note);
                held = !beforeHead && (PlayableNoteInput.GetButtonPush(note.MonitorId, button, note) ||
                       PlayableNoteInput.GetTouchPanelAreaPush(note.MonitorId, button, note));
            }
            if (!down && !held) return false;

            // Preserve same-lane ordinary Tap ±150ms priority, including D-zone bindings.
            if (note is not TouchHoldC && HasNormalTapInJudgeWindow((int)button, now, TryDZoneArea(note, out _)))
                return false;

            if (beforeHead)
            {
                // Just holding before the window is not a MNTAP hit. A fresh down
                // must also satisfy the native timing offset, note order and consumption.
                var offset = Singleton<GamePlayManager>.Instance.GetGameScore(note.MonitorId).UserOption.GetJudgeTimingFrame();
                if (now - offset * 16.666666f < head + note.GetJudgeStartMsec()) return false;
                if (!traverse.Method("IsJudgeNote").GetValue<bool>()) return false;
                var used = note is TouchHoldC
                    ? traverse.Method("IsUsedThisFrameArea").GetValue<bool>()
                    : PlayableNoteInput.IsUsedThisFrame(note.MonitorId, button, note);
                if (used) return false;
                var diff = now - head;
                var timing = NoteJudge.GetJudgeTiming(ref diff, offset,
                    traverse.Field("JudgeType").GetValue<NoteJudge.EJudgeType>());
                if (timing == NoteJudge.ETiming.End || timing == NoteJudge.ETiming.TooLate) return false;
                if (note is TouchHoldC) traverse.Method("SetUsedThisFrame").GetValue();
                else PlayableNoteInput.SetUsedThisFrame(note.MonitorId, button, note);
                MineHoldPenalties[note] = NoteJudge.ETiming.TooLate;
                traverse.Field("JudgeTimingDiffMsec").SetValue(diff);
                traverse.Field("JudgeResult").SetValue(NoteJudge.ETiming.TooLate);
                traverse.Method("EndNote").GetValue();
                return true;
            }

            // No early EndNote, no Miss and no repeated score: the native tail
            // writes its result once, then ApplyMineJudgeTiming applies this cap.
            MineHoldPenalties[note] = NoteJudge.ETiming.LateGood;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>同轨道（startButtonPos 相同）是否有普通 tap 的判定窗口覆盖 now。
    /// 判定窗口 ±150ms（覆盖 good 判定 + 容差）。地雷/自定义音符（NoteKinds≠None）排除；
    /// 只认 tap 系（Tap/ExTap/Break/ExBreakTap）——hold/slide/star 不参与放行。</summary>
    private static bool HasNormalTapInJudgeWindow(int buttonPos, float now, bool dZone = false)
    {
        var list = _activeNoteList;
        if (list == null) return false;
        foreach (var nd in list)
        {
            // 逐元素防御：2026-08-29 用户开启 TapInHoldFix 后实测——放行检查曾抛
            // NullReferenceException（根因：NotesTypeID 自定义 op_Equality 对 null 参数不健壮，
            // `nd.type == null` 求值即 NRE；已改用 ReferenceEquals 引用比较修复）。单个坏元素
            // 跳过继续找 tap，保证放行行为不被破坏。
            try
            {
                if (nd == null || IsFakeNote(nd) || nd.startButtonPos != buttonPos) continue;
                if (DZonePositions.TryGetValue(nd, out _) != dZone) continue;
                if (NoteKinds.TryGetValue(nd.indexNote, out var kind) && kind != CustomNoteKind.None) continue;
                if (ReferenceEquals(nd.type, null)) continue;
                var t = nd.type.getEnum();
                if (t != NotesTypeID.Def.Tap && t != NotesTypeID.Def.ExTap
                    && t != NotesTypeID.Def.Break && t != NotesTypeID.Def.ExBreakTap) continue;
                var T = nd.time.msec;
                if (now >= T - 150f && now <= T + 150f) return true;
            }
            catch
            {
                // 坏元素：跳过继续找（防御）
            }
        }
        return false;
    }

    /// <summary>hold 复用时清除"被碰到"登记（池对象复用）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HoldNote), "Initialize")]
    public static void HoldInitClearTouched(HoldNote __instance)
    {
        MineHoldPenalties.Remove(__instance);
    }

    // BreakHoldNote derives directly from NoteBase, so HoldNote.Initialize's
    // postfix does not run for this pool. Never carry a mine hit into the next note.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakHoldNote), "Initialize")]
    public static void BreakHoldInitClearTouched(BreakHoldNote __instance)
    {
        MineHoldPenalties.Remove(__instance);
    }

    /// <summary>绝赞 touchhold（TouchBreakHoldC）：判定窗口与普通 touchhold 相同，不强制 CP；
    /// 统计由 TouchBreakHoldC.SetPlayResult override 按 BREAK 记录。</summary>

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TouchHoldC), "Initialize")]
    public static void TouchHoldInitClearTouched(TouchHoldC __instance)
    {
        MineHoldPenalties.Remove(__instance);
    }
}
