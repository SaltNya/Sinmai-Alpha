using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using SinmaiAlpha.ChartVisuals;
using MelonLoader;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Guide arcs retain their native material. Prepare only colors that can
    // reach an each arc, in loading frames, before the gameplay clock starts.
    private sealed class HintTintWarmup
    {
        public NoteData First;
        public int Count, NextNote, NextKey, MissingPool, Built, Skipped;
        public bool Collected, Done;
        public readonly Dictionary<(int Rgb, bool Mine), double> Colors = new();
        public readonly Dictionary<(List<VisualChange>, bool, bool, bool, string), List<(int Rgb, double Time)>> Streams = new();
        public List<VisualTintKey> Keys;
    }
    private static readonly Dictionary<(GameCtrl, NotesReader), HintTintWarmup> HintTintWarmups = new();
    private static readonly FieldInfo HintGuideList = AccessTools.Field(typeof(GameCtrl), "_guideObjectList");
    private static readonly FieldInfo HintGuideRender = AccessTools.Field(typeof(NoteGuide), "_spriteEachRender");
    private const int HintTintKeyLimit = 32;
    private const long HintTintByteLimit = 128L * 1024 * 1024;
    private static int hintTintBuilt;
    private static long hintTintBytes;

    private static void ResetHintTintWarmup()
    {
        HintTintWarmups.Clear(); TintWarmedReaders.Clear();
        hintTintBuilt = 0; hintTintBytes = 0;
    }

    private static bool WarmHintTints(GameCtrl controller, NotesReader reader)
    {
        var notes = reader.GetNoteList();
        var first = notes.Count == 0 ? null : notes[0];
        var owner = (controller, reader);
        if (!HintTintWarmups.TryGetValue(owner, out var state) || !ReferenceEquals(state.First, first) || state.Count != notes.Count)
            HintTintWarmups[owner] = state = new HintTintWarmup { First = first, Count = notes.Count };
        if (state.Done) return true;
        if (!state.Collected)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 250; // ~4 ms of collection per loading frame.
            VisualTimelines.TryGetValue(reader, out var timeline);
            while (state.NextNote < notes.Count)
            {
                CollectHintTintNote(state, controller, timeline, notes[state.NextNote++]);
                if (Stopwatch.GetTimestamp() >= deadline) break;
            }
            if (state.NextNote < notes.Count) return false;
            state.Collected = true;
            state.Streams.Clear();
            if (state.Colors.Count == 0) return FinishHintTintWarmup(state);
            return false; // Never collect a whole chart and bake a texture in the same step.
        }
        if (state.Keys == null)
        {
            if (!QueueHintTintKeys(controller, state))
            {
                if (++state.MissingPool < 120) return false;
                MelonLogger.Warning("[Note Visual] Each-guide pool unavailable; leaving tint generation lazy.");
                return FinishHintTintWarmup(state);
            }
            return false;
        }
        while (state.NextKey < state.Keys.Count)
        {
            var key = state.Keys[state.NextKey++];
            if (key.Sprite == null || VisualTintSprites.ContainsKey(key) || VisualTintFailures.Contains(key.Sprite)) continue;
            var bytes = (long)Mathf.RoundToInt(key.Sprite.rect.width) * Mathf.RoundToInt(key.Sprite.rect.height) * 4;
            // Shared by both monitors. This bounds prewarming, not later lazy gameplay requests.
            if (hintTintBuilt >= HintTintKeyLimit || bytes > HintTintByteLimit - hintTintBytes)
            { state.Skipped++; continue; }
            var v = key.Vertex;
            var vertex = new Color32((byte)v, (byte)(v >> 8), (byte)(v >> 16), 255);
            GetVisualTintSprite(key.Sprite, key.Rgb, vertex);
            if (VisualTintSprites.ContainsKey(key))
            { hintTintBuilt++; hintTintBytes += bytes; state.Built++; }
            break; // At most one new texture per call, including failed attempts.
        }
        return state.NextKey >= state.Keys.Count && FinishHintTintWarmup(state);
    }

    private static bool FinishHintTintWarmup(HintTintWarmup state)
    {
        state.Done = true;
        if (state.Colors.Count != 0)
            MelonLogger.Msg("[Note Visual] Each-guide tint prewarm: built " + state.Built +
                ", deferred " + state.Skipped + " keys; shared prewarm bytes=" + hintTintBytes);
        state.Colors.Clear(); state.Streams.Clear(); state.Keys?.Clear();
        return true;
    }

    private static void CollectHintTintNote(HintTintWarmup state, GameCtrl controller, VisualTimeline timeline, NoteData note)
    {
        if (note == null || IsBorrowed(note) || !HasHintArc(note) || !VisualNotes.TryGetValue(note, out var payload)) return;
        var visual = payload.Note;
        if (visual.Family == "blockarea") return;
        var kinds = RuntimeMonitor(controller.MonitorIndex).NoteKinds;
        var mine = kinds.TryGetValue(note.indexNote, out var kind) &&
            (kind == CustomNoteKind.Mine || kind == CustomNoteKind.MineTouchBreak || kind == CustomNoteKind.MineTouchStar);
        var beforeSpawn = (double)note.time.msec - 2000;
        void Add(int rgb, double time)
        {
            void Record(bool isMine)
            {
                var key = (rgb, isMine);
                if (!state.Colors.TryGetValue(key, out var previous) || time < previous) state.Colors[key] = time;
            }
            if (!mine) Record(false);
            if (mine || visual.Mine) Record(true);
        }
        if (visual.Base.Color is int baseRgb) Add(baseRgb, beforeSpawn);
        if (timeline == null || !timeline.Streams.TryGetValue(payload.Stream, out var stream)) return;
        var streamKey = (stream, visual.Mine, visual.Break, visual.Each, visual.Family);
        if (!state.Streams.TryGetValue(streamKey, out var colors))
        {
            var unique = new Dictionary<int, double>();
            foreach (var change in stream)
            {
                if (change == null || change.Kind != "color" || !(change.Value?.Color is int rgb) || double.IsNaN(change.Time)) continue;
                var type = change.Type;
                if (!(visual.Mine ? type == "mine" : (visual.Break && type == "break") || (visual.Each && type == "each") ||
                    type == visual.Family || (visual.Family == "star" && type == "tap") || type == "")) continue;
                if (!unique.TryGetValue(rgb, out var previous) || change.Time < previous) unique[rgb] = change.Time;
            }
            state.Streams[streamKey] = colors = unique.Select(p => (p.Key, p.Value)).ToList();
        }
        foreach (var color in colors) Add(color.Rgb, Math.Max(beforeSpawn, color.Time));
    }

    private static bool HasHintArc(NoteData note)
    {
        if (note.eachChild == null) return false;
        foreach (var child in note.eachChild)
            if (child != null && child.startButtonPos != note.startButtonPos) return true;
        return false;
    }

    private static bool QueueHintTintKeys(GameCtrl controller, HintTintWarmup state)
    {
        var guides = HintGuideList?.GetValue(controller) as List<NoteGuide>;
        if (guides == null || guides.Count == 0) return false;
        var sources = new Dictionary<VisualTintKey, Color>();
        foreach (var guide in guides)
        {
            if (guide == null) continue;
            var render = HintGuideRender?.GetValue(guide) as SpriteRenderer;
            if (render == null || render.sprite == null) return false;
            var vertex = new Color(render.color.r, render.color.g, render.color.b, 1);
            sources[new VisualTintKey(render.sprite, 0, vertex)] = vertex;
        }
        if (sources.Count == 0) return false;
        var keys = new HashSet<VisualTintKey>();
        state.Keys = new List<VisualTintKey>();
        foreach (var color in state.Colors.OrderBy(p => p.Value))
            foreach (var source in sources)
            {
                var sprite = source.Key.Sprite;
                if (color.Key.Mine)
                {
                    if (!MineTextures.TryGetValue("mine", out var texture)) { state.Skipped++; continue; }
                    sprite = CreateSpriteFromTexture("mine", texture, sprite);
                }
                var key = new VisualTintKey(sprite, color.Key.Rgb, source.Value);
                if (keys.Add(key)) state.Keys.Add(key);
            }
        return true;
    }
}
