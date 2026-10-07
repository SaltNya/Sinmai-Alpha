using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;

namespace SinmaiAlpha.Notes;

/// <summary>
/// 地雷 Note 的独立行为组件。
/// 这是往“像 Break 一样独立类”方向走的第一步：
/// 不再把所有逻辑都塞在 CustomNoteTypes 的 patch 里，
/// 而是挂到地雷 Note 对象上，由它自己负责地雷贴图、判定等逻辑。
/// </summary>
public class MineNoteBehaviour : MonoBehaviour
{
    private static readonly Dictionary<int, MineNoteBehaviour> Instances = new Dictionary<int, MineNoteBehaviour>();

    public int NoteIndex { get; private set; }
    public bool IsMine { get; private set; }
    public bool IsTouchBreak { get; private set; }

    public static bool TryGet(int noteIndex, out MineNoteBehaviour behaviour)
    {
        return Instances.TryGetValue(noteIndex, out behaviour);
    }

    public static void ClearInstances()
    {
        Instances.Clear();
    }

    public void Setup(int noteIndex, bool isMine, bool isTouchBreak, bool applyTextures = true)
    {
        NoteIndex = noteIndex;
        IsMine = isMine;
        IsTouchBreak = isTouchBreak;

        Instances[noteIndex] = this;
        if (applyTextures)
        {
            ApplyMineVisual();
        }
    }

    private void OnDestroy()
    {
        if (Instances.TryGetValue(NoteIndex, out var current) && ReferenceEquals(current, this))
        {
            Instances.Remove(NoteIndex);
        }
    }

    private void ApplyMineVisual()
    {
        // 每 note 打日志会刷屏卡顿，这里不再单独打日志（注册日志已能验证）。
        if (IsMine)
        {
            CustomNoteTypes.ApplyMineTexturesToObject(gameObject);
        }
        else
        {
            CustomNoteTypes.ApplyTouchBreakTexturesToObject(gameObject);
        }
    }
}
