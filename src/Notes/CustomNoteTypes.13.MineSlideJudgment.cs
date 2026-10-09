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

// 地雷判定反转、滑条容错和结束等待。
public partial class CustomNoteTypes
{

    public static NoteJudge.ETiming ApplyMineJudgeTiming(NoteJudge.ETiming timing, object note)
    {
        // 独立地雷类（ISelfJudgingMineNote）自己负责反转（override Judge / JudgeToolate），
        // 这里必须放行，否则双重反转。Hold/Slide 等非自反转类继续由 transpiler 反转。
        if (note is ISelfJudgingMineNote) return timing;

        if (note is NoteBase hold && MineHoldPenalties.TryGetValue(hold, out var penalty))
            return penalty;

        // Untouched mine Holds settle as native auto-CP. Do not invert that CP.
        if (note is MineHoldNote or MineBreakHoldNote or MineTouchHoldC) return timing;

        var behaviour = (note as Component)?.GetComponent<MineNoteBehaviour>();
        // MineSlideRoot/MineSlideFan 只可能来自地雷池——即使 behaviour 没挂上（NoteKinds 按
        // indexNote 查不到导致 EnsureMineBehaviour 兜底失败），也按地雷处理（历史 bug #22）。
        if ((behaviour == null || !behaviour.IsMine) && note is not (MineSlideRoot or MineSlideFan)) return timing;

        if (!_loggedMineInversion)
        {
            _loggedMineInversion = true;
            MelonLogger.Msg($"[CustomNoteType] Mine judge inversion active (first mine: note {(behaviour != null ? behaviour.NoteIndex : -1)})");
        }

        // slide 特判（用户 2026-08 最终规则）：SlideRoot.Judge 只在全部轨道被划掉时被调用
        // （NoteCheck 里 hitIndex >= count 才调），JudgeToolate 反之（没划完/星星滑到末尾）。
        //   划完（提前划掉全部轨道，打中）→ Miss(TooLate 14)
        //   没划完（星星跟着轨道滑到最后）→ Critical Perfect(7)
        if (note is SlideRoot slide)
        {
            var completed = IsMineSlideCompleted(slide);
            return completed ? NoteJudge.ETiming.TooLate : NoteJudge.ETiming.Critical;
        }

        // 地雷判定（标准规则，2026-08-26 用户要求四类统一）：任何有效触碰
        // （原版 Critical/Perfect/Great/Good/TooFast）-> Miss(TooLate)；没碰到（TooLate/End）-> Critical。
        // 适用于非自反转的地雷：HoldNote/BreakHoldNote/TouchHoldC 的 JudgeTotalResult（stfld JudgeResult）。
        // ⚠️ 头判定（JudgeHoldHead/JudgeToolate override，写 JudgeHeadResult）刻意不拦截：
        //   JudgeHoldTotal 的输入需要原版头判定才能算出正确总判定（没按 → 头 TooLate → 总 TooLate →
        //   这里反转成 CP；按头 → 头 CP/Good → 总 Good/Great → 反转成 Miss）。头判定显示/SE 保持原版
        //   （玩家按下时刻的反馈），最终炸/不炸由总判定决定。
        return MineJudgeHelper.InvertStandard(timing);
    }

    /// <summary>slide 是否"划完"：SlideRoot 看 _hitIndex >= _hitAreaList.Count - 1；
    /// SlideFan（3 条线）要求每条线都 >= 各自的 Count - 1（与原版 grace 判定条一致）。</summary>
    private static bool IsMineSlideCompleted(SlideRoot slide)
    {
        try
        {
            var traverse = Traverse.Create(slide);
            if (slide is SlideFan)
            {
                var hitIndexes = traverse.Field("_hitIndex").GetValue<int[]>();
                var areaLists = traverse.Field("_hitAreaList").GetValue<System.Collections.IEnumerable>();
                if (hitIndexes == null || areaLists == null) return false;
                var i = 0;
                foreach (var list in areaLists)
                {
                    var count = (list as System.Collections.ICollection)?.Count ?? 0;
                    if (i >= hitIndexes.Length || hitIndexes[i] < count - 1) return false;
                    i++;
                }

                return i >= 3;
            }
            else
            {
                var hitIndex = traverse.Field("_hitIndex").GetValue<int>();
                var areas = traverse.Field("_hitAreaList").GetValue<System.Collections.ICollection>();
                return areas != null && hitIndex >= areas.Count - 1;
            }
        }
        catch
        {
            return false;
        }
    }

    [HarmonyPatch]
    public static class MineJudgeInversion
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(NoteBase), "Judge"),
                AccessTools.Method(typeof(NoteBase), "JudgeToolate"),
                AccessTools.Method(typeof(TouchNoteB), "Judge"),
                AccessTools.Method(typeof(SlideRoot), "Judge"),
                AccessTools.Method(typeof(SlideRoot), "JudgeToolate"),
                AccessTools.Method(typeof(HoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(BreakHoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(TouchHoldC), "JudgeTotalResult"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                if (inst.opcode == OpCodes.Stfld && IsJudgeResultField(inst.operand))
                {
                    // ⚠️ 后置反转（历史 bug #23）：原版 SlideRoot.Judge 里有 br.s 分支直接跳到
                    // stfld JudgeResult（GetSlideJudgeTiming 路径和 autoplay 路径都是），
                    // 前置插入（ldarg.0; call）会被分支跳过 → 原值直写 → 显示 good/great/cp。
                    // 保留原 stfld（分支目标依然有效），在它后面插：
                    //   ldarg.0; ldstr "JudgeResult"; call ApplyMineJudgePostWrite(object, string)
                    // stfld 后栈为空，ldarg.0+ldstr+call 不破坏栈形，且所有路径必然经过。
                    yield return inst;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldstr, GetJudgeResultFieldName(inst.operand));
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(MineJudgeInversion), nameof(ApplyMineJudgePostWrite)));
                    continue;
                }

                yield return inst;
            }
        }

        /// <summary>后置反转：读取刚写入的判定值 → ApplyMineJudgeTiming 决定反转结果 → 写回。
        /// 非地雷 / ISelfJudgingMineNote 返回原值不写（自反转类自己负责）。</summary>
        public static void ApplyMineJudgePostWrite(object note, string fieldName)
        {
            try
            {
                if (note == null || fieldName == null) return;
                var traverse = Traverse.Create(note);
                var timing = traverse.Field(fieldName).GetValue<NoteJudge.ETiming>();
                var result = ApplyMineJudgeTiming(timing, note);
                if (result != timing)
                {
                    traverse.Field(fieldName).SetValue(result);
                }
            }
            catch
            {
            }
        }

        private static string GetJudgeResultFieldName(object operand)
        {
            if (operand is FieldInfo field)
            {
                return field.Name;
            }

            var nameProp = operand?.GetType().GetProperty("Name");
            return nameProp?.GetValue(operand) as string;
        }
    }

    /// <summary>判断 stfld 的 operand 是否是判定字段（Harmony 可能给 FieldInfo 或 Mono.Cecil FieldReference）。</summary>
    private static bool IsJudgeResultField(object operand)
    {
        if (operand is FieldInfo field)
        {
            return field.Name == "JudgeResult" || field.Name == "JudgeHeadResult";
        }

        // Mono.Cecil FieldReference（Harmony 的 CodeInstruction 可能持有 Cecil 引用）。
        var nameProp = operand?.GetType().GetProperty("Name");
        var name = nameProp?.GetValue(operand) as string;
        return name == "JudgeResult" || name == "JudgeHeadResult";
    }

    /// <summary>NoteCheck 里"滑完全部轨道但略迟"宽限写入的替换：
    /// 原版把 TooLate 升成 LateGood(13)——地雷划完 = Miss，宽限写入的地雷结果保持 Miss(TooLate)，
    /// 非地雷保持 13。grace 只会在"划完且原判定 TooLate"时触发，与新规则一致。</summary>
    public static void ApplyMineSlideGraceJudge(SlideRoot slide)
    {
        // Mine* 类按类兜底（历史 bug #22），不依赖 behaviour。
        var isMine = slide is MineSlideRoot or MineSlideFan ||
                     slide?.GetComponent<MineNoteBehaviour>()?.IsMine == true;
        var timing = isMine ? NoteJudge.ETiming.TooLate : (NoteJudge.ETiming)13;
        Traverse.Create(slide).Field("JudgeResult").SetValue(timing);
    }

    [HarmonyPatch]
    public static class SlideGraceJudgeTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(SlideRoot), "NoteCheck"),
                AccessTools.Method(typeof(SlideFan), "NoteCheck"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var inst = list[i];
                // 原版"滑完全部轨道但最后一下略迟 → LateGood(13)"的宽限写入，
                // 序列是 ldarg.0; ldc.i4.s 13; stfld JudgeResult。
                // 把 ldc+stfld 换成 call ApplyMineSlideGraceJudge（前面的 ldarg.0 保留作 this）。
                if (i > 0 && list[i - 1].opcode == OpCodes.Ldarg_0 &&
                    (inst.opcode == OpCodes.Ldc_I4_S || inst.opcode == OpCodes.Ldc_I4) &&
                    Convert.ToInt32(inst.operand) == 13 &&
                    i + 1 < list.Count && list[i + 1].opcode == OpCodes.Stfld &&
                    IsJudgeResultField(list[i + 1].operand))
                {
                    var call = new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(ApplyMineSlideGraceJudge)));
                    // 被替换指令可能带分支标签/异常块，必须转移到 call 上（历史 bug #17/#27）。
                    call.labels.AddRange(inst.labels);
                    call.blocks.AddRange(inst.blocks);
                    if (list[i + 1].labels.Count > 0) call.labels.AddRange(list[i + 1].labels);
                    if (list[i + 1].blocks.Count > 0) call.blocks.AddRange(list[i + 1].blocks);
                    yield return call;
                    i++; // 跳过紧随其后的 stfld
                    continue;
                }

                yield return inst;
            }
        }
    }

    /// <summary>slide 收尾等待条件（原版：lastWaitTime <= 0 || JudgeResult == TooLate(14)）的地雷扩展：
    /// 地雷 slide 没划完时 JudgeToolate 的反转结果是 Critical(7)，原版条件不认 → 收尾会多等
    /// lastWaitTime 耗尽才显示 CP（"星星滑到底部要过一会才判 CP"）。补上 mine && Critical → 立即收尾。</summary>
    public static bool IsMineSlideEndWaitDone(SlideRoot slide)
    {
        try
        {
            if (slide == null) return true;
            var wait = Traverse.Create(slide).Field("lastWaitTime").GetValue<float>();
            if (wait <= 0f) return true;
            var judge = slide.GetJudgeResult();
            if (judge == NoteJudge.ETiming.TooLate) return true;
            if (judge == NoteJudge.ETiming.Critical && slide is (MineSlideRoot or MineSlideFan)) return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    [HarmonyPatch]
    public static class SlideEndWaitTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(SlideRoot), "NoteCheck"),
                AccessTools.Method(typeof(SlideFan), "NoteCheck"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var inst = list[i];
                // 原版收尾等待条件的后半段：call GetJudgeResult; ldc.i4.s 14; ceq
                // → ldarg.0; call IsMineSlideEndWaitDone(object)
                if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) &&
                    GetMethodOperandName(inst.operand) == "GetJudgeResult" &&
                    i + 2 < list.Count &&
                    (list[i + 1].opcode == OpCodes.Ldc_I4_S || list[i + 1].opcode == OpCodes.Ldc_I4) &&
                    Convert.ToInt32(list[i + 1].operand) == 14 &&
                    (list[i + 2].opcode == OpCodes.Ceq ||
                     ((list[i + 2].opcode == OpCodes.Bne_Un || list[i + 2].opcode == OpCodes.Bne_Un_S) &&
                      i >= 4 && list[i - 4].opcode == OpCodes.Ldfld &&
                      GetMethodOperandName(list[i - 4].operand) == "lastWaitTime")))
                {
                    var call = new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(IsMineSlideEndWaitDone)));
                    // 被替换指令可能带分支标签/异常块，必须转移（历史 bug #17/#27）。
                    call.labels.AddRange(inst.labels);
                    call.blocks.AddRange(inst.blocks);
                    if (list[i + 1].labels.Count > 0) call.labels.AddRange(list[i + 1].labels);
                    if (list[i + 1].blocks.Count > 0) call.blocks.AddRange(list[i + 1].blocks);
                    if (list[i + 2].opcode == OpCodes.Ceq)
                    {
                        if (list[i + 2].labels.Count > 0) call.labels.AddRange(list[i + 2].labels);
                        if (list[i + 2].blocks.Count > 0) call.blocks.AddRange(list[i + 2].blocks);
                    }
                    // The preceding ldarg.0 already supplies the slide. The
                    // 1.70 build branches directly rather than emitting ceq.
                    yield return call;
                    if (list[i + 2].opcode != OpCodes.Ceq)
                        yield return new CodeInstruction(list[i + 2]) { opcode = OpCodes.Brfalse };
                    i += 2; // 跳过 ldc + ceq
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
}
