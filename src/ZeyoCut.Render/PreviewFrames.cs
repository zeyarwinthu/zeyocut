// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render;

// A project to show preview frames of, at Width x Height. Variant tells apart frames of the same size
// drawn differently. Playback shows every Step-th frame, drawn up to AheadSeconds ahead.
public sealed record PreviewSource(Project Project, string Directory, FrameKeys Keys, int Width, int Height, int Step = 1, string Variant = "", double AheadSeconds = PreviewSource.DefaultAhead)
{
    public const double DefaultAhead = 5;

    // FrameKeys isn't safe to use from two threads at once.
    public string Key(int frame)
    {
        string content;
        lock (Keys)
        {
            content = Keys.Of(frame);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{content}|{Width}x{Height}{Variant}")));
    }
}

// Preview frames of the open project for the preview page and the editor. Frames are drawn one at a
// time, each clip with one decoder that reads forward, and kept in memory by their FrameKeys key, so
// an edit only redraws the frames it changes. While playing, a background thread draws the frames up
// to the source's AheadSeconds past the one asked for, Step apart. draw turns the renderer's frame into
// what is kept, with its size in bytes.
public sealed class PreviewFrames<T> : IDisposable where T : class
{
    private readonly object _renderGate = new();
    private readonly Func<string> _ffmpeg;
    private readonly Func<FrameRenderer, PreviewSource, int, (T Frame, long Bytes)> _draw;
    private readonly PreviewCache _frames;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private FrameRenderer? _renderer;
    private int _lastFrame = -1;
    private bool _disposed;
    private volatile Ahead? _ahead;
    private double _drawMilliseconds;

    public PreviewFrames(Func<string> ffmpeg, long budget, Func<FrameRenderer, PreviewSource, int, (T Frame, long Bytes)> draw)
    {
        _ffmpeg = ffmpeg;
        _draw = draw;
        _frames = new PreviewCache(budget);
        new Thread(ReadAhead) { IsBackground = true, Name = "preview read-ahead" }.Start();
    }

    // How long drawing a frame took lately, on average; 0 before the first.
    public TimeSpan DrawTime => TimeSpan.FromMilliseconds(Volatile.Read(ref _drawMilliseconds));

    // Why the frames ahead stopped being drawn, when one couldn't be; cleared by Play.
    public Exception? AheadError { get; private set; }

    public bool Playing => _ahead is not null;

    // The frame, from memory or drawn now.
    public T Frame(PreviewSource source, int frame, CancellationToken stop = default)
    {
        lock (_renderGate)
        {
            stop.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = source.Key(frame);
            if (_frames.Get<T>(key) is { } cached)
            {
                return cached;
            }

            if (_renderer is null || !string.Equals(_renderer.ProjectDirectory, Path.GetFullPath(source.Directory), StringComparison.OrdinalIgnoreCase))
            {
                _renderer?.Dispose();
                _renderer = new FrameRenderer(source.Directory, _ffmpeg, streamVideo: true);
                _lastFrame = -1;
            }

            // Each clip keeps one decoder going forward; a jump of more than a second ahead starts
            // new ones there instead of decoding every frame in between.
            if (_lastFrame >= 0 && frame > _lastFrame + source.Project.Fps)
            {
                _renderer.Video.CloseReaders();
            }

            _lastFrame = frame;
            var started = Stopwatch.GetTimestamp();
            var (drawn, bytes) = _draw(_renderer, source, frame);
            var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var average = Volatile.Read(ref _drawMilliseconds);
            Volatile.Write(ref _drawMilliseconds, average == 0 ? took : average * 0.8 + took * 0.2);
            return _frames.Add(key, drawn, bytes);
        }
    }

    // The frame when it is in memory, without drawing it.
    public T? Cached(PreviewSource source, int frame) => _frames.Get<T>(source.Key(frame));

    // Draws the frames after this one in the background, until Pause or another Play.
    public void Play(PreviewSource source, int from)
    {
        AheadError = null;
        _ahead = new Ahead(source, from);
        _wake.Set();
    }

    public void Pause() => _ahead = null;

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
        lock (_renderGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderer?.Dispose();
            _renderer = null;
            _lastFrame = -1;
            _ahead = null;
            _frames.Dispose();
        }
    }

    // Draws the frames up to AheadSeconds past the one asked for last, one step apart.
    private void ReadAhead()
    {
        var stop = _stop.Token;
        PreviewSource? source = null;
        var next = 0;
        while (!stop.IsCancellationRequested)
        {
            var ahead = _ahead;
            if (ahead is null)
            {
                WaitHandle.WaitAny([_wake, stop.WaitHandle]);
                continue;
            }

            var step = ahead.Source.Step;
            var last = Math.Min(FrameMath.FrameCount(ahead.Source.Project) - 1, ahead.From + (int)(ahead.Source.AheadSeconds * ahead.Source.Project.Fps));
            if (!ReferenceEquals(source, ahead.Source) || next <= ahead.From || next > last + step)
            {
                source = ahead.Source;
                next = (ahead.From / step + 1) * step;
            }

            if (next > last)
            {
                WaitHandle.WaitAny([_wake, stop.WaitHandle]);
                continue;
            }

            try
            {
                Frame(source, next, stop);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                AheadError = ex;
                _ahead = null;
            }

            next += step;
        }
    }

    private sealed record Ahead(PreviewSource Source, int From);
}
