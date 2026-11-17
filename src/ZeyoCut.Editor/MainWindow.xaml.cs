// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Editor;

// One asset as the media library lists it: a tile with its thumbnail (Picture, once it's made), a
// dot in its kind's color (Badge), and a video's or sound's length.
public sealed record MediaItem(string Id, string Kind, Brush Badge, string Details, bool Missing)
{
    public Brush? Picture { get; init; }

    public string? Length { get; init; }

    public string Tip { get; init; } = "";
}

// An option of the Export view's preset and language choices.
public sealed record PageChoice(string Group, string Name, bool Selected, bool Usable, string? Tip);

// The editor window: the start page until a project is open, then the media library (left), the
// preview at the playhead (center), the inspector (right) and the timeline (bottom). Every change
// goes through the project's EditorDocument, which saves it at once; changes another program saves
// are taken as soon as project.json changes (and checked every second besides).
public partial class MainWindow : Window
{
    // Playback draws every frame while drawing keeps up, else every second, third or fourth.
    private const int MaxStep = 4;

    // What the media library hands the timeline when an asset is dragged onto it.
    private const string AssetFormat = "ZeyoCut.Asset";

    private static readonly Brush VideoBadge = Frozen(0x5B, 0x8C, 0xFF);
    private static readonly Brush ImageBadge = Frozen(0x3D, 0xD6, 0x8C);
    private static readonly Brush AudioBadge = Frozen(0xA9, 0x8B, 0xFF);
    private static readonly Brush FontBadge = Frozen(0xF0, 0xA9, 0x4B);

    // The inspector header's badge, by kind: its background and its text.
    private static readonly Dictionary<string, (Brush Back, Brush Text)> KindBadges = new()
    {
        ["VIDEO"] = (Frozen(0x24, 0x36, 0x5F), Frozen(0xAF, 0xC4, 0xFF)),
        ["IMAGE"] = (Frozen(0x1F, 0x3D, 0x2C), Frozen(0xA8, 0xE6, 0xC1)),
        ["GROUP"] = (Frozen(0x1C, 0x3A, 0x3A), Frozen(0xA5, 0xE0, 0xE0)),
        ["TEXT"] = (Frozen(0x43, 0x32, 0x1A), Frozen(0xF4, 0xCF, 0x94)),
        ["FONT"] = (Frozen(0x43, 0x32, 0x1A), Frozen(0xF4, 0xCF, 0x94)),
        ["AUDIO"] = (Frozen(0x36, 0x28, 0x4F), Frozen(0xD5, 0xBF, 0xFF)),
        ["CAPTION"] = (Frozen(0x2A, 0x32, 0x42), Frozen(0xC6, 0xD0, 0xE0)),
    };

    private readonly Settings _settings;
    private readonly string _settingsPath;
    private readonly EditorSettings _prefs;
    private readonly JobManager _jobs = new();
    private readonly ExportJobManager _exports;
    private readonly ExportStart _exportStart;
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _ticker;
    private readonly PlaybackClock _clock = new();
    private EditorDocument? _document;
    private ProjectClaim? _claim;
    private ProxyJobs? _proxies;
    private MediaImporter? _importer;
    private FramePreview? _preview;
    private MediaThumbnails? _thumbnails;
    private PreviewSound? _sound;
    private FileSystemWatcher? _watcher;
    private WriteableBitmap? _bitmap;
    private FfmpegPaths? _ffmpeg;
    private DateTime? _drawingSince;
    private double? _zoom;
    private int _frame;
    private int _frames = 1;
    private long _shownRevision = -1;
    private string? _selectedAsset;
    private string? _selectedLayer;
    private bool _rebuilding;
    private bool _inspectorStale;

    // The media panel's filter chip: the kind of asset shown, or null for all of them.
    private AssetType? _mediaFilter;

    // Thumbnails made since the media panel and the timeline were last drawn, waiting to be shown.
    private bool _thumbnailsDue;

    // The zoom slider being set to the timeline's zoom, which isn't a change of the user's.
    private bool _zoomShowing;

    // Whether the shown project's lanes can be packed, worked out once each Rebuild.
    private bool _packable;

    // The layers and lanes the status bar counts, and Claude's last save the header names, worked
    // out once each Rebuild.
    private string _counts = "";
    private SavedChange? _claudeSaved;

    // Saves run one after another on a worker thread (Save): the last one started, and how many
    // aren't shown yet. Until they are, the window leaves the session alone, as a save holds it while
    // it waits for project.lock.
    private Task _saving = Task.CompletedTask;
    private int _saves;

    // Commands given while edits are being saved, which wait for them (WhenSaved).
    private readonly Queue<Action> _waiting = new();

    // What's let finish once the window is asked to close (OnClosing), while it's hidden.
    private Task? _finishing;

    // The project the inspector's rows were built from, which their edits are checked against, and
    // the layer they are of.
    private Project? _inspectorProject;
    private string? _inspectorLayer;

    // The inspector tab chosen last, by name, and the tabs shown: a clip without that tab shows its
    // first, and the next clip that has it shows it again.
    private string _inspectorTab = "";
    private string _inspectorTabs = "";

    // The inspector shows animated values and keyframes at the playhead, so it's built again when the
    // playhead stops moving; queued when that's on its way.
    private bool _inspectorTimed;
    private bool _inspectorQueued;

    // The layer boxes of the frame shown, for picking a layer in the preview; a drag of one under way,
    // where the mouse went down and is now (in screen pixels), and whether its frame is being drawn.
    private IReadOnlyList<LayerOutline> _outlines = [];
    private PreviewDrag? _drag;
    private Point _dragFrom;
    private Point _dragAt;
    private Point _dragDrawn;
    private bool _dragMoved;
    private bool _dragDrawing;
    private string? _dragRefused;
    private bool _committingField;
    private bool _playing;
    private PreviewSource? _playSource;
    private int _playFrom;
    private int _step = 1;
    private double _speed = 1;
    private DateTime _prerollUntil;
    private DateTime _nextAdapt;
    private (Point At, string Asset)? _mediaPressed;
    private ExportStarted? _export;
    private ExportState? _exportShown;
    private string _exportPreset = ExportChoices.ProjectSettings;
    private string _exportLanguage = ExportChoices.EveryLanguage;
    private string? _encoderWarning;

    public MainWindow(Settings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _prefs = EditorSettings.Load(settingsPath);
        VideoEncoder? encoder = null;
        try
        {
            encoder = VideoEncoders.Parse(settings.VideoEncoder);
        }
        catch (ZeyoCutException ex)
        {
            _encoderWarning = $"the VideoEncoder setting: {ex.Message}; using auto";
        }

        _exportStart = new ExportStart(encoder, settings.LibraryPath ?? Settings.DefaultLibraryPath, Ffmpeg);
        _exports = new ExportJobManager { ChooseEncoder = crf => _exportStart.ChooseEncoder(crf) };
        InitializeComponent();
        RestorePlacement();
        SnapToggle.IsChecked = Timeline.Snapping = _prefs.Snapping;
        Timeline.Thumbnail = id => _document?.View.FindAsset(id) is { } asset ? _thumbnails?.Get(asset) : null;
        _prefs.PlaybackScale = _prefs.PlaybackScale switch
        {
            1 => 1,
            0.25 => 0.25,
            _ => 0.5,
        };
        ShowScale();
        SafeAreaToggle.IsChecked = _prefs.SafeArea;
        GridToggle.IsChecked = _prefs.Grid;
        LoopToggle.IsChecked = _prefs.Loop;
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

            RedrawChangedFiles();
            ShowClaude();
        }, Dispatcher);
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        ShowStartPage();
    }

    // Opens a project folder (or the folder of a project.json) in place of the one open. A project
    // another editor window has open comes to the front there instead.
    public void OpenProject(string path)
    {
        string directory;
        EditorDocument document;
        ProjectClaim? claim = null;
        try
        {
            directory = ProjectList.FolderOf(path);
            if (!ProjectStore.IsProjectDirectory(directory))
            {
                throw new ZeyoCutException($"there's no project in {directory}: a project folder holds a {ProjectStore.ProjectFileName}");
            }

            if (_document is not null && ProjectClaim.PipeName(directory) == ProjectClaim.PipeName(_document.Session.Directory))
            {
                Message($"{directory} is open");
                return;
            }

            claim = Claim(directory);
            if (claim is null)
            {
                var note = $"{directory} is open in another editor window, which is in front now";
                if (_document is null)
                {
                    ShowStartPage(note);
                }
                else
                {
                    Message(note);
                }

                return;
            }

            if (!StopExportFirst($"open {Path.GetFileName(directory)}"))
            {
                claim.Dispose();
                return;
            }

            document = EditorDocument.Open(directory);
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            claim?.Dispose();
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
        _claim = claim;
        try
        {
            ShowProject(document);
        }
        catch (Exception ex)
        {
            // Something in it the window can't show: the project is closed again, so the window
            // isn't left half open, and the error's details go to the log.
            CloseProject();
            var log = App.Log(ex, _settings.LogDirectory);
            ShowStartPage($"Couldn't show {directory}: {ex.Message}{(log is null ? "" : $" (the details are in {log})")}");
            return;
        }

        _prefs.AddRecent(directory);
        SavePrefs();
        UpdateCommands();
    }

    // The Edit view is showing, with a project open.
    private bool Editing => _document is not null && EditMode.IsChecked == true;

    // Space plays and pauses wherever the focus is in the Edit view, before a focused button or list
    // takes it.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Editing && e.OriginalSource is not TextBox)
        {
            TogglePlay();
            e.Handled = true;
        }
    }

    // A click anywhere but the inspector field being typed in saves what was typed there first.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (Keyboard.FocusedElement is TextBox { Tag: InspectorField } box && e.OriginalSource is Visual target && !box.IsAncestorOf(target))
        {
            Keyboard.ClearFocus();
        }
    }

    // Whether a key is the project's undo (true) or redo (false): Ctrl+Z, and Ctrl+Y or Ctrl+Shift+Z.
    // Pressed in a text field they are neither: there they are for the typing, also once none of it
    // is left to take back, so they never reach the project's last saved change from a field.
    public static bool? HistoryKey(Key key, ModifierKeys modifiers, bool inField) =>
        inField ? null
        : modifiers == ModifierKeys.Control && key == Key.Z ? true
        : (modifiers == ModifierKeys.Control && key == Key.Y) || (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z) ? false
        : null;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Typing in a field: only Ctrl shortcuts are the window's.
        if (e.Handled || (e.OriginalSource is TextBox && (Keyboard.Modifiers & ModifierKeys.Control) == 0))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var control = modifiers == ModifierKeys.Control;
        var plain = modifiers == ModifierKeys.None;
        var open = Editing;
        if (control && key == Key.O)
        {
            ChooseProject();
        }
        else if (control && _document is not null && key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3)
        {
            (key is Key.D1 or Key.NumPad1 ? ActivityMode : key is Key.D2 or Key.NumPad2 ? EditMode : ExportMode).IsChecked = true;
        }
        else if (control && key == Key.I && open)
        {
            ChooseMedia();
        }
        else if (_document is not null && HistoryKey(key, modifiers, e.OriginalSource is TextBox) is { } undo)
        {
            if (undo)
            {
                Undo();
            }
            else
            {
                Redo();
            }
        }
        else if (open && modifiers is ModifierKeys.None or ModifierKeys.Shift && key is Key.Left or Key.Right)
        {
            Pause();
            var step = modifiers == ModifierKeys.Shift ? _document!.View.Fps : 1;
            Seek(_frame + (key == Key.Left ? -step : step));
        }
        else if (open && plain && key is Key.Home or Key.End)
        {
            Seek(key == Key.Home ? 0 : _frames - 1);
        }
        else if (open && plain && key == Key.F)
        {
            ShowFullScreen();
        }
        else if (open && plain && key is Key.Up or Key.Down)
        {
            JumpToEdit(next: key == Key.Down);
        }
        else if (open && modifiers == ModifierKeys.Alt && key is Key.Left or Key.Right)
        {
            JumpToKeyframe(next: key == Key.Right);
        }
        else if (open && ((plain && key == Key.S) || (control && key == Key.B)))
        {
            SplitSelected();
        }
        else if (open && plain && key is Key.Delete or Key.Back)
        {
            DeleteSelected();
        }
        else if (open && plain && key is Key.B or Key.V)
        {
            (key == Key.B && BladeTool.IsChecked != true ? BladeTool : SelectTool).IsChecked = true;
        }
        else if (open && plain && key == Key.M)
        {
            AddMarker();
        }
        else if (open && plain && key is Key.OemMinus or Key.Subtract)
        {
            Timeline.ZoomBy(1 / 1.5);
        }
        else if (open && plain && key is Key.OemPlus or Key.Add)
        {
            Timeline.ZoomBy(1.5);
        }
        else if (open && modifiers == ModifierKeys.Shift && key == Key.Z)
        {
            Timeline.ZoomToFit();
        }
        else if (open && plain && key == Key.Escape && Timeline.Dragging)
        {
            Timeline.CancelDrag();
        }
        else if (open && plain && key == Key.Escape && _drag is not null)
        {
            CancelPreviewDrag();
        }
        else if (open && plain && key == Key.Escape && BladeTool.IsChecked == true)
        {
            SelectTool.IsChecked = true;
        }
        else if (open && plain && key == Key.Escape && (_selectedLayer is not null || MediaList.SelectedItem is not null))
        {
            MediaList.SelectedItem = null;
            SelectLayer(null);
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
        if (_finishing is not null)
        {
            e.Cancel = !_finishing.IsCompleted;
            return;
        }

        var export = _export?.Job;
        if (!StopExportFirst("close the window", wait: false))
        {
            e.Cancel = true;
            return;
        }

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

        // Edits still being saved (each waits up to 5 s for project.lock), the export and the jobs
        // stopping are let finish, so closing doesn't drop them, with the window hidden rather than
        // held up; then it closes.
        _finishing = Task.WhenAll(
            Task.WhenAny(_saving, Task.Delay(TimeSpan.FromSeconds(10))),
            Task.WhenAny(export?.Completion ?? Task.CompletedTask, Task.Delay(TimeSpan.FromSeconds(5))),
            Task.WhenAny(Task.WhenAll(running.Select(j => j.Completion)), Task.Delay(TimeSpan.FromSeconds(3))));
        if (!_finishing.IsCompleted)
        {
            e.Cancel = true;
            Hide();
            _ = _finishing.ContinueWith(_ => Dispatcher.InvokeAsync(Close), TaskScheduler.Default);
        }
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

    // This window's claim on a project, or null when another window has it open and was asked to
    // come to the front.
    private ProjectClaim? Claim(string directory)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (ProjectClaim.TryClaim(directory, () => Dispatcher.InvokeAsync(BringToFront)) is { } claim)
            {
                return claim;
            }

            if (ProjectClaim.AskToShow(directory, TimeSpan.FromSeconds(2)))
            {
                return null;
            }
        }

        throw new ZeyoCutException("another editor window has it open but doesn't answer; close that window and try again");
    }

    // Another editor was asked to open this window's project.
    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // Windows can keep a window behind the one the user works in; on top for a moment, it shows.
        Topmost = true;
        Topmost = false;
    }

    private void ShowProject(EditorDocument document)
    {
        _document = document;
        var directory = document.Session.Directory;
        _proxies = new ProxyJobs(_jobs, Ffmpeg);
        _importer = new MediaImporter(document, _jobs, Ffmpeg, action => Dispatcher.InvokeAsync(action), operation => SaveImport(document, operation));
        _importer.Added += OnAssetAdded;
        PendingList.ItemsSource = _importer.Pending;
        FramePreview? preview = null;
        preview = new FramePreview(directory, () => Ffmpeg().Ffmpeg, frame => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(preview, _preview) && !_playing)
            {
                OnFrame(frame);
            }
        }));
        _preview = preview;
        MediaThumbnails? thumbnails = null;
        thumbnails = new MediaThumbnails(directory, () => Ffmpeg().Ffmpeg, _ => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(thumbnails, _thumbnails))
            {
                ShowThumbnails();
            }
        }));
        _thumbnails = thumbnails;
        _sound = new PreviewSound(Ffmpeg, Dispatcher);
        _sound.Changed += OnSoundChanged;
        _watcher = Watch(directory);
        _frame = 0;
        _step = 1;
        _selectedAsset = null;
        _selectedLayer = null;
        _shownRevision = -1;
        _bitmap = null;
        PreviewImage.Source = null;
        SoundText.Text = "";
        ExportName.Text = ExportChoices.DefaultFileName(directory);
        _exportPreset = ExportChoices.ProjectSettings;
        _exportLanguage = ExportChoices.EveryLanguage;
        ExportStatus.Text = "";
        StartPage.Visibility = Visibility.Collapsed;
        EditMode.IsChecked = true;
        ShowMode();
        Message(document.Session.MigratedFrom is { } from
            ? $"Updated project.json from format {from} to {Project.CurrentFormatVersion}; the file as it was is kept as {Path.GetFileName(document.Session.OriginalCopy)}"
            : $"Opened {Path.GetFileName(directory)}");
        Timeline.ZoomToFit();
        Rebuild();
        _proxies.Start(document.Session);
    }

    private void CloseProject()
    {
        if (_document is null)
        {
            return;
        }

        Pause();
        _importer!.CancelAll();
        _importer.Added -= OnAssetAdded;
        _watcher?.Dispose();
        if (_preview is { } preview)
        {
            // Disposed on a worker thread: a frame being drawn (footage on a slow drive, many effects)
            // is waited for there, not by the window.
            _ = Task.Run(preview.Dispose);
        }

        _sound!.Changed -= OnSoundChanged;
        _sound.Dispose();
        _thumbnails?.Dispose();
        _thumbnails = null;
        _claim?.Dispose();
        _claim = null;
        _waiting.Clear();
        _export = null;
        _exportShown = null;
        _document = null;
        _importer = null;
        _proxies = null;
        _preview = null;
        _sound = null;
        _watcher = null;
        _bitmap = null;
        _selectedLayer = null;
        PreviewImage.Source = null;
        PendingList.ItemsSource = null;
        Title = "ZeyoCut Editor";
        SaveStatus.Text = "";
        SaveStatus.Visibility = Visibility.Collapsed;
        _counts = "";
        _claudeSaved = null;
        ShowClaude();
        RenderText.Text = "";
        SoundText.Text = "";
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

    // Takes what another program saved, if anything, and shows it. While an edit is being saved, it
    // waits: the save takes it too.
    private void TakeOutsideChanges()
    {
        if (_document is null || _saves > 0)
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
        CanvasText.Text = $"{PreviewZoom.Ratio(project.Width, project.Height)} · {project.Width}×{project.Height} · {project.Fps} fps";
        ProjectName.Text = Path.GetFileName(document.Session.Directory);
        ProjectName.ToolTip = document.Session.Directory;
        _claudeSaved = ProjectHistory.LastByClaude(document.Session.Directory);
        ShowClaude();
        _frames = Math.Max(1, FrameMath.FrameCount(project));
        _frame = Math.Clamp(_frame, 0, _frames - 1);
        _packable = ProjectEditor.PackedLevels(project).Count > 0;

        var lanes = TimelineLayout.Lanes(project);
        Timeline.Show(project, lanes, _frames);
        _counts = $"{Count(project.Layers.Count, "layer")} · {Count(lanes.Count, "lane")}";
        if (_selectedLayer is not null && ClipOf(_selectedLayer) is null)
        {
            _selectedLayer = null;
        }

        Timeline.Selection = ClipOf(_selectedLayer);
        ShowKeyTimes();

        ShowMedia();
        ShowInspector();
        UpdateStatus();
        ApplyZoom();
        ShowPage();
        _sound!.Update(project, document.Session.Directory);
        if (_playing)
        {
            _playSource = _preview!.Source(project, _prefs.PlaybackScale, _step);
            _preview.Play(_playSource, _frame);
            ShowPosition();
        }
        else
        {
            RequestFrame();
        }
    }

    // The media panel: a tile for each asset the filter chips let through, each with its thumbnail
    // once MediaThumbnails has made it. The Fonts chip is there while the project has a font.
    private void ShowMedia()
    {
        if (_document is not { } document)
        {
            return;
        }

        var project = document.View;
        var fonts = project.Assets.Any(a => a.Type == AssetType.Font);
        FontsFilter.Visibility = fonts ? Visibility.Visible : Visibility.Collapsed;
        if (_mediaFilter == AssetType.Font && !fonts)
        {
            _mediaFilter = null;
            AllFilter.IsChecked = true;
        }

        _rebuilding = true;
        List<MediaItem> items;
        try
        {
            items = project.Assets.Where(a => _mediaFilter is null || a.Type == _mediaFilter)
                .Select(a => Item(document.Session.Directory, a) with { Picture = Picture(a) })
                .ToList();
            MediaList.ItemsSource = items;
            MediaList.SelectedItem = items.FirstOrDefault(i => i.Id == _selectedAsset);
            _selectedAsset = (MediaList.SelectedItem as MediaItem)?.Id;
        }
        finally
        {
            _rebuilding = false;
        }

        MediaEmpty.Text = project.Assets.Count == 0
            ? "No media yet. Import images, audio, fonts or video, or ask Claude to."
            : $"No {_mediaFilter switch { AssetType.Video => "video", AssetType.Image => "images", AssetType.Audio => "audio", _ => "fonts" }} in this project.";
        MediaEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // A thumbnail fills a video's tile, and a picture fits in its tile whole.
    private Brush? Picture(Asset asset)
    {
        if (_thumbnails?.Get(asset) is not { } thumbnail)
        {
            return null;
        }

        var brush = new ImageBrush(thumbnail) { Stretch = asset.Type == AssetType.Image ? Stretch.Uniform : Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    // Thumbnails made in a row are shown together.
    private void ShowThumbnails()
    {
        if (_thumbnailsDue)
        {
            return;
        }

        _thumbnailsDue = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _thumbnailsDue = false;
            ShowMedia();
            Timeline.Redraw();
        });
    }

    public static MediaItem Item(string directory, Asset asset)
    {
        // A project.json from elsewhere can name a file outside assets\: it's listed, and never read.
        if (!ProjectStore.IsAssetFile(asset.File))
        {
            var outside = $"not a file in the project's {ProjectStore.AssetsFolder} folder: {asset.File}";
            return new MediaItem(asset.Id, Kind(asset), Badge(asset), outside, true) { Tip = outside };
        }

        var path = ProjectStore.AssetPath(directory, asset);
        if (!File.Exists(path))
        {
            return new MediaItem(asset.Id, Kind(asset), Badge(asset), $"file missing: {asset.File}", true) { Tip = $"file missing: {asset.File}" };
        }

        var size = asset.Width is { } w && asset.Height is { } h ? $"{w}×{h}" : null;
        var length = asset.Duration is { } d ? Timecode.Length(d) : null;
        string?[] details = asset.Type switch
        {
            AssetType.Video => [size, length, asset.FrameRate is { } rate ? $"{rate} fps" : null, asset.Normalized is null ? null : "normalized"],
            AssetType.Image => [size],
            AssetType.Audio => [length],
            _ => [asset.FontFamily],
        };
        var text = string.Join(" · ", details.Where(t => t is not null));
        var use = asset.Type == AssetType.Font ? "A text's Font file field uses it" : "Drag it onto the timeline, or right-click to add it at the playhead";
        return new MediaItem(asset.Id, Kind(asset), Badge(asset), text, false)
        {
            Length = asset.Type is AssetType.Video or AssetType.Audio && asset.Duration is { } seconds ? Timecode.Length(seconds) : null,
            Tip = $"{Path.GetFileName(asset.File)}{(text.Length > 0 ? " · " + text : "")}\n{use}",
        };
    }

    private static string Kind(Asset asset) => EnumNames.ToName(asset.Type).ToUpperInvariant();

    private static Brush Badge(Asset asset) => asset.Type switch
    {
        AssetType.Video => VideoBadge,
        AssetType.Image => ImageBadge,
        AssetType.Audio => AudioBadge,
        _ => FontBadge,
    };

    // Shows the selected clip's fields, the selected asset or the project. While a field holds text
    // that hasn't been saved, a change saved by another program waits until it is: the inspector
    // isn't built again under the user's typing. The field with the keyboard focus keeps it.
    private void ShowInspector()
    {
        var typing = TypingField();
        if (typing is { Dirty: true } && !_committingField)
        {
            _inspectorStale = true;
            return;
        }

        _inspectorStale = false;
        var focused = typing is { } field ? (field.Field.Label, Occurrence(field.Box)) : ((string Label, int Occurrence)?)null;
        _inspectorProject = _document!.View;
        _inspectorLayer = _selectedLayer;
        var rows = InspectorRows();
        _inspectorTimed = rows.Any(f => f.Keyed || f.HasToggle);
        var tabs = rows.Select(f => f.Tab).Where(t => t.Length > 0).Distinct().ToList();
        var tab = tabs.Contains(_inspectorTab) ? _inspectorTab : tabs.FirstOrDefault() ?? "";
        ShowTabs(tabs, tab);
        InspectorList.ItemsSource = tabs.Count == 0 ? rows : rows.Where(f => f.Tab == tab).ToList();
        if (focused is not null)
        {
            Dispatcher.InvokeAsync(() => FocusField(focused.Value.Label, focused.Value.Occurrence), DispatcherPriority.Loaded);
        }
    }

    private List<InspectorField> InspectorRows()
    {
        var document = _document!;
        var project = document.View;
        if (_selectedLayer is { } layerId && project.FindLayer(layerId) is { } layer)
        {
            ShowHeader(InspectorFields.Header(project, layer, Timeline.FindClip(layerId)), true);
            return InspectorFields.Layer(project, layer, Timeline.FindClip(layerId), FrameMath.TimeOf(project, _frame));
        }

        if (_selectedAsset is { } id && project.FindAsset(id) is { } asset)
        {
            ShowHeader(new InspectorHeader(Kind(asset), asset.Id, $"asset · {Path.GetFileName(asset.File)}"), false);
            return Info(AssetRows(document.Session.Directory, project, asset));
        }

        ShowHeader(new InspectorHeader("PROJECT", project.Name, "Project settings"), false);
        var file = Path.Combine(document.Session.Directory, ProjectStore.ProjectFileName);
        List<KeyValuePair<string, string>> rows =
        [
            new("Name", project.Name),
            new("Folder", document.Session.Directory),
            new("Canvas", $"{project.Width} × {project.Height}"),
            new("Frame rate", $"{project.Fps} fps"),
            new("Background", project.BackgroundColor),
            new("Layers", project.Layers.Count.ToString(CultureInfo.InvariantCulture)),
            new("Assets", project.Assets.Count.ToString(CultureInfo.InvariantCulture)),
            new("Markers", project.Markers.Count.ToString(CultureInfo.InvariantCulture)),
            new("Revision", document.Revision.ToString(CultureInfo.InvariantCulture)),
            new("Last saved", File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        ];
        var fields = Info(rows);
        fields.Insert(4, new InspectorField("Duration", FieldKind.Number, Number(project.Duration),
            text => new SetDurationOperation(InspectorFields.Parse(text, "Duration", above: 0, max: 7200), project.Duration),
            Unit: "s", Tip: $"{_frames} frames. The export ends here, and what runs past it is cut"));
        return fields;
    }

    private static List<InspectorField> Info(IEnumerable<KeyValuePair<string, string>> rows) =>
        rows.Select(r => new InspectorField(r.Key, FieldKind.Info, r.Value)).ToList();

    private void ShowHeader(InspectorHeader header, bool clip)
    {
        var (back, text) = KindBadges.GetValueOrDefault(header.Kind, ((Brush)FindResource("ChosenBrush"), (Brush)FindResource("SecondaryTextBrush")));
        InspectorBadge.Background = back;
        InspectorKind.Foreground = text;
        InspectorKind.Text = header.Kind;
        InspectorName.Text = header.Title;
        InspectorName.ToolTip = header.Title;
        InspectorDetails.Text = header.Subtitle;
        InspectorDetails.ToolTip = header.Subtitle.Length == 0 ? null : header.Subtitle;
        InspectorDetails.Visibility = header.Subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InspectorMore.Visibility = clip ? Visibility.Visible : Visibility.Collapsed;
    }

    // The inspector's tabs, made again only when they change.
    private void ShowTabs(List<string> tabs, string chosen)
    {
        var key = string.Join("|", tabs) + "#" + chosen;
        InspectorTabs.Visibility = tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InspectorGap.Visibility = tabs.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (key == _inspectorTabs)
        {
            return;
        }

        _inspectorTabs = key;
        InspectorTabs.Children.Clear();
        foreach (var tab in tabs)
        {
            var button = new RadioButton { Content = tab, GroupName = "InspectorTab", Style = (Style)FindResource("InspectorTab"), IsChecked = tab == chosen, Tag = tab };
            button.Click += OnInspectorTab;
            InspectorTabs.Children.Add(button);
        }
    }

    // The inspector's rows as shown, each followed by its parts.
    private IEnumerable<InspectorField> ShownFields() =>
        InspectorList.Items.OfType<InspectorField>().SelectMany(f => f.Parts.Count > 0 ? [f, .. f.Parts] : new[] { f });

    // The inspector field the keyboard is in, and whether it holds text not saved yet.
    private (InspectorField Field, TextBox Box, bool Dirty)? TypingField() =>
        Keyboard.FocusedElement is TextBox { Tag: InspectorField field } box && InspectorList.IsAncestorOf(box)
            ? (field, box, box.Text.ReplaceLineEndings("\n") != field.Value.ReplaceLineEndings("\n"))
            : null;

    // Rows can share a label, as two face blurs' settings do: occurrence is which of them.
    private TextBox? FocusField(string label, int occurrence)
    {
        if (ShownFields().Where(f => f.Label == label).ElementAtOrDefault(occurrence) is not { } field
            || Descendants<TextBox>(InspectorList).FirstOrDefault(b => ReferenceEquals(b.DataContext, field)) is not { } box)
        {
            return null;
        }

        box.Focus();
        box.SelectAll();
        return box;
    }

    // After another program changed the layer first, the inspector shows its version: what was typed
    // goes back into its field, not saved, for the user to save over that change or drop.
    private void KeepTyped(string layer, string label, int occurrence, string text)
    {
        if (_inspectorLayer == layer && FocusField(label, occurrence) is { } box)
        {
            box.Text = text;
            box.CaretIndex = text.Length;
            Message("What you typed is still in the field: Enter saves it over the other program's change, Escape drops it");
        }
    }

    // Which of the fields with its label a field is, counted from the top; null when it isn't shown.
    private int? Occurrence(InspectorField field)
    {
        var occurrence = 0;
        foreach (var shown in ShownFields())
        {
            if (ReferenceEquals(shown, field))
            {
                return occurrence;
            }

            if (shown.Label == field.Label)
            {
                occurrence++;
            }
        }

        return null;
    }

    // Which of the fields with its label holds the box (its Tag changes when Enter saves it; the
    // field it shows doesn't), counted from the top.
    private int Occurrence(TextBox box) => box.DataContext is InspectorField field ? Occurrence(field) ?? 0 : 0;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }

    // Saves a field's new value through its edit; false when the value can't be used, which the
    // status line says. Typed with Enter it stays to be fixed, otherwise the field shows the saved
    // value again. 'from' is the project the field's row was built from, when the inspector may have
    // been built again since.
    private bool CommitField(InspectorField field, string text, bool fromEnter = false, Project? from = null)
    {
        if (_document is null || field.Commit is null)
        {
            return true;
        }

        if (text.ReplaceLineEndings("\n") == field.Value.ReplaceLineEndings("\n"))
        {
            if (_inspectorStale)
            {
                ShowInspector();
            }

            return true;
        }

        EditOperation operation;
        try
        {
            operation = field.Commit(text);
        }
        catch (ZeyoCutException ex)
        {
            Message(ex.Message);
            if (!fromEnter)
            {
                ShowInspector();
            }

            return false;
        }

        // The inspector is built again over the field being typed in once the edit is saved, unless
        // more was typed meanwhile.
        (TextBox? Box, string? Text) Typing() => TypingField() is { } typing ? (typing.Box, typing.Box.Text) : default;
        var typed = Typing();
        var shown = Occurrence(field);
        var layer = _inspectorLayer;
        Apply(operation, from ?? _inspectorProject, () => Typing() == typed, (saved, overtaken) =>
        {
            if (!saved && overtaken is not null && shown is { } occurrence && layer is not null)
            {
                Dispatcher.InvokeAsync(() => KeepTyped(layer, field.Label, occurrence, text), DispatcherPriority.Loaded);
            }
        });
        return true;
    }
    public static List<KeyValuePair<string, string>> AssetRows(string directory, Project project, Asset asset)
    {
        var path = ProjectStore.IsAssetFile(asset.File) ? ProjectStore.AssetPath(directory, asset) : null;
        var exists = File.Exists(path);
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Id", asset.Id),
            new("Type", EnumNames.ToName(asset.Type)),
            new("File", exists ? asset.File : path is null ? $"{asset.File} (not a file in the project's {ProjectStore.AssetsFolder} folder)" : $"{asset.File} (missing)"),
        };
        if (exists)
        {
            rows.Add(new("Size", Megabytes(new FileInfo(path!).Length)));
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

        if (asset.Type == AssetType.Video && path is not null)
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

        // The header's badge says whether the project is saved; the status bar has the whole line
        // when it says more than that.
        var status = _saves > 0 ? "Saving…" : _document.Status;
        var saved = $"Saved · revision {_document.Revision}";
        var fine = _saves > 0 || status.StartsWith("Saved", StringComparison.Ordinal) || status.EndsWith(saved, StringComparison.Ordinal);
        SaveStatus.Text = status;
        SaveStatus.ToolTip = status;
        SaveStatus.Foreground = (Brush)FindResource(fine ? "SecondaryTextBrush" : "WarningBrush");
        SaveStatus.Visibility = _saves > 0 || status == saved ? Visibility.Collapsed : Visibility.Visible;
        SaveBadgeText.Text = _saves > 0 ? "Saving…" : fine ? $"Saved · rev {_document.Revision}" : $"Rev {_document.Revision}";
        SaveIcon.Text = _saves > 0 ? "\uE895" : fine ? "\uE73E" : "\uE7BA";
        SaveIcon.Foreground = (Brush)FindResource(_saves > 0 ? "SecondaryTextBrush" : fine ? "SuccessBrush" : "WarningBrush");
        SaveBadge.ToolTip = status;
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        var open = _document is not null;

        // While a save runs, the session is busy with it: undo and redo stay as they were.
        if (!open || _saves == 0)
        {
            UndoButton.IsEnabled = open && _document!.CanUndo;
            RedoButton.IsEnabled = open && _document!.CanRedo;
        }

        UpdateEditButtons();
    }

    // The header: the way back to the projects, the project's path and its commands while one is
    // open; the Export view has a way back to Edit in place of the tabs and the Export button.
    private void ShowHeader()
    {
        var open = _document is not null;
        var exporting = open && ExportMode.IsChecked == true;
        LogoButton.IsEnabled = open;
        AppName.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Crumbs.Visibility = ProjectCommands.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ModeBar.Visibility = open && !exporting ? Visibility.Visible : Visibility.Collapsed;
        ExportCrumb.Visibility = BackToEdit.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        ExportViewButton.Visibility = exporting ? Visibility.Collapsed : Visibility.Visible;
    }

    // "Claude saved 3 min ago", while the backups folder has one of its saves.
    private void ShowClaude()
    {
        if (_claudeSaved is not { } last)
        {
            ClaudeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        ClaudeText.Text = $"Claude saved {ProjectHistory.Ago(last.Time, DateTime.Now)}";
        ClaudeBadge.ToolTip = $"Claude's last save, at {last.Time:yyyy-MM-dd HH:mm:ss}: {last.Edit}";
        ClaudeBadge.Visibility = Visibility.Visible;
    }

    // A status bar field takes no room while it has nothing to say.
    private static void ShowText(TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    // Split needs a selected clip under the playhead; Delete, a selected clip; Pack lanes, lanes
    // that can be packed. The Edit view's commands need it showing.
    private void UpdateEditButtons()
    {
        var open = Editing;
        SplitButton.IsEnabled = open && CanSplit(out _);
        DeleteButton.IsEnabled = open && _selectedLayer is not null;
        MarkerButton.IsEnabled = open;
        ShowText(SelectionText, open && _selectedLayer is { } selected ? $"{selected} selected" : "");
        ShowText(LayersText, open ? _counts : "");
        FrameText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PackButton.IsEnabled = open && _packable;
        PackButton.ToolTip = PackButton.IsEnabled || !open
            ? "Put clips that never show at the same time on shared lanes; every frame stays as it is"
            : "The lanes are packed: sharing more of them would change what's drawn on top";
    }

    // Adds a clip of an asset and selects it.
    private void AddClip(AddClipOperation operation)
    {
        var before = _document!.View.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        Apply(operation, then: (_, _) =>
        {
            if (_document!.View.Layers.FirstOrDefault(l => !before.Contains(l.Id)) is { } added)
            {
                SelectLayer(added.Id);
            }
        });
    }

    // A clip of an asset dropped on the timeline: footage or a still on the visual lane it's dropped
    // on, or on a level of its own on top anywhere else; sound in the role of the sound lane it's
    // dropped on, else as music.
    private static AddClipOperation? ClipFor(Asset asset, double time, TimelineLane? lane) => asset.Type switch
    {
        AssetType.Video or AssetType.Image => new AddClipOperation(asset.Type, asset.Id, time, lane is { Kind: LaneKind.Visual } ? lane.ZIndex : null),
        AssetType.Audio => new AddClipOperation(asset.Type, asset.Id, time, Role: lane is { Kind: LaneKind.Audio, Role: { } role } ? EnumNames.ToName(role) : null),
        _ => null,
    };

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
        if (_playing)
        {
            StartPlayback();
            ShowPosition();
            return;
        }

        RequestFrame();
        UpdateEditButtons();
        ShowInspectorAtPlayhead();
    }

    // The inspector at the playhead's time, once the playhead has stopped: built again on its own
    // turn, so stepping or scrubbing through frames doesn't wait for it each frame.
    private void ShowInspectorAtPlayhead()
    {
        if (!_inspectorTimed || _inspectorQueued)
        {
            return;
        }

        _inspectorQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _inspectorQueued = false;
            if (_document is not null && !_playing)
            {
                ShowInspector();
            }
        }, DispatcherPriority.Background);
    }

    // The frame at the playhead, at the canvas size.
    private void RequestFrame()
    {
        if (_document is null || _preview is null)
        {
            return;
        }

        ShowPosition();
        if (_playing)
        {
            return;
        }

        _preview.Request(_preview.Source(_document.View, 1), _frame);
        _drawingSince ??= DateTime.UtcNow;
    }

    // The paused frame again when a file it shows changed without a save (a matte the server
    // finished): its key is made from the files as they are now, and unless a frame is in memory
    // under it, the frame is drawn again. Not while playing, dragging or drawing a frame.
    private void RedrawChangedFiles()
    {
        if (_document is null || _preview is null || _playing || _drag is not null || _drawingSince is not null || PreviewImage.Visibility != Visibility.Visible)
        {
            return;
        }

        _preview.FilesChanged();
        if (_preview.Cached(_preview.Source(_document.View, 1), _frame) is null)
        {
            RequestFrame();
        }
    }

    private void ShowPosition()
    {
        var project = _document!.View;
        Timeline.Playhead = _frame;
        TimecodeText.Text = Timecode.Of(_frame, project.Fps);
        TotalText.Text = $"/ {Timecode.Of(_frames - 1, project.Fps)}";
        FrameText.Text = $"Frame {_frame} / {_frames - 1}";
        FrameText.ToolTip = $"{Number(Math.Round(FrameMath.TimeOf(project, _frame), 3))} s";
    }

    private void OnFrame(PreviewFrame frame)
    {
        _drawingSince = null;
        PreviewBusy.Visibility = Visibility.Collapsed;
        _dragDrawing = false;
        if (frame.Pixels is null)
        {
            PreviewMessage.Text = $"Frame {frame.Frame} can't be drawn:\n{frame.Error}";
            PreviewMessage.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Hidden;
            RenderText.Text = "";
            _outlines = [];
            ShowSelection();
            return;
        }

        ShowPixels(frame);
        if (_drag is not null && _dragMoved && _dragAt != _dragDrawn)
        {
            DrawDrag();
        }

        RenderText.Text = frame.Cached ? "frame from cache" : $"frame drawn in {frame.Took.TotalMilliseconds:0} ms";
        if (frame.Warnings.Count > 0)
        {
            Message(string.Join("; ", frame.Warnings));
        }
    }

    private void ShowPixels(PreviewFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            PreviewImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        PreviewImage.Visibility = Visibility.Visible;
        PreviewMessage.Visibility = Visibility.Collapsed;
        _outlines = frame.Outlines;
        ShowSelection();
    }

    // The timeline clip that holds a layer: its own, or the one of the layer it's attached to.
    private string? ClipOf(string? layerId)
    {
        var project = _document?.View;
        for (var depth = 0; layerId is not null && depth <= (project?.Layers.Count ?? 0); depth++)
        {
            if (Timeline.FindClip(layerId) is not null)
            {
                return layerId;
            }

            layerId = project?.FindLayer(layerId)?.ParentId;
        }

        return null;
    }

    // The selected layer's box and anchor over the preview, when the frame shown has it, and the
    // guides.
    private void ShowSelection()
    {
        ShowOverlays();
        var outline = _document is not null && _selectedLayer is { } id ? _outlines.FirstOrDefault(o => o.LayerId == id) : null;
        if (outline is null || PreviewImage.Visibility != Visibility.Visible || PreviewImage.ActualWidth <= 0)
        {
            SelectionOutline.Visibility = SelectionAnchor.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = PreviewImage.ActualWidth / _document!.View.Width;
        SelectionOutline.Points = new PointCollection(outline.Corners.Select(c => new Point(c.X * scale, c.Y * scale)));
        Canvas.SetLeft(SelectionAnchor, outline.Anchor.X * scale - SelectionAnchor.Width / 2);
        Canvas.SetTop(SelectionAnchor, outline.Anchor.Y * scale - SelectionAnchor.Height / 2);
        SelectionOutline.Visibility = SelectionAnchor.Visibility = Visibility.Visible;
    }

    private void OnPreviewImageSized(object sender, SizeChangedEventArgs e) => ShowSelection();

    // A point on the preview in canvas pixels.
    private (double X, double Y) CanvasPoint(Point point)
    {
        var scale = _document!.View.Width / Math.Max(1, PreviewImage.ActualWidth);
        return (point.X * scale, point.Y * scale);
    }

    // A click in the preview selects the layer drawn on top there (a child layer too), and dragging
    // it moves it.
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Editing || _drag is not null)
        {
            return;
        }

        if (_playing)
        {
            Pause();
        }

        var project = _document!.View;
        var (x, y) = CanvasPoint(e.GetPosition(PreviewImage));
        var hit = LayerOutlines.Hit(_outlines, x, y);
        SelectLayer(hit?.LayerId);
        _dragRefused = null;
        if (hit is null)
        {
            return;
        }

        try
        {
            _drag = PreviewDrag.Start(project, hit, FrameMath.TimeOf(project, _frame));
        }
        catch (ZeyoCutException ex)
        {
            _dragRefused = ex.Message;
        }

        _dragFrom = _dragAt = e.GetPosition(PreviewImage);
        _dragMoved = false;
        PreviewImage.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!PreviewImage.IsMouseCaptured)
        {
            var (x, y) = Editing ? CanvasPoint(e.GetPosition(PreviewImage)) : (-1, -1);
            PreviewImage.Cursor = Editing && LayerOutlines.Hit(_outlines, x, y) is not null ? Cursors.SizeAll : null;
            return;
        }

        _dragAt = e.GetPosition(PreviewImage);
        if (!_dragMoved && Math.Abs(_dragAt.X - _dragFrom.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(_dragAt.Y - _dragFrom.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (_drag is null)
        {
            if (_dragRefused is { } why)
            {
                Message(why);
                _dragRefused = null;
            }

            return;
        }

        _dragMoved = true;
        if (!_dragDrawing)
        {
            DrawDrag();
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!PreviewImage.IsMouseCaptured)
        {
            return;
        }

        var drag = _drag;
        var moved = _dragMoved;
        _dragAt = e.GetPosition(PreviewImage);
        var (dx, dy) = DragTravel();
        _drag = null;
        _dragMoved = false;
        PreviewImage.ReleaseMouseCapture();
        if (drag is null || !moved)
        {
            return;
        }

        EditOperation? edit;
        try
        {
            edit = drag.Move(dx, dy, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        }
        catch (ZeyoCutException ex)
        {
            Message(ex.Message);
            RequestFrame();
            return;
        }

        if (edit is null)
        {
            Message("");
            RequestFrame();
            return;
        }

        Apply(edit, drag.Project);
    }

    private void OnPreviewLostCapture(object sender, MouseEventArgs e) => CancelPreviewDrag();

    // Escape, or the mouse taken by another window: the layer goes back to where it was.
    private void CancelPreviewDrag()
    {
        if (_drag is null)
        {
            return;
        }

        var moved = _dragMoved;
        _drag = null;
        _dragMoved = false;
        PreviewImage.ReleaseMouseCapture();
        if (moved)
        {
            Message("");
            RequestFrame();
        }
    }

    // The mouse's travel since it went down, in canvas pixels.
    private (double X, double Y) DragTravel()
    {
        var scale = _document!.View.Width / Math.Max(1, PreviewImage.ActualWidth);
        return ((_dragAt.X - _dragFrom.X) * scale, (_dragAt.Y - _dragFrom.Y) * scale);
    }

    // Draws the frame with the layer where the mouse has taken it, on a copy of the project; nothing
    // is saved until the mouse is let go. One such frame is drawn at a time, the newest place next.
    private void DrawDrag()
    {
        if (_drag is null || _preview is null)
        {
            return;
        }

        var (dx, dy) = DragTravel();
        var oneAxis = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var moved = ProjectJson.Clone(_document!.View);
        try
        {
            _drag.Move(dx, dy, oneAxis)?.Apply(moved);
        }
        catch (ZeyoCutException ex)
        {
            Message(ex.Message);
            CancelPreviewDrag();
            return;
        }

        Message(_drag.Describe(dx, dy, oneAxis));
        _dragDrawn = _dragAt;
        _dragDrawing = true;
        _preview.Request(_preview.Source(moved, 1), _frame);
        _drawingSince ??= DateTime.UtcNow;
    }

    private void TogglePlay()
    {
        if (_playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    private void Play()
    {
        if (_document is null || _preview is null || _playing)
        {
            return;
        }

        if (_frame >= _frames - 1)
        {
            _frame = 0;
        }

        _playing = true;
        PlayIcon.Data = (Geometry)FindResource("PauseShape");
        PlayButton.ToolTip = "Pause (Space)";
        PreviewBusy.Visibility = Visibility.Collapsed;
        _drawingSince = null;
        StartPlayback();
        CompositionTarget.Rendering += OnPlaybackFrame;
    }

    // Draws ahead from the playhead, and starts the clock and the sound once the first frame ahead
    // is ready (or after a moment).
    private void StartPlayback()
    {
        _playSource = _preview!.Source(_document!.View, _prefs.PlaybackScale, _step);
        _preview.Play(_playSource, _frame);
        _playFrom = _frame;
        _clock.Stop();
        _sound!.Pause();
        _prerollUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
        ShowPlaybackSize();
    }

    private void Pause()
    {
        if (!_playing)
        {
            return;
        }

        _playing = false;
        CompositionTarget.Rendering -= OnPlaybackFrame;
        _clock.Stop();
        _sound?.Pause();
        _preview?.Pause();
        PlayIcon.Data = (Geometry)FindResource("PlayShape");
        PlayButton.ToolTip = "Play (Space)";
        RenderText.Text = "";
        RequestFrame();
        UpdateEditButtons();
        ShowInspectorAtPlayhead();
    }

    // Each screen refresh while playing: where the clock (following the sound) is, and the frame
    // there when it has been drawn; frames not drawn in time are skipped.
    private void OnPlaybackFrame(object? sender, EventArgs e)
    {
        if (!_playing || _document is null || _preview is null || _playSource is null || _sound is null)
        {
            return;
        }

        if (_preview.AheadError is { } error)
        {
            Message($"Playback stopped: {error.Message}");
            Pause();
            return;
        }

        var fps = _document.View.Fps;
        if (!_clock.Running)
        {
            var first = Math.Min(_frames - 1, (_playFrom / _step + 1) * _step);
            if (_preview.Cached(_playSource, first) is null && DateTime.UtcNow < _prerollUntil)
            {
                return;
            }

            var from = FrameMath.TimeOf(_playFrom, fps);
            _clock.Start(from, _speed);
            _sound.Play(from, _speed);
            _nextAdapt = DateTime.UtcNow + TimeSpan.FromSeconds(0.6);
        }

        if (_sound.Position is { } heard)
        {
            _clock.Follow(heard);
        }

        var frame = (int)Math.Floor(_clock.Now * fps + 1e-6);
        if (frame >= _frames && _prefs.Loop && _frames > 1)
        {
            // Looping: on from the first frame, drawn ahead again before the clock starts.
            _frame = 0;
            ShowPosition();
            Timeline.Reveal(0);
            StartPlayback();
            return;
        }

        if (frame >= _frames)
        {
            _frame = _frames - 1;
            Pause();
            return;
        }

        var shown = Math.Max(_playFrom, frame / _step * _step);
        if (shown != _frame)
        {
            _frame = shown;
            _preview.Play(_playSource, shown);
            if (_preview.Cached(_playSource, shown) is { Pixels: not null } cached)
            {
                ShowPixels(cached);
            }

            ShowPosition();
            Timeline.Reveal(shown);
        }

        AdaptStep(fps);
    }

    // Draws every second (third, fourth) frame when drawing each one takes longer than it shows, and
    // every frame again once drawing keeps up.
    private void AdaptStep(int fps)
    {
        if (DateTime.UtcNow < _nextAdapt)
        {
            return;
        }

        _nextAdapt = DateTime.UtcNow + TimeSpan.FromSeconds(0.6);
        var draw = _preview!.DrawTime.TotalSeconds;
        var step = _step;
        if (draw > _step / (fps * _speed) * 0.9 && _step < MaxStep)
        {
            step++;
        }
        else if (_step > 1 && draw < (_step - 1) / (fps * _speed) * 0.6)
        {
            step--;
        }

        if (step != _step)
        {
            _step = step;
            _playSource = _preview.Source(_document!.View, _prefs.PlaybackScale, _step);
            _preview.Play(_playSource, _frame);
            ShowPlaybackSize();
        }
    }

    private void ShowPlaybackSize()
    {
        var size = $"playing at {_playSource!.Width}×{_playSource.Height}";
        RenderText.Text = _step == 1 ? size : $"{size}, every {_step switch { 2 => "2nd", 3 => "3rd", _ => $"{_step}th" }} frame";
    }

    private void OnSoundChanged()
    {
        if (_sound is null)
        {
            return;
        }

        SoundText.Text = _sound.State;
        SoundText.ToolTip = string.IsNullOrEmpty(_sound.State) ? null : _sound.State;
        if (_playing && _clock.Running && _sound.Ready && !_sound.Playing)
        {
            _sound.Play(_clock.Now, _speed);
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
        ZoomText.Text = _zoom is null ? $"Fit · {Math.Round(zoom * 100):0}%" : $"{Math.Round(zoom * 100):0}%";
    }

    // The safe-area guide and the thirds grid over the frame shown, as their switches are.
    private void ShowOverlays()
    {
        var shown = _document is not null && PreviewImage.Visibility == Visibility.Visible && PreviewImage.ActualWidth > 0;
        SafeAreaBox.Visibility = shown && _prefs.SafeArea ? Visibility.Visible : Visibility.Collapsed;
        GridLines.Visibility = shown && _prefs.Grid ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            return;
        }

        var project = _document!.View;
        var scale = PreviewImage.ActualWidth / project.Width;
        var area = ProjectChecker.SafeArea(project);
        Canvas.SetLeft(SafeAreaBox, area.Left * scale);
        Canvas.SetTop(SafeAreaBox, area.Top * scale);
        SafeAreaBox.Width = Math.Max(0, (area.Right - area.Left) * scale);
        SafeAreaBox.Height = Math.Max(0, (area.Bottom - area.Top) * scale);
        var (width, height) = (PreviewImage.ActualWidth, PreviewImage.ActualHeight);
        var lines = new GeometryGroup();
        foreach (var third in new[] { 1 / 3.0, 2 / 3.0 })
        {
            lines.Children.Add(new LineGeometry(new Point(width * third, 0), new Point(width * third, height)));
            lines.Children.Add(new LineGeometry(new Point(0, height * third), new Point(width, height * third)));
        }

        GridLines.Data = lines;
    }

    private void ShowScale() => ScaleText.Text = _prefs.PlaybackScale switch
    {
        1 => "Full res",
        0.25 => "¼ res",
        _ => "½ res",
    };

    // A drop-down list under a button, with the current choice checked.
    private void ShowChoices(Button under, IEnumerable<(string Name, bool Chosen, Action Choose)> choices)
    {
        var menu = new ContextMenu { PlacementTarget = under, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (name, chosen, choose) in choices)
        {
            var item = new MenuItem { Header = name, IsChecked = chosen };
            item.Click += (_, _) => choose();
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // The frame alone on the whole screen, as the preview shows it, until Esc or F.
    private void ShowFullScreen()
    {
        if (_document is null)
        {
            return;
        }

        var image = new Image { Stretch = Stretch.Uniform };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Image.Source)) { Source = PreviewImage });
        var hint = new TextBlock
        {
            Text = "Esc leaves full screen · Space plays",
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
        };
        var screen = new Window
        {
            Owner = this,
            Title = Title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.Black,
            Left = Left + 20,
            Top = Top + 20,
            Width = 640,
            Height = 360,
            Content = new Grid { Children = { image, hint } },
        };
        screen.PreviewKeyDown += (_, e) =>
        {
            var plain = Keyboard.Modifiers == ModifierKeys.None;
            if (plain && e.Key is Key.Escape or Key.F)
            {
                screen.Close();
            }
            else if (plain && e.Key == Key.Space)
            {
                TogglePlay();
            }
            else if (e.Key is Key.Left or Key.Right && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
            {
                Pause();
                var step = Keyboard.Modifiers == ModifierKeys.Shift ? _document?.View.Fps ?? 1 : 1;
                Seek(_frame + (e.Key == Key.Left ? -step : step));
            }
            else
            {
                return;
            }

            e.Handled = true;
        };
        screen.Closed += (_, _) => Activate();
        screen.Show();
        screen.WindowState = WindowState.Maximized;
        screen.Activate();
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
        var progress = running.Select(j => $"{(string.IsNullOrEmpty(j.Stage) ? j.Description : j.Stage)} {j.Percent}%").ToList();
        if (_export?.Job is { } export)
        {
            if (export.State == ExportState.Running)
            {
                progress.Add($"Export {export.Percent.ToString("0", CultureInfo.InvariantCulture)}%");
            }
            else if (_exportShown == ExportState.Running)
            {
                Message(ExportOutcome(export));
                ExportFilesChanged();
            }

            _exportShown = export.State;
            if (ExportPage.Visibility == Visibility.Visible)
            {
                ShowExportState();
            }
        }

        JobsText.Text = string.Join(" · ", progress);
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
                _preview?.ProxiesChanged();
                ShowInspector();
                RequestFrame();
            }
        }
    }

    // The playhead to the next (or previous) place a clip starts or ends.
    private void JumpToEdit(bool next)
    {
        var fps = _document!.View.Fps;
        var frames = TimelineLayout.EditPoints(Timeline.Lanes)
            .Select(t => (int)Math.Round(t * fps))
            .Append(0)
            .Append(_frames - 1)
            .Select(f => Math.Clamp(f, 0, _frames - 1))
            .Distinct()
            .Order()
            .ToList();
        var target = next ? frames.Where(f => f > _frame).DefaultIfEmpty(_frame).First() : frames.Where(f => f < _frame).DefaultIfEmpty(_frame).Last();
        Seek(target);
    }

    // The selected layer's keyframes, marked on its clip in the timeline.
    private void ShowKeyTimes() =>
        Timeline.KeyTimes = _selectedLayer is { } id && _document is not null ? ValueEditor.KeyTimes(_document.View, id) : [];

    // Alt+Left and Alt+Right: the playhead to the selected layer's keyframe before or after it.
    private void JumpToKeyframe(bool next)
    {
        if (_selectedLayer is null)
        {
            Message("Select a clip to go to its keyframes");
            return;
        }

        var project = _document!.View;
        var frames = Timeline.KeyTimes.Select(t => FrameMath.NearestFrame(project, t)).Distinct().Order().ToList();
        var target = next ? frames.Where(f => f > _frame).Cast<int?>().FirstOrDefault() : frames.Where(f => f < _frame).Cast<int?>().LastOrDefault();
        if (target is { } frame)
        {
            Seek(frame);
        }
        else
        {
            Message(frames.Count == 0 ? $"'{_selectedLayer}' has no keyframes" : $"'{_selectedLayer}' has no keyframe {(next ? "after" : "before")} the playhead");
        }
    }

    public void SelectLayer(string? id)
    {
        _selectedLayer = id;
        Timeline.Selection = ClipOf(id);
        ShowKeyTimes();
        ShowSelection();
        if (id is not null)
        {
            _selectedAsset = null;
            _rebuilding = true;
            try
            {
                MediaList.SelectedItem = null;
            }
            finally
            {
                _rebuilding = false;
            }
        }

        ShowInspector();
        UpdateEditButtons();
    }

    // The selected clip can be split at the playhead when the playhead is inside it.
    private bool CanSplit(out double time)
    {
        time = _document is null ? 0 : FrameMath.TimeOf(_document.View, _frame);
        return Timeline.FindClip(_selectedLayer) is { } clip && time > clip.Start + TimelineEditor.Epsilon && time < clip.End - TimelineEditor.Epsilon;
    }

    // The clip selected, at the playhead, as they are when Split is pressed.
    private void SplitSelected()
    {
        var (layer, frame) = (_selectedLayer, _frame);
        WhenSaved(() =>
        {
            if (layer is null)
            {
                Message("Select a clip in the timeline to split it at the playhead");
                return;
            }

            if (Timeline.FindClip(layer) is null && ClipOf(layer) is { } holder)
            {
                Message($"'{layer}' is attached to '{holder}': select that clip to split it with its child layers");
                return;
            }

            var time = FrameMath.TimeOf(_document!.View, frame);
            if (Timeline.FindClip(layer) is not { } clip || time <= clip.Start + TimelineEditor.Epsilon || time >= clip.End - TimelineEditor.Epsilon)
            {
                Message($"The playhead isn't inside '{layer}', so it can't be split there");
                return;
            }

            Apply(new SplitLayerOperation(layer, time));
        });
    }

    // The clip selected when Delete is pressed; pressed again while it's being deleted, it's gone.
    private void DeleteSelected()
    {
        if (_selectedLayer is { } id)
        {
            WhenSaved(() =>
            {
                if (_document!.View.FindLayer(id) is not null)
                {
                    Apply(new RemoveLayerOperation(id, WithChildren: true));
                }
            });
        }
    }

    // Saves an edit and shows the project with it, or why it wasn't saved; 'then' gets whether it
    // was, and the layer another program changed first when that's why not. 'from' is the project the
    // edit was built from, when that isn't the one shown now: a field's rows, or where a drag began.
    // 'committing' is for a field's edit (Save).
    private async void Apply(EditOperation operation, Project? from = null, Func<bool>? committing = null, Action<bool, string?>? then = null)
    {
        if (await Save(document => (Saved: document.Apply(operation, from), document.Summary, document.Overtaken), committing) is not { } result)
        {
            return;
        }

        Message(result.Saved ? result.Summary ?? "" : "");
        then?.Invoke(result.Saved, result.Overtaken);
    }

    // Runs a save after the ones before it, on a worker thread: the wait for project.lock (up to 5 s
    // while another program saves), the backup and the flush to disk don't hold up the window, whose
    // status line says "Saving…" meanwhile. Then shows the project as saved; null when it was closed
    // first. 'committing' says whether the inspector is built again over the field being typed in, as
    // for the field just saved.
    private async Task<T?> Save<T>(Func<EditorDocument, T> save, Func<bool>? committing = null) where T : struct
    {
        var document = _document!;
        _saves++;
        UpdateStatus();
        var saving = _saving.ContinueWith(_ => save(document), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _saving = saving;
        T result;
        try
        {
            result = await saving;
        }
        finally
        {
            _saves--;
            UpdateStatus();

            // After what the saves' callers show (a message, a selection), which comes at a higher
            // priority.
            if (_saves == 0 && _waiting.Count > 0)
            {
                _ = Dispatcher.InvokeAsync(RunWaiting, DispatcherPriority.Background);
            }
        }

        if (!ReferenceEquals(document, _document))
        {
            return null;
        }

        _committingField = committing?.Invoke() == true;
        try
        {
            Rebuild();
        }
        finally
        {
            _committingField = false;
        }

        return result;
    }

    // Runs a command that acts on the project as it's shown (Split, Delete, Pack lanes, a keyframe's
    // diamond, Export) now, or, while edits are being saved, once they're shown: they change what it
    // acts on, and pressed twice, its second time would split or delete what's gone. Commands that
    // wait run in order.
    private void WhenSaved(Action command)
    {
        if (_saves == 0 && _waiting.Count == 0)
        {
            command();
        }
        else
        {
            _waiting.Enqueue(command);
        }
    }

    // The commands that waited, in order, while no edit is being saved: one that saves makes the rest
    // wait for it.
    private void RunWaiting()
    {
        while (_saves == 0 && _waiting.TryDequeue(out var command))
        {
            command();
        }
    }

    private void ShowStartPage(string? error = null)
    {
        Workspace.Visibility = Visibility.Collapsed;
        ActivityPage.Visibility = Visibility.Collapsed;
        ExportPage.Visibility = Visibility.Collapsed;
        StartPage.Visibility = Visibility.Visible;
        StartError.Text = error ?? "";
        StartError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ShowHeader();
        UpdateCommands();
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

    private async void Undo()
    {
        if (await Save(document => document.Undo()) is not null)
        {
            Message("");
        }
    }

    private async void Redo()
    {
        if (await Save(document => document.Redo()) is not null)
        {
            Message("");
        }
    }

    // An import's edit, saved after the window's own (Save); null when its project was closed first.
    private async Task<(bool Saved, string Status)?> SaveImport(EditorDocument into, AddAssetOperation operation) =>
        ReferenceEquals(into, _document) ? await Save(document => (Saved: document.Apply(operation), document.Status)) : null;

    // The proxies are looked for once no edit is being saved: a save holds the session while it waits
    // for project.lock.
    private void OnAssetAdded(Asset asset)
    {
        _selectedAsset = asset.Id;
        _selectedLayer = null;
        Message($"Imported '{asset.Id}'");
        Rebuild();
        WhenSaved(() => _proxies?.Start(_document!.Session));
    }

    private void RestorePlacement()
    {
        MediaColumn.Width = new GridLength(Math.Clamp(double.IsFinite(_prefs.MediaWidth) ? _prefs.MediaWidth : 280, 180, 700));
        InspectorColumn.Width = new GridLength(Math.Clamp(double.IsFinite(_prefs.InspectorWidth) ? _prefs.InspectorWidth : 300, 200, 700));
        TimelineRow.Height = new GridLength(Math.Clamp(double.IsFinite(_prefs.TimelineHeight) ? _prefs.TimelineHeight : 260, 120, 900));
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
        if (Workspace.Visibility == Visibility.Visible && TimelineRow.ActualHeight > 0)
        {
            _prefs.TimelineHeight = TimelineRow.ActualHeight;
        }

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

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];

    private void OnOpen(object sender, RoutedEventArgs e) => ChooseProject();

    private void OnOpenListed(object sender, RoutedEventArgs e) => OpenProject((string)((FrameworkElement)sender).Tag);

    private void OnCloseProject(object sender, RoutedEventArgs e)
    {
        if (!StopExportFirst("close the project"))
        {
            return;
        }

        CloseProject();
        ShowStartPage();
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void OnImport(object sender, RoutedEventArgs e) => ChooseMedia();

    // Files dropped on an open project are imported; a project folder dropped on the start page opens.
    private void OnDragOver(object sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e);
        e.Effects = paths.Length > 0 && (_importer is not null || (paths.Length == 1 && Directory.Exists(paths[0])))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e);
        e.Handled = true;
        if (_importer is null)
        {
            if (paths.Length == 1)
            {
                OpenProject(paths[0]);
            }

            return;
        }

        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Length - files.Count;
        if (folders > 0)
        {
            Message("Folders aren't imported from here: drop the files themselves, or ask Claude to import_folder");
        }

        if (files.Count > 0)
        {
            _ = _importer.Import(files);
        }
    }

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
        if (_selectedAsset is not null)
        {
            _selectedLayer = null;
            Timeline.Selection = null;
            ShowKeyTimes();
            UpdateEditButtons();
        }

        ShowInspector();
    }

    private void OnSeek(object? sender, int frame) => Seek(frame);

    private void OnClipSelected(object? sender, string? id) => SelectLayer(id);

    private void OnTimelineEdit(object? sender, TimelineEdit edit) => Apply(edit.Operation, edit.From);

    // Enter saves a field (Shift+Enter starts a new line of text); Escape puts back what it holds.
    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: InspectorField field } box)
        {
            return;
        }

        if (e.Key == Key.Return && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            // The inspector is built again with the saved value, and the box loses the focus on the
            // way: marked as saved, it isn't saved a second time then.
            e.Handled = true;
            var text = box.Text;
            box.Tag = field with { Value = text };
            if (!CommitField(field, text, fromEnter: true))
            {
                box.Tag = field;
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.Text = field.Value;
            box.SelectAll();
        }
    }

    // Leaving a field saves it, once the focus has moved.
    private void OnFieldLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { Tag: InspectorField field } box)
        {
            var text = box.Text;
            var from = _inspectorProject;
            Dispatcher.InvokeAsync(() => CommitField(field, text, from: from), DispatcherPriority.Input);
        }
    }

    private void OnFieldToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: InspectorField field } box)
        {
            CommitField(field, box.IsChecked == true ? "true" : "false");
        }
    }

    // Typing into an animated value while playing stops the playback first: the field then shows the
    // value at the frame stopped on, where the change is saved.
    private void OnFieldEntered(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_playing && sender is TextBox { Tag: InspectorField { HasToggle: true } })
        {
            Pause();
        }
    }

    // The diamond beside an animatable number: a keyframe at the playhead, added or taken away. While
    // playing, it's where the playback stops.
    private void OnToggleKeyframe(object sender, RoutedEventArgs e)
    {
        if (_document is null || sender is not Button { Tag: InspectorField { ToggleKey: not null } field } || Occurrence(field) is not { } occurrence)
        {
            return;
        }

        // After edits being saved, the diamond's row is taken as the inspector shows them: whether it
        // adds a keyframe or takes one away depends on the project.
        Pause();
        var (layer, frame) = (_inspectorLayer, _frame);
        WhenSaved(() =>
        {
            if (_inspectorLayer != layer || ShownFields().Where(f => f.Label == field.Label).ElementAtOrDefault(occurrence) is not { ToggleKey: { } toggle })
            {
                return;
            }

            try
            {
                Apply(toggle(FrameMath.TimeOf(_document!.View, frame)), _inspectorProject);
            }
            catch (ZeyoCutException ex)
            {
                Message(ex.Message);
            }
        });
    }

    private void OnFieldChosen(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: FieldChoice choice })
        {
            CommitField(choice.Field, choice.Name);
        }
    }

    private void OnInspectorTab(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tab } && tab != _inspectorTab && _document is not null)
        {
            _inspectorTab = tab;
            ShowInspector();
        }
    }

    // A child's chip selects it; a cutout's or redaction's only says what it is.
    private void OnChip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Attachment { LayerId: { } id } })
        {
            SelectLayer(id);
        }
    }

    // While the slider moves, its box shows the value; the value is saved once, when it's let go.
    private void OnSliderMoved(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider { Tag: InspectorField, Parent: Grid row } slider && Descendants<TextBox>(row).FirstOrDefault() is { } box)
        {
            box.Text = InspectorFields.Num(Math.Round(slider.Value));
        }
    }

    private void OnSliderPressed(object sender, MouseButtonEventArgs e)
    {
        if (_playing)
        {
            Pause();
        }
    }

    private void OnSliderReleased(object sender, MouseButtonEventArgs e) => CommitSlider(sender);

    private void OnSliderKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            CommitSlider(sender);
        }
    }

    private void CommitSlider(object sender)
    {
        if (sender is Slider { Tag: InspectorField field } slider)
        {
            CommitField(field, InspectorFields.Num(Math.Round(slider.Value)));
        }
    }

    // The ⋯ menu: what the timeline's menu does to the selected clip.
    private void OnInspectorMenu(object sender, RoutedEventArgs e)
    {
        if (_document?.View is not { } project || _selectedLayer is not { } id || project.FindLayer(id) is not { } layer)
        {
            return;
        }

        var clip = Timeline.FindClip(id) ?? new TimelineClip(id, layer switch { AudioLayer => ClipKind.Audio, VideoLayer => ClipKind.Video, TextLayer => ClipKind.Text, _ => ClipKind.Image },
            id, "", layer.Start, TimelineEditor.ContentEnd(project, layer), 0, !layer.Visible, layer is VideoLayer { Muted: true }, ProjectEditor.Subtree(project, id).Count() - 1, []);
        ShowClipMenu(clip, InspectorMore, true);
    }

    private void OnMediaPressed(object sender, MouseButtonEventArgs e) =>
        _mediaPressed = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(MediaList, source) is ListBoxItem { DataContext: MediaItem item }
            ? (e.GetPosition(MediaList), item.Id)
            : null;

    // Dragging an asset out of the media library, to drop it on the timeline.
    private void OnMediaDragged(object sender, MouseEventArgs e)
    {
        if (_mediaPressed is not { } pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            _mediaPressed = null;
            return;
        }

        var moved = e.GetPosition(MediaList) - pressed.At;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _mediaPressed = null;
        e.Handled = true;
        DragDrop.DoDragDrop(MediaList, new DataObject(AssetFormat, pressed.Asset), DragDropEffects.Copy);
        Timeline.ClearMediaDrop();
    }

    private void OnTimelineDragOver(object sender, DragEventArgs e)
    {
        if (_document is null || e.Data.GetData(AssetFormat) is not string id || _document.View.FindAsset(id) is not { } asset)
        {
            return;
        }

        var (time, lane) = Timeline.MediaDropAt(e.GetPosition(Timeline));
        e.Effects = ClipFor(asset, time, lane) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnTimelineDragLeave(object sender, DragEventArgs e) => Timeline.ClearMediaDrop();

    private void OnTimelineDrop(object sender, DragEventArgs e)
    {
        if (_document is null || e.Data.GetData(AssetFormat) is not string id || _document.View.FindAsset(id) is not { } asset)
        {
            return;
        }

        e.Handled = true;
        var (time, lane) = Timeline.MediaDropAt(e.GetPosition(Timeline));
        Timeline.ClearMediaDrop();
        if (ClipFor(asset, time, lane) is { } operation)
        {
            AddClip(operation);
        }
        else
        {
            Message($"'{id}' is a {EnumNames.ToName(asset.Type)}: a text's Font file field uses it");
        }
    }

    private void OnMediaMenu(object sender, MouseButtonEventArgs e)
    {
        if (_document is null || MediaList.SelectedItem is not MediaItem item || _document.View.FindAsset(item.Id) is not { } asset)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = MediaList };
        var add = new MenuItem { Header = "Add at the playhead" };
        var operation = ClipFor(asset, FrameMath.TimeOf(_document.View, _frame), null);
        add.IsEnabled = operation is not null;
        add.Click += (_, _) => AddClip(operation!);
        menu.Items.Add(add);
        menu.IsOpen = true;
        e.Handled = true;
    }

    // Pressed again while the lanes are being packed, there's nothing more to pack.
    private void OnPackLanes(object sender, RoutedEventArgs e) => WhenSaved(() =>
    {
        if (_packable)
        {
            Apply(new PackLanesOperation());
        }
    });

    private void OnMode(object sender, RoutedEventArgs e) => ShowMode();

    private void OnExportView(object sender, RoutedEventArgs e) => ExportMode.IsChecked = true;

    private void OnBackToEdit(object sender, RoutedEventArgs e) => EditMode.IsChecked = true;

    // Shows the view the mode switch is on. Playback stops outside the Edit view.
    private void ShowMode()
    {
        if (_document is null)
        {
            return;
        }

        var editing = EditMode.IsChecked == true;
        if (!editing)
        {
            Pause();
        }

        Workspace.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = ActivityMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ExportPage.Visibility = ExportMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShowHeader();
        UpdateEditButtons();
        ShowPage();
    }

    // Brings the Activity or Export view up to date when it is the one showing.
    private void ShowPage()
    {
        if (_document is null)
        {
            return;
        }

        if (ActivityMode.IsChecked == true)
        {
            ShowActivity();
        }
        else if (ExportMode.IsChecked == true)
        {
            ShowExportPage();
        }
    }

    // The Activity view: who saved last and the recent saves, from the backups folder.
    private void ShowActivity()
    {
        var document = _document!;
        var saves = ProjectHistory.Saves(document.Session.Directory);
        ActivitySaves.ItemsSource = saves;
        ActivityEmpty.Visibility = saves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivitySaved.Text = saves.Count == 0
            ? $"Revision {document.Revision}."
            : $"Revision {document.Revision}, saved {saves[0].Time:yyyy-MM-dd HH:mm:ss} by {(saves[0].ByEditor ? "you" : "Claude")}: {saves[0].Edit}.";
    }

    // The Export view's choices for the project as it is, keeping the ones made while they fit.
    private void ShowExportPage()
    {
        var project = _document!.View;
        var presets = ExportChoices.Presets(project);
        if (presets.FirstOrDefault(p => p.Name == _exportPreset) is not { Usable: true })
        {
            _exportPreset = ExportChoices.ProjectSettings;
        }

        ExportPresets.ItemsSource = presets.Select(p => new PageChoice("ExportPreset", p.Name, p.Name == _exportPreset, p.Usable, p.Tip)).ToList();
        var languages = ExportChoices.Languages(project);
        if (!languages.Contains(_exportLanguage))
        {
            _exportLanguage = ExportChoices.EveryLanguage;
        }

        ExportLanguages.ItemsSource = languages.Select(l => new PageChoice("ExportLanguage", l, l == _exportLanguage, true, l == ExportChoices.EveryLanguage
            ? "Every layer, whatever its language"
            : $"The layers in {l} and those without a language; clips whose sound is in another language are muted")).ToList();
        ExportLanguageLabel.Visibility = ExportLanguages.Visibility = languages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowExportState();
        ExportFilesChanged();
    }

    // The export this window started last: progress while it runs, what it made when it's done.
    private void ShowExportState()
    {
        var job = _export?.Job;
        var running = job is { State: ExportState.Running };
        ExportButton.IsEnabled = !running;
        ExportName.IsEnabled = !running;
        StopExportButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ExportProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ShowExportButton.Visibility = job is { State: ExportState.Completed } && File.Exists(job.OutputPath) ? Visibility.Visible : Visibility.Collapsed;
        if (job is null)
        {
            return;
        }

        ExportProgress.Value = job.Percent;
        ExportStatus.Foreground = (Brush)FindResource(job.State == ExportState.Failed ? "ErrorBrush" : "TextBrush");
        ExportStatus.Text = ExportText(job, _export!.Version);
    }

    private static string ExportText(ExportJob job, LanguageVersion version)
    {
        var name = Path.GetFileName(job.OutputPath);
        var elapsed = (job.FinishedUtc ?? DateTime.UtcNow) - job.StartedUtc;
        var lines = new List<string>();
        switch (job.State)
        {
            case ExportState.Running:
                lines.Add(job.FramesDone >= job.TotalFrames
                    ? $"Finishing the encode of {name}…"
                    : $"Exporting {name}: {job.Percent.ToString("0.0", CultureInfo.InvariantCulture)}% ({job.FramesDone}/{job.TotalFrames} frames){JobTime.LeftText(job.Percent / 100, elapsed)}");
                break;
            case ExportState.Completed:
                lines.Add($"Saved {ExportSummary.Describe(job.Result!)}");
                lines.AddRange((job.Result!.Notes ?? []).Select(n => $"Processing: {n}"));
                lines.AddRange(job.Result.Warnings.Select(w => $"Warning: {w}"));
                break;
            case ExportState.Cancelled:
                lines.Add($"Stopped after {JobTime.Format(elapsed)}; nothing was written to {name}.");
                break;
            default:
                lines.Add($"The export of {name} failed: {job.Error}");
                break;
        }

        lines.AddRange(version.Warnings.Select(w => $"Warning: {w}"));
        return string.Join("\n", lines);
    }

    // One line for the status bar about an export that has finished.
    private static string ExportOutcome(ExportJob job) => job.State switch
    {
        ExportState.Completed => $"Exported {job.OutputPath} ({ExportSummary.FileSize(job.Result!.Media.FileSize)})",
        ExportState.Cancelled => $"Stopped the export of {Path.GetFileName(job.OutputPath)}",
        _ => $"The export of {Path.GetFileName(job.OutputPath)} failed: {job.Error}",
    };

    private void ExportFilesChanged()
    {
        if (_document is null || ExportPage.Visibility != Visibility.Visible)
        {
            return;
        }

        var files = ExportChoices.Files(_document.Session.Directory);
        ExportFiles.ItemsSource = files;
        ExportFilesEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Before something that would end the running export: asks whether to stop it. True when no
    // export runs or it was stopped; 'wait' waits up to 5 s for it to stop.
    private bool StopExportFirst(string then, bool wait = true)
    {
        if (_export?.Job is not { State: ExportState.Running } job)
        {
            return true;
        }

        var answer = MessageBox.Show(this, $"{Path.GetFileName(job.OutputPath)} is still exporting ({job.Percent.ToString("0", CultureInfo.InvariantCulture)}%). Stop the export and {then}?",
            "ZeyoCut Editor", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        _exports.Cancel(job.Id);
        if (wait)
        {
            job.Completion.Wait(TimeSpan.FromSeconds(5));
        }

        return true;
    }

    private void OnPageChoice(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PageChoice choice })
        {
            if (choice.Group == "ExportPreset")
            {
                _exportPreset = choice.Name;
            }
            else
            {
                _exportLanguage = choice.Name;
            }
        }
    }

    // An edit still being saved goes into the export: it waits for it.
    private void OnExport(object sender, RoutedEventArgs e) => WhenSaved(Export);

    // Exports the project as it is saved now (with what another program saved last), the way
    // start_export does, in this window's process.
    private void Export()
    {
        TakeOutsideChanges();
        if (_document is not { } document)
        {
            return;
        }

        var directory = document.Session.Directory;
        try
        {
            var name = ExportStart.FileName(ExportName.Text);
            ExportName.Text = name;
            if (File.Exists(Path.Combine(directory, ProjectStore.ExportsFolder, name))
                && MessageBox.Show(this, $"{name} is in the exports folder already. Replace it?", "ZeyoCut Editor", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            var preset = ExportChoices.Presets(document.View).First(p => p.Name == _exportPreset).Preset;
            var language = _exportLanguage == ExportChoices.EveryLanguage ? null : _exportLanguage;
            _export = _exportStart.Start(_exports, document.Session.Snapshot(), directory, name, null, preset, language, null);
            _exportShown = ExportState.Running;
            Message($"Exporting {name}{_export.Version.Note}" + (_encoderWarning is null ? "" : $" ({_encoderWarning})"));
            ShowExportState();
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
        {
            ExportStatus.Foreground = (Brush)FindResource("ErrorBrush");
            ExportStatus.Text = $"Couldn't export: {ex.Message}";
        }
    }

    private void OnStopExport(object sender, RoutedEventArgs e)
    {
        if (_export?.Job is { State: ExportState.Running } job)
        {
            _exports.Cancel(job.Id);
        }
    }

    private void OnShowExport(object sender, RoutedEventArgs e)
    {
        if (_export?.Job is { } job)
        {
            ShowInFolder(job.OutputPath);
        }
    }

    private void OnShowExportFile(object sender, RoutedEventArgs e) => ShowInFolder((string)((FrameworkElement)sender).Tag);

    // Opens File Explorer on the file's folder with the file selected.
    private void ShowInFolder(string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Message($"Couldn't open File Explorer: {ex.Message}");
        }
    }

    private void OnClipMenu(object? sender, TimelineClip clip) => ShowClipMenu(clip, Timeline, false);

    private void ShowClipMenu(TimelineClip clip, UIElement target, bool below)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        if (below)
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }

        void Add(string header, string? gesture, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Split at playhead", "S", CanSplit(out _), SplitSelected);
        Add(clip.Children > 0 ? $"Delete with its {clip.Children} child layer(s)" : "Delete", "Delete", true, DeleteSelected);
        menu.Items.Add(new Separator());
        var audio = clip.Kind == ClipKind.Audio;
        Add(audio ? clip.Hidden ? "Unmute" : "Mute" : clip.Hidden ? "Show" : "Hide", null, true, () =>
            Apply(new UpdateLayerOperation(clip.LayerId, new JsonObject { ["visible"] = clip.Hidden })));
        if (clip.Kind == ClipKind.Video && _document!.View.FindLayer(clip.LayerId) is VideoLayer video && _document.View.FindAsset(video.Asset)?.HasAudio == true)
        {
            Add(clip.SoundOff ? "Turn its sound on" : "Turn its sound off", null, true, () =>
                Apply(new UpdateLayerOperation(clip.LayerId, new JsonObject { ["muted"] = !clip.SoundOff })));
        }

        menu.IsOpen = true;
    }

    private void OnTimelineViewChanged(object? sender, EventArgs e)
    {
        TimelineHorizontal.Maximum = Timeline.MaxHorizontalOffset;
        TimelineHorizontal.ViewportSize = Timeline.ViewportWidth;
        TimelineHorizontal.LargeChange = Timeline.ViewportWidth * 0.9;
        TimelineHorizontal.SmallChange = 40;
        TimelineHorizontal.Value = Timeline.HorizontalOffset;
        TimelineHorizontal.IsEnabled = Timeline.MaxHorizontalOffset > 0;
        TimelineVertical.Maximum = Timeline.MaxVerticalOffset;
        TimelineVertical.ViewportSize = Timeline.ViewportHeight;
        TimelineVertical.LargeChange = Timeline.ViewportHeight * 0.9;
        TimelineVertical.SmallChange = 24;
        TimelineVertical.Value = Timeline.VerticalOffset;
        TimelineVertical.IsEnabled = Timeline.MaxVerticalOffset > 0;
        _zoomShowing = true;
        ZoomSlider.Value = Timeline.ZoomLevel;
        ZoomSlider.IsEnabled = Timeline.CanZoom;
        _zoomShowing = false;
    }

    private void OnZoomSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_zoomShowing && Timeline is not null)
        {
            Timeline.ZoomLevel = e.NewValue;
        }
    }

    private void OnTool(object sender, RoutedEventArgs e)
    {
        if (Timeline is not null)
        {
            Timeline.Tool = BladeTool.IsChecked == true ? TimelineTool.Blade : TimelineTool.Select;
        }
    }

    private void OnMediaFilter(object sender, RoutedEventArgs e)
    {
        AssetType? filter = ((FrameworkElement)sender).Tag is string kind ? EnumNames.Parse<AssetType>(kind, "asset type") : null;
        if (filter == _mediaFilter)
        {
            return;
        }

        _mediaFilter = filter;
        ShowMedia();
    }

    private void OnAddMarker(object sender, RoutedEventArgs e) => AddMarker();

    // A chapter marker at the playhead, as add_markers adds one, named for how many there are then.
    private void AddMarker()
    {
        var frame = _frame;
        WhenSaved(() =>
        {
            var project = _document!.View;
            Apply(new AddMarkersOperation([new MarkerEntry(null, FrameMath.TimeOf(project, frame), $"Marker {project.Markers.Count + 1}", null)]));
        });
    }

    // A lane's eye hides every clip in it, or shows them all when they're all hidden; on a sound lane
    // it mutes them, as Shown does there. One step to undo.
    private void OnLaneToggled(object? sender, TimelineLane lane)
    {
        var show = lane.Clips.All(c => c.Hidden);
        var ids = lane.Clips.Where(c => c.Hidden == show).Select(c => c.LayerId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var label = ids.Count == 1 ? $"update_layer {ids[0]}" : $"update_layer {ids[0]} and {ids.Count - 1} more";
        Apply(new UpdateLayersOperation(label, ids, new JsonObject { ["visible"] = show }));
    }

    private void OnTimelineScroll(object sender, ScrollEventArgs e)
    {
        if (ReferenceEquals(sender, TimelineHorizontal))
        {
            Timeline.HorizontalOffset = e.NewValue;
        }
        else
        {
            Timeline.VerticalOffset = e.NewValue;
        }
    }

    private void OnTimelineZoomIn(object sender, RoutedEventArgs e) => Timeline.ZoomBy(1.5);

    private void OnTimelineZoomOut(object sender, RoutedEventArgs e) => Timeline.ZoomBy(1 / 1.5);

    private void OnTimelineFit(object sender, RoutedEventArgs e) => Timeline.ZoomToFit();

    private void OnSplit(object sender, RoutedEventArgs e) => SplitSelected();

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnSnap(object sender, RoutedEventArgs e)
    {
        _prefs.Snapping = Timeline.Snapping = SnapToggle.IsChecked == true;
        SavePrefs();
    }

    private void OnPlay(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnSpeed(object sender, RoutedEventArgs e)
    {
        _speed = double.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);
        if (_playing && _clock.Running && _sound is not null)
        {
            var now = _clock.Now;
            _clock.Start(now, _speed);
            if (_sound.Playing)
            {
                _sound.Play(now, _speed);
            }
        }
    }

    private void OnScaleMenu(object sender, RoutedEventArgs e) =>
        ShowChoices(ScaleButton, new (string, double)[] { ("Full res", 1), ("½ res", 0.5), ("¼ res", 0.25) }
            .Select(c => (c.Item1, c.Item2 == _prefs.PlaybackScale, (Action)(() => SetPlaybackScale(c.Item2)))));

    private void SetPlaybackScale(double scale)
    {
        if (scale == _prefs.PlaybackScale)
        {
            return;
        }

        _prefs.PlaybackScale = scale;
        _step = 1;
        ShowScale();
        SavePrefs();
        if (_playing)
        {
            _playSource = _preview!.Source(_document!.View, scale, _step);
            _preview.Play(_playSource, _frame);
            ShowPlaybackSize();
        }
    }

    private void OnFirstFrame(object sender, RoutedEventArgs e) => Seek(0);

    private void OnPreviousFrame(object sender, RoutedEventArgs e)
    {
        Pause();
        Seek(_frame - 1);
    }

    private void OnNextFrame(object sender, RoutedEventArgs e)
    {
        Pause();
        Seek(_frame + 1);
    }

    private void OnLastFrame(object sender, RoutedEventArgs e) => Seek(_frames - 1);

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    private void OnZoomMenu(object sender, RoutedEventArgs e) =>
        ShowChoices(ZoomButton, new (string, double?)[] { ("Fit", null), ("50%", 0.5), ("100%", 1), ("200%", 2) }
            .Select(c => (c.Item1, c.Item2 == _zoom, (Action)(() =>
            {
                _zoom = c.Item2;
                ApplyZoom();
            }))));

    private void OnOverlay(object sender, RoutedEventArgs e)
    {
        _prefs.SafeArea = SafeAreaToggle.IsChecked == true;
        _prefs.Grid = GridToggle.IsChecked == true;
        SavePrefs();
        ShowOverlays();
    }

    private void OnLoop(object sender, RoutedEventArgs e)
    {
        _prefs.Loop = LoopToggle.IsChecked == true;
        SavePrefs();
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => ShowFullScreen();
}
