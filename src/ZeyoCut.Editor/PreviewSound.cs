// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using ZeyoCut.Core;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor;

// Time while playing: it runs at the playback speed from where play started, and follows the
// sound while the sound is heard.
public sealed class PlaybackClock
{
    // How far the picture may drift from the sound before it is set back to it.
    public const double Drift = 0.08;

    private readonly Stopwatch _watch = new();
    private double _from;

    public double Speed { get; private set; } = 1;

    public bool Running => _watch.IsRunning;

    public double Now => _from + _watch.Elapsed.TotalSeconds * Speed;

    public void Start(double from, double speed)
    {
        _from = from;
        Speed = speed;
        _watch.Restart();
    }

    public void Stop() => _watch.Reset();

    // True when it was set back to the sound.
    public bool Follow(double sound)
    {
        if (!Running || Math.Abs(sound - Now) <= Drift)
        {
            return false;
        }

        Start(sound, Speed);
        return true;
    }
}

// The project's sound as an export mixes it (fades, ducking, loudness): PreviewAudio's mix in the
// project's cache\preview folder, made in the background a moment after each change to the sound
// and played with Windows' own media player. Use it from the window's thread.
public sealed class PreviewSound : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly Func<FfmpegPaths> _ffmpeg;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _wait;
    private readonly HashSet<string> _made = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _making;
    private (Project Project, string Directory, string Key)? _pending;
    private string? _key;
    private string? _open;
    private bool _ready;
    private bool _playing;

    public PreviewSound(Func<FfmpegPaths> ffmpeg, Dispatcher dispatcher)
    {
        _ffmpeg = ffmpeg;
        _dispatcher = dispatcher;
        _wait = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => Make(), dispatcher);
        _wait.Stop();
        _player.MediaOpened += (_, _) =>
        {
            _ready = true;
            State = "";
            Changed?.Invoke();
        };
        _player.MediaFailed += (_, e) =>
        {
            _ready = false;
            State = $"the sound can't be played: {e.ErrorException.Message}";
            Changed?.Invoke();
        };
    }

    // Raised when State changes, and when the sound becomes ready to play.
    public event Action? Changed;

    // "" when the sound is ready or there is none; otherwise what it's waiting for or why it can't play.
    public string State { get; private set; } = "";

    public bool Ready => _ready && _open is not null && _open == _key;

    public bool Playing => _playing;

    // The mix file open, while one is.
    public string? Mix => _open;

    // Where the sound is, while it plays.
    public double? Position => Ready && _playing ? _player.Position.TotalSeconds : null;

    // Takes this version of the project: the same mix plays on; a new one is made a moment after the
    // last change, and plays once it's ready.
    public void Update(Project project, string directory)
    {
        string? key;
        string? problem = null;
        try
        {
            key = PreviewAudio.Key(project, directory);
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
        {
            key = null;
            problem = $"no sound: {ex.Message}";
        }

        if (key == _key)
        {
            return;
        }

        _key = key;
        _making?.Cancel();
        _making = null;
        _wait.Stop();
        if (key is null)
        {
            Close();
            State = problem ?? "";
            Changed?.Invoke();
            return;
        }

        var path = PreviewAudio.PathOf(directory, key);
        if (File.Exists(path))
        {
            Open(path);
            return;
        }

        Close();
        State = "updating sound…";
        _pending = (project, directory, key);
        _wait.Start();
        Changed?.Invoke();
    }

    // Plays from seconds when the sound is ready; Changed says when it becomes ready.
    public void Play(double seconds, double speed)
    {
        if (!Ready)
        {
            return;
        }

        _playing = true;
        _player.SpeedRatio = speed;
        _player.Position = TimeSpan.FromSeconds(seconds);
        _player.Play();
    }

    public void Pause()
    {
        _playing = false;
        if (_open is not null)
        {
            _player.Pause();
        }
    }

    public void Dispose()
    {
        _wait.Stop();
        _making?.Cancel();
        var keep = _open;
        Close();
        DropMade(keep);
    }

    private void Make()
    {
        _wait.Stop();
        if (_pending is not { } pending || pending.Key != _key)
        {
            return;
        }

        _pending = null;
        var cancel = new CancellationTokenSource();
        _making = cancel;
        var path = PreviewAudio.PathOf(pending.Directory, pending.Key);
        var copy = ProjectJson.Copy(pending.Project);
        _ = Task.Run(() =>
        {
            string? error = null;
            try
            {
                PreviewAudio.Make(copy, pending.Directory, _ffmpeg(), path, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _dispatcher.InvokeAsync(() =>
            {
                if (cancel.IsCancellationRequested || pending.Key != _key)
                {
                    return;
                }

                _making = null;
                if (error is not null)
                {
                    State = $"the sound can't be mixed: {error}";
                    Changed?.Invoke();
                    return;
                }

                _made.Add(path);
                Open(path);
            });
        });
    }

    private void Open(string path)
    {
        Close();
        _open = path;
        _player.Open(new Uri(path));
        DropMade(keep: path);
    }

    private void Close()
    {
        if (_open is null)
        {
            return;
        }

        _player.Close();
        _open = null;
        _ready = false;
        _playing = false;
    }

    // Deletes the mixes this editor made that nothing plays any more. Mixes the preview page made
    // are its own to delete.
    private void DropMade(string? keep)
    {
        foreach (var path in _made.Where(p => !string.Equals(p, keep, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try
            {
                File.Delete(path);
                _made.Remove(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
