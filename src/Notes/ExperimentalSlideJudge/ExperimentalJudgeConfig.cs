using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using SinmaiAlpha.Hosting;
using MelonLoader;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

// An extension of the existing config.json, deliberately separate from Settings.
// Old configurations omit this field and remain off. Read once at startup: a
// mid-song edit cannot replace the tables under an active/pool-reused slide.
internal static class ExperimentalJudgeConfig
{
    private static readonly Lazy<bool> StartupValue = new Lazy<bool>(() =>
    {
        bool enabled = Read(Path.Combine(AssetFiles.Root, "config.json"), MelonLogger.Warning);
        MelonLogger.Msg("[Experimental Slide Judge] " + (enabled
            ? "Enabled: isolated unified tables/redirects + single-slide contour judgment; native notes unchanged. Restart after changing config."
            : "Disabled: original slide implementation; no unified or contour patches installed."));
        return enabled;
    });

    internal static bool Enabled => StartupValue.Value;

    [DataContract]
    private sealed class Options
    {
        [DataMember] public bool ExperimentalSlideJudgment = false;
    }

    internal static bool Read(string path, Action<string> warn)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path)));
            return ((Options)new DataContractJsonSerializer(typeof(Options)).ReadObject(input))
                ?.ExperimentalSlideJudgment == true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException ||
            e is SerializationException || e is System.Xml.XmlException || e is ArgumentException)
        {
            warn("[Experimental Slide Judge] Invalid config; experiment disabled: " + e.Message);
            return false;
        }
    }
}
