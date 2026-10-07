using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Object = UnityEngine.Object;
using MelonLoader;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class MediaTimeline
    {
        public MediaTimeline() { }
        public string Root;
        public List<MediaChange> Events = new();
        public string AudioKey;
    }
    private sealed class MediaSession
    {
        public NotesReader Reader;
        public MediaTimeline Timeline;
        public ChartMediaTimelineController Controller;
        public bool Started, Paused;
        public float PausedTime;
    }
    private static List<(int Bar, int Grid, string Kind, string Text)> PendingMedia => RuntimeCharts.Current.PendingMedia;
    private static readonly ConditionalWeakTable<NotesReader, MediaTimeline> MediaTimelines = new();
    private static readonly Dictionary<GameCtrl, MediaSession> MediaSessions = new();
    private static bool NativeMediaPaused;

    public static void ReadMediaCommand(string line)
    {
        if (line == null) return;
        var p = line.Split('\t');
        if (p.Length == 4 && MediaCommands.IsKind(p[0].ToLowerInvariant()) && int.TryParse(p[1], out var bar) && int.TryParse(p[2], out var grid))
            PendingMedia.Add((bar, grid, p[0].ToLowerInvariant(), p[3]));
    }
    public static void BuildMediaCommands(NotesReader reader)
    {
        var timeline = MediaTimelines.GetOrCreateValue(reader);
        timeline.Events.Clear();
        var origin = new NotesTime(); origin.init(0, 0, reader);
        foreach (var command in PendingMedia)
        {
            var time = new NotesTime(); time.init(command.Bar, command.Grid, reader);
            if (MediaCommands.Decode(command.Kind, command.Text, (time.msec - origin.msec) / 1000d, out var item)) timeline.Events.Add(item);
            else MelonLogger.Warning("[Chart Media] Invalid " + command.Kind + ": " + command.Text);
        }
        timeline.Events = timeline.Events.OrderBy(e => e.time).ToList();
        // Player offsets cancel above. Millisecond quantization avoids native
        // float cancellation noise when two readers contain identical audio.
        timeline.AudioKey = (timeline.Root ?? "") + "|" + string.Join(";", timeline.Events.Where(e => e.kind == "audio")
            .Select(e => Math.Round(e.time, 3).ToString("R", CultureInfo.InvariantCulture) + ":" + e.Encode()));
        if (timeline.Events.Count > 0) MelonLogger.Msg("[Chart Media] Loaded " + timeline.Events.Count + " music-clock media events");
    }
    public static void ResetMediaPresentation(NotesReader reader)
    {
        PendingMedia.Clear(); MediaTimelines.Remove(reader);
        foreach (var pair in MediaSessions.Where(p => p.Value.Reader == reader).ToArray()) ReleaseMedia(pair.Key);
        RefreshMediaAudioOwners();
    }
    private static void ReleaseMedia(GameCtrl ctrl)
    {
        if (!MediaSessions.TryGetValue(ctrl, out var session)) return;
        MediaSessions.Remove(ctrl);
        if (session.Controller != null) { session.Controller.Shutdown(); Object.Destroy(session.Controller.gameObject); }
    }
    public static void ReleaseMediaPresentation()
    {
        foreach (var ctrl in MediaSessions.Keys.ToArray()) ReleaseMedia(ctrl);
        NativeMediaPaused = false;
    }
    private static void RefreshMediaAudioOwners()
    {
        foreach (var group in MediaSessions.Where(p => p.Key != null && p.Value.Controller != null).GroupBy(p => p.Value.Timeline.AudioKey, StringComparer.OrdinalIgnoreCase))
        {
            var owner = group.OrderBy(p => p.Key.MonitorIndex).First().Key;
            foreach (var pair in group) pair.Value.Controller.SetAudioEnabled(pair.Key == owner);
        }
    }
    public static void EnsureMediaPresentation(GameCtrl ctrl)
    {
        if (ctrl == null) return;
        var reader = NotesManager.Instance(ctrl.MonitorIndex).getReader();
        if (GuiSizes.SinglePlayer && ctrl.MonitorIndex != 0 || !MediaTimelines.TryGetValue(reader, out var timeline) || timeline.Events.Count == 0 || string.IsNullOrEmpty(timeline.Root))
        { ReleaseMedia(ctrl); RefreshMediaAudioOwners(); return; }
        if (MediaSessions.TryGetValue(ctrl, out var current) && current.Reader == reader && current.Timeline == timeline) return;
        ReleaseMedia(ctrl);
        var movie = Traverse.Create(ctrl).Field("_movieSprite").GetValue<SpriteRenderer>();
        var hasOverlay = timeline.Events.Any(e => e.kind == "pvOverlay");
        if (hasOverlay && movie == null) return;
        var go = new GameObject("ChartMedia_" + ctrl.MonitorIndex);
        go.transform.SetParent(ctrl.transform, false);
        var controller = go.AddComponent<ChartMediaTimelineController>();
        controller.Configure(timeline.Events, timeline.Root, new NativeMediaClock { Monitor = ctrl.MonitorIndex }, false, hasOverlay ? new NativeMediaBackground(movie) : null);
        controller.SetBackgroundFitMode(0);
        controller.SetAudioVolume(1);
        MediaSessions[ctrl] = new MediaSession { Reader = reader, Timeline = timeline, Controller = controller };
        RefreshMediaAudioOwners();
    }
    public static void ApplyMediaPresentation(GameMonitor monitor)
    {
        var ctrl = Traverse.Create(monitor).Field("GameController").GetValue<GameCtrl>();
        EnsureMediaPresentation(ctrl);
        if (ctrl == null || !MediaSessions.TryGetValue(ctrl, out var session) || session.Controller == null) return;
        var playing = NotesManager.Instance(ctrl.MonitorIndex).IsPlaying();
        var now = NotesManager.GetCurrentMsec() / 1000f;
        if (!playing)
        {
            if (session.Started) session.Controller.SetPlaybackActive(false);
            session.Started = session.Paused = false;
            return;
        }
        if (!session.Started) { session.Controller.SetPlaybackActive(true); session.Started = true; }
        if (NativeMediaPaused)
        {
            if (!session.Paused || now != session.PausedTime) session.Controller.SetPausedTimelineTime(now);
            session.Paused = true; session.PausedTime = now;
        }
        else
        {
            if (session.Paused) session.Controller.ContinuePlayback();
            session.Paused = false;
            session.Controller.Tick();
        }
    }
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static class MediaRootPatch
    {
        [HarmonyPrefix]
        public static void Prefix(NotesReader __instance, string fileName)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            try { MediaTimelines.GetOrCreateValue(__instance).Root = Path.GetDirectoryName(Path.GetFullPath(fileName)); }
            catch { MelonLogger.Warning("[Chart Media] Cannot resolve chart directory: " + fileName); }
        }
    }
    [HarmonyPatch(typeof(GameCtrl), "IsReady")]
    public static class MediaReadyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameCtrl __instance, ref bool __result)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            EnsureMediaPresentation(__instance);
            if (__result && MediaSessions.TryGetValue(__instance, out var session)) __result = session.Controller == null || session.Controller.IsPrepared;
        }
    }
    [HarmonyPatch(typeof(NotesManager), "Pause")]
    public static class MediaPausePatch
    {
        [HarmonyPostfix] public static void Postfix(bool isPause) {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }
        NativeMediaPaused = isPause;
    }
    }
    [HarmonyPatch(typeof(NotesManager), "StartPlay")]
    public static class MediaStartPatch
    {
        [HarmonyPostfix] public static void Postfix() {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }
        NativeMediaPaused = false;
    }
    }
}
