// SPDX-License-Identifier: MIT

namespace ZeyoCut.Editor;

// A scope drawn: BGRA pixels (premultiplied), Width by Height.
public sealed record ScopePicture(int Width, int Height, byte[] Pixels);

// The Color view's scopes, from the frame the preview drew (BGRA, premultiplied, opaque where the
// canvas is). The waveform puts each column of the frame in a column of the scope, with its pixels
// at their brightness (Rec. 709 luma: black at the bottom, white at the top), brighter where more
// of them are. The vectorscope puts each pixel at its color's hue and strength around the middle,
// gray in the middle, in the pixels' own color.
public static class Scopes
{
    private const byte WaveR = 0x8E;
    private const byte WaveG = 0xE0;
    private const byte WaveB = 0xB0;

    // How far out the vectorscope's edge is, in chroma: a little past the strongest colors' 0.5.
    public const double Reach = 0.6;

    // Rounded, so a gray's luma is its own value rather than a hair under it.
    public static double Luma(byte r, byte g, byte b) => Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b, 9);

    // Rec. 709 color difference of a pixel, -0.5 to 0.5: blue (Cb) and red (Cr).
    public static (double Cb, double Cr) Chroma(byte r, byte g, byte b)
    {
        var y = Luma(r, g, b);
        return ((b - y) / 1.8556 / 255, (r - y) / 1.5748 / 255);
    }

    // Where a color goes on a vectorscope size pixels across: Cb to the right, Cr up, Reach at the
    // edge.
    public static (int X, int Y) Spot(byte r, byte g, byte b, int size)
    {
        var (cb, cr) = Chroma(r, g, b);
        var half = (size - 1) / 2.0;
        return ((int)Math.Round(half + cb / Reach * half), (int)Math.Round(half - cr / Reach * half));
    }

    public static ScopePicture Waveform(byte[] pixels, int width, int height, int columns, int rows)
    {
        var counts = new int[columns * rows];
        var step = Math.Max(1, (width * height) / 400_000);
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i + 3] == 0)
                {
                    continue;
                }

                var (r, g, b) = Unpremultiplied(pixels, i);
                var row = rows - 1 - (int)(Luma(r, g, b) / 256 * rows);
                counts[row * columns + x * columns / width]++;
            }
        }

        // A column's pixels spread over its rows: a cell as full as an even spread is half bright.
        var even = Math.Max(1.0, (double)height / step * width / columns / rows);
        var picture = new byte[columns * rows * 4];
        for (var cell = 0; cell < counts.Length; cell++)
        {
            if (counts[cell] > 0)
            {
                Paint(picture, cell, WaveR, WaveG, WaveB, Strength(counts[cell], even));
            }
        }

        return new ScopePicture(columns, rows, picture);
    }

    public static ScopePicture Vectorscope(byte[] pixels, int width, int height, int size)
    {
        var counts = new int[size * size];
        var sums = new long[size * size * 3];
        var step = Math.Max(1, (width * height) / 400_000);
        var seen = 0;
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i + 3] == 0)
                {
                    continue;
                }

                var (r, g, b) = Unpremultiplied(pixels, i);
                var (sx, sy) = Spot(r, g, b, size);
                if (sx < 0 || sy < 0 || sx >= size || sy >= size)
                {
                    continue;
                }

                var cell = sy * size + sx;
                counts[cell]++;
                sums[cell * 3] += r;
                sums[cell * 3 + 1] += g;
                sums[cell * 3 + 2] += b;
                seen++;
            }
        }

        var even = Math.Max(1.0, seen / 2000.0);
        var picture = new byte[size * size * 4];
        for (var cell = 0; cell < counts.Length; cell++)
        {
            if (counts[cell] > 0)
            {
                // The cell's average color, lifted so dark colors still show on the dark scope.
                var (r, g, b) = (sums[cell * 3] / (double)counts[cell], sums[cell * 3 + 1] / (double)counts[cell], sums[cell * 3 + 2] / (double)counts[cell]);
                var lift = 200 / Math.Max(1, Math.Max(r, Math.Max(g, b)));
                Paint(picture, cell, Lifted(r, lift), Lifted(g, lift), Lifted(b, lift), Strength(counts[cell], even));
            }
        }

        return new ScopePicture(size, size, picture);
    }

    // How bright a cell is from its count: a few pixels show faintly, as many as an even spread
    // half bright, and ten times that full.
    private static double Strength(int count, double even) => Math.Clamp(0.25 + 0.75 * Math.Log(1 + count) / Math.Log(1 + 10 * even), 0.25, 1);

    private static (byte R, byte G, byte B) Unpremultiplied(byte[] pixels, int i)
    {
        var a = pixels[i + 3];
        return a == 255
            ? (pixels[i + 2], pixels[i + 1], pixels[i])
            : ((byte)Math.Min(255, pixels[i + 2] * 255 / a), (byte)Math.Min(255, pixels[i + 1] * 255 / a), (byte)Math.Min(255, pixels[i] * 255 / a));
    }

    private static byte Lifted(double value, double lift) => (byte)Math.Clamp(Math.Round(value * Math.Max(1, lift)), 0, 255);

    private static void Paint(byte[] picture, int cell, byte r, byte g, byte b, double alpha)
    {
        var i = cell * 4;
        picture[i] = (byte)Math.Round(b * alpha);
        picture[i + 1] = (byte)Math.Round(g * alpha);
        picture[i + 2] = (byte)Math.Round(r * alpha);
        picture[i + 3] = (byte)Math.Round(255 * alpha);
    }
}
