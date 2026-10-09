using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class TouchReserveMonitor
    {
        public int Index;
    }

    private static readonly ConditionalWeakTable<TouchReserve, TouchReserveMonitor> TouchReserveMonitors = new();

    [HarmonyPatch(typeof(TouchReserve), "Initialize")]
    public static class TouchReserveMonitorPatch
    {
        [HarmonyPrefix]
        public static void Prefix(TouchReserve __instance, int monitorIndex)
            => TouchReserveMonitors.GetOrCreateValue(__instance).Index = monitorIndex;
    }

    [HarmonyPatch(typeof(TouchReserve), "DispUpdate")]
    public static class TouchReserveMineSkinPatch
    {
        [HarmonyPostfix]
        public static void Postfix(TouchReserve __instance, List<TouchReserve.ReserveData> ___liveIndex,
            SpriteRenderer[] ____reserveSimboleSprite)
        {
            TrackReserveVisuals(__instance, ___liveIndex, ____reserveSimboleSprite);
            if (___liveIndex.Count == 0 || !TouchReserveMonitors.TryGetValue(__instance, out var monitor) ||
                !FeaturesForMonitor(monitor.Index)) return;

            // These are the upcoming notes, not the current Touch. Reserve UI
            // is shared per sensor and rebuilt on both enqueue and DeathNote.
            // Native DispUpdate restores normal/each sprites before this patch;
            // never mutate its shared sprite arrays or a pooled note's children.
            var kinds = RuntimeMonitor(monitor.Index).NoteKinds;
            for (var i = 0; i < ____reserveSimboleSprite.Length && i < ___liveIndex.Count; i++)
            {
                if (!kinds.TryGetValue(___liveIndex[i].Index, out var kind)) continue;
                var prefix = kind switch
                {
                    CustomNoteKind.Mine or CustomNoteKind.MineTouchStar => "touch_mine_border_",
                    CustomNoteKind.MineTouchBreak => "touch_break_mine_border_",
                    _ => null
                };
                var renderer = ____reserveSimboleSprite[i];
                if (prefix == null || renderer == null) continue;
                var key = prefix + (i + 2);
                if (MineTextures.TryGetValue(key, out var texture))
                    renderer.sprite = CreateSpriteFromTexture(key, texture, renderer.sprite);
            }
        }
    }
}
