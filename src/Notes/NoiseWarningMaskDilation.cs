using System;

namespace SinmaiAlpha.Notes.Libs;

// Same clipped radius-two square dilation as ProtectWarningBorder. This PC
// implementation scans byte runs directly and needs no ZXing/game dependency.
internal sealed class WarningRunDilation
{
    private byte[] onesRow;

    internal void Dilate(int w, int h, byte[] pixels, byte[] scratch)
    {
        Array.Clear(scratch, 0, scratch.Length);
        if (w <= 0 || h <= 0)
        {
            Array.Clear(pixels, 0, pixels.Length);
            return;
        }
        if (onesRow == null || onesRow.Length < w)
        {
            onesRow = new byte[w];
            for (int i = 0; i < onesRow.Length; i++) onesRow[i] = 255;
        }

        // First pass: the union of expanded horizontal nonzero runs. A run
        // never crosses its source row, including on one-pixel-wide images.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int x = 0;
            while (x < w)
            {
                while (x < w && pixels[row + x] == 0) x++;
                if (x == w) break;
                int start = x;
                while (x < w && pixels[row + x] != 0) x++;
                int left = Math.Max(0, start - 2);
                int right = Math.Min(w, x + 2);
                Buffer.BlockCopy(onesRow, 0, scratch, row + left, right - left);
            }
        }

        Array.Clear(pixels, 0, pixels.Length);
        // Second pass: copy each horizontal union run to the same clipped
        // five output rows. Overlapping copies of 255 preserve the old union.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int x = 0;
            while (x < w)
            {
                while (x < w && scratch[row + x] == 0) x++;
                if (x == w) break;
                int start = x;
                while (x < w && scratch[row + x] != 0) x++;
                int top = Math.Max(0, y - 2);
                int bottom = Math.Min(h - 1, y + 2);
                for (int targetY = top; targetY <= bottom; targetY++)
                    Buffer.BlockCopy(onesRow, 0, pixels, targetY * w + start, x - start);
            }
        }
    }

    internal void Clear()
    {
        onesRow = null;
    }
}
