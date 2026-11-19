// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// Where the window was and how big it was, to open it there again.
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

// The editor's own settings, in %LOCALAPPDATA%\ZeyoCut\editor.json (or the file ZEYOCUT_EDITOR_SETTINGS
// names) and never in a project: the recent projects, newest first, the panel sizes and window
// placement as you left them, the playback size, whether playback loops, the preview's guides and
// language, whether the timeline snaps, the Animate view's onion skin and motion path, and the
// preferences: how long a picture added to the timeline lasts, whether another program's changes
// glow, what the Export view starts with, and how the Render queue renders.
public sealed class EditorSettings
{
    public const int MaxRecent = 10;
    public const string PathVariable = "ZEYOCUT_EDITOR_SETTINGS";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultPath => Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path
        ? Path.GetFullPath(path)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeyoCut", "editor.json");

    public List<string> Recent { get; set; } = [];

    public double MediaWidth { get; set; } = 280;

    public double InspectorWidth { get; set; } = 300;

    public double TimelineHeight { get; set; } = 260;

    // The size frames are drawn at while playing, as a share of the canvas: 1, 0.5 or 0.25. Paused
    // frames are always drawn at the canvas size.
    public double PlaybackScale { get; set; } = 0.5;

    public bool Snapping { get; set; } = true;

    public bool Loop { get; set; }

    // The preview's safe-area guide and thirds grid, drawn over the frame and never into it.
    public bool SafeArea { get; set; }

    public bool Grid { get; set; }

    // Home shows the projects as a list, not as cards.
    public bool ProjectsAsList { get; set; }

    // The language whose version the preview shows, as an export in it shows it: en, ja. Projects
    // without layers in it show English, else their first language.
    public string? PreviewLanguage { get; set; }

    // The Animate view's onion skin: on or off, and how many frames it draws before and after the
    // playhead (0 to 5); and whether the selected layer's motion path is drawn.
    public bool OnionSkin { get; set; } = true;

    public int OnionBefore { get; set; } = 3;

    public int OnionAfter { get; set; } = 2;

    public bool MotionPath { get; set; } = true;

    // How long a picture added to the timeline lasts, in seconds (MinStill to MaxStill).
    public double StillLength { get; set; } = AddClipOperation.StillLength;

    // The timeline's clips that another program (Claude) just changed glow for a moment.
    public bool ShowChanges { get; set; } = true;

    // The preset (a preset's name or "project", ExportChoices.ProjectSettings) and quality (High,
    // Balanced, Small) the Export view starts with.
    public string? ExportPreset { get; set; }

    public string? ExportQuality { get; set; }

    // The Render queue: its exports render on one thread so editing stays smooth, use the GPU encoder
    // when one works (else libx264), and flash the taskbar button when one finishes while the window
    // isn't in front.
    public bool GentleExports { get; set; }

    public bool GpuEncoder { get; set; } = true;

    public bool NotifyExports { get; set; } = true;

    public WindowPlacement? Window { get; set; }

    public const double MinStill = 0.1;

    public const double MaxStill = 3600;

    // The settings saved at path; the defaults when there are none or they can't be read.
    public static EditorSettings Load(string path)
    {
        EditorSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = null;
        }

        settings ??= new EditorSettings();
        settings.Recent = settings.Recent?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? [];
        if (!(settings.StillLength >= MinStill && settings.StillLength <= MaxStill))
        {
            settings.StillLength = AddClipOperation.StillLength;
        }
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }

    // Puts the project folder first in the recent list, once, keeping the newest MaxRecent.
    public void AddRecent(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Recent.RemoveAll(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, full);
        if (Recent.Count > MaxRecent)
        {
            Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }
    }

    // The recent projects that are still there.
    public IReadOnlyList<string> ExistingRecent() => Recent.Where(ProjectStore.IsProjectDirectory).ToList();
}
