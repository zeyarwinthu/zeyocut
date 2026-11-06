// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class RenderStatsTests
{
    [Fact]
    public void AnExportMeasuresItsStagesLayersAndSlowestFrames()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("render-stats");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 30, duration: 1));
        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(directory, "src_pattern.mp4"), seconds: 1), "pattern");
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "pattern" });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Stats", FontFamily = "Segoe UI", FontSize = 40, Color = "#FFFFFF", End = 0.5 });
            return EffectEditor.SetEffect(p, "title", EffectKind.Shadow, new EffectValues(null, 4, 4, 6, 0.5, null, null), remove: false);
        });

        var result = Exporter.Export(session.Snapshot(), directory, Path.Combine(directory, "exports", "stats.mp4"), 18, ffmpeg, preset: Presets.Get("shorts_1080p"), fitShape: true);

        var times = Assert.IsType<RenderTimes>(result.Times);
        Assert.Equal(30, times.Frames);
        Assert.Equal(1, times.Workers);
        Assert.Equal(EnumNames.All<RenderStage>().Order(), times.Stages.Keys.Order());
        foreach (var stage in new[] { "scene", "decode", "text", "effects", "compositing", "fit", "encode" })
        {
            Assert.True(times.Stages[stage] > 0, $"{stage} took no time");
        }

        Assert.Equal(0, times.Stages["matte"]);
        Assert.InRange(times.AudioSeconds, 0, 0.01);
        Assert.InRange(times.Stages.Values.Sum() + times.AudioSeconds, 0, result.Elapsed.TotalSeconds);
        Assert.True(times.EncoderCpuSeconds > 0);

        Assert.Equal([("clip", "video", 30), ("title", "text", 15)], times.Layers.Select(l => (l.Id, l.Type, l.Frames)).OrderBy(l => l.Id));
        Assert.Equal(times.Layers.OrderByDescending(l => l.Seconds).Select(l => l.Id), times.Layers.Select(l => l.Id));
        Assert.Equal(RenderStats.KeptFrames, times.SlowestFrames.Count);
        Assert.All(times.SlowestFrames, f => Assert.InRange(f.Frame, 0, 29));
        Assert.Equal(times.SlowestFrames.Count, times.SlowestFrames.Select(f => f.Frame).Distinct().Count());
        Assert.Equal(times.SlowestFrames.OrderByDescending(f => f.Milliseconds).Select(f => f.Frame), times.SlowestFrames.Select(f => f.Frame));
    }

    [Fact]
    public void ThePreviewRendererMeasuresNothingUnlessAskedTo()
    {
        var directory = TestPaths.NewDirectory("render-stats-preview");
        var project = TestProjects.Empty(width: 64, height: 48, fps: 10, duration: 1);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", FontFamily = "Segoe UI", FontSize = 20 });
        using var renderer = new FrameRenderer(directory);
        using var plain = renderer.RenderImage(project, 0);
        Assert.Null(renderer.Stats);

        var stats = new RenderStats();
        renderer.Stats = stats;
        stats.BeginFrame(3);
        using var measured = renderer.RenderImage(project, 3);
        stats.EndFrame();
        var times = stats.Times(1, 0, null);
        Assert.True(times.Stages["text"] > 0);
        Assert.Equal(("title", "text", 1), (times.Layers.Single().Id, times.Layers.Single().Type, times.Layers.Single().Frames));
        Assert.Equal(3, times.SlowestFrames.Single().Frame);
        Assert.Null(times.EncoderCpuSeconds);
    }
}
