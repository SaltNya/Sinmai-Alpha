using System.Reflection;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 独立地雷 Slide 类。
/// 注意：SlideRoot.Judge / JudgeToolate 是非虚方法（Awake 甚至是 private），判定无法 override，
/// 仍由全局 transpiler + MineNoteBehaviour 负责（本类不实现 ISelfJudgingMineNote）。
/// 贴图：不在这里贴——slide 的轨道/箭头由独立地雷箭头池负责（_arrowObjectList / _breakArrowObjectList），
/// 内部星（_starNote / _breakStarNote）保持原版样式（避免被误贴成地雷星）。
/// </summary>
public class MineSlideRoot : SlideRoot
{
    // SlideRoot.Awake 是 private 非虚，直接遮蔽（不会触发基类）。
    private void Awake()
    {
    }

    public override void Initialize(NoteData note)
    {
        base.Initialize(note);
        // 精准贴 slide 移动星（_starNote / _breakStarNote），不遍历子物体。
        CustomNoteTypes.ApplyMineSlideStarTextures(this);
        // 显式确保判定标记（SlideRoot.Initialize 的 postfix 链正常情况下已挂，
        // 这里兜底：万一 postfix 没触发，transpiler 反转会完全不生效）。
        CustomNoteTypes.EnsureMineBehaviour(this, note);
    }

    public override void SetEach(bool eachFlag)
    {
        base.SetEach(eachFlag);
        // SetEach 会给箭头和星赋原版 sprite，重新贴回地雷贴图。
        CustomNoteTypes.ApplyMineArrowTextures(this);
        CustomNoteTypes.ApplyMineSlideStarTextures(this);
    }

    public static MineSlideRoot CreateFrom(SlideRoot prefab, Transform parent)
    {
        return MineNoteFactory.CreateFrom<MineSlideRoot, SlideRoot>(prefab, parent, m =>
        {
            // 基类 Awake 是 private，用反射补执行（SlideRoot.Awake 实例化内部星并缓存组件引用）。
            typeof(SlideRoot).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(m, null);
        });
    }
}
