using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class SkinNote { public string Path, Family; public NoteSkinAssets Assets; }
    private sealed class SkinLease
    {
        public SkinNote Note;
        public SpriteRenderer Render;
        public Sprite Model, Original, AppliedSprite;
        public int Monitor;
        public bool Applied;
        public void Restore()
        {
            if (Applied && Render != null && Render.sprite == AppliedSprite) Render.sprite = Original;
            Applied = false;
        }
    }
    private static readonly ConditionalWeakTable<NoteData, SkinNote> NoteSkins = new();
    private static ConditionalWeakTable<NotesReader, NoteSkinAssets> SkinReaders = new();
    private static readonly HashSet<NoteSkinAssets> SkinStores = new();
    private static readonly Dictionary<NoteBase, SkinLease> SkinOwners = new();
    private static string pendingNoteSkin { get => RuntimeCharts.Current.pendingNoteSkin; set => RuntimeCharts.Current.pendingNoteSkin = value; }
    private static NoteSkinAssets SkinStore(NotesReader reader)
    {
        var store = SkinReaders.GetOrCreateValue(reader); SkinStores.Add(store); return store;
    }
    private static void ReadNoteSkinMarker(MA2Record rec)
    {
        pendingNoteSkin = null;
        if (rec?._str == null || rec._str.Count < 5) return;
        var tail = rec._str[rec._str.Count - 1];
        if (!tail.StartsWith("SK1|", StringComparison.Ordinal)) return;
        pendingNoteSkin = tail; rec._str.RemoveAt(rec._str.Count - 1);
    }
    private static void RestoreNoteSkinMarker(MA2Record rec)
    {
        if (pendingNoteSkin != null) rec._str.Add(pendingNoteSkin);
    }
    private static void ApplyNoteSkinMarker(NoteData note, NotesReader reader, MA2Record rec)
    {
        NoteSkins.Remove(note);
        if (pendingNoteSkin == null) return;
        if (NoteSkin.Decode(pendingNoteSkin, out var path))
        {
            var tag = rec._str[0];
            // The supplied reference applies regular per-note skins in TapBase
            // (Tap and single Star). Hold/Touch/TouchHold currently only carry
            // the metadata. Preserve that distinction; no extra fan/halo swap.
            var family = tag.EndsWith("TAP", StringComparison.Ordinal) || tag == "BRK" || tag == "XTP" ? "tap" :
                tag.EndsWith("STR", StringComparison.Ordinal) || tag == "BST" || tag == "XST" ? "star" : "";
            NoteSkins.Add(note, new SkinNote { Path = path, Family = family, Assets = SkinStore(reader) });
        }
        else MelonLogger.Warning("[Note Skin] Invalid note metadata: " + pendingNoteSkin);
        pendingNoteSkin = null;
    }
    private static void ResetNoteSkinAssets(NotesReader reader)
    {
        if (!SkinReaders.TryGetValue(reader, out var store)) return;
        foreach (var pair in SkinOwners.Where(p => p.Value.Note.Assets == store).ToArray())
        { RestoreVisualLease(pair.Key); pair.Value.Restore(); SkinOwners.Remove(pair.Key); }
        store.Clear(); SkinStores.Remove(store); SkinReaders.Remove(reader);
    }
    private static void ReleaseNoteSkinAssets()
    {
        foreach (var pair in SkinOwners) { RestoreVisualLease(pair.Key); pair.Value.Restore(); }
        SkinOwners.Clear();
        foreach (var store in SkinStores) store.Clear();
        SkinStores.Clear(); SkinReaders = new();
    }
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static class NoteSkinRootPatch
    {
        [HarmonyPrefix]
        public static void Prefix(NotesReader __instance, string fileName)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            try { SkinStore(__instance).Root = Path.GetDirectoryName(Path.GetFullPath(fileName)); }
            catch { MelonLogger.Warning("[Note Skin] Cannot resolve chart directory: " + fileName); }
        }
    }
    [HarmonyPatch(typeof(GameCtrl), "IsReady")]
    public static class NoteSkinReadyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameCtrl __instance, ref bool __result)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (GuiSizes.SinglePlayer && __instance.MonitorIndex != 0) return;
            var reader = NotesManager.Instance(__instance.MonitorIndex).getReader();
            if (!SkinReaders.TryGetValue(reader, out var store)) return;
            var paths = reader.GetNoteList().Select(n => NoteSkins.TryGetValue(n, out var info) && info.Family.Length > 0 ? info.Path : null).Where(p => p != null);
            store.Prepare(paths);
            if (!store.PreloadTick()) __result = false;
        }
    }
    [HarmonyPatch]
    public static class NoteSkinInitializePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m => typeof(NoteBase).IsAssignableFrom(m.DeclaringType) && m.Name == "Initialize"
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(NoteData));
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(NoteBase __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (SkinOwners.TryGetValue(__instance, out var old)) old.Restore();
            SkinOwners.Remove(__instance);
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(NoteBase __instance, NoteData note)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (IsBorrowed(note) || !NoteSkins.TryGetValue(note, out var info) || info.Family.Length == 0) return;
            var render = Traverse.Create(__instance).Field("SpriteRender").GetValue<SpriteRenderer>();
            if (render == null) return;
            var option = Singleton<GamePlayManager>.Instance.GetGameScore(__instance.MonitorId).UserOption;
            var model = info.Family == "star" ? GameNoteImageContainer.NormalStar[(int)option.SlideDesign, (int)option.StarType] : GameNoteImageContainer.NormalTap[(int)option.TapDesign];
            SkinOwners[__instance] = new SkinLease { Note = info, Render = render, Model = model, Monitor = __instance.MonitorId };
        }
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First - 10), HarmonyPatch(typeof(GameCtrl), "UpdateNotes")]
    public static void BeforeSkinFrame(GameCtrl __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        // Visual overlays restore first, then we restore the native picture for
        // the original update. Frame-end skin swap precedes COLOR/SIZE overlays.
        foreach (var lease in SkinOwners.Values) if (lease.Monitor == __instance.MonitorIndex) lease.Restore();
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last + 10), HarmonyPatch(typeof(GameCtrl), "UpdateNotes")]
    public static void AfterSkinFrame(GameCtrl __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        foreach (var pair in SkinOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).ToArray())
        {
            var owner = pair.Key; var lease = pair.Value;
            if (owner == null || !owner.gameObject.activeInHierarchy || owner.IsEnd()) { lease.Restore(); SkinOwners.Remove(owner); continue; }
            if (owner is StarNote && Traverse.Create(owner).Field("MultiSlide").GetValue<bool>()) continue;
            var skin = lease.Note.Assets.Create(lease.Note.Path, lease.Model);
            if (skin == null) continue;
            lease.Original = lease.Render.sprite; lease.AppliedSprite = skin; lease.Applied = true; lease.Render.sprite = skin;
        }
    }
    [HarmonyPrefix, HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static void CollectSkinNotes(GameCtrl __instance)
    {
        foreach (var pair in SkinOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).ToArray())
        { pair.Value.Restore(); SkinOwners.Remove(pair.Key); }
        // Keep decoded assets for practice rewind; release happens on chart
        // reload/game release, after all dependent renderers have been restored.
    }
}
