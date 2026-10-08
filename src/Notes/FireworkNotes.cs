using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Type = System.Type;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class FireworkValue { public NotesReader Reader; public bool NativeTrigger; }
    private sealed class FireworkLease { public FireworkValue Value; public bool Emitted; public int Monitor; }
    private sealed class FireworkDisplay { public TapCEffect Effect; public NotesReader Reader; }
    public sealed class FireworkEndState { internal bool Eligible; internal Vector3 Position; internal GameCtrl Controller; }
    private static readonly ConditionalWeakTable<NoteData, FireworkValue> FireworkNotes = new();
    private static readonly Dictionary<NoteBase, FireworkLease> FireworkOwners = new();
    private static readonly Dictionary<int, FireworkDisplay> FireworkDisplays = new();
    private static readonly Dictionary<Type, bool> FireworkNativeTriggers = new();
    private static bool pendingFirework { get => RuntimeCharts.Current.pendingFirework; set => RuntimeCharts.Current.pendingFirework = value; }

    private static void ReadFireworkMarker(MA2Record rec)
    {
        pendingFirework = false;
        if (rec?._str == null || rec._str.Count < 5 || rec._str[rec._str.Count - 1] != "FW1") return;
        pendingFirework = true; rec._str.RemoveAt(rec._str.Count - 1);
    }
    private static void RestoreFireworkMarker(MA2Record rec) { if (pendingFirework) rec._str.Add("FW1"); }
    private static void ApplyFireworkMarker(NoteData note, NotesReader reader, MA2Record rec)
    {
        FireworkNotes.Remove(note);
        // Touch f already uses the native MA2 effect field and PlayJudgeSe.
        // Record that fact so the final-result observer cannot emit it twice.
        // Numeric/extended FW1 heads borrow the same native effect separately.
        var nativeType = note.type.getEnum();
        var touchFirework = (nativeType == NotesTypeID.Def.TouchTap || nativeType == NotesTypeID.Def.TouchHold) && (int)note.effect != 0;
        if (!pendingFirework && !touchFirework) return;
        var tag = rec._str[0];
        if (touchFirework || new[] { "TAP", "STR", "HLD", "TTP", "THO", "STP" }.Any(s => tag.EndsWith(s, StringComparison.Ordinal)) || new[] { "TAP", "BRK", "XTP", "STR", "BST", "XST", "HLD", "XHO" }.Contains(tag))
            FireworkNotes.Add(note, new FireworkValue { Reader = reader, NativeTrigger = touchFirework });
        else MelonLogger.Warning("[Firework] Invalid head metadata: " + tag);
        pendingFirework = false;
    }

    [HarmonyPatch(typeof(NoteBase), "Initialize")]
    public static class FireworkInitializePatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(NoteBase __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        FireworkOwners.Remove(__instance);
    }
        [HarmonyPostfix, HarmonyPriority(Priority.Last + 20)]
        public static void Postfix(NoteBase __instance, NoteData note)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (IsFakeNote(note) || IsBorrowed(note) || !FireworkNotes.TryGetValue(note, out var value)) return;
            if (value.NativeTrigger && UsesNativeFireworkTrigger(__instance.GetType())) return;
            FireworkOwners[__instance] = new FireworkLease { Value = value, Monitor = __instance.MonitorId };
        }
    }
    [HarmonyPatch]
    public static class FireworkEndPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m => typeof(NoteBase).IsAssignableFrom(m.DeclaringType) && m.Name == "EndNote" && m.GetParameters().Length == 0);
        [HarmonyPrefix, HarmonyPriority(Priority.First + 10)]
        public static void Prefix(NoteBase __instance, out FireworkEndState __state)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) { __state = default; return; }

            __state = null;
            if (IsFakeNoteOwner(__instance) || !FireworkOwners.TryGetValue(__instance, out var lease) || lease.Emitted || __instance.IsEnd()) return;
            var fields = Traverse.Create(__instance);
            if (fields.Field("EndFlag").GetValue<bool>()) return;
            var picture = fields.Field("NoteObj").GetValue<GameObject>()?.transform;
            // Capture before EndNote hides the picture and moves its owner to
            // the pool. Only the graphic position is observed; input launchers
            // and sensor queues retain their native transform and lifetime.
            __state = new FireworkEndState { Eligible = true, Position = picture != null ? picture.position : __instance.transform.position,
                Controller = __instance.GetComponentInParent<GameCtrl>() };
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last - 20)]
        public static void Postfix(NoteBase __instance, FireworkEndState __state)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (__state?.Eligible != true || !FireworkOwners.TryGetValue(__instance, out var lease) || lease.Emitted || IsFakeNoteOwner(__instance) ||
                !Traverse.Create(__instance).Field("EndFlag").GetValue<bool>()) return;
            lease.Emitted = true;
            // Hold/BreakHold finish JudgeTotalResult inside the original
            // EndNote. Reading here observes the scored tail result, including
            // native Break/EX and the existing Mine inversion. Never grade it.
            var result = __instance.GetJudgeResult();
            if (result == NoteJudge.ETiming.End || NoteJudge.ConvertJudge(result) == NoteJudge.JudgeBox.Miss) return;
            ShowFirework(lease.Monitor, __state, lease.Value.Reader);
        }
    }

    private static bool UsesNativeFireworkTrigger(Type type)
    {
        if (!FireworkNativeTriggers.TryGetValue(type, out var native))
        {
            // TouchStar, Break Touch and mines override PlayJudgeSe without
            // invoking the stock firework. Their final-result hook is needed.
            native = AccessTools.Method(type, "PlayJudgeSe")?.DeclaringType == typeof(TouchNoteB);
            FireworkNativeTriggers[type] = native;
        }
        return native;
    }

    private static void ShowFirework(int monitor, FireworkEndState state, NotesReader reader)
    {
        var controller = state.Controller;
        if (controller == null || controller.MonitorIndex != monitor) return;
        // GameCtrl has already selected and preloaded CenterBackEffect using
        // this player's TapDesign. Keep its textures, materials, particle
        // animation, sorting/mask relationship and Execute lifetime intact.
        // Reuse the original emitter: never allocate or move a note/pool.
        var effect = Traverse.Create(controller).Field("_tapCEffectObj").GetValue<TapCEffect>();
        if (effect == null) return;
        if (!FireworkDisplays.TryGetValue(monitor, out var lease) || lease.Effect != effect || lease.Reader != reader)
        {
            RemoveFireworkDisplay(monitor);
            FireworkDisplays[monitor] = new FireworkDisplay { Effect = effect, Reader = reader };
        }
        effect.Intialize(state.Position);
    }

    private static void RemoveFireworkDisplay(int monitor)
    {
        if (!FireworkDisplays.TryGetValue(monitor, out var lease)) return;
        // Practice rewind/reader changes clear any burst emitted by Alpha.
        // This is a borrowed native object; do not destroy it or its assets.
        if (lease.Effect != null) lease.Effect.Stop();
        FireworkDisplays.Remove(monitor);
    }

    private static void ResetFireworks(NotesReader reader = null)
    {
        var monitors = FireworkDisplays.Where(p => reader == null || p.Value.Reader == reader).Select(p => p.Key).ToArray();
        foreach (var owner in FireworkOwners.Where(p => reader == null || p.Value.Value.Reader == reader).Select(p => p.Key).ToArray()) FireworkOwners.Remove(owner);
        foreach (var monitor in monitors) RemoveFireworkDisplay(monitor);
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First + 20), HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static void CollectFireworks(GameCtrl __instance)
    {
        foreach (var owner in FireworkOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).Select(p => p.Key).ToArray()) FireworkOwners.Remove(owner);
        RemoveFireworkDisplay(__instance.MonitorIndex);
    }
}
