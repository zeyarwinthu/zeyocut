// SPDX-License-Identifier: MIT

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Editor;

// How loud a sound is along its length: the loudest sample of each 1/PerSecond of a second, from 0
// (silence) to 255 (full scale), for the waveforms on the media panel's tiles and the timeline's clips.
public sealed record AudioPeaks(byte[] Values)
{
    public const int PerSecond = 200;

    public double Duration => Values.Length / (double)PerSecond;

    // The loudest from one time in the sound to another, from 0 to 1; null when the sound has
    // nothing there.
    public double? Max(double from, double to)
    {
        if (from >= Duration || to <= 0 || to <= from)
        {
            return null;
        }

        var first = Math.Max(0, (int)(from * PerSecond));
        var last = Math.Min(Values.Length, Math.Max(first + 1, (int)Math.Ceiling(to * PerSecond)));
        var max = 0;
        for (var i = first; i < last; i++)
        {
            max = Math.Max(max, Values[i]);
        }

        return max / 255.0;
    }

    // The sound from 'from' to 'to' across 'area' in 'columns' steps, mirrored about its middle and
    // as tall as the area at full scale; flat from 'end' on.
    public Geometry Outline(double from, double to, Rect area, int columns, double end = double.PositiveInfinity)
    {
        if (columns <= 0 || to <= from)
        {
            return Geometry.Empty;
        }

        var middle = area.Y + area.Height / 2;
        var top = new List<Point>(columns * 2);
        for (var i = 0; i < columns; i++)
        {
            var start = from + (to - from) * i / columns;
            var stop = from + (to - from) * (i + 1) / columns;
            var level = start >= end ? 0 : Max(start, Math.Min(stop, end)) ?? 0;
            var y = middle - level * area.Height / 2;
            top.Add(new Point(area.X + area.Width * i / columns, y));
            top.Add(new Point(area.X + area.Width * (i + 1) / columns, y));
        }

        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            context.BeginFigure(top[0], true, true);
            context.PolyLineTo(top.Skip(1).ToList(), false, false);
            context.PolyLineTo(Enumerable.Reverse(top).Select(p => new Point(p.X, 2 * middle - p.Y)).ToList(), false, false);
        }

        outline.Freeze();
        return outline;
    }
}

// Each sound's peaks, made once on a worker thread from the whole file and kept in memory and in the
// project's cache folder (cache\waveforms), named like the thumbnails by the file, its size and when
// it was last changed, so a file replaced under the same name gets new ones. Pictures, footage and
// fonts have none.
public sealed class MediaWaveforms : IDisposable
{
    public const string Folder = "waveforms";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<string> _ffmpeg;
    private readonly Action<string> _ready;
    private readonly CancellationTokenSource _stop = new();

    // Each sound's peaks by its key, null when they couldn't be made.
    private readonly Dictionary<string, AudioPeaks?> _made = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, Asset Asset, string Path)> _wanted = new();
    private Thread? _thread;
    private bool _stopped;

    // ready is called on the worker thread with the id of each asset whose peaks are made.
    public MediaWaveforms(string projectDirectory, Func<string> ffmpeg, Action<string> ready)
    {
        _directory = projectDirectory;
        _ffmpeg = ffmpeg;
        _ready = ready;
    }

    // The sound's peaks, or null when it has none or while they're being made (ready says when).
    public AudioPeaks? Get(Asset asset)
    {
        if (asset.Type != AssetType.Audio || !ProjectStore.IsAssetFile(asset.File))
        {
            return null;
        }

        var path = ProjectStore.AssetPath(_directory, asset);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        var key = Key(asset.File, file.Length, file.LastWriteTimeUtc.Ticks);
        lock (_gate)
        {
            if (_made.TryGetValue(key, out var made))
            {
                return made;
            }

            if (_asked.Add(key) && !_stopped)
            {
                _wanted.Enqueue((key, asset, path));
                if (_thread is null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "waveforms" };
                    _thread.Start();
                }

                Monitor.Pulse(_gate);
            }

            return null;
        }
    }

    // Where a sound's peaks are kept in the project's cache folder.
    public static string PathOf(string projectDirectory, string key) => Path.Combine(projectDirectory, ProjectStore.CacheFolder, Folder, key + ".peaks");

    public static string Key(string file, long length, long changed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file}|{length}|{changed}|peaks {AudioPeaks.PerSecond}")))[..16];

    // A sound being decoded is stopped.
    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            _wanted.Clear();
            Monitor.PulseAll(_gate);
        }

        _stop.Cancel();
    }

    private void Run()
    {
        while (true)
        {
            (string Key, Asset Asset, string Path) next;
            lock (_gate)
            {
                while (_wanted.Count == 0 && !_stopped)
                {
                    Monitor.Wait(_gate);
                }

                if (_stopped)
                {
                    return;
                }

                next = _wanted.Dequeue();
            }

            AudioPeaks? made;
            try
            {
                made = Load(next.Key) ?? Make(next.Key, next.Path);
            }
            catch (Exception)
            {
                made = null;
            }

            lock (_gate)
            {
                _made[next.Key] = made;
                if (_stopped)
                {
                    return;
                }
            }

            if (made is not null)
            {
                _ready(next.Asset.Id);
            }
        }
    }

    private AudioPeaks? Load(string key)
    {
        var path = PathOf(_directory, key);
        try
        {
            return File.Exists(path) ? new AudioPeaks(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private AudioPeaks Make(string key, string path)
    {
        var peaks = AudioAnalyzer.Peaks(_ffmpeg(), path, AudioPeaks.PerSecond, _stop.Token);
        var values = peaks.Select(p => (byte)Math.Round(p * 255)).ToArray();

        // Kept for next time; peaks that can't be saved are still shown.
        try
        {
            var target = PathOf(_directory, key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            File.WriteAllBytes(part, values);
            File.Move(part, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new AudioPeaks(values);
    }
}
