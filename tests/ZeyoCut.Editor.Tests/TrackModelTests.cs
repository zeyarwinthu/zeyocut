// SPDX-License-Identifier: MIT

using System.Windows;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

public class TrackModelTests
{
    // A 640x360 canvas with 3 s of a 320x180 street video (90 frames at 30 fps) in two clips fit to
    // it: 'a' plays its first second, 'b' its last two from 1 s. track_1 is drawn on frame 30 and
    // tracked over every frame, lost on 60–64 and unsure on 65–69; track_2 is a face found on frames
    // 40–89. track_1 is blurred.
    private static (Project Project, Dictionary<string, TrackData> Data) Street()
    {
        var project = TestFiles.Project(640, 360, 3);
        project.Fps = 30;
        project.Assets.Add(new Asset { Id = "street", Type = AssetType.Video, File = "assets/street.mp4", Width = 320, Height = 180, FrameRate = "30", FrameCount = 90, Duration = 3 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "a", Asset = "street", TrimOut = 1 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "b", Asset = "street", Start = 1, TrimIn = 1 });
        TrackedFrame Box(int frame, double confidence = 0.9, bool lost = false) =>
            new() { Frame = frame, X = 20 + frame, Y = 40, Width = 30, Height = 20, Confidence = confidence, Lost = lost };
        var data = new Dictionary<string, TrackData>
        {
            ["track_1"] = new() { Track = "track_1", Asset = "street", Width = 320, Height = 180,
                Frames = Enumerable.Range(0, 90).Select(f => f is >= 60 and < 65 ? Box(f, 0, lost: true) : f is >= 65 and < 70 ? Box(f, 0.3) : Box(f)).ToList() },
            ["track_2"] = new() { Track = "track_2", Asset = "street", Width = 320, Height = 180, Frames = Enumerable.Range(40, 50).Select(f => Box(f)).ToList() },
        };
        project.Tracks.Add(new Track { Id = "track_1", Asset = "street", Layer = "b", Method = TrackMethod.Csrt, Direction = TrackDirection.Both, FirstFrame = 0, LastFrame = 89, Origin = 30,
            File = "tracks/track_1-00000000.json", Anchors = [new TrackBox { Frame = 30, X = 50, Y = 40, Width = 30, Height = 20 }] });
        project.Tracks.Add(new Track { Id = "track_2", Asset = "street", Layer = "b", Method = TrackMethod.Faces, FirstFrame = 40, LastFrame = 89, Origin = 40,
            File = "tracks/track_2-00000000.json" });
        RedactionEditor.Apply(project, ["track_1"], new RedactionSettings(RedactionStyle.Blur, 50, 15, 4, 15, "#000000"));
        return (project, data);
    }

    [Fact]
    public void TheViewWorksOnTheChosenClipAtThePlayheadElseTheTopOne()
    {
        var (project, _) = Street();
        Assert.Equal("a", TrackModel.Clip(project, null, 0.5, null)!.Id);
        Assert.Equal("b", TrackModel.Clip(project, null, 1.5, null)!.Id);

        // The chosen layer when it's at the playhead; past the end, the clip it worked on.
        Assert.Equal("b", TrackModel.Clip(project, "a", 1.5, null)!.Id);
        Assert.Equal("b", TrackModel.Clip(project, "b", 1.5, "a")!.Id);
        Assert.Equal("a", TrackModel.Clip(project, null, 4, "a")!.Id);
        Assert.Null(TrackModel.Clip(project, null, 4, null));
        Assert.Equal(["track_1"], TrackModel.TracksOn(project, (VideoLayer)project.FindLayer("a")!).Select(t => t.Id));
    }

    [Fact]
    public void ACardSaysHowItsHiddenItsTimeOnTheClipAndWhereItWasLost()
    {
        var (project, data) = Street();
        var clip = (VideoLayer)project.FindLayer("b")!;
        var cards = TrackModel.Cards(project, clip, t => data[t.Id], "track_1");

        var first = cards[0];
        Assert.Equal(("track_1 · Object", "#5BE0C8", "Blur", "1.00 – 3.00 s · 60 frames", true), (first.Title, first.Color, first.Hide, first.Span, first.Selected));
        Assert.Equal("Lost for 10 frames at 2 s", first.Problem);
        Assert.Equal((60, 30), (first.ProblemFrame, first.First));
        Assert.Equal([new TrackPart(0, 0.5, false), new TrackPart(0.5, 0.666666667, true), new TrackPart(0.666666667, 1, false)],
            first.Parts.Select(p => new TrackPart(Math.Round(p.From, 9), Math.Round(p.To, 9), p.Unsure)));

        var face = cards[1];
        Assert.Equal(("track_2 · Face", "#F27AD0", "Not hidden", "1.33 – 3.00 s · 50 frames", null, false), (face.Title, face.Color, face.Hide, face.Span, face.Problem, face.Selected));
        Assert.Equal(40, face.First);

        // Only unsure, the line says so; a track whose file can't be read says that.
        foreach (var frame in data["track_1"].Frames.Where(f => f.Lost))
        {
            (frame.Lost, frame.Confidence) = (false, 0.4);
        }

        Assert.Equal("Unsure for 10 frames at 2 s", TrackModel.Cards(project, clip, t => data[t.Id], null)[0].Problem);
        Assert.Equal("Its boxes can't be read", TrackModel.Cards(project, clip, _ => null, null)[0].Span);
    }

    [Fact]
    public void EachTracksBoxIsOnTheFrameWhereTheClipShowsIt()
    {
        var (project, data) = Street();
        var clip = (VideoLayer)project.FindLayer("b")!;

        // At 2 s 'b' shows frame 60: track_1 lost there, the face at 90%. The clip is twice its size on the canvas.
        var shapes = TrackModel.Shapes(project, clip, 2, t => data[t.Id]);
        Assert.Equal(["track_1 · lost", "track_2 · 90%"], shapes.Select(s => s.Label));
        Assert.Equal(new Rect(160, 80, 60, 40), shapes[0].Bounds);
        Assert.Equal([true, false], shapes.Select(s => s.Correctable));

        // The box set by hand says so; before the face is found there is no box for it; off the clip, none.
        Assert.Equal(["track_1 · set by hand"], TrackModel.Shapes(project, clip, 1, t => data[t.Id]).Select(s => s.Label));
        Assert.Empty(TrackModel.Shapes(project, clip, 0.5, t => data[t.Id]));

        // The trail: the middle of the box on the 12 frames before the playhead, oldest first.
        var trail = TrackModel.Trail(project, clip, 45, data["track_1"]);
        Assert.Equal(TrackModel.TrailFrames, trail.Count);
        Assert.Equal(new Point(2 * (20 + 33 + 15), 2 * 50), trail[0]);
        Assert.Equal(new Point(2 * (20 + 44 + 15), 2 * 50), trail[^1]);
    }

    [Fact]
    public void TheTrackingTimelineHasTheSpansAndTheBoxesSetByHand()
    {
        var (project, data) = Street();
        var rows = TrackModel.Rows(project, (VideoLayer)project.FindLayer("b")!, t => data[t.Id]);
        Assert.Equal(["track_1 · Object", "track_2 · Face"], rows.Select(r => r.Title));
        Assert.Equal([new TrackSpan(1, 2, false), new TrackSpan(2, 2.333333333, true), new TrackSpan(2.333333333, 3, false)],
            rows[0].Spans.Select(s => new TrackSpan(Math.Round(s.From, 9), Math.Round(s.To, 9), s.Unsure)));
        Assert.Equal([1.0], rows[0].Anchors);
        Assert.Empty(rows[1].Anchors);

        // On 'a', which plays frames 0–29, the box set at frame 30 isn't shown.
        Assert.Empty(TrackModel.Rows(project, (VideoLayer)project.FindLayer("a")!, t => data[t.Id])[0].Anchors);
        Assert.Equal([0.1, 0.2, 0.5, 1.0, 2.0], new[] { 0.5, 1.5, 3.0, 8.0, 12.0 }.Select(TrackStrip.Step));
    }
}
