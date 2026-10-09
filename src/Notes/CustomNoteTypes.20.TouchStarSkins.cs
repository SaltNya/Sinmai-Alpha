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

// TouchStar、地雷 TouchStar 的贴图与五瓣结构。
public partial class CustomNoteTypes
{

    public static CustomNoteKind GetNoteKind(NoteData note)
    {
        if (note == null) return CustomNoteKind.None;
        return RuntimeCharts.Note(note).NoteKinds.TryGetValue(note.indexNote, out var kind) ? kind : CustomNoteKind.None;
    }

    public static void ApplyTouchStarTexturesToObject(GameObject go, bool isBreak)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        ApplyTouchStarTextures(root != null ? (object)root : go.transform, isBreak);
    }

    private static void ApplyTouchStarTextures(object instance, bool isBreak)
    {
        if (instance == null) return;

        var root = instance as Component;
        if (root == null) return;

        // TouchStar：五瓣星贴图（touch_star / touch_star_break，单瓣贴图）。
        // B 类（A/B/D/E 区）：4 个原瓣物体 + 克隆第 5 瓣 → 5 方向均匀合拢（普通 touch 是四瓣）。
        // C 类（TouchNoteC 无瓣结构）：主 sprite 换五瓣星贴图。
        // 多压（EachFlag，和 touch 一起算 each）时用 touch_star_each / touch_star_break_each；
        // each 贴图缺失时回退普通五瓣。
        var isEach = false;
        try
        {
            isEach = Traverse.Create(root).Field("EachFlag").GetValue<bool>();
        }
        catch
        {
        }

        var key = isBreak ? "touch_star_break" : "touch_star";
        if (isEach)
        {
            var eachKey = isBreak ? "touch_star_break_each" : "touch_star_each";
            if (MineTextures.ContainsKey(eachKey)) key = eachKey;
        }

        if (root is TouchNoteB touchB)
        {
            ReplaceChildSprite(root, "Up", key);
            ReplaceChildSprite(root, "Right", key);
            ReplaceChildSprite(root, "Down", key);
            ReplaceChildSprite(root, "Left", key);
            ExpandToFivePetals(touchB);
        }
        else
        {
            ReplaceFirstSprite(root, key);
        }

        // Just = 判定光环（touch_hit_star，对应原版 UI_NOTES_Touch_Just 的用途）。
        // touch_hit_star 是 320x320，原版 Just 是 128x128 → 按 128/320 = 0.4 缩放，避免合拢时重合。
        ReplaceChildSprite(root, "Just", "touch_hit_star");
        ScaleTouchStarJust(root);

        // 中心点：绝赞（BRSTP）用 touch_break_point（同绝赞 touch），普通保留原版。
        if (isBreak)
        {
            ReplaceChildSprite(root, "Point", "touch_break_point");
        }

        // 两个 border 素材暂时缺失 → 隐藏 Border/Reserve（预留圈/边框）。
        SetChildActive(root, "Border", false);
        SetChildActive(root, "Reserve", false);

        // 白色外框提示（NoticeObject，touch 出现前的提示圈）：放大 1.5 倍更醒目 + 上移一点（幂等）。
        if (ScaledTouchStarNotices.Add(root.gameObject.GetInstanceID()))
        {
            var notice = Traverse.Create(root).Field("NoticeObject").GetValue<GameObject>();
            if (notice != null)
            {
                notice.transform.localScale = notice.transform.localScale * 1.5f;
                notice.transform.localPosition = notice.transform.localPosition + new Vector3(0f, 5f, 0f);
            }
        }
    }

    public static void ApplyMineTouchStarTexturesToObject(GameObject go)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        ApplyMineTouchStarTextures(root != null ? (object)root : go.transform);
    }

    /// <summary>
    /// 地雷 TouchStar（MNSTP）贴图：五瓣星结构同 TouchStar（touch_star_mine 单瓣贴图，
    /// 克隆第 5 瓣、Just 缩放、Notice 放大），但瓣/中心点/外框换成地雷素材：
    ///   瓣（Up/Right/Down/Left）→ touch_star_mine（each 时 touch_star_mine_each）
    ///   判定光环 Just → touch_hit_star_mine（缺失回退 touch_hit_star）
    ///   中心点 Point → touch_point_mine（同地雷 touch）
    ///   外框 Border/Reserve → touch_mine_border_2/3（同地雷 touch）
    ///   提示圈 NoteGuide → mine（ApplyMineGuideTexture）
    /// 不调 ApplyTouchStarTextures（它会把 Border/Reserve 隐藏掉），而是内联五瓣星结构。
    /// </summary>
    private static void ApplyMineTouchStarTextures(object instance)
    {
        if (instance == null) return;
        var root = instance as Component;
        if (root == null) return;

        var isEach = false;
        try
        {
            isEach = Traverse.Create(root).Field("EachFlag").GetValue<bool>();
        }
        catch
        {
        }

        var petalKey = "touch_star_mine";
        if (isEach && MineTextures.ContainsKey("touch_star_mine_each")) petalKey = "touch_star_mine_each";

        // 五瓣星：先贴 4 瓣（clone 继承第 1 瓣贴图），再扩展成 5 瓣。
        if (root is TouchNoteB touchB)
        {
            ReplaceChildSprite(root, "Up", petalKey);
            ReplaceChildSprite(root, "Right", petalKey);
            ReplaceChildSprite(root, "Down", petalKey);
            ReplaceChildSprite(root, "Left", petalKey);
            ExpandToFivePetals(touchB);
        }
        else
        {
            ReplaceFirstSprite(root, petalKey);
        }

        // 判定光环：touch_hit_star_mine（缺失回退原版五瓣光环），照 touchstar 的 0.4 缩放。
        ReplaceChildSprite(root, "Just", MineTextures.ContainsKey("touch_hit_star_mine") ? "touch_hit_star_mine" : "touch_hit_star");
        ScaleTouchStarJust(root);

        // 中心点：地雷 touch 的点。
        ReplaceChildSprite(root, "Point", "touch_point_mine");

        // 外框（Border/Reserve）：同地雷 touch（touch_mine_border_2/3 按序分配）。
        var borderKeys = new[] { "touch_mine_border_2", "touch_mine_border_3" };
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
            }
        }

        // 提示圈：地雷提示圈（mine.png）。
        if (root is NoteBase noteBase)
        {
            ApplyMineGuideTexture(noteBase);
        }

        // 白色外框提示：放大 1.5 倍 + 上移（幂等，同 touchstar）。
        if (ScaledTouchStarNotices.Add(root.gameObject.GetInstanceID()))
        {
            var notice = Traverse.Create(root).Field("NoticeObject").GetValue<GameObject>();
            if (notice != null)
            {
                notice.transform.localScale = notice.transform.localScale * 1.5f;
                notice.transform.localPosition = notice.transform.localPosition + new Vector3(0f, 5f, 0f);
            }
        }
    }

    private static void ScaleTouchStarJust(Component root)
    {
        if (root == null || !ScaledTouchStarJuts.Add(root.gameObject.GetInstanceID())) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.StartsWith("Just", StringComparison.OrdinalIgnoreCase))
            {
                child.localScale = child.localScale * (128f / 320f);
            }
        }
    }

    /// <summary>
    /// 把 touch 的 4 瓣扩展为 5 瓣：克隆第 1 个瓣物体，5 个瓣按 72° 均匀重排。
    /// 游戏多处循环以 DefaultCorlsPos.Length 为边界遍历 ColorsObject / NotePlateSprite /
    /// NotePlateMaterial / TargetSortDiff（SetEach、GetNoteYPosition、Initialize），
    /// 所以这些数组必须同步扩到 5，否则越界崩溃。
    /// 幂等：ColorsObject 已是 5 个时直接返回（对象复用再初始化不会重复克隆）。
    /// </summary>
    private static void ExpandToFivePetals(TouchNoteB note)
    {
        if (note == null) return;
        var t = Traverse.Create(note);
        var colors = t.Field("ColorsObject").GetValue<SpriteRenderer[]>();
        var defaultPos = t.Field("DefaultCorlsPos").GetValue<Vector3[]>();
        var notePlateSprites = t.Field("NotePlateSprite").GetValue<SpriteRenderer[]>();
        var notePlateMaterials = t.Field("NotePlateMaterial").GetValue<Material[]>();
        var targetSortDiff = t.Field("TargetSortDiff").GetValue<int[]>();
        if (colors == null || colors.Length != 4 || defaultPos == null || defaultPos.Length != 4) return;

        var p0 = defaultPos[0];
        var radius = p0.magnitude;
        var baseAngle = Mathf.Atan2(p0.y, p0.x);

        // 克隆第 1 个瓣（继承已替换的 touch_star 贴图）
        var cloneGo = UnityEngine.Object.Instantiate(colors[0].gameObject, colors[0].transform.parent);
        cloneGo.SetActive(true);
        var cloneSr = cloneGo.GetComponent<SpriteRenderer>();
        if (cloneSr == null)
        {
            UnityEngine.Object.Destroy(cloneGo);
            return;
        }

        var newColors = new SpriteRenderer[5];
        var newPos = new Vector3[5];
        // 非 null 一律扩容为 5（缺项补 clone/默认）：游戏多处循环以
        // DefaultCorlsPos.Length=5 为边界遍历这些数组，Length≠4（0/2/3/5 等）
        // 若不扩容 → OOB 崩溃。Length==4 复制前 4 项，Length<4 缺失项留默认。
        var newNotePlateSprites = notePlateSprites != null ? new SpriteRenderer[5] : null;
        var newNotePlateMaterials = notePlateMaterials != null ? new Material[5] : null;
        var newTargetSortDiff = targetSortDiff != null ? new int[5] : null;

        for (var i = 0; i < 5; i++)
        {
            var angle = baseAngle + i * 72f * Mathf.Deg2Rad;
            newPos[i] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, p0.z);

            SpriteRenderer sr;
            float origAngle;
            if (i < 4)
            {
                sr = colors[i];
                origAngle = Mathf.Atan2(defaultPos[i].y, defaultPos[i].x);
                if (newNotePlateSprites != null && i < notePlateSprites.Length) newNotePlateSprites[i] = notePlateSprites[i];
                if (newNotePlateMaterials != null && i < notePlateMaterials.Length) newNotePlateMaterials[i] = notePlateMaterials[i];
                if (newTargetSortDiff != null && i < targetSortDiff.Length) newTargetSortDiff[i] = targetSortDiff[i];
            }
            else
            {
                sr = cloneSr;
                origAngle = Mathf.Atan2(defaultPos[0].y, defaultPos[0].x);
                if (newNotePlateSprites != null) newNotePlateSprites[i] = cloneSr;
                if (newNotePlateMaterials != null) newNotePlateMaterials[i] = cloneSr.material;
                if (newTargetSortDiff != null) newTargetSortDiff[i] = targetSortDiff[0];
            }

            // 瓣贴图朝向跟随新方向：原 z 旋转 + (新方向 - 原方向)
            var euler = sr.transform.localEulerAngles;
            var delta = (angle - origAngle) * Mathf.Rad2Deg;
            sr.transform.localRotation = Quaternion.Euler(euler.x, euler.y, euler.z + delta);

            // 瓣贴图（touch_star 128x192 / touch_star_each 等）比原版 touch（112x82）大 →
            // 按面积根号比缩放（sqrt((112×82)/(128×192)) ≈ 0.61，取 0.6），
            // 避免五瓣合拢时重叠成一团。each 贴图尺寸可能不同，按当前贴图实际尺寸自适应，
            // 与另外两张（touch_star / touch_star_break）保持一致的缩放规则。
            var sprite = sr.sprite;
            if (sprite != null && sprite.rect.width > 1f && sprite.rect.height > 1f)
            {
                var areaScale = Mathf.Sqrt((112f * 82f) / (sprite.rect.width * sprite.rect.height));
                sr.transform.localScale = sr.transform.localScale * areaScale;
            }
            else
            {
                sr.transform.localScale = sr.transform.localScale * 0.6f;
            }

            newColors[i] = sr;
        }

        t.Field("ColorsObject").SetValue(newColors);
        t.Field("DefaultCorlsPos").SetValue(newPos);
        if (newNotePlateSprites != null) t.Field("NotePlateSprite").SetValue(newNotePlateSprites);
        if (newNotePlateMaterials != null) t.Field("NotePlateMaterial").SetValue(newNotePlateMaterials);
        if (newTargetSortDiff != null) t.Field("TargetSortDiff").SetValue(newTargetSortDiff);
    }

    private static void SetChildActive(Component root, string childName, bool active)
    {
        if (root == null) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.StartsWith(childName, StringComparison.OrdinalIgnoreCase))
            {
                child.gameObject.SetActive(active);
            }
        }
    }
}
