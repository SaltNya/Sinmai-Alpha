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

// 对象池访问器、池选择和 RegistNote 接入。
public partial class CustomNoteTypes
{

    // 原版 CreateNotePool 会给池对象设置 ParentTransform（EndNote 回收时用它做父级）。
    private static void TrySetParentTransform(Component note, Transform parent)
    {
        var prop = note.GetType().GetProperty("ParentTransform");
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(note, parent);
            return;
        }

        var field = note.GetType().GetField("<ParentTransform>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        field?.SetValue(note, parent);
    }

    // 地雷 Note 取池：当前 RegistNote 需要该字段时返回地雷池/绝赞池，否则返回原版池。
    private static object GetMinePoolList(GameCtrl instance, string fieldName, System.Type elementType)
    {
        var route = PoolRoutes.For(instance);
        if (route != null && route.Fields.Contains(fieldName))
        {
            var owners = route.Kind switch
            {
                CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak => MinePools,
                CustomNoteKind.TouchBreak => TouchBreakPools,
                CustomNoteKind.TouchStar or CustomNoteKind.TouchBreakStar => TouchStarPools,
                CustomNoteKind.MineTouchStar => MineTouchStarPools,
                _ => null
            };
            if (owners != null && owners.TryGetValue(instance, out var pools) && pools.TryGetValue(fieldName, out var pool)) return pool;
        }
        return Traverse.Create(instance).Field(fieldName).GetValue();
    }

    // 注意：transpiler 替换 ldfld 时栈上是 [instance, "字段名"]（ldstr 在 call 之前），
    // 所以这些访问器必须接收 (GameCtrl, string) 两个参数，否则字段名字符串会被当作
    // GameCtrl 弹出导致 InvalidCastException，RegistNote 抛异常、音符全部注册失败。
    private static List<TapNote> GetTapObjectList(GameCtrl instance, string fieldName) => (List<TapNote>)GetMinePoolList(instance, fieldName, typeof(TapNote));
    private static List<HoldNote> GetHoldObjectList(GameCtrl instance, string fieldName) => (List<HoldNote>)GetMinePoolList(instance, fieldName, typeof(HoldNote));
    private static List<BreakHoldNote> GetBreakHoldObjectList(GameCtrl instance, string fieldName) => (List<BreakHoldNote>)GetMinePoolList(instance, fieldName, typeof(BreakHoldNote));
    private static List<StarNote> GetStarObjectList(GameCtrl instance, string fieldName) => (List<StarNote>)GetMinePoolList(instance, fieldName, typeof(StarNote));
    private static List<BreakStarNote> GetBreakStarObjectList(GameCtrl instance, string fieldName) => (List<BreakStarNote>)GetMinePoolList(instance, fieldName, typeof(BreakStarNote));
    private static List<BreakNote> GetBreakObjectList(GameCtrl instance, string fieldName) => (List<BreakNote>)GetMinePoolList(instance, fieldName, typeof(BreakNote));
    private static List<TouchNoteB> GetTouchBObjectList(GameCtrl instance, string fieldName) => (List<TouchNoteB>)GetMinePoolList(instance, fieldName, typeof(TouchNoteB));
    private static List<TouchNoteC> GetTouchCTapObjectList(GameCtrl instance, string fieldName) => (List<TouchNoteC>)GetMinePoolList(instance, fieldName, typeof(TouchNoteC));
    private static List<TouchHoldC> GetTouchBHoldObjectList(GameCtrl instance, string fieldName) => (List<TouchHoldC>)GetMinePoolList(instance, fieldName, typeof(TouchHoldC));
    private static List<TouchHoldC> GetTouchCHoldObjectList(GameCtrl instance, string fieldName) => (List<TouchHoldC>)GetMinePoolList(instance, fieldName, typeof(TouchHoldC));
    private static List<SlideRoot> GetSlideObjectList(GameCtrl instance, string fieldName) => (List<SlideRoot>)GetMinePoolList(instance, fieldName, typeof(SlideRoot));
    private static List<SlideFan> GetFanSlideObjectList(GameCtrl instance, string fieldName) => (List<SlideFan>)GetMinePoolList(instance, fieldName, typeof(SlideFan));
    private static List<SpriteRenderer> GetArrowObjectList(GameCtrl instance, string fieldName) => (List<SpriteRenderer>)GetMinePoolList(instance, fieldName, typeof(SpriteRenderer));
    private static List<BreakSlide> GetBreakArrowObjectList(GameCtrl instance, string fieldName) => (List<BreakSlide>)GetMinePoolList(instance, fieldName, typeof(BreakSlide));

    // note 类型（重定向后的基础类型）-> 该类型注册时读取的池字段。
    private static HashSet<string> GetMineFieldsForNoteType(NotesTypeID.Def type)
    {
        switch (type)
        {
            case NotesTypeID.Def.Tap:
            case NotesTypeID.Def.ExTap:
                return new HashSet<string> { "_tapObjectList" };
            case NotesTypeID.Def.Hold:
            case NotesTypeID.Def.ExHold:
                return new HashSet<string> { "_holdObjectList" };
            case NotesTypeID.Def.BreakHold:
            case NotesTypeID.Def.ExBreakHold:
                return new HashSet<string> { "_breakHoldObjectList" };
            case NotesTypeID.Def.Star:
            case NotesTypeID.Def.ExStar:
                return new HashSet<string> { "_starObjectList" };
            case NotesTypeID.Def.BreakStar:
            case NotesTypeID.Def.ExBreakStar:
                return new HashSet<string> { "_breakStarObjectList" };
            case NotesTypeID.Def.Break:
            case NotesTypeID.Def.ExBreakTap:
                return new HashSet<string> { "_breakObjectList" };
            case NotesTypeID.Def.Slide:
            case NotesTypeID.Def.BreakSlide:
            case NotesTypeID.Def.ExSlide:
            case NotesTypeID.Def.ExBreakSlide:
            case NotesTypeID.Def.ConnectSlide:
                return new HashSet<string> { "_slideObjectList", "_fanSlideObjectList", "_arrowObjectList", "_breakArrowObjectList" };
            case NotesTypeID.Def.TouchTap:
                return new HashSet<string> { "_touchBObjectList", "_touchCTapObjectList" };
            case NotesTypeID.Def.TouchHold:
                return new HashSet<string> { "_touchBHoldObjectList", "_touchCHoldObjectList" };
            default:
                return new HashSet<string>();
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static IEnumerable<CodeInstruction> RegistNoteTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var inst in instructions)
        {
            var fieldName = GetLoadedMinePoolFieldName(inst);
            if (fieldName != null && MinePoolGetters.TryGetValue(fieldName, out var getter))
            {
                // ldfld 前栈上是 GameCtrl 实例；改为 ldstr 字段名 + call 访问器，栈形不变。
                // 和 HoldOnMineTranspiler 同理：被替换的 ldfld 若带分支标签/异常块，必须转移到
                // 第一条替换指令，否则 DMD 编译报 "Label #N is not marked"。
                var ldstr = new CodeInstruction(OpCodes.Ldstr, fieldName);
                ldstr.labels.AddRange(inst.labels);
                ldstr.blocks.AddRange(inst.blocks);
                yield return ldstr;
                yield return new CodeInstruction(OpCodes.Call, getter);
                continue;
            }

            yield return inst;
        }
    }

    /// <summary>
    /// RegistNote 的箭头分配分支只识别 getEnum()==5（普通箭头）和 ==14（break 箭头）。
    /// 当前自定义 slide 只注册 NMSSS(Slide=5) 和 BRSSS(BreakSlide=14)，正好匹配原版分支，
    /// 无需扩展；若未来恢复 EXSSS(15)/BXSSS(16)/CNSSS(18) 注册，需重新引入族判断 transpiler。
    /// </summary>

    private static string GetLoadedMinePoolFieldName(CodeInstruction inst)
    {
        if (inst.opcode != OpCodes.Ldfld || inst.operand == null) return null;

        if (inst.operand is FieldInfo fi)
        {
            return Array.IndexOf(MinePoolFieldNames, fi.Name) >= 0 ? fi.Name : null;
        }

        var nameProp = inst.operand.GetType().GetProperty("Name");
        var name = nameProp?.GetValue(inst.operand) as string;
        return name != null && Array.IndexOf(MinePoolFieldNames, name) >= 0 ? name : null;
    }

    private sealed class PoolRoute
    {
        internal GameCtrl Controller;
        internal NoteData Note;
        internal bool ArrowsPrepared;
        internal CustomNoteKind Kind;
        internal HashSet<string> Fields;
    }
    private static readonly OwnerCallScope<PoolRoute> PoolRoutes = new();

    [HarmonyPrefix, HarmonyPriority(Priority.First + 50)]
    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static void RegistNotePrefix(GameCtrl __instance, object[] __args, out object __state)
    {
        // Enter even for stock charts: a nested call must mask its caller's route.
        var note = __args?.FirstOrDefault(a => a is NoteData) as NoteData;
        PoolRoute route = null;
        if (FeaturesEnabled(__instance) && note != null)
        {
            if (BounceSegmentsByType.Count > 0)
                BounceTypeByNoteIndex[note.indexNote] = ResolveBounceType(note);
            var kind = GetNoteKind(note);
            if (kind != CustomNoteKind.None)
                route = new PoolRoute { Controller = __instance, Note = note, Kind = kind, Fields = GetMineFieldsForNoteType(note.type.getEnum()) };
        }
        __state = PoolRoutes.Enter(__instance, route);
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last - 50)]
    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static void RegistNoteFinalizer(object __state) => PoolRoutes.Exit(__state);
}
