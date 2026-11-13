// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The preview: how big it shows the frame, the time ruler under it, and drawing frames.
public class PreviewTests
{
    [Fact]
    public void FitKeepsTheCanvasAspectInThePanel()
    {
        Assert.Equal((960.0, 540.0, 0.5), PreviewZoom.Layout(1920, 1080, null, 960, 600, 1));
        Assert.Equal((337.5, 600.0, 0.3125), PreviewZoom.Layout(1080, 1920, null, 800, 600, 1));

        // At 150% display scaling the same panel shows the frame on 1.5 times the screen pixels.
        Assert.Equal((960.0, 540.0, 0.75), PreviewZoom.Layout(1920, 1080, null, 960, 600, 1.5));
        Assert.Equal((0.0, 0.0, 0.0), PreviewZoom.Layout(1920, 1080, null, 0, 0, 1));
    }

    [Fact]
    public void HundredPercentShowsEachPixelOnOneScreenPixel()
    {
        Assert.Equal((1920.0, 1080.0, 1.0), PreviewZoom.Layout(1920, 1080, 1, 500, 500, 1));
        Assert.Equal((1280.0, 720.0, 1.0), PreviewZoom.Layout(1920, 1080, 1, 500, 500, 1.5));
        Assert.Equal((1920.0, 1080.0, 2.0), PreviewZoom.Layout(960, 540, 2, 500, 500, 1));
    }

    [Theory]
    [InlineData(230, 0.5)]
    [InlineData(640, 0.1)]
    [InlineData(10, 10)]
    [InlineData(0.001, 3600)]
    public void RulerLabelsLeaveRoomForTheirText(double pixelsPerSecond, double step) => Assert.Equal(step, TimeRuler.LabelStep(pixelsPerSecond));

    [Fact]
    public void ClickingTheRulerPicksTheFrameUnderThePointer()
    {
        Assert.Equal(0, TimeRuler.FrameAt(0, 600, 180));
        Assert.Equal(179, TimeRuler.FrameAt(599.9, 600, 180));
        Assert.Equal(0, TimeRuler.FrameAt(-20, 600, 180));
        Assert.Equal(179, TimeRuler.FrameAt(900, 600, 180));
        Assert.Equal(0, TimeRuler.FrameAt(100, 0, 180));
        Assert.Equal(300, TimeRuler.XOf(90, 600, 180));
        Assert.All(Enumerable.Range(0, 180), f => Assert.Equal(f, TimeRuler.FrameAt(TimeRuler.XOf(f, 600, 180) + 0.5, 600, 180)));
    }

    [Fact]
    public void AFrameIsDrawnOnceAndShownFromTheCacheUntilItChanges()
    {
        var project = TestFiles.Project();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", FontSize = 40 });
        var folder = TestFiles.NewProject("preview-cache", project);
        using var preview = new FramePreview(folder, () => throw new ZeyoCutException("no FFmpeg needed"), _ => { });

        var first = preview.Render(project, 10);
        Assert.Null(first.Error);
        Assert.Equal((320, 180, 320 * 180 * 4), (first.Width, first.Height, first.Pixels!.Length));
        Assert.False(first.Cached);

        // The background, in BGRA.
        Assert.Equal(new byte[] { 0x20, 0x20, 0x20, 0xFF }, first.Pixels[..4]);

        var again = preview.Render(project, 10);
        Assert.True(again.Cached);
        Assert.Same(first.Pixels, again.Pixels);

        // A frame the title doesn't change still comes from the cache after an edit; one it does doesn't.
        var edited = ProjectJsonCopy(project);
        ProjectEditor.UpdateLayer(edited, "title", new JsonObject { ["text"] = "Hello" });
        Assert.False(preview.Render(edited, 10).Cached);
    }

    [Fact]
    public void AFrameThatCantBeDrawnSaysWhy()
    {
        var project = TestFiles.Project();
        project.Assets.Add(new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 320, Height = 180, FrameRate = "30", FrameCount = 60, Duration = 2, HasAudio = false, Codec = "h264" });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip" });
        var folder = TestFiles.NewProject("preview-error", project);
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllBytes(Path.Combine(folder, "assets", "clip.mp4"), [0]);
        using var preview = new FramePreview(folder, () => throw new ZeyoCutException("no FFmpeg here"), _ => { });

        var frame = preview.Render(project, 0);
        Assert.Null(frame.Pixels);
        Assert.Contains("no FFmpeg here", frame.Error);
    }

    [Fact]
    public async Task TheWorkerDrawsTheFrameAskedFor()
    {
        var project = TestFiles.Project();
        var folder = TestFiles.NewProject("preview-worker", project);
        var drawn = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preview = new FramePreview(folder, () => throw new ZeyoCutException("no FFmpeg needed"), f => drawn.TrySetResult(f));

        preview.Request(project, 42);
        var frame = await drawn.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(42, frame.Frame);
        Assert.NotNull(frame.Pixels);
    }

    private static Project ProjectJsonCopy(Project project) => ZeyoCut.Core.Serialization.ProjectJson.Copy(project);
}
