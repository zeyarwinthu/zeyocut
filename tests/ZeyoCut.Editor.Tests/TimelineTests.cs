// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The timeline: lanes from the layers, times across it, and what a drag does.
public class TimelineTests
{
    // Two clips of one file (clip_2 with a label attached), music, and a title.
    private static Project Footage()
    {
        var project = TestFiles.Project(640, 360, duration: 8);
        project.Assets.Add(new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 640, Height = 360, FrameRate = "30", FrameCount = 300, Duration = 10, HasAudio = true, Codec = "h264" });
        project.Assets.Add(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.wav", Duration = 20 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip_1", Start = 0, TrimIn = 0, TrimOut = 4 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip_2", Start = 4, TrimIn = 4, TrimOut = 8 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "label", Text = "Second", ParentId = "clip_2", Start = 4, End = 8, ZIndex = 5 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "music", End = 8 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 0, End = 3, ZIndex = 10 });
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 2, "Hello"), new CueEntry(2, 4, "World")], project.Duration).Cues, new CaptionOptions(), [], null);
        return project;
    }

    private static TimelineClip Clip(IEnumerable<TimelineLane> lanes, string id) => lanes.SelectMany(l => l.Clips).Single(c => c.LayerId == id);

    private static void AssertPlace(double start, double end, double? snapped, DragPlace place)
    {
        Assert.Equal(start, place.Start, 9);
        Assert.Equal(end, place.End, 9);
        Assert.Equal(snapped is null, place.Snapped is null);
        if (snapped is { } at)
        {
            Assert.Equal(at, place.Snapped!.Value, 9);
        }
    }

    [Fact]
    public void LanesFollowDrawLevelsCaptionGroupsAndSoundRoles()
    {
        var project = Footage();
        var lanes = TimelineLayout.Lanes(project);
        Assert.Equal(["Captions · EN", "Text", "Video 2", "Video 1", "Music"], lanes.Select(l => l.Title));
        Assert.Equal([LaneKind.Captions, LaneKind.Visual, LaneKind.Visual, LaneKind.Visual, LaneKind.Audio], lanes.Select(l => l.Kind));
        Assert.Equal([20, 10, 1, 0, 0], lanes.Select(l => l.ZIndex));

        // The captions are the cues; a layer with children is one clip.
        Assert.Equal(["Hello", "World"], lanes[0].Clips.Select(c => c.Label));
        Assert.Equal((0.0, 2.0, ClipKind.Caption), (lanes[0].Clips[0].Start, lanes[0].Clips[0].End, lanes[0].Clips[0].Kind));
        var second = Clip(lanes, "clip_2");
        Assert.Equal((ClipKind.Video, 4.0, 8.0, 1, "clip"), (second.Kind, second.Start, second.End, second.Children, second.Detail));
        Assert.DoesNotContain(lanes.SelectMany(l => l.Clips), c => c.LayerId == "label");
        Assert.Equal((ClipKind.Audio, 0.0, 8.0), (Clip(lanes, "music").Kind, Clip(lanes, "music").Start, Clip(lanes, "music").End));

        // Every clip's start and end, once each.
        Assert.Equal([0.0, 2, 3, 4, 8], TimelineLayout.EditPoints(lanes));
    }

    // A level is named for what it holds; levels of one name are numbered from the bottom, as they draw.
    [Fact]
    public void LanesAreNamedByWhatTheyHoldAndNumberedFromTheBottom()
    {
        var project = Footage();
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 8, Height = 8 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Start = 0, End = 2, ZIndex = 4 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo_2", Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Start = 3, End = 4, ZIndex = 1 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "credits", Text = "Bye", Start = 6, End = 8, ZIndex = 7 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "beep", Role = "effects", Start = 1, End = 2 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "narration", Role = "voice", Start = 0, End = 2 });

        var lanes = TimelineLayout.Lanes(project);
        Assert.Equal(["Captions · EN", "Text 2", "Text 1", "Images", "Mixed", "Video", "Voice", "Music", "Sound effects"], lanes.Select(l => l.Title));
        Assert.Equal([20, 10, 7, 4, 1, 0, 0, 0, 0], lanes.Select(l => l.ZIndex));
    }

    [Fact]
    public void ClipsThatOverlapInALaneStackInRows()
    {
        var project = Footage();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "late", Text = "Late", Start = 2, End = 5, ZIndex = 10 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "after", Text = "After", Start = 3, End = 6, ZIndex = 10 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "voice_1", Role = "voice", Start = 1, End = 3 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "voice_2", Role = "voice", Start = 2, End = 4 });
        var lanes = TimelineLayout.Lanes(project);

        var text = lanes.Single(l => l.Title == "Text");
        Assert.Equal(2, text.Rows);
        Assert.Equal([("title", 0), ("late", 1), ("after", 0)], text.Clips.Select(c => (c.LayerId, c.Row)));

        // Voice comes before music, each role packed into the rows it needs.
        Assert.Equal(["Voice", "Music"], lanes.Where(l => l.Kind == LaneKind.Audio).Select(l => l.Title));
        Assert.Equal(2, lanes.Single(l => l.Title == "Voice").Rows);
    }

    [Fact]
    public void ClipsShowWhatFollowsThemAndWhatIsOff()
    {
        var project = Footage();
        project.Layers.Add(new CutoutLayer { Id = "subject", Sources = ["clip_2"], ZIndex = 30 });
        project.Tracks.Add(new Track { Id = "face_1", Asset = "clip" });
        project.Layers.Add(new RedactionLayer { Id = "faces", Tracks = ["face_1"] });
        ProjectEditor.UpdateLayer(project, "music", new System.Text.Json.Nodes.JsonObject { ["visible"] = false });
        ProjectEditor.UpdateLayer(project, "clip_1", new System.Text.Json.Nodes.JsonObject { ["muted"] = true });
        var lanes = TimelineLayout.Lanes(project);

        // Cutouts and redactions are marks on the clips they follow, not lanes of their own.
        Assert.Equal(5, lanes.Count);
        Assert.Equal(["redaction 'faces'"], Clip(lanes, "clip_1").Marks);
        Assert.Equal(["cutout 'subject'", "redaction 'faces'"], Clip(lanes, "clip_2").Marks);
        Assert.True(Clip(lanes, "music").Hidden);
        Assert.True(Clip(lanes, "clip_1").SoundOff);
        Assert.False(Clip(lanes, "clip_2").SoundOff);
    }

    [Fact]
    public void TheScaleTurnsTimesIntoPixelsAndBack()
    {
        var scale = new TimelineScale(100, 50);
        Assert.Equal(250, scale.XOf(3));
        Assert.Equal(3, scale.TimeAt(250));

        // Zooming keeps the time under the pointer where it is.
        var zoomed = scale.ZoomedAt(400, 250);
        Assert.Equal(400, zoomed.PixelsPerSecond);
        Assert.Equal(250, zoomed.XOf(3), 9);

        // Frame f covers f/fps to (f+1)/fps.
        Assert.Equal(0, TimelineScale.FrameAt(0, 30, 180));
        Assert.Equal(29, TimelineScale.FrameAt(0.999, 30, 180));
        Assert.Equal(30, TimelineScale.FrameAt(1, 30, 180));
        Assert.Equal(0, TimelineScale.FrameAt(-2, 30, 180));
        Assert.Equal(179, TimelineScale.FrameAt(100, 30, 180));
        Assert.Equal(0, TimelineScale.FrameAt(1, 30, 0));
        Assert.All(Enumerable.Range(0, 180), f => Assert.Equal(f, TimelineScale.FrameAt(f / 30.0, 30, 180)));
    }

    [Fact]
    public void ADragHeldAtAnEdgeScrollsFasterTheFurtherIn()
    {
        // Lanes from x 200 to 1000, with a 24 px band at each edge.
        Assert.Equal(0, TimelineScale.EdgeScroll(600, 200, 1000));
        Assert.Equal(0, TimelineScale.EdgeScroll(224, 200, 1000));
        Assert.Equal(-6, TimelineScale.EdgeScroll(212, 200, 1000));
        Assert.Equal(6, TimelineScale.EdgeScroll(988, 200, 1000));
        Assert.Equal(24, TimelineScale.EdgeScroll(1100, 200, 1000));
        Assert.Equal(-24, TimelineScale.EdgeScroll(0, 200, 1000));
    }

    [Theory]
    [InlineData(230, 0.5)]
    [InlineData(640, 0.1)]
    [InlineData(10, 10)]
    [InlineData(0.001, 3600)]
    public void RulerLabelsLeaveRoomForTheirText(double pixelsPerSecond, double step) => Assert.Equal(step, TimelineScale.LabelStep(pixelsPerSecond));

    [Fact]
    public void AMovedClipLandsOnAFrameOrSnapsByTheNearerEdge()
    {
        var project = Footage();
        var lanes = TimelineLayout.Lanes(project);
        var clip = Clip(lanes, "clip_2");
        var limits = TimelineEditor.Limits(project, "clip_2");

        // The other clips' edges, 0, the playhead and the markers.
        var snaps = TimelineEdits.SnapPoints(lanes, "clip_2", 2.5, [6.2]);
        Assert.Equal([0, 2, 2.5, 3, 4, 6.2, 8], snaps);

        AssertPlace(4.5, 8.5, null, TimelineEdits.Place(clip, DragPart.Body, 0.511, limits, 30, [], 0));
        AssertPlace(2.2, 6.2, 6.2, TimelineEdits.Place(clip, DragPart.Body, -1.75, limits, 30, snaps, 0.1));
        AssertPlace(2.5, 6.5, 2.5, TimelineEdits.Place(clip, DragPart.Body, -1.45, limits, 30, snaps, 0.1));

        // Never before the earliest a move allows: here, 0.
        AssertPlace(0, 4, null, TimelineEdits.Place(clip, DragPart.Body, -10, limits, 30, snaps, 0.1));
        Assert.Equal(new MoveLayerOperation("clip_2", 4.5), TimelineEdits.Operation(clip, DragPart.Body, new DragPlace(4.5, 8.5, null), null));
        Assert.Null(TimelineEdits.Operation(clip, DragPart.Body, new DragPlace(4, 8, null), null));
    }

    [Fact]
    public void ATrimKeepsTheOtherEdgeAndStaysInsideTheSource()
    {
        var project = Footage();
        var lanes = TimelineLayout.Lanes(project);
        var clip = Clip(lanes, "clip_2");
        var limits = TimelineEditor.Limits(project, "clip_2");
        var snaps = TimelineEdits.SnapPoints(lanes, "clip_2", 2.5, []);

        AssertPlace(2.7, 8, null, TimelineEdits.Place(clip, DragPart.Start, -1.3, limits, 30, snaps, 0.1));
        AssertPlace(2.5, 8, 2.5, TimelineEdits.Place(clip, DragPart.Start, -1.45, limits, 30, snaps, 0.1));
        AssertPlace(0, 8, null, TimelineEdits.Place(clip, DragPart.Start, -5, limits, 30, snaps, 0.1));

        // At least a frame is left, and the end goes no further than the file: clip_2 can end at 10 s.
        AssertPlace(8 - 1 / 30.0, 8, null, TimelineEdits.Place(clip, DragPart.Start, 10, limits, 30, snaps, 0.1));
        AssertPlace(4, 10, null, TimelineEdits.Place(clip, DragPart.End, 3, limits, 30, snaps, 0.1));
        AssertPlace(4, 4 + 1 / 30.0, null, TimelineEdits.Place(clip, DragPart.End, -10, limits, 30, snaps, 0.1));

        Assert.Equal(new TrimLayerOperation("clip_2", 2.5, null), TimelineEdits.Operation(clip, DragPart.Start, new DragPlace(2.5, 8, 2.5), null));
        Assert.Equal(new TrimLayerOperation("clip_2", null, 10), TimelineEdits.Operation(clip, DragPart.End, new DragPlace(4, 10, null), null));
        Assert.Null(TimelineEdits.Operation(clip, DragPart.End, new DragPlace(4, 8, null), null));
    }

    [Fact]
    public void DraggingUpOrDownChangesTheDrawLevel()
    {
        var lanes = TimelineLayout.Lanes(Footage());
        var visual = lanes.Where(l => l.Kind != LaneKind.Audio).ToList();
        var first = visual[3];
        var title = visual[1];

        // Onto another level, not a caption lane or its own.
        Assert.Equal(new LaneDrop(1, false), TimelineEdits.Drop(visual, first, 2, null));
        Assert.Null(TimelineEdits.Drop(visual, first, 0, null));
        Assert.Null(TimelineEdits.Drop(visual, first, 3, null));

        // Between two levels: a level of its own, just above the lower one. Next to a lane it has to
        // itself, nothing changes.
        Assert.Equal(new LaneDrop(2, true), TimelineEdits.Drop(visual, first, null, 2));
        Assert.Equal(new LaneDrop(21, true), TimelineEdits.Drop(visual, first, null, 0));
        Assert.Null(TimelineEdits.Drop(visual, first, null, 3));
        Assert.Null(TimelineEdits.Drop(visual, first, null, 4));
        Assert.Equal(new LaneDrop(0, true), TimelineEdits.Drop(visual, title, null, 4));

        // A move and a new level are one edit.
        var clip = first.Clips[0];
        var drag = Assert.IsType<BatchOperation>(TimelineEdits.Operation(clip, DragPart.Body, new DragPlace(1, 5, null), new LaneDrop(2, true)));
        Assert.Equal("drag clip_1", drag.Name);
        Assert.Equal([new MoveLayerOperation("clip_1", 1), new ReorderLayerOperation("clip_1", 2, true)], drag.Operations);
        Assert.Equal(new ReorderLayerOperation("clip_1", 1, false), TimelineEdits.Operation(clip, DragPart.Body, new DragPlace(0, 4, null), new LaneDrop(1, false)));
    }

    [Fact]
    public void ADragIsSavedAsOneEditAndTheLanesFollow()
    {
        var folder = TestFiles.NewProject("timeline-drag", Footage());
        var document = EditorDocument.Open(folder);
        var clip = Clip(TimelineLayout.Lanes(document.View), "clip_1");

        Assert.True(document.Apply(TimelineEdits.Operation(clip, DragPart.Body, new DragPlace(1, 5, null), new LaneDrop(2, true))!));
        var lanes = TimelineLayout.Lanes(document.View);
        Assert.Equal(["Captions · EN", "Text", "Video 2", "Video 1", "Music"], lanes.Select(l => l.Title));
        Assert.Equal((1.0, 5.0), (Clip(lanes, "clip_1").Start, Clip(lanes, "clip_1").End));
        Assert.Equal(1, document.Session.UndoCount);

        // A trim from the timeline, then undo puts it back.
        Assert.True(document.Apply(TimelineEdits.Operation(Clip(lanes, "clip_2"), DragPart.End, new DragPlace(4, 10, null), null)!));
        Assert.Equal(10, Clip(TimelineLayout.Lanes(document.View), "clip_2").End);
        Assert.True(document.Undo());
        Assert.Equal(8, Clip(TimelineLayout.Lanes(document.View), "clip_2").End);
    }
}
