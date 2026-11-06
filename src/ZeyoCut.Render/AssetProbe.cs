// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

public sealed class AssetProbe : IAssetProbe
{
    private readonly Func<string, double>? _audioDuration;
    private readonly Func<string, AssetInfo>? _video;

    public AssetProbe(Func<string, double>? audioDuration = null, Func<string, AssetInfo>? video = null)
    {
        _audioDuration = audioDuration;
        _video = video;
    }

    public AssetInfo Probe(string path, AssetType type)
    {
        switch (type)
        {
            case AssetType.Image:
                {
                    using var codec = SKCodec.Create(path)
                        ?? throw new ZeyoCutException($"not a readable image file: {path}");
                    return new AssetInfo(Width: codec.Info.Width, Height: codec.Info.Height);
                }
            case AssetType.Font:
                {
                    var typeface = SKTypeface.FromFile(path)
                        ?? throw new ZeyoCutException($"not a readable font file: {path}");
                    return new AssetInfo(FontFamily: typeface.FamilyName);
                }
            case AssetType.Video:
                {
                    return _video?.Invoke(path)
                        ?? throw new ZeyoCutException("importing video needs ffprobe; install FFmpeg or set FFMPEG_PATH");
                }
            default:
                {
                    if (_audioDuration is null)
                    {
                        throw new ZeyoCutException("importing audio needs ffprobe; install FFmpeg or set FFMPEG_PATH");
                    }

                    return new AssetInfo(Duration: _audioDuration(path));
                }
        }
    }
}
