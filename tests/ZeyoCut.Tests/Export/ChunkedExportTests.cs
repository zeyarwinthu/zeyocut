// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp.Demo;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class ChunkedExportTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // The MD5 of every decoded frame, in order.
    private static List<string> FrameHashes(FfmpegPaths ffmpeg, string video)
    {
        var result = ProcessRunner.Run(ffmpeg.Ffmpeg, ["-v", "error", "-i", video, "-map", "0:v", "-f", "framemd5", "-"], TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',')[^1].Trim()).ToList();
    }

    private static string[] Keys(Project project, string directory)
    {
        var keys = new FrameKeys(project, directory);
        return Enumerable.Range(0, FrameMath.FrameCount(project)).Select(keys.Of).ToArray();
    }

    private static string[] CachedChunks(string directory) =>
        Directory.GetFiles(Path.Combine(directory, "cache", "chunks")).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();

    // 160x120 at 10 fps for 8 s, so four chunks of 20 frames: a logo for the first 2 s (chunk 0), a
    // title that moves in during the first second and stays, and a label at 6.5–7.5 s (chunk 3).
    private static ProjectSession Timeline(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 160, height: 120, fps: 10, duration: 8));
        session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "src_logo.png"), 40, 40, new SKColor(220, 30, 30)), "logo", new AssetProbe()));
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["n"] = "logo" }, X = 40, Y = 40, End = 2 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Chunks", FontFamily = "Segoe UI", FontSize = 24, Color = "#202020", Y = 90 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "label", Text = "Late", FontFamily = "Segoe UI", FontSize = 20, Color = "#FFFF00", Start = 6.5, End = 7.5 });
            return KeyframeEditor.SetKeyframes(p, [new KeyframeEntry("title", "x", 0, Json(20)), new KeyframeEntry("title", "x", 1, Json(80))]);
        });
        return session;
    }

    [Fact]
    public void ChunksRenderedOnSeveralThreadsGiveTheSameFramesAsOnePass()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var directory = TestPaths.NewDirectory("chunked-same-frames");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 30, duration: 5));
        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(directory, "src_pattern.mp4"), seconds: 6), "pattern");
        var tone = Path.Combine(directory, "src_tone.wav");
        ToneGenerator.WriteTone(tone, 440, 3);
        session.Mutate("import", p => AssetImporter.Import(p, directory, tone, "tone", new AssetProbe(path => Ffprobe.AudioDuration(ffmpeg.Ffprobe, path))));
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "pattern", TrimIn = 1 });
            ProjectEditor.AddAudioLayer(p, new AudioLayerOptions { Asset = "tone", Start = 1 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Chunks", FontFamily = "Segoe UI", FontSize = 40, Color = "#FFFFFF" });
            EffectEditor.SetEffect(p, "title", EffectKind.Shadow, new EffectValues(null, 4, 4, 6, 0.5, null, null), remove: false);
            return KeyframeEditor.SetKeyframes(p, [new KeyframeEntry("title", "x", 0, Json(40)), new KeyframeEntry("title", "x", 5, Json(280))]);
        });
        var project = session.Snapshot();

        var single = Exporter.Export(project, directory, Path.Combine(directory, "exports", "single.mp4"), 0, ffmpeg, cancellationToken: ct);
        var chunked = Exporter.Export(project, directory, Path.Combine(directory, "exports", "chunked.mp4"), 0, ffmpeg, cancellationToken: ct, workers: 3);

        var expected = FrameHashes(ffmpeg, single.OutputPath);
        Assert.Equal(150, expected.Count);
        Assert.Equal(expected, FrameHashes(ffmpeg, chunked.OutputPath));
        Assert.True(chunked.Media.HasAudio);
        Assert.Equal(single.Media.Duration, chunked.Media.Duration, 0.05);
        Assert.Equal(single.Media.VideoFrames, chunked.Media.VideoFrames);
        Assert.Equal(single.Notes, chunked.Notes);
        Assert.Equal(["single.mp4", "chunked.mp4"], Directory.GetFiles(Path.Combine(directory, "exports")).Select(f => Path.GetFileName(f)).OrderDescending(StringComparer.Ordinal));

        var times = Assert.IsType<RenderTimes>(chunked.Times);
        Assert.Equal((150, 3, 3, 0), (times.Frames, times.Chunks, times.Workers, times.ReusedChunks));
        Assert.Equal([("clip", 150), ("title", 150)], times.Layers.Select(l => (l.Id, l.Frames)).OrderBy(l => l.Id));
        Assert.True(times.Stages["decode"] > 0 && times.Stages["encode"] > 0);
        Assert.True(times.RenderSeconds > 0 && times.KeySeconds > 0 && times.JoinSeconds > 0);
        Assert.True(times.EncoderCpuSeconds > 0);
        Assert.Equal(3, CachedChunks(directory).Length);
        Assert.All(CachedChunks(directory), name => Assert.Matches("^[0-9a-f]{32}\\.mp4$", name));
    }

    [Fact]
    public void AFittedPresetExportIsChunkedTheSameWay()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var directory = TestPaths.NewDirectory("chunked-fit");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 10, duration: 4));
        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(directory, "src_pattern.mp4"), seconds: 4, fps: 10), "pattern");
        session.Mutate("layers", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "pattern" }));
        var project = session.Snapshot();
        var preset = Presets.Get("shorts_1080p");

        var single = Exporter.Export(project, directory, Path.Combine(directory, "exports", "single.mp4"), 18, ffmpeg, cancellationToken: ct, preset: preset, fitShape: true);
        var chunked = Exporter.Export(project, directory, Path.Combine(directory, "exports", "chunked.mp4"), 18, ffmpeg, cancellationToken: ct, preset: preset, fitShape: true, workers: 2);

        Assert.Equal((2, 2), (chunked.Times!.Chunks, chunked.Times.Workers));
        Assert.Equal((1080, 1920, 40), (chunked.Media.Width, chunked.Media.Height, FrameHashes(ffmpeg, chunked.OutputPath).Count));
        Assert.Equal(single.Media.PixelFormat, chunked.Media.PixelFormat);
        Assert.Equal(single.Notes, chunked.Notes);
        foreach (var frame in new[] { 0, 19, 20, 39 })
        {
            var a = Ffmpeg.ExtractFrameRgba(ffmpeg, single.OutputPath, frame, 1080, 1920);
            var b = Ffmpeg.ExtractFrameRgba(ffmpeg, chunked.OutputPath, frame, 1080, 1920);
            var next = Ffmpeg.ExtractFrameRgba(ffmpeg, single.OutputPath, frame == 39 ? 38 : frame + 1, 1080, 1920);
            var same = Ffmpeg.MeanLumaError(a, b);
            var neighbour = Ffmpeg.MeanLumaError(next, b);
            Assert.True(same < 1 && neighbour > 3 * same, $"frame {frame}: {same:0.###} from the same frame of one pass, {neighbour:0.###} from the next");
        }
    }

    [Fact]
    public void AReExportRendersOnlyTheChunksThatChanged()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var session = Timeline("chunked-cache");
        var directory = session.Directory;
        var exports = Path.Combine(directory, "exports");

        (RenderTimes Times, List<string> Frames) Export(string name)
        {
            var project = session.Snapshot();
            var chunked = Exporter.Export(project, directory, Path.Combine(exports, name + ".mp4"), 0, ffmpeg, cancellationToken: ct, workers: 2);
            var frames = FrameHashes(ffmpeg, chunked.OutputPath);
            var single = Exporter.Export(project, directory, Path.Combine(exports, name + "_single.mp4"), 0, ffmpeg, cancellationToken: ct);
            Assert.Equal(FrameHashes(ffmpeg, single.OutputPath), frames);
            return (chunked.Times!, frames);
        }

        var first = Export("first");
        Assert.Equal((4, 0, 2), (first.Times.Chunks, first.Times.ReusedChunks, first.Times.Workers));
        Assert.Equal(80, first.Frames.Count);
        var cached = CachedChunks(directory);
        Assert.Equal(4, cached.Length);

        var again = Export("again");
        Assert.Equal((4, 4), (again.Times.Chunks, again.Times.ReusedChunks));
        Assert.All(again.Times.Stages.Values, seconds => Assert.Equal(0, seconds));
        Assert.Empty(again.Times.Layers);
        Assert.Equal(first.Frames, again.Frames);
        Assert.Equal(cached, CachedChunks(directory));

        session.Mutate("label", p => ProjectEditor.UpdateLayer(p, "label", new JsonObject { ["color"] = "#00FFFF" }));
        var recolored = Export("recolored");
        Assert.Equal(3, recolored.Times.ReusedChunks);
        Assert.Equal(["label", "title"], recolored.Times.Layers.Select(l => l.Id).Order(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(65, 10), Enumerable.Range(0, 80).Where(f => recolored.Frames[f] != first.Frames[f]));

        session.Mutate("title", p => KeyframeEditor.SetKeyframes(p, [new KeyframeEntry("title", "x", 0, Json(40))]));
        var moved = Export("moved");
        Assert.Equal(3, moved.Times.ReusedChunks);
        Assert.Equal(Enumerable.Range(0, 10), Enumerable.Range(0, 80).Where(f => moved.Frames[f] != recolored.Frames[f]));

        var logo = ProjectStore.AssetPath(directory, session.Snapshot().FindAsset("logo")!);
        TestImages.Solid(logo, 40, 40, new SKColor(30, 30, 220));
        File.SetLastWriteTimeUtc(logo, DateTime.UtcNow.AddMinutes(1));
        var redrawn = Export("redrawn");
        Assert.Equal(3, redrawn.Times.ReusedChunks);
        Assert.Equal(Enumerable.Range(0, 20), Enumerable.Range(0, 80).Where(f => redrawn.Frames[f] != moved.Frames[f]));
        Assert.DoesNotContain(CachedChunks(directory), name => !name.EndsWith(".mp4", StringComparison.Ordinal) || name.Contains(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public void AStoppedExportGoesOnFromTheChunksItFinished()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var session = Timeline("chunked-resume");
        var directory = session.Directory;
        var output = Path.Combine(directory, "exports", "resumed.mp4");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Assert.ThrowsAny<OperationCanceledException>(() => Exporter.Export(
            session.Snapshot(), directory, output, 0, ffmpeg, (frames, _) =>
            {
                if (frames >= 45)
                {
                    stop.Cancel();
                }
            },
            cancellationToken: stop.Token,
            workers: 1));
        Assert.Equal(2, CachedChunks(directory).Length);
        Assert.All(CachedChunks(directory), name => Assert.Matches("^[0-9a-f]{32}\\.mp4$", name));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "exports")));

        var reported = new List<int>();
        var resumed = Exporter.Export(session.Snapshot(), directory, output, 0, ffmpeg, (frames, _) => reported.Add(frames), cancellationToken: ct, workers: 1);
        Assert.Equal((4, 2), (resumed.Times!.Chunks, resumed.Times.ReusedChunks));
        Assert.Equal(Enumerable.Range(40, 41), reported);
        var single = Exporter.Export(session.Snapshot(), directory, Path.Combine(directory, "exports", "single.mp4"), 0, ffmpeg, cancellationToken: ct);
        Assert.Equal(FrameHashes(ffmpeg, single.OutputPath), FrameHashes(ffmpeg, resumed.OutputPath));
    }

    [Fact]
    public void ThreadsAreCappedByTheFreeMemory()
    {
        const long GB = 1024L * 1024 * 1024;
        Assert.Equal(4, Exporter.ThreadsFor(4, 1920 * 1080, 16 * GB));
        Assert.Equal(2, Exporter.ThreadsFor(4, 1920 * 1080, 4 * GB));
        Assert.Equal(2, Exporter.ThreadsFor(4, 3840 * 2160, 12 * GB));
        Assert.Equal(1, Exporter.ThreadsFor(4, 3840 * 2160, 4 * GB));
        Assert.Equal(1, Exporter.ThreadsFor(4, 3840 * 2160, 0));
        Assert.Equal(1, Exporter.ThreadsFor(0, 640 * 360, 16 * GB));
        Assert.InRange(Exporter.ThreadMemory(1920 * 1080), 0.9 * GB, 1.1 * GB);
    }

    [Fact]
    public async Task TwoExportsAtOnceShareTheCache()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var session = Timeline("chunked-together");
        var directory = session.Directory;
        var project = session.Snapshot();

        var exports = await Task.WhenAll(new[] { "a", "b" }.Select(name => Task.Run(
            () => Exporter.Export(project, directory, Path.Combine(directory, "exports", name + ".mp4"), 0, ffmpeg, cancellationToken: ct, workers: 2), ct)));

        Assert.Equal(FrameHashes(ffmpeg, exports[0].OutputPath), FrameHashes(ffmpeg, exports[1].OutputPath));
        Assert.Equal(80, FrameHashes(ffmpeg, exports[0].OutputPath).Count);
        Assert.Equal(4, CachedChunks(directory).Length);
        Assert.All(CachedChunks(directory), name => Assert.Matches("^[0-9a-f]{32}\\.mp4$", name));
    }

    [Fact]
    public void AFramesKeyChangesOnlyWithWhatThatFrameShows()
    {
        var directory = TestPaths.NewDirectory("frame-keys");
        var project = TestProjects.Empty(width: 64, height: 48, fps: 10, duration: 4);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "moving", Text = "A", FontFamily = "Segoe UI", FontSize = 20 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "late", Text = "B", FontFamily = "Segoe UI", FontSize = 20, Start = 2 });
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("moving", "x", 0, Json(10)), new KeyframeEntry("moving", "x", 1, Json(30)), new KeyframeEntry("moving", "x", 3, Json(50))]);

        var before = Keys(project, directory);
        Assert.Equal(before, Keys(project, directory));
        Assert.Equal(before[30], before[39]);
        Assert.NotEqual(before[5], before[6]);
        Assert.NotEqual(before[19], before[20]);

        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("moving", "x", 3, Json(60))]);
        var moved = Keys(project, directory);
        Assert.Equal(before[..11], moved[..11]);
        Assert.All(Enumerable.Range(11, 29), f => Assert.NotEqual(before[f], moved[f]));

        ProjectEditor.UpdateLayer(project, "late", new JsonObject { ["color"] = "#FF0000" });
        var recolored = Keys(project, directory);
        Assert.Equal(moved[..20], recolored[..20]);
        Assert.All(Enumerable.Range(20, 20), f => Assert.NotEqual(moved[f], recolored[f]));

        ProjectEditor.UpdateLayer(project, "late", new JsonObject { ["visible"] = false });
        var hidden = Keys(project, directory);
        Assert.Equal(recolored[..20], hidden[..20]);
        Assert.All(Enumerable.Range(20, 20), f => Assert.NotEqual(recolored[f], hidden[f]));
        ProjectEditor.UpdateLayer(project, "late", new JsonObject { ["text"] = "C" });
        Assert.Equal(hidden, Keys(project, directory));
    }

    [Fact]
    public void AClipsKeyFollowsItsSourceFrameAndItsFile()
    {
        Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("frame-keys-clip");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 64, height: 48, fps: 10, duration: 2));
        var asset = TestVideos.Import(session, TestVideos.Ramp(Path.Combine(directory, "src_ramp.mp4"), seconds: 4, fps: 10), "ramp");
        session.Mutate("clip", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "ramp" }));

        var before = Keys(session.Snapshot(), directory);
        Assert.Equal(40, before.Distinct().Count());

        session.Mutate("trim", p => ProjectEditor.UpdateLayer(p, "clip", new JsonObject { ["trimIn"] = 1 }));
        var trimmed = Keys(session.Snapshot(), directory);
        Assert.Empty(before.Intersect(trimmed));
        session.Mutate("trim", p => ProjectEditor.UpdateLayer(p, "clip", new JsonObject { ["trimIn"] = 0 }));
        Assert.Equal(before, Keys(session.Snapshot(), directory));

        File.SetLastWriteTimeUtc(ProjectStore.AssetPath(directory, asset), DateTime.UtcNow.AddMinutes(1));
        Assert.Empty(before.Intersect(Keys(session.Snapshot(), directory)));
    }
}
