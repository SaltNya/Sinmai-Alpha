using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 地雷 TouchStar 的 C 传感器版（MNSTP 放在中心区时用 TouchNoteC 组件）。
/// 逻辑与 C touchstar 完全一致（计分 = Touch 分，判定 = 地雷反转：命中 Miss / 未命中 Critical Perfect），
/// 贴图 touch_star_mine（LoadMineTextures 垂直翻转）。
/// 结构：继承 TouchStarNoteC 拿到五瓣星贴图/SetEach/PlayJudgeSe/IsBreakStar，
///      Initialize 里 base 先铺 touch_star 结构，再覆盖成 touch_star_mine 地雷贴图。
/// </summary>
public class MineTouchStarNoteC : TouchStarNoteC
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        CustomNoteTypes.ApplyMineTouchStarTexturesToObject(gameObject);
    }

    protected override bool Judge()
    {
        var result = base.Judge();
        if (result)
        {
            MineJudgeHelper.LogFirstUse();
            MineJudgeHelper.SetJudgeResult(this, MineJudgeHelper.InvertStandard(GetJudgeResult()));
        }

        return result;
    }

    protected override bool JudgeToolate()
    {
        var result = base.JudgeToolate();
        if (result)
        {
            MineJudgeHelper.LogFirstUse();
            MineJudgeHelper.SetJudgeResult(this, MineJudgeHelper.InvertStandard(GetJudgeResult()));
        }

        return result;
    }

    protected override void PlayJudgeSe()
    {
        // 按反转后的判定播 SE（命中播 Miss 音、没碰播 CP 音）；TouchStarNoteC.PlayJudgeSe 播原版值。
        var traverse = Traverse.Create(this);
        if (traverse.Field("ShotJudgeSound").GetValue<bool>()) return;

        var box = NoteJudge.ConvertJudge(MineJudgeHelper.InvertStandard(GetJudgeResult()));
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

    // Awake 私有化与 RunBaseAwake 继承自 TouchStarNoteC（MineNoteFactory 的 setup 回调调用）。

    public new static MineTouchStarNoteC CreateFrom(TouchNoteC prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineTouchStarNoteC, TouchNoteC>(prefab, parent, m => m.RunBaseAwake());
    }
}
