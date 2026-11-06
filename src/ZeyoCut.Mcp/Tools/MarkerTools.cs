// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class MarkerTools
{
    [McpServerTool(Name = "add_markers")]
    [Description("Add or change project markers in one call (one undo step; all-or-nothing): named points on the timeline, each a chapter (the default) or a scene. A marker whose id exists is changed: moved, renamed, and its kind changed only when given. Otherwise it is added, with an id made from its name unless one is given. Markers move with the timeline when remove_range, remove_silences, cut_by_transcript, set_speed or a crossfade ripple it; a marker inside removed time moves to where the cut is. list_layers shows them.")]
    public static string AddMarkers(
        EditorState state,
        [Description("Markers: [{time, name, kind, id}].")] MarkerArg[] markers) => Guard(() =>
    {
        var list = (markers ?? []).Select(m => new MarkerEntry(m.Id, m.Time, m.Name, m.Kind)).ToList();
        var (change, all) = state.Session.Mutate($"add_markers ({list.Count} marker{(list.Count == 1 ? "" : "s")})", p => (MarkerEditor.AddMarkers(p, list), MarkerEditor.List(p)));
        var parts = new List<string>();
        if (change.Added.Count > 0)
        {
            parts.Add($"added {change.Added.Count} marker{(change.Added.Count == 1 ? "" : "s")}: {string.Join(", ", change.Added.Select(m => m.Id))}");
        }

        if (change.Changed.Count > 0)
        {
            parts.Add($"changed {string.Join(", ", change.Changed.Select(m => m.Id))}");
        }

        return WithWarnings(string.Join("; ", parts) + "\n" + all, change.Warnings);
    });

    [McpServerTool(Name = "remove_markers")]
    [Description("Remove project markers (one undo step): the ones named in markerIds, every marker with markerIds [\"all\"], or every marker of one kind with kind.")]
    public static string RemoveMarkers(
        EditorState state,
        [Description("Marker ids, or [\"all\"].")] string[]? markerIds = null,
        [Description("chapter or scene: removes every marker of this kind (with markerIds omitted or [\"all\"]).")] string? kind = null) => Guard(() =>
    {
        var label = markerIds is { Length: > 0 } ? string.Join(", ", markerIds) : "all";
        var (removed, all) = state.Session.Mutate($"remove_markers {label}{(kind is null ? "" : " " + kind)}", p => (MarkerEditor.RemoveMarkers(p, markerIds, kind), MarkerEditor.List(p)));
        return $"removed {removed.Count} marker{(removed.Count == 1 ? "" : "s")}: {string.Join(", ", removed.Select(m => m.Id))}\n{all}";
    });

    [McpServerTool(Name = "export_markers")]
    [Description("Write the chapter markers as a YouTube chapter list to exports/<fileName>, to paste into the video's description: a \"m:ss Name\" line per chapter (\"h:mm:ss\" from an hour on), in whole seconds. Scene markers are left out. YouTube shows chapters only when the first is at 0:00, there are at least 3 and each lasts at least 10 s (the last one runs to the project end); the reply warns about each rule the chapters break.")]
    public static string ExportMarkers(
        EditorState state,
        [Description("Output file name, e.g. \"chapters.txt\" (no folders). Default <project>_chapters.txt.")] string? fileName = null) => Guard(() =>
    {
        var session = state.Session;
        var (project, list, scenes) = session.Read(p => (p.Name, YouTubeChapters.Make(p), p.Markers.Count(m => m.Kind == MarkerKind.Scene)));
        var name = fileName is null ? $"{ProjectTools.SafeFolderName(project)}_chapters.txt" : TextName(fileName);
        var output = Path.Combine(session.Directory, ProjectStore.ExportsFolder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, list.Text, new UTF8Encoding(false));
        var count = list.Chapters.Count;
        var left = scenes == 0 ? "" : $" ({scenes} scene marker{(scenes == 1 ? "" : "s")} left out)";
        return WithWarnings($"wrote {count} chapter{(count == 1 ? "" : "s")} to {output}{left}; paste them into the video's description:\n{list.Text.TrimEnd('\n')}", list.Warnings);
    });

    private static string TextName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ZeyoCutException($"fileName '{fileName}' must be a plain file name like \"chapters.txt\" without folders");
        }

        var name = fileName.Trim();
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            return name + ".txt";
        }

        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ZeyoCutException($"export_markers writes .txt files (got '{extension}')");
        }

        return name;
    }
}
