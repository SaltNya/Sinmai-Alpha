using UnityEngine;

namespace SinmaiAlpha.Difficulty;

public static partial class ExtraDifficulty
{
    // Runtime PNG loading skips Unity's "Alpha Is Transparency" import step.
    // White RGB in invisible pixels otherwise bleeds into bilinear-filtered
    // edges, leaving a light seam where the card and its tab overlap.
    private static void PrepareTextureEdges(Texture2D texture)
    {
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        var pixels = texture.GetPixels32();
        var distance = new byte[pixels.Length];
        var queue = new int[pixels.Length];
        var read = 0;
        var write = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].a == 0) continue;
            distance[i] = 1;
            queue[write++] = i;
        }

        var changed = false;
        var width = texture.width;
        while (read < write)
        {
            var index = queue[read++];
            // Two invisible pixels cover the sampling footprint; there are
            // no mipmaps. Keep this a bounded, linear-time load operation.
            if (distance[index] >= 3) continue;
            var x = index % width;
            if (x > 0) Extend(index, index - 1);
            if (x + 1 < width) Extend(index, index + 1);
            if (index >= width) Extend(index, index - width);
            if (index + width < pixels.Length) Extend(index, index + width);
        }

        if (changed)
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        void Extend(int source, int target)
        {
            if (distance[target] != 0) return;
            var color = pixels[source];
            // Preserve every alpha value and all visible artwork exactly.
            pixels[target] = new Color32(color.r, color.g, color.b, 0);
            distance[target] = (byte)(distance[source] + 1);
            queue[write++] = target;
            changed = true;
        }
    }
}
