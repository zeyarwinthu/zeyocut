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

// One asset as the media library lists it: a tile with its thumbnail or its sound's waveform
// (Picture, once it's made), a dot in its kind's color (Badge), and a video's or sound's length.
public sealed record MediaItem(string Id, string Kind, Brush Badge, string Details, bool Missing)
{
    public Brush? Picture { get; init; }

    public string? Length { get; init; }

    public string Tip { get; init; } = "";
}

// An option of the Export view: a preset's card (its title, and its size or what it needs), a
// language, or a caption group to save as an .srt file.
public sealed record PageChoice(string Group, string Name, string Label, string? Detail, bool Selected, bool Usable, string? Tip);

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

    // The Welcome's "Waiting for Claude…" pill.
    private static readonly Brush WaitingBrush = Frozen(0x3A, 0x2C, 0x16);

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

    // The jobs folder ZeyoCut servers keep their records in, where Claude's running exports are read.
    private readonly JobStore? _serverJobs;
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
    private MediaWaveforms? _waveforms;
    private ProjectThumbnails? _projectPictures;
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

    // Home's projects as last listed, before the search: the recent ones and the projects folder's.
    private IReadOnlyList<ProjectEntry> _homeRecent = [];
    private IReadOnlyList<ProjectEntry> _homeProjects = [];

    // Project pictures drawn since Home was last shown, waiting to be shown.
    private bool _picturesDue;

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
    private string _exportQuality = ExportChoices.Qualities[0].Name;

    // The caption groups to save as .srt files with the next export.
    private readonly HashSet<string> _exportSrt = new(StringComparer.Ordinal);

    // The .srt files the export this window started saves once it's done, with their cues from the
    // project as it was exported, and what saving them said.
    private IReadOnlyList<(string Name, List<(double Start, double End, string Text)> Cues)> _exportSrtFiles = [];
    private string? _exportSrtNote;

    // Claude's exports of the project, as last read from the jobs folder, and when.
    private IReadOnlyList<RunningExport> _claudeExports = [];
    private DateTime _claudeExportsRead;
    private bool _readingClaudeExports;
    private string? _encoderWarning;

    // Whose steps the Activity view shows: yours (true), Claude's (false) or both (null).
    private bool? _activityFilter;

    // What had the keyboard before a view opened over the window, to have it again after.
    private IInputElement? _focusBefore;

    // The New project view's canvas card, and whether its fields are being filled in, not typed in.
    private CanvasChoice _newCanvas = NewProjectChoices.Canvases[0];
    private IReadOnlyList<StartChoice> _newStarts = [];
    private StartChoice? _newStart;
    private bool _fillingNew;

    // The Relink view's missing files, and its search for them, stopped when it closes.
    private IReadOnlyList<MissingFile> _missing = [];
    private CancellationTokenSource? _relinkSearch;

    // The Welcome view: when it opened (a client that connects after turns its Connect step green),
    // the step it shows (0 to 2), and what it showed last.
    private DateTimeOffset _welcomeSince;
    private int _welcomeStep = 1;
    private DispatcherTimer? _welcomeTimer;
    private object? _welcomeShown;

    public MainWindow(Settings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        var firstRun = !File.Exists(settingsPath);
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
        _serverJobs = settings.JobsDirectory is { } jobs ? new JobStore(jobs) : null;
        InitializeComponent();
        RestorePlacement();
        SnapToggle.IsChecked = Timeline.Snapping = _prefs.Snapping;
        Timeline.Thumbnail = id => _document?.View.FindAsset(id) is { } asset ? _thumbnails?.Get(asset) : null;
        Timeline.Waveform = id => _document?.View.FindAsset(id) is { } asset ? _waveforms?.Get(asset) : null;
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
        (_prefs.ProjectsAsList ? HomeRows : HomeCards).IsChecked = true;
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
        if (firstRun)
        {
            ShowWelcome();
        }
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
        if (Relinking.Missing(document.View, directory) is { Count: > 0 } missing)
        {
            ShowRelink(missing, opening: true);
        }
    }

    // The Edit view is showing, with a project open.
    private bool Editing => _document is not null && EditMode.IsChecked == true;

    // Play (Space) runs wherever the focus is in the Edit view, before a focused button or list takes
    // its key.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!e.Handled && Shortcuts.For(key, Keyboard.Modifiers).Any(s => s.Command == Command.Play))
        {
            e.Handled = RunKey(key, Keyboard.Modifiers, e.OriginalSource is TextBox) is not null;
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

    // Whether a key is the project's undo (true) or redo (false), as the shortcuts table has them:
    // Ctrl+Z, and Ctrl+Shift+Z or Ctrl+Y. Pressed in a text field they are neither: there they are for
    // the typing, also once none of it is left to take back, so they never reach the project's last
    // saved change from a field.
    public static bool? HistoryKey(Key key, ModifierKeys modifiers, bool inField) =>
        inField ? null
        : Shortcuts.For(key, modifiers).Any(s => s.Command == Command.Undo) ? true
        : Shortcuts.For(key, modifiers).Any(s => s.Command == Command.Redo) ? false
        : null;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled)
        {
            e.Handled = RunKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.OriginalSource is TextBox) is not null;
        }
    }

    // Runs what a key does, as the shortcuts table has it: the first of the key's commands that can
    // run now (Handler); null when none can. Typing in a field, only Ctrl shortcuts are the window's.
    // While a view is open over the window, Esc closes it and the other keys are the view's own; the
    // Welcome view's are all its own.
    public Command? RunKey(Key key, ModifierKeys modifiers, bool inField)
    {
        if (WelcomePage.Visibility == Visibility.Visible)
        {
            return null;
        }

        if (DialogLayer.Visibility == Visibility.Visible)
        {
            if (key != Key.Escape || modifiers != ModifierKeys.None)
            {
                return null;
            }

            CloseDialog();
            return Command.Cancel;
        }

        if (inField && (modifiers & ModifierKeys.Control) == 0)
        {
            return null;
        }

        foreach (var shortcut in Shortcuts.For(key, modifiers))
        {
            if (Handler(shortcut.Command, inField) is { } run)
            {
                run();
                return shortcut.Command;
            }
        }

        return null;
    }

    // Whether a command can run now, as its key would run it.
    public bool CanRun(Command command, bool inField = false) => Handler(command, inField) is not null;

    // What a command does now, or null when it can't run: most need the Edit view showing, and undo
    // and redo aren't the project's in a text field (HistoryKey).
    private Action? Handler(Command command, bool inField)
    {
        var editing = Editing;
        var open = _document is not null;
        return command switch
        {
            Command.Play => editing ? TogglePlay : null,
            Command.PreviousFrame => editing ? () => Step(-1) : null,
            Command.NextFrame => editing ? () => Step(1) : null,
            Command.BackSecond => editing ? () => Step(-_document!.View.Fps) : null,
            Command.ForwardSecond => editing ? () => Step(_document!.View.Fps) : null,
            Command.FirstFrame => editing ? () => Seek(0) : null,
            Command.LastFrame => editing ? () => Seek(_frames - 1) : null,
            Command.PreviousEdit => editing ? () => JumpToEdit(next: false) : null,
            Command.NextEdit => editing ? () => JumpToEdit(next: true) : null,
            Command.FullScreen => editing ? ShowFullScreen : null,
            Command.Split => editing ? SplitSelected : null,
            Command.Delete => editing ? DeleteSelected : null,
            Command.Duplicate => editing ? DuplicateSelected : null,
            Command.Undo => open && !inField ? Undo : null,
            Command.Redo => open && !inField ? Redo : null,
            Command.Import => editing ? ChooseMedia : null,
            Command.Cancel => editing ? CancelHandler() : null,
            Command.AddMarker => editing ? AddMarker : null,
            Command.Snap => editing ? ToggleSnap : null,
            Command.ZoomIn => editing ? () => Timeline.ZoomBy(1.5) : null,
            Command.ZoomOut => editing ? () => Timeline.ZoomBy(1 / 1.5) : null,
            Command.Fit => editing ? Timeline.ZoomToFit : null,
            Command.SelectTool => editing ? () => TurnOn(SelectTool) : null,
            Command.BladeTool => editing ? () => TurnOn(BladeTool.IsChecked == true ? SelectTool : BladeTool) : null,
            Command.PreviousKeyframe => editing ? () => JumpToKeyframe(next: false) : null,
            Command.NextKeyframe => editing ? () => JumpToKeyframe(next: true) : null,
            Command.Edit => open ? () => TurnOn(EditMode) : null,
            Command.Activity => open ? () => TurnOn(ActivityMode) : null,
            Command.Export => open ? () => TurnOn(ExportMode) : null,
            Command.NewProject => () => ShowNewProject(null),
            Command.OpenProject => ChooseProject,
            Command.Shortcuts => ShowShortcuts,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    // Esc lets go of a drag, leaves the blade or clears the selection, whichever there is.
    private Action? CancelHandler() =>
        Timeline.Dragging ? Timeline.CancelDrag
        : _drag is not null ? CancelPreviewDrag
        : BladeTool.IsChecked == true ? () => TurnOn(SelectTool)
        : _selectedLayer is not null || MediaList.SelectedItem is not null ? ClearSelection
        : null;

    private static void TurnOn(ToggleButton button) => button.IsChecked = true;

    private void ClearSelection()
    {
        MediaList.SelectedItem = null;
        SelectLayer(null);
    }

    // Moves the playhead by frames, paused.
    private void Step(int frames)
    {
        Pause();
        Seek(_frame + frames);
    }

    // N: snapping on or off, as the toggle in the timeline's toolbar turns it.
    private void ToggleSnap()
    {
        SnapToggle.IsChecked = SnapToggle.IsChecked != true;
        OnSnap(SnapToggle, new RoutedEventArgs());
        Message(Timeline.Snapping ? "Snapping on" : "Snapping off");
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
        _welcomeTimer?.Stop();
        _poll.Stop();
        _settle.Stop();
        _ticker.Stop();
        _projectPictures?.Dispose();
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
        CloseWelcome();
        if (DialogLayer.Visibility == Visibility.Visible)
        {
            CloseDialog();
        }

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
        MediaWaveforms? waveforms = null;
        waveforms = new MediaWaveforms(directory, () => Ffmpeg().Ffmpeg, _ => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(waveforms, _waveforms))
            {
                ShowThumbnails();
            }
        }));
        _waveforms = waveforms;
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
        _exportSrt.Clear();
        _claudeExports = [];
        ExportClaude.ItemsSource = null;
        ExportStatus.Text = "";
        StartPage.Visibility = Visibility.Collapsed;
        _projectPictures?.Forget();
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
        _waveforms?.Dispose();
        _waveforms = null;
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

    // A thumbnail fills a video's tile, a picture fits in its tile whole, and a sound's waveform runs
    // across its tile, half as tall as the tile at full scale.
    private Brush? Picture(Asset asset)
    {
        if (asset.Type == AssetType.Audio)
        {
            return _waveforms?.Get(asset) is { } peaks ? Waveform(peaks) : null;
        }

        if (_thumbnails?.Get(asset) is not { } thumbnail)
        {
            return null;
        }

        var brush = new ImageBrush(thumbnail) { Stretch = asset.Type == AssetType.Image ? Stretch.Uniform : Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    private static Brush Waveform(AudioPeaks peaks)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 160, 100))));
        drawing.Children.Add(new GeometryDrawing(null, new Pen(AudioBadge, 0.75), new LineGeometry(new Point(8, 50), new Point(152, 50))));
        drawing.Children.Add(new GeometryDrawing(AudioBadge, null, peaks.Outline(0, peaks.Duration, new Rect(8, 25, 144, 50), 72)));
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
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
                if (export.State == ExportState.Completed)
                {
                    SaveSubtitles(export);
                }

                Message(ExportOutcome(export));
                ExportFilesChanged();
            }

            _exportShown = export.State;
            if (ExportPage.Visibility == Visibility.Visible)
            {
                ShowExportState();
            }
        }

        if (ExportPage.Visibility == Visibility.Visible && DateTime.UtcNow - _claudeExportsRead > TimeSpan.FromSeconds(2))
        {
            _ = ReadClaudeExports();
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

    // Ctrl+D: copies the clip selected, with the layers attached to it, and selects the copy.
    private void DuplicateSelected()
    {
        var layer = _selectedLayer;
        WhenSaved(() =>
        {
            if (layer is null || _document!.View.FindLayer(layer) is not { } original)
            {
                Message("Select a clip in the timeline to duplicate it");
                return;
            }

            var before = _document.View.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            Apply(new DuplicateLayerOperation(layer), then: (saved, _) =>
            {
                if (saved && _document?.View.Layers.FirstOrDefault(l => !before.Contains(l.Id) && l.ParentId == original.ParentId) is { } copy)
                {
                    SelectLayer(copy.Id);
                }
            });
        });
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
        HomeFolder.Text = ProjectList.Shorten(root);
        HomeFolder.ToolTip = root;
        List<ProjectEntry> recentEntries;
        IReadOnlyList<ProjectEntry> projects;
        try
        {
            (recentEntries, projects) = await Task.Run(() => (recent.Select(d => ProjectList.Describe(d)).ToList(), ProjectList.In(root)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProjectsEmpty.Text = $"Couldn't list the projects in {root}: {ex.Message}";
            ProjectsEmpty.Visibility = Visibility.Visible;
            return;
        }

        _homeRecent = recentEntries;
        _homeProjects = projects;
        ShowHome();
    }

    // Home: the recent projects, then the projects folder's others, as cards or rows, each with its
    // picture once ProjectThumbnails has drawn it, and only those whose names have the search text.
    private void ShowHome()
    {
        var search = HomeSearch.Text.Trim();
        var (recent, others) = ProjectList.Group(_homeRecent, _homeProjects, search);
        RecentList.ItemsSource = recent.Select(WithPicture).ToList();
        ProjectsList.ItemsSource = others.Select(WithPicture).ToList();
        var any = recent.Count + others.Count > 0;
        RecentTitle.Text = recent.Count > 0 ? "RECENT" : "PROJECTS";
        RecentList.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectsTitle.Visibility = recent.Count > 0 && others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeListHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        HomeNoMatch.Text = $"No project's name has “{search}” in it.";
        HomeNoMatch.Visibility = !any && search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectsEmpty.Text = $"No projects in {_settings.ProjectsRoot} yet. Ask Claude to create one, or open a project folder from somewhere else.";
        ProjectsEmpty.Visibility = _homeProjects.Count == 0 && search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ProjectEntry WithPicture(ProjectEntry entry) => PictureOf(entry) is { } picture ? entry with { Picture = picture } : entry;

    // A project's picture (on Home's cards and the Export view's summary) once it's drawn. It fits
    // whole: a Shorts project's stands in the middle.
    private Brush? PictureOf(ProjectEntry entry)
    {
        _projectPictures ??= new ProjectThumbnails(() => Ffmpeg().Ffmpeg, _ => Dispatcher.InvokeAsync(ShowPictures));
        if (_projectPictures.Get(entry) is not { } picture)
        {
            return null;
        }

        var brush = new ImageBrush(picture) { Stretch = Stretch.Uniform };
        brush.Freeze();
        return brush;
    }

    // Pictures drawn in a row are shown together.
    private void ShowPictures()
    {
        if (_picturesDue)
        {
            return;
        }

        _picturesDue = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _picturesDue = false;
            if (StartPage.Visibility == Visibility.Visible)
            {
                ShowHome();
            }
            else if (ExportPage.Visibility == Visibility.Visible)
            {
                ShowExportSummary();
            }
        });
    }

    private void OnHomeSearch(object sender, TextChangedEventArgs e)
    {
        HomeSearchHint.Visibility = HomeSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowHome();
    }

    private void OnHomeView(object sender, RoutedEventArgs e)
    {
        if (RecentList is null)
        {
            return;
        }

        var rows = HomeRows.IsChecked == true;
        foreach (var list in new[] { RecentList, ProjectsList })
        {
            list.ItemTemplate = (DataTemplate)FindResource(rows ? "ProjectRow" : "ProjectCard");
            list.ItemsPanel = (ItemsPanelTemplate)FindResource(rows ? "ProjectRows" : "ProjectCards");
        }

        if (_prefs.ProjectsAsList != rows)
        {
            _prefs.ProjectsAsList = rows;
            SavePrefs();
        }
    }

    private void OnHomeSized(object sender, SizeChangedEventArgs e) =>
        RecentList.Tag = ProjectsList.Tag = ProjectList.Layout(HomeContent.ActualWidth);

    private void OnShowProjectsFolder(object sender, RoutedEventArgs e)
    {
        var root = _settings.ProjectsRoot;
        string? error = null;
        if (!Directory.Exists(root))
        {
            error = $"The projects folder {root} isn't there yet. It's made with the first project.";
        }
        else
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                error = $"Couldn't open File Explorer: {ex.Message}";
            }
        }

        var shown = WelcomePage.Visibility == Visibility.Visible ? WelcomeError
            : NewProjectDialog.Visibility == Visibility.Visible ? NewError
            : StartError;
        shown.Text = error ?? "";
        shown.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
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
        if (item.Missing)
        {
            var relink = new MenuItem { Header = "Relink…" };
            relink.Click += (_, _) => ShowRelink([asset], opening: false);
            menu.Items.Add(relink);
        }

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

    // The Activity view: the saves the backups folder keeps, in steps, by the side the filter shows.
    private void ShowActivity()
    {
        var steps = ProjectHistory.Steps(_document!.Session.Directory);
        var shown = ProjectHistory.Shown(steps, _activityFilter, DateTime.Now);
        ActivitySteps.ItemsSource = shown;
        ActivityEmpty.Text = steps.Count == 0 ? "Nothing saved yet since the project was made."
            : _activityFilter == true ? "None of the saves kept are yours."
            : "None of the saves kept are Claude's.";
        ActivityEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityOlder.Visibility = steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnActivityFilter(object sender, RoutedEventArgs e)
    {
        _activityFilter = ReferenceEquals(sender, ActivityYou) ? true : ReferenceEquals(sender, ActivityClaude) ? false : null;
        if (_document is not null && ActivityMode.IsChecked == true)
        {
            ShowActivity();
        }
    }

    // Puts the project back as a step on the Activity view left it, once the edits being saved are.
    private void OnRestoreStep(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SavedStep { Restore: { } backup } step)
        {
            WhenSaved(() => Restore(step, backup));
        }
    }

    private async void Restore(SavedStep step, ProjectBackup backup)
    {
        if (await Save(document => document.Restore(backup)) is { } restored)
        {
            Message(restored ? $"Restored the project as it was after {step.Title} ({step.Details}). Undo takes it back." : "");
        }
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

        ExportPresets.ItemsSource = presets.Select(p => new PageChoice("ExportPreset", p.Name, p.Title, p.Detail, p.Name == _exportPreset, p.Usable, p.Tip)).ToList();
        var languages = ExportChoices.Languages(project);
        if (!languages.Contains(_exportLanguage))
        {
            _exportLanguage = ExportChoices.EveryLanguage;
        }

        ExportLanguages.ItemsSource = languages.Select(l => l == ExportChoices.EveryLanguage
            ? new PageChoice("ExportLanguage", l, l, null, l == _exportLanguage, true, "Every layer, whatever its language")
            : new PageChoice("ExportLanguage", l, l.ToUpperInvariant(), null, l == _exportLanguage, true,
                $"The layers in {l} and those without a language; clips whose sound is in another language are muted")).ToList();
        var subtitles = ExportChoices.Subtitles(project);
        _exportSrt.IntersectWith(subtitles.Select(s => s.GroupId));
        ExportSubtitles.ItemsSource = subtitles.Select(s => new PageChoice("ExportSrt", s.GroupId, s.Label, null, _exportSrt.Contains(s.GroupId), true,
            $"Also save the captions in '{s.GroupId}' as an .srt file next to the video, as export_srt writes it")).ToList();
        ExportLanguageRow.Visibility = languages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportSrtRow.Visibility = subtitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportCaptionsDivider.Visibility = languages.Count > 0 && subtitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportCaptionsSection.Visibility = languages.Count > 0 || subtitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var exports = Path.Combine(_document.Session.Directory, ProjectStore.ExportsFolder);
        ExportFolder.Text = ProjectList.Shorten(exports);
        ExportFolder.ToolTip = exports;
        QualityText.Text = _exportQuality;
        ShowExportSummary();
        ShowExportState();
        ExportFilesChanged();
        _ = ReadClaudeExports();
    }

    // The summary card: the project's picture, and what the export will be with these choices.
    private void ShowExportSummary()
    {
        if (_document is not { } document)
        {
            return;
        }

        var project = document.View;
        var preset = ExportChoices.Presets(project).First(p => p.Name == _exportPreset).Preset;
        ExportResolution.Text = $"{preset?.Width ?? project.Width}×{preset?.Height ?? project.Height}";
        ExportFps.Text = $"{project.Fps} fps";
        ExportDuration.Text = Timecode.Short(project.Duration, tenths: true);
        ExportLength.Text = Timecode.Short(project.Duration);
        ExportCaptions.Text = ExportChoices.CaptionsSummary(project, ExportLanguage(), ExportChoices.Subtitles(project).Where(s => _exportSrt.Contains(s.GroupId)).ToList());
        var directory = document.Session.Directory;
        ExportPicture.Background = PictureOf(new ProjectEntry(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), directory, "", DateTime.Now)
        {
            Revision = document.Revision,
            Width = project.Width,
            Height = project.Height,
            Empty = project.Layers.Count == 0,
            Length = ExportLength.Text,
        });
    }

    private string? ExportLanguage() => _exportLanguage == ExportChoices.EveryLanguage ? null : _exportLanguage;

    // The export this window started last: its card while it runs, what it made when it's done.
    private void ShowExportState()
    {
        var job = _export?.Job;
        var running = job is { State: ExportState.Running };
        ExportButton.IsEnabled = !running;
        ExportName.IsEnabled = !running;
        OwnExport.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (job is null)
        {
            return;
        }

        if (running)
        {
            OwnExportName.Text = Path.GetFileName(job.OutputPath);
            OwnExportStatus.Text = $"{(job.FramesDone >= job.TotalFrames ? "Finishing" : "Rendering")} · {job.Percent.ToString("0", CultureInfo.InvariantCulture)}%";
            OwnExportBy.Text = "Started here" + JobTime.LeftText(job.Percent / 100, DateTime.UtcNow - job.StartedUtc);
            ExportProgress.Value = job.Percent;
        }

        ExportStatus.Foreground = (Brush)FindResource(job.State == ExportState.Failed ? "ErrorBrush" : "SecondaryTextBrush");
        ExportStatus.Text = ExportText(job, _export!.Version, _exportSrtNote);
    }

    // What the export says once it's done (its card shows it while it runs), with the language
    // version's warnings.
    private static string ExportText(ExportJob job, LanguageVersion version, string? subtitles)
    {
        var name = Path.GetFileName(job.OutputPath);
        var elapsed = (job.FinishedUtc ?? DateTime.UtcNow) - job.StartedUtc;
        var lines = new List<string>();
        switch (job.State)
        {
            case ExportState.Running:
                break;
            case ExportState.Completed:
                // By its name: the view shows the exports folder it's in.
                lines.Add($"Saved {name}{ExportSummary.Describe(job.Result!)[job.Result!.OutputPath.Length..]}");
                if (subtitles is not null)
                {
                    lines.Add(subtitles);
                }

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

    // Claude's exports of this project that ZeyoCut servers run now, read from the jobs folder off
    // the UI thread, read-only; when one is gone, it's done, and the files are listed again.
    private async Task ReadClaudeExports()
    {
        if (_serverJobs is not { } store || _document is not { } document || _readingClaudeExports)
        {
            return;
        }

        _readingClaudeExports = true;
        _claudeExportsRead = DateTime.UtcNow;
        var directory = document.Session.Directory;
        IReadOnlyList<RunningExport> exports;
        try
        {
            exports = await Task.Run(() => ExportChoices.ServerExports(store.Running(), directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return;
        }
        finally
        {
            _readingClaudeExports = false;
        }

        if (_document?.Session.Directory != directory || exports.SequenceEqual(_claudeExports))
        {
            return;
        }

        var done = _claudeExports.Any(e => exports.All(x => x.Name != e.Name));
        _claudeExports = exports;
        ExportClaude.ItemsSource = exports;
        if (done)
        {
            ExportFilesChanged();
        }
    }

    // Saves the .srt files chosen for the export that's done, as export_srt writes them.
    private void SaveSubtitles(ExportJob job)
    {
        if (_exportSrtFiles.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var (name, cues) in _exportSrtFiles)
            {
                CaptionBuilder.WriteSrt(job.ProjectDirectory, name, cues);
            }

            _exportSrtNote = $"Saved {string.Join(", ", _exportSrtFiles.Select(f => f.Name))} next to it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _exportSrtNote = $"Couldn't save the .srt files: {ex.Message}";
        }

        _exportSrtFiles = [];
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

            ShowExportSummary();
        }
    }

    private void OnSrtChoice(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: PageChoice choice } box)
        {
            if (box.IsChecked == true)
            {
                _exportSrt.Add(choice.Name);
            }
            else
            {
                _exportSrt.Remove(choice.Name);
            }

            ShowExportSummary();
        }
    }

    private void OnQualityMenu(object sender, RoutedEventArgs e) =>
        ShowChoices(QualityButton, ExportChoices.Qualities.Select(q => ($"{q.Name} · {q.Tip}", q.Name == _exportQuality, (Action)(() =>
        {
            _exportQuality = q.Name;
            QualityText.Text = q.Name;
        }))));

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
            var quality = ExportChoices.Qualities.First(q => q.Name == _exportQuality);

            // The .srt files' cues come from the snapshot before the language version is made of it.
            var snapshot = document.Session.Snapshot();
            var subtitles = ExportChoices.Subtitles(snapshot).Where(s => _exportSrt.Contains(s.GroupId));
            var srt = ExportChoices.SrtFiles(name, subtitles).Select(f => (f.Name, CaptionBuilder.GroupCues(snapshot, f.GroupId))).ToList();
            _export = _exportStart.Start(_exports, snapshot, directory, name, quality.Crf, preset, ExportLanguage(), null);
            _exportSrtFiles = srt;
            _exportSrtNote = null;
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

    // Plays an exported file in the default video player.
    private void OnPlayExportFile(object sender, RoutedEventArgs e)
    {
        var path = (string)((FrameworkElement)sender).Tag;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Message($"Couldn't play {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private void OnShowExportsFolder(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var folder = Path.Combine(_document.Session.Directory, ProjectStore.ExportsFolder);
        if (!Directory.Exists(folder))
        {
            Message("The exports folder isn't there yet; the first export makes it.");
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Message($"Couldn't open File Explorer: {ex.Message}");
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

    // Shows a view over the window (Keyboard shortcuts, New project, Relink) with the keyboard in it.
    // Playback stops; while it's open, Esc is the window's only key (RunKey).
    private void ShowDialog(Border dialog, IInputElement focus)
    {
        Pause();
        if (DialogLayer.Visibility != Visibility.Visible)
        {
            _focusBefore = Keyboard.FocusedElement;
        }

        foreach (var view in new[] { ShortcutsDialog, NewProjectDialog, RelinkDialog })
        {
            view.Visibility = ReferenceEquals(view, dialog) ? Visibility.Visible : Visibility.Collapsed;
        }

        DialogLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Keyboard.Focus(focus));
    }

    private void CloseDialog()
    {
        _relinkSearch?.Cancel();
        _relinkSearch = null;
        DialogLayer.Visibility = ShortcutsDialog.Visibility = NewProjectDialog.Visibility = RelinkDialog.Visibility = Visibility.Collapsed;
        if (_focusBefore is UIElement { IsVisible: true } before)
        {
            Keyboard.Focus(before);
        }

        _focusBefore = null;
    }

    private void OnCloseDialog(object sender, RoutedEventArgs e) => CloseDialog();

    // The Keyboard shortcuts view: the table the keys run from, in its groups, searched as you type.
    private void ShowShortcuts()
    {
        ShortcutSearch.Text = "";
        ShowShortcutGroups();
        ShowDialog(ShortcutsDialog, ShortcutSearch);
    }

    private void ShowShortcutGroups()
    {
        var search = ShortcutSearch.Text.Trim();
        var groups = Shortcuts.Groups(search);
        ShortcutGroups.ItemsSource = groups;
        ShortcutSearchHint.Visibility = ShortcutSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsNone.Text = $"No action or key has “{search}” in it.";
        ShortcutsNone.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShortcutSearch(object sender, TextChangedEventArgs e) => ShowShortcutGroups();

    // The New project view, with a canvas card chosen (a Home card's, else YouTube), a name no project
    // in the projects folder has yet, and the library's templates to start from.
    private void ShowNewProject(string? canvas)
    {
        _fillingNew = true;
        NewName.Text = NewProjectChoices.FreeName(_settings.ProjectsRoot);
        _newStarts = NewProjectChoices.Starts(new Library(_settings.LibraryPath ?? Settings.DefaultLibraryPath));
        ChooseStart(_newStarts[0]);
        NewFps30.IsChecked = true;
        NewBlack.IsChecked = true;
        NewFolder.Text = ProjectList.Shorten(_settings.ProjectsRoot);
        NewFolder.ToolTip = _settings.ProjectsRoot;
        NewError.Visibility = Visibility.Collapsed;
        _fillingNew = false;
        ChooseCanvas(NewProjectChoices.Canvases.FirstOrDefault(c => c.Name == canvas) ?? NewProjectChoices.Canvases[0], fill: true);
        NewName.SelectAll();
        ShowDialog(NewProjectDialog, NewName);
    }

    private void OnNewProjectCard(object sender, RoutedEventArgs e) => ShowNewProject(((FrameworkElement)sender).Tag as string);

    // A canvas card chosen: its size goes in the size fields (fill), Custom keeps what they have.
    private void ChooseCanvas(CanvasChoice canvas, bool fill)
    {
        _newCanvas = canvas;
        NewCanvases.ItemsSource = NewProjectChoices.Canvases.Select(c => c with { Selected = c.Name == canvas.Name }).ToList();
        if (fill && canvas.Width is { } width && canvas.Height is { } height)
        {
            _fillingNew = true;
            NewWidth.Text = width.ToString(CultureInfo.InvariantCulture);
            NewHeight.Text = height.ToString(CultureInfo.InvariantCulture);
            _fillingNew = false;
        }

        ShowNewPreview();
    }

    private void OnNewCanvas(object sender, RoutedEventArgs e)
    {
        var canvas = (CanvasChoice)((FrameworkElement)sender).Tag;
        ChooseCanvas(canvas, fill: true);
        if (canvas.Width is null)
        {
            Keyboard.Focus(NewWidth);
            NewWidth.SelectAll();
        }
    }

    // A size typed in: the card of that size is chosen, or Custom.
    private void OnNewSize(object sender, TextChangedEventArgs e)
    {
        if (_fillingNew)
        {
            return;
        }

        var canvas = NewSize() is { } size ? NewProjectChoices.CanvasOf(size.Width, size.Height) : NewProjectChoices.Canvases[^1];
        if (canvas.Name != _newCanvas.Name)
        {
            ChooseCanvas(canvas, fill: false);
        }
        else
        {
            ShowNewPreview();
        }
    }

    private void OnNewChoice(object sender, RoutedEventArgs e)
    {
        if (!_fillingNew)
        {
            ShowNewPreview();
        }
    }

    private void OnNewStartMenu(object sender, RoutedEventArgs e) =>
        ShowChoices(NewStartButton, _newStarts.Select(s => (s.Label, s == _newStart, (Action)(() => ChooseStart(s)))));

    // What the project starts from: a template has its own canvas, frame rate and background.
    private void ChooseStart(StartChoice start)
    {
        _newStart = start;
        NewStartText.Text = start.Label;
        NewCanvases.IsEnabled = NewSizePanel.IsEnabled = NewFpsPanel.IsEnabled = NewBackgroundPanel.IsEnabled = start.Template is null;
        if (!_fillingNew)
        {
            ShowNewPreview();
        }
    }

    // The size typed in, in pixels; null when it isn't one.
    private (int Width, int Height)? NewSize() =>
        int.TryParse(NewWidth.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
        && int.TryParse(NewHeight.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var height)
        && width > 0 && height > 0
            ? (width, height)
            : null;

    private int NewFps() =>
        new[] { NewFps24, NewFps25, NewFps30, NewFps60 }.FirstOrDefault(b => b.IsChecked == true) is { Tag: string fps }
            ? int.Parse(fps, CultureInfo.InvariantCulture)
            : 30;

    private string NewBackground() =>
        new[] { NewBlack, NewWhite, NewSky, NewTransparent }.FirstOrDefault(b => b.IsChecked == true)?.Tag as string ?? "#000000";

    // The preview: the canvas's shape in its background, with the safe area check_project keeps text
    // inside, its size and frame rate, and the export preset it fits.
    private void ShowNewPreview()
    {
        var fps = NewFps();
        if (_newStart?.Template is { } template)
        {
            NewPreviewCanvas.Visibility = Visibility.Hidden;
            NewShape.Text = $"The template {template} has its own canvas, frame rate and background.";
            NewExportNote.Text = "";
            return;
        }

        if (NewSize() is not { } size)
        {
            NewPreviewCanvas.Visibility = Visibility.Hidden;
            NewShape.Text = "Type the width and height in pixels.";
            NewExportNote.Text = "";
            return;
        }

        var (width, height) = size;
        var scale = Math.Min(210.0 / width, 210.0 / height);
        NewPreviewCanvas.Visibility = Visibility.Visible;
        NewPreviewCanvas.Width = width * scale;
        NewPreviewCanvas.Height = height * scale;
        NewPreviewFill.Background = NewTransparent.IsChecked == true
            ? (Brush)FindResource("CheckerBrush")
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(NewBackground()));
        var (left, top, right, bottom) = ProjectChecker.SafeArea(new Project { Name = "preview", Width = width, Height = height, Fps = fps, Duration = 1 });
        NewPreviewSafe.Margin = new Thickness(left * scale, top * scale, (width - right) * scale, (height - bottom) * scale);
        NewShape.Text = NewProjectChoices.Shape(width, height, fps);
        NewExportNote.Text = NewProjectChoices.ExportNote(width, height, fps);
    }

    private void OnCreateProject(object sender, RoutedEventArgs e) => CreateProject();

    private void OnNewProjectKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateProject();
            e.Handled = true;
        }
    }

    // Makes the project the New project view describes, in the projects folder, and opens it; or says
    // why it can't.
    private void CreateProject()
    {
        var template = _newStart?.Template;
        var size = NewSize();
        if (template is null && size is null)
        {
            ShowNewError("Type the width and height as whole numbers of pixels.");
            return;
        }

        var (width, height) = size ?? (0, 0);
        string folder;
        IReadOnlyList<string> warnings;
        try
        {
            (folder, warnings) = NewProjectChoices.Create(_settings, new NewProjectRequest(NewName.Text, template, width, height, NewFps(), NewBackground()));
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
        {
            ShowNewError($"Couldn't create the project: {ex.Message}");
            return;
        }

        CloseDialog();
        OpenProject(folder);
        if (warnings.Count > 0)
        {
            Message($"Created {Path.GetFileName(folder)}; {string.Join("; ", warnings)}");
        }
    }

    private void ShowNewError(string text)
    {
        NewError.Text = text;
        NewError.Visibility = Visibility.Visible;
    }

    // The Relink view for assets whose files are missing: when a project opens with some, or for one
    // from its tile's menu. It looks for each in the project folder, then the import folders, and
    // offers what it finds; Choose file and Search another folder offer more.
    private void ShowRelink(IReadOnlyList<Asset> assets, bool opening)
    {
        var document = _document!;
        var project = document.View;
        var name = Path.GetFileName(document.Session.Directory);
        _missing = assets.Select(a => new MissingFile(a, ProjectChecker.Users(project, a.Id).Count())).ToList();
        foreach (var file in _missing)
        {
            file.PropertyChanged += (_, _) => ShowRelinkButton();
        }

        RelinkList.ItemsSource = _missing;
        RelinkTitle.Text = _missing.Count == 1 ? $"1 file in {name} can’t be found" : $"{_missing.Count} files in {name} can’t be found";
        RelinkSubtitle.Text = _missing.Count == 1
            ? "It was moved or renamed, and what uses it can’t be drawn or exported until you relink it."
            : "They were moved or renamed, and what uses them can’t be drawn or exported until you relink them.";
        RelinkCancel.Content = opening ? "Open without relinking" : "Cancel";
        RelinkButton.Content = opening ? "Relink and open" : "Relink";
        ShowRelinkButton();
        ShowDialog(RelinkDialog, RelinkCancel);
        _relinkSearch?.Cancel();
        _relinkSearch = new CancellationTokenSource();
        _ = SearchMissing(Relinking.Folders(_settings, document.Session.Directory), null, _relinkSearch.Token);
    }

    // Looks in the folders, in order, for each missing file, and offers what each folder has as soon
    // as it's searched. 'picked' names a folder you chose, for what a file it has nothing for says.
    private async Task SearchMissing(IReadOnlyList<string> folders, string? picked, CancellationToken cancel)
    {
        var project = _document!.View;
        var probe = VideoImport.AsIsProbe(Ffmpeg);
        var files = _missing.ToList();
        foreach (var folder in folders)
        {
            foreach (var file in files)
            {
                if (project.FindAsset(file.Id) is not { } asset)
                {
                    continue;
                }

                List<(RelinkMatch Match, ImageSource? Picture)> found;
                try
                {
                    found = await Task.Run(() => AssetRelinker.Matches(project, asset, [folder], probe, cancel)
                        .Select(m => (m, Relinking.Picture(m.Path, asset.Type))).ToList(), cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ZeyoCutException)
                {
                    file.Show($"Couldn't search {folder}: {ex.Message}", failed: true);
                    continue;
                }

                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                foreach (var (match, picture) in found)
                {
                    file.Offer(new RelinkCandidate(match.Path, match.Reason, picture), choose: false);
                }
            }
        }

        foreach (var file in files)
        {
            file.Searched(picked ?? "the project folder or the import folders");
        }
    }

    private void OnRelinkFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Search a folder for the missing files" };
        if (dialog.ShowDialog(this) != true || _relinkSearch is not { } search)
        {
            return;
        }

        foreach (var file in _missing.Where(f => !f.Found))
        {
            file.Show("Looking for it…");
        }

        _ = SearchMissing([dialog.FolderName], dialog.FolderName, search.Token);
    }

    private void OnRelinkFile(object sender, RoutedEventArgs e)
    {
        var file = (MissingFile)((FrameworkElement)sender).Tag;
        var dialog = new OpenFileDialog { Title = $"Choose the file for '{file.Id}'", Filter = Relinking.Filter(file.Type) };
        if (dialog.ShowDialog(this) == true)
        {
            RelinkTo(file.Id, dialog.FileName);
        }
    }

    // A file chosen for a missing one (Choose file): offered and chosen, or why it can't be used.
    public void RelinkTo(string assetId, string path)
    {
        if (_missing.FirstOrDefault(f => f.Id == assetId) is not { } file || _document is null)
        {
            return;
        }

        try
        {
            AssetRelinker.Check(_document.View, assetId, path);
        }
        catch (ZeyoCutException ex)
        {
            file.Show(ex.Message, failed: true);
            return;
        }

        file.Offer(new RelinkCandidate(path, "Chosen", Relinking.Picture(path, file.Type)), choose: true);
    }

    private void OnRelinkKeep(object sender, RoutedEventArgs e)
    {
        var file = (MissingFile)((FrameworkElement)sender).Tag;
        file.Choose(null);
        file.Show("Stays missing for now");
    }

    private void OnRelinkCandidate(object sender, RoutedEventArgs e)
    {
        var candidate = (RelinkCandidate)((FrameworkElement)sender).Tag;
        _missing.FirstOrDefault(f => f.Candidates.Contains(candidate))?.Choose(candidate);
    }

    private void ShowRelinkButton() => RelinkButton.IsEnabled = _missing.Any(f => f.Choice is not null);

    // Relinks each missing file to its choice: the files are copied into the project's assets folder
    // off the window's thread, then the relinks are saved as one step. A file that can't be used says
    // why, and the view stays open for it.
    private async void OnRelink(object sender, RoutedEventArgs e)
    {
        var document = _document;
        var chosen = _missing.Where(f => f.Choice is not null).Select(f => (File: f, Path: f.Choice!.Path)).ToList();
        if (document is null || chosen.Count == 0)
        {
            return;
        }

        RelinkButton.IsEnabled = false;
        var directory = document.Session.Directory;
        var probe = VideoImport.AsIsProbe(Ffmpeg);
        var prepared = await Task.Run(() => chosen.Select(c =>
        {
            try
            {
                return (c.File, Asset: AssetImporter.Prepare(directory, c.Path, probe, c.File.Type), Error: (string?)null);
            }
            catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
            {
                return (c.File, Asset: (PreparedAsset?)null, Error: ex.Message);
            }
        }).ToList());
        if (!ReferenceEquals(document, _document) || RelinkDialog.Visibility != Visibility.Visible)
        {
            return;
        }

        foreach (var failed in prepared.Where(p => p.Error is not null))
        {
            failed.File.Show(failed.Error!, failed: true);
        }

        var ready = prepared.Where(p => p.Asset is not null).ToList();
        if (ready.Count == 0)
        {
            ShowRelinkButton();
            return;
        }

        EditOperation operation = ready.Count == 1
            ? new RelinkAssetOperation(ready[0].File.Id, ready[0].Asset!)
            : new BatchOperation($"relink_asset {string.Join(", ", ready.Select(r => r.File.Id))}",
                ready.Select(r => (EditOperation)new RelinkAssetOperation(r.File.Id, r.Asset!)).ToList());
        Apply(operation, then: (saved, _) =>
        {
            if (saved)
            {
                _missing = _missing.Where(f => !ready.Any(r => ReferenceEquals(r.File, f))).ToList();
                RelinkList.ItemsSource = _missing;
                if (_missing.All(f => !f.Failed))
                {
                    CloseDialog();
                    return;
                }
            }

            ShowRelinkButton();
        });
    }

    // The Welcome view, the first time the editor starts: the projects folder, connecting Claude and
    // the sample project, from the Connect step.
    private void ShowWelcome()
    {
        _welcomeSince = DateTimeOffset.Now;
        _welcomeStep = 1;
        _welcomeShown = null;
        var server = Welcome.ServerPath(AppContext.BaseDirectory);
        WelcomeFolder.Text = _settings.ProjectsRoot;
        WelcomeConfig.Text = Welcome.Config(server);
        WelcomeServerNote.Visibility = File.Exists(server) ? Visibility.Collapsed : Visibility.Visible;
        WelcomePage.Visibility = Visibility.Visible;
        _welcomeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowWelcomeStep(), Dispatcher);
        ShowWelcomeStep();
    }

    // The Welcome's steps and the card of the one shown. The Connect step turns green once a client
    // lists the server's tools after the view opened (the server notes it: ClientConnection).
    private void ShowWelcomeStep()
    {
        var last = ClientConnection.Read(_settings.LogDirectory);
        var connected = Welcome.Connected(last, _welcomeSince);
        var status = Welcome.Status(last, _welcomeSince);
        var earlier = Welcome.Earlier(last, _welcomeSince);
        var shown = (_welcomeStep, status, earlier);
        if (Equals(_welcomeShown, shown))
        {
            return;
        }

        _welcomeShown = shown;
        var sample = Welcome.Sample(AppContext.BaseDirectory);
        WelcomeSteps.ItemsSource = new List<WelcomeStep>
        {
            new(1, "Choose a projects folder", ProjectList.Shorten(_settings.ProjectsRoot), _welcomeStep == 0, true),
            new(2, "Connect Claude", connected ? status : "One line in Claude’s settings", _welcomeStep == 1, connected),
            new(3, "Open a sample project", sample is null ? "It isn't next to the editor" : $"{Welcome.SampleName}, ready to tweak", _welcomeStep == 2, false),
        };
        WelcomeStepText.Text = $"STEP {_welcomeStep + 1} OF 3";
        WelcomeHeading.Text = _welcomeStep switch
        {
            0 => "Your projects folder",
            1 => "Connect Claude",
            _ => "Open a sample project",
        };
        WelcomeFolderStep.Visibility = _welcomeStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeConnectStep.Visibility = _welcomeStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeSampleStep.Visibility = _welcomeStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeStatus.Visibility = _welcomeStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeStatus.Background = connected ? (Brush)FindResource("YouBrush") : WaitingBrush;
        WelcomeStatusDot.Fill = (Brush)FindResource(connected ? "SuccessBrush" : "WarningBrush");
        WelcomeStatusText.Text = status;
        WelcomeStatusText.Foreground = (Brush)FindResource(connected ? "YouTextBrush" : "WarningBrush");
        WelcomeEarlier.Text = earlier ?? "";
        WelcomeEarlier.Visibility = earlier is null ? Visibility.Collapsed : Visibility.Visible;
        WelcomeSampleText.Text = sample is null
            ? $"The sample project isn't next to the editor. It comes with the release zip and the bundle, in examples\\{Welcome.SampleName}."
            : $"{Welcome.SampleName} is a cutout character rig: squash and stretch, easing, a drawing swap, a lagging head and a wagging tail, sound effects and a Japanese subtitle. "
                + "It's copied into your projects folder and opened, so the one next to the editor stays as it is. Look around, then ask Claude to change it.";
        WelcomeError.Visibility = Visibility.Collapsed;
        WelcomeBack.IsEnabled = _welcomeStep > 0;
        WelcomeNext.Content = _welcomeStep < 2 ? "Continue" : sample is null ? "Go to my projects" : $"Open {Welcome.SampleName}";
    }

    // Leaving the Welcome view: the editor's settings are saved, so it doesn't open again.
    private void CloseWelcome()
    {
        if (WelcomePage.Visibility != Visibility.Visible)
        {
            return;
        }

        _welcomeTimer?.Stop();
        _welcomeTimer = null;
        WelcomePage.Visibility = Visibility.Collapsed;
        SavePrefs();
    }

    private void OnSkipWelcome(object sender, RoutedEventArgs e) => CloseWelcome();

    private void OnWelcomeStep(object sender, RoutedEventArgs e)
    {
        _welcomeStep = ((WelcomeStep)((FrameworkElement)sender).Tag).Number - 1;
        ShowWelcomeStep();
    }

    private void OnWelcomeBack(object sender, RoutedEventArgs e)
    {
        _welcomeStep = Math.Max(0, _welcomeStep - 1);
        ShowWelcomeStep();
    }

    // Continue; on the last step, the sample is copied into the projects folder and opened.
    private void OnWelcomeNext(object sender, RoutedEventArgs e)
    {
        if (_welcomeStep < 2)
        {
            _welcomeStep++;
            ShowWelcomeStep();
            return;
        }

        if (Welcome.Sample(AppContext.BaseDirectory) is not { } sample)
        {
            CloseWelcome();
            return;
        }

        string copy;
        try
        {
            copy = Welcome.CopySample(sample, _settings.ProjectsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WelcomeError.Text = $"Couldn't copy the sample into {_settings.ProjectsRoot}: {ex.Message}";
            WelcomeError.Visibility = Visibility.Visible;
            return;
        }

        CloseWelcome();
        OpenProject(copy);
    }

    private void OnCopyConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(WelcomeConfig.Text);
            WelcomeCopy.Content = "Copied";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            WelcomeError.Text = $"Couldn't copy it: {ex.Message}";
            WelcomeError.Visibility = Visibility.Visible;
        }
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
