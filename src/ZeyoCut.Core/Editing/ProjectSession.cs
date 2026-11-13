// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// The open project, kept in memory and saved to project.json on every change. Another program (the
// editor, a second server) may save the same project. Every save holds project.lock while it
// checks that project.json is still what this session last read or wrote (by its SHA-256); when it
// isn't, the file's version becomes the open project first and the change is made on top of it.
// Sync takes such a version between changes, and TakeOutsideChanges says what it changed.
public sealed class ProjectSession
{
    public const int MaxUndo = 200;

    private readonly object _gate = new();
    private readonly LinkedList<(string Label, string Json)> _undo = new();
    private readonly Stack<(string Label, string Json)> _redo = new();
    private readonly List<OutsideChange> _outside = [];
    private Project _project;

    // project.json as this session last read or wrote it, and the newest backup then, so the
    // backups other programs keep after that tell what they saved.
    private string _hash = "";
    private ProjectFileStamp _stamp;
    private string? _newestBackup;

    // The highest revision this session has read or saved.
    private long _highest;

    // Why undo stops where it does, once another program changed project.json.
    private string? _undoStop;

    // The hash of an unreadable project.json (or "missing") already reported, so it's reported once.
    private string? _unreadable;

    private ProjectSession(string directory, Project project)
    {
        Directory = directory;
        _project = project;
        _newestBackup = ProjectBackups.List(directory).FirstOrDefault()?.Name;
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

    public static ProjectSession Open(string directory)
    {
        var full = Path.GetFullPath(directory);
        var bytes = ProjectStore.ReadBytes(full, out var stamp);
        var project = ProjectStore.Parse(bytes, out var savedVersion);
        ProjectStore.EnsureFolders(full);
        var session = new ProjectSession(full, project);
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
        var session = new ProjectSession(full, project);
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

    // Undo and redo put back earlier content, saved as a new revision. They stop at a change
    // another program saved: putting back content from before it would undo that change too.
    public string Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0)
            {
                throw new ZeyoCutException(_undoStop ?? "nothing to undo");
            }

            using var held = ProjectLock.Acquire(Directory, LockTimeout);
            CatchUp(overwriteUnreadable: false);
            if (_undo.Last is not { } last)
            {
                throw new ZeyoCutException(_undoStop ?? "nothing to undo");
            }

            var current = ProjectJson.Serialize(_project);
            Save(held, ProjectJson.Deserialize(last.Value.Json), $"undo {last.Value.Label}");
            _undo.RemoveLast();
            _redo.Push((last.Value.Label, current));
            return last.Value.Label;
        }
    }

    public string Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0)
            {
                throw new ZeyoCutException("nothing to redo");
            }

            using var held = ProjectLock.Acquire(Directory, LockTimeout);
            CatchUp(overwriteUnreadable: false);
            if (_redo.Count == 0)
            {
                throw new ZeyoCutException("nothing to redo");
            }

            var (label, json) = _redo.Peek();
            var current = ProjectJson.Serialize(_project);
            Save(held, ProjectJson.Deserialize(json), $"redo {label}");
            _redo.Pop();
            _undo.AddLast((label, current));
            return label;
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

    private ZeyoCutException NotSaved(string problem) =>
        new($"{ProjectStore.ProjectFileName} was changed by another program and can't be read ({problem}), so nothing was saved; "
            + $"fix the file, or put a backup back with restore_backup (the open project is revision {_project.Revision})");

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
            _undo.Clear();
            _redo.Clear();
            _undoStop = $"nothing more to undo: undo stops at revision {outside.Revision}, which another program saved "
                + $"(restore_backup lists the copies of {ProjectStore.ProjectFileName} kept before each save)";
            _outside.Add(new OutsideChange(outside.Revision, edits, items, null));
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
    private List<string> EditsSince()
    {
        var backups = ProjectBackups.List(Directory);
        var newer = backups.TakeWhile(b => b.Name != _newestBackup).ToList();
        _newestBackup = backups.FirstOrDefault()?.Name ?? _newestBackup;
        return newer.Select(b => b.Before).OfType<string>().Reverse().ToList();
    }

    private void Commit(ProjectLock held, string label, string before, Project working)
    {
        ProjectValidator.ThrowIfInvalid(working);
        Save(held, working, label);
        _undo.AddLast((label, before));
        if (_undo.Count > MaxUndo)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
    }

    // The project.json being replaced goes to backups\ first.
    private void Save(ProjectLock held, Project project, string label)
    {
        var backup = ProjectBackups.Add(Directory, label);
        Write(held, project);
        if (backup is not null)
        {
            _newestBackup = Path.GetFileNameWithoutExtension(backup);
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
}
