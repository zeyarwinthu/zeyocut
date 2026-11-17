// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;

namespace ZeyoCut.Editor;

// A preset the Export view offers: the project's own settings (no Preset) or one of the presets,
// with why it doesn't fit the project when it doesn't. Its card has a title and the size and frame
// rate exported, or else, short, what it needs.
public sealed record ExportPreset(string Name, string Description, Preset? Preset, string? Unusable)
{
    public bool Usable => Unusable is null;

    public string Tip => Unusable ?? Description;

    public string Title { get; init; } = Name;

    public string Size { get; init; } = "";

    public string? Needs { get; init; }

    public string Detail => Needs ?? Size;
}

// A quality the Export view offers: start_export's crf, lower being better and bigger.
public sealed record ExportQuality(string Name, int Crf, string Tip);

// A file in the project's exports folder; Details has its size and when it was saved.
public sealed record ExportFile(string Name, string Path, long Size, DateTime Saved)
{
    public string Details { get; init; } = "";
}

// A caption group the Export view can also save as an .srt file: by its language (EN), or as
// Captions when it has none.
public sealed record SubtitleChoice(string GroupId, string? Language, string Label);

// The .srt file a caption group is saved in, next to the export.
public sealed record SrtFile(string GroupId, string Name);

// An export a ZeyoCut server runs for Claude: the files it writes and how far it is.
public sealed record RunningExport(string Name, double Percent, string Status)
{
    public string By => "Started by Claude";
}

// What the Export view offers for a project. Exports start the way start_export starts them.
public static class ExportChoices
{
    public const string ProjectSettings = "Project settings";

    public const string EveryLanguage = "Every layer";

    public static readonly IReadOnlyList<ExportQuality> Qualities =
    [
        new("High", 18, "crf 18, the best picture and the biggest file"),
        new("Balanced", 23, "crf 23, a smaller file that looks much the same"),
        new("Small", 28, "crf 28, the smallest file and a softer picture"),
    ];

    // A preset scales the frame to its size and keeps the project's frame rate.
    public static IReadOnlyList<ExportPreset> Presets(Project project) =>
    [
        new(ProjectSettings, $"{project.Width}×{project.Height} at {project.Fps} fps, as the project is set up", null, null) { Size = Size(project.Width, project.Height, project.Fps) },
        .. Core.Editing.Presets.All.Select(p => new ExportPreset(p.Name, p.Description, p, Exporter.SameShape(project, p)
            ? null
            : $"{p.Name} is {p.Width}×{p.Height}, another shape than the project's {project.Width}×{project.Height}; ask Claude to change the canvas first")
        {
            Title = $"{p.Description.Split(' ')[0]} {Math.Min(p.Width, p.Height)}p",
            Size = Size(p.Width, p.Height, project.Fps),
            Needs = Exporter.SameShape(project, p) ? null : $"Needs a {Shape(p.Width, p.Height)} project",
        }),
    ];

    // The project folder's name, as an .mp4.
    public static string DefaultFileName(string directory) => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) + ".mp4";

    // Every layer, and then one version per language the project's layers are tagged with; none
    // when no layer is tagged.
    public static IReadOnlyList<string> Languages(Project project) =>
        Core.Editing.Languages.Used(project) is { Count: > 0 } used ? [EveryLanguage, .. used] : [];

    // The MP4 files in the project's exports folder, newest first.
    public static IReadOnlyList<ExportFile> Files(string directory, DateTime? now = null)
    {
        var folder = Path.Combine(directory, ProjectStore.ExportsFolder);
        var at = now ?? DateTime.Now;
        return !Directory.Exists(folder)
            ? []
            : new DirectoryInfo(folder).EnumerateFiles("*.mp4")
                .Where(f => !f.Name.Contains(".partial.", StringComparison.OrdinalIgnoreCase))
                .Select(f => new ExportFile(f.Name, f.FullName, f.Length, f.LastWriteTime)
                {
                    Details = $"{(f.Length / 1048576.0).ToString(f.Length < 10 * 1048576L ? "0.0" : "0", CultureInfo.InvariantCulture)} MB · {ProjectList.When(f.LastWriteTime, at)}",
                })
                .OrderByDescending(f => f.Saved)
                .ToList();
    }

    // The caption groups with captions in them, which export_srt can write.
    public static IReadOnlyList<SubtitleChoice> Subtitles(Project project) =>
        project.Layers
            .Where(l => l.ParentId is null && TimelineLayout.IsCaptionGroup(l) && HasCues(project, l.Id))
            .Select(l => TimelineLayout.CaptionLanguage(l) is { } language ? new SubtitleChoice(l.Id, language, language.ToUpperInvariant()) : new SubtitleChoice(l.Id, null, "Captions"))
            .OrderBy(s => s.Label, StringComparer.Ordinal)
            .ThenBy(s => s.GroupId, StringComparer.Ordinal)
            .ToList();

    // The .srt files saved next to an export: talk.mp4's English captions in talk.en.srt (the name
    // YouTube reads the language from), a group without a language in talk.srt, and a second group
    // in the same language by its id (talk.captions_en.srt).
    public static IReadOnlyList<SrtFile> SrtFiles(string exportName, IEnumerable<SubtitleChoice> chosen)
    {
        var name = Path.GetFileNameWithoutExtension(exportName);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<SrtFile>();
        foreach (var subtitle in chosen)
        {
            var file = CaptionBuilder.SrtFileName(subtitle.Language is { } language ? $"{name}.{language}.srt" : $"{name}.srt");
            if (!used.Add(file))
            {
                file = CaptionBuilder.SrtFileName($"{name}.{subtitle.GroupId}.srt");
                used.Add(file);
            }

            files.Add(new SrtFile(subtitle.GroupId, file));
        }

        return files;
    }

    // The summary card's captions: the groups drawn in the version exported (every layer, or the
    // layers in one language and those without one) and the .srt files saved with it.
    public static string CaptionsSummary(Project project, string? language, IReadOnlyList<SubtitleChoice> srt)
    {
        var burned = Subtitles(project)
            .Where(s => project.FindLayer(s.GroupId) is { Visible: true } group
                && (language is null || Core.Editing.Languages.VersionOf(project, group) is not { } version || version == language))
            .Select(s => s.Label)
            .ToList();
        var files = srt.Select(s => s.Label).ToList();
        var inVideo = burned.Count > 0 ? $"{string.Join(", ", burned)} burned in" : null;
        var saved = files.Count == 0 ? null : files.SequenceEqual(burned) ? ".srt" : $"{string.Join(", ", files)} .srt";
        return inVideo is null ? saved ?? "None" : saved is null ? inVideo : $"{inVideo} + {saved}";
    }

    // The exports a ZeyoCut server runs for the project in the folder, from the records of its
    // running jobs (start_export, export_batch, export_language_versions).
    public static IReadOnlyList<RunningExport> ServerExports(IEnumerable<JobRecord> running, string directory)
    {
        var exports = new List<RunningExport>();
        foreach (var record in running)
        {
            if (record.Resume is not { } resume || resume["projectDirectory"]?.GetValue<string>() is not { } folder || !SameFolder(folder, directory))
            {
                continue;
            }

            List<string> outputs = resume["outputPath"]?.GetValue<string>() is { } output
                ? [output]
                : resume["outputs"] is JsonArray list ? list.Select(o => o?.GetValue<string>()).OfType<string>().ToList() : [];
            if (outputs.Count == 0)
            {
                continue;
            }

            var percent = Math.Round(record.Progress * 100);
            var stage = string.IsNullOrEmpty(record.Stage) ? "rendering" : record.Stage;
            exports.Add(new RunningExport(string.Join(", ", outputs.Select(o => Path.GetFileName(o))), percent,
                $"{char.ToUpperInvariant(stage[0])}{stage[1..]} · {percent.ToString("0", CultureInfo.InvariantCulture)}%"));
        }

        return exports;
    }

    private static string Size(int width, int height, int fps) => $"{width}×{height} · {fps} fps";

    // A frame's shape as its ratio: 9:16, 16:9.
    private static string Shape(int width, int height)
    {
        int a = width, b = height;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return $"{width / a}:{height / a}";
    }

    private static bool HasCues(Project project, string groupId)
    {
        try
        {
            CaptionBuilder.GroupCues(project, groupId);
            return true;
        }
        catch (ZeyoCutException)
        {
            return false;
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
