using HarmonyLib;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>独立地雷 BreakStar 类（判定/贴图自持，模式同 MineTapNote）。</summary>
public class MineBreakStarNote : BreakStarNote, ISelfJudgingMineNote
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        CustomNoteTypes.ApplyMineTexturesToObject(gameObject);
        // EffectSprite（绝赞光效层）在 base.Initialize 的 SetMulti（单星/双星都走这里）里被赋原版
        // 贴图——登记并立刻替换成地雷光效（star_break_eff_mine / star_break_double_eff_mine）。
        CustomNoteTypes.MineifyBreakStarEffect(this);
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
    // 注意：BreakStarNote.SetMulti / SetSlideStar 是非虚方法，无法 override；
    // 地雷双星光效替换由 CustomNoteTypes.BreakStarSetMultiPostfix（Harmony postfix）处理。

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static MineBreakStarNote CreateFrom(BreakStarNote prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineBreakStarNote, BreakStarNote>(prefab, parent, m => m.RunBaseAwake());
    }
}
