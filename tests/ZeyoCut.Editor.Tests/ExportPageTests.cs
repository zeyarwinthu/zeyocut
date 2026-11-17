// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// What the Export and Assistant views offer and list.
public class ExportPageTests
{
    [Fact]
    public void PresetsOfAnotherShapeThanTheCanvasCantBeChosen()
    {
        var presets = ExportChoices.Presets(TestFiles.Project(1280, 720));
        Assert.Equal([ExportChoices.ProjectSettings, "youtube_1080p", "shorts_1080p"], presets.Select(p => p.Name));
        Assert.Equal([true, true, false], presets.Select(p => p.Usable));
        Assert.Null(presets[0].Preset);
        Assert.Equal("1280×720 at 30 fps, as the project is set up", presets[0].Tip);
        Assert.Equal("shorts_1080p is 1080×1920, another shape than the project's 1280×720; ask Claude to change the canvas first", presets[2].Tip);

        var vertical = ExportChoices.Presets(TestFiles.Project(720, 1280));
        Assert.Equal([true, false, true], vertical.Select(p => p.Usable));
    }

    [Fact]
    public void LanguagesAreOfferedOnceALayerIsTagged()
    {
        var project = TestFiles.Project();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello" });
        Assert.Empty(ExportChoices.Languages(project));

        CaptionTracks.Add(project, "ja", CaptionTracks.Check([new CueEntry(0, 1, "こんにちは")], project.Duration).Cues, new CaptionOptions(), [], null);
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 1, "Hello")], project.Duration).Cues, new CaptionOptions(), [], null);
        Assert.Equal([ExportChoices.EveryLanguage, "en", "ja"], ExportChoices.Languages(project));
    }

    [Fact]
    public void TheExportsFolderListsFinishedFilesNewestFirst()
    {
        var directory = TestFiles.NewProject("export-files");
        Assert.Equal("project.mp4", ExportChoices.DefaultFileName(directory + Path.DirectorySeparatorChar));
        Assert.Empty(ExportChoices.Files(directory));

        var exports = Directory.CreateDirectory(Path.Combine(directory, ProjectStore.ExportsFolder)).FullName;
        void Write(string name, int bytes, DateTime saved)
        {
            var path = Path.Combine(exports, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTime(path, saved);
        }

        Write("first.mp4", 3 * 1048576 / 2, new DateTime(2026, 11, 1, 9, 30, 0));
        Write("second.mp4", 12 * 1048576, new DateTime(2026, 11, 2, 18, 5, 0));
        Write("third.partial.mp4", 10, DateTime.Now);
        Write("notes.txt", 10, DateTime.Now);

        var files = ExportChoices.Files(directory);
        Assert.Equal(["second.mp4", "first.mp4"], files.Select(f => f.Name));
        Assert.Equal(["12 MB · saved 2026-11-02 18:05", "1.5 MB · saved 2026-11-01 09:30"], files.Select(f => f.Details));
        Assert.Equal(Path.Combine(exports, "second.mp4"), files[0].Path);
    }

    [Fact]
    public void TheRecentSavesSayWhoMadeThem()
    {
        var directory = TestFiles.NewProject("saves");
        Assert.Empty(ProjectHistory.Saves(directory));

        var server = ProjectSession.Open(directory);
        server.Mutate("add_text_layer title", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hello" }));
        var editor = ProjectSession.Open(directory, ProjectBackups.EditorProgram);
        editor.Mutate("move_layer title", p => new MoveLayerOperation("title", 0.5).Apply(p));
        editor.Mutate("trim_layer title", p => new TrimLayerOperation("title", null, 1.5).Apply(p));

        var saves = ProjectHistory.Saves(directory);
        Assert.Equal([("trim_layer title", "You"), ("move_layer title", "You"), ("add_text_layer title", "Claude")], saves.Select(s => (s.Edit, s.Who)));
        Assert.True(saves[0].Time >= saves[2].Time);
    }

    [Fact]
    public void TheHeaderSaysWhenClaudeSavedLast()
    {
        var directory = TestFiles.NewProject("claude-saved");
        Assert.Null(ProjectHistory.LastByClaude(directory));

        ProjectSession.Open(directory, ProjectBackups.EditorProgram)
            .Mutate("add_text_layer title", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hello" }));
        Assert.Null(ProjectHistory.LastByClaude(directory));

        ProjectSession.Open(directory).Mutate("move_layer title", p => new MoveLayerOperation("title", 0.5).Apply(p));
        ProjectSession.Open(directory, ProjectBackups.EditorProgram).Mutate("trim_layer title", p => new TrimLayerOperation("title", null, 1.5).Apply(p));
        Assert.Equal("move_layer title", ProjectHistory.LastByClaude(directory)!.Edit);

        var now = new DateTime(2026, 11, 17, 12, 0, 0);
        Assert.Equal("just now", ProjectHistory.Ago(now.AddSeconds(-59), now));
        Assert.Equal("just now", ProjectHistory.Ago(now.AddSeconds(5), now));
        Assert.Equal("1 min ago", ProjectHistory.Ago(now.AddMinutes(-1), now));
        Assert.Equal("59 min ago", ProjectHistory.Ago(now.AddSeconds(-3599), now));
        Assert.Equal("1 h ago", ProjectHistory.Ago(now.AddHours(-1), now));
        Assert.Equal("23 h ago", ProjectHistory.Ago(now.AddMinutes(-1439), now));
        Assert.Equal("on 2026-11-16", ProjectHistory.Ago(now.AddDays(-1), now));
    }
}
