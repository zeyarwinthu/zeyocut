// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class VideoEncoderTests
{
    private static string Line(IEnumerable<string> arguments) => string.Join(" ", arguments);

    [Fact]
    public void AutoTakesTheFirstGpuEncoderThatWorksAndRemembersIt()
    {
        var path = Path.Combine(TestPaths.NewDirectory("encoder-choice"), "library", VideoEncoders.FileName);
        var tried = new List<string>();
        string? Test(VideoEncoder encoder)
        {
            tried.Add(encoder.Name);
            return encoder.Kind == VideoEncoderKind.Qsv ? null : $"no {encoder.Name} here";
        }

        var choice = VideoEncoders.Choose(null, 18, path, "laptop", "ffmpeg 8.1", Test);
        Assert.Equal(VideoEncoder.Qsv, choice.Encoder);
        Assert.Equal("the first GPU encoder that works on this computer", choice.Why);
        Assert.Equal(["nvenc", "qsv"], tried);
        Assert.True(File.Exists(path));

        tried.Clear();
        Assert.Equal(VideoEncoder.Qsv, VideoEncoders.Choose(null, 23, path, "laptop", "ffmpeg 8.1", Test).Encoder);
        Assert.Empty(tried);

        Assert.Equal(VideoEncoder.X264, VideoEncoders.Choose(VideoEncoder.Nvenc, 18, path, "laptop", "ffmpeg 8.1", Test).Encoder);
        Assert.Empty(tried);
        Assert.Equal(VideoEncoder.Qsv, VideoEncoders.Choose(VideoEncoder.Qsv, 18, path, "laptop", "ffmpeg 8.1", Test).Encoder);
        Assert.Equal(VideoEncoder.X264, VideoEncoders.Choose(VideoEncoder.Amf, 18, path, "laptop", "ffmpeg 8.1", Test).Encoder);
        Assert.Equal(["amf"], tried);

        tried.Clear();
        VideoEncoders.Choose(null, 18, path, "laptop", "ffmpeg 8.2", Test);
        Assert.Equal(["nvenc", "qsv"], tried);
        tried.Clear();
        VideoEncoders.Choose(null, 18, path, "desktop", "ffmpeg 8.2", Test);
        Assert.Equal(["nvenc", "qsv"], tried);
    }

    [Fact]
    public void Libx264IsUsedWhenNoGpuEncoderWorksOrForLosslessExports()
    {
        var path = Path.Combine(TestPaths.NewDirectory("encoder-fallback"), VideoEncoders.FileName);
        var tried = new List<string>();
        string? Fail(VideoEncoder encoder)
        {
            tried.Add(encoder.Name);
            return encoder.Kind switch
            {
                VideoEncoderKind.Nvenc => "Cannot load nvcuda.dll",
                VideoEncoderKind.Qsv => "Error creating a MFX session: -9.",
                _ => "DLL amfrt64.dll failed to open",
            };
        }

        var none = VideoEncoders.Choose(null, 18, path, "laptop", "ffmpeg", Fail);
        Assert.Equal(VideoEncoder.X264, none.Encoder);
        Assert.Equal("no GPU encoder works on this computer (nvenc: Cannot load nvcuda.dll; qsv: Error creating a MFX session: -9.; amf: DLL amfrt64.dll failed to open)", none.Why);
        Assert.Equal(["nvenc", "qsv", "amf"], tried);

        var named = VideoEncoders.Choose(VideoEncoder.Nvenc, 18, path, "laptop", "ffmpeg", Fail);
        Assert.Equal(VideoEncoder.X264, named.Encoder);
        Assert.Equal("the VideoEncoder setting is nvenc, but h264_nvenc doesn't work on this computer (Cannot load nvcuda.dll), so libx264 was used", named.Why);

        tried.Clear();
        Assert.Equal(new EncoderChoice(VideoEncoder.X264, "the VideoEncoder setting is x264"), VideoEncoders.Choose(VideoEncoder.X264, 18, Path.Combine(path, "unused.json"), "laptop", "ffmpeg", Fail));
        Assert.Equal(new EncoderChoice(VideoEncoder.X264, "CRF 0 (lossless) always uses libx264"), VideoEncoders.Choose(VideoEncoder.Qsv, 0, path, "laptop", "ffmpeg", Fail));
        Assert.Empty(tried);
    }

    [Fact]
    public void TheSettingNamesAnEncoder()
    {
        Assert.Null(VideoEncoders.Parse(null));
        Assert.Null(VideoEncoders.Parse(" "));
        Assert.Null(VideoEncoders.Parse("Auto"));
        Assert.Equal(VideoEncoder.Qsv, VideoEncoders.Parse("QSV"));
        Assert.Equal(VideoEncoder.Nvenc, VideoEncoders.Parse("h264_nvenc"));
        Assert.Equal(VideoEncoder.Amf, VideoEncoders.Parse("amf"));
        Assert.All(new[] { "x264", "libx264", "cpu" }, name => Assert.Equal(VideoEncoder.X264, VideoEncoders.Parse(name)));
        var error = Assert.Throws<ZeyoCutException>(() => VideoEncoders.Parse("vaapi"));
        Assert.Equal("unknown video encoder 'vaapi'; use auto, x264, nvenc, qsv or amf", error.Message);
    }

    [Fact]
    public void EachEncoderGetsItsOwnArgumentsAtTheSameQuality()
    {
        var plain = new EncodeSettings(1280, 720, 30, 90, 18);
        var delivery = new EncodeSettings(1920, 1080, 30, 90, 20, "medium", ["-profile:v", "high", "-bf", "2", "-g", "15"]);

        var x264 = Line(FfmpegArgs.Build(plain, [], "out.mp4"));
        Assert.Contains("format=yuv420p,setparams=", x264);
        Assert.Contains(" -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -colorspace bt709 ", x264);
        Assert.Equal(x264, Line(FfmpegArgs.Build(plain with { Encoder = VideoEncoder.X264 }, [], "out.mp4")));

        var qsv = Line(FfmpegArgs.Build(plain with { Encoder = VideoEncoder.Qsv }, [], "out.mp4"));
        Assert.Contains("format=nv12,setparams=", qsv);
        Assert.Contains(" -c:v h264_qsv -preset veryfast -global_quality 19 -pix_fmt nv12 -colorspace bt709 ", qsv);
        Assert.DoesNotContain("-crf", qsv);
        Assert.Contains(" -c:v h264_qsv -preset medium -global_quality 21 -pix_fmt nv12 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -profile:v high -bf 2 -g 15 ",
            Line(FfmpegArgs.Build(delivery with { Encoder = VideoEncoder.Qsv }, [], "out.mp4")));

        Assert.Contains(" -c:v h264_nvenc -preset p6 -tune hq -rc vbr -cq 21 -b:v 0 -pix_fmt yuv420p ", Line(FfmpegArgs.Build(delivery with { Encoder = VideoEncoder.Nvenc }, [], "out.mp4")));
        Assert.Contains(" -c:v h264_amf -quality balanced -rc cqp -qp_i 19 -qp_p 21 -qp_b 23 -pix_fmt nv12 ", Line(FfmpegArgs.Build(plain with { Encoder = VideoEncoder.Amf }, [], "out.mp4")));

        Assert.Equal("h264_qsv (Intel Quick Sync, global_quality 19)", VideoEncoder.Qsv.Describe(18));
        Assert.Equal("libx264 (CPU, CRF 18)", VideoEncoder.X264.Describe(18));
    }

    [Fact]
    public void AProblemIsFfmpegsFirstMessage()
    {
        Assert.Equal("Cannot load nvcuda.dll", VideoEncoders.Problem("[h264_nvenc @ 00000279f33bb940] Cannot load nvcuda.dll\n[vost#0:0/h264_nvenc @ 00000279f33b72c0] [enc:h264_nvenc @ 00000279f33b89c0] Error while opening encoder\n", 1));
        Assert.Equal("ffmpeg exit code 171", VideoEncoders.Problem("\n", 171));
    }

    [Fact]
    public void QuickSyncExportsInChunksWhereItWorks()
    {
        var ffmpeg = Ffmpeg.Require();
        Assert.SkipWhen(VideoEncoders.Try(ffmpeg, VideoEncoder.Qsv) is { } problem && problem.Length > 0, "Intel Quick Sync doesn't work on this computer");
        var ct = TestContext.Current.CancellationToken;
        var directory = TestPaths.NewDirectory("encoder-qsv");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 30, duration: 4));
        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(directory, "src_pattern.mp4"), seconds: 4), "pattern");
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "pattern" });
            return ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "GPU", FontFamily = "Segoe UI", FontSize = 40, Color = "#FFFFFF" });
        });
        var project = session.Snapshot();

        var cpu = Exporter.Export(project, directory, Path.Combine(directory, "exports", "cpu.mp4"), 18, ffmpeg, cancellationToken: ct, workers: 2);
        var gpu = Exporter.Export(project, directory, Path.Combine(directory, "exports", "gpu.mp4"), 18, ffmpeg, cancellationToken: ct, workers: 2, encoder: new EncoderChoice(VideoEncoder.Qsv, "a test"));

        Assert.Equal((2, 0), (gpu.Times!.Chunks, gpu.Times.ReusedChunks));
        Assert.Contains("encoder: h264_qsv (Intel Quick Sync, global_quality 19): a test", gpu.Notes!);
        Assert.DoesNotContain(cpu.Notes!, n => n.StartsWith("encoder:", StringComparison.Ordinal));
        Assert.Equal(("h264", "yuv420p", 120), (gpu.Media.VideoCodec, gpu.Media.PixelFormat, gpu.Media.VideoFrames));
        foreach (var frame in new[] { 0, 59, 60, 119 })
        {
            var error = Ffmpeg.MeanLumaError(Ffmpeg.ExtractFrameRgba(ffmpeg, cpu.OutputPath, frame, 320, 240), Ffmpeg.ExtractFrameRgba(ffmpeg, gpu.OutputPath, frame, 320, 240));
            Assert.True(error < 2, $"frame {frame}: Quick Sync is {error:0.##} from libx264");
        }

        var again = Exporter.Export(project, directory, Path.Combine(directory, "exports", "gpu2.mp4"), 18, ffmpeg, cancellationToken: ct, workers: 2, encoder: new EncoderChoice(VideoEncoder.Qsv, "a test"));
        Assert.Equal(2, again.Times!.ReusedChunks);
    }
}
