// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor;

// A preset the Export view offers: the project's own settings (no Preset) or one of the presets,
// with why it doesn't fit the project when it doesn't.
public sealed record ExportPreset(string Name, string Description, Preset? Preset, string? Unusable)
{
    public bool Usable => Unusable is null;

    public string Tip => Unusable ?? Description;
}

// A file in the project's exports folder.
public sealed record ExportFile(string Name, string Path, long Size, DateTime Saved)
{
    public string Details => $"{(Size / 1048576.0).ToString(Size < 10 * 1048576L ? "0.0" : "0", CultureInfo.InvariantCulture)} MB · saved {Saved:yyyy-MM-dd HH:mm}";
}

// What the Export view offers for a project. Exports start the way start_export starts them.
public static class ExportChoices
{
    public const string ProjectSettings = "Project settings";

    public const string EveryLanguage = "Every layer";

    public static IReadOnlyList<ExportPreset> Presets(Project project) =>
    [
        new(ProjectSettings, $"{project.Width}×{project.Height} at {project.Fps} fps, as the project is set up", null, null),
        .. Core.Editing.Presets.All.Select(p => new ExportPreset(p.Name, p.Description, p, Exporter.SameShape(project, p)
            ? null
            : $"{p.Name} is {p.Width}×{p.Height}, another shape than the project's {project.Width}×{project.Height}; ask Claude to change the canvas first")),
    ];

    // The project folder's name, as an .mp4.
    public static string DefaultFileName(string directory) => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) + ".mp4";

    // Every layer, and then one version per language the project's layers are tagged with; none
    // when no layer is tagged.
    public static IReadOnlyList<string> Languages(Project project) =>
        Core.Editing.Languages.Used(project) is { Count: > 0 } used ? [EveryLanguage, .. used] : [];

    // The MP4 files in the project's exports folder, newest first.
    public static IReadOnlyList<ExportFile> Files(string directory)
    {
        var folder = Path.Combine(directory, ProjectStore.ExportsFolder);
        return !Directory.Exists(folder)
            ? []
            : new DirectoryInfo(folder).EnumerateFiles("*.mp4")
                .Where(f => !f.Name.Contains(".partial.", StringComparison.OrdinalIgnoreCase))
                .Select(f => new ExportFile(f.Name, f.FullName, f.Length, f.LastWriteTime))
                .OrderByDescending(f => f.Saved)
                .ToList();
    }
}
