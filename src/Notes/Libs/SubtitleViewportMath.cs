namespace SinmaiAlpha.Notes.Libs;

// GUI coordinates are top-left pixels. Source UVs are bottom-left camera
// coordinates. Keep the conversion here so split/compact displays crop the
// same logical subtitle frame instead of reflowing text independently.
internal static class SubtitleViewportMath
{
    internal static bool Map(PresentationRect frame, PresentationRect imageUv,
        PresentationRect displayPixels, float sourceWidth, float sourceHeight,
        out PresentationRect crop, out PresentationRect destination)
    {
        crop = destination = default;
        if (!frame.HasArea || !imageUv.HasArea || !displayPixels.HasArea || sourceWidth <= 0 || sourceHeight <= 0) return false;
        var visible = frame.Intersect(imageUv);
        if (!visible.HasArea) return false;
        crop = new PresentationRect((visible.X - frame.X) * sourceWidth, (frame.Top - visible.Top) * sourceHeight,
            visible.Width * sourceWidth, visible.Height * sourceHeight);
        destination = new PresentationRect(displayPixels.X + (visible.X - imageUv.X) / imageUv.Width * displayPixels.Width,
            displayPixels.Y + (imageUv.Top - visible.Top) / imageUv.Height * displayPixels.Height,
            visible.Width / imageUv.Width * displayPixels.Width, visible.Height / imageUv.Height * displayPixels.Height);
        return true;
    }
}
