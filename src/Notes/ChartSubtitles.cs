using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Compatibility;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static ConditionalWeakTable<NotesReader, List<SubtitleChange>> SubtitleEvents = new();
    private static readonly Dictionary<GameMonitor, ReferenceSubtitleDisplay> SubtitleDisplays = new();
    private static List<(int Bar, int Grid, string Text)> PendingSubtitles => RuntimeCharts.Current.PendingSubtitles;

    private static void ResetSubtitleCommands(NotesReader reader)
    { PendingSubtitles.Clear(); SubtitleEvents.Remove(reader); }
    private static void ReadSubtitleCommand(string line)
    {
        if (line == null || !line.StartsWith("TEXT\t", StringComparison.Ordinal)) return;
        var p = line.Split('\t');
        if (p.Length == 4 && int.TryParse(p[1], out var bar) && int.TryParse(p[2], out var grid))
            PendingSubtitles.Add((bar, grid, p[3]));
    }
    private static void BuildSubtitleCommands(NotesReader reader)
    {
        var changes = new List<SubtitleChange>();
        foreach (var item in PendingSubtitles)
        {
            var time = new NotesTime(); time.init(item.Bar, item.Grid, reader);
            if (SubtitleCommands.Decode(item.Text, time.msec, out var change)) changes.Add(change);
            else MelonLogger.Warning("[Chart Subtitle] Invalid TEXT payload at " + item.Bar + ":" + item.Grid);
        }
        SubtitleEvents.Remove(reader);
        if (changes.Count != 0)
        {
            SubtitleEvents.Add(reader, changes);
            MelonLogger.Msg("[Chart Subtitle] Loaded " + changes.Count + " events");
        }
    }
    private static void ApplySubtitlePresentation(GameMonitor monitor)
    {
        var reader = NotesManager.Instance(monitor.MonitorIndex).getReader();
        var main = Traverse.Create(monitor).Field("Main").GetValue<CanvasGroup>();
        if (GuiSizes.SinglePlayer && monitor.MonitorIndex != 0 || main == null ||
            !SubtitleEvents.TryGetValue(reader, out var events))
        {
            if (SubtitleDisplays.TryGetValue(monitor, out var stale))
            { stale.Stop(); Object.Destroy(stale); SubtitleDisplays.Remove(monitor); }
            return;
        }
        if (!SubtitleDisplays.TryGetValue(monitor, out var display) || display == null)
            SubtitleDisplays[monitor] = display = monitor.gameObject.AddComponent<ReferenceSubtitleDisplay>();
        display.Bind(monitor, main, events);
    }
    private static void ReleaseSubtitlePresentation()
    {
        foreach (var display in SubtitleDisplays.Values) if (display != null) { display.Stop(); Object.Destroy(display); }
        SubtitleDisplays.Clear(); PendingSubtitles.Clear(); SubtitleEvents = new();
    }
}

// Use the game's existing TMP font atlas and fallback chain. No bundled font
// data, OS font scan or per-event font import is needed during gameplay.
public sealed class ReferenceSubtitleDisplay : MonoBehaviour
{
    private sealed class Label
    {
        public SubtitleChange Event;
        public TMPro.TextMeshProUGUI Text;
    }
    private GameMonitor owner;
    private CanvasGroup main;
    private List<SubtitleChange> events;
    private SubtitleCommands.Timeline timeline;
    private RectTransform root;
    private readonly Dictionary<SubtitleChange, Label> labels = new();
    private readonly List<Label> previous = new();
    private TMPro.TMP_FontAsset font;

    internal static TMPro.TMP_FontAsset NativeFont(CanvasGroup group)
    {
        var canvas = group != null ? group.GetComponentInParent<Canvas>() : null;
        if (canvas != null)
            foreach (var text in canvas.GetComponentsInChildren<TMPro.TMP_Text>(true))
                if (text.font != null && text.font.name.IndexOf("MaruGothic", StringComparison.OrdinalIgnoreCase) >= 0) return text.font;
        var all = Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
        foreach (var item in all)
            if (item != null && item.name.IndexOf("MaruGothic", StringComparison.OrdinalIgnoreCase) >= 0) return item;
        foreach (var item in all)
            if (item != null && item.name.IndexOf("SEGA", StringComparison.OrdinalIgnoreCase) >= 0) return item;
        return TMPro.TMP_Settings.defaultFontAsset;
    }
    internal void Bind(GameMonitor monitor, CanvasGroup group, List<SubtitleChange> changes)
    {
        owner = monitor; main = group; enabled = true;
        if (ReferenceEquals(events, changes)) return;
        ClearLabels(); events = changes; timeline = new SubtitleCommands.Timeline(changes);
        font = NativeFont(group);
        if (font == null) { MelonLogger.Warning("[Chart Subtitle] Native game font unavailable"); return; }
        var obj = new GameObject("Sinmai-Alpha Native Text", typeof(RectTransform), typeof(Canvas));
        obj.layer = group.gameObject.layer; root = (RectTransform)obj.transform;
        root.SetParent(group.transform, false); root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;
        var canvas = obj.GetComponent<Canvas>(); canvas.overrideSorting = true; canvas.sortingOrder = 32760;
        foreach (var ev in changes)
        {
            var labelObject = new GameObject("Chart text " + ev.Index, typeof(RectTransform));
            labelObject.layer = obj.layer; labelObject.transform.SetParent(root, false);
            var text = labelObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.font = font; text.text = ev.Text; text.fontSize = ev.Size > 0 ? ev.Size : 32;
            text.fontStyle = string.IsNullOrEmpty(ev.Font) || ev.Font.Equals("Default", StringComparison.OrdinalIgnoreCase)
                ? TMPro.FontStyles.Bold : TMPro.FontStyles.Normal;
            text.alignment = TMPro.TextAlignmentOptions.Center; text.enableWordWrapping = true;
            text.richText = false; text.raycastTarget = false;
            var rect = text.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            // Generate glyph meshes at chart binding, not the event's first frame.
            text.ForceMeshUpdate(); text.enabled = false;
            labels[ev] = new Label { Event = ev, Text = text };
        }
    }
    internal void Stop()
    {
        enabled = false;
        foreach (var label in previous) if (label.Text != null) label.Text.enabled = false;
        previous.Clear();
    }
    private void LateUpdate()
    {
        foreach (var label in previous) if (label.Text != null) label.Text.enabled = false;
        previous.Clear();
        if (owner == null || main == null || root == null || !main.gameObject.activeInHierarchy || main.alpha <= 0 ||
            timeline == null || GuiSizes.SinglePlayer && owner.MonitorIndex != 0) return;
        var now = NotesManager.GetCurrentMsec();
        foreach (var ev in timeline.At(now))
        {
            if (!labels.TryGetValue(ev, out var label) || label.Text == null) continue;
            var elapsed = Mathf.Max(0, (now - (float)ev.Time) * .001f);
            var typewriter = ev.Style.Equals("Typewriter", StringComparison.OrdinalIgnoreCase);
            var value = typewriter ? SubtitleCommands.TypewriterText(ev.Text, elapsed, Mathf.Max(0, ev.Transition)) : ev.Text;
            if (label.Text.text != value) label.Text.text = value;
            label.Text.color = new Color(1, 1, 1, !typewriter && ev.Transition > 0 ? Mathf.Clamp01(elapsed / ev.Transition) : 1);
            label.Text.rectTransform.anchoredPosition = new Vector2(ev.X * root.rect.width, -ev.Y * root.rect.height);
            label.Text.enabled = true; previous.Add(label);
        }
    }
    private void ClearLabels()
    {
        Stop(); enabled = true; labels.Clear();
        if (root != null) { root.gameObject.SetActive(false); Object.Destroy(root.gameObject); }
        root = null;
    }
    private void OnDestroy() { ClearLabels(); events = null; timeline = null; }
}
