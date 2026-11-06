// SPDX-License-Identifier: MIT

using System.Text.Json;
using SkiaSharp;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class VideoTests
{
    private readonly ITestOutputHelper _output;

    public VideoTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static KeyframeEntry Key(string layer, string property, double time, object value) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value));

    private static byte Gray(SKImage image, int x, int y) => TestImages.Pixel(image, x, y).Red;

    [Fact]
    public void ProbeReadsGeneratedClip()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("video-probe");
        var clip = TestVideos.Ramp(Path.Combine(directory, "ramp.mp4"), seconds: 4, withAudio: true);

        var probe = VideoProbe.Probe(ffmpeg.Ffprobe, clip);
        Assert.Equal(64, probe.Width);
        Assert.Equal(48, probe.Height);
        Assert.Equal("30", probe.FrameRate);
        Assert.Equal(120, probe.FrameCount);
        Assert.Equal(4, probe.Duration, 6);
        Assert.Equal("h264", probe.Codec);
        Assert.True(probe.HasAudio);
        Assert.Equal(0, probe.Rotation);
        Assert.Equal(8, probe.BitDepth);
        Assert.Empty(probe.NormalizeReasons());
    }

    [Fact]
    public void SeekingDecodesTheRequestedFrame()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("video-seek");
        var clip = TestVideos.Ramp(Path.Combine(directory, "ramp.mp4"));
        var asset = TestVideos.AssetFor(clip);

        using var source = new VideoFrameSource(() => ffmpeg.Ffmpeg, stream: false);
        foreach (var frame in new[] { 0, 1, 29, 30, 31, 59, 77, 118, 119 })
        {
            var image = source.GetFrame("clip", clip, asset, frame, 64, 48);
            Assert.Equal(frame, TestVideos.FrameCode(image));
        }

        Assert.Equal(9, source.DecoderStarts);
        source.GetFrame("clip", clip, asset, 77, 64, 48);
        Assert.Equal(9, source.DecoderStarts);
    }

    [Fact]
    public void StreamingAndSeekingDecodeIdenticalPixels()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("video-stream");
        var clip = TestVideos.Pattern(Path.Combine(directory, "pattern.mp4"), seconds: 4, width: 160, height: 120);
        var asset = TestVideos.AssetFor(clip);
        int[] checks = [0, 1, 13, 29, 30, 47, 89, 119];

        var streamed = new Dictionary<int, byte[]>();
        using (var stream = new VideoFrameSource(() => ffmpeg.Ffmpeg, stream: true))
        {
            for (var frame = 0; frame < 120; frame++)
            {
                var image = stream.GetFrame("layer_a", clip, asset, frame, 160, 120);
                if (checks.Contains(frame))
                {
                    streamed[frame] = TestImages.Pixels(image);
                }
            }

            Assert.Equal(1, stream.DecoderStarts);
        }

        var skipping = new Dictionary<int, byte[]>();
        using (var fast = new VideoFrameSource(() => ffmpeg.Ffmpeg, stream: true))
        {
            for (var frame = 13; frame < 120; frame += 17)
            {
                skipping[frame] = TestImages.Pixels(fast.GetFrame("fast", clip, asset, frame, 160, 120));
            }

            Assert.Equal(1, fast.DecoderStarts);
        }

        using var seek = new VideoFrameSource(() => ffmpeg.Ffmpeg, stream: false);
        foreach (var frame in checks)
        {
            Assert.True(streamed[frame].SequenceEqual(TestImages.Pixels(seek.GetFrame("x", clip, asset, frame, 160, 120))), $"frame {frame} differs between streaming and seeking");
        }

        foreach (var (frame, pixels) in skipping)
        {
            Assert.True(pixels.SequenceEqual(TestImages.Pixels(seek.GetFrame("x", clip, asset, frame, 160, 120))), $"skipped-to frame {frame} differs from the seeked frame");
        }

        Assert.False(streamed[29].SequenceEqual(streamed[30]), "neighbouring frames of the pattern should differ");
    }

    [Fact]
    public void VideoLayerFollowsStartTrimAndSpeed()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("video-layer-timing");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 64, height: 48, fps: 30, duration: 4));
        var clip = TestVideos.Ramp(Path.Combine(directory, "src_ramp.mp4"));
        TestVideos.Import(session, clip, "ramp");
        var layer = session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Asset = "ramp", Start = 0.5, TrimIn = 1, Speed = 2 }));
        Assert.Equal(2, layer.TimelineEnd, 9);
        Assert.Equal(1, layer.ScaleX.Value);
        var project = session.Snapshot();

        using var preview = new FrameRenderer(directory, () => ffmpeg.Ffmpeg);
        using var export = new FrameRenderer(directory, () => ffmpeg.Ffmpeg, streamVideo: true);
        foreach (var (frame, expected) in new (int Frame, int? SourceFrame)[] { (12, null), (15, 30), (30, 60), (45, 90), (59, 118), (60, null) })
        {
            using var a = preview.RenderImage(project, frame);
            using var b = export.RenderImage(project, frame);
            if (expected is { } source)
            {
                Assert.Equal(source, TestVideos.FrameCode(a));
            }
            else
            {
                Assert.Equal(SKColors.White, TestImages.Pixel(a, 16, 24));
                Assert.Equal(SKColors.White, TestImages.Pixel(a, 48, 24));
            }

            Assert.True(TestImages.Pixels(a).SequenceEqual(TestImages.Pixels(b)), $"timeline frame {frame}: preview and export renders differ");
        }

        Assert.Equal(1, export.Video.DecoderStarts);
    }

    [Fact]
    public void CropShowsThePartOfTheSourceAtCropX()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("video-crop");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 64, height: 48, fps: 30, duration: 1));
        session.Mutate("bg", p => p.BackgroundColor = "#FF0000");
        TestVideos.Import(session, TestVideos.Halves(Path.Combine(directory, "src_halves.mp4")), "halves");
        session.Mutate("add", p =>
        {
            var added = ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Asset = "halves", CropWidth = 32, CropHeight = 48 });
            Assert.Equal(16, added.AnchorX);
            Assert.Equal(1, added.ScaleX.Value);
            return KeyframeEditor.SetKeyframes(p, [Key(added.Id, "cropX", 0, 0), Key(added.Id, "cropX", 0.5, 32)]);
        });
        var project = session.Snapshot();

        using var renderer = new FrameRenderer(directory, () => ffmpeg.Ffmpeg);
        using var start = renderer.RenderImage(project, 0);
        using var middle = renderer.RenderImage(project, 15);
        Assert.Equal(new SKColor(255, 0, 0), TestImages.Pixel(start, 8, 24));
        Assert.InRange(Gray(start, 32, 24), (byte)0, (byte)3);
        Assert.InRange(Gray(middle, 32, 24), (byte)252, (byte)255);
        Assert.Equal(new SKColor(255, 0, 0), TestImages.Pixel(middle, 56, 24));
    }

    [Fact]
    public void ReframeToVerticalShowsTheSliceAtFocusX()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("video-reframe");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 160, height: 120, fps: 30, duration: 1));
        session.Mutate("bg", p => p.BackgroundColor = "#FF0000");
        TestVideos.Import(session, TestVideos.Halves(Path.Combine(directory, "src_halves.mp4"), width: 160, height: 120), "halves");
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "halves" }));
        session.Mutate("vertical", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Width = 90, Height = 160 }));

        using var renderer = new FrameRenderer(directory, () => ffmpeg.Ffmpeg);
        SKImage Reframed(double focusX)
        {
            var result = session.Mutate("reframe", p => Reframe.ToVertical(p, "all", focusX, out _)).Single();
            Assert.Equal((68.0, 120.0), (result.CropWidth, result.CropHeight));
            Assert.Equal(160 / 120.0, result.Scale, 9);
            return renderer.RenderImage(session.Snapshot(), 10);
        }

        using var left = Reframed(0.25);
        using var right = Reframed(0.75);
        foreach (var (x, y) in new[] { (1, 1), (45, 80), (88, 158) })
        {
            Assert.InRange(Gray(left, x, y), (byte)0, (byte)3);
            Assert.InRange(Gray(right, x, y), (byte)252, (byte)255);
            Assert.Equal(TestImages.Pixel(left, x, y).Red, TestImages.Pixel(left, x, y).Green);
        }
    }

    [Fact]
    public void ColorAdjustmentsChangeImageLayers()
    {
        var directory = TestPaths.NewDirectory("color-adjust");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 40, height: 40, fps: 10, duration: 1));
        var swatch = TestImages.Solid(Path.Combine(directory, "src_swatch.png"), 40, 40, new SKColor(200, 100, 50));
        session.Mutate("import", p => AssetImporter.Import(p, directory, swatch, "swatch", new AssetProbe()));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "swatch", Drawings = new Dictionary<string, string> { ["n"] = "swatch" } }));

        using var renderer = new FrameRenderer(directory);
        SKColor Render(Action<ImageLayer> change)
        {
            var project = session.Snapshot();
            change((ImageLayer)project.FindLayer("swatch")!);
            using var image = renderer.RenderImage(project, 5);
            return TestImages.Pixel(image, 20, 20);
        }

        Assert.Equal(new SKColor(200, 100, 50), Render(_ => { }));
        Assert.Equal(new SKColor(0, 0, 0), Render(l => l.Brightness.Value = -1));
        Assert.Equal(new SKColor(255, 255, 255), Render(l => l.Brightness.Value = 1));

        var gray = Render(l => l.Saturation.Value = 0);
        Assert.Equal(gray.Red, gray.Green);
        Assert.Equal(gray.Green, gray.Blue);
        Assert.InRange(gray.Red, (byte)116, (byte)120);

        var flat = Render(l => l.Contrast.Value = 0);
        Assert.InRange(flat.Red, (byte)127, (byte)128);
        Assert.Equal(flat.Red, flat.Blue);

        var fading = Render(l =>
        {
            l.Brightness.Keyframes.Add(new Keyframe { Time = 0, Value = 0 });
            l.Brightness.Keyframes.Add(new Keyframe { Time = 1, Value = -1 });
        });
        Assert.InRange(fading.Red, (byte)70, (byte)75);
        Assert.Equal(0, fading.Blue);
    }

    [Fact]
    public void AudioClipsIncludeVideoSoundWithTempo()
    {
        var project = TestProjects.Empty(duration: 10);
        project.Assets.Add(new Asset { Id = "talk", Type = AssetType.Video, File = "assets/talk.mp4", Width = 64, Height = 48, FrameRate = "30", FrameCount = 300, Duration = 10, HasAudio = true });
        project.Assets.Add(new Asset { Id = "silent", Type = AssetType.Video, File = "assets/silent.mp4", Width = 64, Height = 48, FrameRate = "30", FrameCount = 300, Duration = 10, HasAudio = false });
        project.Layers.Add(new VideoLayer { Id = "fast", Asset = "talk", Start = 1, TrimIn = 2, TrimOut = 6, Speed = 2, Volume = 0.5, FadeOut = 0.25 });
        project.Layers.Add(new VideoLayer { Id = "slow", Asset = "talk", Start = 0, TrimIn = 0, TrimOut = 1, Speed = 0.25 });
        project.Layers.Add(new VideoLayer { Id = "muted", Asset = "talk", TrimOut = 5, Muted = true });
        project.Layers.Add(new VideoLayer { Id = "no_sound", Asset = "silent", TrimOut = 5 });
        project.Layers.Add(new VideoLayer { Id = "cut", Asset = "talk", Start = 8, TrimOut = 10 });

        var clips = FfmpegArgs.AudioClips(project, @"C:\p").ToDictionary(c => c.LayerId);
        Assert.Equal(["fast", "slow", "cut"], clips.Keys);
        Assert.Equal(2, clips["fast"].Length, 9);
        Assert.Equal(2, clips["fast"].Speed);
        Assert.Equal(4, clips["slow"].Length, 9);
        Assert.Equal(2, clips["cut"].Length, 9);

        var args = FfmpegArgs.Build(new EncodeSettings(640, 360, 30, 300, 18), clips.Values.ToList(), "out.mp4");
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[1:a]atrim=start=2:duration=4,asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,atempo=2,volume=0.5,afade=t=out:st=1.75:d=0.25,adelay=1000:all=1[a1]", filter);
        Assert.Contains("[2:a]atrim=start=0:duration=1,asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,atempo=0.5,atempo=0.5,volume=1[a2]", filter);
        Assert.Equal(["atempo=0.5", "atempo=0.5", "atempo=0.5", "atempo=0.8"], FfmpegArgs.Tempo(0.1));
        Assert.Empty(FfmpegArgs.Tempo(1));
    }

    [Fact]
    public void ExportedVideoFrameMatchesRenderFrame()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("video-export-match");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 30, duration: 2));
        session.Mutate("bg", p => p.BackgroundColor = "#203040");
        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(directory, "src_pattern.mp4"), seconds: 3, withAudio: true), "pattern");
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "pattern", TrimIn = 0.5, TrimOut = 2.5, Rotation = 4, ScaleX = 0.85, ScaleY = 0.85 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "label", Text = "ZeyoCut", FontSize = 26, Color = "#FFFFFF", StrokeColor = "#000000", StrokeWidth = 2, X = 10, Y = 200, AnchorX = 0, AnchorY = 0 });
            return KeyframeEditor.SetKeyframes(p, [Key("clip", "brightness", 0, 0), Key("clip", "brightness", 2, -0.4), Key("clip", "x", 0, 150), Key("clip", "x", 2, 170)]);
        });
        var project = session.Snapshot();
        var output = Path.Combine(directory, "exports", "video.mp4");

        var result = Exporter.Export(project, directory, output, 18, ffmpeg, cancellationToken: TestContext.Current.CancellationToken);
        _output.WriteLine(ExportSummary.Describe(result));
        Assert.Equal(60, result.Media.VideoFrames);
        Assert.True(result.Media.HasAudio);
        Assert.Equal(1, result.AudioClips);
        Assert.InRange(result.Media.Duration, 1.99, 2.05);

        using var renderer = new FrameRenderer(directory, () => ffmpeg.Ffmpeg);
        double ErrorAgainst(int renderedFrame, byte[] decoded)
        {
            using var image = renderer.RenderImage(project, renderedFrame);
            return Ffmpeg.MeanLumaError(TestImages.Pixels(image), decoded);
        }

        foreach (var frame in new[] { 7, 31 })
        {
            var decoded = Ffmpeg.ExtractFrameRgba(ffmpeg, output, frame, 320, 240);
            var same = ErrorAgainst(frame, decoded);
            var before = ErrorAgainst(frame - 1, decoded);
            var after = ErrorAgainst(frame + 1, decoded);
            _output.WriteLine($"frame {frame}: mean luma error {same:0.000}, neighbours {before:0.000} / {after:0.000}");
            Assert.True(same < 2, $"exported frame {frame} differs from render_frame by {same:0.00}/255 luma on average");
            Assert.True(same < 0.6 * Math.Min(before, after), $"exported frame {frame} should match render frame {frame} clearly better than its neighbours");
        }
    }
}
