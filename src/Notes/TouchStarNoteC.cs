using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// TouchStar 的 C 传感器版（NMSTP/BRSTP 放在中心区时用 TouchNoteC 组件）。
/// 逻辑与 C touch 完全一致（NMSTP = Touch 分；BRSTP = 绝赞，Break 分——
/// 计分类别在 Initialize 固化到 IsBreakStar，由 TouchNoteB.EndNote transpiler → GetTouchScoreKind 读取），
/// 贴图五瓣星。
/// </summary>
public class TouchStarNoteC : TouchNoteC, ISelfJudgingMineNote
{
    /// <summary>是否为绝赞 touchstar（BRSTP）。Initialize 时按 kind 固化，之后只读。</summary>
    public bool IsBreakStar { get; private set; }

    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        IsBreakStar = CustomNoteTypes.GetNoteKind(note) == CustomNoteTypes.CustomNoteKind.TouchBreakStar;
        CustomNoteTypes.ApplyTouchStarTexturesToObject(gameObject, IsBreakStar);
    }

    /// <summary>base.SetEach 会给主 sprite 贴原版 EachTouch/NormalTouch，重新贴五瓣星
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

    public static TouchStarNoteC CreateFrom(TouchNoteC prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<TouchStarNoteC, TouchNoteC>(prefab, parent, m => m.RunBaseAwake());
    }
}
