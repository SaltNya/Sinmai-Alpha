using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    [HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static class PracticePoolCollectPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        public static void Prefix(GameCtrl __instance)
        {
            if (__instance == null || !MinePools.ContainsKey(__instance) && !TouchBreakPools.ContainsKey(__instance) &&
                !TouchStarPools.ContainsKey(__instance) && !MineTouchStarPools.ContainsKey(__instance)) return;
            // AquaMai practice seeks (including loops and pause/resume) use
            // this native endpoint. Its body knows only the native pool fields.
            // Release Alpha's leases too, after visual leases are restored and
            // before native guides, active-arrow lists and note flags are reset.
            var retired = new HashSet<Component>();
            CollectPracticePool(__instance, MinePools, retired);
            CollectPracticePool(__instance, TouchBreakPools, retired);
            CollectPracticePool(__instance, TouchStarPools, retired);
            CollectPracticePool(__instance, MineTouchStarPools, retired);
            // Registration precedes UpdateNotes after a seek. Remove these old
            // references now, before the same pool object can be registered twice.
            Traverse.Create(__instance).Field("_activeNoteList").GetValue<List<NoteBase>>()?
                .RemoveAll(note => retired.Contains(note));
            Traverse.Create(__instance).Field("_activeSlideList").GetValue<List<SlideRoot>>()?
                .RemoveAll(slide => retired.Contains(slide));
        }
    }

    private static void CollectPracticePool(GameCtrl controller,
        Dictionary<GameCtrl, Dictionary<string, object>> owners, HashSet<Component> retired)
    {
        // Pool ownership is the gate: leave native/A000 and the other player
        // alone, without replacing any AquaMai/native pool fields or capacity.
        if (controller == null || !owners.TryGetValue(controller, out var pools)) return;
        var arrowParent = Traverse.Create(controller).Field("_slideListParent").GetValue<GameObject>()?.transform;
        foreach (var entry in pools)
        {
            if (!(entry.Value is IEnumerable list)) continue;
            foreach (var item in list)
            {
                if (!(item is Component component) || component == null) continue;
                Transform parent;
                if (component is SlideRoot slide)
                {
                    slide.ResetArrowObject();
                    parent = slide.ParentTransform;
                    retired.Add(slide);
                }
                else if (component is NoteBase note) { parent = note.ParentTransform; retired.Add(note); }
                else if (entry.Key == "_arrowObjectList" || entry.Key == "_breakArrowObjectList") parent = arrowParent;
                else continue;

                // Match ForceNoteCollect: this is not an EndNote or judgment.
                // Retain the pool and parsed SV/visual/chart data for re-use.
                if (component.gameObject.activeSelf) component.gameObject.SetActive(false);
                if (parent != null && component.transform.parent != parent) component.transform.SetParent(parent, false);
            }
        }
    }
}
