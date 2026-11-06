// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;

namespace ZeyoCut.Render.Export;

// Fits a canvas of another shape whole into the output frame, over an enlarged, blurred copy of
// itself that covers the rest (export_batch with a preset whose aspect ratio differs from the canvas).
public sealed class BlurredFit : IDisposable
{
    // The background is blurred at 1/8 size and enlarged, which is much cheaper and just as smooth.
    private const int Shrink = 8;

    // Blur sigma as a fraction of the output's long side.
    public const double BlurFraction = 0.02;

    private readonly SKSurface _output;
    private readonly SKSurface _background;
    private readonly SKImageFilter _blur;
    private readonly SKRect _fit;
    private readonly SKRect _cover;
    private readonly SKRect _full;

    public BlurredFit(int sourceWidth, int sourceHeight, int width, int height)
    {
        _output = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException($"could not create a {width}x{height} drawing surface");
        var smallWidth = Math.Max(1, (int)Math.Ceiling(width / (double)Shrink));
        var smallHeight = Math.Max(1, (int)Math.Ceiling(height / (double)Shrink));
        _background = SKSurface.Create(new SKImageInfo(smallWidth, smallHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException($"could not create a {smallWidth}x{smallHeight} drawing surface");
        var sigma = (float)(Math.Max(width, height) * BlurFraction / Shrink);
        _blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        _fit = SKRectI.Round(Centered(sourceWidth, sourceHeight, width, height, Math.Min(width / (double)sourceWidth, height / (double)sourceHeight)));
        _cover = Centered(sourceWidth, sourceHeight, smallWidth, smallHeight, Math.Max(smallWidth / (double)sourceWidth, smallHeight / (double)sourceHeight));
        _full = new SKRect(0, 0, width, height);
    }

    // Where the whole canvas lands in the output frame.
    public SKRect Fit => _fit;

    public SKSurface Draw(SKImage frame)
    {
        var small = _background.Canvas;
        small.Clear(SKColors.Black);
        using (var blurPaint = new SKPaint())
        {
            blurPaint.ImageFilter = _blur;
            small.DrawImage(frame, _cover, FrameRenderer.ImageSampling, blurPaint);
        }

        using var blurred = _background.Snapshot();
        var canvas = _output.Canvas;
        canvas.Clear(SKColors.Black);
        canvas.DrawImage(blurred, _full, FrameRenderer.ImageSampling, null);
        using var paint = new SKPaint();
        paint.IsAntialias = true;
        canvas.DrawImage(frame, _fit, FrameRenderer.ImageSampling, paint);
        return _output;
    }

    public void Dispose()
    {
        _blur.Dispose();
        _background.Dispose();
        _output.Dispose();
    }

    private static SKRect Centered(int sourceWidth, int sourceHeight, int width, int height, double scale)
    {
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        var left = (width - w) / 2;
        var top = (height - h) / 2;
        return new SKRect((float)left, (float)top, (float)(left + w), (float)(top + h));
    }
}
