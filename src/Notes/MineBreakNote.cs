using HarmonyLib;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>独立地雷 Break Tap 类（判定/贴图自持，模式同 MineTapNote）。</summary>
public class MineBreakNote : BreakNote, ISelfJudgingMineNote
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        CustomNoteTypes.ApplyMineTexturesToObject(gameObject);
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

    protected override void SetEach(bool eachFlag)
    {
        base.SetEach(eachFlag);
        // SetEach 会把 EffectSprite（绝赞光效层）设为原版 BreakEff，替换成地雷光效。
        var effect = Traverse.Create(this).Field("EffectSprite").GetValue<SpriteRenderer>();
        CustomNoteTypes.ApplyMineEffectTexture(effect, "tap_break_eff_mine");
    }

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static MineBreakNote CreateFrom(BreakNote prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineBreakNote, BreakNote>(prefab, parent, m => m.RunBaseAwake());
    }
}
