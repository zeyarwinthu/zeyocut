// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// Two programs on one project: each session checks project.json under project.lock before it saves.
public class SyncTests
{
    [Fact]
    public void ASecondSessionTakesTheFirstOnesEditsAndBuildsOnThem()
    {
        var directory = TestPaths.NewDirectory("sync-two-sessions");
        var server = ProjectSession.Create(directory, TestProjects.Rig());
        var editor = ProjectSession.Open(directory);
        Assert.Equal(1, server.Revision);
        Assert.Equal(1, editor.Revision);
        Assert.False(editor.Sync());

        server.Mutate("update_layer tora_body", p => ProjectEditor.UpdateLayer(p, "tora_body", new JsonObject { ["y"] = 400 }));
        Assert.Equal(2, server.Revision);
        Assert.Null(server.TakeOutsideChanges());

        Assert.True(editor.Sync());
        Assert.Equal(2, editor.Revision);
        Assert.Equal(400, editor.Read(p => ((VisualLayer)p.FindLayer("tora_body")!).Y.Value));
        var change = Assert.IsType<OutsideChange>(editor.TakeOutsideChanges());
        Assert.Equal(2, change.Revision);
        Assert.Equal(["update_layer tora_body"], change.Edits);
        Assert.Equal("changed layer 'tora_body'", Assert.Single(change.Items).Describe());
        Assert.Null(change.Problem);
        Assert.Null(editor.TakeOutsideChanges());
        Assert.False(editor.Sync());

        // The editor's edit goes on top. The server's next edit keeps it without a Sync first: its
        // save reads project.json under the lock and makes the change on top of what's there.
        editor.Mutate("editor remove_layer tora_tail", p => ProjectEditor.RemoveLayer(p, "tora_tail", withChildren: false));
        server.Mutate("add_markers (1 marker)", p => MarkerEditor.AddMarkers(p, [new MarkerEntry("intro", 1, "Intro", null)]));
        var saved = ProjectStore.Load(directory);
        Assert.Equal(4, saved.Revision);
        Assert.Null(saved.FindLayer("tora_tail"));
        Assert.Equal(400, ((VisualLayer)saved.FindLayer("tora_body")!).Y.Value);
        Assert.Equal("intro", Assert.Single(saved.Markers).Id);
        var seen = Assert.IsType<OutsideChange>(server.TakeOutsideChanges());
        Assert.Equal(3, seen.Revision);
        Assert.Equal(["editor remove_layer tora_tail"], seen.Edits);
        Assert.Equal(["removed layer 'tora_tail'"], seen.Items.Select(i => i.Describe()));
        Assert.Equal("4", File.ReadAllText(Path.Combine(directory, ProjectLock.FileName)).Trim());
    }

    [Fact]
    public void UndoTakesBackOnlyWhatItsStepChangedOnceAnotherProgramSaved()
    {
        var directory = TestPaths.NewDirectory("sync-undo");
        var server = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        server.Mutate("update_project", p => p.Duration = 5);
        var editor = ProjectSession.Open(directory);
        editor.Mutate("editor update_project", p => p.Duration = 6);

        server.Mutate("update_project", p => p.Name = "renamed");
        Assert.Equal(6, server.Read(p => p.Duration));
        Assert.Equal(2, server.UndoCount);
        Assert.Equal(0, server.RedoCount);

        Assert.Equal("update_project", server.Undo());
        Assert.Equal("test", server.Read(p => p.Name));
        Assert.Equal(6, server.Read(p => p.Duration));

        // The first step set the duration, which the editor changed since.
        var error = Assert.Throws<ZeyoCutException>(() => server.Undo());
        Assert.Equal("can't undo 'update_project': another program changed the project's duration since that edit (saved: editor update_project), "
            + "and undoing it would throw that change away. restore_backup lists the copies of project.json kept before each save", error.Message);
        Assert.Equal(6, ProjectStore.Load(directory).Duration);
        Assert.Equal(1, server.UndoCount);

        Assert.Equal("update_project", server.Redo());
        Assert.Equal("renamed", ProjectStore.Load(directory).Name);
        Assert.Equal(6, server.Revision);
    }

    [Fact]
    public void TheRevisionNeverGoesBack()
    {
        var directory = TestPaths.NewDirectory("sync-revision");
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        var session = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        session.Mutate("update_project", p => p.Duration = 5);
        Assert.Equal(2, session.Revision);

        // Undo and redo put back earlier content as a new revision.
        session.Undo();
        Assert.Equal(3, session.Revision);
        Assert.Equal(3, ProjectStore.Load(directory).Revision);
        Assert.Equal(3, ProjectStore.Load(directory).Duration);
        session.Redo();
        Assert.Equal(4, ProjectStore.Load(directory).Revision);
        Assert.Equal(5, ProjectStore.Load(directory).Duration);

        // So does restore_backup: the backup of revision 1 comes back as revision 5.
        var first = ProjectBackups.List(directory).Single(b => b.Before == "update_project");
        Assert.Equal(1, ProjectBackups.Load(first).Revision);
        session.Replace("restore_backup first", ProjectBackups.Load(first));
        Assert.Equal(5, session.Revision);
        Assert.Equal(3, ProjectStore.Load(directory).Duration);

        // Another program puts back an older project.json by hand (revision 2). It's taken as it
        // is, and the next save counts on from the highest revision saved, not from 2.
        var second = ProjectBackups.List(directory).Single(b => b.Before == "undo update_project");
        File.Copy(second.Path, file, overwrite: true);
        Assert.True(session.Sync());
        Assert.Equal(2, session.Revision);
        Assert.Equal(5, session.Read(p => p.Duration));
        Assert.Equal(["changed the project settings (duration)"], session.TakeOutsideChanges()!.Items.Select(i => i.Describe()));
        session.Mutate("update_project", p => p.Name = "after");
        Assert.Equal(6, ProjectStore.Load(directory).Revision);

        // A session that never saw revisions 3 to 6 learns the highest from project.lock.
        File.Copy(second.Path, file, overwrite: true);
        var fresh = ProjectSession.Open(directory);
        Assert.Equal(2, fresh.Revision);
        fresh.Mutate("editor update_project", p => p.Name = "fresh");
        Assert.Equal(7, ProjectStore.Load(directory).Revision);
    }

    [Fact]
    public void ALockHeldByAProgramThatDiesIsFreed()
    {
        var directory = TestPaths.NewDirectory("sync-killed-lock");
        var session = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        session.LockTimeout = TimeSpan.FromMilliseconds(300);
        var lockFile = Path.Combine(directory, ProjectLock.FileName);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", $"$held = [IO.File]::Open('{lockFile}', 'OpenOrCreate', 'ReadWrite', 'None'); 'locked'; Start-Sleep -Seconds 60" })
        {
            start.ArgumentList.Add(argument);
        }

        using (var holder = Process.Start(start)!)
        {
            try
            {
                Assert.Equal("locked", holder.StandardOutput.ReadLine());
                var error = Assert.Throws<ZeyoCutException>(() => session.Mutate("update_project", p => p.Duration = 5));
                Assert.Equal("another program is saving this project (it has project.lock open) and didn't finish within 0.3 s, so nothing was saved; try again", error.Message);
                Assert.Equal(3, ProjectStore.Load(directory).Duration);
                Assert.Equal(0, session.UndoCount);
            }
            finally
            {
                holder.Kill();
                holder.WaitForExit();
            }
        }

        // The file is still there, but no one has it open.
        Assert.True(File.Exists(lockFile));
        session.Mutate("update_project", p => p.Duration = 5);
        Assert.Equal(5, ProjectStore.Load(directory).Duration);
        Assert.Equal(2, session.Revision);
    }

    [Fact]
    public void AReaderDoesNotHoldUpASave()
    {
        var directory = TestPaths.NewDirectory("sync-reader");
        var session = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);

        // Opened the way ProjectStore.ReadBytes opens it, and kept open through the save.
        using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        session.Mutate("update_project", p => p.Duration = 5);
        Assert.Equal(5, ProjectStore.Load(directory).Duration);
        Assert.Contains("\"duration\": 3,", new StreamReader(reader).ReadToEnd());
    }

    [Fact]
    public void TwoSessionsSavingAtOnceLoseNothing()
    {
        const int each = 50;
        var directory = TestPaths.NewDirectory("sync-hammer");
        var first = ProjectSession.Create(directory, TestProjects.Empty(duration: 10));
        var second = ProjectSession.Open(directory);
        var revisions = new ConcurrentBag<long>();
        void Add(ProjectSession session, string prefix)
        {
            for (var i = 0; i < each; i++)
            {
                var entry = new MarkerEntry($"{prefix}_{i}", i * 0.1, $"{prefix} {i}", "scene");
                session.Mutate($"add_markers {prefix}_{i}", p => MarkerEditor.AddMarkers(p, [entry]));
                revisions.Add(session.Revision);
            }
        }

        Parallel.Invoke(() => Add(first, "a"), () => Add(second, "b"));

        var saved = ProjectStore.Load(directory);
        Assert.Equal(2 * each, saved.Markers.Count);
        Assert.Equal(1 + 2 * each, saved.Revision);
        Assert.Equal(Enumerable.Range(2, 2 * each).Select(r => (long)r), revisions.Order());
        first.Sync();
        second.Sync();
        Assert.Equal(ProjectJson.Serialize(saved), ProjectJson.Serialize(first.Snapshot()));
        Assert.Equal(ProjectJson.Serialize(saved), ProjectJson.Serialize(second.Snapshot()));
    }

    [Fact]
    public void AProjectJsonThatCantBeReadStopsEditsUntilABackupIsPutBack()
    {
        var directory = TestPaths.NewDirectory("sync-unreadable");
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        var session = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        session.Mutate("update_project", p => p.Duration = 5);

        File.WriteAllText(file, "{ not json");
        Assert.False(session.Sync());
        var change = Assert.IsType<OutsideChange>(session.TakeOutsideChanges());
        Assert.StartsWith("invalid project JSON", change.Problem);
        Assert.Equal(2, change.Revision);
        Assert.Empty(change.Items);
        Assert.False(session.Sync());
        Assert.Null(session.TakeOutsideChanges());

        var error = Assert.Throws<ZeyoCutException>(() => session.Mutate("update_project", p => p.Duration = 6));
        Assert.StartsWith("project.json was changed by another program and can't be read (invalid project JSON", error.Message);
        Assert.EndsWith("so nothing was saved; fix the file, or put a backup back with restore_backup (the open project is revision 2)", error.Message);
        Assert.Throws<ZeyoCutException>(() => session.Undo());
        Assert.Equal("{ not json", File.ReadAllText(file));
        Assert.Equal(5, session.Read(p => p.Duration));

        // restore_backup replaces it; the file it replaces is kept as a backup too.
        var backup = Assert.Single(ProjectBackups.List(directory));
        session.Replace("restore_backup first", ProjectBackups.Load(backup));
        Assert.Equal(3, ProjectStore.Load(directory).Duration);
        Assert.Equal(3, session.Revision);
        Assert.Equal("{ not json", File.ReadAllText(ProjectBackups.List(directory)[0].Path));

        File.Delete(file);
        Assert.False(session.Sync());
        Assert.StartsWith("no project.json in ", session.TakeOutsideChanges()!.Problem);
        Assert.StartsWith("project.json was changed by another program and can't be read (no project.json in ",
            Assert.Throws<ZeyoCutException>(() => session.Mutate("update_project", p => p.Duration = 6)).Message);
    }

    [Fact]
    public void TheSameContentWrittenAgainIsNoChange()
    {
        var directory = TestPaths.NewDirectory("sync-same");
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        var session = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        session.Mutate("update_project", p => p.Duration = 5);

        // Saved again by an editor that writes it on one line: a new file, the same project.
        File.WriteAllText(file, JsonNode.Parse(File.ReadAllText(file))!.ToJsonString());
        Assert.True(session.Sync());
        Assert.Null(session.TakeOutsideChanges());
        Assert.Equal("update_project", session.Undo());
        Assert.Equal(3, ProjectStore.Load(directory).Duration);
    }

    [Fact]
    public void WhatChangedListsEachItemOnce()
    {
        var before = ProjectJson.Clone(TestProjects.Rig());
        var after = ProjectJson.Clone(before);
        after.Name = "renamed";
        after.Duration = 20;
        ProjectEditor.UpdateLayer(after, "tora_body", new JsonObject { ["y"] = 400 });
        ProjectEditor.RemoveLayer(after, "tora_tail", withChildren: false);
        var head = after.FindLayer("tora_head")!;
        after.Layers.Remove(head);
        after.Layers.Insert(0, head);
        MarkerEditor.AddMarkers(after, [new MarkerEntry("intro", 1, "Intro", null)]);
        Assert.Equal(
            [
                "changed the project settings (name, duration)",
                "changed layer 'tora_body'",
                "removed layer 'tora_tail'",
                "changed the order of the layers",
                "added marker 'intro' (Intro)",
            ],
            ProjectDiff.Compare(before, after).Select(i => i.Describe()));
        Assert.Empty(ProjectDiff.Compare(before, ProjectJson.Clone(before)));

        var merged = OutsideChange.Merge(
        [
            new OutsideChange(5, ["editor a"], [new("layer", "x", null, ItemChange.Added), new("layer", "y", "Y", ItemChange.Changed), new("settings", "", "duration", ItemChange.Changed)], null),
            new OutsideChange(6, ["editor b"], [new("layer", "x", null, ItemChange.Removed), new("layer", "y", "Y", ItemChange.Removed), new("settings", "", "fps, duration", ItemChange.Changed), new("marker", "m", "M", ItemChange.Added)], null),
        ]);
        Assert.Equal(6, merged.Revision);
        Assert.Equal(["editor a", "editor b"], merged.Edits);
        Assert.Equal(["removed layer 'y' (Y)", "changed the project settings (duration, fps)", "added marker 'm' (M)"], merged.Items.Select(i => i.Describe()));
    }
}
