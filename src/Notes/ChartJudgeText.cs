using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SinmaiAlpha.ChartVisuals;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static readonly Dictionary<GameMonitor, ChartJudgeTextDisplay> JudgeTextDisplays = new();

    private static void ApplyJudgeTextPresentation(GameMonitor owner, NotesReader reader)
    {
        if (!PresentationTimelines.TryGetValue(reader, out var timeline) ||
            !timeline.Tracks.TryGetValue("showjudgetext", out var track))
        {
            ReleaseJudgeTextPresentation(owner);
            return;
        }
        var ctrl = Traverse.Create(owner).Field("GameController").GetValue<GameCtrl>();
        if (ctrl == null) return;
        if (!JudgeTextDisplays.TryGetValue(owner, out var display) || display == null)
            JudgeTextDisplays[owner] = display = ctrl.gameObject.AddComponent<ChartJudgeTextDisplay>();
        display.Bind(ctrl, owner.MonitorIndex, reader, track);
    }

    private static void ReleaseJudgeTextPresentation(GameMonitor owner)
    {
        if (!JudgeTextDisplays.TryGetValue(owner, out var display)) return;
        JudgeTextDisplays.Remove(owner);
        if (display == null) return;
        display.Stop();
        Object.Destroy(display);
    }

    private static void ResetJudgeTextPresentation(NotesReader reader)
    {
        var owners = new List<GameMonitor>();
        foreach (var pair in JudgeTextDisplays)
            if (pair.Value == null || ReferenceEquals(pair.Value.Reader, reader)) owners.Add(pair.Key);
        foreach (var owner in owners) ReleaseJudgeTextPresentation(owner);
    }

    private static void ReleaseJudgeTextPresentation()
    {
        foreach (var display in JudgeTextDisplays.Values)
            if (display != null) { display.Stop(); Object.Destroy(display); }
        JudgeTextDisplays.Clear();
    }
}

// Like the reference JudgeTextRendererGuard, run after Animator evaluation and
// assign authored alpha absolutely. Only feedback text renderers are enrolled;
// hit effects, LED calls and judge methods are owned by Sinmai. Unity 2018 in
// Sinmai has no forceRenderingOff: lease the text renderer's enabled flag to
// implement that visibility gate, retaining its native enabled state. Neither
// feedback GameObjects nor gameplay components are disabled.
public sealed class ChartJudgeTextDisplay : MonoBehaviour
{
    private static readonly FieldInfo[] Pools =
    {
        AccessTools.Field(typeof(GameCtrl), "_judgeGradeObjectList"),
        AccessTools.Field(typeof(GameCtrl), "_judgeGradeTouchAObjectList"),
        AccessTools.Field(typeof(GameCtrl), "_judgeGradeTouchBObjectList"),
        AccessTools.Field(typeof(GameCtrl), "_judgeGradeTouchCObjectList"),
        AccessTools.Field(typeof(GameCtrl), "_judgeGradeTouchDObjectList"),
        AccessTools.Field(typeof(GameCtrl), "_judgeGradeTouchEObjectList"),
        AccessTools.Field(typeof(GameCtrl), "_judgeSlideObjectList"),
    };
    private static readonly FieldInfo[] GradeSprites =
    {
        AccessTools.Field(typeof(JudgeGrade), "SpriteRender"),
        AccessTools.Field(typeof(JudgeGrade), "SpriteRenderFastLate"),
        AccessTools.Field(typeof(JudgeGrade), "SpriteRenderAdd"),
    };
    private static readonly FieldInfo[] SlideSprites =
    {
        AccessTools.Field(typeof(SlideJudge), "SpriteRender"),
        AccessTools.Field(typeof(SlideJudge), "SpriteRenderAdd"),
    };

    private sealed class RendererLease
    {
        internal SpriteRenderer Renderer;
        private float nativeAlpha, appliedAlpha;
        private bool nativeEnabled, appliedEnabled, owned;
        internal void Apply(float alpha)
        {
            if (Renderer == null) return;
            var color = Renderer.color;
            if (!owned || color.a != appliedAlpha) nativeAlpha = color.a;
            if (!owned || Renderer.enabled != appliedEnabled) nativeEnabled = Renderer.enabled;
            appliedAlpha = alpha;
            appliedEnabled = nativeEnabled && alpha > .001f;
            owned = true;
            if (!Mathf.Approximately(color.a, alpha)) { color.a = alpha; Renderer.color = color; }
            if (Renderer.enabled != appliedEnabled) Renderer.enabled = appliedEnabled;
        }
        internal void Restore()
        {
            if (Renderer == null || !owned) return;
            var color = Renderer.color;
            if (color.a == appliedAlpha) { color.a = nativeAlpha; Renderer.color = color; }
            if (Renderer.enabled == appliedEnabled) Renderer.enabled = nativeEnabled;
            owned = false;
        }
    }

    private GameCtrl controller;
    private int monitorIndex;
    private PresentationCommands.Track track;
    private readonly HashSet<Component> feedbackOwners = new();
    private readonly Dictionary<SpriteRenderer, RendererLease> renderers = new();
    internal NotesReader Reader { get; private set; }

    internal void Bind(GameCtrl ctrl, int monitor, NotesReader reader, PresentationCommands.Track value)
    {
        if (controller != ctrl || !ReferenceEquals(Reader, reader) || !ReferenceEquals(track, value))
        {
            Restore();
            feedbackOwners.Clear();
            renderers.Clear();
        }
        controller = ctrl; monitorIndex = monitor; Reader = reader; track = value;
        enabled = true;
        Apply();
    }

    private void Enroll(Component owner)
    {
        if (owner == null || !feedbackOwners.Add(owner)) return;
        var fields = owner is SlideJudge ? SlideSprites : owner is JudgeGrade ? GradeSprites : null;
        if (fields == null) return;
        foreach (var field in fields)
        {
            var renderer = field?.GetValue(owner) as SpriteRenderer;
            if (renderer != null && !renderers.ContainsKey(renderer))
                renderers.Add(renderer, new RendererLease { Renderer = renderer });
        }
    }

    private void Apply()
    {
        if (!enabled || controller == null || track == null) return;
        foreach (var pool in Pools)
            if (pool?.GetValue(controller) is IEnumerable items)
                foreach (var item in items) Enroll(item as Component);
        foreach (var grade in DZoneLane.Grades(monitorIndex)) Enroll(grade);
        var alpha = Mathf.Clamp01(track.Evaluate(NotesManager.GetCurrentMsec()));
        foreach (var lease in renderers.Values) lease.Apply(alpha);
    }
    private void Restore()
    {
        foreach (var lease in renderers.Values) lease.Restore();
    }
    internal void Stop()
    {
        enabled = false;
        Restore();
        controller = null; Reader = null; track = null;
        feedbackOwners.Clear(); renderers.Clear();
    }
    private void LateUpdate() => Apply();
    private void OnDisable() => Restore();
    private void OnDestroy() => Stop();
}
