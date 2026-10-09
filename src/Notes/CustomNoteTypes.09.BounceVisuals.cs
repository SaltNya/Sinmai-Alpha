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

// 弹跳轨迹、可见性和附属特效同步。
public partial class CustomNoteTypes
{
    /// <summary>弹跳视觉：GetNoteYPosition 后置改写音符位置（基类，Tap/Star/Hold/Break 全覆盖）。
    /// __result 返回弹跳 y（HoldNote.Execute 用返回值算 body 长度/中点，不能改 0）。
    /// 外框（NoteGuide 弧形引导环）：按【原版运动轨迹 + 弹跳位移】处理——贴图/缩放/朝向
    /// 全部保持原版逻辑，只在其原版 local 位置上叠加 delta（= 弹跳位置 − 原版位置），
    /// 与音符的相对关系与原版任意时刻一致（原版判定线处对齐 → 弹跳中自然对齐）。
    /// 光效/绝赞挂到 NoteObj 下跟随（ReparentBounceEffects）。
    /// 弹跳窗口前：音符本体隐藏（alpha=0，对应 Majdata forceRenderingOff）；
    /// 判定后：外框位移归零（原版），恢复可见。</summary>
    public static void BounceNoteVisualPostfix(NoteBase __instance, ref float __result)
    {
        if (BounceSegmentsByType.Count == 0 || __instance == null) return;
        // TouchHoldC 用基类 GetNoteYPosition，但 bounce 只适配 Tap/Star/Hold/Break
        // （Majdata 的 BOUNCE 类型不含 touch）；TouchNoteB 有自己的位置公式，天然排除。
        if (__instance is TouchHoldC) return;
        if (__instance is HoldNote || __instance is BreakHoldNote) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var appear = tr.Field("AppearMsec").GetValue<float>();
            float bounce;
            if (!BounceDurationByNoteIndex.TryGetValue(tr.Field("NoteIndex").GetValue<int>(), out bounce) || bounce <= 0f)
            {
                // fallback：直接查段表（字典可能因加载顺序未构建——GetNoteList 在 loadMa2Main 后置可能为空）。
                string fallbackType;
                if (!BounceTypeByNoteIndex.TryGetValue(tr.Field("NoteIndex").GetValue<int>(), out fallbackType))
                    return;
                bounce = GetBounceDurationAt(appear, fallbackType);
                if (bounce <= 0f) return;
            }

            var now = NotesManager.GetCurrentMsec();
            var judgeOffset = now - appear;
            var bMs = bounce * 1000f;

            if (judgeOffset < -bMs)
            {
                // 弹跳窗口前：音符本体 + 光效 + 外框全部隐藏——note 弹出前外框不可见，
                // 避免外框从中间快速放大移动到判定线的视觉（scale 预过渡也取消）。
                SetBounceAlpha(tr, 0f);
                SetBounceChildrenActive(tr, false);
                SetBounceGuideActive(tr, false);
                return;
            }

            if (judgeOffset < 0f)
            {
                // 弹跳窗口内：抛物线位置（出生半径→判定线→出生半径；SPAWN 联动）。
                // 弹跳轨迹以【原版判定线位置】为起终点（含音符速度常量偏移 V_9），
                // 否则外框在接近判定线时残留 -V_9 偏移、弧圈拼不齐。
                // 出生半径 R 由 SPAWN 曲线给出（默认 1.225）；y 仍按标准映射
                // t=(distance−1.225)/3.575，与停驻位置（v5=(R−1.225)/3.575）无缝衔接。
                var bounceIndex = tr.Field("NoteIndex").GetValue<int>();
                var spawnR = BounceSpawnRadius;
                if (!SpawnRadiusByNoteIndex.TryGetValue(bounceIndex, out spawnR))
                {
                    string fallbackType;
                    if (BounceTypeByNoteIndex.TryGetValue(bounceIndex, out fallbackType))
                        spawnR = GetSpawnRadiusAt(appear, fallbackType);
                }

                var elapsed = Mathf.Clamp(judgeOffset + bMs, 0f, bMs);                var half = bMs * 0.5f;
                var destroyR = RingValuesByNoteIndex.TryGetValue(bounceIndex, out var ringValue) ? ringValue.Destroy : BounceJudgeLine;
                var accel = 8f * (destroyR - spawnR) / (bMs * bMs);
                var fromApex = elapsed - half;
                var distance = spawnR + 0.5f * accel * fromApex * fromApex;
                var t = (distance - BounceSpawnRadius) / (BounceJudgeLine - BounceSpawnRadius);
                var startPos = tr.Field("StartPos").GetValue<float>();
                var endPos = tr.Field("EndPos").GetValue<float>();
                var span = endPos - startPos;
                var v9 = GetNoteSpeedOffsetY(__instance, span);
                var bounceY = startPos + span * t + v9;
                __result = bounceY;

                // 外框（弧形提示线）：弧心固定在音符轨道圆心（屏幕中心，localPosition 不动），
                // 弧大小直接跟随音符当前径向位置（Majdata tapLine：bounceLineScale = clamp01(distance/4.8)）。
                // 弹跳起点（distance=4.8 → scale=1）由窗口前分支的"预过渡"衔接（最后 50ms 原版渐进值
                // 平滑过渡到 1），弹跳期间直接设置——立即跟随、无跳变。
                try
                {
                    var guide = tr.Field("GuideObj").GetValue<NoteGuide>();
                    var gt = guide != null ? guide.transform : tr.Field("NoteGuideTrans").GetValue<Transform>();
                    if (gt != null)
                    {
                        var s = Mathf.Clamp01(distance / BounceJudgeLine);
                        if (s < 0.01f) s = 0.01f;
                        gt.localScale = new Vector3(s, s, 1f);
                    }
                }
                catch
                {
                }

                // 光效/绝赞：挂到 NoteObj 下跟随（音符的一部分，旋转无碍）。
                ReparentBounceEffects(tr);

                SetBounceAlpha(tr, 1f);
                SetBounceChildrenActive(tr, true);
                SetBounceGuideActive(tr, true);
                return;
            }

            // 判定后：恢复可见，外框缩放交给原版每帧继续接管（音符过判定线后外框应随
            // 原版 progress 继续缩小，不能在这里强制设回 1——否则外框停在 1 与下落中的
            // 音符错位）。
            SetBounceAlpha(tr, 1f);
            SetBounceChildrenActive(tr, true);
            SetBounceGuideActive(tr, true);
        }
        catch
        {
        }
    }

    /// <summary>原版 GetNoteYPosition 的返回 = 插值位置 + V_9（音符速度常量偏移）：
    /// V_9 = (EndPos−StartPos) × (−0.008333334) × (GetNoteSpeed()/150 − 1)。
    /// GetNoteSpeed() 读 UserOption 的原始选项值（不经 HS postfix），与原版 IL 一致。</summary>
    private static float GetNoteSpeedOffsetY(NoteBase note, float span)
    {
        try
        {
            var score = Singleton<GamePlayManager>.Instance.GetGameScore(note.MonitorId, -1);
            var speed = score.UserOption.GetNoteSpeed;
            var v8 = Convert.ToInt32(speed) / 150f;
            return span * -0.008333334f * (v8 - 1f);
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>光效/绝赞挂到 NoteObj 下（保持世界位置），随音符弹跳移动（音符的一部分，
    /// 圆形/星形光效旋转无碍）。音符回池复用 Initialize 时层级由原版重新设置。
    /// 外框（NoteGuide 弧形）不挂载——见 bounce 分支的 delta 位移方案。</summary>
    private static void ReparentBounceEffects(Traverse tr)
    {
        try
        {
            var noteObj = tr.Field("NoteObj").GetValue<GameObject>();
            if (noteObj == null) return;
            var noteTrans = noteObj.transform;
            var eff = tr.Field("EffectSprite").GetValue<SpriteRenderer>();
            if (eff != null && eff.transform.parent != noteTrans)
            {
                eff.transform.SetParent(noteTrans, true);
            }
        }
        catch
        {
        }

        try
        {
            var noteObj = tr.Field("NoteObj").GetValue<GameObject>();
            if (noteObj == null) return;
            var noteTrans = noteObj.transform;
            var exObj = tr.Field("ExObj").GetValue<GameObject>();
            if (exObj != null && exObj.transform.parent != noteTrans)
            {
                exObj.transform.SetParent(noteTrans, true);
            }
        }
        catch
        {
        }
    }

    private static void SetBounceAlpha(Traverse tr, float alpha)
    {
        var sr = tr.Field("SpriteRender").GetValue<SpriteRenderer>();
        if (sr != null)
        {
            var c = sr.color;
            c.a = alpha;
            sr.color = c;
        }

        var srEx = tr.Field("SpriteRenderEx").GetValue<SpriteRenderer>();
        if (srEx != null)
        {
            var c = srEx.color;
            c.a = alpha;
            srEx.color = c;
        }
    }

    /// <summary>外框（NoteGuide 判定提示线）的显示开关。弹跳窗口前隐藏（note 弹出前外框不可见），
    /// 弹跳窗口内显示并跟随缩放。SetActive 不会被原版每帧覆盖（原版 SetAlpha 仍执行，恢复后正常）。</summary>
    private static void SetBounceGuideActive(Traverse tr, bool active)
    {
        try
        {
            var guide = tr.Field("GuideObj").GetValue<NoteGuide>();
            if (guide != null)
            {
                guide.gameObject.SetActive(active);
            }
        }
        catch
        {
        }
    }

    /// <summary>光效（Break 脉冲）的显示开关（SetActive）。它的颜色每帧被原版
    /// GetNoteYPosition 重设，用颜色隐藏无效（且会破坏脉冲观感），只能开关 active。
    /// 外框（NoteGuide）不隐藏——原版淡入，无闪现问题。</summary>
    private static void SetBounceChildrenActive(Traverse tr, bool active)
    {
        try
        {
            var eff = tr.Field("EffectSprite").GetValue<SpriteRenderer>();
            if (eff != null)
            {
                eff.gameObject.SetActive(active);
            }
        }
        catch
        {
        }
    }

    /// <summary>弹跳音符激活瞬间（base Initialize）隐藏光效并把光效挂到 NoteObj 下
    /// ——否则激活帧光效会在生成点闪现一瞬。音符贴图的最终隐藏由各子类 Initialize 后置
    /// 完成（子类会重设颜色覆盖 alpha），外框不隐藏（原版淡入）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "Initialize")]
    public static void BounceHideOnInit(NoteBase __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (BounceDurationByNoteIndex.Count == 0 || __instance == null) return;
        if (__instance is TouchHoldC || __instance is TouchNoteB) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float bounce;
            if (!BounceDurationByNoteIndex.TryGetValue(index, out bounce) || bounce <= 0f) return;
            SetBounceChildrenActive(tr, false);
            SetBounceAlpha(tr, 0f);
            ReparentBounceEffects(tr);
        }
        catch
        {
        }
    }

    /// <summary>注册流程内、渲染前把音符贴图 alpha 最终归零（子类 Initialize 会把颜色重置回 1，
    /// 而 UpdateCtrl 注册在 UpdateNotes 之后——不在注册流程内隐藏就会闪一帧）。</summary>
    private static void BounceHideVisualFinal(NoteBase note)
    {
        if (BounceDurationByNoteIndex.Count == 0 || note == null) return;
        try
        {
            var tr = Traverse.Create(note);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float bounce;
            if (!BounceDurationByNoteIndex.TryGetValue(index, out bounce) || bounce <= 0f) return;
            SetBounceAlpha(tr, 0f);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TapNote), "Initialize")]
    public static void BounceHideTapInit(TapNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakNote), "Initialize")]
    public static void BounceHideBreakInit(BreakNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HoldNote), "Initialize")]
    public static void BounceHideHoldInit(HoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakHoldNote), "Initialize")]
    public static void BounceHideBreakHoldInit(BreakHoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StarNote), "Initialize")]
    public static void BounceHideStarInit(StarNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }
}
