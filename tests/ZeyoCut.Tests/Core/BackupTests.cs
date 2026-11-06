// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class BackupTests
{
    [Fact]
    public void EverySaveKeepsTheFileItReplaces()
    {
        var directory = TestPaths.NewDirectory("backup-saves");
        var file = Path.Combine(directory, "project.json");
        var session = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        Assert.Empty(ProjectBackups.List(directory));
        var created = File.ReadAllText(file);

        session.Mutate("update_project", p => p.Duration = 5);
        var first = Assert.Single(ProjectBackups.List(directory));
        Assert.Matches(@"^project-\d{8}-\d{6}-\d{3} before update_project$", first.Name);
        Assert.Equal("update_project", first.Before);
        Assert.Null(first.Format);
        Assert.Equal(created, File.ReadAllText(first.Path));
        Assert.Equal(3, ProjectBackups.Load(first).Duration);
        Assert.InRange((DateTime.Now - first.Time).TotalMinutes, -1, 1);

        var five = File.ReadAllText(file);
        session.Undo();
        session.Redo();
        var backups = ProjectBackups.List(directory);
        Assert.Equal(["redo update_project", "undo update_project", "update_project"], backups.Select(b => b.Before));
        Assert.Equal(created, File.ReadAllText(backups[0].Path));
        Assert.Equal(five, File.ReadAllText(backups[1].Path));
        Assert.Equal(5, ProjectStore.Load(directory).Duration);
        Assert.Empty(Directory.GetFiles(ProjectBackups.Folder(directory), "*.tmp"));

        // By its whole name, with .json, or by enough of its time.
        Assert.Equal(first, ProjectBackups.Find(directory, first.Name));
        Assert.Equal(first, ProjectBackups.Find(directory, first.Name + ".json"));
        Assert.Equal(first, ProjectBackups.Find(directory, first.Name.Split(' ')[0]["project-".Length..]));
        Assert.Contains("no backup 'nope'; the newest are: project-", Assert.Throws<ZeyoCutException>(() => ProjectBackups.Find(directory, "nope")).Message);
        Assert.Contains("fits 3 backups", Assert.Throws<ZeyoCutException>(() => ProjectBackups.Find(directory, "project-")).Message);
        Assert.Equal("this project has no backups yet; one is kept before every save",
            Assert.Throws<ZeyoCutException>(() => ProjectBackups.Find(TestPaths.NewDirectory("backup-none"), "x")).Message);
    }

    [Fact]
    public void OnlyTheNewestTwentyAreKeptButCopiesFromOlderFormatsStay()
    {
        var directory = TestPaths.NewDirectory("backup-rolling");
        File.Copy(TestFixtures.OldProject(4), Path.Combine(directory, "project.json"));
        var session = ProjectSession.Open(directory);
        Assert.Equal(4, session.MigratedFrom);

        for (var i = 1; i <= 25; i++)
        {
            var duration = 10 + i / 10.0;
            session.Mutate($"edit {i}", p => p.Duration = duration);
        }

        var backups = ProjectBackups.List(directory);
        Assert.Equal(ProjectBackups.Kept + 1, backups.Count);
        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(i => $"edit {i}"), backups.Where(b => b.Format is null).Select(b => b.Before));
        var original = Assert.Single(backups, b => b.Format is not null);
        Assert.Equal(4, original.Format);
        Assert.Same(original, backups[^1]);
        Assert.Equal(12.4, ProjectBackups.Load(backups[0]).Duration, 9);

        // The copy from format 4 reads as the current format.
        var old = ProjectBackups.Load(original);
        Assert.Equal(6, old.FormatVersion);
        Assert.Equal("behind", old.Name);
    }

    [Fact]
    public void PuttingABackupBackIsOneUndoStepAndIsBackedUpToo()
    {
        var directory = TestPaths.NewDirectory("backup-replace");
        var session = ProjectSession.Create(directory, TestProjects.Rig());
        session.Mutate("update_layer tora_body", p => ProjectEditor.UpdateLayer(p, "tora_body", new JsonObject { ["y"] = 400 }));
        session.Mutate("remove_layer tora_tail", p => ProjectEditor.RemoveLayer(p, "tora_tail", withChildren: false));
        var now = ProjectJson.Serialize(session.Snapshot());

        var backup = ProjectBackups.List(directory).Single(b => b.Before == "update_layer tora_body");
        session.Replace("restore_backup " + backup.Name.Split(" before ")[0], ProjectBackups.Load(backup));
        Assert.Equal(ProjectJson.Serialize(ProjectBackups.Load(backup)), ProjectJson.Serialize(session.Snapshot()));
        Assert.NotNull(session.Read(p => p.FindLayer("tora_tail")));
        Assert.Equal(3, session.UndoCount);
        Assert.StartsWith("restore_backup project-", ProjectBackups.List(directory)[0].Before);
        Assert.Equal(now, File.ReadAllText(ProjectBackups.List(directory)[0].Path));

        Assert.StartsWith("restore_backup ", session.Undo());
        Assert.Equal(now, ProjectJson.Serialize(session.Snapshot()));

        var invalid = session.Snapshot();
        invalid.Width = 3;
        Assert.Throws<ZeyoCutException>(() => session.Replace("restore_backup bad", invalid));
        Assert.Equal(now, ProjectJson.Serialize(session.Snapshot()));
    }

    [Fact]
    public void NothingIsSavedWhenTheBackupCantBeWritten()
    {
        var directory = TestPaths.NewDirectory("backup-blocked");
        var session = ProjectSession.Create(directory, TestProjects.Empty(duration: 3));
        var file = Path.Combine(directory, "project.json");
        var saved = File.ReadAllText(file);

        // A file where the backups folder goes.
        File.WriteAllText(ProjectBackups.Folder(directory), "not a folder");
        var error = Assert.Throws<ZeyoCutException>(() => session.Mutate("update_project", p => p.Duration = 5));
        Assert.StartsWith($"couldn't keep a backup of project.json in {ProjectBackups.Folder(directory)}, so nothing was saved: ", error.Message);
        Assert.Equal(saved, File.ReadAllText(file));
        Assert.Equal(3, session.Read(p => p.Duration));
        Assert.Equal(0, session.UndoCount);

        File.Delete(ProjectBackups.Folder(directory));
        session.Mutate("update_project", p => p.Duration = 5);
        Assert.Equal(1, session.UndoCount);
        Directory.Move(ProjectBackups.Folder(directory), ProjectBackups.Folder(directory) + "-old");
        File.WriteAllText(ProjectBackups.Folder(directory), "not a folder");
        Assert.Throws<ZeyoCutException>(() => session.Undo());
        Assert.Equal(1, session.UndoCount);
        Assert.Equal(0, session.RedoCount);
        Assert.Equal(5, session.Read(p => p.Duration));
    }

    [Fact]
    public void EditNamesBecomeSafeFileNames()
    {
        var directory = TestPaths.NewDirectory("backup-names");
        var session = ProjectSession.Create(directory, TestProjects.Empty());
        session.Mutate("import_asset a:b/c?.png", p => p.Duration = 4);
        session.Mutate(new string('x', 200) + "...", p => p.Duration = 5);
        session.Mutate("", p => p.Duration = 6);

        Assert.Equal(["edit", new string('x', 80), "import_asset a_b_c_.png"], ProjectBackups.List(directory).Select(b => b.Before));
    }
}
