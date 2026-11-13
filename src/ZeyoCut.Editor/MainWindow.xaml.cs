// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Editor;

// One asset as the media library lists it.
public sealed record MediaItem(string Id, string Kind, Brush Badge, string Details, bool Missing);

// The editor window: the start page until a project is open, then the media library (left), the
// preview at the playhead (center), the inspector (right) and the time ruler (bottom). Every change
// goes through the project's EditorDocument, which saves it at once; changes another program saves
// are taken as soon as project.json changes (and checked every second besides).
public partial class MainWindow : Window
{
    private static readonly Brush VideoBadge = Frozen(0x3E, 0x6F, 0xB0);
    private static readonly Brush ImageBadge = Frozen(0x3E, 0x8F, 0x5A);
    private static readonly Brush AudioBadge = Frozen(0x8A, 0x5B, 0xB5);
    private static readonly Brush FontBadge = Frozen(0xA0, 0x70, 0x30);

    private readonly Settings _settings;
    private readonly string _settingsPath;
    private readonly EditorSettings _prefs;
    private readonly JobManager _jobs = new();
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _ticker;
    private EditorDocument? _document;
    private ProxyJobs? _proxies;
    private MediaImporter? _importer;
    private FramePreview? _preview;
    private FileSystemWatcher? _watcher;
    private WriteableBitmap? _bitmap;
    private FfmpegPaths? _ffmpeg;
    private DateTime? _drawingSince;
    private double? _zoom;
    private int _frame;
    private int _frames = 1;
    private long _shownRevision = -1;
    private string? _selectedAsset;
    private bool _rebuilding;

    public MainWindow(Settings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _prefs = EditorSettings.Load(settingsPath);
        InitializeComponent();
        RestorePlacement();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            TakeOutsideChanges();
        };
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (!_settle.IsEnabled)
            {
                TakeOutsideChanges();
            }
        }, Dispatcher);
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        ShowStartPage();
    }

    // Opens a project folder (or the folder of a project.json) in place of the one open.
    public void OpenProject(string path)
    {
        string directory;
        EditorDocument document;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            directory = File.Exists(full) && string.Equals(Path.GetFileName(full), ProjectStore.ProjectFileName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(full)!
                : full;
            if (!ProjectStore.IsProjectDirectory(directory))
            {
                throw new ZeyoCutException($"there's no project in {directory}: a project folder holds a {ProjectStore.ProjectFileName}");
            }

            document = EditorDocument.Open(directory);
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (_document is null)
            {
                ShowStartPage($"Couldn't open {path}: {ex.Message}");
            }
            else
            {
                Message($"Couldn't open {path}: {ex.Message}");
            }

            return;
        }

        CloseProject();
        ShowProject(document);
        _prefs.AddRecent(directory);
        SavePrefs();
        UpdateMenus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var control = modifiers == ModifierKeys.Control;
        var open = _document is not null;
        if (control && key == Key.O)
        {
            ChooseProject();
        }
        else if (control && key == Key.I && open)
        {
            ChooseMedia();
        }
        else if (control && key == Key.Z && open)
        {
            Undo();
        }
        else if (open && ((control && key == Key.Y) || (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z)))
        {
            Redo();
        }
        else if (open && modifiers is ModifierKeys.None or ModifierKeys.Shift && key is Key.Left or Key.Right)
        {
            var step = modifiers == ModifierKeys.Shift ? _document!.View.Fps : 1;
            Seek(_frame + (key == Key.Left ? -step : step));
        }
        else if (open && modifiers == ModifierKeys.None && key is Key.Home or Key.End)
        {
            Seek(key == Key.Home ? 0 : _frames - 1);
        }
        else if (open && modifiers == ModifierKeys.None && key == Key.Escape && MediaList.SelectedItem is not null)
        {
            MediaList.SelectedItem = null;
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyZoom();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SavePlacement();
        CloseProject();
        _poll.Stop();
        _settle.Stop();
        _ticker.Stop();
        var running = _jobs.All.Where(j => j.State == JobState.Running).ToList();
        foreach (var job in running)
        {
            job.Cancellation.Cancel();
        }

        Task.WaitAll(running.Select(j => j.Completion).ToArray(), TimeSpan.FromSeconds(3));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 * 1048576L ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // FFmpeg as the server finds it: the FfmpegPath setting, FFMPEG_PATH, then PATH. Looked for again
    // until it's found.
    private FfmpegPaths Ffmpeg() => _ffmpeg ??= FfmpegLocator.Locate(_settings.FfmpegPath);

    private void ShowProject(EditorDocument document)
    {
        _document = document;
        var directory = document.Session.Directory;
        _proxies = new ProxyJobs(_jobs, Ffmpeg);
        _importer = new MediaImporter(document, _jobs, Ffmpeg, action => Dispatcher.InvokeAsync(action));
        _importer.Added += OnAssetAdded;
        PendingList.ItemsSource = _importer.Pending;
        FramePreview? preview = null;
        preview = new FramePreview(directory, () => Ffmpeg().Ffmpeg, frame => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(preview, _preview))
            {
                OnFrame(frame);
            }
        }));
        _preview = preview;
        _watcher = Watch(directory);
        _frame = 0;
        _selectedAsset = null;
        _shownRevision = -1;
        _bitmap = null;
        PreviewImage.Source = null;
        StartPage.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
        Message(document.Session.MigratedFrom is { } from
            ? $"Updated project.json from format {from} to {Project.CurrentFormatVersion}; the file as it was is kept as {Path.GetFileName(document.Session.OriginalCopy)}"
            : $"Opened {directory}");
        Rebuild();
        _proxies.Start(document.Session);
    }

    private void CloseProject()
    {
        if (_document is null)
        {
            return;
        }

        _importer!.CancelAll();
        _importer.Added -= OnAssetAdded;
        _watcher?.Dispose();
        _preview?.Dispose();
        _document = null;
        _importer = null;
        _proxies = null;
        _preview = null;
        _watcher = null;
        _bitmap = null;
        PreviewImage.Source = null;
        PendingList.ItemsSource = null;
        Title = "ZeyoCut Editor";
        SaveStatus.Text = "";
        RenderText.Text = "";
    }

    // project.json changing starts a short wait, so a burst of saves is taken (and named) at once.
    private FileSystemWatcher Watch(string directory)
    {
        var watcher = new FileSystemWatcher(directory, ProjectStore.ProjectFileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void Changed(object sender, FileSystemEventArgs e) => Dispatcher.InvokeAsync(() =>
        {
            _settle.Stop();
            _settle.Start();
        });
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += Changed;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    // Takes what another program saved, if anything, and shows it.
    private void TakeOutsideChanges()
    {
        if (_document is null)
        {
            return;
        }

        _document.Refresh();
        if (_document.TakeOutsideChanges() is { Edits.Count: > 0 } change)
        {
            var edits = change.Edits.Count > 4 ? string.Join(", ", change.Edits.Take(4)) + $" and {change.Edits.Count - 4} more" : string.Join(", ", change.Edits);
            Message($"Another program saved: {edits}");
        }

        if (_document.Revision != _shownRevision)
        {
            Rebuild();
            _proxies!.Start(_document.Session);
        }
        else
        {
            UpdateStatus();
        }
    }

    // Shows the project as the document has it now.
    private void Rebuild()
    {
        var document = _document!;
        var project = document.View;
        _shownRevision = document.Revision;
        Title = $"{Path.GetFileName(document.Session.Directory)} — ZeyoCut Editor";
        _frames = Math.Max(1, FrameMath.FrameCount(project));
        _frame = Math.Clamp(_frame, 0, _frames - 1);
        Ruler.Show(_frames, project.Fps, project.Markers);

        _rebuilding = true;
        var items = project.Assets.Select(a => Item(document.Session.Directory, a)).ToList();
        MediaList.ItemsSource = items;
        MediaList.SelectedItem = items.FirstOrDefault(i => i.Id == _selectedAsset);
        _selectedAsset = (MediaList.SelectedItem as MediaItem)?.Id;
        _rebuilding = false;
        MediaEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowInspector();
        UpdateStatus();
        ApplyZoom();
        RequestFrame();
    }

    private static MediaItem Item(string directory, Asset asset)
    {
        var path = ProjectStore.AssetPath(directory, asset);
        if (!File.Exists(path))
        {
            return new MediaItem(asset.Id, Kind(asset), Badge(asset), $"file missing: {asset.File}", true);
        }

        var size = asset.Width is { } w && asset.Height is { } h ? $"{w}×{h}" : null;
        var length = asset.Duration is { } d ? Timecode.Short(d) : null;
        string?[] details = asset.Type switch
        {
            AssetType.Video => [size, length, asset.FrameRate is { } rate ? $"{rate} fps" : null, asset.Normalized is null ? null : "normalized"],
            AssetType.Image => [size],
            AssetType.Audio => [length],
            _ => [asset.FontFamily],
        };
        return new MediaItem(asset.Id, Kind(asset), Badge(asset), string.Join(" · ", details.Where(t => t is not null)), false);
    }

    private static string Kind(Asset asset) => EnumNames.ToName(asset.Type).ToUpperInvariant();

    private static Brush Badge(Asset asset) => asset.Type switch
    {
        AssetType.Video => VideoBadge,
        AssetType.Image => ImageBadge,
        AssetType.Audio => AudioBadge,
        _ => FontBadge,
    };

    private void ShowInspector()
    {
        var document = _document!;
        var project = document.View;
        if (_selectedAsset is { } id && project.FindAsset(id) is { } asset)
        {
            InspectorTitle.Text = $"{Kind(asset)} ASSET";
            InspectorList.ItemsSource = AssetRows(document.Session.Directory, project, asset);
            return;
        }

        InspectorTitle.Text = "PROJECT";
        var file = Path.Combine(document.Session.Directory, ProjectStore.ProjectFileName);
        List<KeyValuePair<string, string>> rows =
        [
            new("Name", project.Name),
            new("Folder", document.Session.Directory),
            new("Canvas", $"{project.Width} × {project.Height}"),
            new("Frame rate", $"{project.Fps} fps"),
            new("Duration", $"{Number(project.Duration)} s ({_frames} frames)"),
            new("Background", project.BackgroundColor),
            new("Layers", project.Layers.Count.ToString(CultureInfo.InvariantCulture)),
            new("Assets", project.Assets.Count.ToString(CultureInfo.InvariantCulture)),
            new("Markers", project.Markers.Count.ToString(CultureInfo.InvariantCulture)),
            new("Revision", document.Revision.ToString(CultureInfo.InvariantCulture)),
            new("Last saved", File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        ];
        InspectorList.ItemsSource = rows;
    }

    private static List<KeyValuePair<string, string>> AssetRows(string directory, Project project, Asset asset)
    {
        var path = ProjectStore.AssetPath(directory, asset);
        var exists = File.Exists(path);
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Id", asset.Id),
            new("Type", EnumNames.ToName(asset.Type)),
            new("File", exists ? asset.File : $"{asset.File} (missing)"),
        };
        if (exists)
        {
            rows.Add(new("Size", Megabytes(new FileInfo(path).Length)));
        }

        if (asset.Width is { } width && asset.Height is { } height)
        {
            rows.Add(new("Resolution", $"{width} × {height}"));
        }

        if (asset.Duration is { } duration)
        {
            rows.Add(new("Duration", $"{Number(duration)} s"));
        }

        if (asset.FrameRate is { } rate)
        {
            rows.Add(new("Frame rate", $"{rate} fps"));
        }

        if (asset.FrameCount is { } frames)
        {
            rows.Add(new("Frames", frames.ToString(CultureInfo.InvariantCulture)));
        }

        if (asset.Type == AssetType.Video)
        {
            rows.Add(new("Sound", asset.HasAudio == true ? "yes" : "no"));
        }

        if (asset.Codec is { } codec)
        {
            rows.Add(new("Codec", codec));
        }

        if (asset.FontFamily is { } family)
        {
            rows.Add(new("Font", family));
        }

        if (asset.OriginalFile is { } original)
        {
            rows.Add(new("Original", original));
        }

        if (asset.Normalized is { } normalized)
        {
            rows.Add(new("Normalized", normalized));
        }

        if (asset.Type == AssetType.Video)
        {
            rows.Add(new("Preview proxy", !Proxies.Wanted(asset)
                ? $"not needed ({Proxies.Above} px or less)"
                : Proxies.Ready(directory, asset) is null ? "not made yet; previews read the original" : $"ready ({Proxies.Side} px); exports read the original"));
        }

        var users = ProjectChecker.Users(project, asset.Id).ToList();
        rows.Add(new("Used by", users.Count == 0 ? "no layer" : string.Join(", ", users)));
        return rows;
    }

    private void UpdateStatus()
    {
        if (_document is null)
        {
            return;
        }

        SaveStatus.Text = _document.Status;
        SaveStatus.ToolTip = _document.Status;
        SaveStatus.Foreground = (Brush)FindResource(_document.Status.StartsWith("Saved", StringComparison.Ordinal) ? "SecondaryTextBrush" : "WarningBrush");
        UpdateMenus();
    }

    private void UpdateMenus()
    {
        var open = _document is not null;
        CloseItem.IsEnabled = open;
        MediaMenu.IsEnabled = open;
        UndoItem.IsEnabled = open && _document!.CanUndo;
        RedoItem.IsEnabled = open && _document!.CanRedo;
        RecentMenu.Items.Clear();
        foreach (var directory in _prefs.ExistingRecent())
        {
            var item = new MenuItem { Header = $"{Path.GetFileName(directory)}  ({directory})".Replace("_", "__", StringComparison.Ordinal), Tag = directory };
            item.Click += (_, _) => OpenProject(directory);
            RecentMenu.Items.Add(item);
        }

        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    private void Message(string text)
    {
        MessageText.Text = text;
        MessageText.ToolTip = string.IsNullOrEmpty(text) ? null : text;
    }

    private void Seek(int frame)
    {
        if (_document is null)
        {
            return;
        }

        _frame = Math.Clamp(frame, 0, _frames - 1);
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_document is null || _preview is null)
        {
            return;
        }

        var project = _document.View;
        _preview.Request(project, _frame);
        _drawingSince ??= DateTime.UtcNow;
        Ruler.Playhead = _frame;
        TimecodeText.Text = Timecode.Of(_frame, project.Fps);
        FrameText.Text = $"{Number(Math.Round(FrameMath.TimeOf(project, _frame), 3))} s · frame {_frame} of {_frames - 1}";
    }

    private void OnFrame(PreviewFrame frame)
    {
        _drawingSince = null;
        PreviewBusy.Visibility = Visibility.Collapsed;
        if (frame.Pixels is null)
        {
            PreviewMessage.Text = $"Frame {frame.Frame} can't be drawn:\n{frame.Error}";
            PreviewMessage.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Hidden;
            RenderText.Text = "";
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            PreviewImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        PreviewImage.Visibility = Visibility.Visible;
        PreviewMessage.Visibility = Visibility.Collapsed;
        RenderText.Text = frame.Cached ? "frame from cache" : $"frame drawn in {frame.Took.TotalMilliseconds:0} ms";
        if (frame.Warnings.Count > 0)
        {
            Message(string.Join("; ", frame.Warnings));
        }
    }

    private void ApplyZoom()
    {
        if (_document is null)
        {
            return;
        }

        var project = _document.View;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (width, height, zoom) = PreviewZoom.Layout(project.Width, project.Height, _zoom, PreviewScroller.ActualWidth - 16, PreviewScroller.ActualHeight - 16, dpi);
        PreviewImage.Width = width;
        PreviewImage.Height = height;
        ZoomText.Text = $"{Math.Round(zoom * 100):0}%";
    }

    // Shows the preview's busy sign once a frame takes a moment, keeps the jobs' progress current,
    // and redraws when a proxy has been made.
    private void Tick()
    {
        if (_drawingSince is { } since && DateTime.UtcNow - since > TimeSpan.FromMilliseconds(200))
        {
            PreviewBusy.Visibility = Visibility.Visible;
        }

        var jobs = _jobs.All;
        var running = jobs.Where(j => j.State == JobState.Running).ToList();
        JobsText.Text = string.Join(" · ", running.Select(j => $"{(string.IsNullOrEmpty(j.Stage) ? j.Description : j.Stage)} {j.Percent}%"));
        foreach (var item in _importer?.Pending ?? [])
        {
            item.Update();
        }

        var finished = jobs.Where(j => j.State != JobState.Running && _running.Remove(j.Id)).ToList();
        foreach (var job in running)
        {
            _running.Add(job.Id);
        }

        foreach (var job in finished.Where(j => j.Kind == "proxy"))
        {
            Message(job.State == JobState.Completed ? job.Result?.Split('\n')[0] ?? "" : $"{job.Id}: {job.Error}");
            if (_document is not null)
            {
                ShowInspector();
                RequestFrame();
            }
        }
    }

    private void ShowStartPage(string? error = null)
    {
        Workspace.Visibility = Visibility.Collapsed;
        StartPage.Visibility = Visibility.Visible;
        StartError.Text = error ?? "";
        StartError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateMenus();
        _ = LoadStartLists();
    }

    private async Task LoadStartLists()
    {
        var root = _settings.ProjectsRoot;
        var recent = _prefs.ExistingRecent();
        ProjectsTitle.Text = $"PROJECTS  ·  {root}";
        List<ProjectEntry> recentEntries;
        IReadOnlyList<ProjectEntry> projects;
        try
        {
            (recentEntries, projects) = await Task.Run(() => (recent.Select(ProjectList.Describe).ToList(), ProjectList.In(root)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProjectsEmpty.Text = $"Couldn't list the projects in {root}: {ex.Message}";
            ProjectsEmpty.Visibility = Visibility.Visible;
            return;
        }

        RecentList.ItemsSource = recentEntries;
        RecentTitle.Visibility = RecentList.Visibility = recentEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectsList.ItemsSource = projects;
        ProjectsEmpty.Text = $"No projects in {root} yet. Ask Claude to create one, or open a project folder from somewhere else.";
        ProjectsEmpty.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChooseProject()
    {
        var root = _settings.ProjectsRoot;
        var dialog = new OpenFolderDialog { Title = "Open a ZeyoCut project folder" };
        if (Directory.Exists(root))
        {
            dialog.InitialDirectory = root;
        }

        if (dialog.ShowDialog(this) == true)
        {
            OpenProject(dialog.FolderName);
        }
    }

    private void ChooseMedia()
    {
        if (_importer is null)
        {
            return;
        }

        var patterns = string.Join(";", $"{AssetImporter.SupportedExtensions}, {AssetImporter.VideoExtensions}".Split(", ").Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Import media",
            Multiselect = true,
            Filter = $"Images, audio, fonts and video|{patterns}|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _ = _importer.Import(dialog.FileNames);
        }
    }

    private void Undo()
    {
        _document!.Undo();
        AfterHistoryStep();
    }

    private void Redo()
    {
        _document!.Redo();
        AfterHistoryStep();
    }

    private void AfterHistoryStep()
    {
        Rebuild();
        Message("");
    }

    private void OnAssetAdded(Asset asset)
    {
        _selectedAsset = asset.Id;
        Message($"Imported '{asset.Id}'");
        Rebuild();
        _proxies?.Start(_document!.Session);
    }

    private void RestorePlacement()
    {
        MediaColumn.Width = new GridLength(Math.Clamp(double.IsFinite(_prefs.MediaWidth) ? _prefs.MediaWidth : 280, 180, 700));
        InspectorColumn.Width = new GridLength(Math.Clamp(double.IsFinite(_prefs.InspectorWidth) ? _prefs.InspectorWidth : 300, 200, 700));
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_prefs.Window is { } placement && placement.Width >= MinWidth && placement.Height >= MinHeight
            && screen.IntersectsWith(new Rect(placement.Left, placement.Top, placement.Width, placement.Height)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
            Width = placement.Width;
            Height = placement.Height;
            WindowState = placement.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _prefs.Window = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        }

        _prefs.MediaWidth = MediaColumn.Width.Value;
        _prefs.InspectorWidth = InspectorColumn.Width.Value;
        SavePrefs();
    }

    private void SavePrefs()
    {
        try
        {
            _prefs.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message($"Couldn't save the editor's settings to {_settingsPath}: {ex.Message}");
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => ChooseProject();

    private void OnOpenListed(object sender, RoutedEventArgs e) => OpenProject((string)((FrameworkElement)sender).Tag);

    private void OnCloseProject(object sender, RoutedEventArgs e)
    {
        CloseProject();
        ShowStartPage();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void OnImport(object sender, RoutedEventArgs e) => ChooseMedia();

    private void OnCancelImport(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PendingImport item)
        {
            _importer?.Cancel(item);
        }
    }

    private void OnDismissImport(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PendingImport item)
        {
            _importer?.Dismiss(item);
        }
    }

    private void OnMediaSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding || _document is null)
        {
            return;
        }

        _selectedAsset = (MediaList.SelectedItem as MediaItem)?.Id;
        ShowInspector();
    }

    private void OnSeek(object? sender, int frame) => Seek(frame);

    private void OnFirstFrame(object sender, RoutedEventArgs e) => Seek(0);

    private void OnPreviousFrame(object sender, RoutedEventArgs e) => Seek(_frame - 1);

    private void OnNextFrame(object sender, RoutedEventArgs e) => Seek(_frame + 1);

    private void OnLastFrame(object sender, RoutedEventArgs e) => Seek(_frames - 1);

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    private void OnZoom(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        _zoom = tag == "fit" ? null : double.Parse(tag, CultureInfo.InvariantCulture);
        ApplyZoom();
    }
}
