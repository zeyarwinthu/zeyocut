// SPDX-License-Identifier: MIT

namespace ZeyoCut.Editor;

// How big the preview shows the frame. A zoom is screen pixels per frame pixel, so 100% shows each
// pixel of the frame on one pixel of the screen at any display scaling; fit is as big as the panel
// allows, keeping the canvas's aspect.
public static class PreviewZoom
{
    // The size to lay the frame out at, in device-independent pixels, and the zoom that is. A null
    // zoom fits the frame in the space available.
    public static (double Width, double Height, double Zoom) Layout(int canvasWidth, int canvasHeight, double? zoom, double availableWidth, double availableHeight, double pixelsPerDip)
    {
        var scale = zoom is { } z
            ? z / pixelsPerDip
            : Math.Max(0, Math.Min(availableWidth / canvasWidth, availableHeight / canvasHeight));
        return (canvasWidth * scale, canvasHeight * scale, scale * pixelsPerDip);
    }

    // The canvas's shape as the viewer's header names it: 16:9, 9:16, 4:5, or 1.85:1 when the
    // whole numbers that make it are big.
    public static string Ratio(int width, int height)
    {
        static int Divisor(int a, int b) => b == 0 ? a : Divisor(b, a % b);
        var divisor = Math.Max(1, Divisor(width, height));
        var (w, h) = (width / divisor, height / divisor);
        return w <= 32 && h <= 32
            ? $"{w}:{h}"
            : $"{((double)width / Math.Max(1, height)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}:1";
    }
}
