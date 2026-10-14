using System;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // SetPixels32 copies the array into the texture (Texture2DScripting::SetAllPixels32 -> Texture2D::SetPixels32 ->
    // ImageReference::BlitImage) and keeps no reference, so one buffer per size serves every recolour instead of a new
    // 4.67 MB array per EachBar colour while GameProcess has the GC disabled. Every element is rewritten each time.
    private static Color32[] VisualPixelScratch;

    private static Color32[] VisualPixelBuffer(int length)
    {
        if (VisualPixelScratch == null || VisualPixelScratch.Length != length)
        {
            VisualPixelScratch = new Color32[length];
        }
        return VisualPixelScratch;
    }

    // Per-call memo of a pure per-pixel function, keyed by the input Color32's packed bytes (open addressing, stamped
    // so it needs no clearing; capped at half the table, later distinct inputs are just computed).
    private const int VisualPixelMemoBits = 16;

    private static int[] VisualPixelMemoKeys;

    private static int[] VisualPixelMemoStamps;

    private static Color32[] VisualPixelMemoValues;

    private static int VisualPixelMemoStamp;

    private static int VisualPixelMemoCount;

    private static void BeginVisualPixelMemo()
    {
        if (VisualPixelMemoKeys == null)
        {
            VisualPixelMemoKeys = new int[1 << VisualPixelMemoBits];
            VisualPixelMemoStamps = new int[1 << VisualPixelMemoBits];
            VisualPixelMemoValues = new Color32[1 << VisualPixelMemoBits];
        }
        if (++VisualPixelMemoStamp == int.MaxValue)
        {
            Array.Clear(VisualPixelMemoStamps, 0, VisualPixelMemoStamps.Length);
            VisualPixelMemoStamp = 1;
        }
        VisualPixelMemoCount = 0;
    }

    private static bool TryVisualPixelMemo(int key, out int slot, out Color32 value)
    {
        slot = (int)((uint)(key * -1640531535) >> (32 - VisualPixelMemoBits));
        while (VisualPixelMemoStamps[slot] == VisualPixelMemoStamp)
        {
            if (VisualPixelMemoKeys[slot] == key)
            {
                value = VisualPixelMemoValues[slot];
                return true;
            }
            slot = (slot + 1) & ((1 << VisualPixelMemoBits) - 1);
        }
        value = default(Color32);
        return false;
    }

    private static void StoreVisualPixelMemo(int slot, int key, Color32 value)
    {
        if (VisualPixelMemoCount < 1 << (VisualPixelMemoBits - 1))
        {
            VisualPixelMemoStamps[slot] = VisualPixelMemoStamp;
            VisualPixelMemoKeys[slot] = key;
            VisualPixelMemoValues[slot] = value;
            VisualPixelMemoCount++;
        }
    }

    // GetVisualTintSprite's transparent branch depends on the pixel's alpha only (Color32 -> Color converts each
    // channel on its own), so its decision and result per alpha come from these tables, built with the same expressions.
    private static bool[] VisualTransparentAlpha;

    private static Color32[] VisualTransparentPixels;

    private static void EnsureVisualTransparentTable()
    {
        if (VisualTransparentAlpha != null)
        {
            return;
        }
        bool[] array = new bool[256];
        Color32[] array2 = new Color32[256];
        for (int i = 0; i < 256; i++)
        {
            Color color3 = new Color32(0, 0, 0, (byte)i);
            if (color3.a < 0.01f)
            {
                array[i] = true;
                array2[i] = new Color(0f, 0f, 0f, color3.a);
            }
        }
        VisualTransparentPixels = array2;
        VisualTransparentAlpha = array;
    }

    // Memoize each RGBA transform; keep expression order for Unity Mono float parity.
    private static void TintVisualPixels(Color32[] source, Color32[] array, int rgb, Color32 color)
    {
        Color.RGBToHSV(new Color((float)((rgb >> 16) & 0xFF) / 255f, (float)((rgb >> 8) & 0xFF) / 255f, (float)(rgb & 0xFF) / 255f, 1f), out var H, out var S, out var V);
        Color color2 = color;
        EnsureVisualTransparentTable();
        BeginVisualPixelMemo();
        bool[] visualTransparentAlpha = VisualTransparentAlpha;
        Color32[] visualTransparentPixels = VisualTransparentPixels;
        for (int i = 0; i < array.Length; i++)
        {
            Color32 color5 = source[i];
            if (visualTransparentAlpha[color5.a])
            {
                array[i] = visualTransparentPixels[color5.a];
                continue;
            }
            int key = color5.r | (color5.g << 8) | (color5.b << 16) | (color5.a << 24);
            if (TryVisualPixelMemo(key, out var slot, out var value))
            {
                array[i] = value;
                continue;
            }
            Color color3 = color5;
            if (color3.a < 0.01f)
            {
                array[i] = new Color(0f, 0f, 0f, color3.a);
                continue;
            }
            color3.r *= color2.r;
            color3.g *= color2.g;
            color3.b *= color2.b;
            Color.RGBToHSV(color3, out var _, out var S2, out var V2);
            float num = VisualSmooth(0.04f, 0.16f, V2);
            float num2 = VisualSmooth(0.05f, 0.25f, S2);
            float num3 = 0.35f * (1f - V);
            float v = Mathf.Clamp01(V2 * V + num3 * (V2 - 0.5f));
            Color b = Color.HSVToRGB(H, Mathf.Clamp01(S2 * S), v);
            Color a = Color.Lerp(color3, b, num * num2);
            float num4 = color3.r * 0.299f + color3.g * 0.587f + color3.b * 0.114f;
            float num5 = Mathf.Clamp01(num4 * (V * 2f) + num3 * (num4 - 0.5f));
            Color b2 = Color.Lerp(color3, new Color(num5, num5, num5, color3.a), num);
            Color color4 = Color.Lerp(a, b2, 1f - VisualSmooth(0.05f, 0.2f, S));
            color4.a = color3.a;
            array[i] = color4;
            StoreVisualPixelMemo(slot, key, array[i]);
        }
    }

    // GetBorrowedGraySprite's per-pixel gray, memoised the same way (original statements, bit-identical).
    private static void GrayVisualPixels(Color32[] source, Color32[] array)
    {
        BeginVisualPixelMemo();
        for (int i = 0; i < array.Length; i++)
        {
            Color32 color2 = source[i];
            int key = color2.r | (color2.g << 8) | (color2.b << 16) | (color2.a << 24);
            if (TryVisualPixelMemo(key, out var slot, out var value))
            {
                array[i] = value;
                continue;
            }
            Color color = color2;
            float num = color.r * 0.299f + color.g * 0.587f + color.b * 0.114f;
            array[i] = new Color(num, num, num, color.a);
            StoreVisualPixelMemo(slot, key, array[i]);
        }
    }

}
