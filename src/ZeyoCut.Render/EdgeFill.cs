// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

public sealed record EdgeFillResult(byte[] Alpha, int Width, int Height, RgbaColor Background, double Removed);

// remove_background method edgeFill, for art on a plain background: everything reachable from the
// image's border through pixels close to the background color is removed (4-connected, so a
// one-pixel outline holds), and pixels touching the removed area fade in over one more tolerance of
// color distance. Areas of the background color that the art encloses stay. Pixels that are already
// transparent count as background.
public static class EdgeFill
{
    private const int Transparent = 8;

    public static EdgeFillResult Compute(SKImage source, string? color, double tolerance)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = Pixels.Read(source);
        var background = color is not null ? RgbaColor.Parse(color) : BorderColor(pixels, width, height);
        var limit = Math.Max(tolerance, 1e-6);
        var band = Math.Max(tolerance, 0.02);

        double Distance(int i)
        {
            var o = i * 4;
            if (pixels[o + 3] < Transparent)
            {
                return 0;
            }

            var dr = pixels[o] - background.R;
            var dg = pixels[o + 1] - background.G;
            var db = pixels[o + 2] - background.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db) / (255 * Math.Sqrt(3));
        }

        var count = width * height;
        var removed = new bool[count];
        var queue = new Queue<int>();
        void Seed(int i)
        {
            if (!removed[i] && Distance(i) <= limit)
            {
                removed[i] = true;
                queue.Enqueue(i);
            }
        }

        for (var x = 0; x < width; x++)
        {
            Seed(x);
            Seed((height - 1) * width + x);
        }

        for (var y = 0; y < height; y++)
        {
            Seed(y * width);
            Seed(y * width + width - 1);
        }

        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var x = i % width;
            if (x > 0)
            {
                Seed(i - 1);
            }

            if (x < width - 1)
            {
                Seed(i + 1);
            }

            if (i >= width)
            {
                Seed(i - width);
            }

            if (i < count - width)
            {
                Seed(i + width);
            }
        }

        var alpha = new byte[count];
        var transparent = 0;
        for (var i = 0; i < count; i++)
        {
            if (removed[i])
            {
                transparent++;
                continue;
            }

            var x = i % width;
            var touches = (x > 0 && removed[i - 1]) || (x < width - 1 && removed[i + 1])
                || (i >= width && removed[i - width]) || (i < count - width && removed[i + width]);
            alpha[i] = touches ? (byte)Math.Round(Math.Clamp((Distance(i) - tolerance) / band, 0, 1) * 255) : (byte)255;
        }

        return new EdgeFillResult(alpha, width, height, background, transparent / (double)count);
    }

    // The most common color along the border (in steps of 8 per channel), averaged within its step.
    private static RgbaColor BorderColor(byte[] pixels, int width, int height)
    {
        var bins = new Dictionary<int, (long R, long G, long B, int Count)>();
        void Add(int i)
        {
            var o = i * 4;
            if (pixels[o + 3] < Transparent)
            {
                return;
            }

            var key = (pixels[o] >> 3) << 10 | (pixels[o + 1] >> 3) << 5 | pixels[o + 2] >> 3;
            bins.TryGetValue(key, out var bin);
            bins[key] = (bin.R + pixels[o], bin.G + pixels[o + 1], bin.B + pixels[o + 2], bin.Count + 1);
        }

        for (var x = 0; x < width; x++)
        {
            Add(x);
            Add((height - 1) * width + x);
        }

        for (var y = 1; y < height - 1; y++)
        {
            Add(y * width);
            Add(y * width + width - 1);
        }

        if (bins.Count == 0)
        {
            return new RgbaColor(255, 255, 255, 255);
        }

        var best = bins.OrderByDescending(b => b.Value.Count).ThenBy(b => b.Key).First().Value;
        return new RgbaColor((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count), 255);
    }
}
