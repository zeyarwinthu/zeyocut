// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
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
using ZeyoCut.Render.Text;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Editor;

// One asset as the media library lists it: a tile with its thumbnail or its sound's waveform
// (Picture, once it's made), a dot in its kind's color (Badge), and a video's or sound's length.
// The Media browser's cards add its size or length (Meta) and how often it's used (Use).
public sealed record MediaItem(string Id, string Kind, Brush Badge, string Details, bool Missing)
{
    public Brush? Picture { get; init; }

    public string? Length { get; init; }

    public string Tip { get; init; } = "";

    public string Meta { get; init; } = "";

    public string Use { get; init; } = "";
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
    private static readonly Brush MissingTileBrush = Frozen(0x2A, 0x1A, 0x1A);

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

    // The roles soloed in the Audio view: the preview hears only them. Never saved.
    private readonly HashSet<AudioRole> _solo = [];
    private (Project From, string Solo, Project Heard)? _heard;

    // The Master meter's peaks of the mix the preview plays, and the mix they're being read from.
    private (string Path, float[] Peaks)? _meter;
    private string? _meterWanted;

    // The Audio view's loudness measurement: running, and the last one with the mix it measured.
    private CancellationTokenSource? _loudness;
    private (string? Key, AudioLevelsReport? Report, string? Error)? _measured;
    private bool _settingDuck;
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

    // The Media browser's shelf, its order, and the ids of the media selected in its grid.
    private string _shelf = MediaBrowser.AllKey;
    private MediaSort _mediaSort = MediaSort.Recent;
    private List<string> _browserSelection = [];

    // The source viewer: its player while a file is open in it, the in and out marks (frames of the
    // file), whether footage's sound is used, the lane its clips go on, and its picture.
    private SourceMonitor? _source;
    private int? _sourceIn;
    private int? _sourceOut;
    private bool _sourceSound = true;
    private string? _sourceTarget;
    private WriteableBitmap? _sourceBitmap;

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

    // The Captions view: its grid as last built and the project it was built from, the track picked
    // as the source, the new row Add caption at playhead made (saved once something is typed into
    // it), the cell saved last (not saved again as it loses the keyboard), the .srt files saved last,
    // the .srt file being imported, and whether the style section is being filled in.
    private CaptionTable? _captionTable;
    private Project? _captionProject;
    private string? _captionSource;
    private (double Start, double End)? _captionDraft;
    private (CaptionTable Table, string Cell, string Text)? _captionSent;
    private IReadOnlyList<string> _captionSaved = [];
    private (string Path, List<CueEntry> Cues, List<string> Problems)? _importSrt;
    private bool _fillingCaptions;

    // The Animate view's onion skin, made when first needed, the picture of it asked for last and the
    // one shown; and the selected layer's motion path, of the project it was found in.
    private OnionSkin? _onion;
    private OnionRequest? _onionAsked;
    private BitmapSource? _onionPicture;
    private (Project Project, string? Layer)? _pathFor;
    private (IReadOnlyList<Point> Path, IReadOnlyList<Point> Keys) _path = ([], []);

    // The project as the preview shows it (Shown), the project it's made of, and its language.
    private Project? _shown;
    private Project? _shownFrom;
    private string? _shownLanguage;

    // Measures captions' text (Measurer), made when first needed.
    private FrameRenderer? _measure;

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
        VolumeLanes.Waveform = Timeline.Waveform;
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
        _prefs.OnionBefore = Math.Clamp(_prefs.OnionBefore, 0, 5);
        _prefs.OnionAfter = Math.Clamp(_prefs.OnionAfter, 0, 5);
        OnionToggle.IsChecked = _prefs.OnionSkin;
        MotionPathToggle.IsChecked = _prefs.MotionPath;
        ShowOnionText();
        ShowBezier([0, 0, 0.4, 1]);
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

    private bool Animating => _document is not null && AnimateMode.IsChecked == true;

    private bool Hearing => _document is not null && AudioMode.IsChecked == true;

    private bool Browsing => _document is not null && MediaMode.IsChecked == true;

    // The source viewer is showing, with a file open in it.
    private bool Sourcing => _document is not null && SourceMode.IsChecked == true && _source is not null;

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

    // A click anywhere but the inspector field (or Captions cell) being typed in saves what was typed
    // there first.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (Keyboard.FocusedElement is TextBox { Tag: InspectorField or CaptionCell or CaptionRow or CaptionColumn } box && e.OriginalSource is Visual target && !box.IsAncestorOf(target))
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
        var animating = Animating;
        var hearing = Hearing;
        var playable = editing || animating || hearing;
        var source = Sourcing ? _source : null;
        return command switch
        {
            Command.Play => playable ? TogglePlay : source is not null ? source.Toggle : null,
            Command.PreviousFrame => playable ? () => Step(-1) : source is not null ? () => StepSource(-1) : null,
            Command.NextFrame => playable ? () => Step(1) : source is not null ? () => StepSource(1) : null,
            Command.BackSecond => playable ? () => Step(-_document!.View.Fps) : source is not null ? () => StepSource(-source.Source.Fps) : null,
            Command.ForwardSecond => playable ? () => Step(_document!.View.Fps) : source is not null ? () => StepSource(source.Source.Fps) : null,
            Command.FirstFrame => playable ? () => Seek(0) : source is not null ? () => source.Seek(0) : null,
            Command.LastFrame => playable ? () => Seek(_frames - 1) : source is not null ? () => source.Seek(source.Frames - 1) : null,
            Command.PreviousEdit => editing ? () => JumpToEdit(next: false) : null,
            Command.NextEdit => editing ? () => JumpToEdit(next: true) : null,
            Command.FullScreen => editing ? ShowFullScreen : null,
            Command.Split => editing ? SplitSelected : null,
            Command.Delete => editing ? DeleteSelected : animating && Keyframes.Selection.Count > 0 ? DeleteKeys : hearing && VolumeLanes.Selected is not null ? DeleteVolumeKey : null,
            Command.Duplicate => editing ? DuplicateSelected : null,
            Command.Undo => open && !inField ? Undo : null,
            Command.Redo => open && !inField ? Redo : null,
            Command.Import => editing || Browsing ? ChooseMedia : null,
            Command.Cancel => editing ? CancelHandler() : animating ? AnimateCancelHandler() : hearing && VolumeLanes.Dragging ? VolumeLanes.CancelDrag : null,
            Command.AddMarker => editing ? AddMarker : null,
            Command.Snap => editing ? ToggleSnap : null,
            Command.ZoomIn => editing ? () => Timeline.ZoomBy(1.5) : null,
            Command.ZoomOut => editing ? () => Timeline.ZoomBy(1 / 1.5) : null,
            Command.Fit => editing ? Timeline.ZoomToFit : null,
            Command.SelectTool => editing ? () => TurnOn(SelectTool) : null,
            Command.BladeTool => editing ? () => TurnOn(BladeTool.IsChecked == true ? SelectTool : BladeTool) : null,
            Command.MarkIn => source is not null ? MarkIn : null,
            Command.MarkOut => source is not null ? MarkOut : null,
            Command.ClearMarks => source is not null ? ClearMarks : null,
            Command.Insert => source is not null ? () => PlaceSource(ClipEdit.Insert) : null,
            Command.Overwrite => source is not null ? () => PlaceSource(ClipEdit.Overwrite) : null,
            Command.PreviousKeyframe => playable ? () => JumpToKeyframe(next: false) : null,
            Command.NextKeyframe => playable ? () => JumpToKeyframe(next: true) : null,
            Command.AddKeyframe => animating ? AddKeyframe : null,
            Command.EaseInOut => animating ? () => SetEasing(Easing.EaseInOut) : null,
            Command.OnionSkin => animating ? ToggleOnion : null,
            Command.Edit => open ? () => TurnOn(EditMode) : null,
            Command.Audio => open ? () => TurnOn(AudioMode) : null,
            Command.Animate => open ? () => TurnOn(AnimateMode) : null,
            Command.Captions => open ? () => TurnOn(CaptionsMode) : null,
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

    // Esc in the Animate view lets go of a drag of keyframes, or clears their selection.
    private Action? AnimateCancelHandler() =>
        Keyframes.Dragging ? Keyframes.CancelDrag
        : Keyframes.Selection.Count > 0 ? Keyframes.ClearSelection
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
        _solo.Clear();
        _heard = null;
        _meter = null;
        _meterWanted = null;
        _measured = null;
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
        _captionTable = null;
        _captionProject = null;
        _captionSource = null;
        _captionDraft = null;
        _captionSent = null;
        _captionSaved = [];
        CaptionSavedPanel.Visibility = Visibility.Collapsed;
        _shown = _shownFrom = null;
        _shownLanguage = null;
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
        CloseSource();
        _loudness?.Cancel();
        _loudness = null;
        _thumbnails?.Dispose();
        _thumbnails = null;
        _waveforms?.Dispose();
        _waveforms = null;
        _claim?.Dispose();
        _claim = null;
        if (_measure is { } measure)
        {
            // After the saves that may be measuring with it.
            _ = _saving.ContinueWith(_ => measure.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _measure = null;
        }

        CaptionRows.ItemsSource = null;
        _captionTable = null;
        _captionProject = null;
        if (_onion is { } onion)
        {
            _ = Task.Run(onion.Dispose);
        }

        _onion = null;
        _onionAsked = null;
        _onionPicture = null;
        _pathFor = null;
        _path = ([], []);
        _shown = _shownFrom = null;
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
        ShowLanguages();
        ShowPage();
        ShowShown();
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
            VolumeLanes.InvalidateVisual();
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
    // open; the Export view, the Media browser and the source viewer have a way back to Edit in place
    // of the tabs, and the Export view has it in place of the Export button too.
    private void ShowHeader()
    {
        var open = _document is not null;
        var exporting = open && ExportMode.IsChecked == true;
        var away = exporting || open && (MediaMode.IsChecked == true || SourceMode.IsChecked == true);
        LogoButton.IsEnabled = open;
        AppName.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Crumbs.Visibility = ProjectCommands.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ModeBar.Visibility = open && !away ? Visibility.Visible : Visibility.Collapsed;
        ExportCrumb.Visibility = BackToEdit.Visibility = away ? Visibility.Visible : Visibility.Collapsed;
        CrumbName.Text = exporting ? "Export" : MediaMode.IsChecked == true ? "Media" : "Source";
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

        _preview.Request(_preview.Source(Shown, 1), _frame);
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
        if (_preview.Cached(_preview.Source(Shown, 1), _frame) is null)
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
        if (CaptionsMode.IsChecked == true)
        {
            MarkCurrentCaption();
        }

        if (AnimateMode.IsChecked == true)
        {
            ShowAnimatePosition();
        }

        if (AudioMode.IsChecked == true)
        {
            ShowAudioPosition();
        }

        if (SourceMode.IsChecked == true)
        {
            ShowSource();
        }
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
        _preview.Request(_preview.Source(InShownLanguage(moved), 1), _frame);
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
        PlayIcon.Data = AudioPlayIcon.Data = (Geometry)FindResource("PauseShape");
        PlayButton.ToolTip = AudioPlayButton.ToolTip = "Pause (Space)";
        PreviewBusy.Visibility = Visibility.Collapsed;
        _drawingSince = null;
        StartPlayback();
        CompositionTarget.Rendering += OnPlaybackFrame;
    }

    // Draws ahead from the playhead, and starts the clock and the sound once the first frame ahead
    // is ready (or after a moment).
    private void StartPlayback()
    {
        _playSource = _preview!.Source(Shown, _prefs.PlaybackScale, _step);
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
        PlayIcon.Data = AudioPlayIcon.Data = (Geometry)FindResource("PlayShape");
        PlayButton.ToolTip = AudioPlayButton.ToolTip = "Play (Space)";
        MasterMeter.Level = (double.NegativeInfinity, double.NegativeInfinity);
        MasterPeak.Text = "-inf";
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

        if (AudioMode.IsChecked == true)
        {
            ShowMeter(_clock.Now);
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
            _playSource = _preview.Source(Shown, _prefs.PlaybackScale, _step);
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
        if (AudioMode.IsChecked == true)
        {
            RequestMeter();
        }

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
    // In the Animate view, the keyframes of the selected layer and the layers under it, or of all
    // the layers listed when none is selected.
    private void JumpToKeyframe(bool next)
    {
        var animating = AnimateMode.IsChecked == true;
        if (_selectedLayer is null && !animating)
        {
            Message("Select a clip to go to its keyframes");
            return;
        }

        var project = _document!.View;
        var times = animating ? AnimateKeyTimes() : Timeline.KeyTimes;
        var frames = times.Select(t => FrameMath.NearestFrame(project, t)).Distinct().Order().ToList();
        var target = next ? frames.Where(f => f > _frame).Cast<int?>().FirstOrDefault() : frames.Where(f => f < _frame).Cast<int?>().LastOrDefault();
        if (target is { } frame)
        {
            Seek(frame);
        }
        else if (_selectedLayer is null)
        {
            Message(frames.Count == 0 ? "There are no keyframes" : $"There's no keyframe {(next ? "after" : "before")} the playhead");
        }
        else
        {
            Message(frames.Count == 0 ? $"'{_selectedLayer}' has no keyframes" : $"'{_selectedLayer}' has no keyframe {(next ? "after" : "before")} the playhead");
        }
    }

    private List<double> AnimateKeyTimes()
    {
        var layers = _selectedLayer is { } id ? AnimateModel.WithChildren(_document!.View, id) : null;
        return Keyframes.Layers.Where(l => layers is null || layers.Contains(l.LayerId)).SelectMany(l => l.Rows).SelectMany(r => r.Keys).Select(k => k.Time).ToList();
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
        if (Animating)
        {
            ShowAnimate();
        }
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
        AnimatePage.Visibility = Visibility.Collapsed;
        AudioPage.Visibility = Visibility.Collapsed;
        CaptionsPage.Visibility = Visibility.Collapsed;
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

    // Double-clicking a video or a sound opens it in the source viewer.
    private void OnMediaOpened(object sender, MouseButtonEventArgs e)
    {
        if (_document is not null && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(MediaList, source) is ListBoxItem { DataContext: MediaItem { Missing: false } item }
            && _document.View.FindAsset(item.Id) is { Type: AssetType.Video or AssetType.Audio })
        {
            OpenSource(item.Id);
        }
    }

    private void OnMediaBrowser(object sender, RoutedEventArgs e) => MediaMode.IsChecked = true;

    // The Media browser: the shelves (each kind, and the bins, which are the assets' tags), the
    // shelf's media as cards, searched and sorted, and the selected media's details.
    private void ShowBrowser()
    {
        if (_document is not { } document)
        {
            return;
        }

        var project = document.View;
        var directory = document.Session.Directory;
        var shelf = MediaBrowser.Find(project, _shelf);
        _shelf = shelf.Key;
        var bins = MediaBrowser.Bins(project);
        ShowShelves(LibraryShelves, MediaBrowser.Library(project), shelf.Key);
        ShowShelves(BinShelves, bins, shelf.Key);
        BinsEmpty.Visibility = bins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var missing = MissingAssets();
        MissingCard.Content = missing.Count == 1 ? "1 file missing" : $"{missing.Count} files missing";
        MissingCard.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var search = BrowserSearch.Text.Trim();
        var items = MediaBrowser.Shown(project, shelf, search, _mediaSort).Select(a => Card(project, directory, a)).ToList();
        _rebuilding = true;
        try
        {
            BrowserList.ItemsSource = items;
            foreach (var item in items.Where(i => _browserSelection.Contains(i.Id)))
            {
                BrowserList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        _browserSelection = BrowserList.SelectedItems.Cast<MediaItem>().Select(i => i.Id).ToList();
        BrowserTitle.Text = shelf.Name;
        BrowserCount.Text = $" · {MediaBrowser.Items(items.Count)}";
        BrowserSortButton.Content = $"Sort: {MediaBrowser.SortName(_mediaSort)}";
        BrowserSearchHint.Visibility = BrowserSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowserEmpty.Text = project.Assets.Count == 0 ? "No media yet. Import images, audio, fonts or video, or ask Claude to."
            : search.Length > 0 ? $"Nothing in {shelf.Name} matches '{search}'."
            : $"Nothing in {shelf.Name} yet.";
        BrowserEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowBrowserDetails();
    }

    private List<Asset> MissingAssets() => _document!.View.Assets.Where(a => Item(_document.Session.Directory, a).Missing).ToList();

    // A card of the browser's grid: the media panel's tile with its size or length and its uses.
    private MediaItem Card(Project project, string directory, Asset asset)
    {
        var item = Item(directory, asset);
        var opens = asset.Type is AssetType.Video or AssetType.Audio ? "Double-click to open it in the source viewer" : "Add at playhead puts it on the timeline";
        return item with
        {
            Picture = item.Missing ? null : Picture(asset),
            Meta = MediaBrowser.Meta(asset),
            Use = MediaBrowser.Use(MediaBrowser.Uses(project, asset.Id), item.Missing),
            Tip = item.Missing ? item.Details : $"{MediaBrowser.Summary(asset)}\n{(asset.Type == AssetType.Font ? "A text's Font file field uses it" : opens)}",
        };
    }

    // A button for each shelf: a dot in its kind's color, or a folder for a bin, its name and how
    // many files it has. A bin's menu removes it.
    private void ShowShelves(Panel panel, IReadOnlyList<MediaShelf> shelves, string chosen)
    {
        panel.Children.Clear();
        foreach (var shelf in shelves)
        {
            var row = new DockPanel();
            var count = new TextBlock { Text = shelf.Count.ToString(CultureInfo.InvariantCulture), FontSize = 12, Foreground = (Brush)FindResource("SecondaryTextBrush"), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(shelf.Tag is null
                ? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = ShelfColor(shelf.Kind), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }
                : new Icon { Data = (Geometry)FindResource("FolderShape"), Width = 14, Height = 14, Margin = new Thickness(-3, 0, 7, 0) });
            row.Children.Add(new TextBlock { Text = shelf.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new RadioButton { Style = (Style)FindResource("ShelfButton"), GroupName = "Shelf", Content = row, Tag = shelf.Key, IsChecked = shelf.Key == chosen };
            AutomationProperties.SetName(button, shelf.Name);
            button.Checked += OnShelf;
            if (shelf.Tag is { } tag)
            {
                button.ToolTip = $"The media tagged '{tag}'. Right-click to remove the bin";
                button.MouseRightButtonUp += (_, e) => ShowBinMenu(button, tag, e);
            }

            panel.Children.Add(button);
        }
    }

    private Brush ShelfColor(AssetType? kind) => kind switch
    {
        AssetType.Video => VideoBadge,
        AssetType.Image => ImageBadge,
        AssetType.Audio => AudioBadge,
        AssetType.Font => FontBadge,
        _ => (Brush)FindResource("TextBrush"),
    };

    private void OnShelf(object sender, RoutedEventArgs e)
    {
        _shelf = (string)((FrameworkElement)sender).Tag;
        Dispatcher.BeginInvoke(ShowBrowser);
    }

    // Remove bin takes its tag off every file in it; the files stay in the project.
    private void ShowBinMenu(FrameworkElement target, string tag, MouseButtonEventArgs e)
    {
        var ids = _document!.View.Assets.Where(a => AssetTags.Has(a, tag)).Select(a => a.Id).ToList();
        var menu = new ContextMenu { PlacementTarget = target };
        var remove = new MenuItem { Header = $"Remove bin '{tag}'", ToolTip = "Takes the tag off its files; the files stay in the project" };
        remove.Click += (_, _) => Apply(new TagAssetsOperation(ids, [], [tag]));
        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnBrowserSearch(object sender, TextChangedEventArgs e) => ShowBrowser();

    private void OnBrowserSort(object sender, RoutedEventArgs e) =>
        ShowChoices(BrowserSortButton, Enum.GetValues<MediaSort>().Select(sort => (MediaBrowser.SortName(sort), sort == _mediaSort, (Action)(() =>
        {
            _mediaSort = sort;
            ShowBrowser();
        }))));

    // As many cards across as fit at 170 px or wider.
    private void OnBrowserSized(object sender, SizeChangedEventArgs e) => BrowserList.Tag = Math.Max(1, (int)((e.NewSize.Width + 14) / (170 + 14)));

    private void OnBrowserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding)
        {
            return;
        }

        _browserSelection = BrowserList.SelectedItems.Cast<MediaItem>().Select(i => i.Id).ToList();
        ShowBrowserDetails();
    }

    private List<Asset> BrowserAssets() => _document is { } document ? _browserSelection.Select(document.View.FindAsset).OfType<Asset>().ToList() : [];

    // The details column: one file's picture, kind, uses, size, place and tags, or several files'
    // kinds and tags; and what can be done with them.
    private void ShowBrowserDetails()
    {
        var assets = BrowserAssets();
        BrowserDetails.Visibility = assets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowserNoSelection.Visibility = assets.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        NewBinButton.IsEnabled = assets.Count > 0;
        NewBinButton.ToolTip = assets.Count > 0 ? "Put the selected media in a new bin" : "Select media in the grid to put it in a new bin";
        if (assets.Count == 0)
        {
            TagBox.Visibility = Visibility.Collapsed;
            return;
        }

        var document = _document!;
        var single = assets.Count == 1 ? assets[0] : null;
        var details = single is null ? MediaBrowser.Details(assets) : MediaBrowser.Details(document.View, document.Session.Directory, single);
        var missing = single is not null && Item(document.Session.Directory, single).Missing;
        DetailTitle.Text = details.Title;
        DetailTitle.ToolTip = details.Title;
        DetailSummary.Text = details.Summary;
        DetailRows.Visibility = single is null ? Visibility.Collapsed : Visibility.Visible;
        DetailUsedBy.Text = details.UsedBy;
        DetailSize.Text = details.FileSize;
        DetailSize.Foreground = (Brush)FindResource(missing ? "ErrorBrush" : "TextBrush");
        DetailLocation.Text = details.Location;
        DetailLocation.ToolTip = details.Path;
        DetailFrame.Background = single switch
        {
            null => (Brush)FindResource("ControlBrush"),
            _ when missing => MissingTileBrush,
            { Type: AssetType.Image } => (Brush)FindResource("CheckerBrush"),
            { Type: AssetType.Audio } => (Brush)FindResource("SoundTileBrush"),
            _ => (Brush)FindResource("ControlBrush"),
        };
        DetailPicture.Background = single is null || missing ? null : Picture(single);
        DetailPicture.Margin = new Thickness(single is { Type: AssetType.Video } ? 0 : 8);
        DetailFont.Visibility = single is { Type: AssetType.Font } && !missing ? Visibility.Visible : Visibility.Collapsed;
        ShowTags(details.Tags);

        var time = FrameMath.TimeOf(document.View, _frame);
        var clip = single is null || missing ? null : ClipFor(single, time, null);
        BrowserAddButton.IsEnabled = clip is not null;
        BrowserAddButton.ToolTip = single is null ? "Select one file to add it"
            : missing ? "Relink the file first"
            : clip is null ? "A font isn't a clip: a text's Font file field uses it"
            : $"Adds it at the playhead ({Timecode.Short(time, tenths: true)}) on a level of its own, as the media panel's menu does";
        var plays = single is { Type: AssetType.Video or AssetType.Audio } && !missing;
        BrowserSourceButton.IsEnabled = plays;
        BrowserSourceButton.ToolTip = plays ? "Choose the part to use, then insert or overwrite it at the playhead" : "The source viewer plays one video or sound";
        BrowserFolderButton.IsEnabled = single is not null && details.Path is { } path && File.Exists(path);
        BrowserFolderButton.ToolTip = BrowserFolderButton.IsEnabled ? details.Path : "Select one file that's there";
    }

    // A chip for each tag, whose × takes it off the selected media, then + tag to add one.
    private void ShowTags(IReadOnlyList<string> tags)
    {
        DetailTags.Children.Clear();
        foreach (var tag in tags)
        {
            var remove = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 8, Padding = new Thickness(4), Margin = new Thickness(3, 0, 0, 0), ToolTip = $"Take '{tag}' off the selected media" };
            AutomationProperties.SetName(remove, $"Remove tag {tag}");
            remove.Click += (_, _) => TagSelected([], [tag]);
            var name = new TextBlock { Text = tag, FontSize = 12, Foreground = (Brush)FindResource("AccentSoftTextBrush"), VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("AccentSoftBrush"),
                Padding = new Thickness(10, 1, 2, 1),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, remove } },
            };
            DetailTags.Children.Add(chip);
        }

        var add = new Button { Style = (Style)FindResource("Chip"), Content = "+ tag", FontSize = 12, ToolTip = "Tag the selected media; a tag is a bin" };
        AutomationProperties.SetName(add, "Add a tag");
        add.Click += (_, _) =>
        {
            TagBox.Text = "";
            TagBox.Visibility = Visibility.Visible;
            TagBox.Focus();
        };
        DetailTags.Children.Add(add);
    }

    private void TagSelected(IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        var ids = BrowserAssets().Select(a => a.Id).ToList();
        if (ids.Count > 0)
        {
            Apply(new TagAssetsOperation(ids, add, remove));
        }
    }

    private void OnTagKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        var tag = TagBox.Text.Trim();
        TagBox.Visibility = Visibility.Collapsed;
        if (e.Key == Key.Enter && tag.Length > 0)
        {
            TagSelected([tag], []);
        }

        e.Handled = true;
    }

    private void OnTagLeft(object sender, KeyboardFocusChangedEventArgs e) => TagBox.Visibility = Visibility.Collapsed;

    private void OnNewBin(object sender, RoutedEventArgs e)
    {
        NewBinBox.Text = "";
        NewBinBox.Visibility = Visibility.Visible;
        NewBinBox.Focus();
    }

    // Enter puts the selected media in the new bin, which is then the shelf shown.
    private void OnNewBinKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        var name = NewBinBox.Text.Trim();
        NewBinBox.Visibility = Visibility.Collapsed;
        var ids = BrowserAssets().Select(a => a.Id).ToList();
        if (e.Key == Key.Enter && name.Length > 0 && ids.Count > 0)
        {
            Apply(new TagAssetsOperation(ids, [name], []), then: (saved, _) =>
            {
                if (saved)
                {
                    _shelf = "tag:" + name;
                    ShowBrowser();
                }
            });
        }

        e.Handled = true;
    }

    private void OnNewBinLeft(object sender, KeyboardFocusChangedEventArgs e) => NewBinBox.Visibility = Visibility.Collapsed;

    private void OnBrowserRelink(object sender, RoutedEventArgs e)
    {
        if (_document is not null && MissingAssets() is { Count: > 0 } missing)
        {
            ShowRelink(missing, opening: false);
        }
    }

    private void OnBrowserAdd(object sender, RoutedEventArgs e)
    {
        if (_document is not null && BrowserAssets() is [var asset] && ClipFor(asset, FrameMath.TimeOf(_document.View, _frame), null) is { } clip)
        {
            AddClip(clip);
        }
    }

    private void OnBrowserSource(object sender, RoutedEventArgs e)
    {
        if (BrowserAssets() is [var asset])
        {
            OpenSource(asset.Id);
        }
    }

    private void OnBrowserFolder(object sender, RoutedEventArgs e)
    {
        if (_document is not null && BrowserAssets() is [var asset] && ProjectStore.IsAssetFile(asset.File))
        {
            ShowInFolder(ProjectStore.AssetPath(_document.Session.Directory, asset));
        }
    }

    // Double-clicking a video or a sound opens it in the source viewer.
    private void OnBrowserOpened(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(BrowserList, source) is ListBoxItem && BrowserSourceButton.IsEnabled)
        {
            OnBrowserSource(sender, e);
        }
    }

    // The details column's actions, and Relink for a missing file.
    private void OnBrowserMenu(object sender, MouseButtonEventArgs e)
    {
        if (BrowserAssets() is not [var asset])
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = BrowserList };
        foreach (var button in new[] { BrowserAddButton, BrowserSourceButton, BrowserFolderButton })
        {
            var item = new MenuItem { Header = button.Content, IsEnabled = button.IsEnabled };
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            menu.Items.Add(item);
        }

        if (MissingAssets().Contains(asset))
        {
            var relink = new MenuItem { Header = "Relink…" };
            relink.Click += (_, _) => ShowRelink([asset], opening: false);
            menu.Items.Add(relink);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    // Opens a video or a sound in the source viewer without marks, on the lane of the clip selected
    // on the timeline when it can go there. The file open already stays as it is, marks and all.
    private void OpenSource(string assetId)
    {
        if (_document is not { } document || document.View.FindAsset(assetId) is not { Type: AssetType.Video or AssetType.Audio } asset)
        {
            return;
        }

        Pause();
        if (_source is null || _source.Asset.Id != asset.Id || _source.Asset.File != asset.File)
        {
            CloseSource();
            _source = new SourceMonitor(asset, SourceViewer.Of(document.View, asset), document.Session.Directory, Ffmpeg, Dispatcher, _prefs.PlaybackScale);
            _source.Changed += OnSourceChanged;
            _sourceIn = _sourceOut = null;
            _sourceSound = asset.HasAudio != false;
            _sourceTarget = SourceViewer.Default(document.View, asset, _selectedLayer).Key;
        }

        if (SourceMode.IsChecked == true)
        {
            ShowSource();
        }
        else
        {
            SourceMode.IsChecked = true;
        }
    }

    private void CloseSource()
    {
        if (_source is { } source)
        {
            source.Changed -= OnSourceChanged;
            source.Dispose();
            _source = null;
            _sourceBitmap = null;
            SourceImage.Source = null;
        }
    }

    // The source viewer: the file and where it is, the part marked, the lane the clip goes on with
    // the timeline around the playhead, and the program at the playhead. A file taken out of the
    // project closes it; a file relinked opens again.
    private void ShowSource()
    {
        if (_document is not { } document)
        {
            return;
        }

        if (_source is not { } source || document.View.FindAsset(source.Asset.Id) is not { Type: AssetType.Video or AssetType.Audio } asset)
        {
            var gone = _source?.Asset.Id;
            CloseSource();
            EditMode.IsChecked = true;
            Message(gone is null ? "" : $"'{gone}' isn't in the project any more");
            return;
        }

        if (asset.File != source.Asset.File)
        {
            OpenSource(asset.Id);
            return;
        }

        var project = document.View;
        var parts = MediaBrowser.Summary(asset).Split(" · ");
        SourceName.Text = Path.GetFileName(asset.File);
        SourceName.ToolTip = asset.Id;
        SourceInfo.Text = string.Join(" · ", parts.Skip(1));
        var range = SourceViewer.Range(source.Source, _sourceIn, _sourceOut);
        var target = SourceViewer.Targets(project, asset).FirstOrDefault(t => t.Key == _sourceTarget) ?? SourceViewer.Default(project, asset, _selectedLayer);
        _sourceTarget = target.Key;
        SourceTargetButton.Content = target.Title;
        SourceTracks.Visibility = asset.Type == AssetType.Video ? Visibility.Visible : Visibility.Collapsed;
        SourceAudio.IsChecked = _sourceSound;
        SourceAudio.IsEnabled = asset.HasAudio != false;
        SourceAudio.ToolTip = asset.HasAudio == false ? "This footage has no sound" : "Use the footage's sound; off, the clip is added with its sound off";
        var at = FrameMath.TimeOf(project, _frame);
        var lane = TimelineLayout.Lanes(project).FirstOrDefault(l => l.Key == target.Key);
        SourceStrip.Show(lane?.Clips ?? [], at, range.Length, $"{asset.Id} · {SourceViewer.Seconds(range.Length)}", project.Duration);
        SourceNote.Text = SourceViewer.Note(range.Length);
        var missing = Item(document.Session.Directory, asset).Missing;
        SourceInsertButton.IsEnabled = SourceOverwriteButton.IsEnabled = range.Length > 0 && !missing;
        ProgramName.Text = Path.GetFileName(document.Session.Directory);
        ProgramTime.Text = Timecode.Of(_frame, project.Fps);
        if (asset.Type == AssetType.Audio)
        {
            SourceWavePicture.Background = Picture(asset);
            SourceWaveIcon.Visibility = SourceWavePicture.Background is null ? Visibility.Visible : Visibility.Collapsed;
        }

        ShowSourcePosition();
    }

    private void OnSourceChanged()
    {
        if (SourceMode.IsChecked == true)
        {
            ShowSourcePosition();
        }
    }

    // The source playhead, the marks, the frame there and the play button.
    private void ShowSourcePosition()
    {
        if (_source is not { } source)
        {
            return;
        }

        var range = SourceViewer.Range(source.Source, _sourceIn, _sourceOut);
        SourceScrubber.Show(source.Frames, source.Frame, _sourceIn, _sourceOut);
        var mark = source.Frame == _sourceIn ? "IN " : source.Frame == _sourceOut ? "OUT " : "";
        SourceStamp.Text = mark + SourceViewer.Stamp(source.Time);
        SourceEnd.Text = SourceViewer.Stamp(source.Source.Duration);
        SourceMarks.Text = SourceViewer.Marks(range);
        SourcePlayIcon.Data = (Geometry)FindResource(source.Playing ? "PauseShape" : "PlayShape");
        SourcePlayButton.ToolTip = source.Playing ? "Pause (Space)" : "Play the file (Space)";
        SourceMessage.Text = source.Error is { } error ? $"Playing stopped: {error}" : source.Picture?.Error ?? source.SoundState;
        var video = source.Asset.Type == AssetType.Video;
        SourceImage.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        SourceWave.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        SourceFrame.HorizontalAlignment = video ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SourceFrame.VerticalAlignment = video ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        if (video && source.Picture is { Pixels: { } pixels } frame)
        {
            if (_sourceBitmap is null || _sourceBitmap.PixelWidth != frame.Width || _sourceBitmap.PixelHeight != frame.Height)
            {
                _sourceBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
                SourceImage.Source = _sourceBitmap;
            }

            _sourceBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), pixels, frame.Width * 4, 0);
        }
    }

    private void OnSourcePlay(object sender, RoutedEventArgs e) => _source?.Toggle();

    private void OnSourceSeek(int frame) => _source?.Seek(frame);

    private void StepSource(int frames)
    {
        if (_source is { } source)
        {
            source.Seek(source.Frame + frames);
        }
    }

    private void OnMarkIn(object sender, RoutedEventArgs e) => MarkIn();

    private void OnMarkOut(object sender, RoutedEventArgs e) => MarkOut();

    private void MarkIn()
    {
        if (_source is { } source)
        {
            (_sourceIn, _sourceOut) = SourceViewer.MarkIn(source.Frame, _sourceOut);
            ShowSource();
        }
    }

    private void MarkOut()
    {
        if (_source is { } source)
        {
            (_sourceIn, _sourceOut) = SourceViewer.MarkOut(source.Frame, _sourceIn);
            ShowSource();
        }
    }

    private void ClearMarks()
    {
        _sourceIn = _sourceOut = null;
        ShowSource();
    }

    private void OnSourceAudio(object sender, RoutedEventArgs e)
    {
        _sourceSound = SourceAudio.IsChecked == true;
        ShowSource();
    }

    private void OnSourceTarget(object sender, RoutedEventArgs e)
    {
        if (_document is { } document && _source is { } source && document.View.FindAsset(source.Asset.Id) is { } asset)
        {
            ShowChoices(SourceTargetButton, SourceViewer.Targets(document.View, asset).Select(t => (t.Title, t.Key == _sourceTarget, (Action)(() =>
            {
                _sourceTarget = t.Key;
                ShowSource();
            }))));
        }
    }

    private void OnSourceStripSeek(double time)
    {
        if (_document is { } document)
        {
            Seek(Math.Clamp(FrameMath.NearestFrame(document.View, time), 0, _frames - 1));
        }
    }

    private void OnSourceInsert(object sender, RoutedEventArgs e) => PlaceSource(ClipEdit.Insert);

    private void OnSourceOverwrite(object sender, RoutedEventArgs e) => PlaceSource(ClipEdit.Overwrite);

    // Adds the part marked at the timeline's playhead on the target lane, inserted or overwritten as
    // the add-layer tools' edit does it, selects it, and moves the playhead to its end for the next
    // one. A clip put on a new track on top makes that track the lane for the next one.
    private void PlaceSource(ClipEdit edit)
    {
        if (_document is not { } document || _source is not { } source || document.View.FindAsset(source.Asset.Id) is not { } asset || !SourceInsertButton.IsEnabled)
        {
            return;
        }

        var project = document.View;
        var range = SourceViewer.Range(source.Source, _sourceIn, _sourceOut);
        var target = SourceViewer.Targets(project, asset).FirstOrDefault(t => t.Key == _sourceTarget) ?? SourceViewer.Default(project, asset, _selectedLayer);
        var at = FrameMath.TimeOf(project, _frame);
        var before = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        source.Pause();
        Apply(SourceViewer.Clip(asset, at, target, range, _sourceSound, edit), then: (saved, _) =>
        {
            if (!saved || _document is not { } now)
            {
                return;
            }

            var added = now.View.Layers.FirstOrDefault(l => !before.Contains(l.Id) && Math.Abs(l.Start - at) < 1e-6 && l switch
            {
                VideoLayer video => video.Asset == asset.Id,
                AudioLayer audio => audio.Asset == asset.Id,
                _ => false,
            });
            if (added is null)
            {
                return;
            }

            if (added is VisualLayer visual && target.Key == SourceViewer.OnTop)
            {
                _sourceTarget = $"z:{visual.ZIndex}";
            }

            SelectLayer(added.Id);
            Seek(Math.Clamp(FrameMath.NearestFrame(now.View, at + range.Length), 0, _frames - 1));
        });
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

    // Shows the view the mode switch is on. Playback stops outside the Edit, Audio and Animate views.
    private void ShowMode()
    {
        if (_document is null)
        {
            return;
        }

        var editing = EditMode.IsChecked == true;
        if (!editing && AnimateMode.IsChecked != true && AudioMode.IsChecked != true)
        {
            Pause();
        }

        Workspace.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AnimatePage.Visibility = AnimateMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AudioPage.Visibility = AudioMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CaptionsPage.Visibility = CaptionsMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = ActivityMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ExportPage.Visibility = ExportMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MediaPage.Visibility = MediaMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SourcePage.Visibility = SourceMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SourceMode.IsChecked != true)
        {
            _source?.Pause();
        }

        ShowHeader();
        UpdateEditButtons();
        ShowPage();
    }

    // Brings the Audio, Animate, Captions, Activity, Export, Media or Source view up to date when it is the one showing.
    private void ShowPage()
    {
        if (_document is null)
        {
            return;
        }

        if (AudioMode.IsChecked == true)
        {
            ShowAudio();
        }
        else if (AnimateMode.IsChecked == true)
        {
            ShowAnimate();
        }
        else if (CaptionsMode.IsChecked == true)
        {
            ShowCaptions();
        }
        else if (ActivityMode.IsChecked == true)
        {
            ShowActivity();
        }
        else if (ExportMode.IsChecked == true)
        {
            ShowExportPage();
        }
        else if (MediaMode.IsChecked == true)
        {
            ShowBrowser();
        }
        else if (SourceMode.IsChecked == true)
        {
            ShowSource();
        }
    }

    // The project as the preview draws and plays it: the version in the preview language, as an
    // export in that language has it (Languages.Select). The same copy until the project changes, so
    // the frames drawn of it are kept.
    private Project Shown
    {
        get
        {
            var view = _document!.View;
            if (!ReferenceEquals(view, _shownFrom))
            {
                _shownFrom = view;
                _shownLanguage = CaptionGrid.PreviewLanguage(view, _prefs.PreviewLanguage);
                _shown = _shownLanguage is null ? view : InShownLanguage(ProjectJson.Clone(view));
            }

            return _shown!;
        }
    }

    // A copy of the project made the version the preview shows.
    private Project InShownLanguage(Project copy)
    {
        if (_shownLanguage is { } language)
        {
            Languages.Select(copy, language);
        }

        return copy;
    }

    // The project as the preview's sound plays it: as shown, with the roles not soloed muted. The
    // same copy until either changes.
    public Project Heard
    {
        get
        {
            var shown = Shown;
            var solo = string.Join(",", _solo.Order());
            if (_heard is not { } heard || !ReferenceEquals(heard.From, shown) || heard.Solo != solo)
            {
                _heard = heard = (shown, solo, MixerModel.Heard(shown, _solo));
            }

            return heard.Heard;
        }
    }

    // The preview and its sound, of the project as shown.
    private void ShowShown()
    {
        var shown = Shown;
        _sound!.Update(Heard, _document!.Session.Directory);
        if (_playing)
        {
            _playSource = _preview!.Source(shown, _prefs.PlaybackScale, _step);
            _preview.Play(_playSource, _frame);
            ShowPosition();
        }
        else
        {
            RequestFrame();
        }
    }

    // The preview language's choices: the Edit view's button and the Captions view's switch, while
    // the project has layers in two languages or more.
    private void ShowLanguages()
    {
        _ = Shown;
        var used = Languages.Used(_document!.View);
        var several = used.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        LanguageButton.Visibility = CaptionLanguageBar.Visibility = several;
        LanguageText.Text = _shownLanguage?.ToUpperInvariant() ?? "";
        CaptionLanguages.ItemsSource = used.Select(l => new PreviewChoice(l, l.ToUpperInvariant(), l == _shownLanguage)).ToList();
        CaptionPreviewText.Text = _shownLanguage is { } language
            ? $"What an export in {language.ToUpperInvariant()} shows at the playhead."
            : "Every layer at the playhead.";
    }

    private void OnLanguageMenu(object sender, RoutedEventArgs e)
    {
        if (_document is not null)
        {
            ShowChoices(LanguageButton, Languages.Used(_document.View).Select(l => (PreviewChoice.Name(l), l == _shownLanguage, (Action)(() => SetPreviewLanguage(l)))));
        }
    }

    private void OnPreviewLanguage(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string language })
        {
            SetPreviewLanguage(language);
        }
    }

    // Shows another language's version: an editor setting, never saved in the project.
    private void SetPreviewLanguage(string language)
    {
        if (_document is null || language == _shownLanguage)
        {
            return;
        }

        _prefs.PreviewLanguage = language;
        SavePrefs();
        _shownFrom = null;
        ShowLanguages();
        ShowShown();
    }

    // Measures captions' text as frames draw it, for the caption edits that keep where a caption's
    // text box stands.
    private Func<Project, ITextMeasurer> Measurer()
    {
        var renderer = _measure ??= new FrameRenderer(_document!.Session.Directory);
        return project => new TextMeasurer(renderer, project);
    }

    // The Captions view: the tracks' chips, the grid of their captions, the preview's languages and
    // the captions' style. The cell being typed in is typed in again once the grid is built again.
    private void ShowCaptions()
    {
        var project = _document!.View;
        (string Key, string? Text, int Caret)? typing = null;
        if (Keyboard.FocusedElement is TextBox focused && CaptionRows.IsAncestorOf(focused) && CaptionTarget(focused) is { } at)
        {
            var text = focused.Text.ReplaceLineEndings("\n");
            var sent = _captionSent is { } last && last.Cell == at.Key && last.Text == text;
            typing = (at.Key, text != at.Original.ReplaceLineEndings("\n") && !sent ? focused.Text : null, focused.CaretIndex);
        }

        var table = CaptionGrid.Build(project, _captionSource, _captionDraft);
        _captionTable = table;
        _captionProject = project;
        CaptionChips.ItemsSource = table.Columns;
        CaptionHeadings.ItemsSource = table.Columns;
        CaptionRows.ItemsSource = table.Rows;
        var tracks = table.Columns.Count > 0;
        CaptionGridBox.Visibility = tracks ? Visibility.Visible : Visibility.Collapsed;
        CaptionsEmpty.Visibility = tracks ? Visibility.Collapsed : Visibility.Visible;
        CaptionAdd.IsEnabled = tracks;
        CaptionSave.IsEnabled = ExportChoices.Subtitles(project).Count > 0;
        MarkCurrentCaption();
        ShowCaptionStyle(project, table);
        if (typing is { } t)
        {
            FocusCaption(t.Key, t.Text, t.Caret);
        }
    }

    // The style section shows the source's first caption's size, place, background and line width,
    // and each track's font.
    private void ShowCaptionStyle(Project project, CaptionTable table)
    {
        _fillingCaptions = true;
        try
        {
            CaptionFonts.ItemsSource = table.Columns;
            var cue = table.Columns.Select(c => CaptionTracks.Cues(project, c.GroupId).FirstOrDefault()).OfType<TextLayer>().FirstOrDefault();
            CaptionStyle.IsEnabled = cue is not null;
            CaptionSize.Value = cue?.FontSize ?? CaptionSize.Minimum;
            CaptionSizeText.Text = cue is null ? "" : $"{Number(cue.FontSize)}px";
            var place = cue is null ? (CaptionPlace?)null : CaptionTracks.PlaceOf(project, cue);
            CaptionTop.IsChecked = place == CaptionPlace.Top;
            CaptionMiddle.IsChecked = place == CaptionPlace.Middle;
            CaptionBottom.IsChecked = place == CaptionPlace.Bottom;
            var look = cue is null ? (CaptionLook?)null : CaptionTracks.LookOf(cue);
            CaptionBox.IsChecked = look == CaptionLook.Box;
            CaptionOutline.IsChecked = look == CaptionLook.Outline;
            CaptionNone.IsChecked = look == CaptionLook.None;
            if (!CaptionWidth.IsKeyboardFocused)
            {
                CaptionWidth.Text = cue?.MaxWidth is { } width ? Number(width) : "";
            }
        }
        finally
        {
            _fillingCaptions = false;
        }
    }

    // The rows the playhead is in are marked.
    private void MarkCurrentCaption()
    {
        if (_captionTable is not { } table || _document is null)
        {
            return;
        }

        var time = FrameMath.TimeOf(_document.View, _frame);
        foreach (var row in table.Rows)
        {
            row.Current = row.Start <= time + 1e-9 && time < row.End - 1e-9;
        }
    }

    // What a box of the grid edits: a track's caption in a row (or the place for one), or the row's
    // start or end. Key names it across builds of the grid: the row's caption, and the track or
    // the time.
    private static (CaptionRow Row, CaptionCell? Cell, bool End, string Key, string Original)? CaptionTarget(TextBox box) => box.Tag switch
    {
        CaptionCell cell => (cell.Row, cell, false, $"{cell.Row.Key}|{cell.Column.GroupId}", cell.Text),
        CaptionRow row when AutomationProperties.GetName(box) == "End" => (row, null, true, $"{row.Key}|end", row.EndText),
        CaptionRow row => (row, null, false, $"{row.Key}|start", row.StartText),
        _ => null,
    };

    // Puts the keyboard in a box of the grid once it's built, with what was typed there.
    private void FocusCaption(string key, string? text, int? caret = null) => Dispatcher.InvokeAsync(() =>
    {
        if (Descendants<TextBox>(CaptionRows).FirstOrDefault(b => CaptionTarget(b)?.Key == key) is { } box)
        {
            if (text is not null)
            {
                box.Text = text;
            }

            Keyboard.Focus(box);
            box.CaretIndex = Math.Min(caret ?? box.Text.Length, box.Text.Length);
        }
    }, DispatcherPriority.Loaded);

    // Enter saves a cell (Shift+Enter starts a new line of a caption); Escape puts back what it
    // holds, and in a new row with nothing to put back, takes the row away.
    private void OnCaptionKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || CaptionTarget(box) is not { } target)
        {
            return;
        }

        if (e.Key == Key.Return && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            CommitCaption(box, fromEnter: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (box.Text != target.Original)
            {
                box.Text = target.Original;
                box.SelectAll();
            }
            else if (target.Row.Draft)
            {
                _captionDraft = null;
                ShowCaptions();
            }
        }
    }

    // Leaving a cell saves it, once the focus has moved.
    private void OnCaptionLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            Dispatcher.InvokeAsync(() => CommitCaption(box, fromEnter: false), DispatcherPriority.Input);
        }
    }

    // Going into a row takes the playhead to its start, unless it's in the row already.
    private void OnCaptionEntered(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_document is null || sender is not TextBox box || CaptionTarget(box) is not { } target)
        {
            return;
        }

        var project = _document.View;
        var time = FrameMath.TimeOf(project, _frame);
        if (time < target.Row.Start - 1e-9 || time >= target.Row.End - 1e-9)
        {
            Seek(Math.Min(_frames - 1, (int)Math.Ceiling(target.Row.Start * project.Fps - 1e-6)));
        }
    }

    // Saves what a box of the grid holds: a caption's text (an emptied cell removes the caption, and
    // text typed into an empty one adds a caption at the row's times, styled like the track's
    // others), or the row's start or end, for its captions in every language as one step.
    private void CommitCaption(TextBox box, bool fromEnter)
    {
        if (_document is null || _captionTable is not { } table || CaptionTarget(box) is not { } target || !table.Rows.Contains(target.Row))
        {
            return;
        }

        var text = box.Text.ReplaceLineEndings("\n");
        if (text == target.Original.ReplaceLineEndings("\n") || _captionSent == (table, target.Key, text))
        {
            return;
        }

        var row = target.Row;
        EditOperation? operation;
        try
        {
            operation = target.Cell is { } cell ? CaptionEdit(row, cell, text) : TimeEdit(row, target.End, text);
        }
        catch (ZeyoCutException ex)
        {
            Message(ex.Message);
            if (!fromEnter)
            {
                box.Text = target.Original;
            }

            return;
        }

        if (operation is null)
        {
            return;
        }

        _captionSent = (table, target.Key, text);
        var group = target.Cell?.Column.GroupId;
        HashSet<string> before = group is null ? [] : CaptionTracks.Cues(_document.View, group).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var draft = row.Draft ? _captionDraft : null;
        if (draft is not null)
        {
            _captionDraft = null;
        }

        Apply(operation, _captionProject, then: (saved, _) =>
        {
            if (draft is null || _document is null)
            {
                return;
            }

            if (!saved)
            {
                _captionDraft = draft;
                ShowCaptions();
                if (fromEnter)
                {
                    FocusCaption(target.Key, box.Text);
                }
            }
            else if (fromEnter && CaptionTracks.Cues(_document.View, group!).FirstOrDefault(c => !before.Contains(c.Id)) is { } added
                && _captionTable?.Rows.FirstOrDefault(r => r.Cells.Any(c => c.LayerId == added.Id)) is { } made)
            {
                FocusCaption($"{made.Key}|{group}", null);
            }
        });
    }

    private EditOperation? CaptionEdit(CaptionRow row, CaptionCell cell, string text)
    {
        if (cell.LayerId is { } id)
        {
            return text.Trim().Length == 0 ? new RemoveLayerOperation(id, false) : new CaptionTextOperation(id, text, Measurer());
        }

        var font = Languages.FontFor(_settings.Fonts, cell.Column.Language);
        return text.Trim().Length == 0 ? null : new AddCaptionOperation(cell.Column.GroupId, row.Start, row.End, text, new CaptionOptions { FontFamily = font }, Measurer());
    }

    // A row's new start or end; a new row's only moves the row until it's saved.
    private EditOperation? TimeEdit(CaptionRow row, bool end, string text)
    {
        var project = _document!.View;
        var time = CaptionTime.Parse(text, end ? "the end" : "the start");
        if (Math.Abs(time - (end ? row.End : row.Start)) < 0.0005)
        {
            return null;
        }

        if (!row.Draft)
        {
            return new RetimeCaptionsOperation(row.LayerIds, end ? null : time, end ? time : null);
        }

        var (start, stop) = end ? (row.Start, Math.Min(time, project.Duration)) : (time, row.End);
        if (start < 0 || start >= project.Duration || stop <= start)
        {
            throw new ZeyoCutException($"a caption must start at 0 s or later and before the project end ({Number(project.Duration)}s), and end after it starts (got {Number(start)}–{Number(stop)}s)");
        }

        _captionDraft = (start, stop);
        ShowCaptions();
        return null;
    }

    // A new row at the playhead, or after the caption the playhead is in, with the keyboard in the
    // source's cell: typing into a cell saves a caption there.
    private void OnAddCaption(object sender, RoutedEventArgs e)
    {
        if (_document is null || _captionTable is not { Columns.Count: > 0 } table)
        {
            return;
        }

        var project = _document.View;
        if (CaptionGrid.Draft(project, table, FrameMath.TimeOf(project, _frame)) is not { } draft)
        {
            Message("The captions run on to the end from the playhead: move it to where a new caption goes.");
            return;
        }

        _captionDraft = draft;
        ShowCaptions();
        FocusCaption($"{CaptionGrid.DraftKey}|{table.Columns[0].GroupId}", null);
    }

    // A chip of a track picks it as the source the rows follow (without an English track).
    private void OnCaptionSource(object sender, RoutedEventArgs e)
    {
        if (_document is not null && sender is FrameworkElement { Tag: CaptionColumn { Pickable: true } column })
        {
            _captionSource = column.GroupId;
            ShowCaptions();
        }
    }

    // Import .srt: the file's captions as its language's track, as import_srt imports them, once
    // the language is chosen.
    private void OnImportSrt(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = "Import captions", Filter = "Subtitles (*.srt)|*.srt" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        List<CueEntry> cues;
        List<string> problems;
        try
        {
            (cues, problems) = CaptionTracks.ParseSrt(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message($"Couldn't read {Path.GetFileName(dialog.FileName)}: {ex.Message}");
            return;
        }

        if (cues.Count == 0)
        {
            Message($"{Path.GetFileName(dialog.FileName)} has no captions to import{(problems.Count > 0 ? $": {problems[0]}" : "")}.");
            return;
        }

        _importSrt = (dialog.FileName, cues, problems);
        ImportSrtTitle.Text = $"Import {Path.GetFileName(dialog.FileName)}";
        ImportSrtLanguage.Text = Languages.FromFileName(dialog.FileName) ?? "";
        ShowImportSrtNote();
        ShowDialog(ImportSrtDialog, ImportSrtLanguage);
        ImportSrtLanguage.SelectAll();
    }

    private void OnImportSrtLanguage(object sender, TextChangedEventArgs e) => ShowImportSrtNote();

    private void OnImportSrtKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            ImportSrt();
        }
    }

    private void OnImportSrtChosen(object sender, RoutedEventArgs e) => ImportSrt();

    // What importing in the language typed does: adds a track, or replaces the one in it.
    private void ShowImportSrtNote()
    {
        if (_document is null || _importSrt is not { } import)
        {
            return;
        }

        var count = Count(import.Cues.Count, "caption");
        if (!Languages.TryNormalize(ImportSrtLanguage.Text, out var language))
        {
            ImportSrtNote.Text = $"{count} in the file. Type a language code such as en, ja, my or pt-BR.";
            ImportSrtButton.IsEnabled = false;
            return;
        }

        var group = CaptionTracks.GroupId(language);
        var existing = _document.View.FindLayer(group) is null ? 0 : CaptionTracks.Cues(_document.View, group).Count;
        var font = Languages.FontFor(_settings.Fonts, language);
        var code = language.ToUpperInvariant();
        var what = existing > 0
            ? $"{count} in the file, in place of the {Count(existing, "caption")} of the {code} track ({group})."
            : $"{count} in the file, as a new {code} track ({group}){(font is null ? "" : $" in {font}")}.";
        var problems = import.Problems.Count switch
        {
            0 => "",
            1 => $" Note: {import.Problems[0]}.",
            _ => $" {import.Problems.Count} notes, the first: {import.Problems[0]}.",
        };
        ImportSrtNote.Text = what + problems;
        ImportSrtButton.IsEnabled = true;
    }

    private void ImportSrt()
    {
        if (_document is null || _importSrt is not { } import || !Languages.TryNormalize(ImportSrtLanguage.Text, out var language))
        {
            return;
        }

        CloseDialog();
        _importSrt = null;
        Apply(new ImportSrtOperation(Path.GetFileName(import.Path), language, import.Cues, import.Problems, Languages.FontFor(_settings.Fonts, language), Measurer()));
    }

    // Save .srt files: each track with captions as an .srt file in the exports folder, as export_srt
    // writes it, named as the Export view names them (comic-panels.en.srt).
    private void OnSaveSrt(object sender, RoutedEventArgs e) => WhenSaved(SaveSrt);

    private void SaveSrt()
    {
        if (_document is not { } document)
        {
            return;
        }

        var project = document.View;
        var directory = document.Session.Directory;
        var files = ExportChoices.SrtFiles(ExportChoices.DefaultFileName(directory), ExportChoices.Subtitles(project));
        if (files.Count == 0)
        {
            return;
        }

        var there = files.Where(f => File.Exists(Path.Combine(directory, ProjectStore.ExportsFolder, f.Name))).Select(f => f.Name).ToList();
        if (there.Count > 0 && MessageBox.Show(this, $"{Names(there)} {(there.Count == 1 ? "is" : "are")} in the exports folder already. Replace {(there.Count == 1 ? "it" : "them")}?",
                "ZeyoCut Editor", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _captionSaved = files.Select(f => CaptionBuilder.WriteSrt(directory, f.Name, CaptionBuilder.GroupCues(project, f.GroupId))).ToList();
            CaptionSaved.Text = $"Saved {Names(files.Select(f => f.Name).ToList())} in the exports folder.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _captionSaved = [];
            CaptionSaved.Text = $"Couldn't save the .srt files: {ex.Message}";
        }

        CaptionSavedPanel.Visibility = Visibility.Visible;
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    private void OnShowSrt(object sender, RoutedEventArgs e)
    {
        if (_captionSaved.FirstOrDefault(File.Exists) is { } file)
        {
            ShowInFolder(file);
        }
        else
        {
            OnShowExportsFolder(sender, e);
        }
    }

    // The style section: each change is one step for every track's captions (a font, the track's).
    private void Restyle(IReadOnlyList<string> groups, CaptionRestyle change)
    {
        if (_document is not null && !_fillingCaptions && groups.Count > 0)
        {
            Apply(new RestyleCaptionsOperation(groups, change, Measurer()), _captionProject);
        }
    }

    private List<string> CaptionGroups() => _captionTable?.Columns.Where(c => CaptionTracks.Cues(_document!.View, c.GroupId).Count > 0).Select(c => c.GroupId).ToList() ?? [];

    private void OnCaptionFontKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: CaptionColumn column } box)
        {
            return;
        }

        if (e.Key == Key.Return)
        {
            e.Handled = true;
            CommitCaptionFont(box, column);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.Text = column.Font;
            box.SelectAll();
        }
    }

    private void OnCaptionFontLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { Tag: CaptionColumn column } box)
        {
            Dispatcher.InvokeAsync(() => CommitCaptionFont(box, column), DispatcherPriority.Input);
        }
    }

    private void CommitCaptionFont(TextBox box, CaptionColumn column)
    {
        var font = box.Text.Trim();
        if (_captionTable?.Columns.Contains(column) == true && font != column.Font)
        {
            Restyle([column.GroupId], new CaptionRestyle { FontFamily = font });
        }
    }

    private void OnCaptionSizeMoved(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_fillingCaptions && IsLoaded)
        {
            CaptionSizeText.Text = $"{Number(CaptionSize.Value)}px";
        }
    }

    private void OnCaptionSizeReleased(object sender, MouseButtonEventArgs e) => CommitCaptionSize();

    private void OnCaptionSizeKey(object sender, KeyEventArgs e) => CommitCaptionSize();

    private void CommitCaptionSize()
    {
        var cue = _captionTable is null || _document is null ? null
            : _captionTable.Columns.Select(c => CaptionTracks.Cues(_document.View, c.GroupId).FirstOrDefault()).OfType<TextLayer>().FirstOrDefault();
        if (cue is not null && Math.Abs(CaptionSize.Value - cue.FontSize) > 1e-9)
        {
            Restyle(CaptionGroups(), new CaptionRestyle { FontSize = CaptionSize.Value });
        }
    }

    private void OnCaptionPlace(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string place })
        {
            Restyle(CaptionGroups(), new CaptionRestyle { Place = Enum.Parse<CaptionPlace>(place) });
        }
    }

    private void OnCaptionLook(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string look })
        {
            Restyle(CaptionGroups(), new CaptionRestyle { Look = Enum.Parse<CaptionLook>(look) });
        }
    }

    private void OnCaptionWidthKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            CommitCaptionWidth();
        }
        else if (e.Key == Key.Escape && _captionTable is { } table && _captionProject is { } project)
        {
            e.Handled = true;
            ShowCaptionStyle(project, table);
            CaptionWidth.Text = table.Columns.Select(c => CaptionTracks.Cues(project, c.GroupId).FirstOrDefault()).OfType<TextLayer>().FirstOrDefault()?.MaxWidth is { } width ? Number(width) : "";
            CaptionWidth.SelectAll();
        }
    }

    private void OnCaptionWidthLeft(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.InvokeAsync(CommitCaptionWidth, DispatcherPriority.Input);

    private void CommitCaptionWidth()
    {
        if (_document is null || _captionTable is not { } table)
        {
            return;
        }

        var text = CaptionWidth.Text.Trim();
        var cue = table.Columns.Select(c => CaptionTracks.Cues(_document.View, c.GroupId).FirstOrDefault()).OfType<TextLayer>().FirstOrDefault();
        if (cue is null || text == (cue.MaxWidth is { } shown ? Number(shown) : ""))
        {
            return;
        }

        if (!double.TryParse(text.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? text[..^2] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
        {
            Message($"The line width is a number of pixels, such as 1000 (got '{text}').");
            return;
        }

        Restyle(CaptionGroups(), new CaptionRestyle { MaxWidth = width });
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

        foreach (var view in new[] { ShortcutsDialog, NewProjectDialog, RelinkDialog, ImportSrtDialog })
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
        DialogLayer.Visibility = ShortcutsDialog.Visibility = NewProjectDialog.Visibility = RelinkDialog.Visibility = ImportSrtDialog.Visibility = Visibility.Collapsed;
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
            _playSource = _preview!.Source(Shown, scale, _step);
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

    // The Animate view: the project's keyframes in the dope sheet or the graph, the easing of those
    // selected, and over the preview the selected layer's onion skin and motion path.
    private void ShowAnimate()
    {
        var project = _document!.View;
        Keyframes.Show(project, AnimateModel.Build(project, _selectedLayer), _selectedLayer, _frames);
        ShowEasing();
        ShowMotionPath();
        ShowAnimatePosition();
    }

    private void ShowAnimatePosition()
    {
        Keyframes.Playhead = _frame;
        AnimateFrameText.Text = $"frame {_frame} of {_frames - 1}";
        RequestOnion();
    }

    // The selected keys' easing on the tiles and the toolbar's buttons, a cubicBezier one's numbers
    // in the boxes, and the key or keys in the toolbar's status.
    private void ShowEasing()
    {
        var selected = Keyframes.Selection;
        var numbers = selected.Where(k => !k.Drawing).ToList();
        EasingSelectionText.Text = selected.Count == 0 ? "No keyframes selected" : $"{Count(selected.Count, "keyframe")} selected";
        EasingTiles.ItemsSource = AnimateModel.TilesFor(selected);
        var shared = AnimateModel.SharedEasing(numbers);
        LinearButton.IsChecked = shared?.Easing == Easing.Linear;
        SmoothButton.IsChecked = shared?.Easing == Easing.EaseInOut;
        HoldButton.IsChecked = shared?.Easing == Easing.Hold;
        LinearButton.IsEnabled = SmoothButton.IsEnabled = HoldButton.IsEnabled = BezierApply.IsEnabled = numbers.Count > 0;
        if (shared is { Easing: Easing.CubicBezier, Bezier: { Length: 4 } bezier })
        {
            ShowBezier(bezier);
        }

        var rows = Keyframes.Layers.SelectMany(l => l.Rows).ToList();
        if (selected.Count == 1 && rows.FirstOrDefault(r => r.LayerId == selected[0].LayerId && r.Property == selected[0].Property) is { } row)
        {
            var key = selected[0];
            var value = key.Name ?? row.Format(key.Value) + (row.Unit == "px" ? " px" : "");
            KeyStatus.Text = $"Key at {Number(key.Time)} s · {key.LayerId} · {row.Label} = {value}";
        }
        else
        {
            KeyStatus.Text = selected.Count == 0 ? "" : $"{Count(selected.Count, "key")} · {Number(selected.Min(k => k.Time))}–{Number(selected.Max(k => k.Time))} s";
        }
    }

    private void ShowBezier(IReadOnlyList<double> bezier)
    {
        var boxes = new[] { BezierX1, BezierY1, BezierX2, BezierY2 };
        for (var i = 0; i < boxes.Length; i++)
        {
            boxes[i].Text = bezier[i].ToString("0.00####", CultureInfo.InvariantCulture);
        }
    }

    private void ShowOnionText() => OnionText.Text = $"Onion skin · {_prefs.OnionBefore} before / {_prefs.OnionAfter} after";

    // The selected layer's motion path, found again when the project or the layer changes.
    private void ShowMotionPath()
    {
        var shown = Shown;
        if (_pathFor is not { } pathFor || !ReferenceEquals(pathFor.Project, shown) || pathFor.Layer != _selectedLayer)
        {
            _pathFor = (shown, _selectedLayer);
            _path = _selectedLayer is { } id ? AnimateModel.MotionPath(shown, id) : ([], []);
        }

        ShowAnimateOverlay();
    }

    private void ShowAnimateOverlay()
    {
        var shown = Shown;
        var (path, keys) = MotionPathToggle.IsChecked == true ? _path : ([], []);
        AnimateFrame.Show(new Size(shown.Width, shown.Height), OnionToggle.IsChecked == true && !_playing ? _onionPicture : null, path, keys);
    }

    // The onion skin of the selected layer at the playhead, while paused; drawn on a worker thread,
    // it's shown once it's done (OnOnion).
    private void RequestOnion()
    {
        var shown = Shown;
        if (_playing || OnionToggle.IsChecked != true || _selectedLayer is not { } id || shown.FindLayer(id) is null || _prefs.OnionBefore + _prefs.OnionAfter == 0)
        {
            _onionAsked = null;
            _onionPicture = null;
            ShowAnimateOverlay();
            return;
        }

        if (_onion is null)
        {
            OnionSkin? onion = null;
            onion = new OnionSkin(_document!.Session.Directory, () => Ffmpeg().Ffmpeg, picture => Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(onion, _onion))
                {
                    OnOnion(picture);
                }
            }));
            _onion = onion;
        }

        var request = new OnionRequest(shown, AnimateModel.WithChildren(shown, id), _frame, _prefs.OnionBefore, _prefs.OnionAfter, Math.Min(1, 1280.0 / Math.Max(shown.Width, shown.Height)));
        if (_onionAsked is { } asked && ReferenceEquals(asked.Project, request.Project) && asked.Frame == request.Frame && asked.Before == request.Before && asked.After == request.After
            && asked.Layers.SetEquals(request.Layers))
        {
            ShowAnimateOverlay();
            return;
        }

        _onionAsked = request;
        _onion.Request(request);
    }

    private void OnOnion(OnionPicture picture)
    {
        if (!ReferenceEquals(picture.Request, _onionAsked))
        {
            return;
        }

        var bitmap = BitmapSource.Create(picture.Width, picture.Height, 96, 96, PixelFormats.Pbgra32, null, picture.Pixels, picture.Width * 4);
        bitmap.Freeze();
        _onionPicture = bitmap;
        ShowAnimateOverlay();
    }

    // The selected keyframes' segments given an easing, cubicBezier with its numbers, as one step.
    // A drawing's keyframes always hold, so they're left as they are.
    private void SetEasing(Easing easing, IReadOnlyList<double>? bezier = null)
    {
        WhenSaved(() =>
        {
            if (_document is null)
            {
                return;
            }

            var selected = Keyframes.Selection;
            var keys = selected.Where(k => !k.Drawing).ToList();
            if (keys.Count == 0)
            {
                Message(selected.Count > 0 ? "A drawing's keyframes always hold; select keyframes of a number to ease them" : "Select keyframes first: click one in the dope sheet or the graph");
                ShowEasing();
                return;
            }

            Apply(new SetEasingOperation(keys.Select(k => k.Ref).ToList(), easing, bezier));
        });
    }

    private void OnEasingTile(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Easing easing })
        {
            SetEasing(easing);
        }
    }

    private void OnEasingButton(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string name })
        {
            ShowEasing();
            SetEasing(Enum.Parse<Easing>(name));
        }
    }

    private void OnBezierApply(object sender, RoutedEventArgs e)
    {
        var numbers = new List<double>();
        foreach (var box in new[] { BezierX1, BezierY1, BezierX2, BezierY2 })
        {
            if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                Message($"{AutomationProperties.GetName(box)} is a number, such as 0.42");
                box.Focus();
                return;
            }

            numbers.Add(number);
        }

        SetEasing(Easing.CubicBezier, numbers);
    }

    private void OnBezierKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            OnBezierApply(sender, e);
        }
    }

    // Alt+K: a keyframe at the playhead for the value last clicked in the list, with the value it
    // has there; without one, for the selected layer's position.
    private void AddKeyframe()
    {
        Pause();
        var frame = _frame;
        WhenSaved(() =>
        {
            if (_document is null)
            {
                return;
            }

            var project = _document.View;
            var time = FrameMath.TimeOf(project, frame);
            try
            {
                if (Keyframes.CurrentRow is { Drawing: false } row && AnimateModel.Value(project, row.LayerId, row.Property) is { } value)
                {
                    if (ValueEditor.KeyAt(project, value, time) is not null)
                    {
                        Message($"{row.LayerId}'s {row.Label} has a keyframe at the playhead already");
                        return;
                    }

                    Apply(row.LayerId == Camera.Id
                        ? new SetKeyframesOperation([new KeyframeEntry(Camera.Id, row.Property, time, System.Text.Json.JsonSerializer.SerializeToElement(Interpolator.Evaluate(value, time)))])
                        : ValueEditor.ToggleKeyframe(project, row.LayerId, row.Property, time));
                }
                else if (_selectedLayer is { } id && project.FindLayer(id) is VisualLayer layer)
                {
                    if (ValueEditor.KeyAt(project, layer.X, time) is not null && ValueEditor.KeyAt(project, layer.Y, time) is not null)
                    {
                        Message($"{id}'s position has a keyframe at the playhead already");
                        return;
                    }

                    Apply(ValueEditor.ToggleKeyframes(project, id, ["x", "y"], time));
                }
                else
                {
                    Message("Select a layer, or click a value in the list on the left, to key it at the playhead");
                }
            }
            catch (ZeyoCutException ex)
            {
                Message(ex.Message);
            }
        });
    }

    // Delete in the Animate view: the selected keyframes, as one step.
    private void DeleteKeys() => WhenSaved(() =>
    {
        if (_document is not null && Keyframes.Selection is { Count: > 0 } keys)
        {
            Apply(new RemoveKeyframesOperation(keys.Select(k => k.Ref).ToList()));
        }
    });

    private void ToggleOnion()
    {
        OnionToggle.IsChecked = OnionToggle.IsChecked != true;
        OnOnionToggle(OnionToggle, new RoutedEventArgs());
        Message(OnionToggle.IsChecked == true ? "Onion skin on" : "Onion skin off");
    }

    private void OnOnionToggle(object sender, RoutedEventArgs e)
    {
        _prefs.OnionSkin = OnionToggle.IsChecked == true;
        SavePrefs();
        if (Animating)
        {
            RequestOnion();
        }
    }

    // The onion skin's frames before and after the playhead, from none to five.
    private void OnOnionCounts(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OnionCounts, Placement = PlacementMode.Bottom };
        foreach (var (name, before) in new[] { ("Frames before", true), ("Frames after", false) })
        {
            var header = new MenuItem { Header = name };
            for (var count = 0; count <= 5; count++)
            {
                var chosen = count;
                var item = new MenuItem { Header = count.ToString(CultureInfo.InvariantCulture), IsChecked = (before ? _prefs.OnionBefore : _prefs.OnionAfter) == count };
                item.Click += (_, _) =>
                {
                    if (before)
                    {
                        _prefs.OnionBefore = chosen;
                    }
                    else
                    {
                        _prefs.OnionAfter = chosen;
                    }

                    SavePrefs();
                    ShowOnionText();
                    if (Animating)
                    {
                        RequestOnion();
                    }
                };
                header.Items.Add(item);
            }

            menu.Items.Add(header);
        }

        menu.IsOpen = true;
    }

    private void OnMotionPathToggle(object sender, RoutedEventArgs e)
    {
        _prefs.MotionPath = MotionPathToggle.IsChecked == true;
        SavePrefs();
        if (Animating)
        {
            ShowAnimateOverlay();
        }
    }

    private void OnKeyframeMode(object sender, RoutedEventArgs e)
    {
        if (Keyframes is null)
        {
            return;
        }

        var graph = GraphMode.IsChecked == true;
        Keyframes.Mode = graph ? KeyframeMode.Graph : KeyframeMode.DopeSheet;
        FitCurvesToggle.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFitCurves(object sender, RoutedEventArgs e) => Keyframes.FitCurves = FitCurvesToggle.IsChecked == true;

    private void OnKeySelection(object? sender, EventArgs e) => ShowEasing();

    private void OnKeyLayer(object? sender, string id) => SelectLayer(id);

    // The Audio view: a strip for each sound role and Master, the loudness, the music's ducking and
    // the volume lanes, of the project as saved. Solo is the preview's own.
    private void ShowAudio()
    {
        var project = _document!.View;
        var lanes = MixerModel.Lanes(project);
        MixerStrips.ItemsSource = MixerModel.Strips(project, _solo);
        MasterSlots.ItemsSource = MixerModel.MasterSlots(project);
        LoudnessTargetButton.Content = MixerModel.TargetText(project.Audio.LoudnessTarget);
        ShowLoudness();
        _settingDuck = true;
        try
        {
            AutoDuck.IsChecked = project.Audio.Ducking;
            DuckLevelSlider.Value = -project.Audio.DuckLevel;
            DuckFadeInSlider.Value = project.Audio.DuckFadeIn;
            DuckFadeOutSlider.Value = project.Audio.DuckFadeOut;
        }
        finally
        {
            _settingDuck = false;
        }

        DuckSettings.IsEnabled = project.Audio.Ducking;
        ShowDuckTexts();
        VolumeLanes.Show(project, lanes, MixerModel.Duck(project, lanes), _frames);
        ShowAudioPosition();
        RequestMeter();
    }

    private void ShowAudioPosition()
    {
        VolumeLanes.Playhead = _frame;
        AudioTimecode.Text = Timecode.Of(_frame, _document!.View.Fps);
        if (!_playing)
        {
            MasterMeter.Level = (double.NegativeInfinity, double.NegativeInfinity);
            MasterPeak.Text = "-inf";
        }
    }

    private void ShowDuckTexts()
    {
        DuckLevelText.Text = MixerModel.Decibels(-DuckLevelSlider.Value);
        DuckFadeInText.Text = MixerModel.Seconds(DuckFadeInSlider.Value);
        DuckFadeOutText.Text = MixerModel.Seconds(DuckFadeOutSlider.Value);
    }

    // The fader's gain under it while it moves, and the role's gain saved when it's let go.
    private void OnFaderMoved(object? sender, double gain)
    {
        if (sender is MixerFader { Parent: StackPanel strip })
        {
            strip.Children.OfType<TextBlock>().Single().Text = MixerModel.GainText(gain);
        }
    }

    private void OnFaderCommitted(object? sender, double gain)
    {
        if (sender is MixerFader { Tag: MixerStrip strip })
        {
            SetMix(new ProjectOptions { Role = strip.Role, Gain = gain });
        }
    }

    private void OnPanMoved(object? sender, double pan)
    {
        if (sender is MixerPan { Parent: DockPanel row })
        {
            row.Children.OfType<TextBlock>().Last().Text = MixerModel.PanLabel(pan);
        }
    }

    private void OnPanCommitted(object? sender, double pan)
    {
        if (sender is MixerPan { Tag: MixerStrip strip })
        {
            SetMix(new ProjectOptions { Role = strip.Role, Pan = pan });
        }
    }

    private void OnStripMute(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: MixerStrip strip } button)
        {
            SetMix(new ProjectOptions { Role = strip.Role, Muted = button.IsChecked == true });
        }
    }

    // Solo: the preview hears only the soloed roles. Nothing is saved.
    private void OnStripSolo(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: MixerStrip strip } button || _document is null)
        {
            return;
        }

        if (button.IsChecked == true)
        {
            _solo.Add(strip.Role);
        }
        else
        {
            _solo.Remove(strip.Role);
        }

        Message(_solo.Count == 0 ? "Every role is heard again" : $"The preview plays only {string.Join(" and ", MixerModel.Roles.Where(_solo.Contains).Select(r => MixerModel.RoleName(r).ToLowerInvariant()))}; nothing is saved");
        ShowAudio();
        _sound!.Update(Heard, _document.Session.Directory);
    }

    // A mixer setting, saved as update_project saves it.
    private void SetMix(ProjectOptions options)
    {
        if (_playing)
        {
            Pause();
        }

        Apply(new UpdateProjectOperation(options));
    }

    private void OnLoudnessTarget(object sender, RoutedEventArgs e)
    {
        if (_document?.View is not { } project)
        {
            return;
        }

        var current = project.Audio.LoudnessTarget;
        var menu = new ContextMenu { PlacementTarget = LoudnessTargetButton, Placement = PlacementMode.Bottom };
        var choices = MixerModel.Targets.Select(t => ($"{t.Name} {MixerModel.Number(t.Lufs)} LUFS", (double?)t.Lufs)).ToList();
        if (current is { } other && MixerModel.Targets.All(t => t.Lufs != other))
        {
            choices.Add(($"{MixerModel.Number(other)} LUFS", other));
        }

        choices.Add(("Off: keep the mix's own loudness", null));
        foreach (var (name, target) in choices)
        {
            var item = new MenuItem { Header = name, IsChecked = current == target };
            item.Click += (_, _) =>
            {
                if (current != target)
                {
                    SetMix(new ProjectOptions { LoudnessTarget = target ?? 0 });
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OnAutoDuck(object sender, RoutedEventArgs e) => SetMix(new ProjectOptions { Ducking = AutoDuck.IsChecked == true });

    private void OnDuckSliderMoved(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingDuck && DuckFadeOutText is not null)
        {
            ShowDuckTexts();
        }
    }

    private void OnDuckSliderReleased(object sender, MouseButtonEventArgs e) => CommitDuck(sender);

    private void OnDuckSliderKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            CommitDuck(sender);
        }
    }

    // A ducking slider's value, saved when it changed: the level to the half dB, a fade to the
    // twentieth of a second.
    private void CommitDuck(object sender)
    {
        if (_document?.View.Audio is not { } audio || sender is not Slider slider)
        {
            return;
        }

        if (slider == DuckLevelSlider && Math.Round(slider.Value * 2) / -2 is var level && level != audio.DuckLevel)
        {
            SetMix(new ProjectOptions { DuckLevel = level == 0 ? 0 : level });
        }
        else if (slider == DuckFadeInSlider && Math.Round(slider.Value * 20) / 20 is var fadeIn && fadeIn != audio.DuckFadeIn)
        {
            SetMix(new ProjectOptions { DuckFadeIn = fadeIn });
        }
        else if (slider == DuckFadeOutSlider && Math.Round(slider.Value * 20) / 20 is var fadeOut && fadeOut != audio.DuckFadeOut)
        {
            SetMix(new ProjectOptions { DuckFadeOut = fadeOut });
        }
    }

    // Delete in the Audio view: the volume keyframe selected on the lanes.
    private void DeleteVolumeKey() => WhenSaved(() =>
    {
        if (_document is not null && VolumeLanes.Selected is { } key)
        {
            Apply(new RemoveKeyframesOperation([key]));
        }
    });

    // Measure: audio_levels' measurement of the mix as the export makes it, in the background.
    private void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (_document is not { } document || _loudness is not null)
        {
            return;
        }

        var project = ProjectJson.Copy(document.View);
        var directory = document.Session.Directory;
        var cancel = new CancellationTokenSource();
        _loudness = cancel;
        MeasureText.Text = "Measuring…";
        MeasureButton.IsEnabled = false;
        _ = Task.Run(() =>
        {
            string? key = null;
            AudioLevelsReport? report = null;
            string? error = null;
            try
            {
                key = PreviewAudio.Key(project, directory);
                report = AudioLevels.Measure(project, directory, Ffmpeg(), AudioLevels.DefaultSection, done => Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_loudness, cancel))
                    {
                        MeasureText.Text = $"Measuring… {Math.Round(done * 100)}%";
                    }
                }), cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_loudness, cancel))
                {
                    return;
                }

                _loudness = null;
                _measured = (key, report, error);
                ShowLoudness();
            });
        });
    }

    // The last measurement while the mix is as it measured it; what Measure does before one.
    private void ShowLoudness()
    {
        var project = _document!.View;
        if (_loudness is null)
        {
            MeasureText.Text = "Measure";
            MeasureButton.IsEnabled = true;
        }

        string? key;
        try
        {
            key = PreviewAudio.Key(project, _document.Session.Directory);
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
        {
            key = null;
        }

        if (_measured is { } measured && measured.Key == key)
        {
            if (measured.Report is { } report)
            {
                LoudnessLufs.Text = double.IsFinite(report.Lufs) ? report.Lufs.ToString("0.0", CultureInfo.InvariantCulture) : "-inf";
                LoudnessPeak.Text = double.IsFinite(report.PeakDb) ? report.PeakDb.ToString("0.0", CultureInfo.InvariantCulture) : "-inf";
                LoudnessText.Text = MixerModel.LoudnessNote(report, project.Audio.LoudnessTarget);
                return;
            }

            LoudnessLufs.Text = LoudnessPeak.Text = "-";
            LoudnessText.Text = measured.Error is { } error ? $"It can't be measured: {error}" : "Nothing in the project is heard.";
            return;
        }

        LoudnessLufs.Text = LoudnessPeak.Text = "-";
        LoudnessText.Text = project.Audio.LoudnessTarget is { } target
            ? $"Every export is normalized to {MixerModel.Number(target)} LUFS. Measure reads the mix as the export makes it."
            : "Normalization is off: exports keep the mix's own loudness. Measure reads it.";
    }

    // The peaks of the mix the preview plays, read in the background for the Master meter.
    private void RequestMeter()
    {
        if (_sound?.Mix is not { } path || _meter?.Path == path || _meterWanted == path)
        {
            return;
        }

        _meterWanted = path;
        _ = Task.Run(() =>
        {
            float[]? peaks = null;
            try
            {
                peaks = AudioAnalyzer.Peaks(Ffmpeg().Ffmpeg, path, MeterPerSecond, CancellationToken.None, channels: 2);
            }
            catch (Exception ex) when (ex is ZeyoCutException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (_meterWanted == path)
                {
                    _meterWanted = null;
                    _meter = peaks is null ? null : (path, peaks);
                }
            });
        });
    }

    private const int MeterPerSecond = 50;

    private void ShowMeter(double seconds)
    {
        if (_meter is not { } meter || meter.Path != _sound?.Mix)
        {
            return;
        }

        var level = MixerModel.MeterLevel(meter.Peaks, MeterPerSecond, seconds);
        MasterMeter.Level = level;
        var loudest = Math.Max(level.Left, level.Right);
        MasterPeak.Text = double.IsFinite(loudest) ? loudest.ToString("0.0", CultureInfo.InvariantCulture) : "-inf";
    }
}
