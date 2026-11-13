// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor;

// A rendered frame as the preview shows it: BGRA pixels (premultiplied) at the canvas size, or why it
// couldn't be drawn.
public sealed record PreviewFrame(int Frame, int Width, int Height, byte[]? Pixels, IReadOnlyList<string> Warnings, string? Error, TimeSpan Took, bool Cached);

// Draws the frame at the playhead for the preview on a worker thread, with the renderer exports use
// (footage over 2560 px from its proxy). Only the newest request is drawn: frames asked for while one
// draws are skipped. Drawn frames stay in the renderer's preview cache under their FrameKeys key, so
// going back to one shows it at once while nothing it shows has changed.
public sealed class FramePreview : IDisposable
{
    private readonly object _gate = new();
    private readonly FrameRenderer _renderer;
    private readonly Action<PreviewFrame> _done;
    private readonly Thread _thread;
    private (Project Project, int Frame)? _next;
    private bool _stopped;
    private Project? _keysOf;
    private FrameKeys? _keys;

    // done is called on the worker thread with each frame drawn.
    public FramePreview(string projectDirectory, Func<string> ffmpeg, Action<PreviewFrame> done)
    {
        _renderer = new FrameRenderer(projectDirectory, ffmpeg);
        _done = done;
        _thread = new Thread(Run) { IsBackground = true, Name = "preview" };
        _thread.Start();
    }

    // Asks for a frame of this version of the project, in place of any frame not started yet.
    public void Request(Project project, int frame)
    {
        lock (_gate)
        {
            _next = (project, frame);
            Monitor.Pulse(_gate);
        }
    }

    // Draws a frame now, on the calling thread.
    public PreviewFrame Render(Project project, int frame)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!ReferenceEquals(project, _keysOf))
            {
                _keys = new FrameKeys(project, _renderer.ProjectDirectory);
                _keysOf = project;
            }

            var key = $"editor|{_keys!.Of(frame)}|{string.Join(",", Preview.Proxied(_renderer, project, frame))}";
            if (_renderer.Previews.Get<PreviewFrame>(key) is { } cached)
            {
                return cached with { Frame = frame, Took = stopwatch.Elapsed, Cached = true };
            }

            using var image = _renderer.RenderImage(project, frame, new RenderOptions(Proxies: true));
            var pixels = Pixels(image);
            var drawn = new PreviewFrame(frame, image.Width, image.Height, pixels, _renderer.TakeWarnings(), null, stopwatch.Elapsed, false);
            return _renderer.Previews.Add(key, drawn, pixels.Length);
        }
        catch (Exception ex)
        {
            // Anything a frame can't be drawn for (a missing file, no FFmpeg, a damaged clip) is shown
            // in its place; the next frame is tried again.
            return new PreviewFrame(frame, 0, 0, null, _renderer.TakeWarnings(), ex.Message, stopwatch.Elapsed, false);
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
        _renderer.Dispose();
    }

    private void Run()
    {
        while (true)
        {
            Project project;
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

                (project, frame) = _next!.Value;
                _next = null;
            }

            _done(Render(project, frame));
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
