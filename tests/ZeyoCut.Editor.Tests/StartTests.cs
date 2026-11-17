// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// What the start page lists, the editor's own settings file, and what it says of an error.
public class StartTests
{
    [Fact]
    public void AnErrorNothingCaughtIsSaidInPlainWordsWithItsDetailsInTheLog()
    {
        var folder = TestFiles.NewFolder("error-log");
        var error = Thrown(new InvalidOperationException("the frame list is empty"));
        var logs = Path.Combine(folder, "logs");
        var log = Path.Combine(logs, App.ErrorLogName);

        Assert.Equal(
            $"Something went wrong in the editor: the frame list is empty\n\nYour changes before it are saved in project.json. The details are in {log}.",
            App.Describe(error, logs));
        var written = File.ReadAllText(log);
        Assert.Contains("System.InvalidOperationException: the frame list is empty", written);
        Assert.Contains(nameof(Thrown), written);

        // The next one is added to the same file.
        App.Describe(Thrown(new IOException("the disk is full")), logs);
        written = File.ReadAllText(log);
        Assert.Contains("the frame list is empty", written);
        Assert.Contains("System.IO.IOException: the disk is full", written);

        // Where the log can't be written (here a file is where its folder would be), or before the
        // settings name the folder, the details are shown.
        var blocked = Path.Combine(folder, "blocked");
        File.WriteAllText(blocked, "");
        foreach (var said in new[] { App.Describe(error, blocked), App.Describe(error, null) })
        {
            Assert.StartsWith("Something went wrong in the editor: the frame list is empty\n\nYour changes before it are saved in project.json.\n\nDetails:\nSystem.InvalidOperationException", said);
            Assert.Contains(nameof(Thrown), said);
        }
    }

    private static Exception Thrown(Exception error)
    {
        try
        {
            throw error;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    [Fact]
    public void TheProjectsFolderIsListedWithTheOneSavedLastFirst()
    {
        var root = TestFiles.NewFolder("start-projects");
        var older = Path.Combine(root, "older");
        var newer = Path.Combine(root, "newer");
        ProjectSession.Create(older, TestFiles.Project(1280, 720, 6));
        ProjectSession.Create(newer, TestFiles.Project());
        Directory.CreateDirectory(Path.Combine(root, "not a project"));
        var broken = Path.Combine(root, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, ProjectStore.ProjectFileName), "{ not json");
        File.SetLastWriteTime(Path.Combine(older, ProjectStore.ProjectFileName), new DateTime(2026, 1, 1, 12, 0, 0));
        File.SetLastWriteTime(Path.Combine(newer, ProjectStore.ProjectFileName), new DateTime(2026, 3, 1, 12, 0, 0));
        File.SetLastWriteTime(Path.Combine(broken, ProjectStore.ProjectFileName), new DateTime(2026, 2, 1, 12, 0, 0));

        var projects = ProjectList.In(root, new DateTime(2026, 11, 17, 12, 0, 0));
        Assert.Equal(["newer", "broken", "older"], projects.Select(p => p.Name));
        Assert.Equal("320×180 · 30 fps · 0 layers · 2026-03-01 12:00", projects[0].Details);
        Assert.Equal("1280×720 · 30 fps · 0 layers · 2026-01-01 12:00", projects[2].Details);
        Assert.Equal(("0:02", "0:06"), (projects[0].Length, projects[2].Length));
        Assert.True(projects[0].Empty);
        Assert.StartsWith("can't be read: ", projects[1].Details);
        Assert.Null(projects[1].Length);
        Assert.Equal(older, projects[2].Directory);

        Assert.Empty(ProjectList.In(Path.Combine(root, "missing")));
    }

    [Fact]
    public void ACardsBadgesGiveTheLengthTheLanguagesAndClaudesEditThatDay()
    {
        var directory = TestFiles.NewProject("card-badges");
        ProjectSession.Open(directory).Mutate("add_text_layer en and ja", p =>
        {
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "en", Text = "Hello", Language = "en" });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "ja", Text = "Konnichiwa", Language = "ja" });
            return 0;
        });
        var saved = ProjectHistory.Saves(directory)[0].Time;
        var card = ProjectList.Describe(directory, saved.AddMinutes(2));
        Assert.Equal(("0:02", "2 languages", "Claude edited 2 min ago"), (card.Length, card.Languages, card.ClaudeEdited));
        Assert.False(card.Empty);
        Assert.Equal(ProjectStore.Load(directory).Revision, card.Revision);
        Assert.Equal((320, 180), (card.Width, card.Height));

        // A day on there's no Claude badge, nor once the editor has saved since; one language has no badge.
        Assert.Null(ProjectList.Describe(directory, saved.AddDays(1)).ClaudeEdited);
        ProjectSession.Open(directory, ProjectBackups.EditorProgram).Mutate("remove_layer ja", p => new RemoveLayerOperation("ja", true).Apply(p));
        card = ProjectList.Describe(directory, DateTime.Now);
        Assert.Equal((null, null), (card.Languages, card.ClaudeEdited));
    }

    [Fact]
    public void HomeListsTheRecentProjectsThenTheFoldersOthersOnceEachAndSearchesTheirNames()
    {
        static ProjectEntry Entry(string directory) => new(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), directory, "", DateTime.MinValue);
        var root = @"C:\Videos\ZeyoCut";
        var tora = Entry(Path.Combine(root, "tora-jump"));
        var comic = Entry(Path.Combine(root, "comic-panels"));
        var street = Entry(Path.Combine(root, "street-demo"));
        ProjectEntry[] recent = [Entry(@"C:\VIDEOS\ZeyoCut\Comic-Panels\"), Entry(@"D:\work\Tora-Short")];

        var (first, rest) = ProjectList.Group(recent, [tora, comic, street], "");
        Assert.Equal(["Comic-Panels", "Tora-Short"], first.Select(p => p.Name));
        Assert.Equal(["tora-jump", "street-demo"], rest.Select(p => p.Name));

        (first, rest) = ProjectList.Group(recent, [tora, comic, street], " TORA ");
        Assert.Equal(["Tora-Short"], first.Select(p => p.Name));
        Assert.Equal(["tora-jump"], rest.Select(p => p.Name));
    }

    [Fact]
    public void HomeSaysWhenAndWhereShortlyAndFitsItsCardsToTheWidth()
    {
        var now = new DateTime(2026, 11, 17, 9, 30, 0);
        Assert.Equal("Today 08:05", ProjectList.When(new DateTime(2026, 11, 17, 8, 5, 0), now));
        Assert.Equal("Yesterday 23:59", ProjectList.When(new DateTime(2026, 11, 16, 23, 59, 0), now));
        Assert.Equal("2026-11-15 18:24", ProjectList.When(new DateTime(2026, 11, 15, 18, 24, 0), now));
        Assert.Equal("2026-11-18 10:00", ProjectList.When(new DateTime(2026, 11, 18, 10, 0, 0), now));

        Assert.Equal(@"…\scratchpad\edemo\projects", ProjectList.Shorten(@"C:\Users\me\AppData\Local\Temp\scratchpad\edemo\projects"));
        Assert.Equal(@"…\me\Videos\ZeyoCut", ProjectList.Shorten(@"C:\Users\me\Videos\ZeyoCut\"));
        Assert.Equal(@"C:\Users\me\Videos", ProjectList.Shorten(@"C:\Users\me\Videos"));
        Assert.Equal(@"…\b\c\d", ProjectList.Shorten(@"\\server\share\a\b\c\d"));

        // Cards at least 240 px wide with 20 px between them, their pictures 16:9.
        Assert.Equal(new HomeLayout(4, 146.25), ProjectList.Layout(1100));
        Assert.Equal(new HomeLayout(3, 180), ProjectList.Layout(1000));
        Assert.Equal(1, ProjectList.Layout(100).Columns);
    }

    [Fact]
    public void RecentProjectsAreKeptNewestFirstOnce()
    {
        var folder = TestFiles.NewFolder("start-settings");
        var path = Path.Combine(folder, "editor.json");
        var settings = EditorSettings.Load(path);
        Assert.Empty(settings.Recent);
        Assert.Null(settings.Window);

        for (var i = 0; i < 12; i++)
        {
            settings.AddRecent(Path.Combine(folder, $"p{i}"));
        }

        settings.AddRecent(Path.Combine(folder, "P5") + Path.DirectorySeparatorChar);
        Assert.Equal(EditorSettings.MaxRecent, settings.Recent.Count);
        Assert.Equal(Path.Combine(folder, "P5"), settings.Recent[0]);
        Assert.Equal(Path.Combine(folder, "p11"), settings.Recent[1]);
        Assert.DoesNotContain(Path.Combine(folder, "p5"), settings.Recent);
        Assert.DoesNotContain(Path.Combine(folder, "p1"), settings.Recent);

        // Saved and read back; only the ones that still hold a project are offered.
        settings.MediaWidth = 333;
        settings.ProjectsAsList = true;
        settings.Window = new WindowPlacement(10, 20, 1200, 800, true);
        settings.Save(path);
        var loaded = EditorSettings.Load(path);
        Assert.Equal(settings.Recent, loaded.Recent);
        Assert.True(loaded.ProjectsAsList);
        Assert.Equal((333.0, new WindowPlacement(10, 20, 1200, 800, true)), (loaded.MediaWidth, loaded.Window));
        Assert.Empty(loaded.ExistingRecent());
        ProjectSession.Create(Path.Combine(folder, "p11"), TestFiles.Project());
        Assert.Equal([Path.Combine(folder, "p11")], loaded.ExistingRecent());
    }

    [Fact]
    public void ASettingsFileThatCantBeReadGivesTheDefaults()
    {
        var folder = TestFiles.NewFolder("start-settings-bad");
        var path = Path.Combine(folder, "editor.json");
        File.WriteAllText(path, "{ nope");
        Assert.Equal(280, EditorSettings.Load(path).MediaWidth);

        File.WriteAllText(path, """{ "recent": ["C:\\a", null, ""], "inspectorWidth": 410 }""");
        var settings = EditorSettings.Load(path);
        Assert.Equal(["C:\\a"], settings.Recent);
        Assert.Equal(410, settings.InspectorWidth);

        File.WriteAllText(path, """{ "recent": null }""");
        Assert.Empty(EditorSettings.Load(path).Recent);
    }

    [Fact]
    public void AProjectIsOpenInOneWindowAtATime()
    {
        var root = TestFiles.NewFolder("start-claim");
        var project = Path.Combine(root, "one");
        var other = Path.Combine(root, "two");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ProjectStore.ProjectFileName), "{}");
        Assert.Equal(ProjectClaim.PipeName(project), ProjectClaim.PipeName(project.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.Equal(project, ProjectList.FolderOf($"\"{Path.Combine(project, ProjectStore.ProjectFileName)}\""));
        Assert.Equal(project, ProjectList.FolderOf(project));
        Assert.NotEqual(ProjectClaim.PipeName(project), ProjectClaim.PipeName(other));

        using var shown = new SemaphoreSlim(0);
        using (var claim = ProjectClaim.TryClaim(project, () => shown.Release()))
        {
            Assert.NotNull(claim);
            Assert.Null(ProjectClaim.TryClaim(project, () => { }));
            using (var second = ProjectClaim.TryClaim(other, () => { }))
            {
                Assert.NotNull(second);
            }

            // Another editor asks the window that has it to come to the front, as often as it likes.
            Assert.True(ProjectClaim.ShownElsewhere(project));
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(ProjectClaim.AskToShow(project, TimeSpan.FromSeconds(2)));
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }

        // Closed, the project can be claimed again, and nobody is asked.
        Assert.False(ProjectClaim.ShownElsewhere(project));
        Assert.False(ProjectClaim.AskToShow(project, TimeSpan.FromMilliseconds(100)));
        using var again = ProjectClaim.TryClaim(project, () => { });
        Assert.NotNull(again);
    }

    [Fact]
    public void TheSettingsFileCanBeMoved()
    {
        var before = Environment.GetEnvironmentVariable(EditorSettings.PathVariable);
        try
        {
            Environment.SetEnvironmentVariable(EditorSettings.PathVariable, @"C:\elsewhere\editor.json");
            Assert.Equal(@"C:\elsewhere\editor.json", EditorSettings.DefaultPath);
            Environment.SetEnvironmentVariable(EditorSettings.PathVariable, null);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeyoCut", "editor.json"), EditorSettings.DefaultPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EditorSettings.PathVariable, before);
        }
    }
}
