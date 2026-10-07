using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// C 区绝赞 Touch。保留 TouchNoteC 的中心定位、输入、判定和音效，
/// 只替换贴图；TouchNoteB.EndNote 的计分补丁将本类计入原版 Break。
/// </summary>
public class TouchBreakNoteC : TouchNoteC, ISelfJudgingMineNote
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        CustomNoteTypes.ApplyTouchBreakTexturesToObject(gameObject);
    }

    protected override void SetEach(bool eachFlag)
    {
        base.SetEach(eachFlag);
        CustomNoteTypes.ApplyTouchBreakTexturesToObject(gameObject);
    }

    // 组件替换时先还原 prefab 字段，再执行原版 Awake。
    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static TouchBreakNoteC CreateFrom(TouchNoteC prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<TouchBreakNoteC, TouchNoteC>(prefab, parent, m => m.RunBaseAwake());
    }
}
