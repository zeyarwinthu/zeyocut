// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Render;

namespace ZeyoCut.Tests.TestSupport;

public static class TestImages
{
    public static string Solid(string path, int width, int height, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        Png.Save(image, path);
        return path;
    }

    public static string SplitLeftRight(string path, int width, int height, SKColor left, SKColor right)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(right);
        using var paint = new SKPaint();
        paint.Color = left;
        surface.Canvas.DrawRect(new SKRect(0, 0, width / 2f, height), paint);
        using var image = surface.Snapshot();
        Png.Save(image, path);
        return path;
    }

    public static SKColor Pixel(SKImage image, int x, int y)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap.GetPixel(x, y);
    }

    public static byte[] Pixels(SKImage image)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap.GetPixelSpan().ToArray();
    }

    public static int CountDarkPixels(SKImage image, byte threshold = 100)
    {
        var pixels = Pixels(image);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < threshold && pixels[i + 1] < threshold && pixels[i + 2] < threshold)
            {
                count++;
            }
        }

        return count;
    }
}
