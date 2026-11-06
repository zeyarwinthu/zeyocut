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

public class RedactionTests
{
    private static readonly Asset Square = new() { Id = "square", Type = AssetType.Video, File = "assets/square.mp4", Width = 320, Height = 180, FrameRate = "30", FrameCount = 90, Duration = 3 };

    // A 40x40 box moving 3 px right and 1 px down a frame, as in TestVideos.MovingSquare, found on
    // 'frames' (the rest missing, or lost when 'lost' says so).
    private static List<TrackedFrame> Moving(IEnumerable<int> frames, IEnumerable<int>? lost = null)
    {
        var gone = (lost ?? []).ToHashSet();
        return frames.Select(f =>
        {
            var (x, y, size) = TestVideos.SquareAt(f);
            return new TrackedFrame { Frame = f, X = x, Y = y, Width = size, Height = size, Confidence = gone.Contains(f) ? 0 : 1, Lost = gone.Contains(f) };
        }).ToList();
    }

    private static bool Contains(CoverBox box, double x, double y, double width, double height) =>
        box.X <= x + 1e-9 && box.Y <= y + 1e-9 && box.X + box.Width >= x + width - 1e-9 && box.Y + box.Height >= y + height - 1e-9;

    // The spec's test: with detection off for 5 frames (missing, or lost by the tracker), every frame
    // from the first detection to the last has a box over the object.
    [Fact]
    public void WithDetectionOffForFiveFramesNoFrameIsUncovered()
    {
        var missing = RedactionCover.Of(Moving(Enumerable.Range(0, 60).Where(f => f is < 20 or > 24)), 90, 15, 15);
        var lost = RedactionCover.Of(Moving(Enumerable.Range(0, 60), lost: Enumerable.Range(20, 5)), 90, 15, 15);
        foreach (var cover in new[] { missing, lost })
        {
            Assert.Equal((0, 74), (cover.First, cover.Last));
            for (var frame = 0; frame < 60; frame++)
            {
                var (x, y, size) = TestVideos.SquareAt(frame);
                var box = Assert.NotNull(cover.At(frame));
                Assert.True(Contains(box, x, y, size, size), $"frame {frame}: {box} doesn't cover the square at {x},{y}");
                Assert.Equal(frame is >= 20 and <= 24 ? CoverKind.Gap : CoverKind.Detected, box.Kind);
            }

            // The gap's box grows toward its middle, where it is farthest from a detection.
            Assert.True(cover.At(22)!.Value.Width > cover.At(20)!.Value.Width && cover.At(22)!.Value.Width > cover.At(16)!.Value.Width + 2);
            Assert.All(Enumerable.Range(60, 15), f => Assert.Equal(CoverKind.Held, cover.At(f)!.Value.Kind));
            Assert.Null(cover.At(75));
        }

        Assert.Equal(missing.Boxes, lost.Boxes);
    }

    [Fact]
    public void BoxesAreHeldAndGrowForHoldFramesPastEachEnd()
    {
        var one = new List<TrackedFrame> { new() { Frame = 30, X = 100, Y = 50, Width = 40, Height = 20, Confidence = 1 } };
        var cover = RedactionCover.Of(one, 90, 0, 15);
        Assert.Equal((15, 45), (cover.First, cover.Last));
        Assert.Null(cover.At(14));
        Assert.Null(cover.At(46));

        // Each frame away grows every side by 3% of the mean side (30 px): 0.9 px, so the last held
        // frame is 27 px wider and taller.
        Assert.All(cover.Boxes, b => Assert.Equal(b.Frame == 30 ? CoverKind.Detected : CoverKind.Held, b.Kind));
        Assert.True(Contains(cover.At(30)!.Value, 100, 50, 40, 20));
        var end = cover.At(45)!.Value;
        Assert.Equal((40 + 27.0, 20 + 27.0), (end.Width, end.Height));
        Assert.Equal(end.Width, cover.At(15)!.Value.Width, 6);
        Assert.True(cover.At(40)!.Value.Width < end.Width);

        // Padding: 15% of the width and height on each side. The footage's ends limit the hold.
        var padded = RedactionCover.Of(one, 90, 15, 0);
        Assert.Equal([new CoverBox(30, 94, 47, 52, 26, CoverKind.Detected)], padded.Boxes);
        Assert.Equal((15, 35), (RedactionCover.Of(one, 36, 15, 15).First, RedactionCover.Of(one, 36, 15, 15).Last));
        Assert.Empty(RedactionCover.Of(Moving([3], lost: [3]), 90, 15, 15).Boxes);
    }

    // A scene cut ends a hold one frame past the cut, on either side, in case the cut was found a frame
    // early or late. Between two detections the box still crosses a cut.
    [Fact]
    public void AHoldEndsAtASceneCut()
    {
        var one = new List<TrackedFrame> { new() { Frame = 30, X = 100, Y = 50, Width = 40, Height = 20, Confidence = 1 } };
        var cover = RedactionCover.Of(one, 90, 0, 15, [10, 25, 36, 40]);
        Assert.Equal((24, 36), (cover.First, cover.Last));
        Assert.Equal(((int?)25, (int?)36), (cover.CutBefore, cover.CutAfter));
        Assert.Equal(CoverKind.Held, cover.At(36)!.Value.Kind);
        Assert.Null(cover.At(37));

        // Cuts beyond the hold leave it as it was. A detection on a cut's own frame is held one frame
        // back into the shot before.
        var far = RedactionCover.Of(one, 90, 0, 15, [5, 60]);
        Assert.Equal((15, 45, (int?)null, (int?)null), (far.First, far.Last, far.CutBefore, far.CutAfter));
        Assert.Equal(RedactionCover.Of(one, 90, 0, 15).Boxes, far.Boxes);
        Assert.Equal(29, RedactionCover.Of(one, 90, 0, 15, [30]).First);

        var between = RedactionCover.Of(Moving([20, 30]), 90, 0, 15, [25]);
        Assert.Equal((5, 45), (between.First, between.Last));
        Assert.All(Enumerable.Range(20, 11), f => Assert.NotNull(between.At(f)));
    }

    // analyze_video keeps 4 decimals of a cut's time, so 1.2333 is frame 37 at 30 fps, not 36.
    [Fact]
    public void CutsAreTheFramesWhereAnalyzeVideoFoundAShotStart()
    {
        var directory = TestPaths.NewDirectory("redaction-cuts");
        Assert.Empty(RedactionCover.Cuts(directory, Square));
        var path = AnalysisFiles.AnalysisPath(directory, Square);
        AnalysisFiles.Save(path, new MediaAnalysis { Asset = "square", Scenes = [new SceneCut(1.2333, 40), new SceneCut(1, 30), new SceneCut(1.0001, 12), new SceneCut(0, 50)] });
        Assert.Equal([30, 37], RedactionCover.Cuts(directory, Square));

        // A file that can't be read leaves every hold its full length.
        File.WriteAllText(path, "{ not json");
        Assert.Empty(RedactionCover.Cuts(directory, Square));
    }

    [Fact]
    public void SmoothingNeverLeavesTheFramesOwnBoxUncovered()
    {
        // A box that jitters by up to 6 px.
        var frames = Enumerable.Range(0, 40).Select(f => new TrackedFrame
        {
            Frame = f,
            X = 100 + 2 * f + (f * 37 % 7 - 3) * 2,
            Y = 50 + (f * 53 % 5 - 2) * 2,
            Width = 40 + f * 31 % 3,
            Height = 40,
            Confidence = 1,
        }).ToList();
        var cover = RedactionCover.Of(frames, 40, 10, 0);
        double rawMoves = 0, smoothMoves = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var box = cover.At(i)!.Value;
            Assert.True(Contains(box, f.X - 0.1 * f.Width, f.Y - 0.1 * f.Height, 1.2 * f.Width, 1.2 * f.Height), $"frame {i}");
            if (i > 0)
            {
                rawMoves += Math.Abs(f.X - frames[i - 1].X);
                smoothMoves += Math.Abs(box.X - cover.At(i - 1)!.Value.X);
            }
        }

        Assert.True(smoothMoves < rawMoves * 0.7, $"the left edge moved {smoothMoves:0.#} px in all, the track's {rawMoves:0.#}");
    }

    // The spec's test: the pixelate block scales with the box, so a small box gets as many blocks
    // across as a big one (20 at strength 0 down to 4 at 100), never under 4 source pixels.
    [Fact]
    public void ThePixelateBlockIsProportionalToTheBox()
    {
        Assert.Equal(120 / 12.0, RedactionEditor.BlockSize(120, 80, 50), 9);
        Assert.Equal(2 * RedactionEditor.BlockSize(120, 80, 50), RedactionEditor.BlockSize(240, 160, 50), 9);
        Assert.Equal(6, RedactionEditor.BlockSize(80, 120, 0), 9);
        Assert.Equal(30, RedactionEditor.BlockSize(80, 120, 100), 9);
        Assert.Equal(4, RedactionEditor.BlockSize(20, 20, 0));
        Assert.Equal(12, RedactionEditor.BlocksAcross(50));
    }

    [Fact]
    public void KeyframesMadeFromATrackContainItsBoxOnEveryFrame()
    {
        // A steady track needs few keyframes: its straight runs need only their ends (75 frames here).
        var steady = RedactionEditor.ToKeyframes(RedactionCover.Of(Moving(Enumerable.Range(10, 50)), 90, 15, 15), Square);
        Assert.All(new[] { steady.X, steady.Y, steady.Width, steady.Height }, v => Assert.InRange(v.Keyframes.Count, 2, 75 / 3));

        var cover = RedactionCover.Of(Moving(Enumerable.Range(10, 50).Where(f => f % 7 != 0)), 90, 15, 15);
        var box = RedactionEditor.ToKeyframes(cover, Square);
        foreach (var value in new[] { box.X, box.Y, box.Width, box.Height })
        {
            Assert.True(value.Keyframes.Count < cover.Boxes.Count, $"{value.Keyframes.Count} keyframes for {cover.Boxes.Count} frames");
            Assert.Equal(Math.Round(VideoTiming.TimeOfFrame(Square, cover.First), 6), value.Keyframes[0].Time);
            Assert.Equal(Math.Round(VideoTiming.TimeOfFrame(Square, cover.Last), 6), value.Keyframes[^1].Time);
        }

        foreach (var exact in cover.Boxes)
        {
            var keyed = Assert.NotNull(RedactionEditor.KeyedBox(box, Square, exact.Frame));
            Assert.True(keyed.X <= exact.X && keyed.Y <= exact.Y && keyed.X + keyed.Width >= exact.X + exact.Width && keyed.Y + keyed.Height >= exact.Y + exact.Height, $"frame {exact.Frame}");
            Assert.True(exact.X - keyed.X < 0.75 && keyed.X + keyed.Width - exact.X - exact.Width < 1, $"frame {exact.Frame} is too big");
        }

        Assert.Null(RedactionEditor.KeyedBox(box, Square, cover.First - 1));
        Assert.Null(RedactionEditor.KeyedBox(box, Square, cover.Last + 1));
    }

    // A project with 'square' playing in 'clip' and two tracks of it, track_1 and track_2.
    private static Project WithTracks()
    {
        var project = TestProjects.Empty(width: 320, height: 180, fps: 30, duration: 3);
        project.Assets.Add(Square);
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "clip", Asset = "square" });
        foreach (var id in new[] { "track_1", "track_2" })
        {
            project.Tracks.Add(new Track { Id = id, Asset = "square", Layer = "clip", FirstFrame = 0, LastFrame = 59, File = $"tracks/{id}-00000000.json", Anchors = [new TrackBox { Frame = 0, Width = 40, Height = 40 }] });
        }

        return project;
    }

    private static RedactionSettings Settings(RedactionStyle style = RedactionStyle.Pixelate, double strength = 50) => new(style, strength, 15, 4, 15, "#000000");

    [Fact]
    public void ApplyRedactionMakesChangesAndMovesRedactions()
    {
        var project = WithTracks();
        var first = RedactionEditor.Apply(project, ["track_1"], Settings());
        Assert.True(first.Created);
        Assert.Equal("redaction_1", first.Layer.Id);
        Assert.Equal(["track_1"], first.Layer.Tracks);
        Assert.Empty(first.Notes);

        // The same tracks again: the same redaction, with the new settings.
        var again = RedactionEditor.Apply(project, ["track_1"], Settings(RedactionStyle.Blur, 80));
        Assert.False(again.Created);
        Assert.Same(first.Layer, again.Layer);
        Assert.Equal((RedactionStyle.Blur, 80.0), (again.Layer.Style, again.Layer.Strength));

        // all: a new redaction takes track_1 along with its keyframed box, and the emptied one goes.
        first.Layer.Boxes = new Dictionary<string, RedactionBox> { ["track_1"] = new() { X = Keys(1), Y = Keys(2), Width = Keys(40), Height = Keys(40) } };
        var all = RedactionEditor.Apply(project, ["all"], Settings());
        Assert.Equal("redaction_1", all.Layer.Id);
        Assert.Equal(["track_1", "track_2"], all.Layer.Tracks);
        Assert.Equal(["removed 'redaction_1', which hid only track_1",
            "track_1 keeps its keyframed box from tracks_to_keyframes, which paddingPercent and holdFrames don't change; clear_keyframes property \"<track>\" goes back to the box worked out from the track"], all.Notes);
        Assert.Single(project.Layers.OfType<RedactionLayer>());
        Assert.NotNull(all.Layer.Boxes!["track_1"]);

        var split = RedactionEditor.Apply(project, ["track_2"], Settings(RedactionStyle.Solid));
        Assert.Equal("redaction_2", split.Layer.Id);
        Assert.Equal(["took track_2 from 'redaction_1', which still hides track_1"], split.Notes);
        Assert.Empty(ProjectValidator.Validate(project));

        Assert.Equal("track 'face_9' not found; tracks: track_1, track_2", Assert.Throws<ZeyoCutException>(() => RedactionEditor.Apply(project, ["face_9"], Settings())).Message);
        Assert.StartsWith("trackIds is empty;", Assert.Throws<ZeyoCutException>(() => RedactionEditor.Apply(project, [], Settings())).Message);
        Assert.Equal("strength must be between 0 and 100 (got 120)", Assert.Throws<ZeyoCutException>(() => RedactionEditor.Apply(project, ["track_1"], Settings(strength: 120))).Message);
        Assert.Equal("the project has no tracks yet; track_object makes one", Assert.Throws<ZeyoCutException>(() => RedactionEditor.Apply(TestProjects.Empty(), ["all"], Settings())).Message);
    }

    private static AnimatedValue Keys(double value) => new(value) { Keyframes = [new Keyframe { Time = 0, Value = value }, new Keyframe { Time = 1, Value = value }] };

    [Fact]
    public void ARedactionKeepsItsOwnSettingsAndIsntPlacedOnTheTimeline()
    {
        var project = WithTracks();
        RedactionEditor.Apply(project, ["track_1"], Settings());
        var follows = "'redaction_1' is a redaction of track_1: it isn't drawn or placed on the timeline itself, it hides those tracks' boxes in every clip that plays their footage, so make this change on the clips (its own settings are name, visible, style, strength, paddingPercent, feather, holdFrames, color; apply_redaction changes its tracks and tracks_to_keyframes its boxes)";
        Assert.Equal(follows, Assert.Throws<ZeyoCutException>(() => TimelineEditor.SplitLayer(project, "redaction_1", 1)).Message);
        Assert.Equal(follows, Assert.Throws<ZeyoCutException>(() => TimelineEditor.MoveLayer(project, "redaction_1", 1)).Message);
        Assert.Equal(follows, Assert.Throws<ZeyoCutException>(() => ProjectEditor.DuplicateLayer(project, new DuplicateOptions { LayerId = "redaction_1" })).Message);
        Assert.Equal(follows, Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateLayer(project, "redaction_1", new JsonObject { ["tracks"] = new JsonArray("track_2") })).Message);
        Assert.Equal(follows, Assert.Throws<ZeyoCutException>(() => ProjectEditor.GetVisualLayer(project, "redaction_1")).Message);

        ProjectEditor.UpdateLayer(project, "redaction_1", new JsonObject { ["style"] = "solid", ["color"] = "#FF0000", ["holdFrames"] = 30, ["visible"] = false });
        var layer = (RedactionLayer)project.FindLayer("redaction_1")!;
        Assert.Equal((RedactionStyle.Solid, "#FF0000", 30, false), (layer.Style, layer.Color, layer.HoldFrames, layer.Visible));
        ProjectEditor.UpdateLayer(project, "redaction_1", new JsonObject { ["strength"] = 150 });
        Assert.Equal(["layer 'redaction_1': strength must be between 0 and 100 (got 150)"], ProjectValidator.Validate(project));
        ProjectEditor.UpdateLayer(project, "redaction_1", new JsonObject { ["strength"] = 50 });

        // Cutting the timeline leaves it alone; it works on the footage wherever a clip plays it.
        TimelineEditor.RemoveRange(project, "all", 0.5, 1, ripple: true);
        Assert.Equal(0, project.FindLayer("redaction_1")!.Start);
        Assert.Empty(ProjectValidator.Validate(project));
        Assert.Contains("redaction_1 [redaction]  hides=track_1  solid #FF0000 padding=15% feather=4 hold=30  hidden", LayerTree.Format(project));

        // The JSON keeps every setting.
        var copy = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Assert.Equal(ProjectJson.Serialize(project), ProjectJson.Serialize(copy));
        Assert.Contains("\"type\": \"redaction\"", ProjectJson.Serialize(project));
        Assert.Contains("\"style\": \"solid\"", ProjectJson.Serialize(project));

        // A track in two redactions, a missing track, a parent and bad boxes are refused.
        project.Layers.Add(new RedactionLayer { Id = "redaction_2", Name = "redaction_2", Tracks = ["track_1", "nope"], ParentId = "clip", Boxes = new() { ["track_1"] = new() { X = Keys(1), Y = new AnimatedValue(), Width = Keys(-1), Height = Keys(5) } } });
        Assert.Equal([
            "layer 'redaction_2': a redaction has no parent; it works on the clips that play its tracks' footage",
            "layer 'redaction_2': track 'track_1' is already hidden by 'redaction_1'; a track has one redaction",
            "layer 'redaction_2': track 'nope' not found; tracks: track_1, track_2",
            "layer 'redaction_2': track_1.y has no keyframes; tracks_to_keyframes makes a box's keyframes",
            "layer 'redaction_2': track_1.width keyframes need times of 0 or more and finite values above 0",
        ], ProjectValidator.Validate(project));
    }

    [Fact]
    public void ARedactionsKeyframedBoxesAreEditedByTrack()
    {
        var project = WithTracks();
        RedactionEditor.Apply(project, ["track_1", "track_2"], Settings());
        var layer = (RedactionLayer)project.FindLayer("redaction_1")!;
        Assert.EndsWith("): track_1 has no keyframed box on 'redaction_1' yet; tracks_to_keyframes trackId=\"track_1\" makes one from the track",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [Key("track_1.x", 0.5, 10)])).Message);

        layer.Boxes = new Dictionary<string, RedactionBox> { ["track_1"] = RedactionEditor.ToKeyframes(RedactionCover.Of(Moving(Enumerable.Range(0, 60)), 90, 15, 0), Square) };
        var change = KeyframeEditor.SetKeyframes(project, [Key("track_1.x", 2.5, 10), Key("track_1.WIDTH", 2.5, 80)]);
        Assert.Equal((2, 0), (change.Added, change.Replaced));
        Assert.Empty(change.Warnings);
        Assert.Equal(10, RedactionEditor.KeyedBox(layer.Boxes["track_1"], Square, 75)!.Value.X, 6);

        Assert.EndsWith("'redaction_1' animates only its tracks' boxes: <track>.x, .y, .width or .height (in the footage's own seconds and pixels) for track_1, track_2; got 'opacity'",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [Key("opacity", 0, 1)])).Message);
        Assert.EndsWith("track_1.height must be above 0 (got 0)", Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [Key("track_1.height", 0, 0)])).Message);

        var x = layer.Boxes["track_1"].X.Keyframes.Count;
        Assert.Equal(1, KeyframeEditor.RemoveKeyframes(project, [new KeyframeRef("redaction_1", "track_1.x", 2.5)]));
        Assert.Equal(x - 1, layer.Boxes["track_1"].X.Keyframes.Count);
        Assert.Equal("a redaction's box is cleared whole: clear_keyframes property \"track_1\" goes back to the box worked out from the track",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.ClearKeyframes(project, "redaction_1", "track_1.x")).Message);
        Assert.Equal(0, KeyframeEditor.ClearKeyframes(project, "redaction_1", "track_2"));
        var all = layer.Boxes["track_1"];
        Assert.Equal(all.X.Keyframes.Count + all.Y.Keyframes.Count + all.Width.Keyframes.Count + all.Height.Keyframes.Count, KeyframeEditor.ClearKeyframes(project, "redaction_1", "track_1"));
        Assert.Null(layer.Boxes);

        layer.Boxes = new Dictionary<string, RedactionBox> { ["track_1"] = new() { X = new AnimatedValue(1) { Keyframes = [new Keyframe { Time = 1, Value = 1 }] }, Y = Keys(2), Width = Keys(40), Height = Keys(40) } };
        Assert.EndsWith("that is the last keyframe of track_1.x; clear_keyframes property \"track_1\" removes the whole box and goes back to the box worked out from the track",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.RemoveKeyframes(project, [new KeyframeRef("redaction_1", "track_1.x", 1)])).Message);
    }

    private static KeyframeEntry Key(string property, double time, double value) => new("redaction_1", property, time, JsonSerializer.SerializeToElement(value));

    [Fact]
    public void TemplatesKeepTheTracksAndRedactionsOfTheirFootage()
    {
        var directory = TestPaths.NewDirectory("redaction-template-src");
        var project = WithTracks();
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        File.WriteAllBytes(Path.Combine(directory, "assets", "square.mp4"), [1, 2, 3]);
        foreach (var track in project.Tracks)
        {
            track.File = TrackFiles.Save(directory, new TrackData { Track = track.Id, Asset = "square", Width = 320, Height = 180, Frames = Moving([0, 1]) });
        }

        RedactionEditor.Apply(project, ["track_1"], Settings());
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi" });
        var library = new Library(TestPaths.NewDirectory("redaction-template-lib"));

        // Only the title: no footage, so no tracks and no redaction.
        Assert.Empty(Templates.Save(project, directory, library, "titles", ["title"], null, overwrite: false).Project.Tracks);
        Assert.DoesNotContain(Templates.Save(project, directory, library, "titles", ["title"], null, overwrite: true).Project.Layers, l => l is RedactionLayer);

        // The clip without its redaction: the redaction comes along, so the footage stays hidden.
        var template = Templates.Save(project, directory, library, "clip", ["clip"], null, overwrite: false);
        Assert.Equal(["track_1", "track_2"], template.Project.Tracks.Select(t => t.Id));
        Assert.Equal(["clip", "redaction_1"], template.Project.Layers.Select(l => l.Id));

        var target = Path.Combine(TestPaths.NewDirectory("redaction-template-dst"), "episode");
        var made = Templates.Create(library, "clip", target, "episode", null, []);
        Assert.Empty(ProjectValidator.Validate(made));
        Assert.All(made.Tracks, t => Assert.Equal(TrackFiles.Load(directory, project.FindTrack(t.Id)!).Frames.Count, TrackFiles.Load(target, t).Frames.Count));
    }
}
