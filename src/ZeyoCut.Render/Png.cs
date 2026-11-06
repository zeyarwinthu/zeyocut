// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;

namespace ZeyoCut.Render;

public static class Png
{
    public static byte[] Encode(SKImage image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new ZeyoCutException("PNG encoding failed");
        return data.ToArray();
    }

    public static void Save(SKImage image, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encode(image));
    }

    public static SKImage Scale(SKImage source, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException($"could not create a {width}x{height} drawing surface");
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint();
        paint.IsAntialias = true;
        surface.Canvas.DrawImage(source, new SKRect(0, 0, width, height), FrameRenderer.ImageSampling, paint);
        return surface.Snapshot();
    }

    // Scales source to cover width x height and crops what sticks out evenly from both sides.
    public static SKImage Cover(SKImage source, int width, int height)
    {
        var scale = Math.Max(width / (double)source.Width, height / (double)source.Height);
        var cropWidth = width / scale;
        var cropHeight = height / scale;
        var left = (source.Width - cropWidth) / 2;
        var top = (source.Height - cropHeight) / 2;
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException($"could not create a {width}x{height} drawing surface");
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint();
        paint.IsAntialias = true;
        var crop = new SKRect((float)left, (float)top, (float)(left + cropWidth), (float)(top + cropHeight));
        surface.Canvas.DrawImage(source, crop, new SKRect(0, 0, width, height), FrameRenderer.ImageSampling, paint);
        return surface.Snapshot();
    }

    public static (int Width, int Height) FitWidth(int width, int height, int maxWidth)
    {
        if (width <= maxWidth)
        {
            return (width, height);
        }

        return (maxWidth, Math.Max(1, (int)Math.Round(height * (double)maxWidth / width)));
    }
}
