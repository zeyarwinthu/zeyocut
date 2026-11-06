// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

public static class SkiaConvert
{
    public static SKMatrix ToSkia(this Affine2D m) =>
        new((float)m.A, (float)m.C, (float)m.E, (float)m.B, (float)m.D, (float)m.F, 0, 0, 1);

    public static SKColor ToSkia(this RgbaColor color) => new(color.R, color.G, color.B, color.A);

    public static SKColor ParseColor(string? text) => RgbaColor.Parse(text).ToSkia();

    public static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

    public static SKPoint Map(this Affine2D m, double x, double y, float scale = 1)
    {
        var (px, py) = m.Apply(x, y);
        return new SKPoint((float)(px * scale), (float)(py * scale));
    }
}
