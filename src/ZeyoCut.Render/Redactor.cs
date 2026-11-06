// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// apply_redaction's boxes hidden on a decoded video frame, before the clip is drawn. Inside the box
// it is hidden completely; the feather adds a soft edge outside it that fades out within that many
// pixels.
public static class Redactor
{
    private static readonly SKSamplingOptions Exact = new(SKFilterMode.Nearest);

    // A copy of the frame with each area hidden in its redaction's style, or null when there is no
    // area. scaleX and scaleY are decoded pixels per source pixel.
    public static SKImage? Apply(SKImage frame, IReadOnlyList<(RedactionLayer Layer, RedactionArea Area)> areas, double scaleX, double scaleY)
    {
        if (areas.Count == 0)
        {
            return null;
        }

        using var surface = SKSurface.Create(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException($"could not create a {frame.Width}x{frame.Height} surface to redact a frame");
        var canvas = surface.Canvas;
        canvas.DrawImage(frame, 0, 0, Exact);
        foreach (var (layer, area) in areas)
        {
            Hide(canvas, frame, layer, area, scaleX, scaleY);
        }

        return surface.Snapshot();
    }

    private static void Hide(SKCanvas canvas, SKImage frame, RedactionLayer layer, RedactionArea area, double scaleX, double scaleY)
    {
        var box = new SKRect((float)(area.X * scaleX), (float)(area.Y * scaleY), (float)((area.X + area.Width) * scaleX), (float)((area.Y + area.Height) * scaleY));
        var feather = (float)(layer.Feather * (scaleX + scaleY) / 2);
        var outer = box;
        outer.Inflate(feather, feather);
        if (!outer.IntersectsWith(new SKRect(0, 0, frame.Width, frame.Height)))
        {
            return;
        }

        var block = RedactionEditor.BlockSize(area.Width, area.Height, layer.Strength);
        using var patch = layer.Style switch
        {
            RedactionStyle.Pixelate => Pixelated(frame, outer, (float)(block * scaleX), (float)(block * scaleY)),
            RedactionStyle.Blur => Blurred(frame, outer, (float)(block * (scaleX + scaleY) / 2)),
            _ => null,
        };
        using var paint = new SKPaint();
        paint.IsAntialias = true;
        if (patch is null)
        {
            paint.Color = SkiaConvert.ParseColor(layer.Color);
        }
        else
        {
            paint.Shader = patch;
        }

        if (feather > 0)
        {
            using var soft = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, feather / 6);
            paint.MaskFilter = soft;
            var edge = box;
            edge.Inflate(feather / 2, feather / 2);
            canvas.DrawRect(edge, paint);
            paint.MaskFilter = null;
        }

        canvas.DrawRect(box, paint);
    }

    // Blocks from the area's top-left corner, each the average of the frame's pixels under it.
    private static SKShader Pixelated(SKImage frame, SKRect area, float blockX, float blockY)
    {
        var columns = Math.Max(1, (int)Math.Ceiling(area.Width / blockX));
        var rows = Math.Max(1, (int)Math.Ceiling(area.Height / blockY));
        var left = Math.Max(0, (int)Math.Floor(area.Left));
        var top = Math.Max(0, (int)Math.Floor(area.Top));
        var right = Math.Min(frame.Width, (int)Math.Ceiling(area.Right));
        var bottom = Math.Min(frame.Height, (int)Math.Ceiling(area.Bottom));
        var sums = new long[columns * rows * 4];
        var counts = new int[columns * rows];
        using (var region = frame.Subset(new SKRectI(left, top, right, bottom)) ?? throw new ZeyoCutException("could not read the part of the frame to redact"))
        {
            var pixels = Pixels.Read(region);
            var width = right - left;
            for (var y = top; y < bottom; y++)
            {
                var row = Math.Clamp((int)((y + 0.5f - area.Top) / blockY), 0, rows - 1);
                for (var x = left; x < right; x++)
                {
                    var cell = row * columns + Math.Clamp((int)((x + 0.5f - area.Left) / blockX), 0, columns - 1);
                    var at = ((y - top) * width + x - left) * 4;
                    sums[cell * 4] += pixels[at];
                    sums[cell * 4 + 1] += pixels[at + 1];
                    sums[cell * 4 + 2] += pixels[at + 2];
                    sums[cell * 4 + 3] += pixels[at + 3];
                    counts[cell]++;
                }
            }
        }

        var cells = new byte[columns * rows * 4];
        for (var cell = 0; cell < counts.Length; cell++)
        {
            for (var channel = 0; channel < 4 && counts[cell] > 0; channel++)
            {
                cells[cell * 4 + channel] = (byte)((sums[cell * 4 + channel] + counts[cell] / 2) / counts[cell]);
            }
        }

        using var image = Pixels.Rgba(cells, columns, rows);
        return image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest), SKMatrix.CreateScaleTranslation(blockX, blockY, area.Left, area.Top));
    }

    // The frame around the area blurred with a radius (sigma) of 'radius' pixels.
    private static SKShader Blurred(SKImage frame, SKRect area, float radius)
    {
        var margin = 3 * radius;
        var left = Math.Max(0, (int)Math.Floor(area.Left - margin));
        var top = Math.Max(0, (int)Math.Floor(area.Top - margin));
        var right = Math.Min(frame.Width, (int)Math.Ceiling(area.Right + margin));
        var bottom = Math.Min(frame.Height, (int)Math.Ceiling(area.Bottom + margin));
        using var region = frame.Subset(new SKRectI(left, top, right, bottom)) ?? throw new ZeyoCutException("could not read the part of the frame to redact");
        using var surface = SKSurface.Create(new SKImageInfo(right - left, bottom - top, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create a surface to blur a redaction");
        using (var blur = SKImageFilter.CreateBlur(radius, radius, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint())
        {
            paint.ImageFilter = blur;
            surface.Canvas.DrawImage(region, 0, 0, Exact, paint);
        }

        using var blurred = surface.Snapshot();
        return blurred.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear), SKMatrix.CreateTranslation(left, top));
    }
}
