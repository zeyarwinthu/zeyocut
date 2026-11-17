// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Editor;

// Small pictures of the project's media, for the media panel's tiles and the timeline's clips: a
// picture made smaller, and a video's first frame. Each is made once on a worker thread and kept in
// memory and in the project's cache folder (cache\thumbnails), named by the file, its size and when
// it was last changed, so a file replaced under the same name gets a new one. Sound and fonts have
// none.
public sealed class MediaThumbnails : IDisposable
{
    public const string Folder = "thumbnails";

    // The longest side, in pixels; smaller pictures keep their size.
    public const int Size = 160;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<string> _ffmpeg;
    private readonly Action<string> _ready;

    // Each thumbnail by its key, null when it couldn't be made.
    private readonly Dictionary<string, BitmapSource?> _made = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, Asset Asset, string Path)> _wanted = new();
    private Thread? _thread;
    private bool _stopped;

    // ready is called on the worker thread with the id of each asset whose thumbnail is made.
    public MediaThumbnails(string projectDirectory, Func<string> ffmpeg, Action<string> ready)
    {
        _directory = projectDirectory;
        _ffmpeg = ffmpeg;
        _ready = ready;
    }

    // The asset's thumbnail, or null when it has none or while it's being made (ready says when).
    public BitmapSource? Get(Asset asset)
    {
        if (asset.Type is not (AssetType.Video or AssetType.Image) || !ProjectStore.IsAssetFile(asset.File))
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
                    _thread = new Thread(Run) { IsBackground = true, Name = "thumbnails" };
                    _thread.Start();
                }

                Monitor.Pulse(_gate);
            }

            return null;
        }
    }

    // Where a thumbnail is kept in the project's cache folder.
    public static string PathOf(string projectDirectory, string key) => Path.Combine(projectDirectory, ProjectStore.CacheFolder, Folder, key + ".png");

    public static string Key(string file, long length, long changed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file}|{length}|{changed}|{Size}")))[..16];

    // The thumbnail's size: the picture's, made smaller to fit Size.
    public static (int Width, int Height) Fit(int width, int height)
    {
        var scale = Math.Min(1, Size / (double)Math.Max(1, Math.Max(width, height)));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            _wanted.Clear();
            Monitor.PulseAll(_gate);
        }
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

            BitmapSource? made;
            try
            {
                made = Load(next.Key) ?? Make(next.Key, next.Asset, next.Path);
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

    private BitmapSource? Load(string key)
    {
        var path = PathOf(_directory, key);
        try
        {
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private BitmapSource Make(string key, Asset asset, string path)
    {
        byte[] png;
        if (asset.Type == AssetType.Video)
        {
            var (width, height) = Fit(asset.Width ?? Size, asset.Height ?? Size);
            using var frames = new VideoFrameSource(_ffmpeg, stream: false);
            png = Png.Encode(frames.GetFrame("thumbnail", path, asset, 0, width, height));
        }
        else
        {
            using var picture = SKImage.FromEncodedData(path) ?? throw new IOException($"not a picture: {path}");
            var (width, height) = Fit(picture.Width, picture.Height);
            using var small = Png.Scale(picture, width, height);
            png = Png.Encode(small);
        }

        // Kept for next time; a thumbnail that can't be saved is still shown.
        try
        {
            var target = PathOf(_directory, key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            File.WriteAllBytes(part, png);
            File.Move(part, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Decode(png);
    }

    private static BitmapSource Decode(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
