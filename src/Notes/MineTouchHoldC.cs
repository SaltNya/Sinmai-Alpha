using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立地雷 TouchHold 类。
/// 判定：JudgeTotalResult 非虚无法 override，仍由 transpiler + MineNoteBehaviour 负责。
/// </summary>
public class MineTouchHoldC : TouchHoldC
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        // 显式挂 MineNoteBehaviour：本类不实现 ISelfJudgingMineNote，判定反转靠全局
        // transpiler + behaviour 识别；原依赖 NoteSpeedApplyPatch（已删）经基类 postfix 挂载。
        // 必须在下方 GetComponent<MineNoteBehaviour>() 判断之前执行。
        CustomNoteTypes.EnsureMineBehaviour(this, note);
        CustomNoteTypes.ApplyMineTexturesToObject(gameObject);
        // 绝赞 touchhold（BRTHO，kind=MineTouchBreak → behaviour.IsTouchBreak）：
        // 用 break 风格素材 touchhold_break_0..3 + 外框 touchhold_break（用户 2026-08 补充）。
        if (GetComponent<MineNoteBehaviour>()?.IsTouchBreak == true)
        {
            CustomNoteTypes.ApplyBreakTouchHoldTextures(this);
        }
    }

    /// <summary>
    /// 地雷绝赞 touchhold（MBTHO，kind = MineTouchBreak → MineNoteBehaviour.IsTouchBreak）：
    /// 与 BRTHO（TouchBreakHoldC）一致，按 BREAK 记分（2500/绝赞额外分）。
    /// 背景（2026-09 用户反馈"新加的绝赞种类分数没有按照 break 计算"）：
    /// 本类原先不 override SetPlayResult，走 TouchHoldC 原版 → 记 Touch(500)，
    /// 与 BRTHO 的 Break 记分不一致（BRTHO 在 TouchBreakHoldC.SetPlayResult 里显式按 Break）。
    /// 普通地雷 touchhold（MNTHO，IsTouchBreak=false）继续走原版 Touch 记分。
    /// </summary>
    protected override void SetPlayResult()
    {
        if (GetComponent<MineNoteBehaviour>()?.IsTouchBreak == true)
        {
            var gameScore = MAI2.Util.Singleton<GamePlayManager>.Instance.GetGameScore(MonitorId, -1);
            gameScore.SetResult(NoteIndex, NoteScore.EScoreType.Break, GetJudgeResult());
            return;
        }

        base.SetPlayResult();
    }

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static MineTouchHoldC CreateFrom(TouchHoldC prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineTouchHoldC, TouchHoldC>(prefab, parent, m => m.RunBaseAwake());
    }
}
