using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using HarmonyLib;
using Manager;
using Monitor.MusicSelect.ChainList;
using SinmaiAlpha.Notes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SinmaiAlpha.Difficulty;

public static class AlphaRights
{
    private const int TextId = int.MinValue;
    private const string Prefix = "Sinmai-Alpha/Rights/";
    private static readonly Dictionary<string, Texture2D> Placeholders = new Dictionary<string, Texture2D>();

    // The native serializer requires an integer StringID. Normalize only the
    // explicit Alpha rights marker in memory; the user's XML stays editable.
    internal static void Normalize(XmlDocument document)
    {
        if (document.DocumentElement?.LocalName != "MusicData") return;
        var id = document.DocumentElement.SelectSingleNode("rightsInfoName/id");
        if (id != null && id.InnerText.Trim().Equals("Alpha", StringComparison.OrdinalIgnoreCase))
            id.InnerText = TextId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [HarmonyPatch(typeof(XmlDocument), "Load", new[] { typeof(string) })]
    public static class XmlPatch
    {
        [HarmonyPostfix] public static void Postfix(XmlDocument __instance, string filename)
        {
            if (Path.GetFileName(filename).Equals("Music.xml", StringComparison.OrdinalIgnoreCase) && !ChartFeatureGate.IsA000(filename)) Normalize(__instance);
        }
    }

    [HarmonyPatch(typeof(Manager.MaiStudio.MusicData), "Init")]
    public static class MusicPatch
    {
        [HarmonyPostfix] public static void Postfix(Manager.MaiStudio.MusicData __instance)
        {
            if (__instance.rightsInfoName?.id != TextId) return;
            var value = __instance.rightsInfoName.str ?? "";
            AccessTools.PropertySetter(typeof(Manager.MaiStudio.MusicData), "rightFile").Invoke(__instance,
                new object[] { Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) });
        }
    }

    [HarmonyPatch(typeof(AssetManager), "GetRightTexture2D", new[] { typeof(string) })]
    public static class TexturePatch
    {
        [HarmonyPrefix] public static bool Prefix(string filename, ref Texture2D __result)
        {
            if (filename == null || !filename.StartsWith(AlphaRights.Prefix, StringComparison.Ordinal)) return true;
            if (!Placeholders.TryGetValue(filename, out var texture) || texture == null)
            {
                texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = filename };
                texture.SetPixel(0, 0, Color.clear); texture.Apply(false, true);
                Placeholders[filename] = texture;
            }
            __result = texture; return false;
        }
    }

    [HarmonyPatch(typeof(MusicChainCardObejct), "SetCopyright")]
    public static class CardPatch
    {
        [HarmonyPostfix] public static void Postfix(Texture2D copyrightTexture, RawImage ____copyRightImage)
        {
            var image = ____copyRightImage;
            if (image == null) return;
            var label = image.transform.Find("Sinmai-Alpha Rights")?.GetComponent<TextMeshProUGUI>();
            var key = copyrightTexture != null ? copyrightTexture.name : null;
            if (key == null || !key.StartsWith(Prefix, StringComparison.Ordinal))
            { if (label != null) label.gameObject.SetActive(false); return; }
            var font = ReferenceSubtitleDisplay.NativeFont(null);
            if (font == null) return;
            if (label == null)
            {
                var go = new GameObject("Sinmai-Alpha Rights", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                go.layer = image.gameObject.layer;
                go.transform.SetParent(image.transform, false);
                label = go.GetComponent<TextMeshProUGUI>();
                var rect = label.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                label.raycastTarget = false; label.richText = false; label.enableAutoSizing = true; label.enableWordWrapping = false;
                label.fontSize = label.fontSizeMax = 24; label.fontSizeMin = 8;
                label.alignment = TextAlignmentOptions.Center; label.color = Color.white;
            }
            label.font = font;
            // TMP outline setters use a per-label material, leaving the game's font material unchanged.
            label.fontStyle = FontStyles.Bold;
            label.outlineColor = Color.black;
            label.outlineWidth = 0.18f;
            // TMP's SDF outline is centered on the face edge. Expanding the
            // face by the same amount moves its inner edge back outside the
            // bold glyph, so the black border cannot eat the white strokes.
            label.fontMaterial.SetFloat(ShaderUtilities.ID_FaceDilate, 0.18f);
            label.UpdateMeshPadding();
            label.text = Encoding.UTF8.GetString(Convert.FromBase64String(key.Substring(Prefix.Length)));
            label.gameObject.SetActive(true);
        }
    }
}
