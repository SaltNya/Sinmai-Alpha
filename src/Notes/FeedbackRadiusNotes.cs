using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class FeedbackRadiusOwner
    {
        public ChartRuntimeState Chart;
        public Transform Launcher;
        public int Monitor;
        public float Radius, Units;
    }

    private sealed class FeedbackRadiusSlot
    {
        public ChartRuntimeState Chart;
        public Transform Root, Parent, Holder;
        public int Monitor;

        public void Reset()
        {
            Chart = null;
            if (Holder == null) return;
            Holder.localPosition = Vector3.zero;
            Holder.localRotation = Quaternion.identity;
        }

        public void Release()
        {
            Reset();
            if (Root != null && Holder != null && Root.parent == Holder)
            {
                var index = Holder.GetSiblingIndex();
                Root.SetParent(Parent, false);
                Root.SetSiblingIndex(index);
            }
            if (Holder != null) Object.Destroy(Holder.gameObject);
        }
    }

    private static readonly Dictionary<NoteBase, FeedbackRadiusOwner> FeedbackRadiusOwners = new();
    private static readonly Dictionary<Component, FeedbackRadiusSlot> FeedbackRadiusSlots = new();
    [ThreadStatic] private static NoteBase FeedbackRadiusCurrentOwner;

    private static void PositionNativeFeedback(Component feedback)
    {
        var owner = FeedbackRadiusCurrentOwner;
        FeedbackRadiusOwner binding = null;
        if (owner != null) FeedbackRadiusOwners.TryGetValue(owner, out binding);
        FeedbackRadiusSlots.TryGetValue(feedback, out var slot);
        if (binding == null || binding.Launcher == null ||
            (binding.Radius >= 0 && Mathf.Abs(binding.Radius - SpawnJudgeLine) < .0001f))
        {
            // An ordinary slot is never captured or written until an authored
            // DESTROY moves it. Its skin, native options and scene pose survive.
            slot?.Reset();
            return;
        }

        if (slot != null && (slot.Root == null || slot.Holder == null || slot.Root.parent != slot.Holder))
        {
            slot.Release(); FeedbackRadiusSlots.Remove(feedback); slot = null;
        }
        if (slot == null)
        {
            var root = feedback.transform;
            var parent = root.parent;
            if (parent == null) return;
            var index = root.GetSiblingIndex();
            var holder = new GameObject("AquaMai Feedback Radius").transform;
            holder.gameObject.layer = feedback.gameObject.layer;
            holder.SetParent(parent, false);
            root.SetParent(holder, false);
            holder.SetSiblingIndex(index);
            slot = new FeedbackRadiusSlot { Root = root, Parent = parent, Holder = holder, Monitor = binding.Monitor };
            FeedbackRadiusSlots.Add(feedback, slot);
        }

        var delta = binding.Launcher.TransformVector(new Vector3(0, (binding.Radius - SpawnJudgeLine) * binding.Units, 0));
        var offset = slot.Parent.InverseTransformVector(delta);
        var flip = binding.Radius < 0 ? Quaternion.Euler(0, 0, 180) : Quaternion.identity;
        // JudgeGrade.Initialize writes its native option-dependent position and
        // updates its animator. The holder survives those writes; compensation
        // flips the picture around its origin without flipping the skin offset.
        var nativePosition = slot.Root.localPosition;
        slot.Holder.localPosition = offset + nativePosition - flip * nativePosition;
        slot.Holder.localRotation = flip;
        slot.Chart = binding.Chart;
    }

    [HarmonyPatch]
    public static class FeedbackRadiusInitializePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m =>
            typeof(NoteBase).IsAssignableFrom(m.DeclaringType) && m.Name == "Initialize" &&
            m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(NoteData));

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(NoteBase __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        FeedbackRadiusOwners.Remove(__instance);
    }

        [HarmonyPostfix, HarmonyPriority(Priority.Last - 10)]
        public static void Postfix(NoteBase __instance, NoteData note)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (IsFakeNote(note)) return;
            var family = SpeedClass(note).Family;
            if (family != "tap" && family != "hold" && family != "star") return;
            var chart = RuntimeCharts.Note(note);
            var radius = chart.RingValuesByNoteIndex.TryGetValue(note.indexNote, out var value) ? value.Destroy : SpawnJudgeLine;
            var units = ((float)FEndPos.GetValue(__instance) - (float)FStartPos.GetValue(__instance)) / SpawnRadiusSpan;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || float.IsNaN(units) || float.IsInfinity(units)) return;
            FeedbackRadiusOwners[__instance] = new FeedbackRadiusOwner
            {
                Chart = chart, Launcher = __instance.transform.parent,
                Monitor = __instance.MonitorId, Radius = radius, Units = units
            };
        }
    }

    [HarmonyPatch]
    public static class FeedbackRadiusOwnerContextPatch
    {
        private static readonly HashSet<string> Names = new()
        {
            "Execute", "NoteCheck", "NoteCheck_old", "Judge", "JudgeToolate", "SetAutoPlayJudge",
            "EndNote", "SetPlayResult", "SetForcePlayResult"
        };
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m =>
            typeof(NoteBase).IsAssignableFrom(m.DeclaringType) && Names.Contains(m.Name));
        [HarmonyPrefix, HarmonyPriority(Priority.First + 90)]
        public static void Prefix(NoteBase __instance, out NoteBase __state)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) { __state = default; return; }

            __state = FeedbackRadiusCurrentOwner;
            FeedbackRadiusCurrentOwner = __instance;
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last - 90)]
        public static void Finalizer(NoteBase __state) => FeedbackRadiusCurrentOwner = __state;
    }

    [HarmonyPatch]
    public static class FeedbackRadiusEffectPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(NoteBase).Assembly.GetTypes()
            .Where(t => typeof(JudgeGrade).IsAssignableFrom(t) || typeof(TouchEffect).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsAbstract && m.GetMethodBody() != null &&
                (m.Name == "Initialize" || m.Name == "InitializeEx" || m.Name == "InitializeBreak" ||
                 m.Name == "InitializeCenter" || m.Name == "InitializeHold" || m.Name == "FinishHold"));
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(Component __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        PositionNativeFeedback(__instance);
    }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(Component __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        PositionNativeFeedback(__instance);
    }
    }

    private static void ResetFeedbackRadiusChart(ChartRuntimeState chart)
    {
        foreach (var slot in FeedbackRadiusSlots.Values.Where(s => ReferenceEquals(s.Chart, chart))) slot.Reset();
        foreach (var pair in FeedbackRadiusOwners.Where(p => ReferenceEquals(p.Value.Chart, chart)).ToArray())
            FeedbackRadiusOwners.Remove(pair.Key);
    }

    [HarmonyPatch]
    public static class FeedbackRadiusReaderResetPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(NotesReader)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "loadMa2" || m.Name == "loadStr" || m.Name == "loadDLMusicScore");
        [HarmonyPrefix, HarmonyPriority(Priority.First + 110)]
        public static void Prefix(NotesReader __instance) => ResetFeedbackRadiusChart(RuntimeCharts.Reader(__instance));
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First + 10), HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static void CollectFeedbackRadius(GameCtrl __instance)
    {
        var monitor = __instance.MonitorIndex;
        foreach (var slot in FeedbackRadiusSlots.Values.Where(s => s.Monitor == monitor)) slot.Reset();
        foreach (var owner in FeedbackRadiusOwners.Where(p => p.Value.Monitor == monitor).Select(p => p.Key).ToArray())
            FeedbackRadiusOwners.Remove(owner);
    }

    private static void ReleaseFeedbackRadius()
    {
        foreach (var slot in FeedbackRadiusSlots.Values) slot.Release();
        FeedbackRadiusSlots.Clear(); FeedbackRadiusOwners.Clear(); FeedbackRadiusCurrentOwner = null;
    }

    [HarmonyPatch]
    public static class FeedbackRadiusReleasePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => new[] { "OnStart", "OnRelease" }
            .Select(name => AccessTools.DeclaredMethod(typeof(GameProcess), name));
        [HarmonyPrefix]
        public static void Prefix() {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }
        ReleaseFeedbackRadius();
    }
    }
}
