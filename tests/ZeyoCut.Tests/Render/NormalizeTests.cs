// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class NormalizeTests
{
    private static VideoProbeResult Probe(string rate, string average) =>
        new(1920, 1080, rate, average, 300, 10, "h264", "yuv420p", 8, 0, null, 0, "1:1", true);

    [Fact]
    public void TargetRateSnapsVariableFootageToAStandardRate()
    {
        Assert.Equal("30", VideoNormalizer.TargetRate(Probe("30", "30")));
        Assert.Equal("30000/1001", VideoNormalizer.TargetRate(Probe("30", "2987/100")));
        Assert.Equal("60000/1001", VideoNormalizer.TargetRate(Probe("120", "119/2")));
        Assert.Equal("25", VideoNormalizer.TargetRate(Probe("90000/1", "2509/100")));
        Assert.Contains("variable frame rate (average 29.87 fps, nominal 30 fps)", Probe("30", "2987/100").NormalizeReasons());
        Assert.Empty(Probe("30000/1001", "30000/1001").NormalizeReasons());
    }

    [Fact]
    public void VariableFrameRateBecomesConstant()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("normalize-vfr");
        var source = TestVideos.VariableFrameRate(Path.Combine(directory, "phone.mp4"));

        var probe = VideoProbe.Probe(ffmpeg.Ffprobe, source);
        Assert.True(probe.IsVariableFrameRate, $"r={probe.FrameRate} avg={probe.AverageFrameRate}");
        Assert.Contains(probe.NormalizeReasons(), r => r.StartsWith("variable frame rate"));

        var progress = new List<double>();
        var output = Path.Combine(directory, "normalized.mp4");
        var result = VideoNormalizer.Normalize(ffmpeg, source, probe, output, progress.Add, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(directory, "normalized.partial.mp4")));
        Assert.Equal("30000/1001", result.FrameRate);
        Assert.False(result.IsVariableFrameRate);
        Assert.InRange(result.FrameCount, 59, 61);
        Assert.InRange(result.Duration, 1.96, 2.04);
        Assert.Equal("h264", result.Codec);
        Assert.Equal("yuv420p", result.PixelFormat);
        Assert.True(result.HasAudio);
        Assert.Empty(result.NormalizeReasons());
        Assert.Equal(1, progress[^1]);

        var asset = TestVideos.AssetFor(output);
        using var frames = new VideoFrameSource(() => ffmpeg.Ffmpeg, stream: false);
        Assert.Equal(160, frames.GetFrame("n", output, asset, 59, 160, 120).Width);
    }

    [Fact]
    public void RotationIsAppliedAndSwapsDimensions()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("normalize-rotation");
        var upright = TestVideos.Halves(Path.Combine(directory, "halves.mp4"));
        var rotated = TestVideos.Rotated(upright, Path.Combine(directory, "rotated.mp4"), 90);

        var probe = VideoProbe.Probe(ffmpeg.Ffprobe, rotated);
        Assert.Equal(64, probe.Width);
        Assert.Equal(48, probe.Height);
        Assert.Contains(probe.Rotation, new[] { 90, 270 });
        Assert.Contains(probe.NormalizeReasons(), r => r.StartsWith("rotated"));

        var output = Path.Combine(directory, "upright.mp4");
        var result = VideoNormalizer.Normalize(ffmpeg, rotated, probe, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(48, result.Width);
        Assert.Equal(64, result.Height);
        Assert.Equal(0, result.Rotation);
        Assert.Equal("30", result.FrameRate);

        // The source's left/right split is now a top/bottom split.
        using var frames = new VideoFrameSource(() => ffmpeg.Ffmpeg, stream: false);
        var image = frames.GetFrame("r", output, TestVideos.AssetFor(output), 0, 48, 64);
        var top = TestImages.Pixel(image, 24, 12).Red;
        var bottom = TestImages.Pixel(image, 24, 52).Red;
        Assert.True(Math.Abs(top - bottom) > 200, $"top {top}, bottom {bottom}");
        Assert.True(Math.Abs(TestImages.Pixel(image, 6, 12).Red - TestImages.Pixel(image, 42, 12).Red) < 8);
    }

    [Fact]
    public void DeepColorAndHdrBecomeEightBitBt709()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("normalize-deep-color");
        foreach (var hdr in new[] { false, true })
        {
            var source = TestVideos.DeepColor(Path.Combine(directory, hdr ? "hdr.mp4" : "ten_bit.mp4"), hdr);
            var probe = VideoProbe.Probe(ffmpeg.Ffprobe, source);
            Assert.Equal(10, probe.BitDepth);
            Assert.Equal(hdr, probe.IsHdr);
            Assert.Contains(probe.NormalizeReasons(), r => r.StartsWith("10-bit"));
            Assert.Equal(hdr, VideoNormalizer.VideoFilter(probe).Contains("tonemap"));

            var result = VideoNormalizer.Normalize(ffmpeg, source, probe, Path.Combine(directory, (hdr ? "hdr" : "ten_bit") + "_sdr.mp4"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(8, result.BitDepth);
            Assert.False(result.IsHdr);
            Assert.Equal("bt709", result.ColorTransfer);
            Assert.Equal("25", result.FrameRate);
            Assert.Equal(25, result.FrameCount);
        }
    }

    [Fact]
    public async Task JobManagerReportsProgressResultsAndErrors()
    {
        var jobs = new JobManager();
        using var gate = new ManualResetEventSlim();
        var first = jobs.Start("import", "slow import", (job, ct) =>
        {
            job.Report(0.5, "transcoding");
            gate.Wait(ct);
            return "imported clip";
        });
        var failing = jobs.Start("import", "broken import", (_, _) => throw new ZeyoCutException("ffmpeg could not read it"));
        var other = jobs.Start("analyze", "analysis", (_, _) => "done");

        Assert.Equal("import_1", first.Id);
        Assert.Equal("import_2", failing.Id);
        Assert.Equal("analyze_1", other.Id);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (first.Percent < 50 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(JobState.Running, first.State);
        Assert.Equal(50, first.Percent);
        Assert.Equal("transcoding", first.Stage);
        gate.Set();
        await Task.WhenAll(first.Completion, failing.Completion, other.Completion).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Completed, first.State);
        Assert.Equal(100, first.Percent);
        Assert.Equal("imported clip", first.Result);
        Assert.Equal(JobState.Failed, failing.State);
        Assert.Equal("ffmpeg could not read it", failing.Error);
        Assert.NotNull(failing.FinishedUtc);
        Assert.Same(other, jobs.Get("analyze_1"));
        Assert.Contains("job 'nope' not found; jobs: import_1, import_2, analyze_1", Assert.Throws<ZeyoCutException>(() => jobs.Get("nope")).Message);
    }
}
