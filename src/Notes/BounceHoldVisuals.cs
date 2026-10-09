using System;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Native Hold.Execute derives its tail from a straight approach. Apply the
    // complete bounce body after Execute/NoteCheck, retaining their input clock.
    private static bool TryBounceHoldPresentation(NoteBase owner)
    {
        if (owner == null || owner.IsEnd() || !(owner is HoldNote || owner is BreakHoldNote)) return false;
        var index = owner.GetNoteIndex();
        if (!BounceDurationByNoteIndex.TryGetValue(index, out var bounce) || bounce <= 0) return false;
        var fields = Traverse.Create(owner);
        var noteObject = (GameObject)RingNoteObject.GetValue(owner);
        if (noteObject == null) return false;
        var appear = (float)FAppearMsec.GetValue(owner);
        var tailTime = (float)FTailMsec.GetValue(owner);
        var now = NotesManager.GetCurrentMsec();
        var type = SvTypeByNoteIndex.TryGetValue(index, out var noteType) ? noteType : "hold";
        var settings = RingSettings(index, appear, type);
        var hs = HsMultByNoteIndex.TryGetValue(index, out var multiplier) ? multiplier : 1f;
        var duration = (float)FDefaultMsec.GetValue(owner);
        var speed = (ReferenceMotionByNoteIndex.Contains(index) ? RingViewSpeed(duration) * .001f : SpawnRadiusSpan / Math.Max(duration, .001f)) * hs;
        var headScroll = GetNoteScroll(index, appear);
        var tailScroll = GetNoteScroll(index, tailTime);
        var currentScroll = GetNoteScroll(index, now);
        var pathDirection = Math.Abs(settings.Destroy - settings.Spawn) < .000001f ? 1f : Math.Sign(settings.Destroy - settings.Spawn);
        var beforeJudge = now < appear;
        float headRadius, tailRadius; bool tailVisible;
        if (beforeJudge)
        {
            var direction = BounceHoldDirection(index, appear, hs);
            var progress = 1f + direction * hs * (currentScroll - headScroll) / (bounce * 1000f);
            var takeoff = BounceHoldTakeoff(index, appear, headScroll, bounce * 1000f, hs * direction);
            var visible = now >= takeoff && (settings.Once || progress >= -.000001f);
            SetBounceAlpha(fields, visible ? 1f : 0f);
            SetBounceChildrenActive(fields, visible);
            SetBounceGuideActive(fields, visible);
            if (!visible)
            {
                var hiddenTail = fields.Field("EndPointObj").GetValue<GameObject>();
                if (hiddenTail != null) hiddenTail.SetActive(false);
                return true;
            }
            var fromApex = 2f * progress - 1f;
            var excursion = (settings.Destroy - settings.Spawn) * (1f - fromApex * fromApex);
            headRadius = direction < 0 ? settings.Destroy + excursion : settings.Destroy - excursion;
            var fullLength = pathDirection * speed * (tailScroll - headScroll);
            var length = Math.Sign(fullLength) * Math.Min(Math.Abs(fullLength), Math.Abs(settings.Destroy - settings.Spawn));
            tailRadius = headRadius - length;
            tailVisible = Math.Abs(length) > .001f;
        }
        else
        {
            SetBounceAlpha(fields, 1f);
            SetBounceChildrenActive(fields, true);
            SetBounceGuideActive(fields, true);
            if (TryRingPresentation(owner, false, out _)) return false;
            // At the head's original judge time, resume normal tail descent.
            // The tail has its own SV scroll target and original release time.
            headRadius = settings.Destroy;
            var tail = ScrollVisualTiming.Ring(tailScroll, currentScroll, speed, settings.Spawn, settings.Destroy,
                settings.Once && RingEverCrossed(owner, true, tailScroll, speed, settings, now));
            tailRadius = tail.Radius;
            tailVisible = tail.Running;
        }
        var headY = RingNativeY(owner, headRadius);
        var tailY = RingNativeY(owner, tailRadius);
        if (beforeJudge)
        {
            var offset = GetNoteSpeedOffsetY(owner, (float)FEndPos.GetValue(owner) - (float)FStartPos.GetValue(owner));
            headY += offset; tailY += offset;
        }
        var hasBody = now < tailTime;
        var position = noteObject.transform.localPosition;
        position.y = hasBody ? (headY + tailY) * .5f : headY;
        noteObject.transform.localPosition = position;
        var nativeSize = Singleton<GamePlayManager>.Instance.GetGameScore(owner.MonitorId).UserOption.NoteSize.GetValue();
        var orientation = hasBody && headY < tailY ? -1f : 1f;
        noteObject.transform.localScale = new Vector3(nativeSize, orientation, 0);
        var sprite = (SpriteRenderer)RingSprite.GetValue(owner);
        sprite.size = new Vector2(sprite.size.x, (hasBody ? Math.Abs(headY - tailY) : 0) + 140f);
        var ex = (SpriteRenderer)RingExSprite.GetValue(owner);
        if (ex != null) ex.size = sprite.size;
        var effect = fields.Field("EffectSprite").GetValue<SpriteRenderer>();
        if (effect != null) effect.size = sprite.size;
        SyncBreakHoldGlow(owner, sprite);
        var tailObject = fields.Field("EndPointObj").GetValue<GameObject>();
        if (tailObject != null)
        {
            tailObject.SetActive(hasBody && tailVisible);
            var tailPosition = tailObject.transform.localPosition;
            tailPosition.y = tailY; tailObject.transform.localPosition = tailPosition;
            tailObject.transform.localScale = Vector3.one;
        }
        var guide = (NoteGuide)RingGuide.GetValue(owner);
        if (guide != null)
        {
            var scale = Math.Abs(headRadius / SpawnJudgeLine);
            guide.transform.localScale = new Vector3(scale, scale, 1);
            guide.SetAlpha(1);
        }
        return true;
    }

    private static float BounceHoldDirection(int index, float appear, float hs)
    {
        if (Math.Abs(hs) < .000001f) return 1;
        if (NoteScrollTableByNoteIndex.TryGetValue(index, out var table))
            for (var i = table.Count - 2; i >= 0; i--)
            {
                if (table[i].Msec >= appear) continue;
                var delta = table[i + 1].Scroll - table[i].Scroll;
                if (Math.Abs(delta) > .000001f) return Math.Sign(hs * delta);
            }
        return Math.Sign(hs);
    }

    private static float BounceHoldTakeoff(int index, float appear, float headScroll, float bounceMsec, float effectiveHs)
    {
        if (Math.Abs(effectiveHs) < .000001f) return appear;
        if (NoteScrollTableByNoteIndex.TryGetValue(index, out var table))
            return ScrollVisualTiming.FirstCrossing(table, headScroll, effectiveHs / bounceMsec, 0, 1);
        return appear - bounceMsec / effectiveHs;
    }

    private static float BounceHoldActivationLead(float lead, NoteData note)
    {
        if (note == null || !note.type.isHold() || !BounceDurationByNoteIndex.TryGetValue(note.indexNote, out var bounce) || bounce <= 0) return lead;
        var index = note.indexNote;
        var hs = HsMultByNoteIndex.TryGetValue(index, out var multiplier) ? multiplier : 1f;
        var start = BounceHoldTakeoff(index, note.time.msec, GetNoteScroll(index, note.time.msec), bounce * 1000f,
            hs * BounceHoldDirection(index, note.time.msec, hs));
        return float.IsNaN(start) ? lead : Math.Min(10000f, Math.Max(lead, note.time.msec - start + 16f));
    }
}
