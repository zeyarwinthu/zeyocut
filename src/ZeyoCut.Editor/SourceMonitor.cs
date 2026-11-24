// SPDX-License-Identifier: MIT

using System.Windows.Media;
using System.Windows.Threading;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor;

// The source viewer's player: the file alone (SourceViewer.Of), its frames drawn by a preview worker
// of its own and its sound mixed and played by a PreviewSound of its own, so the timeline's preview
// and sound are left as they are. A sound has no frames to draw. Frame is the source playhead;
// Changed is raised when it moves, when a frame is drawn, when the sound's state changes and when
// playing starts or stops.
public sealed class SourceMonitor : IDisposable
{
    private readonly FramePreview? _preview;
    private readonly PreviewSound _sound;
    private readonly PlaybackClock _clock = new();
    private readonly double _scale;
    private PreviewSource? _playSource;
    private int _playFrom;
    private DateTime _prerollUntil;
    private bool _disposed;

    // playbackScale is the size frames are drawn at while playing, as the timeline's preview does.
    public SourceMonitor(Asset asset, Project source, string directory, Func<FfmpegPaths> ffmpeg, Dispatcher dispatcher, double playbackScale)
    {
        Asset = asset;
        Source = source;
        Frames = Math.Max(1, FrameMath.FrameCount(source));
        _scale = playbackScale;
        if (asset.Type == AssetType.Video)
        {
            _preview = new FramePreview(directory, () => ffmpeg().Ffmpeg, frame => dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && !Playing)
                {
                    Picture = frame;
                    Changed?.Invoke();
                }
            }));
        }

        _sound = new PreviewSound(ffmpeg, dispatcher);
        _sound.Changed += OnSound;
        _sound.Update(source, directory);
        Seek(0);
    }

    public event Action? Changed;

    public Asset Asset { get; }

    public Project Source { get; }

    public int Frames { get; }

    public int Frame { get; private set; }

    public bool Playing { get; private set; }

    // The newest frame drawn, at the canvas size while paused and the playback size while playing.
    public PreviewFrame? Picture { get; private set; }

    // "" when the sound is ready or there is none; else what it's waiting for, or why it can't play.
    public string SoundState => _sound.State;

    // Why playing stopped, when a frame ahead couldn't be drawn.
    public string? Error { get; private set; }

    public double Time => FrameMath.TimeOf(Source, Frame);

    public void Seek(int frame)
    {
        Pause();
        Frame = Math.Clamp(frame, 0, Frames - 1);
        _preview?.Request(_preview.Source(Source, 1), Frame);
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (Playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    // Plays from the playhead (from the start when it's at the end), once the first frame ahead is
    // drawn or after a moment, with the sound.
    public void Play()
    {
        if (Playing || _disposed)
        {
            return;
        }

        if (Frame >= Frames - 1)
        {
            Frame = 0;
        }

        Playing = true;
        Error = null;
        if (_preview is not null)
        {
            _playSource = _preview.Source(Source, _scale);
            _preview.Play(_playSource, Frame);
        }

        _playFrom = Frame;
        _clock.Stop();
        _prerollUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
        CompositionTarget.Rendering += OnTick;
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (!Playing)
        {
            return;
        }

        Playing = false;
        CompositionTarget.Rendering -= OnTick;
        _clock.Stop();
        _sound.Pause();
        _preview?.Pause();
        _preview?.Request(_preview.Source(Source, 1), Frame);
        Changed?.Invoke();
    }

    // Each screen refresh while playing: the frame where the clock (following the sound) is, when it
    // has been drawn; playing stops at the end.
    private void OnTick(object? sender, EventArgs e)
    {
        if (!Playing)
        {
            return;
        }

        if (_preview?.AheadError is { } error)
        {
            Error = error.Message;
            Pause();
            return;
        }

        if (!_clock.Running)
        {
            var first = Math.Min(Frames - 1, _playFrom + 1);
            if (_preview is not null && _preview.Cached(_playSource!, first) is null && DateTime.UtcNow < _prerollUntil)
            {
                return;
            }

            var from = FrameMath.TimeOf(Source, _playFrom);
            _clock.Start(from, 1);
            _sound.Play(from, 1);
        }

        if (_sound.Position is { } heard)
        {
            _clock.Follow(heard);
        }

        var frame = (int)Math.Floor(_clock.Now * Source.Fps + 1e-6);
        if (frame >= Frames)
        {
            Frame = Frames - 1;
            Pause();
            return;
        }

        if (frame > Frame)
        {
            Frame = frame;
            if (_preview is not null)
            {
                _preview.Play(_playSource!, frame);
                if (_preview.Cached(_playSource!, frame) is { Pixels: not null } cached)
                {
                    Picture = cached;
                }
            }

            Changed?.Invoke();
        }
    }

    private void OnSound()
    {
        if (Playing && _clock.Running && _sound.Ready && !_sound.Playing)
        {
            _sound.Play(_clock.Now, 1);
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Pause();
        _disposed = true;
        _sound.Changed -= OnSound;
        _sound.Dispose();
        if (_preview is { } preview)
        {
            // On a worker thread, as the timeline's preview is: a frame being drawn can take a while.
            _ = Task.Run(preview.Dispose);
        }
    }
}
