using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Monitor;
using SinmaiAlpha.ChartVisuals;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class ReserveVisual
    {
        public int Monitor;
        public List<TouchReserve.ReserveData> Queue;
        public SpriteRenderer[] Renderers;
    }
    private static readonly ConditionalWeakTable<NotesReader, Dictionary<int, NoteData>> ReserveVisualNotes = new();
    private static readonly Dictionary<TouchReserve, ReserveVisual> ReserveVisuals = new();

    private static void TrackReserveVisuals(TouchReserve owner, List<TouchReserve.ReserveData> queue, SpriteRenderer[] renderers)
    {
        if (!TouchReserveMonitors.TryGetValue(owner, out var monitor)) return;
        if (!ReserveVisuals.TryGetValue(owner, out var state))
            ReserveVisuals[owner] = state = new ReserveVisual();
        state.Monitor = monitor.Index; state.Queue = queue; state.Renderers = renderers;
    }

    private static void RestoreReserveVisuals(int monitor)
    {
        foreach (var state in ReserveVisuals.Values)
            if (state.Monitor == monitor)
                foreach (var renderer in state.Renderers)
                    if (renderer != null && VisualOverlays.TryGetValue(renderer, out var overlay)) overlay.Restore();
    }

    private static void ApplyReserveVisuals(int monitor, NotesReader reader, VisualTimeline timeline)
    {
        var notes = ReserveVisualNotes.GetValue(reader, r => {
            var map = new Dictionary<int, NoteData>();
            foreach (var note in r.GetNoteList()) if (note != null && note.indexNote >= 0) map[note.indexNote] = note;
            return map;
        });
        foreach (var pair in ReserveVisuals)
        {
            var state = pair.Value;
            if (state.Monitor != monitor || pair.Key == null || !pair.Key.gameObject.activeInHierarchy) continue;
            for (var i = 0; i < state.Renderers.Length && i < state.Queue.Count; i++)
            {
                var index = state.Queue[i].Index;
                if (!notes.TryGetValue(index, out var note) || !VisualNotes.TryGetValue(note, out var payload)) continue;
                var live = new VisualValue();
                if (timeline != null && timeline.Streams.TryGetValue(payload.Stream, out var changes))
                    live = VisualState.Resolve(changes, NotesManager.GetCurrentMsec(), payload.Note);
                // A reserve ring represents its queued note, not the currently hit Touch.
                // Only alpha follows here; native/mine artwork and geometry stay intact.
                ApplyVisualPart(new VisualPart { Native = state.Renderers[i], Hint = true },
                    new VisualValue { Alpha = live.Alpha }, new VisualValue { Alpha = payload.Note.Base.Alpha });
            }
        }
    }
}
