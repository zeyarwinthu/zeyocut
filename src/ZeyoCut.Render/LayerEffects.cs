// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// set_effect and set_mask on screen. A layer with effects or a mask is drawn into a layer of its
// own: the content, then the mask (in the layer's pixels), then tint, outline, drop shadow and blur
// in screen pixels times the layer's on-screen scale.
public static class LayerEffects
{
    // Effect sizes on screen are capped so a huge zoom can't stall a frame.
    private const double MaxScreenSize = 1000;
    private const double Visible = 0.01;

    // The outline is the shape grown by three rectangles whose union is close to a disc.
    private const float OutlineThin = 0.38f;
    private const float OutlineDiagonal = 0.71f;

    // A subject cutout draws its source's picture again without the outline and shadow, which the
    // source already drew around the whole layer.
    public static SKImageFilter? Filter(VisualLayer layer, double time, double scale, bool cutout = false)
    {
        SKImageFilter? filter = null;
        if (layer.Tint is { } tint && Math.Clamp(Interpolator.Evaluate(tint.Amount, time), 0, 1) is var amount && amount > 0)
        {
            var color = RgbaColor.Parse(tint.Color);
            var a = (float)(amount * color.A / 255.0);
            float[] matrix =
            [
                1 - a, 0, 0, 0, a * color.R / 255f,
                0, 1 - a, 0, 0, a * color.G / 255f,
                0, 0, 1 - a, 0, a * color.B / 255f,
                0, 0, 0, 1, 0,
            ];
            using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
            filter = Chain(filter, SKImageFilter.CreateColorFilter(colorFilter, filter));
        }

        if (!cutout && layer.Outline is { } outline && Size(outline.Width, time, scale) is var width && width > Visible)
        {
            var w = (float)width;
            using var wide = SKImageFilter.CreateDilate(w, w * OutlineThin, filter);
            using var tall = SKImageFilter.CreateDilate(w * OutlineThin, w, filter);
            using var diagonal = SKImageFilter.CreateDilate(w * OutlineDiagonal, w * OutlineDiagonal, filter);
            using var grown = SKImageFilter.CreateMerge([wide, tall, diagonal]);
            using var paint = SKColorFilter.CreateBlendMode(SkiaConvert.ParseColor(outline.Color), SKBlendMode.SrcIn);
            using var ring = SKImageFilter.CreateColorFilter(paint, grown);
            filter = Chain(filter, SKImageFilter.CreateMerge(ring, filter));
        }

        if (!cutout && layer.Shadow is { } shadow && Math.Clamp(Interpolator.Evaluate(shadow.Opacity, time), 0, 1) is var opacity && opacity > 0)
        {
            var color = SkiaConvert.ParseColor(shadow.Color);
            color = color.WithAlpha((byte)Math.Round(color.Alpha * opacity));
            var sigma = (float)Size(shadow.Blur, time, scale);
            var dx = (float)Math.Clamp(Interpolator.Evaluate(shadow.X, time) * scale, -MaxScreenSize, MaxScreenSize);
            var dy = (float)Math.Clamp(Interpolator.Evaluate(shadow.Y, time) * scale, -MaxScreenSize, MaxScreenSize);
            filter = Chain(filter, SKImageFilter.CreateDropShadow(dx, dy, sigma, sigma, color, filter));
        }

        if (layer.Blur is { } blur)
        {
            var sx = Size(blur.X, time, scale);
            var sy = Size(blur.Y, time, scale);
            if (sx > Visible || sy > Visible)
            {
                filter = Chain(filter, SKImageFilter.CreateBlur((float)sx, (float)sy, filter));
            }
        }

        return filter;
    }

    // Keeps the layer's content only inside the mask (outside with invert). Drawn in the layer's
    // pixels, inside the layer's own drawing layer.
    public static void DrawMask(SKCanvas canvas, Mask mask, double time, SKImage? image)
    {
        var x = (float)Interpolator.Evaluate(mask.X, time);
        var y = (float)Interpolator.Evaluate(mask.Y, time);
        var width = (float)Math.Max(0, Interpolator.Evaluate(mask.Width, time));
        var height = (float)Math.Max(0, Interpolator.Evaluate(mask.Height, time));
        var feather = (float)Math.Max(0, Interpolator.Evaluate(mask.Feather, time));
        using var layerPaint = new SKPaint();
        layerPaint.BlendMode = mask.Invert ? SKBlendMode.DstOut : SKBlendMode.DstIn;
        canvas.SaveLayer(layerPaint);
        using var paint = new SKPaint();
        paint.Color = SKColors.White;
        paint.IsAntialias = true;
        var rect = new SKRect(x, y, x + width, y + height);
        switch (mask.Type)
        {
            case MaskType.Rectangle:
            case MaskType.Ellipse:
                using (var soft = feather > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, feather / 2) : null)
                {
                    paint.MaskFilter = soft;
                    if (mask.Type == MaskType.Rectangle)
                    {
                        canvas.DrawRect(rect, paint);
                    }
                    else
                    {
                        canvas.DrawOval(rect, paint);
                    }
                }

                break;
            case MaskType.Image when image is not null:
                using (var soft = feather > 0 ? SKImageFilter.CreateBlur(feather / 2, feather / 2) : null)
                {
                    paint.ImageFilter = soft;
                    canvas.DrawImage(image, rect, FrameRenderer.ImageSampling, paint);
                }

                break;
        }

        canvas.Restore();
    }

    // How much the layer is scaled on screen (parents and camera included).
    public static double ScreenScale(EvaluatedLayer layer)
    {
        var world = layer.World;
        return Math.Sqrt(Math.Abs(world.A * world.D - world.B * world.C));
    }

    private static double Size(AnimatedValue value, double time, double scale) =>
        Math.Clamp(Interpolator.Evaluate(value, time) * scale, 0, MaxScreenSize);

    // Skia filters hold their inputs, so the previous step can be let go once the next wraps it.
    private static SKImageFilter Chain(SKImageFilter? previous, SKImageFilter next)
    {
        previous?.Dispose();
        return next;
    }
}
