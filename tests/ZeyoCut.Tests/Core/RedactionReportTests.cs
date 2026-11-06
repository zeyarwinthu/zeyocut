// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class RedactionReportTests
{
    // 320x180 at 30 fps, 90 frames, playing whole in 'clip' unless trimmed.
    private static readonly Asset Square = new() { Id = "square", Type = AssetType.Video, File = "assets/square.mp4", Width = 320, Height = 180, FrameRate = "30", FrameCount = 90, Duration = 3 };

    private static Project WithTrack(TrackMethod method = TrackMethod.Csrt, double? trimOut = null)
    {
        var project = TestProjects.Empty(width: 320, height: 180, fps: 30, duration: 3);
        project.Assets.Add(Square);
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "clip", Asset = "square", TrimOut = trimOut });
        project.Tracks.Add(new Track { Id = "track_1", Asset = "square", Layer = "clip", Method = method, FirstFrame = 10, LastFrame = 60, File = "tracks/track_1-00000000.json" });
        return project;
    }

    private static RedactionSettings Settings(int holdFrames) => new(RedactionStyle.Pixelate, 50, 15, 4, holdFrames, "#000000");

    // TestVideos.MovingSquare's square on frames 10-60, lost on 20-24 and again from 45 on.
    private static TrackData Square10To60() => new()
    {
        Track = "track_1",
        Asset = "square",
        Width = 320,
        Height = 180,
        Frames = Enumerable.Range(10, 51).Select(f =>
        {
            var (x, y, size) = TestVideos.SquareAt(f);
            var lost = f is >= 20 and <= 24 or >= 45;
            return new TrackedFrame { Frame = f, X = x, Y = y, Width = size, Height = size, Confidence = lost ? 0 : 0.9, Lost = lost };
        }).ToList(),
    };

    private static TrackedFrame Box(int frame, double x, double y, double size = 40, double confidence = 0.9) =>
        new() { Frame = frame, X = x, Y = y, Width = size, Height = size, Confidence = confidence };

    [Fact]
    public void CountsTheFramesCoveredWithAndWithoutADetectionAndTheOnesLeftUncovered()
    {
        var project = WithTrack();
        var data = Square10To60();
        RedactionEditor.Apply(project, ["track_1"], Settings(3));

        var report = RedactionRisks.Of(project, project.Tracks[0], data);

        // Held 3 frames before frame 10 and after frame 44; the 5 lost frames between are held too.
        Assert.True(report.Hidden);
        Assert.False(report.Keyed);
        Assert.Equal((7, 47), (report.CoverFirst, report.CoverLast));
        Assert.Equal((30, 5, 6, 13), (report.Detected, report.HeldBetween, report.HeldAtEnds, report.Uncovered));

        // The square starts 50 px from the frame's edge, so frames 0-6 may show it; the track loses it
        // 36 px from the bottom, and it stays in the picture until frame 80, so frames 48-89 may too.
        Assert.Equal(
        [
            new RiskSpan(RiskKind.BeforeStart, 0, 6, 50),
            new RiskSpan(RiskKind.Uncovered, 48, 60, 13),
            new RiskSpan(RiskKind.AfterEnd, 48, 89, 36),
        ], report.Risks);
    }

    [Fact]
    public void FlagsLowConfidenceLongHoldsFastMotionSmallBoxesAndFootageAfterTheEnd()
    {
        var project = WithTrack(TrackMethod.Faces);
        var frames = new List<TrackedFrame>();
        for (var f = 0; f <= 70; f++)
        {
            frames.Add(f switch
            {
                5 => Box(f, 140, 70, confidence: 0.3),
                6 => Box(f, 140, 70, confidence: 0.45),
                7 => Box(f, 140, 70, confidence: 0.4),
                >= 20 and <= 39 => new TrackedFrame { Frame = f, X = 140, Y = 70, Width = 40, Height = 40, Lost = true },
                >= 60 and <= 62 => new TrackedFrame { Frame = f, X = 175, Y = 86, Width = 10, Height = 8, Confidence = 0.9 },
                > 50 => Box(f, 160, 70),
                _ => Box(f, 140, 70),
            });
        }

        var data = new TrackData { Track = "track_1", Asset = "square", Width = 320, Height = 180, Frames = frames };

        var report = RedactionRisks.Of(project, project.Tracks[0], data);

        // Not hidden: nothing is covered, and the footage after frame 70 plays while the box sits 70 px
        // from the edges.
        Assert.False(report.Hidden);
        Assert.Equal((0, 0, 0, 0), (report.Detected, report.HeldBetween, report.HeldAtEnds, report.Uncovered));
        Assert.Equal(
        [
            new RiskSpan(RiskKind.LowConfidence, 5, 7, 0.3),
            new RiskSpan(RiskKind.LongHold, 20, 39, 20),
            new RiskSpan(RiskKind.FastMotion, 50, 51, 0.5),
            new RiskSpan(RiskKind.SmallBox, 60, 62, 10),
            new RiskSpan(RiskKind.AfterEnd, 71, 89, 70),
        ], report.Risks);

        // Hidden, held 15 frames past frame 70: the footage after 85 is still uncovered.
        RedactionEditor.Apply(project, ["track_1"], Settings(15));
        var hidden = RedactionRisks.Of(project, project.Tracks[0], data);
        Assert.Equal((0, 85), (hidden.CoverFirst, hidden.CoverLast));
        Assert.Equal((51, 20, 15, 0), (hidden.Detected, hidden.HeldBetween, hidden.HeldAtEnds, hidden.Uncovered));
        Assert.Equal(new RiskSpan(RiskKind.AfterEnd, 86, 89, 70), hidden.Risks[^1]);

        // Last found against the frame's right edge, it has left the picture: nothing after it to check.
        frames[^1] = Box(70, 280, 70);
        Assert.DoesNotContain(RedactionRisks.Of(project, project.Tracks[0], data).Risks, r => r.Kind == RiskKind.AfterEnd);
    }

    [Fact]
    public void AKeyframedBoxCoversFromItsFirstKeyframeToItsLast()
    {
        var project = WithTrack();
        var data = Square10To60();
        var layer = RedactionEditor.Apply(project, ["track_1"], Settings(3)).Layer;
        layer.Boxes = new Dictionary<string, RedactionBox> { ["track_1"] = RedactionEditor.ToKeyframes(RedactionCover.Of(data.Frames, 90, 15, 3), Square) };

        var report = RedactionRisks.Of(project, project.Tracks[0], data);

        Assert.True(report.Keyed);
        Assert.Equal((7, 47), (report.CoverFirst, report.CoverLast));
        Assert.Equal((30, 5, 6, 13), (report.Detected, report.HeldBetween, report.HeldAtEnds, report.Uncovered));

        layer.Visible = false;
        Assert.False(RedactionRisks.Of(project, project.Tracks[0], data).Hidden);
    }

    // Only what some clip plays can show: the clip trimmed to 0-1.5 s (frames 0-44) leaves out the
    // uncovered frames 48-60 and the footage after the box, and another clip playing frames 54-89
    // brings them back.
    [Fact]
    public void LeavesOutSpansNoClipPlays()
    {
        var project = WithTrack(trimOut: 1.5);
        var data = Square10To60();
        RedactionEditor.Apply(project, ["track_1"], Settings(3));

        Assert.Equal([RiskKind.BeforeStart], RedactionRisks.Of(project, project.Tracks[0], data).Risks.Select(r => r.Kind));

        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "replay", Asset = "square", TrimIn = 1.8 });
        Assert.Equal([(0, 44), (54, 89)], RedactionRisks.Shown(project, Square));
        Assert.Equal([RiskKind.BeforeStart, RiskKind.Uncovered, RiskKind.AfterEnd], RedactionRisks.Of(project, project.Tracks[0], data).Risks.Select(r => r.Kind));
    }
}
