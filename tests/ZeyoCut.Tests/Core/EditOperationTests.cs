// SPDX-License-Identifier: MIT

using System.Text.Json;
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

    private static KeyframeEntry Key(string layer, string property, double time, object value, string? easing = null, double[]? bezier = null) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value), easing, bezier);

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
            (new DuplicateLayerOperation("clip_2"), p => ProjectEditor.DuplicateLayer(p, new DuplicateOptions { LayerId = "clip_2" })),
            (new AddMarkersOperation([new MarkerEntry(null, 2, "Marker 1", null)]), p => MarkerEditor.AddMarkers(p, [new MarkerEntry(null, 2, "Marker 1", null)])),
            (new AddMarkersOperation([new MarkerEntry("outro", 6.5, "Outro", "scene"), new MarkerEntry(null, 0, "Intro", null)]),
                p => MarkerEditor.AddMarkers(p, [new MarkerEntry("outro", 6.5, "Outro", "scene"), new MarkerEntry(null, 0, "Intro", null)])),
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
        Assert.Equal(0.3, Layer<AudioLayer>(again, "music").Volume.Value);
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

    // The editor's Add marker button adds a marker as add_markers does, named and labelled the same way.
    [Fact]
    public void AddingMarkersIsAddMarkersInOneStep()
    {
        var project = TestProjects.Footage();
        var add = new AddMarkersOperation([new MarkerEntry(null, 2, "Marker 1", null)]);
        Assert.Equal(("add_markers (1 marker)", 0), (add.Label, add.Layers.Count));
        Assert.Equal("added 1 marker: marker_1", add.Apply(project));
        Assert.Equal([("marker_1", 2.0, "Marker 1", MarkerKind.Chapter)], project.Markers.Select(m => (m.Id, m.Time, m.Name, m.Kind)));

        // A second at the same time is added with the warning add_markers gives.
        var again = new AddMarkersOperation([new MarkerEntry(null, 2, "Marker 2", null), new MarkerEntry("marker_1", 1, "Start", null)]);
        Assert.Equal("add_markers (2 markers)", again.Label);
        var summary = again.Apply(project);
        Assert.StartsWith("added 1 marker: marker_2; changed marker_1", summary);
        Assert.Equal([("marker_1", 1.0), ("marker_2", 2.0)], project.Markers.Select(m => (m.Id, m.Time)));

        // Past the project's end, nothing is added.
        var before = ProjectJson.Serialize(project);
        Assert.Contains("markers go from 0 to the project end", Assert.Throws<ZeyoCutException>(() => new AddMarkersOperation([new MarkerEntry(null, 99, "Late", null)]).Apply(project)).Message);
        Assert.Equal(before, ProjectJson.Serialize(project));
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

        // A still can be given its length (the editor's Default still-image length).
        Assert.Equal("added image layer 'photo_2' at 18s on draw level 12", new AddClipOperation(AssetType.Image, "photo", 18, StillSeconds: 2.5).Apply(project));
        Assert.Equal((18.0, 20.5, 20.5), (Layer<ImageLayer>(project, "photo_2").Start, Layer<ImageLayer>(project, "photo_2").End!.Value, project.Duration));
        foreach (var bad in new[] { 0, -1, double.NaN })
        {
            Assert.Equal("a still's length must be more than 0 seconds", Assert.Throws<ZeyoCutException>(() => new AddClipOperation(AssetType.Image, "photo", 0, StillSeconds: bad).Apply(project)).Message);
        }

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
        Assert.Equal((0.25, 1.5, 1.0), (music.Volume.Value, music.FadeIn, music.FadeOut));

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

    // The Animate view's drags: keyframes to other times, and in its graph to other values, each
    // keeping its easing, as one step.
    [Fact]
    public void MovingKeyframesKeepsTheirValueAndEasingAndIsOneStep()
    {
        var directory = TestPaths.NewDirectory("edit-move-keys");
        var project = TestProjects.Rig();
        KeyframeEditor.SetKeyframes(project,
        [
            Key("tora_head", "y", 0, 100, bezier: [0, 0, 0.4, 1]),
            Key("tora_head", "y", 1, 200, "easeOutBack"),
            Key("tora_head", "y", 2, 150),
            Key("tora_head", "drawing", 0.5, "shocked"),
            Key("camera", "zoom", 0, 1, "easeIn"),
            Key("camera", "zoom", 2, 2),
        ]);
        ProjectSession.Create(directory, project);
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        var original = TestProjects.WithoutRevision(File.ReadAllText(file));
        var document = EditorDocument.Open(directory);

        var retime = new MoveKeyframesOperation([new KeyframeMove("tora_head", "y", 0, 0.4), new KeyframeMove("tora_head", "y", 1, 1.2), new KeyframeMove("tora_head", "drawing", 0.5, 0.9)]);
        Assert.Equal("move_keyframes tora_head", retime.Label);
        Assert.Equal(["tora_head"], retime.Layers);
        Assert.True(document.Apply(retime), document.Status);
        Assert.Equal("moved 3 keyframes on tora_head.y, tora_head.drawing", document.Summary);
        Assert.Equal(1, document.Session.UndoCount);
        var head = Layer<ImageLayer>(ProjectStore.Load(directory), "tora_head");
        Assert.Equal([(0.4, 100.0, Easing.CubicBezier), (1.2, 200.0, Easing.EaseOutBack), (2.0, 150.0, Easing.Linear)], head.Y.Keyframes.Select(k => (k.Time, k.Value, k.Easing)));
        Assert.Equal([0, 0, 0.4, 1], head.Y.Keyframes[0].Bezier!);
        Assert.Equal((0.9, "shocked"), (head.Drawing.Keyframes.Single().Time, head.Drawing.Keyframes.Single().Value));

        // In the graph a drag changes the value too; past its neighbour a key keeps its own easing.
        Assert.True(document.Apply(new MoveKeyframesOperation([new KeyframeMove("tora_head", "y", 1.2, 2.5, 260)])), document.Status);
        Assert.Equal("moved tora_head.y's keyframe from 1.2s to 2.5s, value 260", document.Summary);
        head = Layer<ImageLayer>(ProjectStore.Load(directory), "tora_head");
        Assert.Equal([(0.4, 100.0, Easing.CubicBezier), (2.0, 150.0, Easing.Linear), (2.5, 260.0, Easing.EaseOutBack)], head.Y.Keyframes.Select(k => (k.Time, k.Value, k.Easing)));

        // A keyframe can't land on another, and then nothing changes.
        Assert.False(document.Apply(new MoveKeyframesOperation([new KeyframeMove("tora_head", "y", 0.4, 0.6), new KeyframeMove("camera", "zoom", 0, 2)])));
        Assert.Equal("move_keyframes tora_head camera wasn't saved: camera.zoom has a keyframe at 2s already, and a keyframe can't land on another", document.Status);
        Assert.Equal(0.4, Layer<ImageLayer>(ProjectStore.Load(directory), "tora_head").Y.Keyframes[0].Time);

        // Each drag is one step back.
        Assert.True(document.Undo());
        Assert.Equal(1.2, Layer<ImageLayer>(ProjectStore.Load(directory), "tora_head").Y.Keyframes[1].Time);
        Assert.True(document.Undo());
        Assert.Equal(original, TestProjects.WithoutRevision(File.ReadAllText(file)));
    }

    [Fact]
    public void AMoveNeedsTheKeyframeAndATimeAndKeepsADrawingsName()
    {
        var project = TestProjects.Rig();
        KeyframeEditor.SetKeyframes(project, [Key("tora_head", "y", 0, 100), Key("tora_head", "y", 1, 200), Key("tora_head", "drawing", 0.5, "shocked")]);
        string Refused(params KeyframeMove[] moves) => Assert.Throws<ZeyoCutException>(() => KeyframeEditor.MoveKeyframes(project, moves)).Message;

        Assert.Equal("no keyframe at 3s on tora_head.y; keyframe times: 0, 1", Refused(new KeyframeMove("tora_head", "y", 3, 2)));
        Assert.Equal("time must be >= 0 (got -0.5)", Refused(new KeyframeMove("tora_head", "y", 0, -0.5)));
        Assert.Equal("tora_head.drawing keyframes are drawing names, which a move keeps; set_keyframes changes one", Refused(new KeyframeMove("tora_head", "drawing", 0.5, 1, 2)));
        Assert.Equal("tora_head.y has a keyframe at 1s already, and a keyframe can't land on another", Refused(new KeyframeMove("tora_head", "y", 0, 1)));
        Assert.Equal("""
            2 problems, so nothing was changed:
            - entry 1 (tora_head.wobble @ 0s): unknown property 'wobble'; animatable properties: x, y, rotation, scaleX, scaleY, opacity; brightness, contrast, saturation (image and video layers); cropX, cropY (video layers); drawing (image layers); grade.exposure, grade.highlights, grade.shadows, grade.temperature, grade.tint (image and video layers with a grade, see set_color), grade.lift.red, .green, .blue and the same for grade.gamma and grade.gain (once set_color sets that wheel), grade.look.amount (once it has a look); volume (audio layers and video layers' own sound, 1 as recorded, 0 silent, up to 10); shadow.x, shadow.y, shadow.blur, shadow.opacity, outline.width, blur.x, blur.y, tint.amount (layers with that effect, see set_effect); mask.x, mask.y, mask.width, mask.height, mask.feather (layers with a mask, see set_mask)
            - entry 2 (tora_head.y @ 0s): the keyframe at 0s on tora_head.y is moved twice
            """, Refused(new KeyframeMove("tora_head", "y", 0, 0.5), new KeyframeMove("tora_head", "wobble", 0, 1), new KeyframeMove("tora_head", "y", 0, 0.7)));
        Assert.Equal([0.0, 1.0], ((ImageLayer)project.FindLayer("tora_head")!).Y.Keyframes.Select(k => k.Time));

        // Two keyframes can trade places.
        Assert.Equal("moved 2 keyframes on tora_head.y", KeyframeEditor.MoveKeyframes(project, [new KeyframeMove("tora_head", "y", 0, 1), new KeyframeMove("tora_head", "y", 1, 0)]));
        Assert.Equal([(0.0, 200.0), (1.0, 100.0)], ((ImageLayer)project.FindLayer("tora_head")!).Y.Keyframes.Select(k => (k.Time, k.Value)));
    }

    // The Animate view's easing tiles, Linear, Smooth and Hold, and its custom bezier: the keyframes
    // eased as set_keyframes eases them when it replaces them.
    [Fact]
    public void SettingAnEasingMakesTheProjectSetKeyframesMakes()
    {
        static Project Animated()
        {
            var project = TestProjects.Rig();
            KeyframeEditor.SetKeyframes(project,
            [
                Key("tora_head", "y", 0, 100, "easeIn"),
                Key("tora_head", "y", 1, 200),
                Key("tora_head", "rotation", 0.5, 10, bezier: [0.1, 0.2, 0.3, 0.4]),
                Key("tora_head", "rotation", 1.5, -10),
            ]);
            return project;
        }

        foreach (var easing in Enum.GetValues<Easing>())
        {
            double[]? bezier = easing == Easing.CubicBezier ? [0.25, 0.1, 0.25, 1] : null;
            var name = EnumNames.ToName(easing);
            var edited = Animated();
            var operation = new SetEasingOperation([new KeyframeRef("tora_head", "y", 0), new KeyframeRef("tora_head", "rotation", 0.5)], easing, bezier);
            Assert.Equal("set_keyframes tora_head", operation.Label);
            Assert.Equal($"set 2 keyframe(s) on tora_head.y, tora_head.rotation to {name}{(bezier is null ? "" : " [0.25, 0.1, 0.25, 1]")}", operation.Apply(edited));

            var expected = Animated();
            KeyframeEditor.SetKeyframes(expected, [Key("tora_head", "y", 0, 100, name, bezier), Key("tora_head", "rotation", 0.5, 10, name, bezier)]);
            Assert.Equal(ProjectJson.Serialize(expected), ProjectJson.Serialize(edited));
        }

        var project = Animated();
        KeyframeEditor.SetKeyframes(project, [Key("tora_head", "drawing", 0.5, "shocked")]);
        string Refused(Easing easing, double[]? bezier, params KeyframeRef[] refs) =>
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetEasing(project, refs, easing, bezier)).Message;
        Assert.Equal("bezier must be 4 numbers, [x1, y1, x2, y2] (got none)", Refused(Easing.CubicBezier, null, new KeyframeRef("tora_head", "y", 0)));
        Assert.Equal("bezier goes with easing cubicBezier, not easeIn", Refused(Easing.EaseIn, [0, 0, 1, 1], new KeyframeRef("tora_head", "y", 0)));
        Assert.Equal("tora_head.drawing keyframes always hold", Refused(Easing.Linear, null, new KeyframeRef("tora_head", "drawing", 0.5)));
        Assert.Equal("""
            2 problems, so nothing was changed:
            - entry 0 (tora_head.y @ 0.5s): no keyframe at 0.5s on tora_head.y; keyframe times: 0, 1
            - entry 2 (tora_head.drawing @ 0.5s): tora_head.drawing keyframes always hold
            """, Refused(Easing.Hold, null, new KeyframeRef("tora_head", "y", 0.5), new KeyframeRef("tora_head", "y", 1), new KeyframeRef("tora_head", "drawing", 0.5)));
        Assert.Equal(Easing.Linear, ((ImageLayer)project.FindLayer("tora_head")!).Y.Keyframes[1].Easing);
    }

    // Ctrl+D: duplicate_layer with its defaults, the layer with its children.
    [Fact]
    public void DuplicatingCopiesTheLayerWithItsChildren()
    {
        var project = TestProjects.Footage();
        var duplicate = new DuplicateLayerOperation("clip_2");
        Assert.Equal("duplicate_layer clip_2", duplicate.Label);
        Assert.Equal(["clip_2"], duplicate.Layers);
        Assert.Equal("copied 'clip_2' -> 'clip_2_2', 'label' -> 'label_2'", duplicate.Apply(project));
        var copy = Layer<VideoLayer>(project, "clip_2_2");
        Assert.Equal((4.0, 8.0, 4.0, 8.0), (copy.Start, copy.TimelineEnd, copy.TrimIn, copy.TrimOut));
        Assert.Equal("clip_2_2", project.FindLayer("label_2")!.ParentId);
        Assert.Equal("copied 'clip_2' -> 'clip_2_3', 'label' -> 'label_3'", duplicate.Apply(project));
        Assert.StartsWith("layer 'nothing' not found", Assert.Throws<ZeyoCutException>(() => new DuplicateLayerOperation("nothing").Apply(project)).Message);
    }

    // relink_asset and the editor's Relink: the asset keeps its id and takes the new file's details.
    [Fact]
    public void RelinkingKeepsTheIdAndTakesTheNewFilesDetails()
    {
        var project = TestProjects.Footage();
        var clip = project.FindAsset("clip")!;
        clip.OriginalFile = @"D:\phone\clip.mov";
        clip.Normalized = "variable frame rate";
        var bigger = new PreparedAsset(@"D:\moved\clip.mp4", @"C:\p\assets\clip_2.mp4", AssetType.Video,
            new AssetInfo(Width: 1280, Height: 720, Duration: 10, FrameRate: "30", FrameCount: 300, HasAudio: false, Codec: "hevc"));
        var relink = new RelinkAssetOperation("clip", bigger);
        Assert.Equal("relink_asset clip", relink.Label);
        Assert.Empty(relink.Layers);
        var summary = relink.Apply(project);
        Assert.Equal(AssetRelinker.Relink(TestProjects.Footage(), "clip", bigger).Summary, summary);
        Assert.Equal("relinked 'clip' to assets/clip_2.mp4; 'clip' was 640x360, 10s and is now 1280x720, 10s", summary);
        var relinked = project.FindAsset("clip")!;
        Assert.Equal(("assets/clip_2.mp4", 1280, 720, false, "hevc", null, null),
            (relinked.File, relinked.Width, relinked.Height, relinked.HasAudio, relinked.Codec, relinked.OriginalFile, relinked.Normalized));
        Assert.Equal(["clip_1", "clip_2"], ProjectChecker.Users(project, "clip"));

        var sound = new PreparedAsset(@"D:\moved\music.wav", @"C:\p\assets\music.wav", AssetType.Audio, new AssetInfo(Duration: 20.01));
        Assert.Equal("relinked 'music' to assets/music.wav", new RelinkAssetOperation("music", sound).Apply(project));
        Assert.Equal(20.01, project.FindAsset("music")!.Duration);

        Assert.Equal("'music.wav' is an audio file, and asset 'clip' needs a video",
            Assert.Throws<ZeyoCutException>(() => new RelinkAssetOperation("clip", sound).Apply(project)).Message);
        Assert.Equal("'notes.txt' isn't a file ZeyoCut imports, and asset 'music' needs an audio file",
            Assert.Throws<ZeyoCutException>(() => AssetRelinker.Check(project, "music", @"D:\notes.txt")).Message);
        Assert.Equal(AssetType.Audio, AssetRelinker.Check(project, "music", @"D:\other\Music.MP3").Type);
        Assert.StartsWith("asset 'logo' not found; assets: clip, music", Assert.Throws<ZeyoCutException>(() => AssetRelinker.Check(project, "logo", @"D:\logo.png")).Message);
    }

    // Relink's likely matches: the same name and kind in the folders or below them, and the same
    // size, length (within a frame) or font family.
    [Fact]
    public void ALikelyMatchHasTheSameNameKindAndSizeOrLength()
    {
        var root = TestPaths.NewDirectory("relink-matches");
        string File(string relative, double length)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, [0]);
            Lengths[path] = length;
            return path;
        }

        var near = File(@"sounds\music.wav", 20.02);
        File(@"old\music.wav", 19);
        File(@"music.mp3", 20);
        var deep = File(@"a\b\c\MUSIC.WAV", 20);
        File(@"a\b\c\d\e\f\music.wav", 20);
        var other = TestPaths.NewDirectory("relink-matches-picked");
        var picked = Path.Combine(other, "music.wav");
        System.IO.File.WriteAllBytes(picked, [0]);
        Lengths[picked] = 20;

        var project = TestProjects.Footage();
        var music = project.FindAsset("music")!;
        var matches = AssetRelinker.Matches(project, music, [root, Path.Combine(root, "missing folder"), other, root], new LengthProbe(), TestContext.Current.CancellationToken);
        Assert.Equal([(deep, "same length 20s"), (near, "same length 20s"), (picked, "same length 20s")],
            matches.Select(m => (m.Path, m.Reason)).OrderBy(m => m.Path == picked).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase));

        // An asset with no length on record matches by name and kind alone.
        music.Duration = null;
        Assert.Equal(4, AssetRelinker.Matches(project, music, [root, other], new LengthProbe(), TestContext.Current.CancellationToken).Count(m => m.Reason == "same name"));

        // A file the probe can't read is passed over; cancelling stops the search.
        Lengths.Remove(near);
        music.Duration = 20;
        Assert.DoesNotContain(near, AssetRelinker.Matches(project, music, [root], new LengthProbe(), TestContext.Current.CancellationToken).Select(m => m.Path));
        Assert.Throws<OperationCanceledException>(() => AssetRelinker.Matches(project, music, [root], new LengthProbe(), new CancellationToken(true)));
    }

    private static readonly Dictionary<string, double> Lengths = new(StringComparer.OrdinalIgnoreCase);

    private sealed class LengthProbe : IAssetProbe
    {
        public AssetInfo Probe(string path, AssetType type) =>
            Lengths.TryGetValue(path, out var length) ? new AssetInfo(Duration: length) : throw new ZeyoCutException($"can't read {path}");
    }
}
