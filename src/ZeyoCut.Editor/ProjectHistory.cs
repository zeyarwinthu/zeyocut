// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// A save of project.json, as the copy kept before it in the backups folder records it: when, the
// edit that was saved, and whether the editor saved it or Claude did (through the MCP server).
public sealed record SavedChange(DateTime Time, string Edit, bool ByEditor)
{
    public string Who => ByEditor ? "You" : "Claude";
}

// The project's recent saves, for the Activity view and the header.
public static class ProjectHistory
{
    public const int Shown = 20;

    private const string EditorPrefix = ProjectBackups.EditorProgram + " ";

    // Newest first: the editor's and the server's together, as many as the backups folder keeps.
    public static IReadOnlyList<SavedChange> Saves(string directory) => All(directory).Take(Shown).ToList();

    // Claude's newest save that the backups folder keeps; null when it has none.
    public static SavedChange? LastByClaude(string directory) => All(directory).FirstOrDefault(s => !s.ByEditor);

    // How long ago a save was, as the header says it: "just now", "5 min ago", "3 h ago", or the day.
    public static string Ago(DateTime time, DateTime now)
    {
        var since = now - time;
        return since < TimeSpan.FromMinutes(1) ? "just now"
            : since < TimeSpan.FromHours(1) ? $"{(int)since.TotalMinutes} min ago"
            : since < TimeSpan.FromDays(1) ? $"{(int)since.TotalHours} h ago"
            : $"on {time:yyyy-MM-dd}";
    }

    private static IEnumerable<SavedChange> All(string directory) =>
        ProjectBackups.List(directory)
            .Where(b => b.Before is not null)
            .Select(b => b.Before!.StartsWith(EditorPrefix, StringComparison.Ordinal)
                ? new SavedChange(b.Time, b.Before[EditorPrefix.Length..], true)
                : new SavedChange(b.Time, b.Before, false));
}
