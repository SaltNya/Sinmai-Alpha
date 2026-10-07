using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;
namespace SinmaiAlpha.Notes;
internal static class MineAudioRouting
{
    public static void ReserveAnswerSe(GameSingleCueCtrl native, int index, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveAnswerSe(index);
        else native.ReserveAnswerSe(index);
    }
    public static void ReserveTouchJudgeSe(GameSingleCueCtrl native, int index, NoteJudge.JudgeBox judge, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveTouchJudgeSe(index, judge);
        else native.ReserveTouchJudgeSe(index, judge);
    }
    public static void ReserveTapJudgeSe(GameSingleCueCtrl native, int index, NoteJudge.JudgeBox judge, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveTapJudgeSe(index, judge);
        else native.ReserveTapJudgeSe(index, judge);
    }
    public static void ReserveBreakJudgeSe(GameSingleCueCtrl native, int index, NoteJudge.JudgeBox judge, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveBreakJudgeSe(index, judge);
        else native.ReserveBreakJudgeSe(index, judge);
    }
    public static void ReserveSlideTouchSe(GameSingleCueCtrl native, int index, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveSlideTouchSe(index);
        else native.ReserveSlideTouchSe(index);
    }
    public static void ReserveBreakSlideTouchSe(GameSingleCueCtrl native, int index, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveBreakSlideTouchSe(index);
        else native.ReserveBreakSlideTouchSe(index);
    }
    public static void ReserveBreakSlideSe(GameSingleCueCtrl native, int index, NoteJudge.JudgeBox judge, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveBreakSlideSe(index, judge);
        else native.ReserveBreakSlideSe(index, judge);
    }
    public static void ReserveExSe(GameSingleCueCtrl native, int index, NoteJudge.JudgeBox judge, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveExSe(index, judge);
        else native.ReserveExSe(index, judge);
    }
    public static void ReserveCenterEffectSe(GameSingleCueCtrl native, int index, NoteJudge.JudgeBox judge, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveCenterEffectSe(index, judge);
        else native.ReserveCenterEffectSe(index, judge);
    }
    public static void ReserveTouchHoldLoopSe(GameSingleCueCtrl native, int index, NoteJudge.JudgeBox judge, bool loopDisable, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.Cues.ReserveTouchHoldLoopSe(index, judge, loopDisable);
        else native.ReserveTouchHoldLoopSe(index, judge, loopDisable);
    }
    public static void StopGameSingleSe(int target, SoundManager.PlayerID player, Component owner)
    {
        if (CustomNoteTypes.IsMineNoteOwner(owner)) MineAudio.StopGameSingleSe(target, player);
        else SoundManager.StopGameSingleSe(target, player);
    }
    internal static MethodInfo Replacement(MethodInfo method)
    {
        if (method == null) return null;
        if (method.DeclaringType == typeof(GameSingleCueCtrl) && method.Name.StartsWith("Reserve", StringComparison.Ordinal)
            || method.DeclaringType == typeof(SoundManager) && method.Name == "StopGameSingleSe")
            return AccessTools.Method(typeof(MineAudioRouting), method.Name);
        return null;
    }
}
public partial class CustomNoteTypes
{
    public static void ReserveMineAwareAnswer(GameSingleCueCtrl native, int index, NoteData note)
    {
        if (IsFakeNote(note)) return;
        if (GetNoteKind(note) is CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak or CustomNoteKind.MineTouchStar)
            MineAudio.Cues.ReserveAnswerSe(index);
        else native.ReserveAnswerSe(index);
    }

    [HarmonyTranspiler, HarmonyPatch(typeof(GameCtrl), "UpdateCtrl")]
    public static IEnumerable<CodeInstruction> MineAnswerRouting(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var reserve = AccessTools.Method(typeof(GameSingleCueCtrl), "ReserveAnswerSe");
        for (var i = 0; i < code.Count; i++)
        {
            var instruction = code[i];
            if (!instruction.Calls(reserve)) { yield return instruction; continue; }
            // UpdateCtrl has several NoteData locals. Use the owner of the head/tail
            // played flag set immediately after this reservation, not an arbitrary local.
            var flag = code.FindIndex(i + 1, Math.Min(8, code.Count - i - 1), x =>
                x.opcode == OpCodes.Stfld && x.operand is FieldInfo f && f.DeclaringType == typeof(NoteData)
                && (f.Name == "playAnsSoundHead" || f.Name == "playAnsSoundTail"));
            if (flag < i + 3) throw new InvalidOperationException("Cannot locate the Answer note owner.");
            var load = code[flag - 2];
            if (!load.IsLdloc()) throw new InvalidOperationException("Unexpected Answer note local load.");
            var note = new CodeInstruction(load.opcode, load.operand);
            note.labels.AddRange(instruction.labels);
            note.blocks.AddRange(instruction.blocks);
            yield return note;
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CustomNoteTypes), nameof(ReserveMineAwareAnswer)));
        }
    }

    [HarmonyPatch]
    public static class MineAudioRoutingPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(NoteBase).Assembly.GetTypes()
            .Where(t => typeof(NoteBase).IsAssignableFrom(t) || typeof(SlideRoot).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsAbstract && m.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(m).Any(i =>
                (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && MineAudioRouting.Replacement(i.operand as MethodInfo) != null));
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                var replacement = MineAudioRouting.Replacement(instruction.operand as MethodInfo);
                if ((instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) || replacement == null)
                { yield return instruction; continue; }
                var owner = new CodeInstruction(OpCodes.Ldarg_0);
                owner.labels.AddRange(instruction.labels);
                owner.blocks.AddRange(instruction.blocks);
                yield return owner;
                yield return new CodeInstruction(OpCodes.Call, replacement);
            }
        }
    }
}
