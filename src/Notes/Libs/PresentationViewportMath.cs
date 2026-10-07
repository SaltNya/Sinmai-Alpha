using System;

namespace SinmaiAlpha.Notes.Libs;

// All rectangles use bottom-left normalized camera coordinates. A chart's
// image shift is relative to its own frame, never the shared camera width.
internal readonly struct PresentationRect
{
    public readonly float X, Y, Width, Height;
    public float Right => X + Width;
    public float Top => Y + Height;
    public bool HasArea => Width > 0 && Height > 0;
    public PresentationRect(float x, float y, float width, float height)
    { X = x; Y = y; Width = width; Height = height; }
    public PresentationRect Shift(float x, float y) => new(X + x, Y + y, Width, Height);
    public PresentationRect Intersect(PresentationRect other)
    {
        var x = Math.Max(X, other.X); var y = Math.Max(Y, other.Y);
        return new PresentationRect(x, y, Math.Max(0, Math.Min(Right, other.Right) - x),
            Math.Max(0, Math.Min(Top, other.Top) - y));
    }
}

internal static class PresentationViewportMath
{
    public static bool Shift(PresentationRect frame, float x, float y,
        out PresentationRect destination, out PresentationRect source)
    {
        var dx = x * frame.Width; var dy = y * frame.Height;
        var camera = new PresentationRect(0, 0, 1, 1);
        destination = frame.Intersect(frame.Shift(dx, dy)).Intersect(camera).Intersect(camera.Shift(dx, dy));
        source = destination.Shift(-dx, -dy);
        return destination.HasArea;
    }

    public static float CoverAlpha(float value) => Math.Max(0, Math.Min(1, value));

    // Both values are black-cover opacity. The player's native brightness
    // remains a multiplier on the visible chart background.
    public static float MovieCoverAlpha(float nativeAlpha, float chartAlpha)
        => 1 - (1 - CoverAlpha(nativeAlpha)) * (1 - CoverAlpha(chartAlpha));
}
