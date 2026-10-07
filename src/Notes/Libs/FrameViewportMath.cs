using System;
using UnityEngine;

namespace SinmaiAlpha.Notes.Libs;

// Keep original source pixel centers when rendering only one monitor. Rounding
// the backing viewport outward also supports fractional/screen-adjusted edges;
// the final visible rectangle is still clipped to the exact authored frame.
internal readonly struct FrameViewportCrop
{
    internal readonly PresentationRect ProjectionViewport, TextureUv;
    internal readonly int Width, Height;
    private FrameViewportCrop(PresentationRect projection, PresentationRect uv, int width, int height)
    { ProjectionViewport = projection; TextureUv = uv; Width = width; Height = height; }

    internal static bool TryCreateFull(PresentationRect bounds, int sourceWidth, int sourceHeight, out FrameViewportCrop crop)
    {
        crop = default;
        if (!TryCreate(bounds, sourceWidth, sourceHeight, out _)) return false;
        crop = new FrameViewportCrop(new PresentationRect(0, 0, 1, 1),
            bounds.Intersect(new PresentationRect(0, 0, 1, 1)), sourceWidth, sourceHeight);
        return true;
    }

    internal static bool TryCreate(PresentationRect bounds, int sourceWidth, int sourceHeight, out FrameViewportCrop crop)
    {
        crop = default;
        if (sourceWidth <= 0 || sourceHeight <= 0 || !Finite(bounds.X) || !Finite(bounds.Y) ||
            !Finite(bounds.Width) || !Finite(bounds.Height)) return false;
        var visible = bounds.Intersect(new PresentationRect(0, 0, 1, 1));
        if (!visible.HasArea) return false;
        var left = Math.Max(0, (int)Math.Floor((double)visible.X * sourceWidth));
        var bottom = Math.Max(0, (int)Math.Floor((double)visible.Y * sourceHeight));
        var right = Math.Min(sourceWidth, (int)Math.Ceiling((double)visible.Right * sourceWidth));
        var top = Math.Min(sourceHeight, (int)Math.Ceiling((double)visible.Top * sourceHeight));
        if (right <= left || top <= bottom) return false;
        var projection = new PresentationRect((float)left / sourceWidth, (float)bottom / sourceHeight,
            (float)(right - left) / sourceWidth, (float)(top - bottom) / sourceHeight);
        var uv = new PresentationRect((visible.X - projection.X) / projection.Width, (visible.Y - projection.Y) / projection.Height,
            visible.Width / projection.Width, visible.Height / projection.Height);
        crop = new FrameViewportCrop(projection, uv, right - left, top - bottom);
        return true;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal Matrix4x4 Projection(Matrix4x4 original)
    {
        var matrix = Matrix4x4.identity;
        matrix.m00 = 1 / ProjectionViewport.Width;
        matrix.m03 = (1 - 2 * ProjectionViewport.X - ProjectionViewport.Width) / ProjectionViewport.Width;
        matrix.m11 = 1 / ProjectionViewport.Height;
        matrix.m13 = (1 - 2 * ProjectionViewport.Y - ProjectionViewport.Height) / ProjectionViewport.Height;
        return matrix * original;
    }
}
