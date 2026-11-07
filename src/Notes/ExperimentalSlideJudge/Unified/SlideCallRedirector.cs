using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;

namespace SinmaiAlpha.Notes;

/// <summary>Carry the actual NoteData owner through calls inside child loops.</summary>
internal static class SlideCallRedirector
{
    private static void MoveEdges(CodeInstruction original, CodeInstruction first, CodeInstruction last)
    {
        first.labels.AddRange(original.labels);
        foreach (var block in original.blocks)
            (block.blockType == ExceptionBlockType.EndExceptionBlock ? last : first).blocks.Add(block);
    }
    private static string Name(object value) => value is MemberInfo member ? member.Name
        : value is Mono.Cecil.MemberReference cecil ? cecil.Name : null;

    private static string DeclaringTypeName(object value) => value is MemberInfo member ? member.DeclaringType?.Name
        : value is Mono.Cecil.MemberReference cecil ? cecil.DeclaringType?.Name : null;

    public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator, MethodInfo path, MethodInfo areas, MethodInfo length)
    {
        var owner = generator.DeclareLocal(typeof(NoteData));
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Stloc, owner);
        foreach (var original in instructions)
        {
            // Dup consumes no owner: preserve the original field access stack.
            // In foreach bodies this is the child local, not Initialize's arg1.
            if (original.opcode == OpCodes.Ldfld && DeclaringTypeName(original.operand) == nameof(NoteData) &&
                (Name(original.operand) == "slideData" || Name(original.operand) == "startButtonPos"))
            {
                var dup = new CodeInstruction(OpCodes.Dup);
                var field = new CodeInstruction(original.opcode, original.operand);
                MoveEdges(original, dup, field);
                yield return dup;
                yield return new CodeInstruction(OpCodes.Stloc, owner);
                yield return field;
                continue;
            }
            var isCall = original.opcode == OpCodes.Call || original.opcode == OpCodes.Callvirt;
            MethodInfo redirect = null;
            if (isCall && DeclaringTypeName(original.operand) == nameof(SlideManager))
                redirect = Name(original.operand) switch
                {
                    "GetSlidePath" => path,
                    "GetSlideHitArea" => areas,
                    "GetSlideLength" => length,
                    _ => null
                };
            if (redirect == null) { yield return original; continue; }
            var load = new CodeInstruction(OpCodes.Ldloc, owner);
            var call = new CodeInstruction(OpCodes.Call, redirect);
            MoveEdges(original, load, call);
            yield return load;
            yield return call;
        }
    }
}
