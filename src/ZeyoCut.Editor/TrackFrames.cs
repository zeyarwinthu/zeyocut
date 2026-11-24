// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;

namespace ZeyoCut.Editor;

// A frame for the Track & blur view to draw: of Project, which Plain made.
public sealed record TrackFrameRequest(Project Project, int Frame, double Scale);

// The frame drawn: BGRA pixels (premultiplied), or why it couldn't be drawn.
public sealed record TrackFramePicture(TrackFrameRequest Request, int Width, int Height, byte[]? Pixels, string? Error);

// The Track & blur view's frame without its redactions, so the boxes show what they follow: drawn
// by the renderer exports use, on a worker thread, only the newest asked for. The frames drawn are
// kept while the project and the size are the same.
public sealed class TrackFrames : IDisposable
{
    private const int Kept = 48;

    private readonly object _gate = new();
    private readonly FrameRenderer _renderer;
    private readonly Action<TrackFramePicture> _done;
    private readonly Thread _thread;
    private readonly Dictionary<int, TrackFramePicture> _drawn = [];
    private (Project Project, double Scale)? _drawnFor;
    private TrackFrameRequest? _next;
    private bool _stopped;

    // done is called on the worker thread with each frame drawn.
    public TrackFrames(string projectDirectory, Func<string> ffmpeg, Action<TrackFramePicture> done)
    {
        _renderer = new FrameRenderer(projectDirectory, ffmpeg);
        _done = done;
        _thread = new Thread(Run) { IsBackground = true, Name = "track frames" };
        _thread.Start();
    }

    // The project with its redactions turned off; the project itself when none hides anything.
    public static Project Plain(Project project)
    {
        if (!project.Layers.OfType<RedactionLayer>().Any(r => r.Visible))
        {
            return project;
        }

        var copy = ProjectJson.Clone(project);
        foreach (var redaction in copy.Layers.OfType<RedactionLayer>())
        {
            redaction.Visible = false;
        }

        return copy;
    }

    // Asks for a frame, in place of any not started yet.
    public void Request(TrackFrameRequest request)
    {
        lock (_gate)
        {
            _next = request;
            Monitor.Pulse(_gate);
        }
    }

    // Draws a frame now, on the calling thread.
    public TrackFramePicture Draw(TrackFrameRequest request)
    {
        if (_drawnFor is not { } drawnFor || !ReferenceEquals(drawnFor.Project, request.Project) || drawnFor.Scale != request.Scale)
        {
            _drawn.Clear();
            _drawnFor = (request.Project, request.Scale);
        }

        if (_drawn.TryGetValue(request.Frame, out var kept))
        {
            return kept with { Request = request };
        }

        TrackFramePicture picture;
        try
        {
            using var image = _renderer.RenderImage(request.Project, request.Frame, new RenderOptions(Proxies: true, Scale: request.Scale));
            picture = new TrackFramePicture(request, image.Width, image.Height, FramePreview.Pixels(image), null);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
        {
            // Anything the frame can't be drawn for (a missing file, a damaged clip) is said in its
            // place; the next frame is tried again.
            picture = new TrackFramePicture(request, 0, 0, null, ex.Message);
        }

        _renderer.TakeWarnings();
        foreach (var far in _drawn.Keys.OrderByDescending(f => Math.Abs(f - request.Frame)).Take(Math.Max(0, _drawn.Count + 1 - Kept)).ToList())
        {
            _drawn.Remove(far);
        }

        if (picture.Pixels is not null)
        {
            _drawn[request.Frame] = picture;
        }

        return picture;
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
            TrackFrameRequest request;
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

                request = _next!;
                _next = null;
            }

            TrackFramePicture picture;
            try
            {
                picture = Draw(request);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            _done(picture);
        }
    }
}
