using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立绝赞 Touch 类（BRTTP，kind = TouchBreak）。
///
/// - 贴图：override Initialize 贴绝赞贴图（touch_break 系列，ApplyTouchBreakTextures）。
/// - 判定：判定窗口/输入逻辑与普通 touch 相同，命中按原判定结果；
///   统计按 BREAK 计分（由 TouchNoteB.EndNote transpiler 处理：SetResult kind → Break）——绝赞 = break 语义。
/// - 不是地雷：不做反转。实现 ISelfJudgingMineNote 仅用于让判定 transpiler 放行
///   （判定由本类自己负责）且不挂 MineNoteBehaviour。
/// </summary>
public class TouchBreakNoteB : TouchNoteB, ISelfJudgingMineNote
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        CustomNoteTypes.ApplyTouchBreakTexturesToObject(gameObject);
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

    public static TouchBreakNoteB CreateFrom(TouchNoteB prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<TouchBreakNoteB, TouchNoteB>(prefab, parent, m => m.RunBaseAwake());
    }
}
