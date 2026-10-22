using System;
using System.IO;
using System.Text;
using SinmaiAlpha.Hosting;

namespace SinmaiAlpha.Hosting { public static class AssetFiles { public static string Root; } }
namespace MelonLoader { public static class MelonLogger { public static int Warnings; public static void Warning(string text) { Warnings++; } } }

internal static class Program
{
    private const string Json = "{\"GameplayDiagnostics\":true,\"ChartFeatures\":false,\"SinglePlayer\":true,\"ExtraPoolCount\":7,\"Unused\":\"测试\"}";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Defaults(string name)
    {
        Check(!Settings.Current.GameplayDiagnostics && Settings.Current.ChartFeatures && Settings.Current.ExtraPoolCount == 128 &&
            Settings.Current.SinglePlayer == null && !Settings.Current.ShowSssJudgePath &&
            Settings.Current.Artwork != null && !Settings.Current.Artwork.Enabled && Settings.Current.Artwork.EnableGif && Settings.Current.Artwork.Directory == "LocalAssets", name);
    }
    public static void Main()
    {
        AssetFiles.Root = Path.Combine(Path.GetTempPath(), "SinmaiAlpha-Settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AssetFiles.Root);
        var path = Path.Combine(AssetFiles.Root, "config.json");
        try
        {
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
            {
                File.WriteAllText(path, Json, encoding); Settings.Load();
                Check(Settings.Current.GameplayDiagnostics && !Settings.Current.ChartFeatures && Settings.Current.SinglePlayer == true && Settings.Current.ExtraPoolCount == 7,
                    encoding.WebName + " preamble=" + encoding.GetPreamble().Length + " honors configured values");
            }
            Check(MelonLoader.MelonLogger.Warnings == 0, "valid encodings do not warn");
            File.WriteAllText(path, "{\"GameplayDiagnostics\":true}", new UTF8Encoding(true)); Settings.Load();
            Check(Settings.Current.GameplayDiagnostics && Settings.Current.ChartFeatures && Settings.Current.ExtraPoolCount == 128 && !Settings.Current.ShowSssJudgePath,
                "partial config preserves defaults");
            Check(!Settings.Current.Artwork.Enabled && Settings.Current.Artwork.EnableJackets && Settings.Current.Artwork.EnableFrames,
                "existing config keeps artwork opt-in");
            File.WriteAllText(path, "{\"Artwork\":{\"Enabled\":true,\"EnableGif\":false,\"Directory\":\"Art 目录\"}}"); Settings.Load();
            Check(Settings.Current.Artwork.Enabled && !Settings.Current.Artwork.EnableGif && Settings.Current.Artwork.EnableVideo &&
                Settings.Current.Artwork.EnableImages && Settings.Current.Artwork.Directory == "Art 目录", "partial artwork object honors values and retains media defaults");
            foreach (var json in new[] { "{\"Artwork\":null}", "{\"Artwork\":{}}", "{\"Artwork\":{\"Directory\":null}}", "{\"Artwork\":{\"Directory\":\" \"}}" })
            {
                File.WriteAllText(path, json); Settings.Load();
                Check(Settings.Current.Artwork != null && !Settings.Current.Artwork.Enabled && Settings.Current.Artwork.Directory == "LocalAssets", "null/empty artwork uses safe defaults: " + json);
            }
            foreach (var invalid in new[] { "{invalid", "null", "" })
            {
                File.WriteAllText(path, Json); Settings.Load();
                File.WriteAllText(path, invalid); Settings.Load(); Defaults("invalid input resets stale settings: " + invalid);
            }
            Check(MelonLoader.MelonLogger.Warnings == 3, "invalid inputs warn");
            File.WriteAllText(path, Json); Settings.Load(); File.Delete(path); Settings.Load(); Defaults("missing file resets stale settings");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(AssetFiles.Root);
        }
    }
}
