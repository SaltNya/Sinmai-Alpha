using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Monitor;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Keep this extra flag with its reader note and pooled owner. Neither note
    // indices nor the most recently selected chart can identify it safely.
    private static class TouchExState
    {
        internal sealed class Flag { }
        internal static readonly Flag Value = new();
        internal static readonly ConditionalWeakTable<NoteData, Flag> Notes = new();
        internal static readonly ConditionalWeakTable<TouchNoteB, Flag> Owners = new();
    }

    public struct TouchExReadState
    {
        public int MarkerIndex, NoteCount;
        public bool Marked;
    }

    [HarmonyPatch(typeof(NotesReader), "loadNote")]
    public static class TouchExReadPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First + 30)]
        public static void Prefix(MA2Record rec, NotesData ____note, out TouchExReadState __state)
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) { __state = default; return; }

            __state = new TouchExReadState { MarkerIndex = -1, NoteCount = ____note?._noteData.Count ?? 0 };
            if (rec?._str == null || rec._str.Count < 7 || !rec._str[0].EndsWith("TTP", StringComparison.Ordinal)) return;
            for (var i = 7; i < rec._str.Count; i++)
            {
                if (rec._str[i] != "XT1") continue;
                __state.MarkerIndex = i; __state.Marked = true;
                rec._str.RemoveAt(i);
                break;
            }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last - 30)]
        public static void Postfix(MA2Record rec, NotesData ____note, bool __result, TouchExReadState __state)
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

            if (!__state.Marked) return;
            rec._str.Insert(Math.Min(__state.MarkerIndex, rec._str.Count), "XT1");
            if (!__result || ____note?._noteData == null) return;
            for (var i = __state.NoteCount; i < ____note._noteData.Count; i++)
            {
                var note = ____note._noteData[i];
                TouchExState.Notes.Remove(note);
                TouchExState.Notes.Add(note, TouchExState.Value);
            }
        }
    }

    [HarmonyPatch(typeof(TouchNoteB), "Initialize")]
    public static class TouchExInitializePatch
    {
        [HarmonyPostfix]
        public static void Postfix(TouchNoteB __instance, NoteData note)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            TouchExState.Owners.Remove(__instance);
            if (note != null && TouchExState.Notes.TryGetValue(note, out _))
                TouchExState.Owners.Add(__instance, TouchExState.Value);
        }
    }

    [HarmonyPatch(typeof(TouchNoteB), "Judge")]
    public static class TouchExJudgePatch
    {
        // EX only upgrades a hit already accepted by the native timing window.
        // Empty input, timeout, sensor consumption and scoring remain native.
        public static NoteJudge.ETiming Convert(NoteJudge.ETiming timing, TouchNoteB owner)
        {
            if (ReferenceEquals(owner, null) || !TouchExState.Owners.TryGetValue(owner, out _)) return timing;
            return (int)timing > 0 && (int)timing < (int)NoteJudge.ETiming.TooLate
                ? NoteJudge.ETiming.Critical : timing;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var timingMethod = AccessTools.Method(typeof(NoteJudge), "GetJudgeTiming");
            var convert = AccessTools.Method(typeof(TouchExJudgePatch), nameof(Convert));
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!instruction.Calls(timingMethod)) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, convert);
            }
        }
    }
}
