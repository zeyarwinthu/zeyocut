// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor;

// A rendered frame as the preview shows it: BGRA pixels (premultiplied), or why it couldn't be drawn.
public sealed record PreviewFrame(int Frame, int Width, int Height, byte[]? Pixels, IReadOnlyList<string> Warnings, string? Error, TimeSpan Took, bool Cached);

// The preview's frames, through the preview page's PreviewFrames with the renderer exports use
// (footage over 2560 px from its proxy). A paused frame is drawn at the canvas size on a worker
// thread, only the newest asked for; while playing, the frames ahead are drawn at the playback size.
// Frames stay in memory under their FrameKeys key, so going back to one shows it at once while
// nothing it shows has changed.
public sealed class FramePreview : IDisposable
{
    // Memory for the frames kept; the frames drawn ahead while playing take at most half of it.
    public const long Budget = 512L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly PreviewFrames<PreviewFrame> _frames;
    private readonly Action<PreviewFrame> _done;
    private readonly Thread _thread;
    private (PreviewSource Source, int Frame)? _next;
    private bool _stopped;
    private Project? _keysOf;
    private FrameKeys? _keys;
    private int _proxies;

    // done is called on the worker thread with each frame drawn.
    public FramePreview(string projectDirectory, Func<string> ffmpeg, Action<PreviewFrame> done)
    {
        _directory = projectDirectory;
        _frames = new PreviewFrames<PreviewFrame>(ffmpeg, Budget, Draw);
        _done = done;
        _thread = new Thread(Run) { IsBackground = true, Name = "preview" };
        _thread.Start();
    }

    // How long drawing a frame took lately.
    public TimeSpan DrawTime => _frames.DrawTime;

    // Why the frames ahead stopped being drawn, if one couldn't be.
    public Exception? AheadError => _frames.AheadError;

    // This version of the project at scale (1: the canvas size), showing every step-th frame while
    // playing. Call it from one thread.
    public PreviewSource Source(Project project, double scale, int step = 1)
    {
        if (!ReferenceEquals(project, _keysOf))
        {
            _keys = new FrameKeys(project, _directory);
            _keysOf = project;
        }

        var (width, height) = FrameRenderer.ScaledSize(project, scale);
        var perSecond = Math.Max(1, project.Fps / (double)step);
        var ahead = Math.Clamp(Budget / 2.0 / (width * height * 4L) / perSecond, 1, PreviewSource.DefaultAhead);
        return new PreviewSource(project, _directory, _keys!, width, height, step, $"|editor|{_proxies}", ahead);
    }

    // Frames drawn before a proxy was made read the original footage; after it, they're drawn again.
    public void ProxiesChanged() => _proxies++;

    // Asks for a frame, in place of any frame not started yet.
    public void Request(PreviewSource source, int frame)
    {
        lock (_gate)
        {
            _next = (source, frame);
            Monitor.Pulse(_gate);
        }
    }

    // The frame when it is in memory, without drawing it.
    public PreviewFrame? Cached(PreviewSource source, int frame) =>
        _frames.Cached(source, frame) is { } cached ? cached with { Frame = frame, Cached = true } : null;

    // Draws the frames after this one in the background, until Pause or another Play.
    public void Play(PreviewSource source, int frame) => _frames.Play(source, frame);

    public void Pause() => _frames.Pause();

    // Draws a frame now, on the calling thread.
    public PreviewFrame Render(PreviewSource source, int frame)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (Cached(source, frame) is { } cached)
            {
                return cached with { Took = stopwatch.Elapsed };
            }

            return _frames.Frame(source, frame) with { Frame = frame };
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
        {
            // Anything a frame can't be drawn for (a missing file, no FFmpeg, a damaged clip) is shown
            // in its place; the next frame is tried again.
            return new PreviewFrame(frame, 0, 0, null, [], ex.Message, stopwatch.Elapsed, false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            Monitor.Pulse(_gate);
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _frames.Dispose();
    }

    private static (PreviewFrame Frame, long Bytes) Draw(FrameRenderer renderer, PreviewSource source, int frame)
    {
        var started = Stopwatch.GetTimestamp();
        var scale = source.Width >= source.Project.Width ? 1 : source.Width / (double)source.Project.Width;
        using var image = renderer.RenderImage(source.Project, frame, new RenderOptions(Proxies: true, Scale: scale));
        var pixels = Pixels(image);
        return (new PreviewFrame(frame, image.Width, image.Height, pixels, renderer.TakeWarnings(), null, Stopwatch.GetElapsedTime(started), false), pixels.Length);
    }

    private void Run()
    {
        while (true)
        {
            PreviewSource source;
            int frame;
            lock (_gate)
            {
                while (_next is null && !_stopped)
                {
                    Monitor.Wait(_gate);
                }

                if (_stopped)
                {
                    return;
                }

                (source, frame) = _next!.Value;
                _next = null;
            }

            PreviewFrame drawn;
            try
            {
                drawn = Render(source, frame);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            _done(drawn);
        }
    }

    private static byte[] Pixels(SKImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            image.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }
}
