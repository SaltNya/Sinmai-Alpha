using System.Collections.Generic;
using SinmaiAlpha.ChartVisuals;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static Material NoteTintMaterial, NoteTintGaugeMaterial;
    private static readonly Dictionary<SpriteRenderer, Material> BorrowedTintDefaults = new();
    private static readonly Dictionary<SpriteRenderer, MaterialPropertyBlock> NoteTintBlocks = new();
    private static bool HasNoteTintMaterials() => NoteTintMaterial != null || NoteTintGaugeMaterial != null;

    private static readonly HashSet<Manager.NotesReader> TintWarmedReaders = new();
    [HarmonyLib.HarmonyPatch(typeof(Monitor.Game.GameCtrl), "IsReady")]
    public static class NoteTintWarmupPatch
    {
        public static void Postfix(Monitor.Game.GameCtrl __instance, bool __result)
        {
            if (!__result || !FeaturesEnabled(__instance)) return;
            var reader = Manager.NotesManager.Instance(__instance.MonitorIndex)?.getReader();
            if (reader == null || !TintWarmedReaders.Add(reader)) return;
            var sprite = Process.GameNoteImageContainer.NormalTap[0];
            if (sprite == null) { TintWarmedReaders.Remove(reader); return; }
            var obj = new GameObject("Sinmai-Alpha tint warmup");
            var render = obj.AddComponent<SpriteRenderer>(); render.enabled = false;
            try
            {
                if (ApplyNoteTintMaterial(render, sprite, Color.white, 0xFFFFFF, 1))
                    render.sharedMaterial.SetPass(0);
            }
            finally { NoteTintBlocks.Remove(render); Object.Destroy(obj); }
        }
    }
    private static bool ApplyNoteTintMaterial(SpriteRenderer render, Sprite source, Color vertex,
        int? rgb, float? alpha, Material native = null, bool grayscale = false)
    {
        if (render == null || source == null || !LoadTouchProgressShader()) return false;
        var masked = native != null && native.shader != null && native.shader.name == "Custom/AroundFillShade";
        if (masked && !MineTextures.ContainsKey("touchhold_progress_mask")) return false;
        Material material;
        if (masked)
        {
            if (NoteTintGaugeMaterial == null)
                NoteTintGaugeMaterial = new Material(TouchProgressShader) { name = "AquaMai Note Tint Gauge" };
            material = NoteTintGaugeMaterial;
        }
        else
        {
            if (NoteTintMaterial == null)
            {
                var shader = TouchProgressBundle.LoadAsset<Shader>("assets/aquamai/referencenotetint.shader");
                if (shader == null || !shader.isSupported) return false;
                NoteTintMaterial = new Material(shader) { name = "AquaMai Note Tint" };
            }
            material = NoteTintMaterial;
        }
        render.sprite = source; render.sharedMaterial = material; render.color = vertex;
        if (!NoteTintBlocks.TryGetValue(render, out var properties))
            NoteTintBlocks[render] = properties = new MaterialPropertyBlock();
        render.GetPropertyBlock(properties);
        properties.SetColor("_Color", Color.white);
        properties.SetColor("_NoteColor", rgb.HasValue
            ? new Color(((rgb.Value >> 16) & 255) / 255f, ((rgb.Value >> 8) & 255) / 255f, (rgb.Value & 255) / 255f, 1)
            : new Color(1, 1, 1, 0));
        properties.SetFloat("_NoteAlpha", Mathf.Clamp01(alpha ?? 1));
        properties.SetFloat("_Brightness", 1); properties.SetFloat("_SrcHue", -1);
        properties.SetFloat("_TintCoverage", 0); properties.SetFloat("_DarkDetail", .35f);
        properties.SetFloat("_Grayscale", grayscale ? 1 : 0);
        if (masked)
        {
            properties.SetTexture("_ProgressMask", MineTextures["touchhold_progress_mask"]);
            properties.SetFloat("_Amount", native.GetFloat("_Amount"));
            properties.SetFloat("_MaskScale", source.pixelsPerUnit / 502f);
        }
        render.SetPropertyBlock(properties);
        return true;
    }
    private static bool ApplyBorrowedTintMaterial(SpriteRenderer render, Sprite picture, VisualValue value, bool mine)
    {
        if (!BorrowedTintDefaults.TryGetValue(render, out var native)) native = render.sharedMaterial;
        if (ApplyNoteTintMaterial(render, picture, Color.white, value.Color, value.Alpha,
            grayscale: mine && !value.Color.HasValue))
        {
            BorrowedTintDefaults[render] = native;
            return true;
        }
        if (BorrowedTintDefaults.Remove(render))
        { render.sharedMaterial = native; render.SetPropertyBlock(null); }
        return false;
    }
    private static void ReleaseNoteTintMaterials()
    {
        foreach (var pair in BorrowedTintDefaults)
            if (pair.Key != null) { pair.Key.sharedMaterial = pair.Value; pair.Key.SetPropertyBlock(null); }
        BorrowedTintDefaults.Clear();
        NoteTintBlocks.Clear(); TintWarmedReaders.Clear();
        if (NoteTintMaterial != null) Object.Destroy(NoteTintMaterial);
        if (NoteTintGaugeMaterial != null) Object.Destroy(NoteTintGaugeMaterial);
        NoteTintMaterial = null; NoteTintGaugeMaterial = null;
        // A Break TouchHold can still own the bundle after its COLORV is reset.
        // Resetting the cache must not tear down that note's original progress.
        if (TouchProgressOwners.Count == 0) ResetTouchProgress();
    }
}
