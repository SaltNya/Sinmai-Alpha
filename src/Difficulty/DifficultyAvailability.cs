using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using ChartNotes = Manager.MaiStudio.Notes;
using Monitor;
using Monitor.MusicSelect.ChainList;
using Process;
using Process.SubSequence;

namespace SinmaiAlpha.Difficulty;

// The mask describes charts, never the numeric distance between difficulty IDs.
public static class DifficultyAvailability
{
    public const int Count = 5;
    public static int Mask(IList<ChartNotes> notes)
    {
        int mask = 0;
        if (notes != null)
            for (int i = 0; i < Math.Min(notes.Count, DifficultyAvailability.Count); i++)
                if (notes[i]?.isEnable == true) mask |= 1 << i;
        return mask;
    }
    public static int Resolve(int mask, int requested)
    {
        if (requested >= 0 && requested < DifficultyAvailability.Count && (mask & (1 << requested)) != 0) return requested;
        for (int i = Math.Min(requested, DifficultyAvailability.Count - 1); i >= 0; i--)
            if ((mask & (1 << i)) != 0) return i;
        return Next(mask, -1, 1);
    }
    public static int Next(int mask, int current, int direction)
    {
        if (direction == 0) return -1;
        direction = Math.Sign(direction);
        int start = direction > 0 ? Math.Max(0, current + 1) : Math.Min(DifficultyAvailability.Count - 1, current - 1);
        for (int i = start; i >= 0 && i < DifficultyAvailability.Count; i += direction)
            if ((mask & (1 << i)) != 0) return i;
        return -1;
    }
}

public static partial class ExtraDifficulty
{
    private static bool IsOrdinarySong(int id) => id > 0 && id < 100000 && (id < 20000 || id >= 30000);
    private static IMusicSelectProcessProcessing Processing(object sequence) =>
        Field<IMusicSelectProcessProcessing>(sequence, "ProcessProcessing");
    private static int Player(object sequence) => Field<int>(sequence, "PlayerIndex");

    private static int SongMask(MusicSelectProcess.MusicSelectData row) =>
        row?.MusicData != null && IsOrdinarySong(row.MusicData.name.id)
            ? DifficultyAvailability.Mask(row.ScoreData ?? (IList<ChartNotes>)row.MusicData.notesData) : -1;

    private static int SequenceNext(int current, int direction, object sequence)
    {
        int mask = SongMask(Processing(sequence).GetMusic(0));
        if (mask < 0) return current + direction;
        int next = DifficultyAvailability.Next(mask, DifficultyAvailability.Resolve(mask, current), direction);
        // Preserve native upper/lower-bound checks and all ghost/ranking side effects.
        return next >= 0 ? next : direction > 0 ? DifficultyAvailability.Count : -1;
    }

    [HarmonyPatch]
    public static class AvailableStepPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(MusicSelectSequence), typeof(DifficultySelectSequence) })
            foreach (var name in new[] { "AddDifficulty", "SubDifficulty" })
                yield return AccessTools.Method(type, name);
        }
        [HarmonyTranspiler] public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            int changed = 0;
            for (int i = 0; i < code.Count; i++)
            {
                // Match chart fields/enum-array reads only; leave monitor-loop counters alone.
                if (i > 0 && i + 1 < code.Count && code[i].LoadsConstant(1) &&
                    (code[i + 1].opcode == OpCodes.Add || code[i + 1].opcode == OpCodes.Sub) &&
                    (code[i - 1].opcode == OpCodes.Ldelem_I4 || code[i - 1].opcode == OpCodes.Ldind_I4 ||
                     code[i - 1].operand is FieldInfo field && (field.Name == "Difficulty" || field.Name == "difficultyIndex")))
                {
                    int direction = code[i + 1].opcode == OpCodes.Add ? 1 : -1;
                    code[i].opcode = direction > 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_M1;
                    code[i].operand = null;
                    yield return code[i];
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    code[++i].opcode = OpCodes.Call;
                    code[i].operand = AccessTools.Method(typeof(ExtraDifficulty), nameof(SequenceNext));
                    changed++;
                }
                yield return code[i];
            }
            if (changed < 4) throw new InvalidOperationException("Unsupported difficulty-step IL: " + __originalMethod);
        }
    }

    [HarmonyPatch]
    public static class AvailableResolvedDifficultyPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MusicSelectProcess), "GetCurrentDifficulty");
            yield return AccessTools.Method(typeof(MusicSelectProcess), "GetDifficulty");
        }
        [HarmonyPostfix] public static void Postfix(MusicSelectProcess __instance, int __0, object[] __args, ref int __result)
        {
            if (drawingSong == 0 && __instance.IsGenreSeq(__0)) return;
            int offset = __args.Length > 1 ? (int)__args[1] : 0;
            int mask = SongMask(__instance.GetMusic(offset));
            int resolved = mask < 0 ? -1 : DifficultyAvailability.Resolve(mask, __result);
            if (resolved >= 0) __result = resolved;
        }
    }

    [HarmonyPatch(typeof(MusicSelectChainList), "SetChainData")]
    public static class AvailableCardDifficultyPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(MusicSelectChainList __instance, int __1, ref int __3)
        {
            var process = Field<IMusicSelectProcess>(__instance, "SelectProcess");
            int mask = SongMask(process.GetMusic(__1));
            int requested = __3 >= 0 ? __3 : process.GetDifficulty(Field<int>(__instance, "MonitorIndex"), __1);
            int resolved = mask < 0 ? -1 : DifficultyAvailability.Resolve(mask, requested);
            if (resolved >= 0) __3 = resolved;
        }
    }
}
