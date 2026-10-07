using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// TouchStar（NMSTP / BRSTP）独立类。
/// 基础逻辑与 touch 完全一致（NMSTP = 普通 touch：判定窗口、计分（Touch 分）全部与 touch 相同；
/// BRSTP = 绝赞 touch star：判定窗口同 touch，按 BREAK 统计总分——同绝赞 touch BRTTP，
/// 不强制 CP，判定结果按原判定）。
/// 计分类别在 Initialize（进入判定前）就按 kind 统一固化到 IsBreakStar：
///   普通 NMSTP → Touch 分；BRSTP → Break 分（绝赞额外分）。
/// 计分时刻（TouchNoteB.EndNote transpiler → GetTouchScoreKind）只读 IsBreakStar，不做类型推断。
/// 贴图：五瓣星 touch_star / touch_star_break / touch_hit_star（普通 touch 是四瓣）。
/// 暂时没有地雷版。
/// </summary>
public class TouchStarNoteB : TouchNoteB, ISelfJudgingMineNote
{
    /// <summary>是否为绝赞 touchstar（BRSTP）。Initialize 时按 kind 固化，之后只读。</summary>
    public bool IsBreakStar { get; private set; }

    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        IsBreakStar = CustomNoteTypes.GetNoteKind(note) == CustomNoteTypes.CustomNoteKind.TouchBreakStar;
        CustomNoteTypes.ApplyTouchStarTexturesToObject(gameObject, IsBreakStar);
    }

    /// <summary>base.SetEach 会给瓣贴原版 EachTouch/NormalTouch，重新贴五瓣星
    /// （ApplyTouchStarTextures 内部按 EachFlag 选 touch_star_each / touch_star）。</summary>
    protected override void SetEach(bool eachFlag)
    {
        base.SetEach(eachFlag);
        CustomNoteTypes.ApplyTouchStarTexturesToObject(gameObject, IsBreakStar);
    }

    protected override void PlayJudgeSe()
    {
        var traverse = Traverse.Create(this);
        if (traverse.Field("ShotJudgeSound").GetValue<bool>()) return;

        var box = NoteJudge.ConvertJudge(GetJudgeResult());
        if (IsExNote)
        {
            ReserveExJudgeSe(box);
        }
        else
        {
            ReserveTapJudgeSe(box);
        }

        traverse.Field("ShotJudgeSound").SetValue(true);
    }

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static TouchStarNoteB CreateFrom(TouchNoteB prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<TouchStarNoteB, TouchNoteB>(prefab, parent, m => m.RunBaseAwake());
    }
}
