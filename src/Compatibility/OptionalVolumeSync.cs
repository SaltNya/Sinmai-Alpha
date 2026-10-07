using System;
using System.Reflection;

namespace SinmaiAlpha.Compatibility;

// AquaMai is optional. Its VolumeSync module keeps per-player gains in an
// array sized at native sound initialization, while Alpha adds mine players
// later. Grow that bookkeeping before exposing the additional player IDs.
internal static class OptionalVolumeSync
{
    private static FieldInfo playerVolumes;

    internal static void EnsureCapacity(int playerCount)
    {
        if (playerCount <= 0) return;
        if (playerVolumes == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("AquaMai.Mods.GameSystem.VolumeSync", false);
                var field = type?.GetField("_playerVolumes", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (field?.FieldType != typeof(float[])) continue;
                playerVolumes = field;
                break;
            }
        }

        // A null array means VolumeSync has not initialized (or is disabled).
        // Do not activate the optional feature or overwrite its existing gains.
        var current = playerVolumes?.GetValue(null) as float[];
        if (current == null || current.Length >= playerCount) return;
        var expanded = new float[playerCount];
        Array.Copy(current, expanded, current.Length);
        for (var i = current.Length; i < expanded.Length; i++) expanded[i] = 1f;
        playerVolumes.SetValue(null, expanded);
    }
}
