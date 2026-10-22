using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using MelonLoader;

namespace SinmaiAlpha.Hosting
{
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
        [DataMember] public bool ShowSssJudgePath = false;
        [DataMember] public bool GameplayDiagnostics = false;
        [DataMember] public Artwork.ArtworkSettings Artwork = new Artwork.ArtworkSettings();
        public static Settings Current { get; private set; } = new Settings();
        [OnDeserializing] private void Defaults(StreamingContext context)
        { ChartFeatures = ExtraDifficulty = ShowMineHitFeedback = true; MineVolume = .7f; ExtraPoolCount = 128; ShowSssJudgePath = GameplayDiagnostics = false; Artwork = new Artwork.ArtworkSettings(); }
        [OnDeserialized] private void Validate(StreamingContext context)
        { if (Artwork == null) Artwork = new Artwork.ArtworkSettings(); }
        public static void Load()
        {
            Current = new Settings();
            var path = Path.Combine(AssetFiles.Root, "config.json");
            if (!File.Exists(path)) return;
            try
            {
                // StreamReader detects UTF BOMs; the Mono JSON serializer does not.
                // Normalize decoded text to BOM-free UTF-8 before deserializing.
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path)));
                Current = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream)
                    ?? throw new SerializationException("Configuration must be a JSON object.");
            }
            catch (Exception error) { MelonLogger.Warning("[Sinmai-Alpha] Invalid config, using defaults: " + error.Message); }
        }
    }
}
