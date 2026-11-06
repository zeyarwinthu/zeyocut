// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using SkiaSharp;
using ZeyoCut.Core;

namespace ZeyoCut.Render;

public sealed class AssetCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<SKImage>> _images = new(StringComparer.OrdinalIgnoreCase);

    public SKImage GetImage(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ZeyoCutException($"image file is missing: {path}");
        }

        var key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        return _images.GetOrAdd(key, _ => new Lazy<SKImage>(() => Decode(info.FullName))).Value;
    }

    public void Dispose()
    {
        foreach (var image in _images.Values)
        {
            if (image.IsValueCreated)
            {
                image.Value.Dispose();
            }
        }

        _images.Clear();
    }

    private static SKImage Decode(string path)
    {
        using var bitmap = SKBitmap.Decode(path)
            ?? throw new ZeyoCutException($"could not decode image: {path}");
        return SKImage.FromPixelCopy(bitmap.PeekPixels())
            ?? throw new ZeyoCutException($"could not load image: {path}");
    }
}
