using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立绝赞 TouchHold 类（BRTHO，kind = TouchBreak，非地雷）。
///
/// - 贴图：Initialize 时贴绝赞贴图（touchhold_break_0..3 + 外框 touchhold_break）。
/// - 判定：判定窗口/输入逻辑与普通 touchhold 相同，命中按原判定结果；
///   统计按 BREAK 计分（SetResult(NoteIndex, 3, timing)）——绝赞 = break 语义，不强制 CP。
///   （TouchHoldC 的流程是 EndNote → JudgeTotalResult（无 SetResult）→ SetPlayResult，
///    统计只发生在 SetPlayResult，所以这个 override 直接生效，无需 transpiler。）
/// - 不是地雷：不做反转。实现 ISelfJudgingMineNote 仅用于让判定 transpiler 放行
///   且不挂 MineNoteBehaviour。
/// </summary>
public class TouchBreakHoldC : TouchHoldC, ISelfJudgingMineNote
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        CustomNoteTypes.ApplyBreakTouchHoldTextures(this);
    }

    protected override void SetEach(bool eachFlag)
    {
        base.SetEach(eachFlag);
        CustomNoteTypes.ApplyBreakTouchHoldTextures(this);
    }

    protected override void SetPlayResult()
    {
        // 绝赞 touchhold（BRTHO）：按 BREAK 统计总分（同 BreakHoldNote）
        var gameScore = Singleton<GamePlayManager>.Instance.GetGameScore(MonitorId, -1);
        gameScore.SetResult(NoteIndex, NoteScore.EScoreType.Break, GetJudgeResult());
    }

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static TouchBreakHoldC CreateFrom(TouchHoldC prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<TouchBreakHoldC, TouchHoldC>(prefab, parent, m => m.RunBaseAwake());
    }
}
