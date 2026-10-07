using System.Collections.Generic;
using HarmonyLib;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class MineGuideSpriteLease
    {
        public Sprite Original;
        public Sprite Applied;
    }

    private static readonly Dictionary<NoteGuide, Dictionary<SpriteRenderer, MineGuideSpriteLease>> MineGuideSpriteLeases = new();

    private static void ApplyMineGuideSprite(NoteGuide guide, SpriteRenderer renderer, Texture2D texture)
    {
        if (guide == null || renderer == null || renderer.sprite == null) return;
        if (!MineGuideSpriteLeases.TryGetValue(guide, out var sprites))
            MineGuideSpriteLeases[guide] = sprites = new Dictionary<SpriteRenderer, MineGuideSpriteLease>();
        if (!sprites.TryGetValue(renderer, out var lease))
            sprites[renderer] = lease = new MineGuideSpriteLease { Original = renderer.sprite };
        else if (renderer.sprite != lease.Applied)
            // SetColor has just assigned a native guide sprite, or another
            // visual owner changed it. Repeated mine application must not
            // replace the remembered native sprite with our own mine sprite.
            lease.Original = renderer.sprite;
        var applied = CreateSpriteFromTexture("mine", texture, renderer.sprite);
        renderer.sprite = applied;
        lease.Applied = applied;
    }

    private static void RestoreMineGuideSprites(NoteGuide guide)
    {
        if (ReferenceEquals(guide, null)) return;
        MineGuides.Remove(guide);
        if (!MineGuideSpriteLeases.TryGetValue(guide, out var sprites)) return;
        MineGuideSpriteLeases.Remove(guide);
        foreach (var pair in sprites)
            // A later visual owner keeps its change. We only return sprites
            // which still belong to this lease, including inactive each arcs.
            if (pair.Key != null && ReferenceEquals(pair.Key.sprite, pair.Value.Applied))
                pair.Key.sprite = pair.Value.Original;
    }

    private static void RestoreAllMineGuideSprites()
    {
        foreach (var guide in new List<NoteGuide>(MineGuideSpriteLeases.Keys)) RestoreMineGuideSprites(guide);
        MineGuides.Clear();
    }

    [HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static class MineGuideForceCollectPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameCtrl __instance)
        {
            if (__instance == null) return;
            // ForceNoteCollect directly hides/reparents guides instead of
            // calling ReturnToBase. Scope cleanup to this native controller;
            // the other monitor's active mine guides retain their leases.
            var guides = Traverse.Create(__instance).Field("_guideObjectList").GetValue<List<NoteGuide>>();
            if (guides != null) foreach (var guide in guides) RestoreMineGuideSprites(guide);
        }
    }
}
