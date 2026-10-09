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

// 地雷滑条箭头、星星及 WiFi 轨道贴图。
public partial class CustomNoteTypes
{

    private static void ApplyMineArrowTexture(SpriteRenderer sr)
    {
        if (sr == null || !MineTextures.TryGetValue("slide_mine", out var texture)) return;
        sr.sprite = CreateSpriteFromTexture("slide_mine", texture, sr.sprite);
    }

    private static void ApplyMineBreakArrowTexture(BreakSlide bs)
    {
        if (bs == null) return;
        if (bs.SpriteRender != null && MineTextures.TryGetValue("slide_break_mine", out var texture))
        {
            bs.SpriteRender.sprite = CreateSpriteFromTexture("slide_break_mine", texture, bs.SpriteRender.sprite);
        }

        // 光效层（EffectSprite，private）：贴地雷光效 + 标记（SetSprite 会重置，postfix 重贴）。
        var effect = Traverse.Create(bs).Field("EffectSprite").GetValue<SpriteRenderer>();
        if (effect != null && MineTextures.TryGetValue("slide_break_eff_mine", out var effTexture))
        {
            MineBreakSlides.Add(bs);
            effect.sprite = CreateSpriteFromTexture("slide_break_eff_mine", effTexture, effect.sprite);
        }
    }

    /// <summary>
    /// 把 slide 的轨道箭头重新贴成地雷贴图。
    /// 原因：SlideRoot.SetEach（Initialize 内部调用）会给箭头赋原版 NormalSlide/EachSlide sprite，
    /// 覆盖地雷箭头池的贴图，所以 MineSlideRoot.SetEach override 里要重新贴。
    /// </summary>
    public static void ApplyMineArrowTextures(SlideRoot slide)
    {
        if (slide == null) return;

        var spriteRenders = Traverse.Create(slide).Field("_spriteRenders").GetValue<List<SpriteRenderer>>();
        if (spriteRenders != null)
        {
            foreach (var sr in spriteRenders)
            {
                ApplyMineArrowTexture(sr);
            }
        }

        var breakRenders = Traverse.Create(slide).Field("_breakSpriteRenders").GetValue<List<BreakSlide>>();
        if (breakRenders != null)
        {
            foreach (var bs in breakRenders)
            {
                ApplyMineBreakArrowTexture(bs);
            }
        }
    }

    /// <summary>
    /// 给 slide 的移动星（SlideRoot 内部 _starNote / _breakStarNote）贴地雷星贴图。
    /// 只精准贴这两个星对象（不遍历子物体），普通星用 star_mine、break 星用 star_break_mine。
    /// </summary>
    public static void ApplyMineSlideStarTextures(SlideRoot slide)
    {
        if (slide == null) return;

        var traverse = Traverse.Create(slide);
        var star = traverse.Field("_starNote").GetValue<GameObject>();
        if (star != null)
        {
            var sr = star.GetComponent<SpriteRenderer>();
            if (sr != null) ApplyMineStarTexture(sr, "star_mine");
        }

        var breakStar = traverse.Field("_breakStarNote").GetValue<GameObject>();
        if (breakStar != null)
        {
            var bs = breakStar.GetComponent<BreakStarNote>();
            var sr = bs != null ? bs.GetSpriteRender() : breakStar.GetComponent<SpriteRenderer>();
            if (sr != null) ApplyMineStarTexture(sr, "star_break_mine");
            // 内部 break 星的 EffectSprite（绝赞光效层）也地雷化——SetSlideStar 在 base.Initialize
            // 里已执行（此时还没登记），这里直接替换；后续 SetSlideStar 由 postfix 兜底。
            if (bs != null) MineifyBreakStarEffect(bs);
        }
    }

    private static void ApplyMineStarTexture(SpriteRenderer sr, string textureKey)
    {
        if (sr == null || !MineTextures.TryGetValue(textureKey, out var texture)) return;
        sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
    }

    /// <summary>
    /// 给 fan slide（Wi-Fi 扇形滑）贴地雷贴图（用户提供的新贴图）：
    ///   - _spriteLines（11 组 × 2，分 LR）→ slide_fun_mine_00..10（同组 i/2 同号）
    ///   - _effectSprites（11 组 × 2，光效）→ slide_fun_eff_mine_00..10（同组 i/2 同号）
    ///   - _spriteStars / _baseSpriteStars（各 3）星 → star_mine / star_break_mine（按 BreakFlag）
    /// 注意：原版线是白色贴图 + _laneColor 染色（UpdateAlpha 每帧 set_color），
    /// 地雷线是彩色贴图，MineSlideFan 已 override UpdateAlpha 把 RGB 重置为白（保留 alpha 动画）。
    /// </summary>
    public static void ApplyMineFanSlideTextures(SlideFan fan)
    {
        if (fan == null) return;

        var traverse = Traverse.Create(fan);

        var lines = traverse.Field("_spriteLines").GetValue<SpriteRenderer[]>();
        if (lines != null)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i] == null) continue;
                // 素材文件名是两位数字：slide_fun_mine_00..10
                var key = "slide_fun_mine_" + (i / 2 % 11).ToString("D2");
                if (!MineTextures.TryGetValue(key, out var texture))
                {
                    if (!_loggedMissingFanTexture)
                    {
                        _loggedMissingFanTexture = true;
                        MelonLogger.Warning($"[CustomNoteType] Missing fan textures: '{key}'（请确认 Sinmai-Alpha/CustomNoteTypes 里有 slide_fun_mine_00..10.png）");
                    }

                    continue;
                }

                lines[i].sprite = CreateSpriteFromTexture(key, texture, lines[i].sprite);
            }
        }

        var effects = traverse.Field("_effectSprites").GetValue<SpriteRenderer[]>();
        if (effects != null)
        {
            for (var i = 0; i < effects.Length; i++)
            {
                if (effects[i] == null) continue;
                var key = "slide_fun_eff_mine_" + (i / 2 % 11).ToString("D2");
                if (MineTextures.TryGetValue(key, out var texture))
                {
                    effects[i].sprite = CreateSpriteFromTexture(key, texture, effects[i].sprite);
                }
            }
        }

        var isBreak = traverse.Field("BreakFlag").GetValue<bool>();
        var starKey = isBreak ? "star_break_mine" : "star_mine";
        var stars = traverse.Field("_spriteStars").GetValue<SpriteRenderer[]>();
        if (stars != null)
        {
            foreach (var sr in stars)
            {
                ApplyMineStarTexture(sr, starKey);
            }
        }

        var baseStars = traverse.Field("_baseSpriteStars").GetValue<SpriteRenderer[]>();
        if (baseStars != null)
        {
            foreach (var sr in baseStars)
            {
                ApplyMineStarTexture(sr, starKey);
            }
        }

        // fan 的星（SlideLaneStar = StarNote/BreakStarNote，继承 NoteBase）自带 NoteGuide 提示圈
        // （蓝色外框 + 判定前橙色闪光）——同样地雷化。
        ApplyFanStarGuideTextures(traverse);

        // fan 的 break 星（_breakStarObjs，原版 BreakStarNote 实例）的 EffectSprite（绝赞光效层）
        // 也要地雷化，否则单星判定时闪橙光。SetSlideStar 在 base.Initialize 里已执行
        // （此时还没登记），这里直接替换；后续 SetSlideStar 由 postfix 兜底。
        foreach (var field in new[] { "_starObjs", "_breakStarObjs", "_baseStarObjs" })
        {
            var objs = traverse.Field(field).GetValue<GameObject[]>();
            if (objs == null) continue;
            foreach (var go in objs)
            {
                if (go == null) continue;
                var breakStar = go.GetComponent<BreakStarNote>();
                if (breakStar != null) MineifyBreakStarEffect(breakStar);
            }
        }
    }

    // 把 fan 星对象（_starObjs / _breakStarObjs / _baseStarObjs）上的 NoteGuide 提示圈地雷化。
    private static void ApplyFanStarGuideTextures(Traverse fanTraverse)
    {
        foreach (var field in new[] { "_starObjs", "_breakStarObjs", "_baseStarObjs" })
        {
            var objs = fanTraverse.Field(field).GetValue<GameObject[]>();
            if (objs == null) continue;
            foreach (var go in objs)
            {
                if (go == null) continue;
                var noteBase = go.GetComponent<NoteBase>();
                if (noteBase != null)
                {
                    ApplyMineGuideTexture(noteBase);
                }
            }
        }
    }
}
