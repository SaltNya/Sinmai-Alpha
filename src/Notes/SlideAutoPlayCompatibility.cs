using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Monitor;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Alpha patches SlideRoot before later mods patch the tiny IsAutoPlay method.
    // An indirect call prevents Mono from inlining its original (manual) result
    // into our patched slide methods. Keep the game's live switch authoritative.
    private static readonly Func<bool> NativeAutoPlay = (Func<bool>)Delegate.CreateDelegate(
        typeof(Func<bool>), AccessTools.Method(typeof(GameManager), "IsAutoPlay"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SlideIsAutoPlay() => NativeAutoPlay();

    [HarmonyPatch]
    public static class MineSlideAutoPlayCompatibility
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(SlideRoot), "NoteCheck");
            yield return AccessTools.DeclaredMethod(typeof(SlideFan), "NoteCheck");
        }

        [HarmonyPrefix]
        public static bool Prefix(SlideRoot __instance)
        {
            if (!FeaturesEnabled(__instance) || __instance is not (MineSlideRoot or MineSlideFan) ||
                !SlideIsAutoPlay() || IsFakeNoteOwner(__instance) || __instance.GetJudgeResult() != NoteJudge.ETiming.End)
                return true;

            // 普通 autoplay 会推进 hitIndex 并划掉地雷，触发命中反转 Miss。
            // 只跳过自动输入；星星移动仍由 Execute 更新，到尾部复用地雷的
            // 未触发 CP、计分与回收。SlideFan 自己重写了 NoteCheck，也需处理。
            MineSlideAutoJudgePostfix(__instance);
            return false;
        }
    }

    [HarmonyPatch]
    public static class SlideAutoPlayCompatibility
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SlideRoot), "NoteCheck");
            yield return AccessTools.Method(typeof(SlideRoot), "Judge");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(GameManager), "IsAutoPlay");
            var indirect = AccessTools.Method(typeof(CustomNoteTypes), nameof(SlideIsAutoPlay));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = indirect; }
                yield return instruction;
            }
        }
    }
}
