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

// 自定义滑条路径、判定区和轨道长度接入。
public partial class CustomNoteTypes
{

    private static bool IsSlideType(NotesTypeID.Def type)
    {
        return type is NotesTypeID.Def.Slide or NotesTypeID.Def.BreakSlide or
            NotesTypeID.Def.ExSlide or NotesTypeID.Def.ExBreakSlide or NotesTypeID.Def.ConnectSlide;
    }

    private static void TryApplySpeedToNoteObject(object instance, NoteData note)
    {
        var component = instance as Component;
        if (component == null) return;

        // 去重：同一个对象同一 note 只处理一次。
        if (HandledInitializations.TryGetValue(component, out var handledIndex) && handledIndex == note.indexNote)
        {
            return;
        }

        HandledInitializations[component] = note.indexNote;

        NoteKinds.TryGetValue(note.indexNote, out var kind);

        if (kind != CustomNoteKind.None)
        {
            var isMine = kind is CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak;
            var isTouchBreak = kind is CustomNoteKind.TouchBreak or CustomNoteKind.MineTouchBreak;

            var go = component.gameObject;
            // SlideRoot 内部实例化的星（StarNote/BreakStarNote 用 slide 的 NoteData 初始化）
            // 是 slide 的视觉星，不是独立地雷键：不贴地雷贴图、不挂 behaviour、判定保持原版。
            var isSlideStar = instance is StarNote or BreakStarNote && IsSlideType(note.type.getEnum());
            // ISelfJudgingMineNote 独立类贴图/判定自己负责，不需要 MineNoteBehaviour；
            // Hold/Slide 等非自反转类仍需 behaviour（transpiler 靠它识别地雷）。
            if (!isSlideStar && !(instance is ISelfJudgingMineNote))
            {
                var behaviour = go.GetComponent<MineNoteBehaviour>();
                if (behaviour == null) behaviour = go.AddComponent<MineNoteBehaviour>();
                // SlideRoot/SlideFan 的 ApplyMineVisual 会遍历子物体误伤内部星（_starNote），
                // 所以只挂判定标记不贴图；轨道贴图由独立地雷箭头池负责。
                var applyTextures = !(instance is MineSlideRoot or SlideFan);
                behaviour.Setup(note.indexNote, isMine, isTouchBreak, applyTextures);
            }
        }

        // 2026-08-23 SV/HS 精简：速度应用部分移除——内嵌 x{m} 倍率由 GetNoteSpeed/
        // GetTouchSpeed postfix 统一缩放 DefaultMsec；本函数只负责地雷 behaviour 挂载。
    }

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

    /// <summary>
    /// GameCtrl.RegistNote 在 SlideRoot.Initialize 之前调用 GetSlideArrowNum(NoteData) 计算箭头排序号。
    /// 该方法内部的 GetSlidePath 调用没有被 SlideNoteDataHack 重定向（注入曾致内置 slide 崩溃被禁用），
    /// 自定义路径的 slideData.type（GetEndType 结果 1/2/3）× start/end 组合在原生查表里越界 → 进曲崩溃。
    /// 对 CustomSlideNoteData 直接按自定义路径复刻原版箭头数算法短路。
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SlideRoot), "GetSlideArrowNum", new[] { typeof(NoteData) })]
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

    /// <summary>
    /// UpdateAlpha 的循环边界是 _dispLaneNum（Initialize 里按路径点算），
    /// 但 _spriteRenders/_breakSpriteRenders 由 GameCtrl.RegistNote 按 GetSlideArrowNum 分配。
    /// 两者理论上一致；若不一致（分配不足/池占用），UpdateAlpha 越界崩溃。
    /// 这里做 clamp 兜底 + 打印一次差异日志定位根因。
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SlideRoot), "UpdateAlpha")]
    public static void UpdateAlphaSafePrefix(SlideRoot __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var t = Traverse.Create(__instance);
        var noteData = t.Field("NoteData").GetValue<NoteData>();
        if (noteData == null) return;
        var dispLane = t.Field("_dispLaneNum").GetValue<int>();
        var spriteRenders = (List<SpriteRenderer>)t.Field("_spriteRenders").GetValue();
        var breakRenders = (List<BreakSlide>)t.Field("_breakSpriteRenders").GetValue();
        var isBreak = t.Field("BreakFlag").GetValue<bool>();
        var max = isBreak ? breakRenders.Count : spriteRenders.Count;
        if (dispLane > max)
        {
            // 原版 GetSlideArrowNum（分配箭头数）与 Initialize 的 _dispLaneNum（按路径点算）
            // 理论上一致；不一致说明路径/段结构异常（如长链同屏过多耗尽箭头池），clamp 防崩。
            t.Field("_dispLaneNum").SetValue(max);
        }
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

    public static float GetSlideLengthRedirect(SlideManager instance, SlideType slideType,
        int start, int end, NoteData noteData)
    {
        if (noteData is CustomSlideNoteData data)
        {
            return data.SlidePathLength;
        }
        try
        {
            // 预检：原生 _slidePathList 查表要求 slideType∈[0,13] 且 start/end∈[0,7]。
            // 越界直接走日志+兜底，避免 AOOE 在 catch 里被二次 NRE 掩蔽。
            if ((int)slideType < 0 || (int)slideType > 13 || start < 0 || start > 7 || end < 0 || end > 7)
            {
                LogGetSlideLengthOOB(slideType, start, end, noteData, "range");
                return 1f;
            }
            return instance.GetSlideLength(slideType, start, end);
        }
        catch (Exception ex)
        {
            // NotesTypeID 是 class 且重载了 ==（op_Equality 会解引用两个操作数），
            // 判空必须用 ReferenceEquals，否则 catch 里 nt == null 二次 NRE，
            // 把真实的 AOOE 信息掩蔽成表面上的 NullReferenceException。
            LogGetSlideLengthOOB(slideType, start, end, noteData, ex.GetType().Name + ":" + ex.Message);
            return 1f;
        }
    }

    private static void LogGetSlideLengthOOB(SlideType slideType, int start, int end, NoteData noteData, string why)
    {
        try
        {
            var nt = ReferenceEquals(noteData, null) ? null : noteData.type;
            var noteDesc = ReferenceEquals(nt, null)
                ? "null"
                : $"{nt.getEnum()} isTouch={nt.isTouch()} isStar={nt.isStar()} isSlide={nt.isSlide()}";
            var extra = "";
            try
            {
                if (!ReferenceEquals(noteData, null) && noteData.slideData != null)
                {
                    extra = $" slideType={noteData.slideData.type} targetNote={noteData.slideData.targetNote}";
                }
            }
            catch (Exception) { }
            MelonLogger.Error(
                $"[CustomNoteType] GetSlideLengthRedirect OOB({why}): slideType={(int)slideType}({slideType}) " +
                $"start={start} end={end} noteType={noteDesc} " +
                $"index={noteData?.index} indexNote={noteData?.indexNote} indexSlide={noteData?.indexSlide} " +
                $"startButtonPos={noteData?.startButtonPos}{extra}");
        }
        catch (Exception logEx)
        {
            MelonLogger.Error($"[CustomNoteType] GetSlideLengthRedirect LOGGING FAILED: {logEx}");
        }
    }
}
