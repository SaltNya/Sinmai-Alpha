using HarmonyLib;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>独立地雷 TouchC 类（C 传感器触摸，判定走继承的 NoteBase.Judge）。</summary>
public class MineTouchNoteC : TouchNoteC, ISelfJudgingMineNote
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
        // 按反转后的判定播 SE（命中播 Miss 音、没碰播 CP 音）；TouchNoteC 继承链播原版值。
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

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static MineTouchNoteC CreateFrom(TouchNoteC prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineTouchNoteC, TouchNoteC>(prefab, parent, m => m.RunBaseAwake());
    }
}
