// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor.Tests;

// What the start page lists, and the editor's own settings file.
public class StartTests
{
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

        var projects = ProjectList.In(root);
        Assert.Equal(["newer", "broken", "older"], projects.Select(p => p.Name));
        Assert.Equal("320×180 · 30 fps · 0:02 · 0 layers", projects[0].Details);
        Assert.Equal("1280×720 · 30 fps · 0:06 · 0 layers", projects[2].Details);
        Assert.StartsWith("can't be read: ", projects[1].Details);
        Assert.Equal(older, projects[2].Directory);

        Assert.Empty(ProjectList.In(Path.Combine(root, "missing")));
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
        settings.Window = new WindowPlacement(10, 20, 1200, 800, true);
        settings.Save(path);
        var loaded = EditorSettings.Load(path);
        Assert.Equal(settings.Recent, loaded.Recent);
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
