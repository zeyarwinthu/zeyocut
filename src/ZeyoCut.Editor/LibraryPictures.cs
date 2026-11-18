// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows.Media.Imaging;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Text;

namespace ZeyoCut.Editor;

// The Titles view's pictures, drawn by the renderer exports use, one at a time on a worker thread: a
// style's name written in it on a 16:9 canvas of the size it was saved for, and a template's frame a
// second in (10% in, when it's shorter than 10 s). Kept while the window is open, by name and save time.
public sealed class LibraryPictures : IDisposable
{
    public const int Width = 320;
    public const int Height = 180;

    private readonly object _gate = new();
    private readonly Func<string> _ffmpeg;
    private readonly Action _ready;
    private readonly Dictionary<string, BitmapSource?> _made = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Key, Func<BitmapSource> Draw)> _wanted = new();
    private Thread? _thread;
    private bool _stopped;

    // ready is called on the worker thread after each picture is drawn.
    public LibraryPictures(Func<string> ffmpeg, Action ready)
    {
        _ffmpeg = ffmpeg;
        _ready = ready;
    }

    // The style's picture, or null while it's being drawn or when it can't be.
    public BitmapSource? Style(Library library, TextStyle style) =>
        Get($"style|{library.Folder(LibraryKind.Style)}|{style.Name}|{style.SavedUtc.Ticks}", () => DrawStyle(library.Folder(LibraryKind.Style), style));

    public BitmapSource? Template(Library library, TemplateFile template) =>
        Get($"template|{library.Folder(LibraryKind.Template)}|{template.Name}|{template.SavedUtc.Ticks}",
            () => DrawTemplate(library.PathOf(LibraryKind.Template, template.Name), template.Project));

    // The project a style is drawn from: its name in the style, centered, on a canvas of the size it
    // was saved for, over a background its color shows on. Its font file is the one next to the style.
    public static Project StyleSample(TextStyle style)
    {
        var side = style.ReferenceSize > 0 ? (int)Math.Round(style.ReferenceSize) : 1080;
        var project = new Project
        {
            Name = style.Name,
            Width = (int)Math.Round(side * 16 / 9.0),
            Height = side,
            Fps = 30,
            Duration = 1,
            BackgroundColor = style.Box is null && Light(style.Color) ? "#232A3A" : "#C9CED8",
        };
        if (style.Font is { } font)
        {
            project.Assets.Add(ProjectJson.Copy(font));
        }

        project.Layers.Add(new TextLayer
        {
            Id = "sample",
            Name = "sample",
            Text = style.Name,
            FontFamily = style.FontFamily,
            FontAsset = style.Font?.Id,
            Bold = style.Bold,
            FontSize = style.FontSize,
            Color = style.Color,
            StrokeColor = style.StrokeColor,
            StrokeWidth = style.StrokeWidth,
            Align = TextAlign.Center,
            LineHeight = style.LineHeight,
            Box = style.Box is null ? null : ProjectJson.Copy(style.Box),
            MaxWidth = project.Width * 0.85,
            X = new AnimatedValue(project.Width / 2.0),
            Y = new AnimatedValue(project.Height / 2.0),
        });
        return project;
    }

    // Whether a #RRGGBB color is light enough to want a dark background.
    public static bool Light(string color)
    {
        var text = color.Trim();
        if (text.Length is not (7 or 9) || text[0] != '#' || !uint.TryParse(text.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return true;
        }

        return 0.299 * (rgb >> 16 & 0xFF) + 0.587 * (rgb >> 8 & 0xFF) + 0.114 * (rgb & 0xFF) > 140;
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

    private BitmapSource? Get(string key, Func<BitmapSource> draw)
    {
        lock (_gate)
        {
            if (_made.TryGetValue(key, out var made))
            {
                return made;
            }

            if (_asked.Add(key) && !_stopped)
            {
                _wanted.Enqueue((key, draw));
                if (_thread is null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "library pictures" };
                    _thread.Start();
                }
                else
                {
                    Monitor.PulseAll(_gate);
                }
            }

            return null;
        }
    }

    private void Run()
    {
        while (true)
        {
            (string Key, Func<BitmapSource> Draw) next;
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

            BitmapSource? picture;
            try
            {
                picture = next.Draw();
            }
            catch (Exception)
            {
                picture = null;
            }

            lock (_gate)
            {
                _made[next.Key] = picture;
                if (_stopped)
                {
                    return;
                }
            }

            _ready();
        }
    }

    private BitmapSource DrawStyle(string folder, TextStyle style)
    {
        var project = StyleSample(style);
        using var renderer = new FrameRenderer(folder, _ffmpeg);
        var text = (TextLayer)project.Layers[0];
        var (width, height) = new TextMeasurer(renderer, project).Measure(text);
        (text.AnchorX, text.AnchorY) = (width / 2, height / 2);
        return Draw(renderer, project, 0);
    }

    private BitmapSource DrawTemplate(string folder, Project project)
    {
        using var renderer = new FrameRenderer(folder, _ffmpeg);
        return Draw(renderer, project, FrameMath.NearestFrame(project, ProjectThumbnails.CoverTime(project.Duration)));
    }

    private static BitmapSource Draw(FrameRenderer renderer, Project project, int frame)
    {
        var scale = Math.Min(1, Math.Min(Width / (double)project.Width, Height / (double)project.Height));
        using var image = renderer.RenderImage(project, frame, new RenderOptions(Proxies: true, Scale: scale));
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(Png.Encode(image));
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
