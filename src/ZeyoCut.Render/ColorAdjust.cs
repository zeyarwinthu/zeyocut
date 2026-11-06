// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// brightness -1..1 (0 = unchanged, -1 = black, 1 = white), contrast and saturation 0..4 (1 = unchanged).
public static class ColorAdjust
{
    private const float LumaR = 0.2126f;
    private const float LumaG = 0.7152f;
    private const float LumaB = 0.0722f;

    public static SKColorFilter? Create(IColorAdjustable layer, double time) =>
        Create(
            Interpolator.Evaluate(layer.Brightness, time),
            Interpolator.Evaluate(layer.Contrast, time),
            Interpolator.Evaluate(layer.Saturation, time));

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
}
