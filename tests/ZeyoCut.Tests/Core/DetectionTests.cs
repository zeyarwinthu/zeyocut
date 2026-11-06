// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Tests.Core;

public class DetectionTests
{
    private const double Confident = 0.6;
    private const double Weak = 0.3;

    // A 1 s buffer at 30 fps.
    private static DetectionLinker Linker() => new(Confident, Weak, 30);

    private static Detection Box(double x, double y, double score = 0.9, double size = 60) => new(x, y, size, size, score);

    private static string Report(List<LinkedTrack> tracks) =>
        string.Join("\n", tracks.Select(t => $"{t.Key}: {t.Frames[0].Frame}-{t.Frames[^1].Frame} from {t.Frames[0].X:0} to {t.Frames[^1].X:0}, lost {t.Frames.Count(f => f.Lost)}"));

    [Fact]
    public void AFaceMissedForAFewFramesStaysOneTrack()
    {
        var linker = Linker();
        for (var frame = 0; frame < 60; frame++)
        {
            linker.Add(frame, frame is >= 20 and < 30 ? [] : [Box(100 + 2 * frame, 50)]);
        }

        var (tracks, brief) = linker.Finish();

        var track = Assert.Single(tracks);
        Assert.Empty(brief);
        Assert.Equal(Enumerable.Range(0, 60), track.Frames.Select(f => f.Frame));
        Assert.Equal(Enumerable.Range(20, 10), track.Frames.Where(f => f.Lost).Select(f => f.Frame));
        Assert.All(track.Frames.Where(f => f.Lost), f =>
        {
            Assert.Equal(138, f.X);
            Assert.Equal(0, f.Confidence);
        });
        Assert.Equal(0.9, track.Frames[0].Confidence);
    }

    [Fact]
    public void AFaceGoneLongerThanTheBufferComesBackAsANewTrack()
    {
        var linker = Linker();
        for (var frame = 0; frame < 90; frame++)
        {
            linker.Add(frame, frame is >= 20 and < 60 ? [] : [Box(100, 50)]);
        }

        var (tracks, _) = linker.Finish();

        Assert.Equal(new[] { (0, 19), (60, 89) }, tracks.Select(t => (t.Frames[0].Frame, t.Frames[^1].Frame)));
        Assert.All(tracks, t => Assert.DoesNotContain(t.Frames, f => f.Lost));
    }

    // Two faces pass each other, and while they overlap the detector finds only the one in front.
    [Fact]
    public void FacesThatCrossKeepTheirTracks()
    {
        var linker = Linker();
        var keys = new Dictionary<int, List<int>> { [0] = [], [1] = [] };
        for (var frame = 0; frame < 50; frame++)
        {
            var right = Box(6 * frame, 100);
            var left = Box(294 - 6 * frame, 110);
            var found = linker.Add(frame, frame is >= 23 and <= 26 ? [right] : [right, left]);
            keys[0].Add(found[0]);
            if (found.Length > 1)
            {
                keys[1].Add(found[1]);
            }
        }

        var (tracks, _) = linker.Finish();

        Assert.True(tracks.Count == 2, Report(tracks));
        Assert.Single(keys[0].Distinct());
        Assert.Single(keys[1].Distinct());
        var rightward = tracks.Single(t => t.Key == keys[0][0]);
        Assert.Equal(6 * 49, rightward.Frames[^1].X);
        Assert.DoesNotContain(rightward.Frames, f => f.Lost);
        var leftward = tracks.Single(t => t.Key == keys[1][0]);
        Assert.Equal(294 - 6 * 49, leftward.Frames[^1].X);
        Assert.Equal(Enumerable.Range(23, 4), leftward.Frames.Where(f => f.Lost).Select(f => f.Frame));
    }

    [Fact]
    public void WeakDetectionsOnlyKeepATrackGoing()
    {
        var linker = Linker();
        for (var frame = 0; frame < 40; frame++)
        {
            var detections = new List<Detection>();

            // A face found confidently, then weakly for 10 frames, then confidently again; missed on
            // frame 35, it isn't taken back by a weak detection on frame 36.
            if (frame != 35)
            {
                detections.Add(Box(100, 50, frame is >= 10 and < 20 or > 35 ? 0.4 : 0.9));
            }

            // Something only ever found weakly never starts a track.
            detections.Add(Box(400, 50, 0.45));
            linker.Add(frame, detections);
        }

        var (tracks, brief) = linker.Finish();

        var track = Assert.Single(tracks);
        Assert.Empty(brief);
        Assert.Equal(Enumerable.Range(0, 35), track.Frames.Select(f => f.Frame));
        Assert.DoesNotContain(track.Frames, f => f.Lost);
        Assert.All(track.Frames.Where(f => f.Frame is >= 10 and < 20), f => Assert.Equal(0.4, f.Confidence));
    }

    [Fact]
    public void ATrackMissedBehindSomethingTakesTheFirstPartOfItBackNearWhereItWas()
    {
        var linker = Linker();
        for (var frame = 0; frame < 30; frame++)
        {
            // Covered from the right on frames 8-9, the box shrinks to the left part of the face (so the
            // track heads left); hidden on 10-12, then found again as the right part first.
            Detection[] found = frame switch
            {
                8 => [new Detection(100, 50, 40, 60, 0.8)],
                9 => [new Detection(100, 50, 20, 60, 0.7)],
                >= 10 and <= 12 => [],
                13 => [new Detection(140, 50, 20, 60, 0.7)],
                14 => [new Detection(130, 50, 30, 60, 0.8)],
                _ => [Box(100, 50)],
            };
            linker.Add(frame, found);
        }

        var (tracks, _) = linker.Finish();

        var track = Assert.Single(tracks);
        Assert.Equal(Enumerable.Range(10, 3), track.Frames.Where(f => f.Lost).Select(f => f.Frame));
    }

    // A plate is wide and low: covered from the right, its box shrinks to a sliver a third of its width
    // before it is missed, so coming out it is compared with its whole box of a moment before. These
    // are the boxes of a van's plate with a bar passing in front of it.
    [Fact]
    public void APlateHiddenFromOneSideIsTakenBackByItsWholeBox()
    {
        var linker = Linker();
        for (var frame = 0; frame < 30; frame++)
        {
            Detection[] found = frame switch
            {
                8 => [new Detection(742, 514, 88, 50, 0.89)],
                9 => [new Detection(743, 521, 22, 41, 0.63)],
                10 or 11 => [],
                12 => [new Detection(799, 507, 81, 44, 0.82)],
                _ => [new Detection(742, 511, 138, 55, 0.92)],
            };
            linker.Add(frame, found);
        }

        var (tracks, _) = linker.Finish();

        var track = Assert.Single(tracks);
        Assert.Equal([10, 11], track.Frames.Where(f => f.Lost).Select(f => f.Frame));
    }

    [Fact]
    public void BriefDetectionsAreLeftOut()
    {
        var linker = Linker();
        for (var frame = 0; frame < 20; frame++)
        {
            var detections = new List<Detection> { Box(100, 50) };
            if (frame is 5 or 6)
            {
                detections.Add(Box(400, 300, 0.7));
            }

            if (frame == 15)
            {
                detections.Add(Box(700, 300, 0.7));
            }

            linker.Add(frame, detections);
        }

        var (tracks, brief) = linker.Finish();

        Assert.Single(tracks);
        Assert.Equal(new[] { 5, 15 }, brief);
    }

    // A single confident detection, then weak ones, as a patch of clothing can give.
    [Fact]
    public void ATrackNeedsTwoConfidentDetectionsInARowToStart()
    {
        var linker = Linker();
        for (var frame = 0; frame < 20; frame++)
        {
            var detections = new List<Detection> { Box(100, 50) };
            if (frame is >= 5 and <= 7)
            {
                detections.Add(Box(400, 300, frame == 5 ? 0.63 : frame == 6 ? 0.56 : 0.38, 140));
            }

            linker.Add(frame, detections);
        }

        var (tracks, brief) = linker.Finish();

        Assert.Equal(Enumerable.Range(0, 20), Assert.Single(tracks).Frames.Select(f => f.Frame));
        Assert.Equal(new[] { 5 }, brief);
    }

    [Fact]
    public void FramesAreLinkedInOrder()
    {
        var linker = Linker();
        linker.Add(4, [Box(0, 0)]);

        Assert.Equal("frames must be linked in order (frame 4 after 4)", Assert.Throws<ZeyoCutException>(() => linker.Add(4, [])).Message);
    }
}
