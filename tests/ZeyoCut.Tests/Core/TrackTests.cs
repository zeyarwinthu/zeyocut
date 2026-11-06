// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class TrackTests
{
    // A 640x360 canvas with a 320x180 clip 'clip' of 'square' (60 frames at 30 fps), fit to the canvas
    // (scale 2) unless 'scale' says otherwise.
    private static Project Canvas(double? scale = null)
    {
        var project = TestProjects.Empty(width: 640, height: 360, fps: 30, duration: 2);
        project.Assets.Add(new Asset { Id = "square", Type = AssetType.Video, File = "assets/square.mp4", Width = 320, Height = 180, FrameRate = "30", FrameCount = 60, Duration = 2 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "clip", Asset = "square", ScaleX = scale, ScaleY = scale });
        return project;
    }

    private static TrackBox Map(Project project, double time, double x, double y, double width, double height)
    {
        var (layer, asset) = TrackEditor.Clip(project, "clip");
        var (frame, shown) = TrackEditor.SourceFrameAt(project, layer, asset, time);
        return TrackEditor.SourceBox(project, layer, asset, shown, frame, x, y, width, height);
    }

    private static void AssertBox(TrackBox box, int frame, double x, double y, double width, double height)
    {
        Assert.Equal(frame, box.Frame);
        Assert.Equal(x, box.X, 6);
        Assert.Equal(y, box.Y, 6);
        Assert.Equal(width, box.Width, 6);
        Assert.Equal(height, box.Height, 6);
    }

    private static Track Track(TrackDirection direction, int origin, params int[] anchors) => new()
    {
        Id = "track_1",
        Asset = "square",
        Layer = "clip",
        Direction = direction,
        FirstFrame = 0,
        LastFrame = 100,
        Origin = origin,
        File = "tracks/track_1-00000000.json",
        Anchors = anchors.Select(f => new TrackBox { Frame = f, Width = 10, Height = 10 }).ToList(),
    };

    [Fact]
    public void ACanvasBoxMapsIntoTheClipsOwnPixels()
    {
        // Fit to the canvas: twice the size, so canvas pixels are half as many source pixels.
        var fit = Canvas();
        AssertBox(Map(fit, 0, 40, 120, 80, 80), 0, 20, 60, 40, 40);
        AssertBox(Map(fit, 1, 100, 200, 80, 60), 30, 50, 100, 40, 30);

        // The nearest frame to 0.51s is frame 15 (0.5s).
        AssertBox(Map(fit, 0.51, 100, 200, 80, 60), 15, 50, 100, 40, 30);

        // The camera zoomed in twice on the canvas center: canvas pixels are a quarter of source ones.
        CameraEditor.Set(fit, 320, 180, 2, null, reset: false);
        AssertBox(Map(fit, 0, 320, 180, 80, 80), 0, 160, 90, 20, 20);

        // A crop shows source 100..260 x 50..140 at scale 1, centered: the box is cut to what shows.
        var crop = Canvas(scale: 1);
        var clip = (VideoLayer)crop.FindLayer("clip")!;
        (clip.CropWidth, clip.CropHeight, clip.CropX.Value, clip.CropY.Value) = (160, 90, 100, 50);
        (clip.AnchorX, clip.AnchorY) = (80, 45);
        AssertBox(Map(crop, 0, 300, 170, 40, 20), 0, 160, 85, 40, 20);
        AssertBox(Map(crop, 0, 230, 180, 40, 20), 0, 100, 95, 30, 20);
    }

    [Fact]
    public void BoxesOutsideThePictureOrTimeOutsideTheClipAreRefused()
    {
        var project = Canvas();
        Assert.Equal("the box 600,300 100x100 is mostly outside clip 'clip''s picture at 0s, or under 4 source pixels; check where it is with render_frame",
            Assert.Throws<ZeyoCutException>(() => Map(project, 0, 600, 300, 100, 100)).Message);
        Assert.Equal("the box 10,10 6x6 is mostly outside clip 'clip''s picture at 0s, or under 4 source pixels; check where it is with render_frame",
            Assert.Throws<ZeyoCutException>(() => Map(project, 0, 10, 10, 6, 6)).Message);
        Assert.Equal("box needs finite x and y and a width and height above 0 (got 10,10 0x20)",
            Assert.Throws<ZeyoCutException>(() => Map(project, 0, 10, 10, 0, 20)).Message);

        project.Duration = 4;
        Assert.Equal("clip 'clip' isn't on the timeline at 3s (it plays 0–2s)",
            Assert.Throws<ZeyoCutException>(() => Map(project, 3, 40, 120, 80, 80)).Message);
        project.Layers.Add(new TextLayer { Id = "title", Text = "Hi", FontFamily = "Segoe UI" });
        Assert.Equal("tracking works on video layers; 'title' is a text layer",
            Assert.Throws<ZeyoCutException>(() => TrackEditor.Clip(project, "title")).Message);
    }

    [Fact]
    public void ReanchoringTracksAwayFromTheOriginUpToTheNextAnchor()
    {
        var both = Track(TrackDirection.Both, 40, 20, 40, 70);
        Assert.Equal([new TrackRun(60, 69)], TrackEditor.Rerun(both, 60));
        Assert.Equal([new TrackRun(80, 100)], TrackEditor.Rerun(both, 80));
        Assert.Equal([new TrackRun(70, 100)], TrackEditor.Rerun(both, 70));
        Assert.Equal([new TrackRun(30, 21)], TrackEditor.Rerun(both, 30));
        Assert.Equal([new TrackRun(10, 0)], TrackEditor.Rerun(both, 10));
        Assert.Equal([new TrackRun(40, 21), new TrackRun(40, 69)], TrackEditor.Rerun(both, 40));

        var forward = Track(TrackDirection.Forward, 0, 0);
        Assert.Equal([new TrackRun(0, 100)], TrackEditor.Rerun(forward, 0));
        Assert.Equal([new TrackRun(100, 100)], TrackEditor.Rerun(forward, 100));

        var anchors = TrackEditor.WithAnchor(both.Anchors, new TrackBox { Frame = 70, X = 5, Width = 12, Height = 12 });
        Assert.Equal([20, 40, 70], anchors.Select(a => a.Frame));
        Assert.Equal(12, anchors[2].Width);

        Assert.Equal([new TrackRun(25, 0), new TrackRun(25, 59)], TrackEditor.Runs(TrackDirection.Both, 25, 0, 59));
        Assert.Equal([new TrackRun(25, 59)], TrackEditor.Runs(TrackDirection.Forward, 25, 0, 59));
    }

    [Fact]
    public void LowConfidenceFramesNearEachOtherMakeOneDriftSpan()
    {
        var confidences = new Dictionary<int, double> { [10] = 0.3, [11] = 0.1, [14] = 0.4, [25] = 0.45, [29] = 0.2 };
        var frames = Enumerable.Range(0, 40)
            .Select(f => new TrackedFrame { Frame = f, Width = 10, Height = 10, Confidence = confidences.GetValueOrDefault(f, 0.9), Lost = f == 11 })
            .ToList();

        var drift = TrackEditor.Drift(frames);

        Assert.Equal([new DriftSpan(10, 14, 0.1, 1), new DriftSpan(25, 25, 0.45, 0), new DriftSpan(29, 29, 0.2, 0)], drift);
    }

    [Fact]
    public void TracksAreWrittenOnlyWhenThereAreSomeAndAreValidated()
    {
        var project = Canvas();
        Assert.DoesNotContain("\"tracks\"", ProjectJson.Serialize(project));
        Assert.Equal(6, project.FormatVersion);

        project.Tracks.Add(Track(TrackDirection.Forward, 0, 0));
        var restored = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        var track = Assert.Single(restored.Tracks);
        Assert.Equal(("track_1", "square", "clip", TrackMethod.Csrt, "tracks/track_1-00000000.json"), (track.Id, track.Asset, track.Layer, track.Method, track.File));
        Assert.Contains("\"method\": \"csrt\"", ProjectJson.Serialize(restored));
        Assert.Empty(ProjectValidator.Validate(restored));

        restored.Tracks.Add(Track(TrackDirection.Forward, 0, 0));
        restored.Tracks[1].Asset = "nothing";
        restored.Tracks[1].File = "../outside.json";
        restored.Tracks[1].Origin = 200;
        Assert.Equal(
            [
                "duplicate track id 'track_1'",
                "track 'track_1': asset 'nothing' not found",
                "track 'track_1': file '../outside.json' must be a file in the project's tracks folder",
                "track 'track_1': frames 0–100 must run forward from 0 and include its origin frame 200",
            ],
            ProjectValidator.Validate(restored));
    }

    [Fact]
    public void EachVersionOfATrackIsAFileOfItsOwn()
    {
        var directory = TestPaths.NewDirectory("track-files");
        var data = new TrackData
        {
            Track = "track_1",
            Asset = "square",
            Width = 320,
            Height = 180,
            Frames = [new TrackedFrame { Frame = 0, X = 20.004, Y = 60, Width = 40, Height = 40, Confidence = 1 }, new TrackedFrame { Frame = 1, X = 23.5, Y = 61, Width = 40, Height = 40, Confidence = 0.12345, Lost = true }],
        };

        var first = TrackFiles.Save(directory, data);
        Assert.Matches("^tracks/track_1-[0-9a-f]{8}\\.json$", first);
        Assert.Equal(first, TrackFiles.Save(directory, data));
        data.Frames[1].X = 24;
        var second = TrackFiles.Save(directory, data);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(Path.Combine(directory, first)));

        var loaded = TrackFiles.Load(directory, new Track { Id = "track_1", File = first });
        Assert.Equal([20, 23.5], loaded.Frames.Select(f => f.X));
        Assert.Equal(0.123, loaded.Frames[1].Confidence);
        Assert.True(loaded.Frames[1].Lost);
        Assert.DoesNotContain("\"lost\"", File.ReadAllText(Path.Combine(directory, first)).Split("},")[0]);
        Assert.Equal("track 'track_9': its file tracks/track_9-x.json is missing; run track_object again",
            Assert.Throws<ZeyoCutException>(() => TrackFiles.Load(directory, new Track { Id = "track_9", File = "tracks/track_9-x.json" })).Message);
    }
}
