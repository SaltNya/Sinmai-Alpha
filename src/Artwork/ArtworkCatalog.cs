using System;
using System.Collections.Generic;
using System.IO;

namespace SinmaiAlpha.Artwork
{
    internal sealed class ArtworkCatalog
    {
        private readonly ArtworkSettings configuration;
        private readonly LooseAssetIndex local;
        private readonly List<LooseAssetIndex> mounts = new List<LooseAssetIndex>();
        private readonly Dictionary<string, LooseAssetIndex> externalDirectories = new Dictionary<string, LooseAssetIndex>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();
        private readonly Action<string> warning;

        internal ArtworkCatalog(string gameRoot, ArtworkSettings settings, IEnumerable<string> roots, Action<string> warn)
        {
            configuration = settings; warning = warn;
            local = NewIndex();
            Scan(local, Path.GetFullPath(Path.Combine(gameRoot, settings.Directory)));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root) || !visited.Add(Path.GetFullPath(root))) continue;
                string[] directories = System.IO.Directory.GetDirectories(root);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                {
                    string name = Path.GetFileName(directory);
                    if (name.Length == 4 && (char.ToUpperInvariant(name[0]) == 'A' || char.ToUpperInvariant(name[0]) == 'M') &&
                        char.IsDigit(name[1]) && char.IsDigit(name[2]) && char.IsDigit(name[3])) AddMount(directory);
                }
            }
        }
        private bool Allows(string path)
        {
            int kind = LooseAssetIndex.ExtensionPriority(path);
            return kind == 0 ? configuration.EnableGif : kind >= 1 && kind <= 4 ? configuration.EnableVideo : kind >= 5 && configuration.EnableImages;
        }
        private LooseAssetIndex NewIndex() { return new LooseAssetIndex { Filter = Allows }; }
        private void Scan(LooseAssetIndex index, string path)
        {
            try { index.AddDirectory(path); }
            catch (Exception ex) { if (warning != null) warning("Cannot scan " + path + ": " + ex.Message); }
        }
        private void AddMount(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            var index = NewIndex(); Scan(index, Path.Combine(directory, "AssetBundleImages")); mounts.Add(index);
        }
        internal void RegisterMounts(IEnumerable<string> directories)
        {
            if (directories == null) return;
            lock (sync) { mounts.Clear(); foreach (string directory in directories) AddMount(directory); }
        }
        internal bool TryResolve(string request, bool frame, out string path)
        {
            path = null;
            if (!configuration.Enabled || (frame ? !configuration.EnableFrames : !configuration.EnableJackets) || string.IsNullOrEmpty(request)) return false;
            if (Path.IsPathRooted(request)) return TryAbsolute(request, frame, out path);
            lock (sync)
            {
                if (local.TryResolve(request, true, out path)) return true;
                for (int i = mounts.Count - 1; i >= 0; i--) if (mounts[i].TryResolve(request, true, out path)) return true;
            }
            return false;
        }
        private bool TryAbsolute(string request, bool frame, out string path)
        {
            path = null;
            if (LooseAssetIndex.IsAnimated(request))
            {
                if (!Allows(request) || !File.Exists(request)) return false;
                path = request; return true;
            }
            if (!LooseAssetIndex.IsStill(request)) return false;
            string directory = Path.GetDirectoryName(request), stem = Path.GetFileNameWithoutExtension(request);
            if (!System.IO.Directory.Exists(directory)) return false;
            // External-chart mods commonly request bg.png. Recognize a dedicated
            // animated jacket next to it without treating the song's BGA as artwork.
            LooseAssetIndex index;
            lock (sync)
            {
                if (!externalDirectories.TryGetValue(directory, out index))
                { index = NewIndex(); Scan(index, directory); externalDirectories[directory] = index; }
            }
            if (!frame && (stem.Equals("bg", StringComparison.OrdinalIgnoreCase) || stem.Equals("cover", StringComparison.OrdinalIgnoreCase) || stem.Equals("jacket", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string name in new[] { "jacket", "cover", "bg" })
                    if (index.TryResolve(name, true, out var candidate) && LooseAssetIndex.IsAnimated(candidate) &&
                        (name != "bg" || Path.GetExtension(candidate).Equals(".gif", StringComparison.OrdinalIgnoreCase)))
                    { path = candidate; return true; }
            }
            if (index.TryResolve(stem, true, out var resolved) &&
                (frame || !stem.Equals("bg", StringComparison.OrdinalIgnoreCase) || !IsVideo(resolved)))
            { path = resolved; return true; }
            if (configuration.EnableImages && File.Exists(request)) { path = request; return true; }
            return false;
        }
        private static bool IsVideo(string path) { int kind = LooseAssetIndex.ExtensionPriority(path); return kind >= 1 && kind <= 4; }
    }
}
