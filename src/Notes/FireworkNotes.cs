using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class FireworkValue { public NotesReader Reader; }
    private sealed class FireworkLease { public FireworkValue Value; public bool Emitted; public int Monitor; }
    private sealed class FireworkDisplay { public GameObject Object; public NotesReader Reader; public Material[] Materials; public SpriteRenderer[] Renderers; public Animator Animator; public FireworkPlayfieldClip Clip; }
    public sealed class FireworkEndState { internal bool Eligible; internal Vector3 Position; internal Transform Basis; internal int Layer, SortLayer; }
    private static readonly ConditionalWeakTable<NoteData, FireworkValue> FireworkNotes = new();
    private static readonly Dictionary<NoteBase, FireworkLease> FireworkOwners = new();
    private static readonly Dictionary<int, FireworkDisplay> FireworkDisplays = new();
    private static RuntimeAnimatorController FireworkController;
    private static Shader FireworkColorShader, FireworkHanabiShader;
    private static Sprite FireworkBall, FireworkFlower;
    private static Texture2D FireworkBallTexture, FireworkFlowerTexture;
    private static AssetBundle FireworkBundle;
    private static bool FireworkLoadAttempted;
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
        // Touch f already occupies the native MA2 effect field. Bind that flag
        // without rewriting it, so native sound/chain and judgment stay intact.
        // It shares the final-result display callback used by numeric heads.
        var nativeType = note.type.getEnum();
        var touchFirework = (nativeType == NotesTypeID.Def.TouchTap || nativeType == NotesTypeID.Def.TouchHold) && (int)note.effect != 0;
        if (!pendingFirework && !touchFirework) return;
        var tag = rec._str[0];
        if (touchFirework || new[] { "TAP", "STR", "HLD", "TTP", "THO", "STP" }.Any(s => tag.EndsWith(s, StringComparison.Ordinal)) || new[] { "TAP", "BRK", "XTP", "STR", "BST", "XST", "HLD", "XHO" }.Contains(tag))
            FireworkNotes.Add(note, new FireworkValue { Reader = reader });
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
            var render = fields.Field("SpriteRender").GetValue<SpriteRenderer>();
            var picture = fields.Field("NoteObj").GetValue<GameObject>()?.transform;
            // Capture before EndNote hides the picture and moves its owner to
            // the pool. Only the graphic position is observed; input launchers
            // and sensor queues retain their native transform and lifetime.
            __state = new FireworkEndState { Eligible = true, Position = picture != null ? picture.position : __instance.transform.position,
                Basis = __instance.transform.parent?.parent, Layer = render != null ? render.gameObject.layer : __instance.gameObject.layer,
                SortLayer = render != null ? render.sortingLayerID : 0 };
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

    private static void ShowFirework(int monitor, FireworkEndState state, NotesReader reader)
    {
        if (state.Basis == null || !LoadFireworkGraphics()) return;
        if (!FireworkDisplays.TryGetValue(monitor, out var lease) || lease.Object == null || lease.Reader != reader)
        {
            RemoveFireworkDisplay(monitor);
            lease = CreateFireworkDisplay(monitor, state.Basis, reader);
            FireworkDisplays[monitor] = lease;
        }
        lease.Object.transform.SetParent(state.Basis, false);
        lease.Object.transform.position = state.Position;
        lease.Object.transform.localRotation = Quaternion.identity;
        foreach (var renderer in lease.Renderers)
        { renderer.gameObject.layer = state.Layer; renderer.sortingLayerID = state.SortLayer; }
        lease.Object.SetActive(true);
        // The two native Mecanim clips retain the source's authored Hermite
        // curves and its zero-duration Fire trigger/self-transition/exit.
        // Animator Normal uses Unity scaled time, as the reference. No note
        // time, input, judge result or playback state is advanced here.
        lease.Animator.SetTrigger("Fire");
        lease.Clip.Refresh();
    }

    private static bool LoadFireworkGraphics()
    {
        if (!FireworkLoadAttempted)
        {
            FireworkLoadAttempted = true;
            try
            {
                var path = SinmaiAlpha.Hosting.FileSystem.ResolvePath("Sinmai-Alpha/Alpha/Firework.ab");
                if (!File.Exists(path)) throw new FileNotFoundException("Missing Firework asset", path);
                FireworkBundle = AssetBundle.LoadFromFile(path);
                if (FireworkBundle == null) throw new InvalidDataException("Firework bundle");
                FireworkController = FireworkBundle.LoadAsset<RuntimeAnimatorController>("assets/animation/firework.controller");
                FireworkColorShader = FireworkBundle.LoadAsset<Shader>("assets/aquamai/coloradjust.shader");
                FireworkHanabiShader = FireworkBundle.LoadAsset<Shader>("assets/aquamai/hanabi.shader");
                if (FireworkController == null || FireworkColorShader == null || FireworkHanabiShader == null || !FireworkColorShader.isSupported || !FireworkHanabiShader.isSupported)
                    throw new InvalidDataException("Incomplete Firework shaders/animation");
                // Older bundles cannot contain the firework inside the playfield.
                // Keep native feedback instead of loading an uncropped graphic.
                foreach (var shader in new[] { FireworkColorShader, FireworkHanabiShader })
                {
                    var check = new Material(shader);
                    var clips = check.HasProperty("_PlayfieldClipX") && check.HasProperty("_PlayfieldClipY");
                    Object.Destroy(check);
                    if (!clips) throw new InvalidDataException("Update Sinmai-Alpha/Alpha/Firework.ab together with the mod");
                }
                // Preserve the reference build's DXT5 pixels, transparent-edge
                // dilation and imported tight meshes instead of reimporting PNGs.
                FireworkBall = FireworkBundle.LoadAsset<Sprite>("assets/sprite/colorball.sprite");
                FireworkFlower = FireworkBundle.LoadAsset<Sprite>("assets/sprite/firework_new.sprite");
                if (FireworkBall == null || FireworkFlower == null) throw new InvalidDataException("Incomplete Firework sprites");
                FireworkBallTexture = FireworkBall.texture; FireworkFlowerTexture = FireworkFlower.texture;
            }
            catch (Exception error)
            {
                ReleaseFireworkGraphics();
                MelonLogger.Warning("[Firework] Reference graphics unavailable; native effect retained: " + error.Message);
            }
        }
        return FireworkController != null && FireworkBall != null && FireworkFlower != null;
    }

    private static FireworkDisplay CreateFireworkDisplay(int monitor, Transform basis, NotesReader reader)
    {
        var display = new GameObject("AquaMai Reference Firework " + monitor);
        display.SetActive(false); display.transform.SetParent(basis, false);
        display.transform.localScale = Vector3.one * 100;
        var renderers = new SpriteRenderer[3]; var materials = new Material[3];
        var names = new[] { "ColorBallBig", "Firework", "ColorBall" };
        var orders = new[] { -5, -6, -4 };
        for (var i = 0; i < names.Length; i++)
        {
            var child = new GameObject(names[i]); child.transform.SetParent(display.transform, false);
            var renderer = child.AddComponent<SpriteRenderer>(); renderers[i] = renderer;
            renderer.sprite = i == 1 ? FireworkFlower : FireworkBall;
            renderer.sortingOrder = orders[i]; renderer.color = Color.white;
            var material = new Material(i == 1 ? FireworkHanabiShader : FireworkColorShader); materials[i] = material;
            material.name = "AquaMai Firework " + names[i] + " " + monitor;
            material.SetFloat("_Brightness", 1); material.SetFloat("_Saturation", i == 1 ? .545f : 1);
            if (i == 1)
            {
                material.SetFloat("_Alpha", .589f); material.SetFloat("_Speed", .5f);
                material.SetFloat("_InnerLB", .018f); material.SetFloat("_InnerUB", .054f);
                material.SetFloat("_OuterLB", .36f); material.SetFloat("_OuterUB", .429f);
            }
            else material.SetFloat("_Contrast", 1);
            renderer.sharedMaterial = material;
        }
        var animator = display.AddComponent<Animator>();
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.updateMode = AnimatorUpdateMode.Normal; animator.applyRootMotion = false;
        animator.runtimeAnimatorController = FireworkController;
        var owner = basis.GetComponentInParent<GameMonitor>();
        var main = owner != null ? Traverse.Create(owner).Field("Main").GetValue<CanvasGroup>() : null;
        var clip = display.AddComponent<FireworkPlayfieldClip>();
        clip.Bind(main != null ? main.transform as RectTransform : null, renderers, materials);
        return new FireworkDisplay { Object = display, Reader = reader, Materials = materials, Renderers = renderers, Animator = animator, Clip = clip };
    }

    [HarmonyPatch(typeof(TapCEffect), "Intialize")]
    public static class FireworkNativeParticlePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return true; }

            // Only replace the native particle for a bound f head while its
            // original PlayJudgeSe is running. Native sound reservation, Hold
            // tail grading and all ordinary Touch effects still run normally.
            var owner = FeedbackRadiusCurrentOwner;
            return owner == null || !(owner is TouchNoteB) || !FireworkOwners.ContainsKey(owner) || !LoadFireworkGraphics();
        }
    }

    private static void ReleaseFireworkGraphics()
    {
        if (FireworkBall != null) Object.Destroy(FireworkBall);
        if (FireworkFlower != null) Object.Destroy(FireworkFlower);
        if (FireworkBallTexture != null) Object.Destroy(FireworkBallTexture);
        if (FireworkFlowerTexture != null) Object.Destroy(FireworkFlowerTexture);
        FireworkBall = FireworkFlower = null; FireworkBallTexture = FireworkFlowerTexture = null;
        FireworkController = null; FireworkColorShader = FireworkHanabiShader = null;
        if (FireworkBundle != null) FireworkBundle.Unload(true);
        FireworkBundle = null;
    }

    private static void RemoveFireworkDisplay(int monitor)
    {
        if (!FireworkDisplays.TryGetValue(monitor, out var lease)) return;
        if (lease.Object != null)
        {
            // Destroy is deferred until the frame ends. Rebinding a reader
            // must stop the old renderers before creating the new display.
            lease.Object.SetActive(false);
            Object.Destroy(lease.Object);
        }
        foreach (var material in lease.Materials) if (material != null) Object.Destroy(material);
        FireworkDisplays.Remove(monitor);
    }

    private static void ResetFireworks(NotesReader reader = null)
    {
        var monitors = FireworkDisplays.Where(p => reader == null || p.Value.Reader == reader).Select(p => p.Key).ToArray();
        foreach (var owner in FireworkOwners.Where(p => reader == null || p.Value.Value.Reader == reader).Select(p => p.Key).ToArray()) FireworkOwners.Remove(owner);
        foreach (var monitor in monitors) RemoveFireworkDisplay(monitor);
        if (reader != null) return;
        ReleaseFireworkGraphics(); FireworkLoadAttempted = false;
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First + 20), HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static void CollectFireworks(GameCtrl __instance)
    {
        foreach (var owner in FireworkOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).Select(p => p.Key).ToArray()) FireworkOwners.Remove(owner);
        RemoveFireworkDisplay(__instance.MonitorIndex);
    }
}
