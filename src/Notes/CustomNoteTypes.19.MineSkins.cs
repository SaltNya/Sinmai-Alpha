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

// 精灵生成、地雷引导线、特效及 Touch 绝赞皮肤。
public partial class CustomNoteTypes
{

    private static Sprite CreateSpriteFromTexture(string textureKey, Texture2D texture, Sprite original)
    {
        // ppu 必须沿用原 sprite：固定 1f 会让细长贴图（如 hold 条）尺寸/拉伸全错。
        var ppu = original != null ? original.pixelsPerUnit : 1f;
        // 缓存 key 必须包含完整几何信息：图集 sprite 的 name 可能是空的，
        // 只按 name 缓存会让不同子物体（不同 rect/border）串用同一个 Sprite。
        var cacheKey = textureKey + "|" +
            (original != null
                ? original.name + "|" + original.rect + "|" + original.pivot + "|" + original.border + "|" + ppu
                : "null");
        if (SpriteCache.TryGetValue(cacheKey, out var cached))
        {
            RememberSpriteModel(cached, original);
            return cached;
        }

        var pivot = original != null
            ? new Vector2(original.pivot.x / original.rect.width, original.pivot.y / original.rect.height)
            : new Vector2(0.5f, 0.5f);
        // 9-slice border 必须按新贴图尺寸等比换算：原版 hold 条是 Sliced 渲染（中间段拉伸），
        // border 是相对原 sprite 的像素值，直接套用会切错位置导致整条被拉伸。
        var border = Vector4.zero;
        if (original != null && original.border != Vector4.zero)
        {
            var scaleX = texture.width / original.rect.width;
            var scaleY = texture.height / original.rect.height;
            border = new Vector4(
                original.border.x * scaleX,
                original.border.y * scaleY,
                original.border.z * scaleX,
                original.border.w * scaleY);
        }

        // FullRect 不做 alpha 裁剪，避免 Tight 把细长贴图裁出奇怪的形状。
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, ppu, 0, SpriteMeshType.FullRect, border);
        SpriteCache[cacheKey] = sprite;
        RememberSpriteModel(sprite, original);
        return sprite;
    }

    // 把 NoteGuide（提示圈，共享池对象）替换成地雷提示圈（mine.png）。
    // GuideObj 是共享池对象；独立记录主体和 Each 子圈的原贴图，回池时归还。
    private static void ApplyMineGuideTexture(NoteBase noteBase)
    {
        try
        {
            var guide = Traverse.Create(noteBase).Field("GuideObj").GetValue<NoteGuide>();
            if (!MineTextures.TryGetValue("mine", out var texture)) return;

            if (guide == null) return;

            MineGuides.Add(guide);
            foreach (var sr in guide.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr.sprite == null) continue;
                ApplyMineGuideSprite(guide, sr, texture);
            }
        }
        catch
        {
        }
    }

    /// <summary>绝赞 touchhold 的 break 风格贴图（BRTHO 绝赞 touchhold / MBTHO 地雷绝赞 touchhold 共用）：
    /// Red/Yellow/Green/Blue → touchhold_break_0..3，外框 HoldGauge → touchhold_break。</summary>
    public static void ApplyBreakTouchHoldTextures(Component root)
    {
        if (root == null) return;
        ReplaceChildSprite(root, "Red", "touchhold_break_0");
        ReplaceChildSprite(root, "Yellow", "touchhold_break_1");
        ReplaceChildSprite(root, "Green", "touchhold_break_2");
        ReplaceChildSprite(root, "Blue", "touchhold_break_3");
        ReplaceChildSprite(root, "HoldGauge", "touchhold_break");
        ReplaceChildSprite(root, "Point", root is MineTouchHoldC ? "touch_break_point_mine" : "touch_break_point");
        ApplyBreakTouchHoldProgress(root);
    }

    /// <summary>把渲染器贴成地雷光效贴图（绝赞光芒替换用）。</summary>
    public static void ApplyMineEffectTexture(SpriteRenderer sr, string textureKey)
    {
        if (sr == null || !MineTextures.TryGetValue(textureKey, out var texture)) return;
        sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
    }

    /// <summary>登记一颗地雷 break 星并立刻替换其 EffectSprite（SetMulti/SetSlideStar 可能在登记前已执行）。
    /// 单星（MultiSlide=false）用 star_break_eff_mine，双星用 star_break_double_eff_mine。</summary>
    internal static void MineifyBreakStarEffect(BreakStarNote star)
    {
        if (star == null) return;
        MineBreakStars.Add(star);
        var effect = Traverse.Create(star).Field("EffectSprite").GetValue<SpriteRenderer>();
        var multi = Traverse.Create(star).Field("MultiSlide").GetValue<bool>();
        ApplyMineEffectTexture(effect, multi ? "star_break_double_eff_mine" : "star_break_eff_mine");
    }

    /// <summary>BreakSlide.SetSprite 会把 EffectSprite 重置为原版 BreakSlideEff，地雷箭头重贴。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakSlide), "SetSprite")]
    public static void BreakSlideSetSpritePostfix(BreakSlide __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineBreakSlides.Contains(__instance)) return;
        var effect = Traverse.Create(__instance).Field("EffectSprite").GetValue<SpriteRenderer>();
        ApplyMineEffectTexture(effect, "slide_break_eff_mine");
    }

    /// <summary>
    /// BreakStarNote.SetMulti 非虚：地雷 break 星的 EffectSprite（绝赞光效）替换成地雷光效。
    /// Initialize 对单星/双星都会调 SetMulti（multiFlag = child.Count >= 2），所以两种都要处理：
    /// 双星 → star_break_double_eff_mine，单星 → star_break_eff_mine。
    /// 判断用登记表（slide/fan 内部星是原版 BreakStarNote 实例，不是 MineBreakStarNote）。
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakStarNote), "SetMulti")]
    public static void BreakStarSetMultiPostfix(BreakStarNote __instance, bool multiFlag)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineBreakStars.Contains(__instance)) return;
        var effect = Traverse.Create(__instance).Field("EffectSprite").GetValue<SpriteRenderer>();
        ApplyMineEffectTexture(effect, multiFlag ? "star_break_double_eff_mine" : "star_break_eff_mine");
    }

    /// <summary>BreakStarNote.SetSlideStar 非虚：地雷单星（独立池或 slide/fan 内部星）的
    /// 绝赞光效替换成 star_break_eff_mine。内部星是原版 BreakStarNote，用登记表判断。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakStarNote), "SetSlideStar")]
    public static void BreakStarSetSlideStarPostfix(BreakStarNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineBreakStars.Contains(__instance)) return;
        var effect = Traverse.Create(__instance).Field("EffectSprite").GetValue<SpriteRenderer>();
        ApplyMineEffectTexture(effect, "star_break_eff_mine");
    }

    /// <summary>NoteGuide.SetColor 会把提示圈主体 sprite 重置回原版，地雷化过的 guide 重新贴回。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteGuide), "SetColor")]
    public static void NoteGuideSetColorPostfix(NoteGuide __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineGuides.Contains(__instance)) return;
        if (!MineTextures.TryGetValue("mine", out var texture)) return;

        var sr = Traverse.Create(__instance).Field("_spriteRender").GetValue<SpriteRenderer>();
        if (sr != null)
        {
            ApplyMineGuideSprite(__instance, sr, texture);
        }
    }

    /// <summary>GuideObj 是共享池对象（_guideObjectList）：地雷 note 结束时 guide 归还池，
    /// 普通 note 复用时不能再贴回 mine。ReturnToBase 即归还点，从登记表移除。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteGuide), "ReturnToBase")]
    public static void NoteGuideReturnToBasePostfix(NoteGuide __instance)
    {
        RestoreMineGuideSprites(__instance);
    }

    private static bool ReplaceChildSprite(Component root, string childName, string textureKey)
    {
        if (root == null || !MineTextures.TryGetValue(textureKey, out var texture)) return false;

        var replaced = false;
        foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
        {
            // 实际 GameObject 名字会带 (Clone)，所以用 StartsWith 匹配。
            if (sr.gameObject.name.StartsWith(childName, StringComparison.OrdinalIgnoreCase))
            {

                sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
                replaced = true;
            }
        }

        return replaced;
    }

    private static void OffsetChildPosition(Component root, string childName, Vector3 offset)
    {
        if (root == null) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.StartsWith(childName, StringComparison.OrdinalIgnoreCase))
            {
                child.localPosition += offset;
            }
        }
    }

    private static bool ReplaceFirstSprite(Component root, string textureKey)
    {
        if (root == null || !MineTextures.TryGetValue(textureKey, out var texture)) return false;

        foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.sprite != null)
            {
                sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
                return true;
            }
        }

        return false;
    }

    public static void ApplyMineTexturesToObject(GameObject go)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        if (root == null)
        {
            // 正常情况下 Note 组件一定在根上；走到这里说明类型或结构异常，记一条日志便于排查。
            MelonLogger.Warning($"[CustomNoteType] ApplyMineTexturesToObject: no NoteBase on '{go.name}', using Transform fallback");
            ApplyMineTextures(go.transform, null);
            return;
        }

        ApplyMineTextures(root, null);
    }

    public static void ApplyTouchBreakTexturesToObject(GameObject go)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        ApplyTouchBreakTextures(root != null ? (object)root : go.transform);
    }

    private static void ApplyMineTextures(object instance, NoteData note)
    {
        if (instance == null) return;

        var root = instance as Component;
        if (root == null) return;

        // NoteGuide（提示圈）是独立对象（NoteBase.GuideObj，从共享池分配），
        // 不在 note 的子物体树里——必须单独替换，否则地雷键保留原版蓝色提示圈
        // 以及判定前的橙色闪光动画（"蓝色外框 + 橙色动效闪光"的来源）。
        if (root is NoteBase noteBase)
        {
            ApplyMineGuideTexture(noteBase);
        }

        var typeName = instance.GetType().Name;
        var isBreak = typeName.Contains("Break");
        var replaced = false;
        string fallbackKey = null;

        // 所有地雷 Note 的提示圈都换成 Mine.png
        replaced |= ReplaceChildSprite(root, "NoteGuide", "mine");

        if (typeName.Contains("BreakNote"))
        {
            // BreakNote = Break Tap
            fallbackKey = "tap_break_mine";
            replaced |= ReplaceChildSprite(root, "Break", fallbackKey);
        }
        else if (typeName.Contains("Tap"))
        {
            fallbackKey = isBreak ? "tap_break_mine" : "tap_mine";
            replaced |= ReplaceChildSprite(root, isBreak ? "Break" : "Tap", fallbackKey);
        }
        else if (typeName.Contains("TouchHold"))
        {
            // TouchHold 要在 Hold 之前判断，否则会被当成 Hold。
            fallbackKey = isBreak ? "touch_break_mine" : "touch_mine";
            replaced |= ReplaceChildSprite(root, "Red", "touchhold_mine_0");
            replaced |= ReplaceChildSprite(root, "Yellow", "touchhold_mine_1");
            replaced |= ReplaceChildSprite(root, "Green", "touchhold_mine_2");
            replaced |= ReplaceChildSprite(root, "Blue", "touchhold_mine_3");
            replaced |= ReplaceChildSprite(root, "HoldGauge", isBreak ? "touchhold_break_mine" : "touchhold_off");
            replaced |= ReplaceChildSprite(root, "Point", isBreak ? "touch_break_point_mine" : "touch_point_mine");
        }
        else if (typeName.Contains("Hold"))
        {
            fallbackKey = isBreak ? "hold_break_mine" : "hold_mine";
            replaced |= ReplaceChildSprite(root, "Hold", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Effect", isBreak ? "hold_break_mine_on" : "hold_mine_on");
            replaced |= ReplaceChildSprite(root, "HoldEnd", "hold_mine_end");
        }
        else if (typeName.Contains("Star"))
        {
            fallbackKey = isBreak ? "star_break_mine" : "star_mine";
            replaced |= ReplaceChildSprite(root, "Star", fallbackKey);
            replaced |= ReplaceChildSprite(root, "BreakStar", fallbackKey);
        }
        else if (typeName.Contains("Slide") || typeName.Contains("Wifi"))
        {
            fallbackKey = isBreak ? "slide_break_mine" : "slide_mine";
            replaced |= ReplaceChildSprite(root, "SlideLaneStar", isBreak ? "star_break_mine" : "star_mine");
            replaced |= ReplaceChildSprite(root, "SlideArrow", fallbackKey);
            replaced |= ReplaceChildSprite(root, "BreakSlideArrow", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Effect", fallbackKey);
        }
        else if (typeName.Contains("Touch"))
        {
            fallbackKey = isBreak ? "touch_break_mine" : "touch_mine";
            replaced |= ReplaceChildSprite(root, "Point", isBreak ? "touch_break_point_mine" : "touch_point_mine");
            replaced |= ReplaceChildSprite(root, "Up", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Right", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Down", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Left", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Just", fallbackKey);

            // 临时补偿：Down 看起来偏高，往下挪一点。
            OffsetChildPosition(root, "Down", new Vector3(0f, -3f, 0f));

            // 地雷 touch 外框（Border/Reserve）：普通地雷 touch（MNTTP）用 touch_mine_border_2/3，
            // 绝赞地雷 touch（MBTTP，MineTouchNoteB.IsMineTouchBreak）用 touch_break_mine_border_2/3。
            // 此前漏掉外框替换 → 地雷键仍显示普通 touch 的 border。
            var isMineBreak = root is MineTouchNoteB mineTouch && mineTouch.IsMineTouchBreak;
            var borderKeys = isMineBreak
                ? new[] { "touch_break_mine_border_2", "touch_break_mine_border_3" }
                : new[] { "touch_mine_border_2", "touch_mine_border_3" };
            var borderRenderers = root.GetComponentsInChildren<SpriteRenderer>(true)
                .Where(sr => sr.sprite != null &&
                             (sr.gameObject.name.Contains("Border") || sr.gameObject.name.Contains("Reserve")))
                .ToArray();
            for (var i = 0; i < borderRenderers.Length; i++)
            {
                var key = borderKeys[Math.Min(i, borderKeys.Length - 1)];
                if (MineTextures.TryGetValue(key, out var texture))
                {
                    borderRenderers[i].sprite = CreateSpriteFromTexture(key, texture, borderRenderers[i].sprite);
                    replaced = true;
                }
            }
        }

        // 如果精确匹配没替换到任何东西，至少把第一个 SpriteRenderer 换掉，避免完全没效果。
        if (!replaced && fallbackKey != null)
        {
            ReplaceFirstSprite(root, fallbackKey);
        }
    }

    private static void ApplyTouchBreakTextures(object instance)
    {
        if (instance == null) return;

        var root = instance as Component;
        if (root == null) return;

        // 绝赞touch：主体用 touch_break，点用 touch_break_point。
        // 不替换 Just，避免闪橙色光芒。
        ReplaceChildSprite(root, "Point", "touch_break_point");
        ReplaceChildSprite(root, "Up", "touch_break");
        ReplaceChildSprite(root, "Right", "touch_break");
        ReplaceChildSprite(root, "Down", "touch_break");
        ReplaceChildSprite(root, "Left", "touch_break");

        // 如果有 Border/Reserve 子物体，分别尝试 border_2 / border_3。
        var borderRenderers = root.GetComponentsInChildren<SpriteRenderer>(true)
            .Where(sr => sr.sprite != null &&
                         (sr.gameObject.name.Contains("Border") || sr.gameObject.name.Contains("Reserve")))
            .ToArray();

        for (var i = 0; i < borderRenderers.Length; i++)
        {
            var key = i == 0 ? "touch_break_border_2" : "touch_break_border_3";
            if (MineTextures.TryGetValue(key, out var texture))
            {
                borderRenderers[i].sprite = CreateSpriteFromTexture(key, texture, borderRenderers[i].sprite);
            }
        }
    }
}
