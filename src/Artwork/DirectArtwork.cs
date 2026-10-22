using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SinmaiAlpha.Artwork
{
    public static class DirectArtwork
    {
        private static ArtworkCatalog catalog;
        private static readonly Dictionary<string, Texture2D> images = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void OnBeforePatch()
        {
            var config = Hosting.Settings.Current.Artwork;
            string gameRoot = Environment.CurrentDirectory;
            var roots = new List<string> { Path.Combine(gameRoot, "StreamingAssets"), Application.streamingAssetsPath, Path.Combine(gameRoot, "Sinmai_Data", "StreamingAssets") };
            try
            {
                Type appImage = AccessTools.TypeByName("AMDaemon.AppImage");
                var optionRoot = appImage == null ? null : AccessTools.Property(appImage, "OptionMountRootPath");
                if (optionRoot != null) roots.Add(optionRoot.GetValue(null, null) as string);
            }
            catch (Exception ex) { Warn("Option mount root is not ready: " + ex.Message); }
            catalog = new ArtworkCatalog(gameRoot, config, roots, Warn);
        }

        [HarmonyPatch]
        public static class TexturePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { "GetJacketTexture2D", "GetJacketThumbTexture2D", "GetFrameTexture2D", "GetFrameThumbTexture2D", "GetIconTexture2D", "GetPlateTexture2D" })
                {
                    MethodInfo method = AccessTools.Method(typeof(AssetManager), name, new[] { typeof(string) });
                    if (method == null || method.ReturnType != typeof(Texture2D)) throw new MissingMethodException("Unsupported AssetManager: " + name + "(string)");
                    yield return method;
                }
            }
            [HarmonyPostfix, HarmonyPriority(Priority.Last)]
            public static void Postfix(string __0, MethodBase __originalMethod, ref Texture2D __result)
                => ArtworkPostfix(__0, __originalMethod, ref __result);
        }

        [HarmonyPatch(typeof(Manager.DataManager), "LoadMusicBase")]
        public static class MountPatch
        {
            [HarmonyPrefix] public static void Prefix(object __instance) => RegisterMounts(__instance);
        }

        // A postfix runs even when AquaMai/ReSinmai-Mod skips the stock method.
        // The previous result remains the fallback; no gameplay/BGA methods change.
        private static void ArtworkPostfix(string __0, MethodBase __originalMethod, ref Texture2D __result)
        {
            string path = null;
            try
            {
                ArtworkKind kind = __originalMethod.Name.IndexOf("Frame", StringComparison.Ordinal) >= 0 ? ArtworkKind.Frame
                    : __originalMethod.Name == "GetIconTexture2D" ? ArtworkKind.Icon
                    : __originalMethod.Name == "GetPlateTexture2D" ? ArtworkKind.NamePlate : ArtworkKind.Jacket;
                if (catalog == null || !catalog.TryResolve(__0, kind, out path) || failures.Contains(path)) return;
                Texture2D result;
                if (LooseAssetIndex.IsAnimated(path))
                {
                    Texture2D poster = __result;
                    result = DirectArtworkPlayer.Load(path, kind == ArtworkKind.Frame, () => poster);
                }
                else
                {
                    if (!images.TryGetValue(path, out result) || result == null)
                    {
                        result = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        try
                        {
                            if (!ImageConversion.LoadImage(result, File.ReadAllBytes(path), false))
                                throw new InvalidDataException("Image decoder rejected the file.");
                        }
                        catch { UnityEngine.Object.Destroy(result); throw; }
                        result.name = "ArtworkImage_" + Path.GetFileName(path);
                        result.wrapMode = TextureWrapMode.Clamp; images[path] = result;
                    }
                }
                if (result != null) __result = result;
            }
            catch (Exception ex)
            {
                if (failures.Add(path ?? __0 ?? "<empty>")) Warn("Artwork fallback retained for " + __0 + ": " + ex.Message);
            }
        }
        private static void RegisterMounts(object __instance)
        {
            try
            {
                var field = AccessTools.Field(__instance.GetType(), "_targetDirs");
                if (field != null && catalog != null) catalog.RegisterMounts(field.GetValue(__instance) as IEnumerable<string>);
            }
            catch (Exception ex) { Warn("Could not refresh mounted artwork: " + ex.Message); }
        }
        public static void Shutdown()
        {
            foreach (var player in Resources.FindObjectsOfTypeAll<DirectArtworkPlayer>())
            { player.ReleaseAll(); UnityEngine.Object.Destroy(player.gameObject); }
            foreach (var texture in images.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
            images.Clear(); failures.Clear(); catalog = null;
        }
        private static void Warn(string message) { Debug.LogWarning("[Sinmai-Alpha/Artwork] " + message); }
    }
}
