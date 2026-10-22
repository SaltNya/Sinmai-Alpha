using System.Text.Json;
using SinmaiAlpha.Artwork;

string root = Path.Combine(Path.GetTempPath(), "SinmaiAlpha-Artwork-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"))) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
try
{
    int checks = 0;
    void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    foreach (var fixture in JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json"))).RootElement.EnumerateArray())
    {
        string name = fixture.GetProperty("file").GetString();
        var gif = new GifAnimation(File.ReadAllBytes(Path.Combine(root, name)));
        var expected = fixture.GetProperty("frames");
        Check(gif.Width == fixture.GetProperty("width").GetInt32() && gif.Height == fixture.GetProperty("height").GetInt32(), name + " dimensions");
        Check(gif.FrameCount == expected.GetArrayLength(), name + " frame count");
        for (int cycle = 0; cycle < 3; cycle++)
            for (int frame = 0; frame < gif.FrameCount; frame++)
            {
                var info = expected[frame];
                byte[] pixels = File.ReadAllBytes(Path.Combine(root, info.GetProperty("pixels").GetString()));
                Check(gif.Pixels.SequenceEqual(pixels), name + " pixels frame " + frame + " cycle " + cycle);
                Check(Math.Abs(gif.Delay - info.GetProperty("delay").GetSingle()) < .001, name + " timing");
                gif.Advance();
            }
    }
    byte[] valid = File.ReadAllBytes(Path.Combine(root, "jacket-smoke.gif"));
    foreach (int length in new[] { 0, 6, 13, valid.Length / 2, valid.Length - 1 })
    {
        bool rejected = false;
        try { new GifAnimation(valid.Take(length).ToArray()); } catch (IOException) { rejected = true; }
        Check(rejected, "Truncated GIF accepted at " + length);
    }
    byte[] huge = valid.ToArray(); huge[6] = 255; huge[7] = 127;
    try { new GifAnimation(huge); throw new Exception("Huge GIF accepted"); } catch (InvalidDataException) { checks++; }
    string indexRoot = Path.Combine(root, "index");
    void Add(string path) { path = Path.Combine(indexRoot, path); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "fixture"); }
    foreach (string path in new[] { "Jacket/UI_Jacket_123456.PnG", "Jacket/UI_Jacket_123456.gif", "Jacket/UI_Jacket_123456.mp4", "Frame/UI_Frame_123456.GiF", "Frame/222222.Png", "333333.pNg", "Jacket_S/UI_Jacket_444444_S.jpg", "Jacket/UI_Jacket_444444.png" }) Add(path);
    var index = new LooseAssetIndex(); index.AddDirectory(indexRoot);
    void Resolve(string request, bool animated, string suffix)
    {
        Check(index.TryResolve(request, animated, out var path) && path.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal), request + " => " + path);
    }
    Resolve("Jacket_S\\UI_Jacket_123456_S.png", false, "Jacket/UI_Jacket_123456.PnG");
    Resolve("Jacket_S/UI_Jacket_123456_S.png", true, "Jacket/UI_Jacket_123456.gif");
    Resolve("Frame_S/UI_Frame_123456_S.png", true, "Frame/UI_Frame_123456.GiF");
    Resolve("Frame_S/UI_Frame_222222_S.png", false, "Frame/222222.Png");
    Resolve("Jacket_S/UI_Jacket_333333_S.png", false, "333333.pNg");
    Resolve("Jacket_S/UI_Jacket_444444_S.png", false, "Jacket_S/UI_Jacket_444444_S.jpg");
    Check(!index.TryResolve("Frame/UI_Frame_333333.png", true, out _), "Bare jacket ID leaked into Frame");
    Check(!index.TryResolve("Jacket/UI_Jacket_222222.png", true, out _), "Scoped Frame ID leaked into jacket");
    Check(!index.TryResolve("FrameMask/UI_FrameMask_123456.png", true, out _), "Frame leaked into mask");
    Console.WriteLine($"PASS {checks} checks: Pillow pixel/timing parity across 3 loops, disposal 1/2/3, transparency, local palettes, interlace/LZW, malformed GIFs, PNG/animation resolution.");
    var settings = new ArtworkSettings { Enabled = true };
    string catalogRoot = Path.Combine(root, "catalog");
    string MountFile(string relative)
    {
        string path = Path.Combine(catalogRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "fixture"); return path;
    }
    foreach (string path in new[] {
        "StreamingAssets/A000/AssetBundleImages/jacket/UI_Jacket_111111.png",
        "StreamingAssets/A698/AssetBundleImages/jacket/UI_Jacket_111111.Gif",
        "LocalAssets/Jacket/UI_Jacket_111111.png",
        "StreamingAssets/A000/AssetBundleImages/jacket/UI_Jacket_222222.png",
        "StreamingAssets/A698/AssetBundleImages/jacket/UI_Jacket_222222.gif",
        "StreamingAssets/A698/AssetBundleImages/jacket/UI_Jacket_222222.mp4",
        "LocalAssets/Frame/UI_Frame_222222.gif", "Chart/bg.png", "Chart/bg.mp4", "Chart/jacket.gif"
    }) MountFile(path);
    settings = new ArtworkSettings { Enabled = true };
    var catalog = new ArtworkCatalog(catalogRoot, settings, new[] { Path.Combine(catalogRoot, "StreamingAssets") }, message => throw new Exception(message));
    void CatalogResolve(string request, bool frame, string suffix)
    {
        Check(catalog.TryResolve(request, frame, out var path) && path.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal), "Catalog " + request + " => " + path);
    }
    CatalogResolve("Jacket_S/UI_Jacket_111111_S.png", false, "LocalAssets/Jacket/UI_Jacket_111111.png");
    CatalogResolve("Jacket_S/UI_Jacket_222222_S.png", false, "A698/AssetBundleImages/jacket/UI_Jacket_222222.gif");
    settings.EnableGif = false;
    CatalogResolve("Jacket/UI_Jacket_222222.png", false, "A698/AssetBundleImages/jacket/UI_Jacket_222222.mp4");
    settings.EnableVideo = false;
    CatalogResolve("Jacket/UI_Jacket_222222.png", false, "A000/AssetBundleImages/jacket/UI_Jacket_222222.png");
    settings.EnableImages = false;
    Check(!catalog.TryResolve("Jacket/UI_Jacket_222222.png", false, out _), "All media disabled");
    settings.EnableImages = settings.EnableGif = settings.EnableVideo = true;
    settings.EnableFrames = false;
    Check(!catalog.TryResolve("Frame/UI_Frame_222222.png", true, out _), "Frame switch");
    settings.EnableFrames = true; settings.EnableJackets = false;
    Check(!catalog.TryResolve("Jacket/UI_Jacket_222222.png", false, out _), "Jacket switch");
    CatalogResolve("Frame_S/UI_Frame_222222_S.png", true, "LocalAssets/Frame/UI_Frame_222222.gif");
    settings.EnableJackets = true;
    CatalogResolve(Path.Combine(catalogRoot, "Chart/bg.png"), false, "Chart/jacket.gif");
    File.Delete(Path.Combine(catalogRoot, "Chart/jacket.gif"));
    CatalogResolve(Path.Combine(catalogRoot, "Chart/bg.png"), false, "Chart/bg.png");
    catalog.RegisterMounts(new[] { Path.Combine(catalogRoot, "StreamingAssets/A698"), Path.Combine(catalogRoot, "StreamingAssets/A000") });
    CatalogResolve("Jacket/UI_Jacket_222222.png", false, "A000/AssetBundleImages/jacket/UI_Jacket_222222.png");
    settings.Enabled = false;
    Check(!catalog.TryResolve("Frame/UI_Frame_222222.png", true, out _), "Global disable");
    Console.WriteLine($"PASS {checks} total checks including local/mount priority, thumbnail fallback, media/category toggles, and BGA isolation.");
    if (args.Length > 0)
    {
        var reference = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var realGif = new GifAnimation(File.ReadAllBytes(reference.GetProperty("file").GetString()));
        var frames = reference.GetProperty("frames");
        Check(realGif.Width == reference.GetProperty("width").GetInt32() && realGif.Height == reference.GetProperty("height").GetInt32(), "Actual jacket dimensions");
        Check(realGif.FrameCount == frames.GetArrayLength(), "Actual jacket frame count");
        for (int cycle = 0; cycle < 2; cycle++)
            for (int f = 0; f < realGif.FrameCount; f++)
            {
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(realGif.Pixels)).ToLowerInvariant();
                Check(hash == frames[f].GetProperty("sha256").GetString(), "Actual jacket frame " + f + " cycle " + cycle);
                Check(Math.Abs(realGif.Delay - frames[f].GetProperty("delay").GetSingle()) < .001, "Actual jacket frame delay");
                realGif.Advance();
            }
        Console.WriteLine($"PASS actual jacket: {realGif.Width}x{realGif.Height}, {realGif.FrameCount} frames, 2 complete loops match Pillow pixel hashes and timing; decoded/hashed in {timer.Elapsed.TotalSeconds:F2}s.");
    }

}
finally { Directory.Delete(root, true); }
