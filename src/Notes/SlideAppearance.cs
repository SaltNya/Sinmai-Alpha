using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SinmaiAlpha.Notes.Libs;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class SlideAppearanceLease
    {
        public float Head, Launch, Speed, Option;
        public bool Touch;
        public SpriteRenderer[] Body, Stars;
        public GameObject[] StarObjects;
        public readonly Dictionary<SpriteRenderer, (float Native, float Applied)> Alphas = new();
    }
    private static readonly ConditionalWeakTable<SlideRoot, SlideAppearanceLease> SlideAppearances = new();
    // DefaultMsec already comes from this player's native Sinmai note speed,
    // including legacy per-note multipliers. HS scales this velocity once;
    // SV scales elapsed scroll separately. Do not reinterpret the option label
    // using a preview player's speed formula or a different game's option table.
    private static float RingViewSpeed(float nativeMsec)
        => SpawnRadiusSpan * 1000 / Math.Max(nativeMsec, .001f);
    private static bool IsTouchSlideAppearance(NoteData note)
    {
        if (note is not CustomSlideNoteData custom) return false;
        // Numeric/D slides and wifi use SlideDrop/WifiDrop. Cross-area SC uses
        // TouchSlideDrop. Legacy numeric SC (e.g. 3Q5K7) keeps its native style.
        var code = custom.SlideCode;
        return code != null && (code.StartsWith("DG2:") || !code.StartsWith("DG1:") && !code.StartsWith("DF1:") &&
            (code.IndexOf('E') >= 0 || code.IndexOf('D') >= 0 || code.IndexOf('F') >= 0 || code.IndexOf('B') >= 0 || code.IndexOf('C') >= 0));
    }
    private static void RegisterSlideAppearance(SlideRoot owner, NoteData note)
    {
        RestoreSlideBodyAppearance(owner);
        SlideAppearances.Remove(owner);
        var hs = ResolvePlayableHs(note);
        var referenceStyle = SpeedClass(note).SlideAppearance;
        if (referenceStyle == 0 && Math.Abs(hs - 1) < .00001f) return;
        var fields = Traverse.Create(owner);
        var lease = new SlideAppearanceLease
        {
            Head = fields.Field("AppearMsec").GetValue<float>(),
            Launch = fields.Field("StarLaunchMsec").GetValue<float>(),
            Speed = RingViewSpeed(fields.Field("DefaultMsec").GetValue<float>()) * hs,
            Option = ((int)Singleton<GamePlayManager>.Instance.GetGameScore(owner.MonitorId).UserOption.SlideSpeed - 10) * .1f,
            Touch = referenceStyle != 0 ? referenceStyle == 2 : IsTouchSlideAppearance(note)
        };
        if (owner is SlideFan)
        {
            lease.Body = fields.Field("_spriteLines").GetValue<SpriteRenderer[]>();
            lease.Stars = fields.Field("_baseSpriteStars").GetValue<SpriteRenderer[]>();
            lease.StarObjects = fields.Field("_baseStarObjs").GetValue<GameObject[]>();
        }
        else
        {
            var count = fields.Field("_dispLaneNum").GetValue<int>();
            lease.Body = fields.Field("BreakFlag").GetValue<bool>()
                ? fields.Field("_breakSpriteRenders").GetValue<List<BreakSlide>>().Take(count).Select(s => s.SpriteRender).ToArray()
                : fields.Field("_spriteRenders").GetValue<List<SpriteRenderer>>().Take(count).ToArray();
            lease.Stars = new[] { fields.Field("BaseSpriteRender").GetValue<SpriteRenderer>() };
            lease.StarObjects = new[] { fields.Field("_baseStarNote").GetValue<GameObject>() };
        }
        SlideAppearances.Add(owner, lease);
    }
    private static void RestoreSlideBodyAppearance(SlideRoot owner)
    {
        if (!SlideAppearances.TryGetValue(owner, out var lease)) return;
        foreach (var entry in lease.Alphas)
            if (entry.Key != null && Math.Abs(entry.Key.color.a - entry.Value.Applied) < .00001f)
            { var color = entry.Key.color; color.a = entry.Value.Native; entry.Key.color = color; }
        // Native NoteCheck may have hidden an arrow since our previous frame.
        // Preserve that change rather than resurrecting the judged trail.
        lease.Alphas.Clear();
    }
    private static void ApplySlideBodyAppearance(SlideRoot owner)
    {
        if (!SlideAppearances.TryGetValue(owner, out var lease) || owner.IsEnd()) return;
        var now = NotesManager.GetCurrentMsec();
        if (now + NoteJudge.JudgeAdjustMs >= lease.Head) return; // native trail owns arrows once input can consume them
        var alpha = ScrollVisualTiming.SlideAppearance(now, lease.Head, lease.Launch, lease.Speed, lease.Option, lease.Touch).BodyAlpha;
        foreach (var sprite in lease.Body)
        {
            if (sprite == null) continue;
            lease.Alphas[sprite] = (sprite.color.a, alpha);
            var color = sprite.color; color.a = alpha; sprite.color = color;
        }
    }
    private static void ApplySlideStarAppearance(SlideRoot owner)
    {
        if (!SlideAppearances.TryGetValue(owner, out var lease) || owner.IsEnd()) return;
        var now = NotesManager.GetCurrentMsec();
        if (now > lease.Launch) return; // native motion/SV postprocess controls the moving star
        var presentation = ScrollVisualTiming.SlideAppearance(now, lease.Head, lease.Launch, lease.Speed, lease.Option, lease.Touch);
        var size = Singleton<GamePlayManager>.Instance.GetGameScore(owner.MonitorId).UserOption.SlideSize.GetValue();
        for (var i = 0; i < lease.Stars.Length; i++)
        {
            if (lease.Stars[i] != null) { var color = lease.Stars[i].color; color.a = presentation.StarAlpha; lease.Stars[i].color = color; }
            if (lease.StarObjects[i] != null) lease.StarObjects[i].transform.localScale = new Vector3(presentation.StarScale * size, presentation.StarScale * size, 1);
        }
    }
    [HarmonyPatch]
    public static class SlideBodyAppearancePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => new[]
        { AccessTools.DeclaredMethod(typeof(SlideRoot), "UpdateAlpha"), AccessTools.DeclaredMethod(typeof(SlideFan), "UpdateAlpha") };
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(SlideRoot __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        RestoreSlideBodyAppearance(__instance);
    }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(SlideRoot __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        ApplySlideBodyAppearance(__instance);
    }
    }
    [HarmonyPatch]
    public static class SlideAppearanceInitializePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => new[]
        { AccessTools.DeclaredMethod(typeof(SlideRoot), "Initialize"), AccessTools.DeclaredMethod(typeof(SlideFan), "Initialize") };
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(SlideRoot __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            RestoreSlideBodyAppearance(__instance);
            SlideAppearances.Remove(__instance);
        }
    }
}
