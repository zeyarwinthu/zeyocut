// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// Where the window was and how big it was, to open it there again.
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

// The editor's own settings, in %LOCALAPPDATA%\ZeyoCut\editor.json (or the file ZEYOCUT_EDITOR_SETTINGS
// names) and never in a project: the recent projects, newest first, and the panel sizes and window
// placement as you left them.
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

    public WindowPlacement? Window { get; set; }

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
