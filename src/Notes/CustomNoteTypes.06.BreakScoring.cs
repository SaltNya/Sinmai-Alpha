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

// Touch 绝赞计分、总分与结算修正。
public partial class CustomNoteTypes
{

    /// <summary>
    /// TouchNoteB.EndNote 里的 SetResult(NoteIndex, EScoreType.Touch, timing) 是 touch 系的唯一统计点。
    /// SetResult 有 isJudged 守卫：第一次调用后 isJudged=true，之后任何 SetResult 都会被忽略
    /// （所以原来加在 SetPlayResult override 里的 SetResult(Break) 无效，必须改这里）。
    /// 把 kind 参数（ldc.i4.4 = Touch）替换成 GetTouchScoreKind(this)：
    /// 绝赞系（BRSTP TouchBreakStar / BRTTP TouchBreak / MBTTP 地雷绝赞）→ Break(3)，
    /// 普通 touch（含 NMSTP 普通 TouchStar）→ Touch(4)——计分类别在 Initialize 已固化。
    /// TouchNoteC 不 override EndNote（继承 TouchNoteB.EndNote），B/C/E/D 全区都覆盖。
    /// 2026-08-22 修复：原实现是 CustomNoteTypes 类上的方法级 [HarmonyPatch]，方法名
    /// TouchNoteBEndNoteScoreTranspiler 不以 Prefix/Postfix/Transpiler 开头 → PatchAll 静默
    /// 跳过（日志 Applying 列表无此项）→ BRTTP 计分/统计一直按普通 Touch。必须用嵌套类 +
    /// 标准 Transpiler 方法名。
    /// </summary>
    [HarmonyPatch(typeof(TouchNoteB), "EndNote")]
    public static class TouchNoteBEndNoteScorePatch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getKind = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetTouchScoreKind));
            var codes = instructions.ToList();
            var replaced = 0;
            for (var i = 0; i < codes.Count; i++)
            {
                if (!IsSetResultCall(codes[i])) continue;
                // SetResult 调用前 6 条指令内找 ldc.i4.4（kind 参数，EScoreType.Touch）
                for (var j = i - 1; j >= 0 && j >= i - 6; j--)
                {
                    if (codes[j].opcode != OpCodes.Ldc_I4_4) continue;
                    codes[j] = new CodeInstruction(OpCodes.Ldarg_0);
                    codes.Insert(j + 1, new CodeInstruction(OpCodes.Call, getKind));
                    i++; // 跳过插入的 call
                    replaced++;
                    break;
                }
            }
            // 2026-08-25 修复：原实现用 CodeInstruction.Calls(MethodInfo)——object.Equals 按引用
            // 比较 operand 与 AccessTools.Method 结果（反汇编得到的 MethodInfo 实例与
            // GetMethod 实例不是同一对象，MethodInfo 不重写 Equals）→ 永远不命中 →
            // BRTTP/BRSTP 计分一直是普通 Touch。必须按名字+声明类型匹配。
            if (replaced == 0)
            {
                MelonLogger.Warning("[CustomNoteType] EndNote transpiler: SetResult call site NOT found; BRTTP/BRSTP scoring stays Touch");
            }
            else
            {
                MelonLogger.Msg($"[CustomNoteType] EndNote transpiler: {replaced} SetResult kind replaced -> GetTouchScoreKind");
            }
            return codes;
        }

        private static bool IsSetResultCall(CodeInstruction code)
        {
            if (code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt) return false;
            // 反射模式 operand=MethodInfo；Cecil 模式 operand=Mono.Cecil.MethodReference。
            if (code.operand is MethodInfo mi)
            {
                return mi.Name == "SetResult" && mi.DeclaringType != null && mi.DeclaringType.Name == "GameScoreList";
            }
            if (code.operand != null)
            {
                var name = code.operand.GetType().GetProperty("Name")?.GetValue(code.operand) as string;
                if (name != "SetResult") return false;
                var decl = code.operand.GetType().GetProperty("DeclaringType")?.GetValue(code.operand);
                return decl?.GetType().GetProperty("Name")?.GetValue(decl) as string == "GameScoreList";
            }
            return false;
        }
    }

    // Keep rendering types intact. Correct both score denominators and category counts
    // using the game's score table, after each fresh calcTotal (which clears _total).
    [HarmonyPatch(typeof(NotesReader), "calcTotal")]
    public static class CalcTotalBreakFixPostfix
    {
        [HarmonyPostfix]
        public static void Postfix(NotesReader __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            var total = __instance.GetTotal();
            foreach (var note in __instance.GetNoteList())
            {
                if (IsFakeNote(note)) continue;
                if (!NoteKinds.TryGetValue(note.indexNote, out var kind) ||
                    (kind != CustomNoteKind.TouchBreak && kind != CustomNoteKind.TouchBreakStar &&
                     kind != CustomNoteKind.MineTouchBreak)) continue;
                if (note.type.isBreak() || note.type.isExBreak()) continue;
                var original = note.type.isHold() ? NoteScore.EScoreType.Hold : NoteScore.EScoreType.Tap;
                total._allPerfectScore += NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, NoteScore.EScoreType.Break)
                    - NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, original);
                total._breakBonusScore += NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, NoteScore.EScoreType.BreakBonus);
                total._totalData[(int)note.type.getEnum()]--;
                total._totalData[(int)(note.type.isHold() ? NotesTypeID.Def.BreakHold : NotesTypeID.Def.Break)]++;
            }
            total._allPerfectPlusScore = total._allPerfectScore + total._breakBonusScore;
        }
    }

    public static NoteScore.EScoreType GetTouchScoreKind(TouchNoteB note)
    {
        NoteScore.EScoreType kind;
        if (note is TouchStarNoteB starB) kind = starB.IsBreakStar ? NoteScore.EScoreType.Break : NoteScore.EScoreType.Touch;
        else if (note is TouchStarNoteC starC) kind = starC.IsBreakStar ? NoteScore.EScoreType.Break : NoteScore.EScoreType.Touch;
        else if (note is TouchBreakNoteB) kind = NoteScore.EScoreType.Break;
        else if (note is TouchBreakNoteC) kind = NoteScore.EScoreType.Break;
        else if (note is MineTouchNoteB mine && mine.IsMineTouchBreak) kind = NoteScore.EScoreType.Break;
        else kind = NoteScore.EScoreType.Touch;

        // 诊断（2026-09）：每种 (运行时类型 → kind) 组合只打印一次，用于确认绝赞 touch 是否真的按 Break 计分。
        var scoreKindKey = note.GetType().Name + " -> " + kind;
        if (ScoreKindLogged.Add(scoreKindKey))
        {
            MelonLogger.Msg($"[CustomNoteType] GetTouchScoreKind {scoreKindKey}");
        }

        return kind;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameScoreList), "SetResult")]
    public static void CustomBreakResultPrefix(GameScoreList __instance, int ____monitorIndex,
        int index, ref NoteScore.EScoreType kind, NoteJudge.ETiming timing)
    {
        if (!(CustomNoteTypes.FeaturesForMonitor(____monitorIndex))) {  return; }

        if (!__instance.IsEnable || !NoteKinds.TryGetValue(index, out var custom) ||
            (custom != CustomNoteKind.TouchBreak && custom != CustomNoteKind.TouchBreakStar &&
             custom != CustomNoteKind.MineTouchBreak)) return;
        var reader = NotesManager.Instance(____monitorIndex).getReader();
        var note = reader.GetNoteList()[index];
        if (note.isJudged || IsFakeNote(note)) return;
        kind = NoteScore.EScoreType.Break;
        // Stock SetResult updates touch chains only for kind=Touch. Preserve that
        // housekeeping while every call site (including forced/skip results) scores Break.
        if (note.type.getEnum() == NotesTypeID.Def.TouchTap && note.indexTouchGroup != -1)
        {
            var chain = reader.GetTouchChainList()[note.indexTouchGroup];
            chain.EndCount++;
            chain.ChainJudge = __instance.IsTrackSkip ? NoteJudge.ETiming.TooLate : timing;
        }
    }
}
