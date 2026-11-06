// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// Keys a color out while the layer is drawn (remove_background method chroma). Colors are compared
// by their chroma (Cb, Cr after BT.709), so shading on the screen keys like the screen. A pixel's
// screen amount is its chroma along the key's direction minus its chroma across it, relative to the
// key's: 1 for the key itself, 0 for grays, below 0 for colors away from the key. distance = 1 - amount;
// pixels within 'tolerance' are removed, and alpha rises to full over the next 'softness'. Spill
// suppression takes the screen amount's chroma out of the pixels that stay, keeping their brightness.
public static class ChromaKey
{
    private const double KR = 0.2126;
    private const double KG = 0.7152;
    private const double KB = 0.0722;
    private const double CbScale = 1.8556;
    private const double CrScale = 1.5748;

    // The picture with the spill taken out (opaque) and the key's alpha as an Alpha8 image, both
    // the size of the source.
    public static (SKImage Picture, SKImage Alpha) Apply(SKImage source, Matte matte)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = Pixels.Read(source);
        var alpha = new byte[width * height];
        var key = RgbaColor.Parse(matte.Color);
        var (keyCb, keyCr) = Chroma(key.R / 255.0, key.G / 255.0, key.B / 255.0);
        var keyLength = Math.Sqrt(keyCb * keyCb + keyCr * keyCr);
        var ux = keyCb / keyLength;
        var uy = keyCr / keyLength;
        var tolerance = matte.Tolerance ?? 0;
        var softness = matte.Softness ?? 0;
        var spill = matte.Spill ?? 0;

        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var o = i * 4;
                var r = pixels[o] / 255.0;
                var g = pixels[o + 1] / 255.0;
                var b = pixels[o + 2] / 255.0;
                var luma = KR * r + KG * g + KB * b;
                var cb = (b - luma) / CbScale;
                var cr = (r - luma) / CrScale;
                var along = cb * ux + cr * uy;
                var across = Math.Abs(cb * uy - cr * ux);
                var screen = along - across;
                var distance = 1 - screen / keyLength;
                double keep = softness > 0
                    ? Math.Clamp((distance - tolerance) / softness, 0, 1)
                    : distance > tolerance ? 1 : 0;
                alpha[i] = (byte)Math.Round(keep * pixels[o + 3]);

                if (spill > 0 && screen > 0)
                {
                    var removed = spill * screen;
                    cb -= removed * ux;
                    cr -= removed * uy;
                    var newR = luma + CrScale * cr;
                    var newB = luma + CbScale * cb;
                    var newG = (luma - KR * newR - KB * newB) / KG;
                    pixels[o] = Byte(newR);
                    pixels[o + 1] = Byte(newG);
                    pixels[o + 2] = Byte(newB);
                }

                pixels[o + 3] = 255;
            }
        });

        return (Pixels.Rgba(pixels, width, height), Pixels.Alpha(alpha, width, height));
    }

    // The screen color: among the pixels along the picture's edges with clear chroma, the average of
    // the most common hue (in 15° steps). Null when the edges are gray.
    public static RgbaColor? Detect(SKImage source)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = Pixels.Read(source);
        var band = Math.Max(1, Math.Min(width, height) / 50);
        var bins = new (double R, double G, double B, int Count)[24];
        void Sample(int x, int y)
        {
            var o = (y * width + x) * 4;
            if (pixels[o + 3] < 128)
            {
                return;
            }

            var r = pixels[o] / 255.0;
            var g = pixels[o + 1] / 255.0;
            var b = pixels[o + 2] / 255.0;
            var (cb, cr) = Chroma(r, g, b);
            if (Math.Sqrt(cb * cb + cr * cr) < Core.Editing.MatteEditor.MinKeyChroma)
            {
                return;
            }

            var angle = Math.Atan2(cr, cb) * 180 / Math.PI + 180;
            var bin = Math.Min(23, (int)(angle / 15));
            bins[bin] = (bins[bin].R + r, bins[bin].G + g, bins[bin].B + b, bins[bin].Count + 1);
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x < band || x >= width - band || y < band || y >= height - band)
                {
                    Sample(x, y);
                }
            }
        }

        var best = bins.MaxBy(b => b.Count);
        if (best.Count == 0)
        {
            return null;
        }

        return new RgbaColor(Byte(best.R / best.Count), Byte(best.G / best.Count), Byte(best.B / best.Count), 255);
    }

    private static (double Cb, double Cr) Chroma(double r, double g, double b)
    {
        var luma = KR * r + KG * g + KB * b;
        return ((b - luma) / CbScale, (r - luma) / CrScale);
    }

    private static byte Byte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
}
