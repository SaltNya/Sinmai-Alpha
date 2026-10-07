using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class FakeMarker { }
    private sealed class FakeLease
    {
        public NoteData Note;
        public float EndMsec;
        public int Monitor;
    }
    private static readonly ConditionalWeakTable<NoteData, FakeMarker> FakeNotes = new();
    private static readonly Dictionary<Component, FakeLease> FakeOwners = new();
    private static readonly ConditionalWeakTable<Transform, Transform> FakeLaunchers = new();
    private static readonly HashSet<Transform> FakeLauncherTransforms = new();
    private static bool pendingFakeMarker { get => RuntimeCharts.Current.pendingFakeMarker; set => RuntimeCharts.Current.pendingFakeMarker = value; }
    private static bool removedFakeMarker { get => RuntimeCharts.Current.removedFakeMarker; set => RuntimeCharts.Current.removedFakeMarker = value; }

    // FK precedes the optional stream id and follows all native fields/DZ.
    private static void ReadFakeMarker(MA2Record rec)
    {
        pendingFakeMarker = removedFakeMarker = false;
        if (rec?._str == null || rec._str.Count < 5 || rec._str[rec._str.Count - 1] != "FK") return;
        pendingFakeMarker = removedFakeMarker = true;
        rec._str.RemoveAt(rec._str.Count - 1);
    }
    private static void RestoreFakeMarker(MA2Record rec)
    {
        if (removedFakeMarker) rec._str.Add("FK");
        removedFakeMarker = false;
    }
    private static void ApplyFakeMarker(NoteData note)
    {
        FakeNotes.Remove(note);
        if (pendingFakeMarker)
        {
            FakeNotes.Add(note, new FakeMarker());
            note.playAnsSoundHead = note.playAnsSoundTail = true;
        }
        pendingFakeMarker = false;
    }
    public static bool IsFakeNote(NoteData note) => note != null && FakeNotes.TryGetValue(note, out _);
    public static bool IsFakeNoteOwner(Component owner) => owner != null && FakeOwners.ContainsKey(owner);

    public static NoteDataList ScoringNoteList(NoteDataList source)
    {
        if (!source.Any(IsFakeNote)) return source;
        var result = new NoteDataList();
        foreach (var note in source) if (!IsFakeNote(note)) result.Add(note);
        return result;
    }

    private static IEnumerable<MethodInfo> FakeOwnerMethods()
        => new[] { typeof(NoteBase).Assembly, typeof(CustomNoteTypes).Assembly }.Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(NoteBase).IsAssignableFrom(t) || typeof(SlideRoot).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsAbstract && m.GetMethodBody() != null);

    [HarmonyPatch]
    public static class FakeInitializePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m => m.Name == "Initialize"
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(NoteData));

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(Component __instance, object[] __args)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            FakeOwners.Remove(__instance);
            var note = (NoteData)__args[0];
            if (!IsFakeNote(note)) return;
            var fields = Traverse.Create(__instance);
            var monitor = __instance is NoteBase tap ? tap.MonitorId : ((SlideRoot)__instance).MonitorId;
            FakeOwners[__instance] = new FakeLease { Note = note, EndMsec = fields.Field("TailMsec").GetValue<float>(), Monitor = monitor };
            // Share native visuals, never native sibling input queues. D-zone
            // binding has already placed its launcher at the physical D angle.
            if (!(__instance is NoteBase)) return;
            var native = __instance.transform.parent;
            if (native == null || FakeLauncherTransforms.Contains(native)) return;
            if (!FakeLaunchers.TryGetValue(native, out var visual) || visual == null)
            {
                FakeLaunchers.Remove(native);
                visual = new GameObject("AquaMai Fake Visual Launcher").transform;
                visual.SetParent(native.parent, false);
                visual.localPosition = native.localPosition;
                visual.localRotation = native.localRotation;
                visual.localScale = native.localScale;
                FakeLaunchers.Add(native, visual);
                FakeLauncherTransforms.Add(visual);
            }
            __instance.transform.SetParent(visual, false);
        }
    }

    [HarmonyPatch]
    public static class FakeGameplayGuardPatch
    {
        private static readonly HashSet<string> Names = new()
        {
            "NoteCheck", "NoteCheck_old", "IsJudgeNote", "Judge", "JudgeToolate", "SetAutoPlayJudge",
            "SetChainPlayResult", "SetTouchHoldChainPlayResult", "SetForcePlayResult", "SetPlayResult", "EndNote",
            "PlayJudgeSe", "PlayJudgeHeadSe", "ReserveSlideTouchSe", "ReserveSlideSe"
        };
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m => Names.Contains(m.Name));
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static bool Prefix(Component __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }
        return !IsFakeNoteOwner(__instance);
    }
    }

    [HarmonyPatch(typeof(NotesReader), "calcTotal")]
    public static class FakeTotalPatch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = AccessTools.Field(typeof(NotesData), "_noteData");
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, list))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CustomNoteTypes), nameof(ScoringNoteList)));
            }
        }
    }

    [HarmonyPatch(typeof(GameScoreList), "Initialize")]
    public static class FakeScoreInitializePatch
    {
        [HarmonyPostfix]
        public static void Postfix(int monitorIndex, ref JudgeResultSt[] ____judgeResultList)
        {
        if (!(CustomNoteTypes.FeaturesForMonitor(monitorIndex))) {  return; }

            var notes = NotesManager.Instance(monitorIndex)?.getReader()?.GetNoteList();
            if (notes == null || !notes.Any(IsFakeNote)) return;
            // Native results use the full reader's indexNote. Totals exclude
            // fake carriers, so the native allocation is shorter than that
            // index space. Keep its real results and reserve neutral slots.
            Array.Resize(ref ____judgeResultList, notes.Count);
            for (var i = 0; i < notes.Count; i++)
            {
                if (!IsFakeNote(notes[i]) && !notes[i].type.isConnectSlide()) continue;
                ____judgeResultList[i] = new JudgeResultSt
                {
                    Judged = true,
                    Type = NoteScore.EScoreType.End,
                    Timing = (int)NoteJudge.ETiming.End
                };
            }
        }
    }

    [HarmonyPatch(typeof(NotesReader), "IsTouchNext")]
    public static class FakeTouchChainPatch
    {
        public static bool Prefix(NoteData temp, NoteData link, ref bool __result)
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return true; }

            if (!IsFakeNote(temp) && !IsFakeNote(link)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameScoreList), "SetResult")]
    public static class FakeResultPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static bool Prefix(int ____monitorIndex, int index)
        {
        if (!(CustomNoteTypes.FeaturesForMonitor(____monitorIndex))) {  return true; }

            var list = NotesManager.Instance(____monitorIndex)?.getReader()?.GetNoteList();
            return list == null || index < 0 || index >= list.Count || !IsFakeNote(list[index]);
        }
    }

    [HarmonyPatch(typeof(GameCtrl), "GetTouchReserve")]
    public static class FakeTouchReservePatch
    {
        public static void Postfix(GameCtrl __instance, List<TouchReserve.ReserveData> touchNoteIndexs)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            var notes = NotesManager.Instance(__instance.MonitorIndex).getReader().GetNoteList();
            touchNoteIndexs.RemoveAll(item => item.Index >= 0 && item.Index < notes.Count && IsFakeNote(notes[item.Index]));
        }
    }

    private static void RetireFake(Component owner, FakeLease lease)
    {
        var fields = Traverse.Create(owner);
        fields.Field("EndFlag").SetValue(true);
        if (owner is SlideRoot slide)
        {
            slide.ResetArrowObject();
        }
        else if (owner is NoteBase)
        {
            fields.Field("NoteStat").SetValue(NoteBase.NoteStatus.End);
            if (fields.Field("NeedGuide").GetValue<bool>())
                fields.Field("GuideObj").GetValue<NoteGuide>()?.ReturnToBase();
        }
        // Lifecycle completion never calls EndNote/SetResult or grade/effect/audio.
        lease.Note.isJudged = true;
        owner.gameObject.SetActive(false);
        var parent = owner is NoteBase note ? note.ParentTransform : ((SlideRoot)owner).ParentTransform;
        if (parent != null) owner.transform.SetParent(parent, false);
    }

    [HarmonyPatch(typeof(GameCtrl), "UpdateNotes")]
    public static class FakeLifetimePatch
    {
        public static void Postfix(GameCtrl __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            var now = NotesManager.GetCurrentMsec();
            foreach (var pair in FakeOwners.ToArray())
            {
                if (pair.Key == null) { FakeOwners.Remove(pair.Key); continue; }
                if (pair.Value.Monitor != __instance.MonitorIndex) continue;
                if (pair.Key.gameObject.activeSelf && now > pair.Value.EndMsec + 150f)
                    RetireFake(pair.Key, pair.Value);
                if (!pair.Key.gameObject.activeSelf) FakeOwners.Remove(pair.Key);
            }
        }
    }

    [HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static class FakeCollectPatch
    {
        public static void Postfix(GameCtrl __instance)
        {
            foreach (var pair in FakeOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).ToArray())
                FakeOwners.Remove(pair.Key);
            // Native practice collection clears these flags for every note.
            // Fake answer sounds must remain suppressed after a rewind as well.
            foreach (var note in NotesManager.Instance(__instance.MonitorIndex).getReader().GetNoteList())
                if (IsFakeNote(note)) note.playAnsSoundHead = note.playAnsSoundTail = true;
        }
    }
}
