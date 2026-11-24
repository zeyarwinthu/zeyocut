// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Render;

// Red, green and blue amounts, as a color wheel's.
public readonly record struct Rgb(double Red, double Green, double Blue);

// brightness -1..1 (0 = unchanged, -1 = black, 1 = white), contrast and saturation 0..4 (1 = unchanged).
// A grade comes first: exposure -3..3 stops, highlights, shadows, temperature and tint -1..1 (0 = unchanged),
// then the lift, gamma and gain wheels -1..1 for each channel. A look (a 3D LUT) comes last.
public static class ColorAdjust
{
    // How many stops temperature and tint at 1 move a channel by.
    public const double Balance = 0.5;

    private const float LumaR = 0.2126f;
    private const float LumaG = 0.7152f;
    private const float LumaB = 0.0722f;

    private static readonly byte[] Identity = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    // The look as a color filter: the color, unpremultiplied and placed in the LUT's domain, found
    // between the LUT's points (the atlas has a slice of red by green for each blue point; sampling
    // mixes red and green, and two slices are mixed for blue), then mixed with the color by amount.
    private static readonly Lazy<SKRuntimeEffect> LookEffect = new(() =>
        SKRuntimeEffect.CreateColorFilter("""
            uniform shader lut;
            uniform float size;
            uniform float amount;
            uniform float3 low;
            uniform float3 high;
            half4 main(half4 color) {
                float alpha = color.a;
                if (alpha <= 0) {
                    return color;
                }
                float3 rgb = color.rgb / alpha;
                float3 p = clamp((rgb - low) / (high - low), 0, 1) * (size - 1);
                float b0 = min(floor(p.b), size - 2);
                float3 below = lut.eval(float2(b0 * size + p.r + 0.5, p.g + 0.5)).rgb;
                float3 above = lut.eval(float2((b0 + 1) * size + p.r + 0.5, p.g + 0.5)).rgb;
                float3 looked = mix(below, above, p.b - b0);
                return half4(mix(rgb, looked, amount) * alpha, alpha);
            }
            """, out var errors) ?? throw new InvalidOperationException($"the look's color filter didn't compile: {errors}"));

    // A layer's color as one filter: the grade, then saturation, contrast and brightness, then its
    // look, whose LUT look gives (by the LUT asset's id); null when it leaves the picture as it is.
    public static SKColorFilter? Create(IColorAdjustable layer, double time, Func<string, LookTable>? look = null)
    {
        var filter = Create(
            Interpolator.Evaluate(layer.Brightness, time),
            Interpolator.Evaluate(layer.Contrast, time),
            Interpolator.Evaluate(layer.Saturation, time));
        if (layer.Grade is not { } grade)
        {
            return filter;
        }

        static Rgb Wheel(ColorWheel? wheel, double time) => wheel is null
            ? default
            : new Rgb(Interpolator.Evaluate(wheel.Red, time), Interpolator.Evaluate(wheel.Green, time), Interpolator.Evaluate(wheel.Blue, time));

        var graded = Grade(
            Interpolator.Evaluate(grade.Exposure, time),
            Interpolator.Evaluate(grade.Highlights, time),
            Interpolator.Evaluate(grade.Shadows, time),
            Interpolator.Evaluate(grade.Temperature, time),
            Interpolator.Evaluate(grade.Tint, time),
            Wheel(grade.Lift, time),
            Wheel(grade.Gamma, time),
            Wheel(grade.Gain, time));
        filter = Then(graded, filter);
        if (grade.Look is { } chosen && look is not null)
        {
            filter = Then(filter, Look(look(chosen.Asset), Interpolator.Evaluate(chosen.Amount, time)));
        }

        return filter;
    }

    // The LUT laid over the picture by amount (0 to 1); null at 0.
    public static SKColorFilter? Look(LookTable table, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        if (amount == 0)
        {
            return null;
        }

        var effect = LookEffect.Value;
        using var shader = table.Atlas.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
        using var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["size"] = (float)table.Size,
            ["amount"] = (float)amount,
            ["low"] = table.DomainMin,
            ["high"] = table.DomainMax,
        };
        using var children = new SKRuntimeEffectChildren(effect) { ["lut"] = shader };
        return effect.ToColorFilter(uniforms, children);
    }

    // first, then second; either may be null.
    private static SKColorFilter? Then(SKColorFilter? first, SKColorFilter? second)
    {
        if (first is null || second is null)
        {
            return first ?? second;
        }

        using (first)
        using (second)
        {
            return SKColorFilter.CreateCompose(second, first);
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
    public static SKColorFilter? Grade(double exposure, double highlights, double shadows, double temperature, double tint, Rgb lift = default, Rgb gamma = default, Rgb gain = default)
    {
        var (red, green, blue) = Tables(exposure, highlights, shadows, temperature, tint, lift, gamma, gain);
        return red.AsSpan().SequenceEqual(Identity) && green.AsSpan().SequenceEqual(Identity) && blue.AsSpan().SequenceEqual(Identity)
            ? null
            : SKColorFilter.CreateTable(Identity, red, green, blue);
    }

    // What the grade makes of each 8-bit value of red, green and blue. Exposure and the white
    // balance scale the light itself (sRGB decoded): each stop of exposure doubles it, temperature
    // raises red and lowers blue, and tint lowers green toward magenta. Then two curves that keep
    // black and white where they are: shadows lifts or deepens the dark values (most at a third of
    // the way up) and highlights the light ones (most at two thirds). Then each channel's wheels.
    // Each table only goes up, so no two values swap places.
    public static (byte[] Red, byte[] Green, byte[] Blue) Tables(double exposure, double highlights, double shadows, double temperature, double tint, Rgb lift = default, Rgb gamma = default, Rgb gain = default)
    {
        var light = Math.Pow(2, Math.Clamp(exposure, -3, 3));
        temperature = Math.Clamp(temperature, -1, 1);
        tint = Math.Clamp(tint, -1, 1);
        highlights = Math.Clamp(highlights, -1, 1);
        shadows = Math.Clamp(shadows, -1, 1);
        return (
            Table(light * Math.Pow(2, Balance * temperature), highlights, shadows, lift.Red, gamma.Red, gain.Red),
            Table(light * Math.Pow(2, -Balance * tint), highlights, shadows, lift.Green, gamma.Green, gain.Green),
            Table(light * Math.Pow(2, -Balance * temperature), highlights, shadows, lift.Blue, gamma.Blue, gain.Blue));
    }

    // A value from 0 to 1 through one channel's wheels, as Resolve's lift, gamma and gain: lift
    // moves black and keeps white, gamma bends the middle and keeps both, gain scales and keeps
    // black. Each at 0 leaves the value as it is.
    public static double Wheels(double value, double lift, double gamma, double gain)
    {
        var y = value + Math.Clamp(lift, -1, 1) * (1 - value);
        y = Math.Pow(Math.Max(y, 0), Math.Pow(2, -Math.Clamp(gamma, -1, 1)));
        return y * (1 + Math.Clamp(gain, -1, 1));
    }

    private static byte[] Table(double light, double highlights, double shadows, double lift, double gamma, double gain)
    {
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            var y = Encode(Math.Clamp(Decode(i / 255.0) * light, 0, 1));
            y += shadows * y * (1 - y) * (1 - y);
            y += highlights * y * y * (1 - y);
            y = Wheels(Math.Clamp(y, 0, 1), lift, gamma, gain);
            table[i] = (byte)Math.Round(Math.Clamp(y, 0, 1) * 255, MidpointRounding.AwayFromZero);
        }

        return table;
    }

    // A LUT as an image the look's filter samples: a slice of red by green for each blue point, side
    // by side, in 32-bit floats.
    public static LookTable Table(CubeLut lut)
    {
        var size = lut.Size;
        var pixels = new float[size * size * size * 4];
        for (var b = 0; b < size; b++)
        {
            for (var g = 0; g < size; g++)
            {
                for (var r = 0; r < size; r++)
                {
                    var from = ((b * size + g) * size + r) * 3;
                    var to = ((g * size * size) + b * size + r) * 4;
                    pixels[to] = lut.Data[from];
                    pixels[to + 1] = lut.Data[from + 1];
                    pixels[to + 2] = lut.Data[from + 2];
                    pixels[to + 3] = 1;
                }
            }
        }

        var info = new SKImageInfo(size * size, size, SKColorType.RgbaF32, SKAlphaType.Unpremul);
        var atlas = SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray())
            ?? throw new ZeyoCutException($"could not make an image of a {size}-point LUT");
        return new LookTable(atlas, size, [.. lut.DomainMin], [.. lut.DomainMax]);
    }

    private static double Decode(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double Encode(double value) => value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
}

// A LUT ready to draw: its image (see ColorAdjust.Table), its points a side and its domain.
public sealed class LookTable(SKImage atlas, int size, float[] domainMin, float[] domainMax) : IDisposable
{
    public SKImage Atlas { get; } = atlas;

    public int Size { get; } = size;

    public float[] DomainMin { get; } = domainMin;

    public float[] DomainMax { get; } = domainMax;

    public void Dispose() => Atlas.Dispose();
}
