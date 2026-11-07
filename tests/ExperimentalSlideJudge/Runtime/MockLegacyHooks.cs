// Exact restored legacy redirect/arrow methods, with minimal game services.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using Monitor;
using MelonLoader;
using UnityEngine;
using SinmaiAlpha.Notes.Libs;
namespace SinmaiAlpha.Notes;
[HarmonyPatch]
public partial class CustomNoteTypes {
    static bool FeaturesEnabled(object owner)=>true;
    [HarmonyPatch]
    public static class SlideNoteDataHack

    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(SlideRoot), "Initialize"),
                AccessTools.Method(typeof(SlideFan), "Initialize"),
                // Temporarily disabled: the IL injection here can crash on built-in slide notes.
                // （待办：排查崩溃原因后恢复，见同目录 TODO.md §2.5）
                // AccessTools.Method(typeof(SlideRoot), "GetSlideArrowNum", [typeof(NoteData)]),
                AccessTools.Method(typeof(StarNote), "Initialize"),
                AccessTools.Method(typeof(BreakStarNote), "Initialize"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var methodGetSlidePathRedirect = AccessTools.Method(typeof(CustomNoteTypes), "GetSlidePathRedirect");
            var methodGetSlideHitAreaRedirect = AccessTools.Method(typeof(CustomNoteTypes), "GetSlideHitAreaRedirect");
            var methodGetSlideLengthRedirect = AccessTools.Method(typeof(CustomNoteTypes), "GetSlideLengthRedirect");

            var newInstList = new List<CodeInstruction>();
            var injected = 0;
            void Redirect(CodeInstruction original, MethodInfo method)
            {
                var note = new CodeInstruction(OpCodes.Ldarg_1);
                note.labels.AddRange(original.labels);
                var call = new CodeInstruction(OpCodes.Call, method);
                call.blocks.AddRange(original.blocks);
                newInstList.Add(note);
                newInstList.Add(call);
                injected++;
            }
            foreach (var inst in instructions)
            {
                if (IsSlideManagerCall(inst, "GetSlidePath"))
                {
                    // 原始 callvirt 调用栈：instance, slideType, start, end, starButton（5 值）。
                    // redirect 是静态方法且比原实例方法多一个 NoteData 参数（6 参数）：
                    // 必须在 call 前补 ldarg.1（三个目标 Initialize 的签名均为 (NoteData)，
                    // arg_1 就是 noteData），否则栈下溢 → Mono 验证器报
                    // InvalidProgramException "call 0x00000095"（wrapper 编译崩溃，
                    // 并连锁 SlideLayerReverse 等同一目标方法的 patch 失败）。
                    // 不能保留原 callvirt：redirect 消费全部参数栈后残留的 callvirt 同样下溢。
                    // 内置 slide 在 redirect 内部走回退分支（instance.GetXxx(...)），无行为变化。
                    Redirect(inst, methodGetSlidePathRedirect);
                }
                else if (IsSlideManagerCall(inst, "GetSlideHitArea"))
                {
                    // GetSlideHitArea(SlideType, int, int) 栈 4 值 + noteData = 5 = redirect 参数数
                    Redirect(inst, methodGetSlideHitAreaRedirect);
                }
                else if (IsSlideManagerCall(inst, "GetSlideLength"))
                {
                    // GetSlideLength(SlideType, int, int) 栈 4 值 + noteData = 5 = redirect 参数数
                    Redirect(inst, methodGetSlideLengthRedirect);
                }
                else
                {
                    newInstList.Add(inst);
                }
            }
            // 2026-08-25 修复：原实现用 inst.Calls(MethodInfo)——object.Equals 按引用比较
            // operand 与 AccessTools.Method 结果（反汇编 MethodInfo 实例与 GetMethod 实例
            // 不是同一对象）→ 永远不命中 → 自定义 slide（NMSSS/BRSSS）的路径/判定区/长度
            // redirect 从未注入，一直静默退化到原生模板近似形状。必须按名字+声明类型匹配。
            if (injected == 0)
            {
                MelonLogger.Warning("[CustomNoteType] SlideNoteDataHack: no SlideManager calls matched; custom slide redirects NOT injected");
            }
            else
            {
                MelonLogger.Msg($"[CustomNoteType] SlideNoteDataHack: {injected} SlideManager calls redirected");
            }
            return newInstList;
        }

        private static bool IsSlideManagerCall(CodeInstruction inst, string methodName)
        {
            if (inst.opcode != OpCodes.Call && inst.opcode != OpCodes.Callvirt) return false;
            // 反射模式 operand=MethodInfo；Cecil 模式 operand=Mono.Cecil.MethodReference。
            if (inst.operand is MethodInfo mi)
            {
                return mi.Name == methodName && mi.DeclaringType != null && mi.DeclaringType.Name == "SlideManager";
            }
            if (inst.operand != null)
            {
                var name = inst.operand.GetType().GetProperty("Name")?.GetValue(inst.operand) as string;
                if (name != methodName) return false;
                var decl = inst.operand.GetType().GetProperty("DeclaringType")?.GetValue(inst.operand);
                return decl?.GetType().GetProperty("Name")?.GetValue(decl) as string == "SlideManager";
            }
            return false;
        }
    }
    public static List<Vector4> GetSlidePathRedirect(SlideManager instance, SlideType slideType, int start, int end,
        int starButton, NoteData noteData)
    {
        if (noteData is CustomSlideNoteData data)
        {
            if (data.IsFan) return data.FanPathLists[starButton][data.FanLane(end)];
            return data.SlidePathList[starButton];
        }
        return instance.GetSlidePath(slideType, start, end, starButton);
    }
    public static List<SlideManager.HitArea> GetSlideHitAreaRedirect(SlideManager instance, SlideType slideType,
        int start, int end, int starButton, NoteData noteData)
    {
        if (noteData is CustomSlideNoteData data)
        {
            if (data.IsFan) return data.FanHitAreaLists[starButton][data.FanLane(end)];
            return data.SlideHitAreasList[starButton];
        }
        return instance.GetSlideHitArea(slideType, start, end, starButton);
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SlideRoot), "GetSlideArrowNum", new[] {typeof(NoteData)})]
    public static bool SlideArrowNumCustomPrefix(SlideRoot __instance, NoteData note, ref int __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        if (note is not CustomSlideNoteData data) return true;
        var path = data.SlidePathList[__instance.ButtonId];
        if (path == null || path.Count < 3)
        {
            __result = 0;
            return false;
        }
        // 复刻原版：倒数第二点 z + 23.556 &lt; 末点 z → Count-2，否则 Count-3
        __result = path[path.Count - 2].z + 23.556f < path[path.Count - 1].z
            ? path.Count - 2
            : path.Count - 3;
        return false;
    }
public static float GetSlideLengthRedirect(SlideManager instance,SlideType slideType,int start,int end,NoteData noteData) => noteData is CustomSlideNoteData custom ? custom.SlidePathLength : instance.GetSlideLength(slideType,start,end);
}
