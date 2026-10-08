using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(SinmaiAlpha.Plugin), "Sinmai-Alpha", "0.1.1", "SaltNya")]
[assembly: MelonGame("sega-interactive", "Sinmai")]
[assembly: MelonPriority(-10000)]
[assembly: HarmonyDontPatchAll]

namespace SinmaiAlpha
{
    public sealed class Plugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            Hosting.Settings.Load();
            Compatibility.LegacyPatchGuard.Initialize(HarmonyInstance);
            var settings = Hosting.Settings.Current;
            Notes.CustomNoteTypes.ShowMineHitFeedback = settings.ShowMineHitFeedback;
            Notes.CustomNoteTypes.MineVolume = Mathf.Clamp01(settings.MineVolume);
            Notes.ExtendNotesPool.count = Math.Max(0, Math.Min(512, settings.ExtraPoolCount));
            var roots = new List<Type> { typeof(Difficulty.AlphaRights) };
            if (settings.ChartFeatures) roots.AddRange(new[] { typeof(Notes.CustomNoteTypes), typeof(Notes.ExtendNotesPool), typeof(Notes.ReviveFinaleVSlide), typeof(Notes.TapInHoldFix) });
            if (settings.ExtraDifficulty) roots.Add(typeof(Difficulty.ExtraDifficulty));
            var count = 0;
            try
            {
                foreach (var root in roots) Apply(root, ref count);
                MelonLogger.Msg(Notes.ChartWatermark.Credit);
                MelonLogger.Msg("[Sinmai-Alpha] Ready: " + count + " patch groups; assets=" + Hosting.AssetFiles.Root);
            }
            catch (Exception error)
            {
                HarmonyInstance.UnpatchSelf();
                MelonLogger.Error("[Sinmai-Alpha] Initialization failed: " + error);
                throw;
            }
        }
        private void Apply(Type type, ref int count)
        {
            Lifecycle(type, "OnBeforePatch");
            HarmonyInstance.PatchAll(type);
            Lifecycle(type, "OnAfterPatch"); count++;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
                if (nested.GetCustomAttributes(false).Length != 0) Apply(nested, ref count);
        }
        private void Lifecycle(Type type, string name)
        {
            var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null) method.Invoke(null, method.GetParameters().Select(p => p.ParameterType == typeof(HarmonyLib.Harmony) ? (object)HarmonyInstance : throw new InvalidOperationException("Unsupported lifecycle argument")).ToArray());
        }
    }
}

namespace SinmaiAlpha.Hosting
{
    public static class FileSystem
    {
        public static string ResolvePath(string path)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(Environment.CurrentDirectory, expanded));
        }
    }
    public static class AssetFiles
    {
        public static string Root => FileSystem.ResolvePath("Sinmai-Alpha");
        public static Stream Open(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative));
            if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Asset path leaves Sinmai-Alpha");
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
    }
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public bool ChartFeatures = true;
        [DataMember] public bool ExtraDifficulty = true;
        [DataMember] public bool ShowMineHitFeedback = true;
        [DataMember] public float MineVolume = .7f;
        [DataMember] public int ExtraPoolCount = 128;
        // null follows an installed display adapter; true/false is an explicit
        // hint for cabinets whose single-screen mod exposes no public setting.
        [DataMember] public bool? SinglePlayer = null;
        [DataMember] public bool HideSubMonitor = false;
        public static Settings Current { get; private set; } = new Settings();
        [OnDeserializing] private void Defaults(StreamingContext context)
        { ChartFeatures = ExtraDifficulty = ShowMineHitFeedback = true; MineVolume = .7f; ExtraPoolCount = 128; }
        public static void Load()
        {
            var path = Path.Combine(AssetFiles.Root, "config.json");
            if (!File.Exists(path)) return;
            try { using var stream = File.OpenRead(path); Current = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream); }
            catch (Exception error) { MelonLogger.Warning("[Sinmai-Alpha] Invalid config, using defaults: " + error.Message); }
        }
    }
    public static class GuiSizes
    {
        public static bool SinglePlayer => Settings.Current.SinglePlayer ?? Compatibility.OptionalDisplay.SinglePlayer;
    }
}

namespace SinmaiAlpha.Compatibility
{
    internal static class OptionalDisplay
    {
        private static readonly Dictionary<string, Type> Types = new();
        static OptionalDisplay()
        { AppDomain.CurrentDomain.AssemblyLoad += (_, __) => { lock (Types) Types.Clear(); }; }
        private static Type Find(string name)
        {
            lock (Types)
            {
                if (Types.TryGetValue(name, out var found)) return found;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                { found = assembly.GetType(name, false); if (found != null) return Types[name] = found; }
                return Types[name] = null;
            }
        }
        internal static object Read(string type, string member)
        {
            var owner = Find(type); if (owner == null) return null;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            return owner.GetProperty(member, flags)?.GetValue(null, null) ?? owner.GetField(member, flags)?.GetValue(null);
        }
        internal static bool SinglePlayer => Read("AquaMai.Core.Helpers.GuiSizes", "SinglePlayer") is bool value && value;
        internal static RawImage DisplayImage(int monitor, bool sub)
        {
            var owner = Find("AquaMai.Mods.Utils.ScreenPositionAdjust");
            return owner?.GetMethod("DisplayImage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, new object[] { monitor, sub }) as RawImage;
        }
    }
    internal static class SinglePlayer
    {
        internal static bool HideSubMonitor => Hosting.Settings.Current.HideSubMonitor || OptionalDisplay.Read("AquaMai.Mods.GameSystem.SinglePlayer", "HideSubMonitor") is bool value && value;
    }
    internal static class ScreenPositionAdjust
    {
        internal static Camera GameRenderCamera => OptionalDisplay.Read("AquaMai.Mods.Utils.ScreenPositionAdjust", "GameRenderCamera") as Camera;
        internal static RawImage DisplayImage(int monitor, bool sub) => OptionalDisplay.DisplayImage(monitor, sub);
    }
    internal static class PracticeMode
    {
        internal static float speed => OptionalDisplay.Read("AquaMai.Mods.UX.PracticeMode.PracticeMode", "speed") is float value ? value : 1f;
    }
    internal static class LegacyPatchGuard
    {
        private static HarmonyLib.Harmony harmony;
        private static readonly HashSet<Assembly> Seen = new();
        internal static bool IsLegacyFeature(Type type)
        {
            var name = type?.FullName ?? "";
            foreach (var root in new[] { "CustomNoteTypes.CustomNoteTypes", "ExtraDifficulty.ExtraDifficulty", "ReviveFinaleVSlide", "TapInHoldFix" })
                if (name == "AquaMai.Mods.Fancy.GamePlay." + root || name.StartsWith("AquaMai.Mods.Fancy.GamePlay." + root + "+", StringComparison.Ordinal)) return true;
            return false;
        }
        private static bool BeforeLegacyPatch(Type type) => !IsLegacyFeature(type);
        internal static void Initialize(HarmonyLib.Harmony instance)
        {
            harmony = instance;
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Inspect(args.LoadedAssembly);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Inspect(assembly);
        }
        private static void Inspect(Assembly assembly)
        {
            if (assembly.GetName().Name != "AquaMai.Core" || !Seen.Add(assembly)) return;
            var startup = assembly.GetType("AquaMai.Core.Startup", false);
            var apply = startup?.GetMethod("ApplyPatch", BindingFlags.Public | BindingFlags.Static);
            if (apply != null) harmony.Patch(apply, prefix: new HarmonyMethod(typeof(LegacyPatchGuard), nameof(BeforeLegacyPatch)));
        }
    }
}

