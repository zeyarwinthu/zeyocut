// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Video;

// Hands out decoded video frames. In preview mode every miss is a single-frame seek. In streaming mode
// (export) each reader key — one per video layer — keeps one ffmpeg process that only reads forward:
// frames are skipped for speeds above 1 and the last frame is repeated for speeds below 1.
public sealed class VideoFrameSource : IDisposable
{
    private const long PreviewCacheBytes = 256L * 1024 * 1024;
    private const long StreamCacheBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Func<string>? _ffmpeg;
    private readonly bool _stream;
    private readonly long _budget;
    private readonly Dictionary<string, VideoFrameReader> _readers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<CachedFrame>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedFrame> _lru = new();
    private long _bytes;

    public VideoFrameSource(Func<string>? ffmpeg, bool stream)
    {
        _ffmpeg = ffmpeg;
        _stream = stream;
        _budget = stream ? StreamCacheBytes : PreviewCacheBytes;
    }

    public int DecoderStarts { get; private set; }

    public SKImage GetFrame(string readerKey, string file, Asset asset, int frame, int width, int height)
    {
        lock (_gate)
        {
            var key = $"{file}|{width}x{height}|{frame}";
            if (_index.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }

            if (!System.IO.File.Exists(file))
            {
                throw new ZeyoCutException($"video file is missing: {file}");
            }

            var image = _stream ? Stream(readerKey, file, asset, frame, width, height) : Seek(file, asset, frame, width, height);
            Add(key, image, (long)width * height * 4);
            return image;
        }
    }

    // Stops every reader, so each clip's next frame starts a new one there instead of decoding the frames
    // in between: for the exporter, moving on to a chunk further on.
    public void CloseReaders()
    {
        lock (_gate)
        {
            foreach (var reader in _readers.Values)
            {
                reader.Dispose();
            }

            _readers.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var reader in _readers.Values)
            {
                reader.Dispose();
            }

            _readers.Clear();
            foreach (var cached in _lru)
            {
                cached.Image.Dispose();
            }

            _lru.Clear();
            _index.Clear();
            _bytes = 0;
        }
    }

    private SKImage Seek(string file, Asset asset, int frame, int width, int height)
    {
        // The frame count from the container can be one or two more than the decodable frames,
        // so near the end fall back to the last frame that exists.
        for (var attempt = frame; attempt >= Math.Max(0, frame - 3); attempt--)
        {
            using var reader = Start(file, asset, attempt, width, height, 1);
            if (reader.Advance())
            {
                return ToImage(reader);
            }

            reader.ThrowIfFailed();
        }

        throw new ZeyoCutException($"could not decode frame {frame} of '{Path.GetFileName(file)}'");
    }

    private SKImage Stream(string readerKey, string file, Asset asset, int frame, int width, int height)
    {
        _readers.TryGetValue(readerKey, out var reader);
        var usable = reader is not null
            && reader.File == file && reader.Width == width && reader.Height == height
            && (reader.CurrentFrame >= 0 ? frame >= reader.CurrentFrame : frame >= reader.NextFrame);
        if (!usable)
        {
            reader?.Dispose();
            reader = Start(file, asset, frame, width, height, null);
            _readers[readerKey] = reader;
        }

        while (reader!.CurrentFrame < frame && reader.Advance())
        {
        }

        if (reader.CurrentFrame < 0)
        {
            reader.ThrowIfFailed();
            return Seek(file, asset, frame, width, height);
        }

        return ToImage(reader);
    }

    private VideoFrameReader Start(string file, Asset asset, int frame, int width, int height, int? maxFrames)
    {
        var ffmpeg = _ffmpeg?.Invoke()
            ?? throw new ZeyoCutException("drawing video layers needs FFmpeg; install it or set FFMPEG_PATH");
        DecoderStarts++;
        return new VideoFrameReader(ffmpeg, file, frame, VideoDecoder.SeekSeconds(asset, frame), width, height, maxFrames);
    }

    private static SKImage ToImage(VideoFrameReader reader)
    {
        var info = new SKImageInfo(reader.Width, reader.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return SKImage.FromPixelCopy(info, reader.Current, reader.Width * 4)
            ?? throw new ZeyoCutException("could not create an image from a decoded video frame");
    }

    private void Add(string key, SKImage image, long bytes)
    {
        _index[key] = _lru.AddFirst(new CachedFrame(key, image, bytes));
        _bytes += bytes;
        while (_bytes > _budget && _lru.Count > 2)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
            last.Value.Image.Dispose();
        }
    }

    private sealed record CachedFrame(string Key, SKImage Image, long Bytes);
}
