// SPDX-License-Identifier: MIT

using System.Windows.Media;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// A project as Home shows it: its folder's name, the folder, a line about it and when it was last
// saved, and for its card the revision its picture is of, its canvas, and the badges: its length, how
// many languages it's in, and when Claude edited it, while Claude's is its newest save and is from the
// last day. A project that can't be read has no length; one with no layers is Empty.
public sealed record ProjectEntry(string Name, string Directory, string Details, DateTime Saved)
{
    public long Revision { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool Empty { get; init; }

    public string? Length { get; init; }

    public string? Languages { get; init; }

    public string? ClaudeEdited { get; init; }

    public Brush? Picture { get; init; }

    // An empty project's canvas, drawn as a dashed outline 100 units on its longer side.
    public double EmptyWidth => Width >= Height ? 100 : 100.0 * Width / Math.Max(1, Height);

    public double EmptyHeight => Height >= Width ? 100 : 100.0 * Height / Math.Max(1, Width);
}

// How many cards fit across Home, and how tall their pictures are.
public sealed record HomeLayout(int Columns, double PictureHeight);

public static class ProjectList
{
    public const double CardWidth = 240;
    public const double CardGap = 20;

    // The projects in a folder (each subfolder with a project.json, as list_projects finds them), the
    // one saved last first.
    public static IReadOnlyList<ProjectEntry> In(string root, DateTime? now = null) =>
        !Directory.Exists(root)
            ? []
            : Directory.EnumerateDirectories(root).Where(ProjectStore.IsProjectDirectory).Select(d => Describe(d, now)).OrderByDescending(e => e.Saved).ToList();

    // The project folder a path names: the folder itself, or the folder of its project.json.
    public static string FolderOf(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        return File.Exists(full) && string.Equals(Path.GetFileName(full), ProjectStore.ProjectFileName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(full)!
            : full;
    }

    public static ProjectEntry Describe(string directory, DateTime? now = null)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var saved = File.GetLastWriteTime(Path.Combine(directory, ProjectStore.ProjectFileName));
        var at = now ?? DateTime.Now;
        try
        {
            var project = ProjectStore.Load(directory);
            var layers = project.Layers.Count == 1 ? "1 layer" : $"{project.Layers.Count} layers";
            var languages = Languages.Used(project).Count;
            var newest = ProjectHistory.Saves(directory).FirstOrDefault();
            return new ProjectEntry(name, directory, $"{project.Width}×{project.Height} · {project.Fps} fps · {layers} · {When(saved, at)}", saved)
            {
                Revision = project.Revision,
                Width = project.Width,
                Height = project.Height,
                Empty = project.Layers.Count == 0,
                Length = Timecode.Short(project.Duration),
                Languages = languages > 1 ? $"{languages} languages" : null,
                ClaudeEdited = newest is { ByEditor: false } && at - newest.Time < TimeSpan.FromDays(1) ? $"Claude edited {ProjectHistory.Ago(newest.Time, at)}" : null,
            };
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
        {
            return new ProjectEntry(name, directory, $"can't be read: {ex.Message}", saved);
        }
    }

    // Home's two groups: the recent projects, then the projects folder's others in its order, each
    // project once; both only those whose name has the search text in it.
    public static (IReadOnlyList<ProjectEntry> Recent, IReadOnlyList<ProjectEntry> Others) Group(IReadOnlyList<ProjectEntry> recent, IReadOnlyList<ProjectEntry> folder, string search)
    {
        var text = search.Trim();
        bool Matches(ProjectEntry entry) => entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
        var shown = new HashSet<string>(recent.Select(r => Full(r.Directory)), StringComparer.OrdinalIgnoreCase);
        return (recent.Where(Matches).ToList(), folder.Where(f => !shown.Contains(Full(f.Directory)) && Matches(f)).ToList());
    }

    // When a project was saved, as its card says it: "Today 18:24", "Yesterday 09:10", else the day.
    public static string When(DateTime saved, DateTime now) =>
        saved.Date == now.Date ? $"Today {saved:HH:mm}"
        : saved.Date == now.Date.AddDays(-1) ? $"Yesterday {saved:HH:mm}"
        : $"{saved:yyyy-MM-dd HH:mm}";

    // A folder by its last three names ("…\scratchpad\edemo\projects"); the whole path is in a tooltip.
    public static string Shorten(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var root = Path.GetPathRoot(trimmed) ?? "";
        var names = trimmed[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return names.Length > 3 ? "…" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, names[^3..]) : trimmed;
    }

    // As many cards as fit across width, at least CardWidth wide with CardGap between them.
    public static HomeLayout Layout(double width)
    {
        var columns = Math.Max(1, (int)((width + CardGap) / (CardWidth + CardGap)));
        var card = Math.Max(1, (width + CardGap) / columns - CardGap);
        return new HomeLayout(columns, card * 9 / 16);
    }

    private static string Full(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
}
