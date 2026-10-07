using System.Runtime.CompilerServices;
using Process;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class SpriteModelSource { public Sprite Model; }
    private static readonly ConditionalWeakTable<Sprite, SpriteModelSource> SpriteModelSources = new();

    private static void RememberSpriteModel(Sprite sprite, Sprite original)
    {
        if (sprite == null || original == null) return;
        if (SpriteModelSources.TryGetValue(original, out var parent) && parent.Model != null)
            original = parent.Model;
        if (sprite == original) return;
        if (SpriteModelSources.TryGetValue(sprite, out var previous) && previous.Model == original) return;
        SpriteModelSources.Remove(sprite);
        SpriteModelSources.Add(sprite, new SpriteModelSource { Model = original });
    }
    private static Sprite MinePlainPair(Sprite source, Sprite[] special, Sprite[] normal)
    {
        for (var i = 0; i < special.Length && i < normal.Length; i++)
            if (special[i] == source && normal[i] != null) return normal[i];
        return source;
    }
    private static Sprite MinePlainStar(Sprite source, Sprite[] special, Sprite[,] normal)
    {
        for (var i = 0; i < special.Length && i < normal.GetLength(0); i++)
            if (special[i] == source && normal[i, 0] != null) return normal[i, 0];
        return source;
    }
    private static Sprite GetMineColorSprite(Sprite source)
    {
        if (source == null) return source;
        if (SpriteModelSources.TryGetValue(source, out var origin) && origin.Model != null) source = origin.Model;
        // The reference gives mines the plain family sprite even if they are
        // Break/Each. A named mine color replaces the desaturation material.
        source = MinePlainPair(source, GameNoteImageContainer.NormalBreak, GameNoteImageContainer.NormalTap);
        source = MinePlainPair(source, GameNoteImageContainer.EachTap, GameNoteImageContainer.NormalTap);
        source = MinePlainPair(source, GameNoteImageContainer.BreakHold, GameNoteImageContainer.NormalHold);
        source = MinePlainPair(source, GameNoteImageContainer.EachHold, GameNoteImageContainer.NormalHold);
        source = MinePlainPair(source, GameNoteImageContainer.BreakHoldOn, GameNoteImageContainer.NormalHoldOn);
        source = MinePlainPair(source, GameNoteImageContainer.EachHoldOn, GameNoteImageContainer.NormalHoldOn);
        source = MinePlainPair(source, GameNoteImageContainer.BreakHoldOff, GameNoteImageContainer.HoldOff);
        source = MinePlainPair(source, GameNoteImageContainer.BreakSlide, GameNoteImageContainer.NormalSlide);
        source = MinePlainPair(source, GameNoteImageContainer.EachSlide, GameNoteImageContainer.NormalSlide);
        source = MinePlainStar(source, GameNoteImageContainer.BreakStar, GameNoteImageContainer.NormalStar);
        source = MinePlainStar(source, GameNoteImageContainer.EachStar, GameNoteImageContainer.NormalStar);
        source = MinePlainStar(source, GameNoteImageContainer.BreakDoubleStar, GameNoteImageContainer.NormalDoubleStar);
        source = MinePlainStar(source, GameNoteImageContainer.EachDoubleStar, GameNoteImageContainer.NormalDoubleStar);
        if (source == GameNoteImageContainer.BreakHoldEnd || source == GameNoteImageContainer.EachHoldEnd)
            source = GameNoteImageContainer.NormalHoldEnd ?? source;
        if (source == GameNoteImageContainer.EachTouch) source = GameNoteImageContainer.NormalTouch ?? source;
        if (source == GameNoteImageContainer.EachTouchPoint) source = GameNoteImageContainer.NormalTouchPoint ?? source;
        return source;
    }
}
