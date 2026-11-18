// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// A canvas the New project view offers: its size (none for Custom) and the outline its card draws.
public sealed record CanvasChoice(string Name, int? Width, int? Height, double ShapeWidth, double ShapeHeight)
{
    public string Size => Width is { } width && Height is { } height ? $"{width}×{height}" : "Any size";

    public bool Selected { get; init; }
}

// What a new project starts from: blank, or a library template by name.
public sealed record StartChoice(string Label, string? Template);

// A new project as the New project view describes it.
public sealed record NewProjectRequest(string Name, string? Template, int Width, int Height, int Fps, string BackgroundColor);

// The New project view's choices, and making the project: a blank one as create_project makes it,
// one from a template as new_project_from_template does, in the projects folder.
public static class NewProjectChoices
{
    public const string Custom = "Custom";

    public const string Blank = "Blank project";

    public static readonly IReadOnlyList<CanvasChoice> Canvases =
    [
        new("YouTube", 1920, 1080, 28, 16),
        new("HD 720p", 1280, 720, 24, 14),
        new("Shorts", 1080, 1920, 14, 26),
        new("Square", 1080, 1080, 22, 22),
        new("Portrait 4:5", 1080, 1350, 20, 25),
        new(Custom, null, null, 24, 18),
    ];

    // Where each export preset's videos go, for the note under the preview.
    private static readonly Dictionary<string, string> PresetUses = new(StringComparer.Ordinal)
    {
        [Presets.Youtube1080p.Name] = "YouTube",
        [Presets.Shorts1080p.Name] = "Shorts, Reels and TikTok",
    };

    // Blank, then the library's templates; none when the library can't be read.
    public static IReadOnlyList<StartChoice> Starts(Library library)
    {
        List<string> templates;
        try
        {
            templates = library.Names(LibraryKind.Template);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            templates = [];
        }

        return [new StartChoice(Blank, null), .. templates.Select(t => new StartChoice($"Template: {t}", t))];
    }

    // The canvas card for a size: the one of that size, or Custom.
    public static CanvasChoice CanvasOf(int width, int height) =>
        Canvases.FirstOrDefault(c => c.Width == width && c.Height == height) ?? Canvases[^1];

    // A name no project in the projects folder has yet: 'name', then 'name 2' and so on.
    public static string FreeName(string projectsRoot, string name = "New project")
    {
        var candidate = name;
        for (var n = 2; Directory.Exists(Path.Combine(projectsRoot, ProjectStore.FolderName(candidate))); n++)
        {
            candidate = $"{name} {n}";
        }

        return candidate;
    }

    // "16:9 · 1920×1080 · 30 fps"; the ratio is left out when it has no small numbers.
    public static string Shape(int width, int height, int fps)
    {
        var divisor = Divisor(Math.Max(width, 1), Math.Max(height, 1));
        var (across, down) = (width / divisor, height / divisor);
        var ratio = across <= 32 && down <= 32 && across > 0 && down > 0 ? $"{across}:{down} · " : "";
        return $"{ratio}{width}×{height} · {fps} fps";
    }

    // Which export preset a project of this size and frame rate fits.
    public static string ExportNote(int width, int height, int fps)
    {
        var sized = Presets.All.Where(p => p.Width == width && p.Height == height).ToList();
        if (sized.FirstOrDefault(p => p.Fps == fps) is { } fits)
        {
            return $"Fits {PresetUses.GetValueOrDefault(fits.Name, fits.Description)} and the {fits.Name} export preset.";
        }

        return sized.Count > 0
            ? $"The {sized[0].Name} export preset has this size at {sized[0].Fps} fps; Export's Project settings keeps {fps} fps."
            : "No export preset has this size; Export's Project settings exports it as it is.";
    }

    // Makes the project in the projects folder, in the folder its name gives, and says where, with
    // what a template couldn't bring along.
    public static (string Folder, IReadOnlyList<string> Warnings) Create(Settings settings, NewProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ZeyoCutException("give the project a name");
        }

        var folder = Path.Combine(settings.ProjectsRoot, ProjectStore.FolderName(request.Name));
        var warnings = new List<string>();
        var project = request.Template is { } template
            ? Templates.Create(new Library(settings.LibraryPath ?? Settings.DefaultLibraryPath), template, folder, request.Name.Trim(), null, warnings)
            : ProjectEditor.NewProject(new NewProjectOptions
            {
                Name = request.Name,
                Width = request.Width,
                Height = request.Height,
                Fps = request.Fps,
                BackgroundColor = request.BackgroundColor,
            });
        ProjectSession.Create(folder, project);
        return (folder, warnings);
    }

    private static int Divisor(int a, int b) => b == 0 ? a : Divisor(b, a % b);
}
