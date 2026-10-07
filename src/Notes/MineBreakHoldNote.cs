using HarmonyLib;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立地雷 BreakHold 类。
/// 判定：JudgeTotalResult 非虚无法 override，仍由 transpiler + MineNoteBehaviour 负责。
/// </summary>
public class MineBreakHoldNote : BreakHoldNote
{
    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        // 显式挂 MineNoteBehaviour：本类不实现 ISelfJudgingMineNote，判定反转靠全局
        // transpiler + behaviour 识别；原依赖 NoteSpeedApplyPatch（已删）经基类 postfix 挂载。
        CustomNoteTypes.EnsureMineBehaviour(this, note);
        CustomNoteTypes.ApplyMineTexturesToObject(gameObject);
    }

    protected override void SetEach(bool eachFlag)
    {
        base.SetEach(eachFlag);
        // SetEach 会把 BreakEffectSprite（绝赞光效层）赋成原版 BreakHoldEff（橙色闪光），
        // 替换成 hold_break_eff_mine（用户 2026-08 补充的素材）。
        var breakEff = Traverse.Create(this).Field("BreakEffectSprite").GetValue<SpriteRenderer>();
        CustomNoteTypes.ApplyMineEffectTexture(breakEff, "hold_break_eff_mine");
    }

    private new void Awake()
    {
    }

    internal void RunBaseAwake() => base.Awake();

    public static MineBreakHoldNote CreateFrom(BreakHoldNote prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineBreakHoldNote, BreakHoldNote>(prefab, parent, m => m.RunBaseAwake());
    }
}
