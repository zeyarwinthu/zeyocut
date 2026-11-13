// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// The project as the manual editor has it open. Every edit is saved at once, through the same
// session code the server saves with (project.lock and the content check), and backups label it
// "editor <edit>". View is the project as the editor shows it; Status is the line for its status bar.
//
// When another program saved since View was taken, the edit is made on top of that version, unless
// it changed a layer the edit works on: then nothing is saved, Status says why, and View becomes
// that version, so you see what the other program did before you try again.
public sealed class EditorDocument
{
    private readonly List<OutsideChange> _outside = [];

    private EditorDocument(ProjectSession session)
    {
        Session = session;
        View = session.Snapshot();
        Status = Saved();
    }

    public ProjectSession Session { get; }

    public Project View { get; private set; }

    public string Status { get; private set; }

    // What the last edit saved did, in its operation's words; null when it wasn't saved.
    public string? Summary { get; private set; }

    public long Revision => View.Revision;

    public IReadOnlyList<HistoryEntry> History => Session.History;

    public bool CanUndo => Session.UndoCount > 0;

    public bool CanRedo => Session.RedoCount > 0;

    public static EditorDocument Open(string directory) => new(ProjectSession.Open(directory, ProjectBackups.EditorProgram));

    // True when the edit was saved.
    public bool Apply(EditOperation operation)
    {
        var shown = View;
        string? changed = null;
        string summary;
        try
        {
            summary = Session.Mutate(operation.Label, project =>
            {
                changed = operation.Layers.FirstOrDefault(id => LayerJson(shown, id) != LayerJson(project, id));
                if (changed is not null)
                {
                    throw new ZeyoCutException($"another program changed layer '{changed}'");
                }

                return operation.Apply(project);
            });
        }
        catch (ZeyoCutException ex)
        {
            Take();
            Summary = null;
            Status = changed is null
                ? $"{operation.Label} wasn't saved: {ex.Message}"
                : $"{operation.Label} wasn't saved: another program {(View.FindLayer(changed) is null ? "removed" : "changed")} layer '{changed}' first{SavedBy()}; this is its version now";
            return false;
        }

        Take();
        Summary = summary;
        Status = Saved();
        return true;
    }

    public bool Undo() => Run("undo", () => Session.Undo());

    public bool Redo() => Run("redo", () => Session.Redo());

    // Takes what another program saved since; true when View changed.
    public bool Refresh()
    {
        var took = Session.Sync();
        var problem = Take();
        if (problem is not null)
        {
            Status = problem;
        }
        else if (took)
        {
            Status = Saved();
        }

        return took;
    }

    // What other programs changed since this was last asked, as one; null when nothing.
    public OutsideChange? TakeOutsideChanges()
    {
        if (_outside.Count == 0)
        {
            return null;
        }

        var merged = OutsideChange.Merge(_outside);
        _outside.Clear();
        return merged;
    }

    private static string? LayerJson(Project project, string id) =>
        project.FindLayer(id) is { } layer ? ProjectJson.LayerToJson(layer) : null;

    private bool Run(string verb, Action action)
    {
        try
        {
            action();
        }
        catch (ZeyoCutException ex)
        {
            Take();
            Status = ex.Message.StartsWith($"can't {verb}", StringComparison.Ordinal) || ex.Message.StartsWith("nothing to", StringComparison.Ordinal)
                ? ex.Message
                : $"can't {verb}: {ex.Message}";
            return false;
        }

        Take();
        Status = Saved();
        return true;
    }

    // Brings View up to the session and keeps what other programs changed; returns the problem
    // when project.json can't be read.
    private string? Take()
    {
        View = Session.Snapshot();
        if (Session.TakeOutsideChanges() is not { } change)
        {
            return null;
        }

        _outside.Add(change);
        return change.Problem is { } problem
            ? $"{ProjectStore.ProjectFileName} can't be read ({problem}); edits aren't saved until it's fixed or restore_backup puts a backup back"
            : null;
    }

    private string SavedBy()
    {
        var edits = _outside.Count > 0 ? _outside[^1].Edits : [];
        return edits.Count == 0 ? "" : $" ({string.Join(", ", edits.TakeLast(3))})";
    }

    private string Saved() => $"Saved · revision {View.Revision}";
}
