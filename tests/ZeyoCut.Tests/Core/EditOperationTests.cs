// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// trim_layer, and the operations the editor records: each makes the change its MCP tool makes.
public class EditOperationTests
{
    private static T Layer<T>(Project project, string id) where T : Layer => Assert.IsType<T>(project.FindLayer(id));

    // The layers drawn every quarter second, bottom first.
    private static List<string> DrawOrders(Project project) =>
        Enumerable.Range(0, (int)(project.Duration * 4))
            .Select(i => string.Join(" ", SceneEvaluator.Evaluate(project, i / 4.0).Where(e => e.Shown).Select(e => e.Layer.Id)))
            .ToList();

    [Fact]
    public void TrimmingInCutsTheLayerAndItsChildrenAndKeepsTheFades()
    {
        var project = TestProjects.Footage();
        var end = TimelineEditor.TrimLayer(project, "clip_2", null, 7);
        Assert.Equal("trimmed 'clip_2': now 4–7s (was 4–8s)", end.Summary);
        var clip = Layer<VideoLayer>(project, "clip_2");
        Assert.Equal((4.0, 4.0, 7.0, 0.5), (clip.Start, clip.TrimIn, clip.TrimOut, clip.FadeOut));
        Assert.Equal((4.0, 7.0), (project.FindLayer("label")!.Start, project.FindLayer("label")!.End));

        // Trimming doesn't shorten the project.
        Assert.Equal(8, project.Duration);
        Assert.Equal(end.OldDuration, end.NewDuration);

        var start = TimelineEditor.TrimLayer(project, "clip_2", 5, null);
        Assert.Equal("trimmed 'clip_2': now 5–7s (was 4–7s)", start.Summary);
        clip = Layer<VideoLayer>(project, "clip_2");
        Assert.Equal((5.0, 5.0, 7.0, 0.0, 0.5), (clip.Start, clip.TrimIn, clip.TrimOut, clip.FadeIn, clip.FadeOut));
        Assert.Equal((5.0, 7.0), (project.FindLayer("label")!.Start, project.FindLayer("label")!.End));

        // Both edges at once; the first clip keeps its fade in.
        TimelineEditor.TrimLayer(project, "clip_1", 1, 3);
        var first = Layer<VideoLayer>(project, "clip_1");
        Assert.Equal((1.0, 1.0, 3.0, 0.5), (first.Start, first.TrimIn, first.TrimOut, first.FadeIn));
    }

    [Fact]
    public void TrimmingInRemovesAChildWithNothingLeft()
    {
        var project = TestProjects.Footage();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "tag", Text = "Tag", ParentId = "clip_2", Start = 4, End = 5 });
        var change = TimelineEditor.TrimLayer(project, "clip_2", 6, null);
        Assert.Equal("trimmed 'clip_2': now 6–8s (was 4–8s); removed 'tag', which had nothing left", change.Summary);
        Assert.Null(project.FindLayer("tag"));
        Assert.Equal(6, project.FindLayer("label")!.Start);
    }

    [Fact]
    public void TrimmingOutShowsMoreAsFarAsTheSourceGoes()
    {
        var project = TestProjects.Footage();
        var change = TimelineEditor.TrimLayer(project, "clip_2", null, 10);
        Assert.Equal("trimmed 'clip_2': now 4–10s (was 4–8s)", change.Summary);
        Assert.Equal((8.0, 10.0), (change.OldDuration, change.NewDuration));
        Assert.Equal(10, Layer<VideoLayer>(project, "clip_2").TrimOut);
        Assert.Equal(10, project.Duration);

        // Children stay as they are.
        Assert.Equal(8, project.FindLayer("label")!.End);

        var late = Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "clip_2", null, 10.5));
        Assert.Equal("'clip_2' can end at 10s at the latest: its source has nothing after that", late.Message);

        TimelineEditor.TrimLayer(project, "clip_2", 2, null);
        var clip = Layer<VideoLayer>(project, "clip_2");
        Assert.Equal((2.0, 2.0, 10.0), (clip.Start, clip.TrimIn, clip.TrimOut));

        // Sound: the music can end where its 20 s run out, and start no earlier than its source.
        TimelineEditor.MoveLayer(project, "music", 1);
        var early = Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "music", 0.5, null));
        Assert.Equal("'music' can start at 1s at the earliest: its source has nothing before that", early.Message);
        var past = Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "music", null, 22));
        Assert.Equal("'music' can end at 21s at the latest: its source has nothing after that", past.Message);
        TimelineEditor.TrimLayer(project, "music", 3, 21);
        var music = Layer<AudioLayer>(project, "music");
        Assert.Equal((3.0, 2.0, 21.0, 1.0), (music.Start, music.TrimIn, music.End!.Value, music.FadeOut));
        Assert.Equal(21, project.Duration);

        // Text and pictures have no source to run out of.
        TimelineEditor.TrimLayer(project, "title", null, 25);
        Assert.Equal((0.0, 25.0), (project.FindLayer("title")!.Start, project.FindLayer("title")!.End));
        Assert.Equal(25, project.Duration);
    }

    [Fact]
    public void ALayerWithoutAnEndCanBeTrimmed()
    {
        var project = TestProjects.Footage();
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 200, Height = 100 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" } });
        Assert.Null(project.FindLayer("logo")!.End);

        Assert.Equal("trimmed 'logo': now 0–2s (was 0–8s)", TimelineEditor.TrimLayer(project, "logo", null, 2).Summary);
        Assert.Equal(2, project.FindLayer("logo")!.End);
        TimelineEditor.TrimLayer(project, "logo", 1, 9);
        Assert.Equal((1.0, 9.0, 9.0), (project.FindLayer("logo")!.Start, project.FindLayer("logo")!.End!.Value, project.Duration));
    }

    [Fact]
    public void TrimmingNeedsANewEdge()
    {
        var project = TestProjects.Footage();
        Assert.Equal("give a new start, a new end or both", Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "clip_2", null, null)).Message);
        Assert.Equal("the end (5s) must be after the start (6s)", Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "clip_2", 6, 5)).Message);
        Assert.Equal("layer 'clip_2' already runs 4–8s", Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "clip_2", 4, 8)).Message);
        Assert.Equal("start must be >= 0 and the times finite (got -1–8)", Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "clip_2", -1, null)).Message);
        Assert.Contains("nope", Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(project, "nope", 1, null)).Message);
        Assert.Equal(ProjectJson.Serialize(TestProjects.Footage()), ProjectJson.Serialize(project));
    }

    [Fact]
    public void OperationsMakeTheSameChangesAsTheEditorsTheirToolsCall()
    {
        var volume = new JsonObject { ["volume"] = 0.3 };
        var cases = new (EditOperation Operation, Action<Project> Direct)[]
        {
            (new MoveLayerOperation("clip_2", 9), p => TimelineEditor.MoveLayer(p, "clip_2", 9)),
            (new TrimLayerOperation("clip_2", 5, 7), p => TimelineEditor.TrimLayer(p, "clip_2", 5, 7)),
            (new SplitLayerOperation("clip_2", 6), p => TimelineEditor.SplitLayer(p, "clip_2", 6)),
            (new RemoveLayerOperation("clip_2", WithChildren: true), p => ProjectEditor.RemoveLayer(p, "clip_2", withChildren: true)),
            (new RemoveLayerOperation("label", WithChildren: false), p => ProjectEditor.RemoveLayer(p, "label", withChildren: false)),
            (new UpdateLayerOperation("music", volume), p => ProjectEditor.UpdateLayer(p, "music", new JsonObject { ["volume"] = 0.3 })),
            (new ReorderLayerOperation("title", 2), p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["zIndex"] = 2 })),
            (new SetSpeedOperation("clip_2", 2), p => TimelineEditor.SetSpeed(p, "clip_2", 2, ripple: false)),
            (new SetSpeedOperation("clip_1", 0.5), p => TimelineEditor.SetSpeed(p, "clip_1", 0.5, ripple: false)),
        };
        foreach (var (operation, direct) in cases)
        {
            var applied = TestProjects.Footage();
            operation.Apply(applied);
            var expected = TestProjects.Footage();
            direct(expected);
            Assert.Equal(ProjectJson.Serialize(expected), ProjectJson.Serialize(applied));
        }

        // The same operation applies again (its changes are copied each time).
        var again = TestProjects.Footage();
        cases[5].Operation.Apply(again);
        Assert.Equal(0.3, Layer<AudioLayer>(again, "music").Volume);
    }

    [Fact]
    public void OperationsAreLabelledAndSummedUpLikeTheirTools()
    {
        var project = TestProjects.Footage();
        var move = new MoveLayerOperation("title", 1);
        Assert.Equal("move_layer title", move.Label);
        Assert.Equal(["title"], move.Layers);
        Assert.Equal("moved 'title' by +1s: now 1–4s", move.Apply(project));

        var trim = new TrimLayerOperation("clip_2", null, 7);
        Assert.Equal("trim_layer clip_2", trim.Label);
        Assert.Equal("trimmed 'clip_2': now 4–7s (was 4–8s)", trim.Apply(project));

        var split = new SplitLayerOperation("clip_1", 2);
        Assert.Equal("split_layer clip_1", split.Label);
        Assert.Equal("split 'clip_1' at 2s into 'clip_1' and 'clip_3'", split.Apply(project));

        var remove = new RemoveLayerOperation("clip_2", WithChildren: true);
        Assert.Equal("remove_layer clip_2", remove.Label);
        Assert.Equal("removed: clip_2, label", remove.Apply(project));

        var reorder = new ReorderLayerOperation("title", 1);
        Assert.Equal("reorder_layer title", reorder.Label);
        Assert.Equal("'title' now draws at z=1", reorder.Apply(project));
        Assert.Equal(1, Layer<TextLayer>(project, "title").ZIndex);
    }

    // The editor's speed field changes the clip as set_speed with ripple off does: the clip keeps its
    // start, and the layers after it stay.
    [Fact]
    public void SpeedStretchesTheClipAndLeavesTheLayersAfterIt()
    {
        var project = TestProjects.Footage();
        var speed = new SetSpeedOperation("clip_1", 2);
        Assert.Equal("set_speed clip_1", speed.Label);
        Assert.Equal(["clip_1"], speed.Layers);
        var summary = speed.Apply(project);
        Assert.Equal(TimelineEditor.SetSpeed(TestProjects.Footage(), "clip_1", 2, ripple: false).Summary, summary);
        var clip = Layer<VideoLayer>(project, "clip_1");
        Assert.Equal((0.0, 2.0, 2.0), (clip.Start, clip.TimelineEnd, clip.Speed));
        Assert.Equal(4, project.FindLayer("clip_2")!.Start);

        Assert.Equal("layer 'clip_1' already plays at 2x", Assert.Throws<ZeyoCutException>(() => speed.Apply(project)).Message);
        Assert.StartsWith("set_speed works on video layers", Assert.Throws<ZeyoCutException>(() => new SetSpeedOperation("title", 2).Apply(project)).Message);
    }

    [Fact]
    public void ReorderingTakesTheChildrenAlongAndInsertingMakesRoom()
    {
        // clip_1 draws at z=0, clip_2 at 1 with its label at 5, the title at 10.
        var project = TestProjects.Footage();
        Assert.Equal("'clip_2' now draws at z=3 with 1 child layer(s)", new ReorderLayerOperation("clip_2", 3).Apply(project));
        Assert.Equal((3, 7), (Layer<VideoLayer>(project, "clip_2").ZIndex, Layer<TextLayer>(project, "label").ZIndex));

        // A level of its own where another layer draws: that one and those above it move up.
        project = TestProjects.Footage();
        Assert.Equal("'title' now draws at z=1; 2 layer(s) drawn at z=1 or above moved up 1", new ReorderLayerOperation("title", 1, Insert: true).Apply(project));
        Assert.Equal([0, 1, 2, 6], new[] { "clip_1", "title", "clip_2", "label" }.Select(id => project.FindLayer(id)!.ZIndex));

        // Where nothing draws, nothing else moves.
        project = TestProjects.Footage();
        Assert.Equal("'title' now draws at z=3", new ReorderLayerOperation("title", 3, Insert: true).Apply(project));
        Assert.Equal([0, 1, 5, 3], new[] { "clip_1", "clip_2", "label", "title" }.Select(id => project.FindLayer(id)!.ZIndex));

        // A layer with children takes as many levels as they span, and the sound has no level.
        project = TestProjects.Footage();
        Assert.Equal(
            "'clip_2' now draws at z=0 with 1 child layer(s); 2 layer(s) drawn at z=0 or above moved up 5",
            new ReorderLayerOperation("clip_2", 0, Insert: true).Apply(project));
        Assert.Equal([0, 4, 5, 15], new[] { "clip_2", "label", "clip_1", "title" }.Select(id => project.FindLayer(id)!.ZIndex));
        Assert.Throws<ZeyoCutException>(() => new ReorderLayerOperation("music", 2).Apply(project));
    }

    [Fact]
    public void PackingPutsLayersThatNeverShowTogetherOnOneLevel()
    {
        // clip_1 shows 0–4 s at z=0, clip_2 4–8 s at 1 with its label at 5, the title 0–3 s at 10.
        var project = TestProjects.Footage();
        var before = DrawOrders(project);
        Assert.Equal(new Dictionary<string, int> { ["clip_2"] = 0, ["label"] = 1, ["title"] = 1 }, ProjectEditor.PackedLevels(project));
        Assert.Equal(
            "packed the top-level layers from 3 draw level(s) onto 2; 3 layer(s) changed level, and every frame is drawn as before",
            new PackLanesOperation().Apply(project));
        Assert.Equal([0, 0, 1, 1], new[] { "clip_1", "clip_2", "label", "title" }.Select(id => project.FindLayer(id)!.ZIndex));
        Assert.Equal(before, DrawOrders(project));
        Assert.Empty(ProjectEditor.PackedLevels(project));
        Assert.StartsWith("the lanes are already packed", Assert.Throws<ZeyoCutException>(() => ProjectEditor.PackLanes(project)).Message);

        // Clips added one at a time, each on a level of its own, share one. Text stays above
        // everything it shows with: the outro shows with the label, so it goes above it, and the
        // title, which shows with neither, shares the outro's level rather than take a lane of its own.
        project = TestProjects.Footage();
        project.Duration = 12;
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip_3", Start = 8, TrimIn = 0, TrimOut = 4 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "outro", Text = "Bye", Start = 3, End = 12 });
        before = DrawOrders(project);
        new PackLanesOperation().Apply(project);
        Assert.Equal([0, 0, 0, 1, 2, 2], new[] { "clip_1", "clip_2", "clip_3", "label", "title", "outro" }.Select(id => project.FindLayer(id)!.ZIndex));
        Assert.Equal(before, DrawOrders(project));

        // A cutout shows while its source does, so it stays above it. A layer without an end shows
        // from its start on, so it stays above everything under it from then on.
        project = TestProjects.Footage();
        project.Layers.Add(new CutoutLayer { Id = "cut", Sources = ["clip_2"], ZIndex = 3 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "late", Text = "Late", Start = 2, ZIndex = 4 });
        before = DrawOrders(project);
        new PackLanesOperation().Apply(project);
        Assert.Equal([0, 0, 1, 2, 3, 3], new[] { "clip_1", "clip_2", "cut", "late", "label", "title" }.Select(id => project.FindLayer(id)!.ZIndex));
        Assert.Equal(before, DrawOrders(project));

        // Packing that saves no level changes nothing. A text at level 0 shows with clip_2, and the
        // outro with clip_2's label, so the title could only trade the outro's level for clip_2's.
        project = TestProjects.Footage();
        project.Duration = 12;
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "late", Text = "Late", Start = 5, End = 12, ZIndex = 0 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "outro", Text = "Bye", Start = 4, End = 8, ZIndex = 10 });
        Assert.Empty(ProjectEditor.PackedLevels(project));
    }

    [Fact]
    public void AddingAClipPutsItOnALevelOrOnTopAndSoundInItsRole()
    {
        var project = TestProjects.Footage();
        project.Assets.Add(new Asset { Id = "photo", Type = AssetType.Image, File = "assets/photo.png", Width = 3200, Height = 1800 });
        project.Assets.Add(new Asset { Id = "font", Type = AssetType.Font, File = "assets/font.ttf" });

        var video = new AddClipOperation(AssetType.Video, "clip", 6, ZIndex: 0);
        Assert.Equal(("add_video_layer clip", 0), (video.Label, video.Layers.Count));
        Assert.Equal("added video layer 'clip' at 6s on draw level 0", video.Apply(project));
        Assert.Equal((6.0, 16.0, 0), (project.FindLayer("clip")!.Start, Layer<VideoLayer>(project, "clip").TimelineEnd, Layer<VideoLayer>(project, "clip").ZIndex));
        Assert.Equal(16, project.Duration);

        // A still lasts 5 s and is made smaller to fit the canvas; with no level, it goes on top.
        Assert.Equal("added image layer 'photo' at 14s on draw level 11", new AddClipOperation(AssetType.Image, "photo", 14).Apply(project));
        var photo = Layer<ImageLayer>(project, "photo");
        Assert.Equal((14.0, 19.0, 0.2, "photo"), (photo.Start, photo.End!.Value, photo.ScaleX.Value, photo.Drawings["normal"]));
        Assert.Equal(19, project.Duration);

        // Sound doesn't make the project longer.
        Assert.Equal("added audio layer 'music_2' at 1s as voice", new AddClipOperation(AssetType.Audio, "music", 1, Role: "voice").Apply(project));
        Assert.Equal((AudioRole.Voice, 19.0), (Layer<AudioLayer>(project, "music_2").Role, project.Duration));

        Assert.Equal("'font' is a font: only video, images and sound go on the timeline", Assert.Throws<ZeyoCutException>(() => new AddClipOperation(AssetType.Font, "font", 0).Apply(project)).Message);
        Assert.Throws<ZeyoCutException>(() => new AddClipOperation(AssetType.Video, "photo", 0).Apply(project));
    }

    [Fact]
    public void OneChangeCanStyleEveryCaptionInATrack()
    {
        var project = TestProjects.Footage();
        TestProjects.AddCaptions(project);
        var cues = project.Layers.Where(l => l.ParentId == "captions_en").Select(l => l.Id).ToList();
        var style = new UpdateLayersOperation("update_layer captions_en", cues, new JsonObject { ["fontSize"] = 30, ["color"] = "#FFEE00" });
        Assert.Equal(("update_layer captions_en", cues), (style.Label, style.Layers));
        Assert.Equal("updated 2 layer(s): fontSize, color", style.Apply(project));
        Assert.All(cues, id => Assert.Equal((30.0, "#FFEE00"), (Layer<TextLayer>(project, id).FontSize, Layer<TextLayer>(project, id).Color)));
        Assert.Equal("there are no layers to change", Assert.Throws<ZeyoCutException>(() => new UpdateLayersOperation("none", [], new JsonObject { ["fontSize"] = 1 }).Apply(project)).Message);
    }

    [Fact]
    public void LimitsSayHowFarATrimAndAMoveReach()
    {
        // clip_2 plays 4–8 s of a 10 s file. Moved to start at 6 s, its start reaches back to 2 s and
        // its end on to 12 s, and each limit is where the trim stops.
        var project = TestProjects.Footage();
        TimelineEditor.MoveLayer(project, "clip_2", 6);
        Assert.Equal(new TimelineLimits(2, 12, 0), TimelineEditor.Limits(project, "clip_2"));
        Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(ProjectJson.Copy(project), "clip_2", 1.9, null));
        Assert.Throws<ZeyoCutException>(() => TimelineEditor.TrimLayer(ProjectJson.Copy(project), "clip_2", null, 12.1));
        TimelineEditor.TrimLayer(ProjectJson.Copy(project), "clip_2", 2, 12);

        // The music plays the first 8 s of a 20 s file; a title reaches on without end.
        Assert.Equal(new TimelineLimits(0, 20, 0), TimelineEditor.Limits(project, "music"));
        Assert.Equal(new TimelineLimits(0, double.PositiveInfinity, 0), TimelineEditor.Limits(project, "title"));

        // A child that starts before its parent stops a move first.
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "early", Text = "Early", ParentId = "clip_2", Start = 5, End = 7 });
        Assert.Equal(1, TimelineEditor.Limits(project, "clip_2").EarliestMove);
        Assert.Throws<ZeyoCutException>(() => TimelineEditor.MoveLayer(ProjectJson.Copy(project), "clip_2", 0.9));
        TimelineEditor.MoveLayer(ProjectJson.Copy(project), "clip_2", 1);
    }

    [Fact]
    public void UpdateLayerOperationChangesSoundFadesAndCaptions()
    {
        var project = TestProjects.Footage();
        var captions = TestProjects.AddCaptions(project);

        var louder = new UpdateLayerOperation("music", new JsonObject { ["volume"] = 0.25, ["fadeIn"] = 1.5 });
        Assert.Equal(("update_layer music", "updated layer 'music'"), (louder.Label, louder.Apply(project)));
        var music = Layer<AudioLayer>(project, "music");
        Assert.Equal((0.25, 1.5, 1.0), (music.Volume, music.FadeIn, music.FadeOut));

        // An audio layer is muted by hiding it.
        new UpdateLayerOperation("music", new JsonObject { ["visible"] = false }).Apply(project);
        Assert.False(Layer<AudioLayer>(project, "music").Visible);

        new UpdateLayerOperation("clip_2", new JsonObject { ["fadeIn"] = 0.25, ["fadeOut"] = 0 }).Apply(project);
        Assert.Equal((0.25, 0.0), (Layer<VideoLayer>(project, "clip_2").FadeIn, Layer<VideoLayer>(project, "clip_2").FadeOut));

        // A caption's text and timing.
        var cue = captions.LayerIds[0];
        new UpdateLayerOperation(cue, new JsonObject { ["text"] = "Hi there", ["start"] = 0.5, ["end"] = 1.75 }).Apply(project);
        var text = Layer<TextLayer>(project, cue);
        Assert.Equal(("Hi there", 0.5, 1.75), (text.Text, text.Start, text.End!.Value));
        Assert.Equal(captions.GroupId, text.ParentId);
    }

    [Fact]
    public void ABatchIsOneStepAndKeepsNothingWhenAPartFails()
    {
        var directory = TestPaths.NewDirectory("edit-batch");
        ProjectSession.Create(directory, TestProjects.Footage());
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        var original = TestProjects.WithoutRevision(File.ReadAllText(file));
        var document = EditorDocument.Open(directory);
        var drag = new BatchOperation("drag clip_2", [new MoveLayerOperation("clip_2", 5), new ReorderLayerOperation("clip_2", 3)]);
        Assert.Equal(["clip_2"], drag.Layers);
        Assert.True(document.Apply(drag));
        Assert.Equal("moved 'clip_2' with 1 child layer(s) by +1s: now 5–9s; 'clip_2' now draws at z=3 with 1 child layer(s)", document.Summary);
        Assert.Equal(1, document.Session.UndoCount);
        var saved = ProjectStore.Load(directory);
        Assert.Equal((5.0, 3), (saved.FindLayer("clip_2")!.Start, Layer<VideoLayer>(saved, "clip_2").ZIndex));
        Assert.Equal("drag clip_2", ProjectBackups.List(directory)[0].Before![(ProjectBackups.EditorProgram.Length + 1)..]);

        var bad = new BatchOperation("drag clip_1", [new MoveLayerOperation("clip_1", 1), new TrimLayerOperation("clip_1", null, 20)]);
        Assert.Equal(["clip_1"], bad.Layers);
        Assert.False(document.Apply(bad));
        Assert.Equal("drag clip_1 wasn't saved: 'clip_1' can end at 11s at the latest: its source has nothing after that", document.Status);
        Assert.Equal(saved.Revision, ProjectStore.Load(directory).Revision);
        Assert.Equal(0, document.View.FindLayer("clip_1")!.Start);

        Assert.Equal("the batch has no operations", Assert.Throws<ZeyoCutException>(() => new BatchOperation("nothing", []).Apply(TestProjects.Footage())).Message);

        Assert.True(document.Undo());
        Assert.Equal(original, TestProjects.WithoutRevision(File.ReadAllText(file)));
    }
}
