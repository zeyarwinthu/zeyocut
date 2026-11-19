// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// brightness -1..1 (0 = unchanged, -1 = black, 1 = white), contrast and saturation 0..4 (1 = unchanged).
// A grade comes first: exposure -3..3 stops, highlights, shadows, temperature and tint -1..1 (0 = unchanged).
public static class ColorAdjust
{
    // How many stops temperature and tint at 1 move a channel by.
    public const double Balance = 0.5;

    private const float LumaR = 0.2126f;
    private const float LumaG = 0.7152f;
    private const float LumaB = 0.0722f;

    private static readonly byte[] Identity = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    public static SKColorFilter? Create(IColorAdjustable layer, double time)
    {
        var adjust = Create(
            Interpolator.Evaluate(layer.Brightness, time),
            Interpolator.Evaluate(layer.Contrast, time),
            Interpolator.Evaluate(layer.Saturation, time));
        if (layer.Grade is not { } grade)
        {
            return adjust;
        }

        var graded = Grade(
            Interpolator.Evaluate(grade.Exposure, time),
            Interpolator.Evaluate(grade.Highlights, time),
            Interpolator.Evaluate(grade.Shadows, time),
            Interpolator.Evaluate(grade.Temperature, time),
            Interpolator.Evaluate(grade.Tint, time));
        if (graded is null || adjust is null)
        {
            return graded ?? adjust;
        }

        using (graded)
        using (adjust)
        {
            return SKColorFilter.CreateCompose(adjust, graded);
        }
    }

    public static SKColorFilter? Create(double brightness, double contrast, double saturation)
    {
        var b = (float)Math.Clamp(brightness, -1, 1);
        var c = (float)Math.Clamp(contrast, 0, 4);
        var s = (float)Math.Clamp(saturation, 0, 4);
        if (b == 0 && c == 1 && s == 1)
        {
            return null;
        }

        // Saturation around Rec.709 luma, then contrast around mid gray, then the brightness offset.
        var r = (1 - s) * LumaR;
        var g = (1 - s) * LumaG;
        var bl = (1 - s) * LumaB;
        var offset = 0.5f * (1 - c) + b;
        float[] matrix =
        [
            c * (r + s), c * g, c * bl, 0, offset,
            c * r, c * (g + s), c * bl, 0, offset,
            c * r, c * g, c * (bl + s), 0, offset,
            0, 0, 0, 1, 0,
        ];
        return SKColorFilter.CreateColorMatrix(matrix);
    }

    // The grade as a table for each channel; null when it leaves the picture as it is.
    public static SKColorFilter? Grade(double exposure, double highlights, double shadows, double temperature, double tint)
    {
        var (red, green, blue) = Tables(exposure, highlights, shadows, temperature, tint);
        return red.AsSpan().SequenceEqual(Identity) && green.AsSpan().SequenceEqual(Identity) && blue.AsSpan().SequenceEqual(Identity)
            ? null
            : SKColorFilter.CreateTable(Identity, red, green, blue);
    }

    // What the grade makes of each 8-bit value of red, green and blue. Exposure and the white
    // balance scale the light itself (sRGB decoded): each stop of exposure doubles it, temperature
    // raises red and lowers blue, and tint lowers green toward magenta. Then two curves that keep
    // black and white where they are: shadows lifts or deepens the dark values (most at a third of
    // the way up) and highlights the light ones (most at two thirds). Each table only goes up, so
    // no two values swap places.
    public static (byte[] Red, byte[] Green, byte[] Blue) Tables(double exposure, double highlights, double shadows, double temperature, double tint)
    {
        var gain = Math.Pow(2, Math.Clamp(exposure, -3, 3));
        temperature = Math.Clamp(temperature, -1, 1);
        tint = Math.Clamp(tint, -1, 1);
        highlights = Math.Clamp(highlights, -1, 1);
        shadows = Math.Clamp(shadows, -1, 1);
        return (
            Table(gain * Math.Pow(2, Balance * temperature), highlights, shadows),
            Table(gain * Math.Pow(2, -Balance * tint), highlights, shadows),
            Table(gain * Math.Pow(2, -Balance * temperature), highlights, shadows));
    }

    private static byte[] Table(double gain, double highlights, double shadows)
    {
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            var y = Encode(Math.Clamp(Decode(i / 255.0) * gain, 0, 1));
            y += shadows * y * (1 - y) * (1 - y);
            y += highlights * y * y * (1 - y);
            table[i] = (byte)Math.Round(Math.Clamp(y, 0, 1) * 255, MidpointRounding.AwayFromZero);
        }

        return table;
    }

    private static double Decode(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double Encode(double value) => value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
}
