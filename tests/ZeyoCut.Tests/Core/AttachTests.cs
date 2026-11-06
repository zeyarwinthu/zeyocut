// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class AttachTests
{
    // 320x180 at 30 fps, 90 frames, fit to the 640x360 canvas at scale 2 as 'clip'.
    private static readonly Asset Square = new() { Id = "square", Type = AssetType.Video, File = "assets/square.mp4", Width = 320, Height = 180, FrameRate = "30", FrameCount = 90, Duration = 3 };

    private static Project WithClip()
    {
        var project = TestProjects.Empty(width: 640, height: 360, fps: 30, duration: 3);
        project.Assets.Add(Square);
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "clip", Asset = "square" });
        project.Tracks.Add(new Track { Id = "track_1", Asset = "square", Layer = "clip", FirstFrame = 0, LastFrame = 89, File = "tracks/track_1-00000000.json" });
        return project;
    }

    private static TextLayer Label(Project project, double x = 100, double y = 50, string? parent = null, double scale = 1) =>
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "label", Text = "Ann", X = x, Y = y, ParentId = parent, ScaleX = scale, ScaleY = scale });

    // A square moving right and down and growing: at 20+2f, 60+f/2, 40+f/5 px on frame f. The tracker
    // lost it on frames 30-34, where its boxes are left behind at 0,0.
    private static (double X, double Y, double Side) BoxOf(int frame) => (20 + 2 * frame, 60 + frame / 2.0, 40 + frame / 5.0);

    private static TrackData Moving(int from = 0, int to = 89) => new()
    {
        Track = "track_1",
        Asset = "square",
        Width = 320,
        Height = 180,
        Frames = Enumerable.Range(from, to - from + 1).Select(f =>
        {
            var lost = f is >= 30 and <= 34;
            var (x, y, side) = lost ? (0, 0, 40) : BoxOf(f);
            return new TrackedFrame { Frame = f, X = x, Y = y, Width = side, Height = side, Confidence = lost ? 0 : 0.9, Lost = lost };
        }).ToList(),
    };

    // The center and size on the canvas of the box on the frame the top clip shows at 'time'.
    private static (double X, double Y, double Size) Box(Project project, double time)
    {
        var placed = SceneEvaluator.Evaluate(project, time).Last(e => e.Shown && e.Layer is VideoLayer);
        var clip = (VideoLayer)placed.Layer;
        var (x, y, side) = BoxOf(VideoTiming.FrameAt(Square, VideoTiming.SourceTime(clip, time)));
        var crop = VideoTiming.Crop(clip, Square, time);
        var (cx, cy) = placed.World.Apply(x + side / 2 - crop.X, y + side / 2 - crop.Y);
        return (cx, cy, Scale(placed.World) * side);
    }

    // Where the label's anchor is on the canvas at 'time', and how much it is scaled there.
    private static (double X, double Y, double Scale) Placed(Project project, double time)
    {
        var placed = SceneEvaluator.Evaluate(project, time).First(e => e.Layer.Id == "label");
        var (x, y) = placed.World.Apply(placed.Layer.AnchorX, placed.Layer.AnchorY);
        return (x, y, Scale(placed.World));
    }

    private static double Scale(Affine2D m) => Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));

    // The clip moves and turns, and the camera zooms from 1 to 1.5: the label stays 60 px above the
    // box's center on every frame, within the 0.5 px its keyframes are thinned to (0.75 px on the
    // canvas at zoom 1.5).
    [Fact]
    public void FollowsTheBoxThroughTheClipsPlacementAndTheCamera()
    {
        var project = WithClip();
        var clip = (VideoLayer)project.FindLayer("clip")!;
        clip.X.Keyframes = [new Keyframe { Time = 0, Value = 320 }, new Keyframe { Time = 3, Value = 420 }];
        clip.Rotation = new AnimatedValue(10);
        project.Camera = new Camera { X = new AnimatedValue(320), Y = new AnimatedValue(180) };
        project.Camera.Zoom.Keyframes = [new Keyframe { Time = 0, Value = 1 }, new Keyframe { Time = 3, Value = 1.5 }];
        var label = Label(project);

        var result = AttachEditor.Attach(project, "label", project.Tracks[0], Moving(), (0, -60), followScale: false);

        Assert.Equal([new AttachRun("clip", 0, 89 / 30.0)], result.Runs);
        Assert.Equal(["x", "y"], result.Keys.Select(k => k.Property));
        Assert.InRange(result.Keys[0].Keys, 2, 60);
        Assert.Empty(label.ScaleX.Keyframes);
        for (var frame = 0; frame < 90; frame++)
        {
            var box = Box(project, frame / 30.0);
            var placed = Placed(project, frame / 30.0);
            Assert.True(Math.Abs(placed.X - box.X) <= 0.76 && Math.Abs(placed.Y - (box.Y - 60)) <= 0.76, $"frame {frame}: label at {placed.X},{placed.Y}, box center {box.X},{box.Y}");
        }
    }

    // Under a parent that is moved, turned and scaled 2x, the label (0.5x, so 1x on the canvas) grows
    // with the box, and so does its offset from the box's center.
    [Fact]
    public void ScalesTheLayerAndItsOffsetWithTheBoxUnderAParent()
    {
        var project = WithClip();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "holder", Text = "x", X = 200, Y = 100, Rotation = 30, ScaleX = 2, ScaleY = 2 });
        Label(project, parent: "holder", scale: 0.5);

        var result = AttachEditor.Attach(project, "label", project.Tracks[0], Moving(), (10, -50), followScale: true);

        Assert.Equal(["x", "y", "scaleX", "scaleY"], result.Keys.Select(k => k.Property));
        Assert.Equal(1, result.Smallest, 6);
        Assert.Equal((40 + 89 / 5.0) / 40, result.Largest, 3);
        var start = Box(project, 0);
        Assert.Equal(1, Placed(project, 0).Scale, 3);
        for (var frame = 0; frame < 90; frame++)
        {
            var box = Box(project, frame / 30.0);
            var placed = Placed(project, frame / 30.0);
            var grown = box.Size / start.Size;
            Assert.True(Math.Abs(placed.X - (box.X + 10 * grown)) <= 1.02 && Math.Abs(placed.Y - (box.Y - 50 * grown)) <= 1.02, $"frame {frame}: label at {placed.X},{placed.Y}, box center {box.X},{box.Y}, grown {grown}");
            Assert.True(Math.Abs(placed.Scale - grown) <= 0.004, $"frame {frame}: label scaled {placed.Scale}, box grown {grown}");
        }
    }

    // Without an offset the label stays where it is at the first frame: the box's center there is
    // 40,80 in the footage, 80,160 on the canvas.
    [Fact]
    public void KeepsWhereTheLayerIsWithoutAnOffset()
    {
        var project = WithClip();
        var label = Label(project, 100, 50);

        var result = AttachEditor.Attach(project, "label", project.Tracks[0], Moving(), null, followScale: true);

        Assert.False(result.OffsetGiven);
        Assert.Equal((20.0, -110.0), (result.OffsetX, result.OffsetY));
        Assert.Equal((0.0, 100.0, 50.0), (label.X.Keyframes[0].Time, label.X.Keyframes[0].Value, label.Y.Keyframes[0].Value));
        Assert.Equal(1, label.ScaleX.Keyframes[0].Value);
    }

    // The track found the object on frames 15-60 (0.5-2 s): the label's own x keyframe at 1 s goes,
    // the ones at 0 s and 2.9 s stay, and its motion path goes.
    [Fact]
    public void ReplacesItsOwnKeyframesOnlyWhereItFollowsAndItsMotionPath()
    {
        var project = WithClip();
        var label = Label(project);
        label.X.Keyframes = [new Keyframe { Time = 0, Value = 5 }, new Keyframe { Time = 1, Value = 6 }, new Keyframe { Time = 2.9, Value = 7 }];
        label.MotionPath = new MotionPath { Points = [new PathPoint { X = 0, Y = 0 }, new PathPoint { X = 10, Y = 10 }] };

        var result = AttachEditor.Attach(project, "label", project.Tracks[0], Moving(15, 60), (0, 0), followScale: false);

        Assert.Equal([new AttachRun("clip", 0.5, 2)], result.Runs);
        Assert.Equal(1, result.Replaced);
        Assert.Equal([(0.0, 5.0), (2.9, 7.0)], label.X.Keyframes.Where(k => k.Value < 10).Select(k => (k.Time, k.Value)));
        Assert.All(label.X.Keyframes.Where(k => k.Value >= 10), k => Assert.InRange(k.Time, 0.5, 2));
        Assert.Null(label.MotionPath);
        Assert.Equal([EffectEditor.PathRemovedWarning(label)], result.Warnings);
    }

    // Split at 1 s and the second piece slowed to half speed: the label follows into it.
    [Fact]
    public void FollowsAcrossASplitAndASpeedChange()
    {
        var project = WithClip();
        TimelineEditor.SplitLayer(project, "clip", 1);
        TimelineEditor.SetSpeed(project, "clip_2", 0.5, ripple: true);
        Label(project);

        var result = AttachEditor.Attach(project, "label", project.Tracks[0], Moving(), (0, -40), followScale: false);

        Assert.Equal(["clip", "clip_2"], result.Runs.Select(r => r.Clip));
        Assert.Equal(1, result.Runs[1].From, 6);
        for (var frame = 0; frame / 30.0 <= result.Runs[1].To + 1e-9; frame++)
        {
            var box = Box(project, frame / 30.0);
            var placed = Placed(project, frame / 30.0);
            Assert.True(Math.Abs(placed.X - box.X) <= 0.51 && Math.Abs(placed.Y - (box.Y - 40)) <= 0.51, $"frame {frame}: label at {placed.X},{placed.Y}, box center {box.X},{box.Y}");
        }
    }

    [Fact]
    public void RefusesWhatItCantFollow()
    {
        var project = WithClip();
        var label = Label(project);
        var track = project.Tracks[0];

        Assert.Equal("attach_to_track moves text and image layers; 'clip' is a video layer",
            Assert.Throws<ZeyoCutException>(() => AttachEditor.Attach(project, "clip", track, Moving(), null, true)).Message);

        label.Start = 2.5;
        Assert.Equal(
            "'label' is on the timeline 2.5–3s, and no visible clip shows the frames where track 'track_1' found the object then; they play 0–2s on 'clip'; change 'label''s start and end with update_layer so they meet",
            Assert.Throws<ZeyoCutException>(() => AttachEditor.Attach(project, "label", track, Moving(0, 60), null, true)).Message);
        label.Start = 0;

        project.FindLayer("clip")!.ParentId = "label";
        Assert.StartsWith("clip 'clip' plays the track's footage and is a child of 'label', so moving 'label' would move the footage too",
            Assert.Throws<ZeyoCutException>(() => AttachEditor.Attach(project, "label", track, Moving(), null, true)).Message);
        project.FindLayer("clip")!.ParentId = null;

        var lost = Moving();
        lost.Frames.ForEach(f => f.Lost = true);
        Assert.Equal("track 'track_1' has no frame where the object was found, so there is nothing to follow",
            Assert.Throws<ZeyoCutException>(() => AttachEditor.Attach(project, "label", track, lost, null, true)).Message);
        Assert.Empty(label.X.Keyframes);
    }
}
