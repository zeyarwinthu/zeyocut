// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Jobs;

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

        // Each card has a title and the size and frame rate exported (a preset keeps the project's
        // frame rate), or else, short, what it needs.
        Assert.Equal(["Project settings", "YouTube 1080p", "Shorts 1080p"], presets.Select(p => p.Title));
        Assert.Equal(["1280×720 · 30 fps", "1920×1080 · 30 fps", "Needs a 9:16 project"], presets.Select(p => p.Detail));

        var vertical = ExportChoices.Presets(TestFiles.Project(720, 1280));
        Assert.Equal([true, false, true], vertical.Select(p => p.Usable));
        Assert.Equal("Needs a 16:9 project", vertical[1].Detail);
        var slow = TestFiles.Project(1280, 720);
        slow.Fps = 24;
        Assert.Equal("1920×1080 · 24 fps", ExportChoices.Presets(slow)[1].Detail);
    }

    [Fact]
    public void TheQualitiesAreStartExportsCrf()
    {
        Assert.Equal([("High", 18), ("Balanced", 23), ("Small", 28)], ExportChoices.Qualities.Select(q => (q.Name, q.Crf)));
        Assert.All(ExportChoices.Qualities, q => Assert.StartsWith($"crf {q.Crf}, ", q.Tip));
    }

    [Fact]
    public void CaptionGroupsCanAlsoBeSavedAsSrtFilesNamedForTheirLanguage()
    {
        var project = TestFiles.Project();
        Assert.Empty(ExportChoices.Subtitles(project));
        Assert.Equal("None", ExportChoices.CaptionsSummary(project, null, []));

        // A group with no captions in it isn't offered.
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "captions" });
        Assert.Empty(ExportChoices.Subtitles(project));

        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "cue", Text = "Hi", ParentId = "captions", Start = 0, End = 1 });
        CaptionTracks.Add(project, "ja", CaptionTracks.Check([new CueEntry(0, 1, "こんにちは")], project.Duration).Cues, new CaptionOptions(), [], null);
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 1, "Hello")], project.Duration).Cues, new CaptionOptions(), [], null);
        var subtitles = ExportChoices.Subtitles(project);
        Assert.Equal(["Captions", "EN", "JA"], subtitles.Select(s => s.Label));
        Assert.Equal(["captions", "captions_en", "captions_ja"], subtitles.Select(s => s.GroupId));

        // talk.mp4's English captions go in talk.en.srt, a group without a language in talk.srt, and
        // a second group in one language by its id.
        Assert.Equal(["talk.srt", "talk.en.srt", "talk.ja.srt"], ExportChoices.SrtFiles("talk.mp4", subtitles).Select(f => f.Name));
        project.FindLayer("captions")!.Language = "en";
        var tagged = ExportChoices.Subtitles(project);
        Assert.Equal(["EN", "EN", "JA"], tagged.Select(s => s.Label));
        Assert.Equal([("captions", "talk.en.srt"), ("captions_en", "talk.captions_en.srt"), ("captions_ja", "talk.ja.srt")],
            ExportChoices.SrtFiles("talk.mp4", tagged).Select(f => (f.GroupId, f.Name)));
        project.FindLayer("captions")!.Language = null;

        // The summary card names the captions drawn in the version exported and the .srt files.
        Assert.Equal("Captions, EN, JA burned in", ExportChoices.CaptionsSummary(project, null, []));
        Assert.Equal("Captions, JA burned in + EN .srt", ExportChoices.CaptionsSummary(project, "ja", [subtitles[1]]));
        Assert.Equal("Captions, EN burned in + .srt", ExportChoices.CaptionsSummary(project, "en", [subtitles[0], subtitles[1]]));
        project.FindLayer("captions")!.Visible = false;
        Assert.Equal("EN burned in", ExportChoices.CaptionsSummary(project, "en", []));
        Assert.Equal("JA .srt", ExportChoices.CaptionsSummary(project, "de", [subtitles[2]]));
    }

    [Fact]
    public void ClaudesRunningExportsComeFromTheServersJobRecords()
    {
        var directory = TestFiles.NewProject("claude-exports");
        var exports = Path.Combine(directory, ProjectStore.ExportsFolder);
        JobRecord Record(string id, double progress, string? stage, JsonObject? resume) => new() { Id = id, Kind = id[..id.IndexOf('_')], Progress = progress, Stage = stage, Resume = resume };
        var running = ExportChoices.ServerExports(
        [
            Record("export_1", 0.62, "rendering", new JsonObject { ["projectDirectory"] = directory + Path.DirectorySeparatorChar, ["outputPath"] = Path.Combine(exports, "talk-ja.mp4") }),
            Record("batch_1", 0.25, "exporting shorts_1080p", new JsonObject { ["projectDirectory"] = directory, ["outputs"] = new JsonArray(Path.Combine(exports, "a.mp4"), Path.Combine(exports, "b.mp4")) }),
            Record("export_2", 0.5, "rendering", new JsonObject { ["projectDirectory"] = Path.Combine(directory, "..", "other"), ["outputPath"] = "x.mp4" }),
            Record("analyze_1", 0.5, null, null),
            Record("export_3", 1, "finishing encode", new JsonObject { ["projectDirectory"] = directory.ToUpperInvariant(), ["outputPath"] = Path.Combine(exports, "talk.mp4") }),
        ], directory);

        Assert.Equal(["talk-ja.mp4", "a.mp4, b.mp4", "talk.mp4"], running.Select(r => r.Name));
        Assert.Equal(["Rendering · 62%", "Exporting shorts_1080p · 25%", "Finishing encode · 100%"], running.Select(r => r.Status));
        Assert.Equal([62, 25, 100], running.Select(r => r.Percent));
        Assert.All(running, r => Assert.Equal("Started by Claude", r.By));
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

        var files = ExportChoices.Files(directory, new DateTime(2026, 11, 2, 20, 0, 0));
        Assert.Equal(["second.mp4", "first.mp4"], files.Select(f => f.Name));
        Assert.Equal(["12 MB · Today 18:05", "1.5 MB · Yesterday 09:30"], files.Select(f => f.Details));
        Assert.Equal(Path.Combine(exports, "second.mp4"), files[0].Path);
        Assert.Equal("1.5 MB · 2026-11-01 09:30", ExportChoices.Files(directory, new DateTime(2026, 11, 5))[1].Details);
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
        Assert.Equal([4L, 3L, 2L], saves.Select(s => s.Revision));
    }

    // A save as the backups folder lists it, at 10:mm:ss on 18 November unless 'day' says otherwise.
    private static SavedChange Save(long revision, int minute, int second, string edit, bool byEditor, int day = 18) =>
        new(new DateTime(2026, 11, day, 10, minute, second), edit, byEditor)
        {
            Revision = revision,
            Backup = new ProjectBackup($"backup-{revision}", $"backup-{revision}.json", new DateTime(2026, 11, day, 10, minute, second), edit, null),
        };

    private static readonly SavedChange[] Kept =
    [
        Save(9, 30, 0, "update_layer title", true),
        Save(8, 26, 0, "update_layer title", true),
        Save(7, 25, 30, "move_layer title", true),
        Save(6, 20, 30, "trim_layer clip", true),
        Save(5, 20, 0, "add_markers (1 marker)", false),
        Save(4, 19, 0, "add_text_layer title", false),
        Save(2, 18, 0, "update_project", false, day: 16),
        Save(1, 17, 0, "update_layer clip", true, day: 16),
    ];

    // Saves in a row by the same side, each under 5 minutes after the one before, are one step,
    // named by rule from its first edit. A step is put back from the copy kept before the next save;
    // the newest is the project as it is, and one whose next save isn't kept has nothing to put back.
    [Fact]
    public void SavesInARowBySideUnderFiveMinutesApartAreOneStep()
    {
        var steps = ProjectHistory.Steps(Kept);
        Assert.Equal(
            [
                ("move_layer title, then 2 more", "You", "Y", "10:25 – 10:30 · rev 9"),
                ("trim_layer clip", "You", "Y", "10:20 · rev 6"),
                ("add_text_layer title, then 1 more", "Claude", "C", "10:19 – 10:20 · rev 5"),
                ("update_project", "Claude", "C", "10:18 · rev 2"),
                ("update_layer clip", "You", "Y", "10:17 · rev 1"),
            ],
            steps.Select(s => (s.Title, s.Who, s.Initial, s.Details)));
        Assert.Equal(["move_layer title", "update_layer title ×2"], steps[0].Edits);
        Assert.Equal(["add_text_layer title", "add_markers (1 marker)"], steps[2].Edits);

        Assert.Equal([true, false, false, false, false], steps.Select(s => s.Newest));
        Assert.Equal([null, "backup-7", "backup-6", null, "backup-2"], steps.Select(s => s.Restore?.Name));

        // Revision 3 isn't kept: the saves on either side of it aren't one step, and the older can't
        // be put back, since no copy holds the project as it left it.
        Assert.Equal([false, false, false, true, false], steps.Select(s => s.Gap));

        // A copy that doesn't say its revision can't be shown to follow another.
        var unknown = ProjectHistory.Steps([Kept[0], Kept[1] with { Revision = null }]);
        Assert.Equal([1, 1], unknown.Select(s => s.Saves.Count));
        Assert.Null(unknown[1].Restore);
        Assert.False(unknown[1].Gap);
        Assert.Equal("10:26", unknown[1].Details);

        // A restore is a step of its own, so the step before it can still be put back.
        var restored = ProjectHistory.Steps([Save(11, 31, 0, "update_layer title", true), Save(10, 30, 30, "restore_backup project-20261118-101900-000", true), .. Kept]);
        Assert.Equal(["update_layer title", "restore_backup project-20261118-101900-000", "move_layer title, then 2 more"], restored.Take(3).Select(s => s.Title));
        Assert.Equal(["backup-11", "backup-10"], restored.Skip(1).Take(2).Select(s => s.Restore?.Name));

        Assert.Empty(ProjectHistory.Steps([]));
    }

    // The filter keeps one side's steps; the first step of each day shown is headed with it, and a
    // step after saves that aren't kept says so, also when the step it was on is filtered out.
    [Fact]
    public void TheFilterKeepsOneSidesStepsUnderTheirDays()
    {
        var steps = ProjectHistory.Steps(Kept);
        var now = new DateTime(2026, 11, 18, 12, 0, 0);
        Assert.Equal(
            [("move_layer title, then 2 more", "Today", false), ("trim_layer clip", null, false), ("add_text_layer title, then 1 more", null, false), ("update_project", "2026-11-16", true), ("update_layer clip", null, false)],
            ProjectHistory.Shown(steps, null, now).Select(s => (s.Title, s.Day, s.Gap)));
        Assert.Equal(
            [("move_layer title, then 2 more", "Today", false), ("trim_layer clip", null, false), ("update_layer clip", "2026-11-16", true)],
            ProjectHistory.Shown(steps, true, now).Select(s => (s.Title, s.Day, s.Gap)));
        Assert.Equal(
            [("add_text_layer title, then 1 more", "Yesterday", false), ("update_project", "2026-11-16", true)],
            ProjectHistory.Shown(steps, false, now.AddDays(1)).Select(s => (s.Title, s.Day, s.Gap)));
    }

    // From a real backups folder: the revision each save made, and a copy that puts a step back as it
    // left the project.
    [Fact]
    public void TheStepsComeFromTheBackupsWithTheRevisionsTheySaved()
    {
        var directory = TestFiles.NewProject("steps");
        Assert.Empty(ProjectHistory.Steps(directory));

        ProjectSession.Open(directory).Mutate("add_text_layer title", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hello" }));
        var editor = ProjectSession.Open(directory, ProjectBackups.EditorProgram);
        editor.Mutate("move_layer title", p => new MoveLayerOperation("title", 0.5).Apply(p));
        editor.Mutate("trim_layer title", p => new TrimLayerOperation("title", null, 1.5).Apply(p));

        var steps = ProjectHistory.Steps(directory);
        Assert.Equal([("move_layer title, then 1 more", "You", 4L), ("add_text_layer title", "Claude", 2L)], steps.Select(s => (s.Title, s.Who, s.Revision)));
        Assert.Null(steps[0].Restore);
        var restored = ProjectBackups.Load(steps[1].Restore!);
        Assert.Equal((2L, 0.0), (restored.Revision, restored.FindLayer("title")!.Start));
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
