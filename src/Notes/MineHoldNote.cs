using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立地雷 Hold 类。
/// 注意：HoldNote.JudgeTotalResult / JudgeHoldHead 是非虚方法，判定无法 override，
/// 所以判定反转仍由全局 transpiler + MineNoteBehaviour 负责（本类不实现 ISelfJudgingMineNote），
/// 独立类部分只做：组件类型 + 贴图（Initialize override）。
/// </summary>
public class MineHoldNote : HoldNote
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        // 显式挂 MineNoteBehaviour：本类不实现 ISelfJudgingMineNote，判定反转靠全局
        // transpiler + behaviour 识别；原依赖 NoteSpeedApplyPatch（已删）经基类 postfix 挂载。
        CustomNoteTypes.EnsureMineBehaviour(this, note);
        CustomNoteTypes.ApplyMineTexturesToObject(gameObject);
    }

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static MineHoldNote CreateFrom(HoldNote prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineHoldNote, HoldNote>(prefab, parent, m => m.RunBaseAwake());
    }
}
