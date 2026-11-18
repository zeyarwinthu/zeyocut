// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;

namespace ZeyoCut.Editor;

// What the onion skin draws: a layer with its children, at the frames around Frame.
public sealed record OnionRequest(Project Project, IReadOnlySet<string> Layers, int Frame, int Before, int After, double Scale);

// The onion skin's ghosts as one picture to lay over the frame: BGRA pixels, premultiplied.
public sealed record OnionPicture(OnionRequest Request, int Width, int Height, byte[] Pixels);

// The Animate view's onion skin: the layer drawn alone, by the renderer exports use, at the frames
// before and after the playhead (AnimateModel.Ghosts), faded the further they are and the ones after
// in green. Drawn on a worker thread, only the newest asked for; each frame's drawing is kept while
// the project, the layers and the size are the same.
public sealed class OnionSkin : IDisposable
{
    public static readonly (byte R, byte G, byte B) AfterTint = (0x2F, 0xBF, 0x71);

    private const int Kept = 64;

    private readonly object _gate = new();
    private readonly FrameRenderer _renderer;
    private readonly Action<OnionPicture> _done;
    private readonly Thread _thread;
    private readonly Dictionary<int, byte[]> _drawn = [];
    private OnionRequest? _next;
    private (Project Project, string Layers, double Scale)? _drawnFor;
    private bool _stopped;

    // done is called on the worker thread with each picture drawn.
    public OnionSkin(string projectDirectory, Func<string> ffmpeg, Action<OnionPicture> done)
    {
        _renderer = new FrameRenderer(projectDirectory, ffmpeg);
        _done = done;
        _thread = new Thread(Run) { IsBackground = true, Name = "onion skin" };
        _thread.Start();
    }

    // Asks for a picture, in place of any not started yet.
    public void Request(OnionRequest request)
    {
        lock (_gate)
        {
            _next = request;
            Monitor.Pulse(_gate);
        }
    }

    // Draws a picture now, on the calling thread.
    public OnionPicture Draw(OnionRequest request)
    {
        var layers = string.Join("|", request.Layers.Order(StringComparer.Ordinal));
        if (_drawnFor is not { } drawnFor || !ReferenceEquals(drawnFor.Project, request.Project) || drawnFor.Layers != layers || drawnFor.Scale != request.Scale)
        {
            _drawn.Clear();
            _drawnFor = (request.Project, layers, request.Scale);
        }

        var (width, height) = FrameRenderer.ScaledSize(request.Project, request.Scale);
        var pixels = new byte[width * height * 4];
        foreach (var ghost in AnimateModel.Ghosts(request.Frame, FrameMath.FrameCount(request.Project), request.Before, request.After))
        {
            Over(pixels, Ghost(request, ghost.Frame), ghost.Opacity, ghost.After ? AfterTint : null);
        }

        foreach (var far in _drawn.Keys.OrderByDescending(f => Math.Abs(f - request.Frame)).Take(Math.Max(0, _drawn.Count - Kept)).ToList())
        {
            _drawn.Remove(far);
        }

        return new OnionPicture(request, width, height, pixels);
    }

    // Lays premultiplied BGRA pixels over others at an opacity, all in one color when tinted.
    public static void Over(byte[] into, byte[] ghost, double opacity, (byte R, byte G, byte B)? tint)
    {
        var strength = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        for (var i = 0; i + 3 < ghost.Length && i + 3 < into.Length; i += 4)
        {
            int alpha = ghost[i + 3];
            if (alpha == 0)
            {
                continue;
            }

            var a = alpha * strength / 255;
            var (b, g, r) = tint is { } color
                ? (color.B * a / 255, color.G * a / 255, color.R * a / 255)
                : (ghost[i] * strength / 255, ghost[i + 1] * strength / 255, ghost[i + 2] * strength / 255);
            var keep = 255 - a;
            into[i] = (byte)(b + into[i] * keep / 255);
            into[i + 1] = (byte)(g + into[i + 1] * keep / 255);
            into[i + 2] = (byte)(r + into[i + 2] * keep / 255);
            into[i + 3] = (byte)(a + into[i + 3] * keep / 255);
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

    private byte[] Ghost(OnionRequest request, int frame)
    {
        if (!_drawn.TryGetValue(frame, out var pixels))
        {
            using var image = _renderer.RenderImage(request.Project, frame, new RenderOptions(Transparent: true, Only: request.Layers, Proxies: true, Scale: request.Scale));
            pixels = FramePreview.Pixels(image);
            _renderer.TakeWarnings();
            _drawn[frame] = pixels;
        }

        return pixels;
    }

    private void Run()
    {
        while (true)
        {
            OnionRequest request;
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

            OnionPicture picture;
            try
            {
                picture = Draw(request);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
            {
                // A ghost that can't be drawn (a missing file, a damaged clip) leaves the skin empty;
                // the preview says why.
                var (width, height) = FrameRenderer.ScaledSize(request.Project, request.Scale);
                picture = new OnionPicture(request, width, height, new byte[width * height * 4]);
            }

            _done(picture);
        }
    }
}
