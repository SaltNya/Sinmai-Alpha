using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static readonly FieldInfo RingNoteObject = AccessTools.Field(typeof(NoteBase), "NoteObj");
    private static readonly FieldInfo RingSprite = AccessTools.Field(typeof(NoteBase), "SpriteRender");
    private static readonly FieldInfo RingExSprite = AccessTools.Field(typeof(NoteBase), "SpriteRenderEx");
    private static readonly FieldInfo RingGuide = AccessTools.Field(typeof(NoteBase), "GuideObj");
    private static readonly FieldInfo RingBreakEffect = AccessTools.Field(typeof(BreakHoldNote), "BreakEffectSprite");
    private static bool _loggedRingVisualError;

    private static void SyncBreakHoldGlow(NoteBase owner, SpriteRenderer body)
    {
        // Native Execute sized this layer before Alpha rewrote the Hold body.
        // Reapply the final size for SV/HS/reversal and bounce presentations.
        if (owner is BreakHoldNote && RingBreakEffect.GetValue(owner) is SpriteRenderer glow)
            glow.size = body.size;
    }

    private static bool RingVisualActive(int index, float appear, float duration, float spawn, float now, float destroy = SpawnJudgeLine, bool once = false)
    {
        if (!HsMultByNoteIndex.TryGetValue(index, out var hs)) return false;
        // Conversion marks every note as ReferenceMotion. That selects the
        // reference curve only when a chart actually changes movement; it must
        // not replace native approach/scale timing at unit SV/HS and default
        // radii. Keeping the native path here preserves the player's speed feel.
        if (Math.Abs(hs - 1) > .00001f || Math.Abs(spawn - SpawnDefaultRadius) > .00001f ||
            Math.Abs(destroy - SpawnJudgeLine) > .00001f || once) return true;
        // Preserve native animation exactly when the entire relevant approach is
        // unit scroll. A freeze and reversal can cancel in the total integral,
        // so checking only two endpoints is insufficient.
        if (!NoteScrollTableByNoteIndex.TryGetValue(index, out var table)) return false;
        var from = Math.Min(now, appear - duration * 2);
        var to = Math.Max(now, appear);
        var origin = GetNoteScroll(index, from);
        if (Math.Abs(GetNoteScroll(index, to) - origin - (to - from)) > .01f) return true;
        foreach (var point in table)
            if (point.Msec > from && point.Msec < to && Math.Abs(point.Scroll - origin - (point.Msec - from)) > .01f) return true;
        return false;
    }
    private static bool TryRingPresentation(NoteBase owner, bool tail, out ScrollVisualTiming.Presentation presentation)
    {
        presentation = default;
        if (owner == null || owner.IsEnd() || owner is TouchNoteB || owner is TouchHoldC) return false;
        var index = owner.GetNoteIndex();
        if (!SvScrollPosByNoteIndex.TryGetValue(index, out var target) || !SvTypeByNoteIndex.TryGetValue(index, out var type)) return false;
        var appear = (float)FAppearMsec.GetValue(owner);
        var now = NotesManager.GetCurrentMsec();
        if (now < appear && BounceDurationByNoteIndex.TryGetValue(index, out var bounce) && bounce > 0) return false;
        var duration = (float)FDefaultMsec.GetValue(owner);
        var settings = RingSettings(index, appear, type);
        if (duration <= 0 || !RingVisualActive(index, appear, duration, settings.Spawn, now, settings.Destroy, settings.Once)) return false;
        var hs = HsMultByNoteIndex[index];
        if (tail && !SvTailScrollPosByNoteIndex.TryGetValue(index, out target)) return false;
        var speed = (ReferenceMotionByNoteIndex.Contains(index) ? RingViewSpeed(duration) * .001f : SpawnRadiusSpan / duration) * hs;
        var everCrossed = settings.Once && RingEverCrossed(owner, tail, target, speed, settings, now);
        presentation = ScrollVisualTiming.Ring(target, GetNoteScroll(index, now), speed, settings.Spawn, settings.Destroy, everCrossed);
        if ((owner is HoldNote || owner is BreakHoldNote) && now >= (tail ? (float)FTailMsec.GetValue(owner) : appear) && presentation.Running)
            presentation = new ScrollVisualTiming.Presentation(settings.Destroy, 1, true);
        return true;
    }
    private static float RingNativeY(NoteBase owner, float radius)
    {
        var start = (float)FStartPos.GetValue(owner);
        var end = (float)FEndPos.GetValue(owner);
        return start + (end - start) * ((radius - SpawnDefaultRadius) / SpawnRadiusSpan);
    }
    private static float RingFirstVisibleTime(NoteData note, float duration)
    {
        var index = note.indexNote;
        if (!NoteScrollTableByNoteIndex.TryGetValue(index, out var table) || table.Count == 0 || duration <= 0) return float.NaN;
        var hs = HsMultByNoteIndex[index];
        var type = SvTypeByNoteIndex[index];
        var settings = RingSettings(index, note.time.msec, type);
        var speed = (ReferenceMotionByNoteIndex.Contains(index) ? RingViewSpeed(duration) * .001f : SpawnRadiusSpan / duration) * hs;
        // Zero speed is already on the judgment ring, independently of SV.
        if (Math.Abs(speed) <= .000000001) return table[0].Msec;
        var target = SvScrollPosByNoteIndex[index];
        if (speed < 0 && ReferenceMotionByNoteIndex.Contains(index) && ResolveSpeedType(note) != "hold")
        {
            // Negative HS approaches from outside the frame. Being past SPAWN
            // does not make a far-away object visible. A conservative 12-radius
            // envelope includes frame corners/native art; the caller retains
            // at least the ordinary activation lead. Hold bodies keep their
            // full-history activation because they can span across the frame.
            return ScrollVisualTiming.FirstViewportEntry(table, target, speed, settings.Spawn, settings.Destroy, 12);
        }
        var threshold = target - (Math.Abs(settings.Destroy - settings.Spawn) + 2.5f) / speed;
        bool Crossed(float scroll) => speed > 0 ? scroll >= threshold : scroll <= threshold;
        for (var i = 0; i < table.Count && table[i].Msec <= note.time.msec; i++)
        {
            var point = table[i];
            if (Crossed(point.Scroll)) return point.Msec;
            if (i + 1 == table.Count) break;
            var next = table[i + 1];
            if (!Crossed(next.Scroll)) continue;
            var delta = next.Scroll - point.Scroll;
            if (Math.Abs(delta) < .00001f) continue;
            return point.Msec + (next.Msec - point.Msec) * (threshold - point.Scroll) / delta;
        }
        return float.NaN;
    }

    // Different HS/SV can register a later note before an earlier note. Restore
    // time order in the native lane, whose IsJudgeNote uses sibling order.
    public static void OrderSignedRingInputQueue(GameCtrl __instance, NoteData note, bool __result)
    {
        if (!__result || note == null || HsMultByNoteIndex.Count == 0 || note.type.isAllSlide() || IsFakeNote(note)) return;
        var active = Traverse.Create(__instance).Field("_activeNoteList").GetValue<List<NoteBase>>();
        var owner = active?.FirstOrDefault(value => value != null && !value.IsEnd() && value.GetNoteIndex() == note.indexNote);
        var parent = owner != null ? owner.transform.parent : null;
        if (parent == null) return;
        var notes = new List<NoteBase>();
        for (var i = 0; i < parent.childCount; i++)
        {
            var candidate = parent.GetChild(i).GetComponent<NoteBase>();
            if (candidate != null && candidate.gameObject.activeSelf && !candidate.IsEnd()) notes.Add(candidate);
        }
        foreach (var candidate in notes.OrderBy(value => (float)FAppearMsec.GetValue(value)).ThenBy(value => value.GetNoteIndex()))
            candidate.transform.SetAsFirstSibling();
    }

    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static class SignedRingQueuePatch
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(GameCtrl __instance, NoteData note, bool __result)
            {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        OrderSignedRingInputQueue(__instance, note, __result);
    }
    }

    [HarmonyPatch]
    public static class SignedRingPresentationPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // Hold/BreakHold and custom subclasses override both Execute and
            // NoteCheck. Postprocess their visuals once after native play logic.
            return typeof(NoteBase).Assembly.GetTypes().Concat(typeof(CustomNoteTypes).Assembly.GetTypes())
                .Where(type => typeof(NoteBase).IsAssignableFrom(type) && type != typeof(NoteBase) &&
                    !typeof(TouchNoteB).IsAssignableFrom(type) && !typeof(TouchHoldC).IsAssignableFrom(type))
                .Select(type => type.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(method => method != null && method.GetParameters().Length == 0).Distinct();
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(NoteBase __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            try
            {
                if (TryBounceHoldPresentation(__instance)) return;
                if (!TryRingPresentation(__instance, false, out var head)) return;
                var noteObject = (GameObject)RingNoteObject.GetValue(__instance);
                if (noteObject == null) return;
                var nativeSize = Singleton<GamePlayManager>.Instance.GetGameScore(__instance.MonitorId).UserOption.NoteSize.GetValue();
                var y = RingNativeY(__instance, head.Radius);
                var position = noteObject.transform.localPosition;
                var orientation = 1f;
                var isHold = __instance is HoldNote || __instance is BreakHoldNote;
                if (isHold)
                {
                    var fields = Traverse.Create(__instance);
                    var tailObject = fields.Field("EndPointObj").GetValue<GameObject>();
                    var tail = TryRingPresentation(__instance, true, out var value) ? value : head;
                    var tailY = RingNativeY(__instance, tail.Radius);
                    var hasBody = head.Running && NotesManager.GetCurrentMsec() < (float)FTailMsec.GetValue(__instance);
                    var height = hasBody ? Math.Abs(y - tailY) + 140f : 140f;
                    if (hasBody) { orientation = y >= tailY ? 1 : -1; position.y = (y + tailY) * .5f; }
                    else position.y = y;
                    var sprite = (SpriteRenderer)RingSprite.GetValue(__instance);
                    sprite.size = new Vector2(sprite.size.x, height);
                    var ex = (SpriteRenderer)RingExSprite.GetValue(__instance);
                    if (ex != null) ex.size = sprite.size;
                    var effect = fields.Field("EffectSprite").GetValue<SpriteRenderer>();
                    if (effect != null) effect.size = sprite.size;
                    SyncBreakHoldGlow(__instance, sprite);
                    if (tailObject != null)
                    {
                        tailObject.SetActive(hasBody && tail.Running);
                        var tailPosition = tailObject.transform.localPosition;
                        tailPosition.y = tailY; tailObject.transform.localPosition = tailPosition;
                        tailObject.transform.localScale = new Vector3(head.Scale, head.Scale, 1);
                    }
                }
                else position.y = y;
                noteObject.transform.localPosition = position;
                noteObject.transform.localScale = new Vector3(head.Scale * nativeSize, head.Scale * (isHold ? orientation : nativeSize), 0);
                var guide = (NoteGuide)RingGuide.GetValue(__instance);
                if (guide != null)
                {
                    var scale = Math.Abs(head.Radius / SpawnJudgeLine);
                    guide.transform.localScale = new Vector3(scale, scale, 1);
                    guide.SetAlpha(head.Scale);
                }
                // No input, NoteStat, JudgeResult, timing, queue, result or audio writes.
            }
            catch (Exception error)
            {
                if (_loggedRingVisualError) return;
                _loggedRingVisualError = true;
                MelonLoader.MelonLogger.Warning("[CustomNoteType] Signed ring presentation: " + error.Message);
            }
        }
    }
}
