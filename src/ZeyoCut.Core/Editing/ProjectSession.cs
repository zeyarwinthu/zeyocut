// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public sealed class ProjectSession
{
    public const int MaxUndo = 200;

    private readonly object _gate = new();
    private readonly LinkedList<(string Label, string Json)> _undo = new();
    private readonly Stack<(string Label, string Json)> _redo = new();
    private Project _project;

    private ProjectSession(string directory, Project project)
    {
        Directory = directory;
        _project = project;
    }

    public string Directory { get; }

    // Set when Open found a project.json from an older ZeyoCut and updated it; OriginalCopy is
    // the file as it was.
    public int? MigratedFrom { get; private set; }

    public string? OriginalCopy { get; private set; }

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
        var project = ProjectStore.Load(full, out var savedVersion);
        ProjectStore.EnsureFolders(full);
        var session = new ProjectSession(full, project);
        if (savedVersion < Project.CurrentFormatVersion)
        {
            session.OriginalCopy = ProjectBackups.KeepOriginal(full, savedVersion);
            session.MigratedFrom = savedVersion;
            ProjectStore.Save(project, full);
        }

        return session;
    }

    public static ProjectSession Create(string directory, Project project)
    {
        var full = Path.GetFullPath(directory);
        if (ProjectStore.IsProjectDirectory(full))
        {
            throw new ZeyoCutException($"a project already exists in '{full}'; use open_project instead");
        }

        ProjectJson.Normalize(project);
        ProjectValidator.ThrowIfInvalid(project);
        ProjectStore.EnsureFolders(full);
        ProjectStore.Save(project, full);
        return new ProjectSession(full, project);
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

    public T Mutate<T>(string label, Func<Project, T> change)
    {
        lock (_gate)
        {
            var before = ProjectJson.Serialize(_project);
            var working = ProjectJson.Deserialize(before);
            var result = change(working);
            Commit(label, before, working);
            return result;
        }
    }

    // Puts a whole project in place of the open one, as one undo step.
    public void Replace(string label, Project replacement)
    {
        lock (_gate)
        {
            Commit(label, ProjectJson.Serialize(_project), ProjectJson.Clone(replacement));
        }
    }

    public string Undo()
    {
        lock (_gate)
        {
            if (_undo.Last is not { } last)
            {
                throw new ZeyoCutException("nothing to undo");
            }

            var current = ProjectJson.Serialize(_project);
            Restore(last.Value.Json, $"undo {last.Value.Label}");
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

            var (label, json) = _redo.Peek();
            var current = ProjectJson.Serialize(_project);
            Restore(json, $"redo {label}");
            _redo.Pop();
            _undo.AddLast((label, current));
            return label;
        }
    }

    private void Commit(string label, string before, Project working)
    {
        ProjectValidator.ThrowIfInvalid(working);
        Save(working, label);
        _project = working;
        _undo.AddLast((label, before));
        if (_undo.Count > MaxUndo)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
    }

    private void Restore(string json, string label)
    {
        var project = ProjectJson.Deserialize(json);
        Save(project, label);
        _project = project;
    }

    // The project.json being replaced goes to backups\ first.
    private void Save(Project project, string label)
    {
        ProjectBackups.Add(Directory, label);
        ProjectStore.Save(project, Directory);
    }
}
