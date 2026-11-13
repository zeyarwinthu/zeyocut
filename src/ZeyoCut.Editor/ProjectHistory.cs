// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// A save of project.json, as the copy kept before it in the backups folder records it: when, the
// edit that was saved, and whether the editor saved it or Claude did (through the MCP server).
public sealed record SavedChange(DateTime Time, string Edit, bool ByEditor)
{
    public string Who => ByEditor ? "You" : "Claude";
}

// The project's recent saves, for the Assistant view.
public static class ProjectHistory
{
    public const int Shown = 20;

    private const string EditorPrefix = ProjectBackups.EditorProgram + " ";

    // Newest first: the editor's and the server's together, as many as the backups folder keeps.
    public static IReadOnlyList<SavedChange> Saves(string directory) =>
        ProjectBackups.List(directory)
            .Where(b => b.Before is not null)
            .Select(b => b.Before!.StartsWith(EditorPrefix, StringComparison.Ordinal)
                ? new SavedChange(b.Time, b.Before[EditorPrefix.Length..], true)
                : new SavedChange(b.Time, b.Before, false))
            .Take(Shown)
            .ToList();
}
