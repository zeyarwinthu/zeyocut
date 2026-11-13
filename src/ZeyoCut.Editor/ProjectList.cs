// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// A project as the start page lists it: its folder's name, the folder, a line about it and when it
// was last saved.
public sealed record ProjectEntry(string Name, string Directory, string Details, DateTime Saved);

public static class ProjectList
{
    // The projects in a folder (each subfolder with a project.json, as list_projects finds them), the
    // one saved last first.
    public static IReadOnlyList<ProjectEntry> In(string root) =>
        !Directory.Exists(root)
            ? []
            : Directory.EnumerateDirectories(root).Where(ProjectStore.IsProjectDirectory).Select(Describe).OrderByDescending(e => e.Saved).ToList();

    // The project folder a path names: the folder itself, or the folder of its project.json.
    public static string FolderOf(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        return File.Exists(full) && string.Equals(Path.GetFileName(full), ProjectStore.ProjectFileName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(full)!
            : full;
    }

    public static ProjectEntry Describe(string directory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var saved = File.GetLastWriteTime(Path.Combine(directory, ProjectStore.ProjectFileName));
        try
        {
            var project = ProjectStore.Load(directory);
            var layers = project.Layers.Count == 1 ? "1 layer" : $"{project.Layers.Count} layers";
            return new ProjectEntry(name, directory, $"{project.Width}×{project.Height} · {project.Fps} fps · {Timecode.Short(project.Duration)} · {layers}", saved);
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
        {
            return new ProjectEntry(name, directory, $"can't be read: {ex.Message}", saved);
        }
    }
}
