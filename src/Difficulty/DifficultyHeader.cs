using HarmonyLib;
using Monitor;
using UnityEngine;
using UnityEngine.UI;
using Theme = SinmaiAlpha.Assets.DifficultyTheme;

namespace SinmaiAlpha.Difficulty;

public static partial class ExtraDifficulty
{
    private static bool IsDifficultyHeaderImage(Image image)
    {
        foreach (var controller in image.GetComponentsInParent<DifficultyContoroller>(true))
            if (Field<Image>(controller, "_difficutlyNameImage") == image ||
                Field<Image>(controller, "_background01Image") == image) return true;
        return false;
    }

    [HarmonyPatch(typeof(DifficultyContoroller), "ChangeName")]
    public static class DifficultyHeaderNamePatch
    {
        [HarmonyPostfix]
        public static void Postfix(DifficultyContoroller __instance, int ____difficulty, Image ____difficutlyNameImage)
        {
            // Use this controller's selected slot, not a neighboring button's
            // destination or a global slot left over from the other player.
            var theme = ThemeFor(Identity(__instance), ____difficulty);
            ApplyImage(____difficutlyNameImage,
                SpriteFor("UI_CMN_TitleTextImage_" + ThemeCode(theme), theme));
        }
    }

    [HarmonyPatch(typeof(DifficultyContoroller), "ChangeBackground")]
    public static class DifficultyHeaderBackgroundPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DifficultyContoroller __instance, int ____difficulty, Image ____background01Image)
        {
            // The fan-shaped base is shared white artwork. Native code restores
            // the selected difficulty's color before every call, so leaving a
            // custom song requires no mutation/restoration of shared palettes.
            var theme = ThemeFor(Identity(__instance), ____difficulty);
            if (theme == Theme.None || ____background01Image == null ||
                !ColorUtility.TryParseHtmlString(theme.Color, out var color)) return;
            color.a = ____background01Image.color.a;
            ____background01Image.color = color;
        }
    }
}
