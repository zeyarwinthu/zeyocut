// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp;
using ZeyoCut.Mcp.Demo;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class ExportTests
{
    private readonly ITestOutputHelper _output;

    public ExportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static ProjectSession MovingSquareProject(string name, FfmpegPaths? ffmpeg, bool withAudio)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 30, duration: 1));
        session.Mutate("background", p => p.BackgroundColor = "#808080");
        var probe = new AssetProbe(ffmpeg is null ? null : path => Ffprobe.AudioDuration(ffmpeg.Ffprobe, path));

        var red = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "src_red.png"), 40, 40, new SKColor(220, 30, 30)), "red", probe));
        var split = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.SplitLeftRight(Path.Combine(directory, "src_split.png"), 60, 60, new SKColor(20, 160, 60), new SKColor(250, 220, 40)), "split", probe));
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "panel", Drawings = new Dictionary<string, string> { ["n"] = split.Id }, X = 160, Y = 170, Rotation = 10 });
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "square", Drawings = new Dictionary<string, string> { ["n"] = red.Id }, Y = 80 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "label", Text = "ZeyoCut", FontFamily = "Segoe UI", FontSize = 28, Color = "#FFFFFF", StrokeColor = "#000000", StrokeWidth = 2, X = 10, Y = 200, AnchorX = 0, AnchorY = 0 });
            return KeyframeEditor.SetKeyframes(p,
            [
                new KeyframeEntry("square", "x", 0, Json(20)),
                new KeyframeEntry("square", "x", 1, Json(300)),
                new KeyframeEntry("square", "rotation", 0, Json(0), "easeInOut"),
                new KeyframeEntry("square", "rotation", 1, Json(180)),
            ]);
        });

        if (withAudio)
        {
            var tone = Path.Combine(directory, "src_tone.wav");
            ToneGenerator.WriteTone(tone, 440, 0.5);
            var asset = session.Mutate("import", p => AssetImporter.Import(p, directory, tone, "tone", probe));
            Assert.InRange(asset.Duration!.Value, 0.49, 0.52);
            session.Mutate("audio", p => ProjectEditor.AddAudioLayer(p, new AudioLayerOptions { Asset = "tone", Start = 0.25, Volume = 0.8, FadeOut = 0.1 }));
        }

        return session;
    }

    [Fact]
    public void BuildsExpectedFfmpegCommand()
    {
        var clips = new List<AudioClip>
        {
            new("boing_1", @"C:\p\assets\boing.wav", 0.7, 0, 0.5, 0.8, 0, 0.1),
            new("music", @"C:\p\assets\music.mp3", 0, 2, 3, 0.5, 1, 1),
        };
        var args = FfmpegArgs.Build(new EncodeSettings(1280, 720, 30, 90, 18), clips, @"C:\p\exports\out.mp4");
        var line = string.Join(" ", args);

        Assert.Contains("-f rawvideo -pix_fmt rgba -s 1280x720 -framerate 30 -i pipe:0", line);
        Assert.Contains(@"-i C:\p\assets\boing.wav -i C:\p\assets\music.mp3", line);
        Assert.Contains("-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p", line);
        Assert.Contains("-t 3 -movflags +faststart", line);

        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[1:a]atrim=start=0:duration=0.5,asetpts=PTS-STARTPTS", filter);
        Assert.Contains("volume=0.8,afade=t=out:st=0.4:d=0.1,adelay=700:all=1[a1]", filter);
        Assert.Contains("[2:a]atrim=start=2:duration=3", filter);
        Assert.Contains("afade=t=in:st=0:d=1,afade=t=out:st=2:d=1[a2]", filter);
        Assert.Contains("[a1][a2]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,apad[aout]", filter);
        Assert.Equal("[aout]", args[args.LastIndexOf("-map") + 1]);

        var silent = FfmpegArgs.Build(new EncodeSettings(640, 360, 24, 48, 23), [], "out.mp4");
        Assert.DoesNotContain("-c:a", silent);
        Assert.DoesNotContain("[aout]", silent);
    }

    [Fact]
    public void AudioClipsRespectTrimEndAndVisibility()
    {
        var project = TestProjects.Empty(duration: 3);
        project.Assets.Add(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.mp3", Duration = 10 });
        project.Layers.Add(new AudioLayer { Id = "a", Asset = "music", Start = 1, TrimIn = 2 });
        project.Layers.Add(new AudioLayer { Id = "b", Asset = "music", Start = 0, TrimIn = 9.5 });
        project.Layers.Add(new AudioLayer { Id = "c", Asset = "music", Start = 0.5, End = 1.25, TrimIn = 0, TrimOut = 4 });
        project.Layers.Add(new AudioLayer { Id = "muted", Asset = "music", Visible = false });
        project.Layers.Add(new AudioLayer { Id = "late", Asset = "music", Start = 3 });

        var clips = FfmpegArgs.AudioClips(project, @"C:\p").ToDictionary(c => c.LayerId);
        Assert.Equal(["a", "b", "c"], clips.Keys);
        Assert.Equal(2, clips["a"].Length, 6);
        Assert.Equal(0.5, clips["b"].Length, 6);
        Assert.Equal(0.75, clips["c"].Length, 6);
        Assert.Equal(@"C:\p\assets\music.mp3", clips["a"].Path);
    }

    [Fact]
    public void ExportedFrameMatchesRenderedFrame()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = MovingSquareProject("export-frame-match", ffmpeg, withAudio: true);
        var project = session.Snapshot();
        var output = Path.Combine(session.Directory, "exports", "match.mp4");

        var result = Exporter.Export(project, session.Directory, output, 18, ffmpeg, cancellationToken: TestContext.Current.CancellationToken);
        _output.WriteLine(ExportSummary.Describe(result));

        Assert.Equal(320, result.Media.Width);
        Assert.Equal(240, result.Media.Height);
        Assert.Equal(30, result.Media.Fps);
        Assert.Equal("h264", result.Media.VideoCodec);
        Assert.Equal("yuv420p", result.Media.PixelFormat);
        Assert.Equal(30, result.Media.VideoFrames);
        Assert.True(result.Media.HasAudio);
        Assert.Equal("aac", result.Media.AudioCodec);
        Assert.InRange(result.Media.Duration, 0.99, 1.05);
        Assert.False(File.Exists(Path.Combine(session.Directory, "exports", "match.partial.mp4")));

        using var renderer = new FrameRenderer(session.Directory);
        double ErrorAgainst(int renderedFrame, byte[] decoded)
        {
            using var image = renderer.RenderImage(project, renderedFrame);
            return Ffmpeg.MeanAbsoluteError(TestImages.Pixels(image), decoded);
        }

        var decoded = Ffmpeg.ExtractFrameRgba(ffmpeg, output, 15, 320, 240);
        var same = ErrorAgainst(15, decoded);
        var before = ErrorAgainst(14, decoded);
        var after = ErrorAgainst(16, decoded);
        _output.WriteLine($"mean abs error: frame15={same:0.000} frame14={before:0.000} frame16={after:0.000}");

        Assert.True(same < 2.5, $"exported frame 15 differs from the render by {same:0.00}/255 on average");
        Assert.True(same < before && same < after, "frame 15 of the MP4 should match render frame 15 better than its neighbours");
    }

    [Fact]
    public async Task ExportJobRunsInBackground()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = MovingSquareProject("export-job", ffmpeg, withAudio: false);
        var manager = new ExportJobManager();
        var job = manager.Start(session.Snapshot(), session.Directory, Path.Combine(session.Directory, "exports", "job.mp4"), 23, ffmpeg);

        Assert.Equal("export_1", job.Id);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(ExportState.Completed, job.State);
        Assert.Equal(100, job.Percent);
        Assert.False(job.Result!.Media.HasAudio);
        Assert.Same(job, manager.Get("export_1"));
        Assert.Contains("export job 'nope' not found; jobs: export_1", Assert.Throws<ZeyoCut.Core.ZeyoCutException>(() => manager.Get("nope")).Message);
    }

    [Fact]
    public async Task WaitingForAStuckFfmpegGivesUpOrCancels()
    {
        var ffmpeg = Ffmpeg.Require();
        using (var stuck = Process.Start(ProcessRunner.StartInfo(ffmpeg.Ffmpeg, ["-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "anullsrc", "-f", "null", "-"]))!)
        {
            try
            {
                var waited = Stopwatch.StartNew();
                Assert.False(ProcessRunner.WaitForExit(stuck, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
                Assert.InRange(waited.Elapsed.TotalSeconds, 0.9, 5);
                Assert.False(stuck.HasExited);

                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                Assert.Throws<OperationCanceledException>(() => ProcessRunner.WaitForExit(stuck, TimeSpan.FromMinutes(1), cancel.Token));
                Assert.False(stuck.HasExited);
            }
            finally
            {
                stuck.Kill(entireProcessTree: true);
                stuck.WaitForExit();
            }
        }

        using var quick = Process.Start(ProcessRunner.StartInfo(ffmpeg.Ffmpeg, ["-hide_banner", "-version"]))!;
        var output = quick.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(ProcessRunner.WaitForExit(quick, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.StartsWith("ffmpeg version", await output);
    }

    [Fact]
    public void CliExportWritesMp4()
    {
        Ffmpeg.Require();
        var session = MovingSquareProject("export-cli", null, withAudio: false);
        var output = new StringWriter();
        var error = new StringWriter();
        var target = Path.Combine(session.Directory, "cli.mp4");

        var exit = Cli.Run(["export", session.Directory, target, "--crf", "28"], output, error);

        Assert.True(exit == 0, error.ToString());
        Assert.Contains("320x240 @ 30 fps", output.ToString());
        Assert.Contains("rendering 100%", error.ToString());
        Assert.True(File.Exists(target));
    }
}
