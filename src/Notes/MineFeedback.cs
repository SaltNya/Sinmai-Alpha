using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Shared pools (notably MineTouchHoldC / BRTHO) use marker state; dedicated Mine classes
    // remain Mine even if an older registration path attached an incomplete marker.
    internal static bool ShouldShowMineFeedback(Component owner)
    {
        if (ShowMineHitFeedback || owner == null) return true;
        return !IsMineNoteOwner(owner);
    }

    internal static bool IsMineNoteOwner(Component owner)
    {
        if (owner == null) return false;
        var marker = owner.GetComponent<MineNoteBehaviour>();
        // ISelfJudgingMineNote is also used by ordinary TouchBreak classes to bypass inversion.
        var isMine = owner is MineTapNote || owner is MineStarNote
            || owner is MineBreakNote || owner is MineBreakStarNote
            || owner is MineTouchNoteB || owner is MineTouchNoteC
            || owner is MineHoldNote || owner is MineBreakHoldNote
            || owner is MineTouchStarNoteB || owner is MineTouchStarNoteC
            || owner is MineSlideRoot || owner is MineSlideFan
            || marker != null && marker.IsMine;
        return isMine;
    }

    [HarmonyPatch]
    public static class MineFeedbackPatch
    {
        internal static bool IsFeedbackCall(MethodInfo method)
        {
            if (method == null || method.IsStatic || method.ReturnType != typeof(void)) return false;
            return (method.DeclaringType == typeof(JudgeGrade) || method.DeclaringType == typeof(SlideJudge))
                    && method.Name.StartsWith("Initialize", StringComparison.Ordinal)
                || method.DeclaringType == typeof(TouchEffect)
                    && (method.Name.StartsWith("Initialize", StringComparison.Ordinal) || method.Name == "FinishHold");
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            // Discover actual callers in this game version, including hold head/body/tail paths.
            // Only native note owners are included; shared effect components remain unpatched.
            return typeof(NoteBase).Assembly.GetTypes()
                .Where(t => typeof(NoteBase).IsAssignableFrom(t) || typeof(SlideRoot).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => !m.IsAbstract && m.GetMethodBody() != null
                    && PatchProcessor.GetOriginalInstructions(m).Any(i =>
                        (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && IsFeedbackCall(i.operand as MethodInfo)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if ((instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) || !IsFeedbackCall(method))
                {
                    yield return instruction;
                    continue;
                }
                // Receiver and arguments are already on the stack. Preserve their evaluation,
                // then skip only the visual call, never the surrounding judgment/score/recycle.
                var show = generator.DefineLabel();
                var done = generator.DefineLabel();
                var owner = new CodeInstruction(OpCodes.Ldarg_0);
                owner.labels.AddRange(instruction.labels);
                owner.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return owner;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CustomNoteTypes), nameof(ShouldShowMineFeedback)));
                yield return new CodeInstruction(OpCodes.Brtrue, show);
                foreach (var parameter in method.GetParameters()) yield return new CodeInstruction(OpCodes.Pop);
                if (method.DeclaringType == typeof(TouchEffect) && method.Name == "FinishHold")
                    yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(TouchEffect), "StopHoldPlay"));
                else
                    yield return new CodeInstruction(OpCodes.Pop);
                yield return new CodeInstruction(OpCodes.Br, done);
                instruction.labels.Add(show);
                yield return instruction;
                var end = new CodeInstruction(OpCodes.Nop);
                end.labels.Add(done);
                yield return end;
            }
        }
    }
}
