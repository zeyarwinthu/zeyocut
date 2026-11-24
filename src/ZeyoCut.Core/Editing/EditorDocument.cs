// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// The project as the manual editor has it open. Every edit is saved at once, through the same
// session code the server saves with (project.lock and the content check), and backups label it
// "editor <edit>". View is the project as the editor shows it; Status is the line for its status bar.
//
// When another program saved since View was taken, the edit is made on top of that version, unless
// it changed a layer the edit works on: then nothing is saved, Status says why, and View becomes
// that version, so you see what the other program did before you try again. An edit built from an
// earlier View (a field typed in, a drag begun before the other program's change was shown) says so,
// and is dropped the same way.
//
// A file or folder that can't be read or written (held by another program, read-only, gone) doesn't
// throw: nothing is saved and Status gives the reason. Status also names the problems a project has
// when it is opened.
//
// The editor saves on a worker thread and takes other programs' changes on its window's thread: an
// edit, an undo or redo, and taking those changes run one at a time.
public sealed class EditorDocument
{
    private readonly object _gate = new();
    private readonly List<OutsideChange> _outside = [];

    // How many changes by other programs each View came after, and for each layer the last of them
    // that changed it, with the edits it saved. The editor's own saves aren't counted.
    private readonly ConditionalWeakTable<Project, StrongBox<int>> _cameAfter = [];
    private readonly Dictionary<string, (int Change, IReadOnlyList<string> Edits)> _changedBy = new(StringComparer.Ordinal);
    private int _changes;

    private const int ProblemsNamed = 3;

    private EditorDocument(ProjectSession session)
    {
        Session = session;
        View = session.Snapshot();
        _cameAfter.Add(View, new StrongBox<int>(0));
        Status = Problems(ProjectValidator.Validate(View)) ?? Saved();
    }

    public ProjectSession Session { get; }

    public Project View { get; private set; }

    public string Status { get; private set; }

    // What the last edit saved did, in its operation's words; null when it wasn't saved.
    public string? Summary { get; private set; }

    // The layer another program changed first, when the last edit wasn't saved for that.
    public string? Overtaken { get; private set; }

    public long Revision => View.Revision;

    public IReadOnlyList<HistoryEntry> History => Session.History;

    public bool CanUndo => Session.UndoCount > 0;

    public bool CanRedo => Session.RedoCount > 0;

    public static EditorDocument Open(string directory) => new(ProjectSession.Open(directory, ProjectBackups.EditorProgram));

    // True when the edit was saved. 'from' is the View the edit was built from, when that isn't the
    // one shown now.
    public bool Apply(EditOperation operation, Project? from = null)
    {
        lock (_gate)
        {
            var shown = View;
            string? changed = null;
            string summary;
            try
            {
                summary = Session.Mutate(operation.Label, project =>
                {
                    changed = operation.Layers.FirstOrDefault(id => (from is not null && ChangedSince(from, id)) || LayerJson(shown, id) != LayerJson(project, id));
                    if (changed is not null)
                    {
                        throw new ZeyoCutException($"another program changed layer '{changed}'");
                    }

                    return operation.Apply(project);
                });
            }
            catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
            {
                Take();
                Summary = null;
                Overtaken = changed;
                Status = changed is null
                    ? $"{operation.Label} wasn't saved: {ex.Message}"
                    : $"Your change wasn't saved: another program {(View.FindLayer(changed) is null ? "removed" : "changed")} '{changed}' first{SavedBy(changed)}. You see its version now.";
                return false;
            }

            Take();
            Summary = summary;
            Overtaken = null;
            Status = Saved();
            return true;
        }
    }

    // After an undo or a redo that was refused, the next one steps over that step, and Status names it.
    public bool Undo()
    {
        lock (_gate)
        {
            var label = "";
            IReadOnlyList<string> over = [];
            if (!Run("undo", () => label = Session.Undo(out over)))
            {
                return false;
            }

            if (over.Count > 0)
            {
                Status = $"Undid '{label}' and stepped over {string.Join(", ", over.Select(l => $"'{l}'"))}, which can't be undone on top of another program's changes. {Saved()}";
            }

            return true;
        }
    }

    public bool Redo()
    {
        lock (_gate)
        {
            var label = "";
            IReadOnlyList<string> over = [];
            if (!Run("redo", () => label = Session.Redo(out over)))
            {
                return false;
            }

            if (over.Count > 0)
            {
                Status = $"Redid '{label}' and stepped over {string.Join(", ", over.Select(l => $"'{l}'"))}, which can't be redone on top of another program's changes. {Saved()}";
            }

            return true;
        }
    }

    // Puts the project back as the backup holds it, the way restore_backup does: saved as the next
    // revision, as one undo step. Like an edit, it isn't saved when another program saved after
    // View was taken, since what it would replace isn't what you saw.
    public bool Restore(ProjectBackup backup)
    {
        lock (_gate)
        {
            var shown = View.Revision;
            var changes = _outside.Count;
            var overtaken = false;
            if (Run("restore", () => Session.Replace(ProjectBackups.RestoreLabel(backup), ProjectBackups.Load(backup), project =>
                {
                    overtaken = project.Revision != shown;
                    if (overtaken)
                    {
                        throw new ZeyoCutException("another program saved first");
                    }
                })))
            {
                return true;
            }

            if (overtaken)
            {
                var edits = _outside.Count > changes ? _outside[^1].Edits : [];
                Status = $"Nothing was restored: another program saved first{(edits.Count == 0 ? "" : $" ({string.Join(", ", edits.TakeLast(3))})")}. You see its version now.";
            }

            return false;
        }
    }

    // Takes what another program saved since; true when View changed.
    public bool Refresh()
    {
        lock (_gate)
        {
            bool took;
            try
            {
                took = Session.Sync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = $"Couldn't check {ProjectStore.ProjectFileName} for another program's changes: {ex.Message}";
                return false;
            }

            var problem = Take(took);
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
    }

    // What other programs changed since this was last asked, as one; null when nothing.
    public OutsideChange? TakeOutsideChanges()
    {
        lock (_gate)
        {
            if (_outside.Count == 0)
            {
                return null;
            }

            var merged = OutsideChange.Merge(_outside);
            _outside.Clear();
            return merged;
        }
    }

    private static string? LayerJson(Project project, string id) =>
        project.FindLayer(id) is { } layer ? ProjectJson.LayerToJson(layer) : null;

    // Whether another program changed the layer after 'from' was the View. A project that never was
    // the View is compared with it instead.
    private bool ChangedSince(Project from, string id) =>
        _cameAfter.TryGetValue(from, out var seen)
            ? _changedBy.TryGetValue(id, out var by) && by.Change > seen.Value
            : LayerJson(from, id) != LayerJson(View, id);

    private bool Run(string verb, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
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
    // when project.json can't be read. After a check that took nothing (changed false), View stays
    // unless another program's change came in after all: copying the project takes time that grows
    // with it.
    private string? Take(bool changed = true)
    {
        var change = Session.TakeOutsideChanges();
        if (!changed && change is null)
        {
            return null;
        }

        View = Session.Snapshot();
        if (change is not null)
        {
            _outside.Add(change);
            _changes++;
            foreach (var item in change.Items.Where(i => i.Kind == "layer"))
            {
                _changedBy[item.Id] = (_changes, change.Edits);
            }
        }

        _cameAfter.Add(View, new StrongBox<int>(_changes));
        return change?.Problem is { } problem
            ? $"{ProjectStore.ProjectFileName} can't be read ({problem}); edits aren't saved until it's fixed, or a copy from the {ProjectStore.BackupsFolder} folder is put in its place"
            : null;
    }

    private string SavedBy(string layer)
    {
        var edits = _changedBy.TryGetValue(layer, out var by) ? by.Edits : [];
        return edits.Count == 0 ? "" : $" ({string.Join(", ", edits.TakeLast(3))})";
    }

    private string Saved() => $"Saved · revision {View.Revision}";

    // The problems a project has as it is opened: one on the line, more on a line each as the
    // validator lists them. Null when it has none.
    private static string? Problems(List<string> problems) => problems.Count switch
    {
        0 => null,
        1 => $"{ProjectStore.ProjectFileName} has a problem, and edits aren't saved until it's fixed: {problems[0]}",
        _ => $"{ProjectStore.ProjectFileName} has {problems.Count} problems, and edits aren't saved until they're fixed:\n- "
            + string.Join("\n- ", problems.Take(ProblemsNamed))
            + (problems.Count > ProblemsNamed ? $"\n- and {problems.Count - ProblemsNamed} more" : ""),
    };
}
