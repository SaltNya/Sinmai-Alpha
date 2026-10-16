using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private const int DefaultMineArrowCapacity = 1024;

    private static bool IsConnectedArrowSlide(NoteData note)
    {
        if (note?.child == null) return false;
        var type = note.type.getEnum();
        if (type != NotesTypeID.Def.Slide && type != NotesTypeID.Def.BreakSlide) return false;
        foreach (var child in note.child)
            if (child != null && child.type.getEnum() == NotesTypeID.Def.ConnectSlide) return true;
        return false;
    }

    // Scan after the chart manager is attached, not every frame. Prewarm only the largest
    // long chain of each arrow type, rather than multiplying every chart's pool.
    internal static int[] LongMineChainTargets(IEnumerable<NoteData> notes, Func<NoteData, int> count)
    {
        var targets = new int[2];
        foreach (var note in notes)
        {
            if (!IsConnectedArrowSlide(note) || GetNoteKind(note) != CustomNoteKind.Mine) continue;
            var required = count(note);
            if (required <= DefaultMineArrowCapacity) continue;
            var index = note.type.getEnum() == NotesTypeID.Def.BreakSlide ? 1 : 0;
            targets[index] = Math.Max(targets[index], required);
        }
        return targets;
    }

    private static void PrewarmLongMineChains(GameCtrl controller, Dictionary<string, object> pools)
    {
        try
        {
            var notes = Traverse.Create(controller).Field("NoteMng").GetValue<NotesManager>()?.getReader()?.GetNoteList();
            if (notes == null || !pools.TryGetValue("_slideObjectList", out var slides) ||
                ((List<SlideRoot>)slides).Count == 0) return;
            var counter = ((List<SlideRoot>)slides)[0];
            var button = counter.ButtonId;
            var monitor = counter.MonitorId;
            int[] targets;
            try
            {
                counter.MonitorId = controller.MonitorIndex;
                targets = LongMineChainTargets(notes, note =>
                {
                    counter.ButtonId = note.startButtonPos;
                    return counter.GetSlideArrowNum(note);
                });
            }
            finally { counter.ButtonId = button; counter.MonitorId = monitor; }
            EnsureMineArrowCapacity(controller, pools, false, targets[0]);
            EnsureMineArrowCapacity(controller, pools, true, targets[1]);
        }
        catch (Exception error)
        {
            MelonLogger.Warning($"[Slide Pool] Long-chain prewarm unavailable; registration fallback remains active: {error.Message}");
        }
    }

    // Native GameMonitor calls Initialize/CreateNotePool BEFORE SetNoteManager.
    // Reading controller.NoteMng inside CreateNotePool silently skips prewarm.
    // SetNoteManager runs during loading, before any gameplay registration.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameCtrl), "SetNoteManager")]
    public static void PrewarmLongMineChainsAfterManager(GameCtrl __instance)
    {
        if (FeaturesEnabled(__instance) && MinePools.TryGetValue(__instance, out var pools))
            PrewarmLongMineChains(__instance, pools);
    }

    private static readonly Func<SlideRoot, int> NativeSlideArrowCount =
        (Func<SlideRoot, int>)Delegate.CreateDelegate(typeof(Func<SlideRoot, int>),
            AccessTools.Method(typeof(SlideRoot), "GetSlideArrowNum", System.Type.EmptyTypes));
    private static readonly AccessTools.FieldRef<SlideRoot, bool> ArrowBreakFlag = AccessTools.FieldRefAccess<SlideRoot, bool>("BreakFlag");
    private static readonly AccessTools.FieldRef<SlideRoot, List<SpriteRenderer>> ArrowRenderers = AccessTools.FieldRefAccess<SlideRoot, List<SpriteRenderer>>("_spriteRenders");
    private static readonly AccessTools.FieldRef<SlideRoot, List<BreakSlide>> BreakArrowRenderers = AccessTools.FieldRefAccess<SlideRoot, List<BreakSlide>>("_breakSpriteRenders");

    // Clamp only the drawing loops, never the hit areas, input progress, or
    // geometric arrow count used for allocating/positioning a connected path.
    internal static int AvailableSlideArrowCount(SlideRoot slide, ref int cached)
    {
        if (cached >= 0) return cached;
        var required = NativeSlideArrowCount(slide);
        if (!FeaturesEnabled(slide)) return cached = required;
        var available = ArrowBreakFlag(slide) ? BreakArrowRenderers(slide)?.Count ?? 0 : ArrowRenderers(slide)?.Count ?? 0;
        return cached = Math.Max(0, Math.Min(required, available));
    }

    [HarmonyPatch(typeof(SlideRoot), "NoteCheck")]
    public static class SlideArrowDrawingBounds
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, System.Reflection.Emit.ILGenerator generator)
        {
            var original = AccessTools.Method(typeof(SlideRoot), "GetSlideArrowNum", System.Type.EmptyTypes);
            var bounded = AccessTools.Method(typeof(CustomNoteTypes), nameof(AvailableSlideArrowCount));
            // A long connected path used to be recounted once per arrow in
            // the loop condition. Lazily compute it once per NoteCheck call;
            // frames without input do not need to calculate it at all.
            var cached = generator.DeclareLocal(typeof(int));
            yield return new CodeInstruction(OpCodes.Ldc_I4_M1);
            yield return new CodeInstruction(OpCodes.Stloc, cached);
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original))
                {
                    var address = new CodeInstruction(OpCodes.Ldloca, cached);
                    address.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    address.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                    yield return address;
                    instruction.opcode = OpCodes.Call; instruction.operand = bounded;
                }
                yield return instruction;
            }
        }
    }

    // RegistNote requests the complete connected-path count BEFORE enumerating
    // its arrow pool. Grow that player's routed pool only once, accounting for
    // arrows already leased by other simultaneously visible slides.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SlideRoot), "GetSlideArrowNum", new[] { typeof(NoteData) })]
    public static void PrepareMineArrowCapacity(NoteData note, int __result)
    {
        var route = PoolRoutes.Current;
        if (route == null || route.ArrowsPrepared || !ReferenceEquals(route.Note, note) ||
            route.Kind != CustomNoteKind.Mine || __result <= DefaultMineArrowCapacity || !IsConnectedArrowSlide(note)) return;
        var kind = note.type.getEnum();
        if (kind != NotesTypeID.Def.Slide && kind != NotesTypeID.Def.BreakSlide) return;
        if (!MinePools.TryGetValue(route.Controller, out var pools)) return;
        EnsureMineArrowCapacity(route.Controller, pools, kind == NotesTypeID.Def.BreakSlide, __result);
        route.ArrowsPrepared = true;
    }

    private static void EnsureMineArrowCapacity(GameCtrl controller, Dictionary<string, object> pools, bool broken, int required)
    {
        if (required <= DefaultMineArrowCapacity) return;
        var parent = Traverse.Create(controller).Field("_slideListParent").GetValue<GameObject>();
        if (parent == null) return;
        if (!broken && pools.TryGetValue("_arrowObjectList", out var normal))
            GrowMineArrows((List<SpriteRenderer>)normal, required,
                () => { var item = UnityEngine.Object.Instantiate(GameNotePrefabContainer.Arrow, parent.transform); item.gameObject.SetActive(false); ApplyMineArrowTexture(item); return item; });
        else if (broken && pools.TryGetValue("_breakArrowObjectList", out var critical))
            GrowMineArrows((List<BreakSlide>)critical, required,
                () => { var item = UnityEngine.Object.Instantiate(GameNotePrefabContainer.BreakArrow, parent.transform); item.gameObject.SetActive(false); ApplyMineBreakArrowTexture(item); return item; });
    }

    private static void GrowMineArrows<T>(List<T> pool, int required, Func<T> create) where T : Component
    {
        var free = 0;
        foreach (var item in pool) if (item != null && !item.gameObject.activeSelf) free++;
        if (free >= required) return;
        var before = pool.Count;
        for (var i = free; i < required; i++) pool.Add(create());
        MelonLogger.Warning($"[Slide Pool] {typeof(T).Name} mine arrows expanded {before}->{pool.Count}; requested={required}, previouslyFree={free}");
    }
}
