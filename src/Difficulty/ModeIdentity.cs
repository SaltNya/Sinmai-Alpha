using MAI2.Util;
using Manager;
using Monitor.MusicSelect.ChainList;
using UnityEngine;

namespace SinmaiAlpha.Difficulty;

public static partial class ExtraDifficulty
{
    internal static string ModeChart(Component owner, string nativeIcon)
    {
        var identity = Identity(owner);
        if (identity == null || identity.Utage || identity.Id <= 0 || identity.Id >= 20000 || identity.Difficulty < 0 || identity.Difficulty > 4) return null;
        var id = identity.Id;
        // A dual-mode selection card contains both badges, including the back
        // tab. Resolve each badge to its own chart instead of skinning both.
        if (owner.GetComponentsInParent<MusicChainCardObejct>(true).Length > 0)
        {
            var deluxe = nativeIcon.EndsWith("DeluxeMode", System.StringComparison.Ordinal);
            if (deluxe && id < 10000) id += 10000;
            else if (!deluxe && id >= 10000) id -= 10000;
        }
        var music = Singleton<DataManager>.Instance.GetMusic(id);
        return music?.notesData != null && music.notesData.Count > identity.Difficulty
            ? music.notesData[identity.Difficulty]?.file?.path : null;
    }
}
