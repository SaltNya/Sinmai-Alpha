using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SinmaiAlpha.Hosting;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes;

// 地雷行为组件绑定和自动播放判定。
public partial class CustomNoteTypes
{

    /// <summary>确保地雷 note 挂了 MineNoteBehaviour（slide 特判用）。
    /// SlideFan.Initialize 不调用 SlideRoot.Initialize，基类 postfix 链可能不触发，必须显式挂。
    /// ⚠️ 不能只依赖 NoteKinds[data.indexNote]：slide 初始化拿到的 NoteData 的 indexNote
    /// 可能与注册时不一致（历史 bug #22）——Mine* 类本身只可能来自地雷池，直接按类判定。</summary>
    internal static void EnsureMineBehaviour(Component note, NoteData data)
    {
        try
        {
            if (note == null || data == null) return;

            if (!NoteKinds.TryGetValue(data.indexNote, out var kind) || kind == CustomNoteKind.None)
            {
                // indexNote 查不到（slide 的 NoteData indexNote 与注册不一致）→ 按类兜底。
                if (note is not (MineSlideRoot or MineSlideFan)) return;
                kind = CustomNoteKind.Mine;
            }

            var behaviour = note.gameObject.GetComponent<MineNoteBehaviour>();
            if (behaviour == null)
            {
                behaviour = note.gameObject.AddComponent<MineNoteBehaviour>();
            }

            behaviour.Setup(data.indexNote,
                kind is CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak,
                kind is CustomNoteKind.TouchBreak or CustomNoteKind.MineTouchBreak,
                applyTextures: false);
        }
        catch
        {
        }
    }

    /// <summary>判断是不是地雷 note（简单键类型；slide 不在这里处理）。
    /// ⚠️ 必须按类判断：自反转类（tap/star/break/touch）按历史设计不挂 MineNoteBehaviour，
    /// 用 behaviour 判断会导致自动 CP 对它们完全不触发（历史 bug #25）。
    /// 2026-08-26：加入 MineTouchStarNoteB/C（MNSTP 地雷 touchstar）——与 MNTTP 一样到线自动 CP。</summary>
    private static bool IsMineSimpleNote(NoteBase note)
    {
        return note is MineTapNote or MineStarNote or MineBreakNote or MineBreakStarNote
            or MineHoldNote or MineBreakHoldNote or MineTouchNoteB or MineTouchNoteC or MineTouchHoldC
            or MineTouchStarNoteB or MineTouchStarNoteC;
    }

    /// <summary>地雷键自动判定：按键到达判定线那一刻自动判 Critical Perfect，不需要玩家碰。
    /// 只取 autoplay 的"到线判 CP"时机，不改动其它任何行为（不自动推进、不忽略触摸）：
    /// 玩家在判定线之前碰到 → 正常判定路径（打偏 → Miss）；不碰 → 到线自动 CP，从这一帧起
    /// 不再有任何 late/miss 判定（note 直接以 CP 收尾）。
    /// slide 不适用：slide 有自己的规则（星星到底=CP、提前划完=Miss，见 ApplyMineJudgeTiming）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "SetAutoPlayJudge")]
    public static void MineAutoJudgePostfix(NoteBase __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (IsFakeNoteOwner(__instance)) return;
        try
        {
            // ⚠️ hold 系排除（历史 bug：MineHoldNote 也命中 IsMineSimpleNote）：
            // 这里 Traverse 直写 JudgeResult=Critical 会绕过 MineJudgeInversion 的 stfld 反转链，
            // 且让 MineHoldTouchCheck 的守卫 `GetJudgeResult()!=End → return` 从判定线前 ~4.17ms 起
            // 永远触发 → "判定期间碰到→立即 Miss" 完全失效（用户实测"MNHLD 碰到也 CP"的根因）。
            // hold 的头自动判定由原版 SetAutoPlayJudge override（写 JudgeHeadResult，配合
            // MineHoldAutoJudge）负责，不写 JudgeResult，无冲突。
            if (__instance is MineHoldNote or MineBreakHoldNote or MineTouchHoldC) return;
            if (!IsMineSimpleNote(__instance)) return;
            // 已判过（玩家判定线前碰到 = Miss / 已自动 CP）→ 不动
            if (__instance.GetJudgeResult() != NoteJudge.ETiming.End) return;
            // 原版 autoplay 门槛：判定线前 4.17ms 内
            var now = NotesManager.GetCurrentMsec();
            var appear = Traverse.Create(__instance).Field("AppearMsec").GetValue<float>();
            if (now < appear - 4.166667f) return;
            // 到轨道 → 自动 Critical Perfect（AutoJudge 在非 autoplay 模式返回 TooFast，不能直接用它）
            Traverse.Create(__instance).Field("JudgeResult").SetValue(NoteJudge.ETiming.Critical);
            Traverse.Create(__instance).Method("PlayJudgeSe").GetValue();
        }
        catch
        {
        }
    }

    /// <summary>地雷 slide 自动判定：星星滑到 perfect 判定时刻还没提前划完 → 自动判 Critical Perfect 并立即收尾。
    /// 背景：SlideRoot : MonoBehaviour（不继承 NoteBase），MineAutoJudgePostfix（patch NoteBase.SetAutoPlayJudge）
    /// 对 slide 不生效。原版地雷 slide 的 CP 由 ApplyMineJudgeTiming 在 JudgeToolate（超时）时才给；
    /// 用户 2026-08-26 要求："在普通星星刚好判定为perfect的时候就判定为perfect 只要没提前划完就是perfect"。
    /// 判定点 = 滑到终点的 perfect 时刻（Judge() 里 diff = now-TailMsec+lastWait 为 0 的点 = TailMsec-lastWaitTimeForJudge）。
    /// 2026-08-26 二次修正：用户实测"地雷星星会稍稍快于刚好划完的时候就消失"——TailMsec-lastWait 比星星
    /// 视觉到终点（TailMsec，slide 走完整条轨道）提前了 lastWait ms → 自动收尾时刻改到 tailMsec（轨道刚走完，
    /// 星星到终点即消失）。玩家在判定点前划完 → 走原版 Judge → 反转 Miss，不受影响；判定点后、TailMsec 前
    /// 划完 → 原版 perfect/晚判 → 反转 Miss（碰到地雷 = Miss），只有完全没划完才自动 CP。
    /// 收尾必须在本帧完成（复刻 NoteCheck 收尾段），否则延迟窗口内玩家晚划会触发 Judge() 把 CP 覆盖成 Miss。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SlideRoot), "NoteCheck")]
    public static void MineSlideAutoJudgePostfix(SlideRoot __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (IsFakeNoteOwner(__instance)) return;
        try
        {
            if (__instance is not (MineSlideRoot or MineSlideFan)) return;
            // 已判定（提前划完 = Miss / 已自动 CP）→ 不动
            if (__instance.GetJudgeResult() != NoteJudge.ETiming.End) return;
            var t = Traverse.Create(__instance);
            var tailMsec = t.Field("TailMsec").GetValue<float>();
            // 自动判定点 = 轨道走完时刻（TailMsec）：星星刚好到终点即收尾消失；
            // 未到点不自动判（玩家仍可提前划完 → Miss）。
            if (NotesManager.GetCurrentMsec() < tailMsec) return;
            // 没提前划完 → 自动 Critical Perfect
            t.Field("JudgeResult").SetValue(NoteJudge.ETiming.Critical);
            t.Field("JudgeTimingDiffMsec").SetValue(0f);
            t.Method("PlayJudgeSe").GetValue();
            // 立即收尾（复刻 SlideRoot.NoteCheck 720-749 行收尾段）
            var parent = __instance.ParentTransform;
            var arrows = t.Field("_arrowList").GetValue<List<GameObject>>();
            for (var i = arrows.Count - 1; i >= 0; i--)
            {
                arrows[i].SetActive(false);
                arrows[i].transform.SetParent(parent, false);
                arrows.RemoveAt(i);
            }

            var breaks = t.Field("_breakArrowList").GetValue<List<GameObject>>();
            for (var i = breaks.Count - 1; i >= 0; i--)
            {
                breaks[i].SetActive(false);
                breaks[i].transform.SetParent(parent, false);
                breaks.RemoveAt(i);
            }

            var judgeObj = t.Field("JudgeObj").GetValue<SlideJudge>();
            if (judgeObj != null && ShouldShowMineFeedback(__instance))
            {
                judgeObj.Initialize(NoteJudge.ETiming.Critical, 0f, t.Field("BreakFlag").GetValue<bool>());
            }

            t.Field("DispJudge").SetValue(true);
            t.Field("EndFlag").SetValue(true);
            t.Method("SetPlayResult").GetValue(); // SetResult(NoteIndex, kind, Critical) → CP 计分
            __instance.gameObject.SetActive(false);
            __instance.transform.SetParent(parent, false);
        }
        catch
        {
        }
    }

    /// <summary>地雷 hold/touchhold 视作 autoplay（自动按住到尾部，不碰=按住到底判 CP）。</summary>
    public static bool IsAutoPlayOrMineHold(object instance)
    {
        if (instance is MineHoldNote or MineBreakHoldNote or MineTouchHoldC)
        {
            return true;
        }

        return GameManager.IsAutoPlay();
    }

    /// <summary>地雷 hold 的自动判定值（AutoJudge 在非 autoplay 模式返回 TooFast，不能用）。</summary>
    public static NoteJudge.ETiming MineHoldAutoJudge(object instance)
    {
        if (instance is MineHoldNote or MineBreakHoldNote or MineTouchHoldC) return NoteJudge.ETiming.Critical;
        return GameManager.AutoJudge();
    }
}
