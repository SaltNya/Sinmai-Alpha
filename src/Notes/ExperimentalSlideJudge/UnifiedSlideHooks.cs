using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using Monitor;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

// These are the earlier direct edits to CustomNoteTypes.21.SlidePaths, now
// installed only by ExperimentalSlideJudgment. The restored legacy transpiler
// and arrow prefix remain byte-for-byte unchanged and run normally when off.
internal static class UnifiedSlideHooks
{
    [HarmonyPatch(typeof(CustomNoteTypes.SlideNoteDataHack), nameof(CustomNoteTypes.SlideNoteDataHack.Transpiler))]
    internal static class LegacyTranspilerBypass
    {
        [HarmonyPrefix]
        internal static bool Prefix(IEnumerable<CodeInstruction> instructions, ref IEnumerable<CodeInstruction> __result)
        {
            // The new transpiler below already handles every original call,
            // carrying the actual child NoteData. Do not inject arg1 again.
            __result = instructions;
            return false;
        }
    }

    [HarmonyPatch(typeof(CustomNoteTypes), nameof(CustomNoteTypes.SlideArrowNumCustomPrefix))]
    internal static class ArrowPrefixBridge
    {
        [HarmonyPrefix]
        internal static bool Prefix(NoteData __1, ref bool __result)
        {
            if (__1 is not CustomSlideNoteData || __1.child == null || __1.child.Count == 0) return true;
            // This patches the legacy PREFIX method: true is its return value
            // (allow native counting); false here skips only that old prefix.
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class CorrectSlideRedirects
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SlideRoot), "Initialize");
            yield return AccessTools.Method(typeof(SlideFan), "Initialize");
            yield return AccessTools.Method(typeof(SlideRoot), "GetSlideArrowNum", new[] { typeof(NoteData) });
            yield return AccessTools.Method(typeof(SlideFan), "GetSlideArrowNum", new[] { typeof(NoteData) });
            yield return AccessTools.Method(typeof(StarNote), "Initialize");
            yield return AccessTools.Method(typeof(BreakStarNote), "Initialize");
        }

        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => SlideCallRedirector.Rewrite(instructions, generator,
                AccessTools.Method(typeof(CustomNoteTypes), nameof(CustomNoteTypes.GetSlidePathRedirect)),
                AccessTools.Method(typeof(CustomNoteTypes), nameof(CustomNoteTypes.GetSlideHitAreaRedirect)),
                AccessTools.Method(typeof(CustomNoteTypes), nameof(CustomNoteTypes.GetSlideLengthRedirect)));
    }
}
