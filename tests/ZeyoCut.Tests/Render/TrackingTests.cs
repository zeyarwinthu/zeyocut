// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class TrackingTests
{
    private static (string Ffmpeg, string File, Asset Asset) Square(string name, (int First, int Last, int X, int Y)? cover = null)
    {
        var ffmpeg = Ffmpeg.Require();
        var file = TestVideos.MovingSquare(Path.Combine(TestPaths.NewDirectory(name), "square.mp4"), cover: cover);
        return (ffmpeg.Ffmpeg, file, TestVideos.AssetFor(file, "square"));
    }

    private static TrackBox AnchorAt(int frame)
    {
        var (x, y, size) = TestVideos.SquareAt(frame);
        return new TrackBox { Frame = frame, X = x, Y = y, Width = size, Height = size };
    }

    // The box's center within 3 px of the square's on every frame (and its size within 'sizeShare').
    private static void AssertOnTheSquare(IReadOnlyList<TrackedFrame> frames, string method, double sizeShare)
    {
        var report = string.Join("; ", frames.Select(f =>
        {
            var (x, y, size) = TestVideos.SquareAt(f.Frame);
            return $"{f.Frame}: center {f.X + f.Width / 2 - (x + size / 2):0.#},{f.Y + f.Height / 2 - (y + size / 2):0.#} size {f.Width:0.#}x{f.Height:0.#} c={f.Confidence:0.##}";
        }));
        foreach (var frame in frames)
        {
            var (x, y, size) = TestVideos.SquareAt(frame.Frame);
            var centerX = frame.X + frame.Width / 2 - (x + size / 2);
            var centerY = frame.Y + frame.Height / 2 - (y + size / 2);
            Assert.True(Math.Abs(centerX) <= 3 && Math.Abs(centerY) <= 3, $"{method} frame {frame.Frame} is off: {report}");
            Assert.True(Math.Abs(frame.Width / size - 1) <= sizeShare && Math.Abs(frame.Height / size - 1) <= sizeShare, $"{method} frame {frame.Frame} has the wrong size: {report}");
            Assert.True(frame.Confidence >= 0.5, $"{method} frame {frame.Frame}: confidence {frame.Confidence:0.###}");
            Assert.False(frame.Lost);
        }
    }

    // CSRT searches for the size too; on this rigid square it wanders between 33 and 44 px.
    private static double SizeShare(TrackMethod method) => method == TrackMethod.Csrt ? 0.2 : 0.05;

    [Theory]
    [InlineData(TrackMethod.Csrt)]
    [InlineData(TrackMethod.Template)]
    [InlineData(TrackMethod.OpticalFlow)]
    public void EachMethodFollowsAMovingSquareWithinThreePixels(TrackMethod method)
    {
        var (ffmpeg, file, asset) = Square($"track-square-{method}");
        var progress = new List<double>();

        var frames = ObjectTracker.Track(ffmpeg, file, asset, AnchorAt(0), method, 59, progress.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 60), frames.Select(f => f.Frame));
        Assert.Equal(1, frames[0].Confidence);
        AssertOnTheSquare(frames, method.ToString(), SizeShare(method));
        Assert.Equal(1, progress[^1], 3);
    }

    [Theory]
    [InlineData(TrackMethod.Csrt)]
    [InlineData(TrackMethod.OpticalFlow)]
    public void TrackingRunsBackwardsFromTheAnchor(TrackMethod method)
    {
        var (ffmpeg, file, asset) = Square($"track-backward-{method}");

        // 50 frames back, across the decoder's 24-frame chunks.
        var frames = ObjectTracker.Track(ffmpeg, file, asset, AnchorAt(50), method, 0, null, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 51), frames.Select(f => f.Frame));
        Assert.Equal(1, frames[^1].Confidence);
        AssertOnTheSquare(frames, method.ToString(), SizeShare(method));
    }

    [Theory]
    [InlineData(TrackMethod.Csrt)]
    [InlineData(TrackMethod.Template)]
    [InlineData(TrackMethod.OpticalFlow)]
    public void ASquareCoveredForAFewFramesIsReportedAsDrift(TrackMethod method)
    {
        var (ffmpeg, file, asset) = Square($"track-covered-{method}", cover: (30, 35, 100, 75));

        var frames = ObjectTracker.Track(ffmpeg, file, asset, AnchorAt(0), method, 59, null, TestContext.Current.CancellationToken);
        var drift = TrackEditor.Drift(frames);

        var report = string.Join("; ", drift.Select(d => $"{d.First}-{d.Last} lowest {d.Lowest:0.##} lost {d.Lost}"));
        var covered = Assert.Single(drift, d => d.First <= 35 && d.Last >= 30);
        Assert.True(covered.First >= 30 && covered.First <= 31, report);
        Assert.True(covered.Last >= 35, report);
        Assert.All(frames.Where(f => f.Frame < 30), f => Assert.True(f.Confidence >= 0.5, $"frame {f.Frame}: {f.Confidence:0.###} ({report})"));
    }
}
