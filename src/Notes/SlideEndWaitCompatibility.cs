using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Monitor;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
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
                // Only extend the outer (lastWaitTime <= 0 || grade == TooLate).
                // Debug/recompiled DLLs also emit ceq for the INNER TooLate test.
                // Replacing that inner test turns every expired normal judgment
                // into LateGood, even if it was Critical before the wait elapsed.
                if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) &&
                    GetMethodOperandName(inst.operand) == "GetJudgeResult" &&
                    i >= 4 && list[i - 4].opcode == OpCodes.Ldfld &&
                    GetMethodOperandName(list[i - 4].operand) == "lastWaitTime" &&
                    list[i - 3].opcode == OpCodes.Ldc_R4 &&
                    Convert.ToSingle(list[i - 3].operand) == 0f &&
                    (list[i - 2].opcode == OpCodes.Ble || list[i - 2].opcode == OpCodes.Ble_S) &&
                    list[i - 1].opcode == OpCodes.Ldarg_0 &&
                    i + 2 < list.Count &&
                    (list[i + 1].opcode == OpCodes.Ldc_I4_S || list[i + 1].opcode == OpCodes.Ldc_I4) &&
                    Convert.ToInt32(list[i + 1].operand) == 14 &&
                    (list[i + 2].opcode == OpCodes.Ceq ||
                     list[i + 2].opcode == OpCodes.Bne_Un || list[i + 2].opcode == OpCodes.Bne_Un_S))
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
