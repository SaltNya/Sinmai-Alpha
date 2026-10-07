using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class JudgeLineTimeline
    {
        public PresentationChange[] Changes;
    }
    private sealed class NativeJudgeLineLease
    {
        public NotesReader Reader;
        public JudgeLineTimeline Timeline;
        public SpriteRenderer Renderer;
        public Color Original;
        public JudgeLineCommands.Track Track;
        public void Restore() { if (Renderer != null) Renderer.color = Original; }
        public void Apply(double milliseconds)
        {
            if (Renderer == null) return;
            var tint = Track.Evaluate(milliseconds);
            Renderer.color = new Color(tint.R, tint.G, tint.B, tint.A);
        }
    }
    private static readonly ConditionalWeakTable<NotesReader, JudgeLineTimeline> JudgeLineTimelines = new();
    private static readonly Dictionary<GameMonitor, NativeJudgeLineLease> JudgeLineLeases = new();

    // Called with the decoded presentation events once per native reader.
    private static void BuildJudgeLineCommands(NotesReader reader, IEnumerable<PresentationChange> changes)
    {
        ResetJudgeLineCommands(reader);
        var events = changes.Where(c => JudgeLineCommands.IsKind(c.Kind)).ToArray();
        if (events.Length != 0) JudgeLineTimelines.Add(reader, new JudgeLineTimeline { Changes = events });
    }
    private static void ApplyJudgeLinePresentation(GameMonitor monitor)
    {
        if (monitor == null) return;
        if (GuiSizes.SinglePlayer && monitor.MonitorIndex != 0)
        { ReleaseJudgeLinePresentation(monitor); return; }
        var reader = NotesManager.Instance(monitor.MonitorIndex).getReader();
        if (!JudgeLineTimelines.TryGetValue(reader, out var timeline))
        { ReleaseJudgeLinePresentation(monitor); return; }
        var controller = Traverse.Create(monitor).Field("GameController").GetValue<GameCtrl>();
        // Installed Sinmai 1.70 Initialize selects the player's OutlineDesign
        // sprite on this exact renderer. Do not scan guide/note/effect pools.
        var outline = controller != null ? Traverse.Create(controller).Field("_guideEndPointObj").GetValue<GameObject>() : null;
        var renderer = outline != null ? outline.GetComponent<SpriteRenderer>() : null;
        if (renderer == null) { ReleaseJudgeLinePresentation(monitor); return; }
        if (!JudgeLineLeases.TryGetValue(monitor, out var lease) || lease.Reader != reader || lease.Timeline != timeline || lease.Renderer != renderer)
        {
            ReleaseJudgeLinePresentation(monitor);
            var original = renderer.color;
            lease = new NativeJudgeLineLease { Reader = reader, Timeline = timeline, Renderer = renderer, Original = original,
                Track = new JudgeLineCommands.Track(timeline.Changes, new JudgeLineCommands.Tint(original.r, original.g, original.b, original.a)) };
            JudgeLineLeases.Add(monitor, lease);
        }
        lease.Apply(NotesManager.GetCurrentMsec());
    }
    private static void ReleaseJudgeLinePresentation(GameMonitor monitor)
    {
        if (!JudgeLineLeases.TryGetValue(monitor, out var lease)) return;
        lease.Restore(); JudgeLineLeases.Remove(monitor);
    }
    private static void ResetJudgeLineCommands(NotesReader reader)
    {
        foreach (var monitor in JudgeLineLeases.Where(p => p.Value.Reader == reader).Select(p => p.Key).ToArray())
            ReleaseJudgeLinePresentation(monitor);
        JudgeLineTimelines.Remove(reader);
    }
    private static void ReleaseJudgeLinePresentation()
    {
        foreach (var lease in JudgeLineLeases.Values) lease.Restore();
        JudgeLineLeases.Clear();
    }
}
