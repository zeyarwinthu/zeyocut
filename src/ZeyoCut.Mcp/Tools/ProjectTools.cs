// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "create_project")]
    [Description("Create a new project folder (project.json + assets/ renders/ exports/) and open it. Width and height must be even. preset youtube_1080p (1920x1080) or shorts_1080p (1080x1920 vertical) sets the canvas and frame rate. Exports are loudness-normalized to -14 LUFS with music ducked under voice unless changed with update_project.")]
    public static string CreateProject(
        EditorState state,
        [Description("Project name. Also used as the folder name under the projects root.")] string name,
        [Description("Width in pixels (even). Default 1920, or the preset's.")] int? width = null,
        [Description("Height in pixels (even). Default 1080, or the preset's.")] int? height = null,
        [Description("Frames per second. Default 30, or the preset's.")] int? fps = null,
        [Description("Duration in seconds. Default 10.")] double duration = 10,
        [Description("Background color #RRGGBB or #RRGGBBAA. Default #FFFFFF.")] string backgroundColor = "#FFFFFF",
        [Description("Optional absolute folder for the project inside the projects root, instead of <projectsRoot>/<name>.")] string? directory = null,
        [Description("youtube_1080p or shorts_1080p.")] string? preset = null) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ZeyoCutException("name is required");
        }

        var chosen = string.IsNullOrWhiteSpace(preset) ? null : Presets.Get(preset);
        var folder = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(state.Settings.ProjectsRoot, SafeFolderName(name))
            : AllowedFolders.Project(state.Settings, directory);
        var project = new Project
        {
            Name = name.Trim(),
            Width = width ?? chosen?.Width ?? 1920,
            Height = height ?? chosen?.Height ?? 1080,
            Fps = fps ?? chosen?.Fps ?? 30,
            Duration = duration,
            BackgroundColor = backgroundColor,
        };
        if (chosen is not null)
        {
            project.Audio.LoudnessTarget = chosen.Loudness;
        }

        var session = ProjectSession.Create(folder, project);
        state.Open(session);
        return $"created and opened project '{project.Name}' at {session.Directory}{(chosen is null ? "" : $" (preset {chosen.Name}: {chosen.Description})")}\n"
            + $"{Summary(project)}\n"
            + $"audio: loudness {ProjectEditor.Loudness(project.Audio.LoudnessTarget)}, music ducking {(project.Audio.Ducking ? "on" : "off")}\n"
            + "next: import_asset / import_video, then add_image_layer / add_text_layer / add_audio_layer / add_video_layer";
    });

    [McpServerTool(Name = "update_project")]
    [Description("Change the open project's settings. Only the parameters you pass change. Keyframe times are in seconds, so changing fps keeps the timing; changing the canvas size does not move layers (for landscape footage on a new vertical canvas use reframe_to_vertical). Extend the duration when a voiceover or music runs past the end; the result warns about audio that would be cut and keyframes or layers past a shorter end. Audio: loudnessTarget (LUFS, two-pass loudnorm at export; 0 = off) and ducking (music under voice).")]
    public static string UpdateProject(
        EditorState state,
        [Description("New display name (the folder is not renamed).")] string? name = null,
        [Description("Canvas width in pixels (even, 16-8192).")] int? width = null,
        [Description("Canvas height in pixels (even, 16-8192).")] int? height = null,
        [Description("Frames per second (1-120).")] int? fps = null,
        [Description("Duration in seconds.")] double? duration = null,
        [Description("Background color #RRGGBB or #RRGGBBAA.")] string? backgroundColor = null,
        [Description("youtube_1080p (1920x1080) or shorts_1080p (1080x1920): sets width, height, fps and loudness.")] string? preset = null,
        [Description("Export loudness in LUFS, -40..-5 (-14 for YouTube and most platforms); 0 turns normalization off.")] double? loudnessTarget = null,
        [Description("Duck music (role music) under voice (role voice) in the export.")] bool? ducking = null) => Guard(() =>
    {
        var session = state.Session;
        var update = session.Mutate("update_project", p => ProjectEditor.UpdateProject(p, new ProjectOptions
        {
            Name = name,
            Width = width,
            Height = height,
            Fps = fps,
            Duration = duration,
            BackgroundColor = backgroundColor,
            Preset = preset,
            LoudnessTarget = loudnessTarget,
            Ducking = ducking,
        }));
        var project = session.Snapshot();
        return WithWarnings($"updated project '{project.Name}': {string.Join("; ", update.Changes)}\n{Summary(project)}", update.Warnings);
    });

    [McpServerTool(Name = "open_project")]
    [Description("Open an existing project by name (a folder under the projects root) or by absolute folder path. Returns the layer tree.")]
    public static string OpenProject(
        EditorState state,
        [Description("Project name from list_projects, or an absolute path to a project folder inside the projects root.")] string nameOrPath) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            throw new ZeyoCutException("nameOrPath is required");
        }

        var directory = state.ResolveProjectDirectory(nameOrPath);
        if (!ProjectStore.IsProjectDirectory(directory))
        {
            throw new ZeyoCutException($"no project found at '{directory}'; projects: {Ids.List(FindProjects(state).Select(p => p.Name))}");
        }

        ProjectSession session;
        try
        {
            session = ProjectSession.Open(directory);
        }
        catch (ZeyoCutException ex) when (ProjectBackups.List(directory) is [var newest, ..])
        {
            throw new ZeyoCutException($"{ex.Message}\nthe newest backup is {Path.Combine(ProjectStore.BackupsFolder, newest.Name)}.json ({When(newest)}); copy it over {ProjectStore.ProjectFileName} to go back to it", ex);
        }

        state.Open(session);
        var project = session.Snapshot();
        var problems = ProjectValidator.Validate(project);
        var text = $"opened {session.Directory}\n";
        if (session.MigratedFrom is { } from)
        {
            text += $"updated project.json from format {from} (saved by an older ZeyoCut) to {Project.CurrentFormatVersion}; "
                + $"the file as it was is kept as {Path.GetRelativePath(session.Directory, session.OriginalCopy!)}\n";
        }

        text += LayerTree.Format(project);
        text += state.StartProxies(session).TrimStart('\n');
        return WithWarnings(text, problems.Select(p => "project.json problem: " + p));
    });

    [McpServerTool(Name = "get_project")]
    [Description("Return the full project model (project.json) of the open project.")]
    public static string GetProject(EditorState state) => Guard(() => ProjectJson.Serialize(state.Session.Snapshot()));

    [McpServerTool(Name = "list_projects")]
    [Description("List the projects in the projects root folder.")]
    public static string ListProjects(EditorState state) => Guard(() =>
    {
        var projects = FindProjects(state);
        var builder = new StringBuilder($"projects root: {state.Settings.ProjectsRoot}\n");
        if (projects.Count == 0)
        {
            builder.Append("(no projects yet; use create_project)");
            return builder.ToString();
        }

        var open = state.HasSession ? state.Session.Directory : null;
        foreach (var (name, directory, summary) in projects)
        {
            var marker = string.Equals(directory, open, StringComparison.OrdinalIgnoreCase) ? "  [open]" : "";
            builder.AppendLine($"- {name}: {summary}{marker}");
        }

        return builder.ToString().TrimEnd();
    });

    [McpServerTool(Name = "undo")]
    [Description("Undo the last change to the open project (in-memory history of this session, up to 200 steps).")]
    public static string Undo(EditorState state) => Guard(() =>
    {
        var session = state.Session;
        var label = session.Undo();
        return $"undid: {label} ({session.UndoCount} more undo step(s), {session.RedoCount} redo step(s))";
    });

    [McpServerTool(Name = "redo")]
    [Description("Redo the last undone change.")]
    public static string Redo(EditorState state) => Guard(() =>
    {
        var session = state.Session;
        var label = session.Redo();
        return $"redid: {label} ({session.UndoCount} undo step(s), {session.RedoCount} more redo step(s))";
    });

    [McpServerTool(Name = "restore_backup")]
    [Description("List the open project's backups, newest first, or put one back. Before every save the project.json being replaced is copied to the project's backups folder, named after the edit saved over it; the last 20 are kept. When open_project updates a file from an older ZeyoCut, the file as it was is kept there too, for good. Backups hold project.json only, not the assets. Restoring is one undo step, and what it replaces is backed up as well.")]
    public static string RestoreBackup(
        EditorState state,
        [Description("A backup's name from the list; its time (e.g. 20261104-161502-123) is enough. Omit to list them.")] string? backup = null) => Guard(() =>
    {
        var session = state.Session;
        if (string.IsNullOrWhiteSpace(backup))
        {
            var backups = ProjectBackups.List(session.Directory);
            if (backups.Count == 0)
            {
                return $"no backups yet in {ProjectBackups.Folder(session.Directory)}; one is kept before every save";
            }

            var builder = new StringBuilder($"{backups.Count} backup(s) in {ProjectBackups.Folder(session.Directory)}, newest first:\n");
            foreach (var item in backups)
            {
                string summary;
                try
                {
                    var saved = ProjectBackups.Load(item);
                    summary = $"{saved.Layers.Count} layer(s), {Num(saved.Duration)}s";
                }
                catch (ZeyoCutException ex)
                {
                    summary = ex.Message;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    summary = $"can't be read: {ex.Message}";
                }

                builder.Append($"- {item.Name}: {When(item)}; {summary}\n");
            }

            builder.Append("restore one with restore_backup backup=\"<name>\" (one undo step)");
            return builder.ToString();
        }

        var found = ProjectBackups.Find(session.Directory, backup);
        session.Replace($"restore_backup {found.Name.Split(" before ")[0]}", ProjectBackups.Load(found));
        return $"restored {found.Name} ({When(found)}); undo puts back what was there\n{LayerTree.Format(session.Snapshot())}";
    });

    private static string When(ProjectBackup backup)
    {
        var time = backup.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return backup.Format is { } format ? $"{time}, as format {format} saved it, before open_project updated it" : time;
    }

    private static List<(string Name, string Directory, string Summary)> FindProjects(EditorState state)
    {
        var root = state.Settings.ProjectsRoot;
        var result = new List<(string, string, string)>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!ProjectStore.IsProjectDirectory(directory))
            {
                continue;
            }

            string summary;
            try
            {
                var project = ProjectStore.Load(directory);
                summary = $"\"{project.Name}\" {project.Width}x{project.Height} @ {project.Fps} fps, {Num(project.Duration)}s, {project.Layers.Count} layer(s), modified {File.GetLastWriteTime(Path.Combine(directory, ProjectStore.ProjectFileName)):yyyy-MM-dd HH:mm}";
            }
            catch (ZeyoCutException ex)
            {
                summary = $"unreadable: {ex.Message}";
            }

            result.Add((Path.GetFileName(directory), directory, summary));
        }

        return result;
    }

    private static string Summary(Project project) =>
        $"{project.Width}x{project.Height} @ {project.Fps} fps, {Num(project.Duration)}s ({FrameMath.FrameCount(project)} frames), background {project.BackgroundColor}";

    internal static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return cleaned.Length == 0 ? "project" : cleaned;
    }
}
