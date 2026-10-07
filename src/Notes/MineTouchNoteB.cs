using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立地雷 Touch 类（B 类触摸）。
/// 判定/贴图自持：Judge / JudgeToolate override 反转（TouchNoteB.Judge 是 virtual override，可再 override）。
/// 判定规则（标准地雷，2026-08-26）：碰到 → Miss；没碰到（超时）→ Critical Perfect（InvertStandard）。
/// SE：override PlayJudgeSe 按反转后判定播（命中播 Miss 音、没碰播 CP 音），防音画不同步。
/// 地雷绝赞（MBTTP，kind=MineTouchBreak）：统计按 BREAK（由 TouchNoteB.EndNote transpiler 处理）。
/// </summary>
public class MineTouchNoteB : TouchNoteB, ISelfJudgingMineNote
{
    private bool _isMineTouchBreak;

    internal bool IsMineTouchBreak => _isMineTouchBreak;

    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        _isMineTouchBreak = CustomNoteTypes.GetNoteKind(note) == CustomNoteTypes.CustomNoteKind.MineTouchBreak;
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
        // base.Judge / JudgeToolate 内部虚调用到这里时 JudgeResult 还是原版值（反转在其返回后），
        // 必须按反转后的判定播 SE：命中（原版 Perfect/Good/...）播 Miss 音，没碰到播 CP 音。
        // 原版 TouchNoteB.PlayJudgeSe 会按原版值播（音画不同步），这里覆盖。
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

    public static MineTouchNoteB CreateFrom(TouchNoteB prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineTouchNoteB, TouchNoteB>(prefab, parent, m => m.RunBaseAwake());
    }
}
