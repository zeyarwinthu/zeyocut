// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// The open project, kept in memory and saved to project.json on every change. Another program (the
// editor, a second server) may save the same project. Every save holds project.lock while it
// checks that project.json is still what this session last read or wrote (by its SHA-256); when it
// isn't, the file's version becomes the open project first and the change is made on top of it.
// Sync takes such a version between changes, and TakeOutsideChanges says what it changed.
//
// Undo and redo put back what a step changed. When no other program saved since, that is the
// whole project as it was; after another program's change it is only the items the step changed,
// so the other change stays, and it's refused when another program changed one of those items.
// The next undo steps over a step refused that way, and takes back the one before it; redo
// does the same with the steps that were undone.
public sealed class ProjectSession
{
    public const int MaxUndo = 200;

    private readonly object _gate = new();
    private readonly LinkedList<Step> _undo = new();
    private readonly LinkedList<Step> _redo = new();
    private readonly List<OutsideChange> _outside = [];

    // The changes other programs saved, for History, newest last, and how many there have been.
    private readonly List<OutsideChange> _outsideLog = [];
    private int _outsideCount;

    // What this session's backups are labelled with first ("editor "), or "".
    private readonly string _program;
    private Project _project;

    private const int ItemsNamed = 5;

    // project.json as this session last read or wrote it, and the newest backup then, so the
    // backups other programs keep after that tell what they saved.
    private string _hash = "";
    private ProjectFileStamp _stamp;
    private ProjectBackup? _newestBackup;

    // The highest revision this session has read or saved.
    private long _highest;

    // The hash of an unreadable project.json (or "missing") already reported, so it's reported once.
    private string? _unreadable;

    private ProjectSession(string directory, Project project, string? program)
    {
        Directory = directory;
        _project = project;
        _program = program is null ? "" : program + " ";
        _newestBackup = ProjectBackups.List(directory).FirstOrDefault();
    }

    public string Directory { get; }

    // How long a save waits while another program saves the same project.
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // Set when Open found a project.json from an older ZeyoCut and updated it; OriginalCopy is
    // the file as it was.
    public int? MigratedFrom { get; private set; }

    public string? OriginalCopy { get; private set; }

    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _project.Revision;
            }
        }
    }

    public int UndoCount
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count;
            }
        }
    }

    public int RedoCount
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count;
            }
        }
    }

    // Oldest first: the steps undo can take back, with the changes other programs saved among
    // them (Outside set), which it can't.
    public IReadOnlyList<HistoryEntry> History
    {
        get
        {
            lock (_gate)
            {
                return _undo.Select(s => new HistoryEntry(s.Label, s.Revision, null))
                    .Concat(_outsideLog.Select(o => new HistoryEntry(o.Edits.Count > 0 ? string.Join(", ", o.Edits) : $"{ProjectStore.ProjectFileName} changed", o.Revision, o)))
                    .OrderBy(e => e.Revision)
                    .ToList();
            }
        }
    }

    // 'program' names who saves, as the first word of each backup's label ("editor"); the server
    // leaves it out.
    public static ProjectSession Open(string directory, string? program = null)
    {
        var full = Path.GetFullPath(directory);
        var bytes = ProjectStore.ReadBytes(full, out var stamp);
        var project = ProjectStore.Parse(bytes, out var savedVersion);
        ProjectStore.EnsureFolders(full);
        var session = new ProjectSession(full, project, program);
        session.Took(project, bytes, stamp);
        if (savedVersion < Project.CurrentFormatVersion)
        {
            session.UpdateFormat();
        }

        return session;
    }

    public static ProjectSession Create(string directory, Project project)
    {
        var full = Path.GetFullPath(directory);
        if (ProjectStore.IsProjectDirectory(full))
        {
            throw Exists(full);
        }

        ProjectJson.Normalize(project);
        ProjectValidator.ThrowIfInvalid(project);
        ProjectStore.EnsureFolders(full);
        var session = new ProjectSession(full, project, null);
        lock (session._gate)
        {
            using var held = ProjectLock.Acquire(full, session.LockTimeout);
            if (ProjectStore.IsProjectDirectory(full))
            {
                throw Exists(full);
            }

            session.Write(held, project);
        }

        return session;
    }

    public Project Snapshot()
    {
        lock (_gate)
        {
            return ProjectJson.Clone(_project);
        }
    }

    public T Read<T>(Func<Project, T> read)
    {
        lock (_gate)
        {
            return read(_project);
        }
    }

    // The change runs on a copy of project.json as it is on disk, while project.lock is held.
    public T Mutate<T>(string label, Func<Project, T> change)
    {
        lock (_gate)
        {
            using var held = ProjectLock.Acquire(Directory, LockTimeout);
            CatchUp(overwriteUnreadable: false);
            var before = ProjectJson.Serialize(_project);
            var working = ProjectJson.Deserialize(before);
            var result = change(working);
            Commit(held, label, before, working);
            return result;
        }
    }

    // Puts a whole project in place of the open one, as one undo step. It may replace a
    // project.json that can't be read (a copy of it is kept in backups\ first).
    public void Replace(string label, Project replacement)
    {
        lock (_gate)
        {
            using var held = ProjectLock.Acquire(Directory, LockTimeout);
            CatchUp(overwriteUnreadable: true);
            Commit(held, label, ProjectJson.Serialize(_project), ProjectJson.Clone(replacement));
        }
    }

    // Undo and redo save what they put back as a new revision.
    public string Undo() => Undo(out _);

    // A step whose undo was refused before, and still would be, is stepped over: it stays as it
    // is, and the step before it is undone. steppedOver names those steps, the newest first.
    public string Undo(out IReadOnlyList<string> steppedOver)
    {
        lock (_gate)
        {
            if (_undo.Count == 0)
            {
                throw new ZeyoCutException("nothing to undo");
            }

            using var held = ProjectLock.Acquire(Directory, LockTimeout);
            CatchUp(overwriteUnreadable: false);
            var over = new List<Step>();
            for (var node = _undo.Last; node is not null; node = node.Previous)
            {
                var step = node.Value;
                Project put;
                try
                {
                    put = PutBack(step, backwards: true, over);
                }
                catch (ZeyoCutException) when (step.Refused)
                {
                    over.Add(step);
                    continue;
                }
                catch (ZeyoCutException ex)
                {
                    node.Value = step with { Refused = true };
                    if (node.Previous is null)
                    {
                        throw;
                    }

                    throw new ZeyoCutException($"{ex.Message}. The next undo steps over this step and goes on to the one before it", ex);
                }

                var current = ProjectJson.Serialize(_project);
                Save(held, put, $"undo {step.Label}");
                _undo.Remove(node);
                _redo.AddLast(step with { Json = current, Outside = _outsideCount, Revision = _project.Revision, Refused = false });
                steppedOver = over.Select(s => s.Label).ToList();
                return step.Label;
            }

            throw new ZeyoCutException(over.Count == 0
                ? "nothing to undo"
                : $"nothing to undo: {And(over.Select(s => $"'{s.Label}'").ToList())} can't be undone on top of another program's changes");
        }
    }

    public string Redo() => Redo(out _);

    // As with undo: a step whose redo was refused before, and still would be, is stepped over. It
    // stays undone, and the step undone before it is redone.
    public string Redo(out IReadOnlyList<string> steppedOver)
    {
        lock (_gate)
        {
            if (_redo.Count == 0)
            {
                throw new ZeyoCutException("nothing to redo");
            }

            using var held = ProjectLock.Acquire(Directory, LockTimeout);
            CatchUp(overwriteUnreadable: false);
            var over = new List<Step>();
            for (var node = _redo.Last; node is not null; node = node.Previous)
            {
                var step = node.Value;
                Project put;
                try
                {
                    put = PutBack(step, backwards: false, over);
                }
                catch (ZeyoCutException) when (step.Refused)
                {
                    over.Add(step);
                    continue;
                }
                catch (ZeyoCutException ex)
                {
                    node.Value = step with { Refused = true };
                    if (node.Previous is null)
                    {
                        throw;
                    }

                    throw new ZeyoCutException($"{ex.Message}. The next redo steps over this step and goes on to the one after it", ex);
                }

                var current = ProjectJson.Serialize(_project);
                Save(held, put, $"redo {step.Label}");
                _redo.Remove(node);
                AddStep(step with { Json = current, Outside = _outsideCount, Revision = _project.Revision, Refused = false });
                steppedOver = over.Select(s => s.Label).ToList();
                return step.Label;
            }

            throw new ZeyoCutException(over.Count == 0
                ? "nothing to redo"
                : $"nothing to redo: {And(over.Select(s => $"'{s.Label}'").ToList())} can't be redone on top of another program's changes");
        }
    }

    // Takes project.json as the open project if another program saved it since this session last
    // read or wrote it. It compares the file's size and time first, so it costs next to nothing
    // when nothing changed, and it doesn't take project.lock, so it never holds up another
    // program's save. True when it took another version of project.json (TakeOutsideChanges then
    // says what changed in the project, unless only how the file was written did).
    public bool Sync()
    {
        lock (_gate)
        {
            if (ProjectStore.Stamp(Directory) == _stamp)
            {
                return false;
            }

            byte[] bytes;
            ProjectFileStamp stamp;
            try
            {
                bytes = ProjectStore.ReadBytes(Directory, out stamp);
            }
            catch (ZeyoCutException ex)
            {
                Unreadable("missing", ex.Message);
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Can't be read just now; the next save reads it under project.lock.
                return false;
            }

            if (ProjectStore.Hash(bytes) == _hash)
            {
                _stamp = stamp;
                return false;
            }

            return Take(bytes, stamp, out _);
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

    private static ZeyoCutException Exists(string directory) =>
        new($"a project already exists in '{directory}'; use open_project instead");

    // Saves a project.json from an older ZeyoCut in the current format, keeping the file as it was.
    private void UpdateFormat()
    {
        lock (_gate)
        {
            using var held = ProjectLock.Acquire(Directory, LockTimeout);
            var bytes = ProjectStore.ReadBytes(Directory, out var stamp);
            var project = ProjectStore.Parse(bytes, out var savedVersion);
            Took(project, bytes, stamp);

            // Another program may have updated it meanwhile.
            if (savedVersion >= Project.CurrentFormatVersion)
            {
                return;
            }

            OriginalCopy = ProjectBackups.KeepOriginal(Directory, savedVersion);
            MigratedFrom = savedVersion;
            Write(held, project);
        }
    }

    // Under project.lock: reads project.json and takes it as the open project if another program
    // changed it. One that can't be read stops the save, unless it's to be overwritten.
    private void CatchUp(bool overwriteUnreadable)
    {
        byte[] bytes;
        ProjectFileStamp stamp;
        try
        {
            bytes = ProjectStore.ReadBytes(Directory, out stamp);
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
        {
            if (overwriteUnreadable)
            {
                return;
            }

            throw NotSaved(ex.Message);
        }

        if (ProjectStore.Hash(bytes) == _hash)
        {
            _stamp = stamp;
            return;
        }

        if (!Take(bytes, stamp, out var problem) && !overwriteUnreadable)
        {
            throw NotSaved(problem!);
        }
    }

    // What undo (backwards) or redo of a step saves. With no change from another program since,
    // it's the whole project the step kept; otherwise the open project with the step's items put
    // back, refused when another program changed one of them, or a step that this undo or redo
    // stepped over ('over') did.
    private Project PutBack(Step step, bool backwards, IReadOnlyList<Step> over)
    {
        if (step.Outside == _outsideCount)
        {
            return ProjectJson.Deserialize(step.Json);
        }

        var verb = backwards ? "undo" : "redo";
        var conflicts = ProjectDiff.Conflicts(_project, step.Changes, backwards);

        // The server's messages name the tool that goes further back; the editor has none.
        var backups = _program.Length > 0 ? "" : $". restore_backup lists the copies of {ProjectStore.ProjectFileName} kept before each save";
        if (conflicts.Count > 0)
        {
            var theirs = conflicts.Where(c => LeftBy(over, c, backwards) is null).ToList();
            if (theirs.Count == 0)
            {
                var left = LeftBy(over, conflicts[0], backwards)!.Label;
                throw new ZeyoCutException(backwards
                    ? $"can't undo '{step.Label}': '{left}', a later step that was stepped over, changed {conflicts[0].Describe()} too, and undoing '{step.Label}' would throw that change away{backups}"
                    : $"can't redo '{step.Label}': '{left}', an earlier step that was stepped over, changed {conflicts[0].Describe()} too, and '{step.Label}' was made on top of that change{backups}");
            }

            conflicts = theirs;
            var named = conflicts.Take(ItemsNamed).Select(c => ProjectDiff.DescribeConflict(_project, c, backwards)).ToList();
            if (conflicts.Count > ItemsNamed)
            {
                named.Add($"changed {conflicts.Count - ItemsNamed} more");
            }

            var edits = _outsideLog.Where(o => o.Revision > step.Revision && conflicts.Any(o.Touches))
                .SelectMany(o => o.Edits).Distinct().ToList();
            var saved = edits.Count == 0 ? "" : $" (saved: {string.Join(", ", edits.TakeLast(ItemsNamed))})";
            throw new ZeyoCutException($"can't {verb} '{step.Label}': another program {And(named)} since {(backwards ? "that edit" : "it was undone")}{saved}, "
                + $"and {(backwards ? "undoing" : "redoing")} it would throw that change away{backups}");
        }

        var working = ProjectJson.Clone(_project);
        ProjectDiff.Apply(working, step.Changes, backwards);
        ProjectJson.Normalize(working);
        try
        {
            ProjectValidator.ThrowIfInvalid(working);
        }
        catch (ZeyoCutException ex)
        {
            throw new ZeyoCutException($"can't {verb} '{step.Label}' on top of what another program changed since: {ex.Message}", ex);
        }

        return working;
    }

    // The stepped-over step that left an item as it is now, if one did: then that step changed
    // it (undo) or would have (redo, where the item is as that step found it), not another program.
    private Step? LeftBy(IReadOnlyList<Step> over, ItemDelta item, bool backwards) =>
        over.FirstOrDefault(s => s.Changes.Any(d => d.Kind == item.Kind && d.Id == item.Id && ProjectDiff.Conflicts(_project, [d], backwards).Count == 0));

    private static string And(List<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items[..^1]) + " and " + items[^1];

    private ZeyoCutException NotSaved(string problem) =>
        new($"{ProjectStore.ProjectFileName} was changed by another program and can't be read ({problem}), so nothing was saved; "
            + $"fix the file, or {(_program.Length > 0 ? $"put a copy from the {ProjectStore.BackupsFolder} folder in its place" : "put a backup back with restore_backup")} "
            + $"(the open project is revision {_project.Revision})");

    // Takes another program's project.json as the open project and notes what changed. False, with
    // the reason, when it can't be read: the open project then stays as it was.
    private bool Take(byte[] bytes, ProjectFileStamp stamp, out string? problem)
    {
        Project outside;
        try
        {
            outside = ProjectStore.Parse(bytes, out _);
        }
        catch (ZeyoCutException ex)
        {
            problem = ex.Message;
            Unreadable(ProjectStore.Hash(bytes), problem);
            return false;
        }

        problem = null;
        var items = ProjectDiff.Compare(_project, outside);
        var edits = EditsSince();
        Took(outside, bytes, stamp);
        if (items.Count > 0)
        {
            var change = new OutsideChange(outside.Revision, edits, items, null);
            _outside.Add(change);
            _outsideLog.Add(change);
            if (_outsideLog.Count > MaxUndo)
            {
                _outsideLog.RemoveAt(0);
            }

            _outsideCount++;
        }

        return true;
    }

    private void Took(Project project, byte[] bytes, ProjectFileStamp stamp)
    {
        _project = project;
        _hash = ProjectStore.Hash(bytes);
        _stamp = stamp;
        _highest = Math.Max(_highest, project.Revision);
        _unreadable = null;
    }

    private void Unreadable(string key, string problem)
    {
        if (_unreadable != key)
        {
            _unreadable = key;
            _outside.Add(new OutsideChange(_project.Revision, [], [], problem));
        }
    }

    // The edits other programs saved since this session last looked, oldest first, from the
    // backups they kept (project-<time> before <edit>.json). A hand edit keeps none.
    // (The one this session knew may have been pruned since: those older than it don't count.)
    private List<string> EditsSince()
    {
        var backups = ProjectBackups.List(Directory);
        var known = _newestBackup;
        var newer = backups.TakeWhile(b => known is null || (b.Name != known.Name && b.Time >= known.Time)).ToList();
        _newestBackup = backups.FirstOrDefault() ?? _newestBackup;
        return newer.Select(b => b.Before).OfType<string>().Reverse().ToList();
    }

    private void Commit(ProjectLock held, string label, string before, Project working)
    {
        ProjectValidator.ThrowIfInvalid(working);
        var changes = ProjectDiff.Changes(_project, working);
        Save(held, working, label);
        AddStep(new Step(label, before, changes, _outsideCount, working.Revision));
        _redo.Clear();
    }

    private void AddStep(Step step)
    {
        _undo.AddLast(step);
        if (_undo.Count > MaxUndo)
        {
            _undo.RemoveFirst();
        }
    }

    // The project.json being replaced goes to backups\ first, labelled with the program and edit.
    private void Save(ProjectLock held, Project project, string label)
    {
        var backup = ProjectBackups.Add(Directory, _program + label);
        Write(held, project);
        if (backup is not null)
        {
            _newestBackup = ProjectBackups.Describe(backup) ?? _newestBackup;
        }
    }

    // Saves the project as the next revision: one more than any this session or project.lock
    // knows, so a revision never goes back, even when undo, redo or restore_backup put back older
    // content or another program put back an older file.
    private void Write(ProjectLock held, Project project)
    {
        project.Revision = Math.Max(_highest, held.Highest) + 1;
        var bytes = ProjectStore.Save(project, Directory);
        held.Saved(project.Revision);
        _project = project;
        _highest = project.Revision;
        _hash = ProjectStore.Hash(bytes);
        _stamp = ProjectStore.Stamp(Directory);
        _unreadable = null;
    }

    // One step undo can take back (or redo put back): its label, the whole project.json it puts
    // back when no other program saved since (the version before the step; for redo, the one
    // before the undo), the items it changed, how many outside changes had come by then, and the
    // revision it saved. Refused: an undo of it (or, once undone, a redo) was refused, so the next
    // one steps over it.
    private sealed record Step(string Label, string Json, IReadOnlyList<ItemDelta> Changes, int Outside, long Revision, bool Refused = false);
}

// One entry of a session's history: a step this session saved, which undo can take back, or a
// change another program saved (Outside; Label lists the edits it saved), which it can't.
public sealed record HistoryEntry(string Label, long Revision, OutsideChange? Outside);
