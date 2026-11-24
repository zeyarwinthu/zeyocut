// SPDX-License-Identifier: MIT

using System.Windows.Media.Imaging;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render;

namespace ZeyoCut.Editor;

// A picture of each project for Home's cards: its frame a second in (10% in, when it's shorter than
// 10 s), drawn small by the renderer exports use, one at a time on a worker thread. It's kept in the
// project's cache folder (cache\thumbnails\project-<revision>.png), so a project saved since is drawn
// again, and the picture of an older revision is let go. A project with no layers has none.
public sealed class ProjectThumbnails : IDisposable
{
    public const string Prefix = "project-";
    public const int Width = 320;
    public const int Height = 180;

    private readonly object _gate = new();
    private readonly Func<string> _ffmpeg;
    private readonly Action<string> _ready;

    // Each picture by folder and revision, null when it couldn't be drawn.
    private readonly Dictionary<string, BitmapSource?> _made = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Key, ProjectEntry Project)> _wanted = new();
    private Thread? _thread;
    private bool _stopped;

    // ready is called on the worker thread with the folder of each project whose picture is drawn.
    public ProjectThumbnails(Func<string> ffmpeg, Action<string> ready)
    {
        _ffmpeg = ffmpeg;
        _ready = ready;
    }

    // The project's picture, or null when it has none or while it's being drawn (ready says when).
    public BitmapSource? Get(ProjectEntry project)
    {
        if (project.Empty || project.Length is null)
        {
            return null;
        }

        var key = $"{Path.GetFullPath(project.Directory)}|{project.Revision}";
        lock (_gate)
        {
            if (_made.TryGetValue(key, out var made))
            {
                return made;
            }

            if (_asked.Add(key) && !_stopped)
            {
                _wanted.Enqueue((key, project));
                if (_thread is null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "project pictures" };
                    _thread.Start();
                }

                Monitor.Pulse(_gate);
            }

            return null;
        }
    }

    // Drops the pictures not started yet, when Home is left; they're asked for again when it's back.
    public void Forget()
    {
        lock (_gate)
        {
            foreach (var (key, _) in _wanted)
            {
                _asked.Remove(key);
            }

            _wanted.Clear();
        }
    }

    // Where a revision's picture is kept in the project's cache folder.
    public static string PathOf(string projectDirectory, long revision) =>
        Path.Combine(projectDirectory, ProjectStore.CacheFolder, MediaThumbnails.Folder, $"{Prefix}{revision}.png");

    // The time the picture shows: a second in, or 10% in when the project is shorter than 10 s.
    public static double CoverTime(double duration) => Math.Min(1, duration * 0.1);

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
            (string Key, ProjectEntry Project) next;
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
                made = Load(next.Project) ?? Make(next.Project);
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
                _ready(next.Project.Directory);
            }
        }
    }

    private static BitmapSource? Load(ProjectEntry project)
    {
        var path = PathOf(project.Directory, project.Revision);
        try
        {
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // Draws the project as it's saved now, which is newer than the card's revision when it was saved
    // again since Home listed it.
    private BitmapSource Make(ProjectEntry entry)
    {
        var project = ProjectStore.Load(entry.Directory);
        var scale = Math.Min(1, Math.Min(Width / (double)project.Width, Height / (double)project.Height));
        using var renderer = new FrameRenderer(entry.Directory, _ffmpeg);
        using var image = renderer.RenderImage(project, FrameMath.NearestFrame(project, CoverTime(project.Duration)), new RenderOptions(Proxies: true, Scale: scale));
        var png = Png.Encode(image);

        // Kept for next time, in place of the older revisions' pictures; one that can't be saved is
        // still shown.
        try
        {
            var target = PathOf(entry.Directory, project.Revision);
            var folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            var part = target + ".part";
            File.WriteAllBytes(part, png);
            File.Move(part, target, overwrite: true);
            foreach (var old in Directory.EnumerateFiles(folder, Prefix + "*.png"))
            {
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(old);
                }
            }
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
