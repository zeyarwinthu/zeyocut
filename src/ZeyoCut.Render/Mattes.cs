// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// Drawing layers whose background is removed. The layer's picture is drawn into a layer of its own
// and its alpha matte cuts it out (DstIn), so color adjustments, masks, effects and the camera work
// on the cut-out picture as usual. Computed mattes are cached next to the asset (see MatteFiles).
public static class Mattes
{
    // Draws picture (pictureSource → destination) cut out by alpha (alphaSource → destination). The
    // paint's alpha is the layer's opacity and its color filter the layer's color adjustments.
    public static void Draw(SKCanvas canvas, SKImage picture, SKRect pictureSource, SKImage alpha, SKRect alphaSource, SKRect destination, SKPaint paint)
    {
        using var layerPaint = new SKPaint();
        layerPaint.Color = SKColors.White.WithAlpha(paint.Color.Alpha);
        canvas.SaveLayer(destination, layerPaint);
        using var picturePaint = new SKPaint();
        picturePaint.IsAntialias = true;
        picturePaint.ColorFilter = paint.ColorFilter;
        canvas.DrawImage(picture, pictureSource, destination, FrameRenderer.ImageSampling, picturePaint);
        using var cut = new SKPaint();
        cut.IsAntialias = true;
        cut.BlendMode = SKBlendMode.DstIn;
        canvas.DrawImage(alpha, alphaSource, destination, FrameRenderer.ImageSampling, cut);
        canvas.Restore();
    }

    // shrink (negative grows) and feather in the matte's pixels. The edges of the picture are
    // extended outward first, so a subject cut off by the frame keeps its edge at the frame.
    public static SKImage Refine(SKImage alpha, double shrink, double feather)
    {
        var radius = (int)Math.Round(Math.Abs(shrink), MidpointRounding.AwayFromZero);
        var sigma = (float)(feather / 2);
        var pad = radius + (int)Math.Ceiling(sigma * 3) + 2;
        var width = alpha.Width;
        var height = alpha.Height;

        using var padded = SKSurface.Create(new SKImageInfo(width + 2 * pad, height + 2 * pad, SKColorType.Alpha8, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create a matte surface");
        using (var shader = alpha.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(pad, pad)))
        using (var fill = new SKPaint())
        {
            fill.Shader = shader;
            padded.Canvas.DrawRect(new SKRect(0, 0, width + 2 * pad, height + 2 * pad), fill);
        }

        using var extended = padded.Snapshot();
        SKImageFilter? filter = null;
        if (radius > 0)
        {
            filter = shrink > 0 ? SKImageFilter.CreateErode(radius, radius) : SKImageFilter.CreateDilate(radius, radius);
        }

        if (sigma > 0)
        {
            var blurred = SKImageFilter.CreateBlur(sigma, sigma, filter);
            filter?.Dispose();
            filter = blurred;
        }

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create a matte surface");
        using (filter)
        using (var paint = new SKPaint())
        {
            paint.ImageFilter = filter;
            surface.Canvas.DrawImage(extended, -pad, -pad, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }

        return surface.Snapshot();
    }

    // Computes an edgeFill matte for one drawing and caches it.
    public static EdgeFillResult EdgeFill(string projectDirectory, Asset asset, SKImage picture, Matte matte, out string path)
    {
        var result = Render.EdgeFill.Compute(picture, matte.Color, matte.Tolerance ?? MatteEditor.DefaultEdgeTolerance);
        path = MatteFiles.CachePath(projectDirectory, asset, matte);
        Pixels.SaveGray(result.Alpha, result.Width, result.Height, path);
        return result;
    }

    // Share of a matte's pixels that are fully removed.
    public static double RemovedShare(SKImage alpha)
    {
        var bytes = Pixels.ReadAlpha(alpha);
        return bytes.Count(b => b == 0) / (double)Math.Max(1, bytes.Length);
    }

    // A frame of a video matte (decoded as gray RGBA) as an 8-bit matte.
    public static SKImage FromGrayFrame(SKImage frame)
    {
        using var pixmap = frame.PeekPixels();
        var rgba = pixmap is not null && pixmap.ColorType == SKColorType.Rgba8888 ? pixmap.GetPixelSpan().ToArray() : Pixels.Read(frame);
        var alpha = new byte[frame.Width * frame.Height];
        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = rgba[i * 4];
        }

        return Pixels.Alpha(alpha, frame.Width, frame.Height);
    }
}

// Mattes and keyed pictures of image drawings, kept for the renderer's lifetime.
public sealed class MatteCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<SKImage>> _alphas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<(SKImage Picture, SKImage Alpha)>> _keyed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<SKImage>> _refined = new(StringComparer.OrdinalIgnoreCase);

    // The drawing's picture and its refined matte, or null when the matte isn't computed yet.
    public (SKImage Picture, SKImage Alpha)? Image(string projectDirectory, Asset asset, SKImage picture, Matte matte)
    {
        var file = ProjectStore.AssetPath(projectDirectory, asset);
        var info = new FileInfo(file);
        var stamp = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        string key;
        SKImage alpha;
        switch (matte.Method)
        {
            case MatteMethod.Chroma:
                key = $"{stamp}|{matte.Color}|{matte.Tolerance:R}|{matte.Softness:R}|{matte.Spill:R}";
                var keyed = _keyed.GetOrAdd(key, _ => new Lazy<(SKImage, SKImage)>(() => ChromaKey.Apply(picture, matte))).Value;
                picture = keyed.Picture;
                alpha = keyed.Alpha;
                break;
            case MatteMethod.EdgeFill:
                key = MatteFiles.CachePath(projectDirectory, asset, matte);
                alpha = _alphas.GetOrAdd(key, path => new Lazy<SKImage>(() =>
                {
                    if (!File.Exists(path))
                    {
                        Mattes.EdgeFill(projectDirectory, asset, picture, matte, out _);
                    }

                    return Pixels.LoadGray(path);
                })).Value;
                break;
            default:
                key = MatteFiles.CachePath(projectDirectory, asset, matte);
                if (!File.Exists(key))
                {
                    return null;
                }

                alpha = _alphas.GetOrAdd(key, path => new Lazy<SKImage>(() => Pixels.LoadGray(path))).Value;
                break;
        }

        if (alpha.Width != picture.Width || alpha.Height != picture.Height)
        {
            throw new ZeyoCutException($"the matte of '{asset.Id}' is {alpha.Width}x{alpha.Height} but the image is {picture.Width}x{picture.Height}; run remove_background again");
        }

        if (matte.Shrink == 0 && matte.Feather == 0)
        {
            return (picture, alpha);
        }

        var refined = _refined.GetOrAdd($"{key}|{matte.Shrink:R}|{matte.Feather:R}", _ => new Lazy<SKImage>(() => Mattes.Refine(alpha, matte.Shrink, matte.Feather))).Value;
        return (picture, refined);
    }

    public void Dispose()
    {
        foreach (var image in _alphas.Values.Concat(_refined.Values).Where(i => i.IsValueCreated))
        {
            image.Value.Dispose();
        }

        foreach (var keyed in _keyed.Values.Where(i => i.IsValueCreated))
        {
            keyed.Value.Picture.Dispose();
            keyed.Value.Alpha.Dispose();
        }

        _alphas.Clear();
        _refined.Clear();
        _keyed.Clear();
    }
}

// Raw pixel access for keying and mattes: RGBA is 8-bit unpremultiplied, mattes are 8-bit alpha.
public static class Pixels
{
    public static byte[] Read(SKImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        Copy(image, info, pixels);
        return pixels;
    }

    public static byte[] ReadAlpha(SKImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        var pixels = new byte[info.BytesSize];
        Copy(image, info, pixels);
        return pixels;
    }

    public static SKImage Rgba(byte[] pixels, int width, int height) =>
        SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), pixels, width * 4)
            ?? throw new ZeyoCutException("could not create an image from pixels");

    public static SKImage Alpha(byte[] alpha, int width, int height) =>
        SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul), alpha, width)
            ?? throw new ZeyoCutException("could not create a matte image");

    // Writes a matte as an 8-bit gray PNG (through a temporary file, so a half-written matte is never read).
    public static void SaveGray(byte[] alpha, int width, int height, string path)
    {
        using var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque), alpha, width)
            ?? throw new ZeyoCutException("could not create a matte image");
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new ZeyoCutException("PNG encoding of the matte failed");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data.ToArray());
        File.Move(temp, path, overwrite: true);
    }

    public static SKImage LoadGray(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new ZeyoCutException($"could not read the matte {path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        var gray = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(gray, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new ZeyoCutException($"could not decode the matte {path}: {result}");
            }
        }
        finally
        {
            handle.Free();
        }

        return Alpha(gray, info.Width, info.Height);
    }

    private static void Copy(SKImage image, SKImageInfo info, byte[] pixels)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            if (!image.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
            {
                throw new ZeyoCutException("could not read the image's pixels");
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
