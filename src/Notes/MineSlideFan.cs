using System.Reflection;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立地雷 FanSlide（Wi-Fi 扇形滑）类，对应 NMSF_ 系列的地雷版（MNSF_ 等）。
///
/// - 轨道线（_spriteLines，11 条）贴 wifi_mine_0..10。
/// - 3 颗移动星贴 star_mine / star_break_mine。
/// - 判定：SlideFan 继承 SlideRoot，判定走继承的 SlideRoot.Judge / JudgeToolate
///   （非虚，由全局 transpiler + MineNoteBehaviour 反转，本类不实现 ISelfJudgingMineNote）。
/// - SlideFan.Awake 是 private 非虚（实例化 _arrowPrefubs → _spriteLines），
///   组件替换时用反射补执行。
/// </summary>
public class MineSlideFan : SlideFan
{
    // SlideFan.Awake 是 private 非虚，直接遮蔽（不会触发基类）。
    private void Awake()
    {
    }

    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        // base.Initialize 已执行 SetEach + SetBreak（原版贴图赋值完毕），统一贴地雷贴图。
        CustomNoteTypes.ApplyMineFanSlideTextures(this);
        // ⚠️ SlideFan.Initialize 不调用 SlideRoot.Initialize（完全重写），基类 postfix 链
        // 不会给 fan 挂 MineNoteBehaviour——必须显式挂，否则 transpiler 反转完全不生效
        // （表现：划完后显示原版 good/great/criticalperfect 而不是 miss）。
        CustomNoteTypes.EnsureMineBehaviour(this, note);
    }

    public override void SetEach(bool eachFlag)
    {
        base.SetEach(eachFlag);
        // SetEach 会给 _spriteStars 赋原版星贴图，重新贴回地雷贴图。
        CustomNoteTypes.ApplyMineFanSlideTextures(this);
    }

    protected override void UpdateAlpha()
    {
        base.UpdateAlpha();
        // slide_fun_mine_00-10 是白色贴图，染灰（地雷风格），保留 base 算好的 alpha 淡入淡出。
        var lines = HarmonyLib.Traverse.Create(this).Field("_spriteLines").GetValue<SpriteRenderer[]>();
        if (lines == null) return;
        foreach (var sr in lines)
        {
            if (sr == null) continue;
            var c = sr.color;
            sr.color = new Color(0.5f, 0.5f, 0.5f, c.a);
        }
    }

    public static MineSlideFan CreateFrom(SlideFan prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineSlideFan, SlideFan>(prefab, parent, m =>
        {
            // 基类 Awake 是 private，用反射补执行（实例化 _arrowPrefubs 并填充 _spriteLines 等）。
            typeof(SlideFan).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(m, null);
        });
    }
}
