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
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Isolated ownership avoids touching native outline, note or guide pools.
    private static class JudgeLineExpansionRuntime
    {
        private sealed class Timeline
        {
            public JudgeLineExpansionCommands.Track Track;
            public JudgeLineCommands.Track Opacity;
        }
        private sealed class Lease
        {
            public NotesReader Reader;
            public Timeline Timeline;
            public SpriteRenderer Source, Copy;
            public void Dispose()
            {
                if (Copy == null) return;
                Copy.enabled = false;
                Object.Destroy(Copy.gameObject);
            }
            public void Apply(double milliseconds)
            {
                if (Source == null || Copy == null) return;
                if (!Timeline.Track.Evaluate(milliseconds, out var angle)) { Copy.enabled = false; return; }
                Copy.gameObject.layer = Source.gameObject.layer;
                Copy.sprite = Source.sprite;
                Copy.sharedMaterial = Source.sharedMaterial;
                Copy.sortingLayerID = Source.sortingLayerID;
                Copy.sortingOrder = Source.sortingOrder + 1;
                Copy.drawMode = Source.drawMode;
                Copy.size = Source.size;
                Copy.tileMode = Source.tileMode;
                Copy.adaptiveModeThreshold = Source.adaptiveModeThreshold;
                Copy.spriteSortPoint = Source.spriteSortPoint;
                Copy.flipX = Source.flipX; Copy.flipY = Source.flipY;
                Copy.maskInteraction = Source.maskInteraction;
                Copy.transform.localPosition = Vector3.zero;
                Copy.transform.localRotation = Quaternion.Euler(0, 0, angle);
                Copy.transform.localScale = Vector3.one;
                var opacity = Timeline.Opacity.Opacity(milliseconds);
                // The reference intentionally keeps the copy white when JLINE
                // recolors the original. JUDGELINE controls both transparencies.
                Copy.color = new Color(1, 1, 1, opacity);
                Copy.enabled = Source.enabled && Source.sprite != null && opacity > .001f;
            }
        }
        private static readonly ConditionalWeakTable<NotesReader, Timeline> Timelines = new();
        private static readonly Dictionary<GameMonitor, Lease> Leases = new();

        public static void Build(NotesReader reader, IEnumerable<PresentationChange> changes)
        {
            Reset(reader);
            var events = changes.ToArray();
            if (events.Any(c => JudgeLineExpansionCommands.IsKind(c.Kind)))
                Timelines.Add(reader, new Timeline { Track = new JudgeLineExpansionCommands.Track(events),
                    Opacity = new JudgeLineCommands.Track(events, new JudgeLineCommands.Tint(1, 1, 1, 1)) });
        }
        public static void Apply(GameMonitor monitor)
        {
            if (monitor == null) return;
            if (GuiSizes.SinglePlayer && monitor.MonitorIndex != 0) { Release(monitor); return; }
            var reader = NotesManager.Instance(monitor.MonitorIndex).getReader();
            if (reader == null || !Timelines.TryGetValue(reader, out var timeline)) { Release(monitor); return; }
            var controller = Traverse.Create(monitor).Field("GameController").GetValue<GameCtrl>();
            var outline = controller != null ? Traverse.Create(controller).Field("_guideEndPointObj").GetValue<GameObject>() : null;
            var source = outline != null ? outline.GetComponent<SpriteRenderer>() : null;
            if (source == null) { Release(monitor); return; }
            if (!Leases.TryGetValue(monitor, out var lease) || lease.Reader != reader || lease.Timeline != timeline || lease.Source != source || lease.Copy == null)
            {
                Release(monitor);
                var go = new GameObject("ChartJudgeLineExpansion", typeof(SpriteRenderer));
                go.transform.SetParent(source.transform, false);
                var copy = go.GetComponent<SpriteRenderer>(); copy.enabled = false;
                lease = new Lease { Reader = reader, Timeline = timeline, Source = source, Copy = copy };
                Leases.Add(monitor, lease);
            }
            lease.Apply(NotesManager.GetCurrentMsec());
        }
        private static void Release(GameMonitor monitor)
        {
            if (!Leases.TryGetValue(monitor, out var lease)) return;
            lease.Dispose(); Leases.Remove(monitor);
        }
        public static void Reset(NotesReader reader)
        {
            foreach (var monitor in Leases.Where(p => p.Value.Reader == reader).Select(p => p.Key).ToArray()) Release(monitor);
            Timelines.Remove(reader);
        }
        public static void ReleaseAll()
        {
            foreach (var lease in Leases.Values) lease.Dispose();
            Leases.Clear();
        }
    }
}
