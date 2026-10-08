using HarmonyLib;
using Manager;
using MelonLoader;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    [HarmonyPatch(typeof(NotesReader), "calcSlide")]
    public static class ConnectedSlideSafetyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(NotesReader __instance)
        {
            if (!FeaturesEnabled(__instance)) return;
            var removed = 0;
            foreach (var note in __instance.GetNoteList())
            {
                if (note?.child == null) continue;
                // Native calcSlide starts its successor search at the current
                // note. A zero-duration, headless loop can therefore point to
                // itself. SlideRoot's unbounded child walk then freezes when
                // the slide enters the pool. Keep the segment, its real parent
                // and all authored timing; remove only this impossible edge.
                for (var i = note.child.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(note.child[i], note))
                    { note.child.RemoveAt(i); removed++; }
            }
            if (removed != 0)
                MelonLogger.Warning("[Slide Chain] Removed " + removed + " zero-duration self-link(s); note timing and scoring unchanged");
        }
    }
}
