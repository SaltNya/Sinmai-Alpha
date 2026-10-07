using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 标记：判定由类自身反转（override Judge/JudgeToolate）的独立地雷类。
/// 全局判定 transpiler（MineJudgeInversion）对这类对象放行，避免双重反转；
/// TryApplySpeedToNoteObject 也不给这类对象挂 MineNoteBehaviour。
/// </summary>
public interface ISelfJudgingMineNote
{
}

/// <summary>各独立地雷类共用的判定工具（JudgeResult 是原版 private 字段，用 Traverse 访问）。</summary>
internal static class MineJudgeHelper
{
    private static bool _logged;

    /// <summary>旧版地雷键判定规则（已废弃，2026-08-26 用户要求四类统一为 InvertStandard）：
    /// 刚好在轨道上（原判定 Critical，误差 0）→ Critical Perfect；其余一律 Miss。
    /// 保留仅供历史参考——所有地雷类现在都走 InvertStandard（碰到→Miss、没碰→CP）。</summary>
    internal static NoteJudge.ETiming Invert(NoteJudge.ETiming timing)
    {
        // 只有原判定 Critical 保持 CP；其余全部映射到 Miss（TooLate 14）。
        return timing == NoteJudge.ETiming.Critical
            ? NoteJudge.ETiming.Critical
            : NoteJudge.ETiming.TooLate;
    }

    /// <summary>标准地雷判定规则（touch 系地雷：MNTTP/MNSTP，2026-08-26 用户要求）：
    /// 玩家碰到（原判定 Critical/Perfect/Great/Good/TooFast 等有效触碰）→ Miss（TooLate）；
    /// 没碰到（TooLate/End 超时未触发判定）→ Critical Perfect。
    /// 与 Invert 的区别：Invert 对"没碰到"也给 Miss（恰好等同普通判定，看起来没有反转）；
    /// 标准地雷是"碰到炸、不碰无事"，未命中必须给 CP。</summary>
    internal static NoteJudge.ETiming InvertStandard(NoteJudge.ETiming timing)
    {
        // 没碰到（超时/结束）→ CP；其余（碰到了，含完美/打偏/提前碰）一律 Miss。
        return timing == NoteJudge.ETiming.TooLate || timing == NoteJudge.ETiming.End
            ? NoteJudge.ETiming.Critical
            : NoteJudge.ETiming.TooLate;
    }

    internal static void SetJudgeResult(NoteBase note, NoteJudge.ETiming timing)
    {
        Traverse.Create(note).Field("JudgeResult").SetValue(timing);
    }

    internal static void LogFirstUse()
    {
        if (_logged) return;
        _logged = true;
        MelonLogger.Msg("[CustomNoteType] Mine note independent class active");
    }
}

/// <summary>
/// 组件替换工厂：把原版 Note prefab 克隆上的原版组件替换成 Mine* 类组件。
/// Unity 组件类型无法原地更换，做法：快照字段 → DestroyImmediate → AddComponent → 恢复字段 → postCreate（补执行基类 Awake）。
/// </summary>
public static class MineNoteFactory
{
    /// <typeparam name="TNew">目标 Mine* 组件类型（须继承 TOld）。</typeparam>
    /// <typeparam name="TOld">原版组件类型（如 TapNote）。</typeparam>
    /// <param name="postCreate">字段恢复后执行（通常用于补执行基类 Awake，缓存 SpriteRender 等）。</param>
    public static TNew CreateFrom<TNew, TOld>(TOld prefab, Transform parent, Action<TNew> postCreate = null)
        where TNew : Component, TOld
        where TOld : Component
    {
        var go = UnityEngine.Object.Instantiate(prefab.gameObject, parent);
        var old = go.GetComponent<TOld>();
        var snapshot = SnapshotFields(old);
        UnityEngine.Object.DestroyImmediate(old);
        var mine = go.AddComponent<TNew>();
        RestoreFields(mine, snapshot);
        postCreate?.Invoke(mine);
        return mine;
    }

    // 收集游戏逻辑层的实例字段值。
    // 跳过 UnityEngine.* 声明的字段：那些是 native 绑定（m_CachedPtr / m_GameObject 等），
    // 复制会导致托管对象指向错误的 native 对象。
    private static List<KeyValuePair<FieldInfo, object>> SnapshotFields(object source)
    {
        var list = new List<KeyValuePair<FieldInfo, object>>();
        for (var type = source.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            if (type.Namespace != null && type.Namespace.StartsWith("UnityEngine")) continue;

            foreach (var field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                list.Add(new KeyValuePair<FieldInfo, object>(field, field.GetValue(source)));
            }
        }

        return list;
    }

    private static void RestoreFields(object target, List<KeyValuePair<FieldInfo, object>> snapshot)
    {
        foreach (var pair in snapshot)
        {
            pair.Key.SetValue(target, pair.Value);
        }
    }
}
