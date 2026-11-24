// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The preview: how big it shows the frame, and drawing frames paused and while playing.
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
    public void TheViewerNamesTheCanvasShape()
    {
        Assert.Equal("16:9", PreviewZoom.Ratio(1920, 1080));
        Assert.Equal("9:16", PreviewZoom.Ratio(1080, 1920));
        Assert.Equal("4:5", PreviewZoom.Ratio(1080, 1350));
        Assert.Equal("1:1", PreviewZoom.Ratio(1000, 1000));
        Assert.Equal("1.85:1", PreviewZoom.Ratio(1998, 1080));
        Assert.Equal("1.78:1", PreviewZoom.Ratio(1366, 768));
    }

    [Fact]
    public void HundredPercentShowsEachPixelOnOneScreenPixel()
    {
        Assert.Equal((1920.0, 1080.0, 1.0), PreviewZoom.Layout(1920, 1080, 1, 500, 500, 1));
        Assert.Equal((1280.0, 720.0, 1.0), PreviewZoom.Layout(1920, 1080, 1, 500, 500, 1.5));
        Assert.Equal((1920.0, 1080.0, 2.0), PreviewZoom.Layout(960, 540, 2, 500, 500, 1));
    }

    [Fact]
    public void AFrameIsDrawnOnceAndShownFromTheCacheUntilItChanges()
    {
        var project = TestFiles.Project();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", FontSize = 40 });
        var folder = TestFiles.NewProject("preview-cache", project);
        using var preview = new FramePreview(folder, () => throw new ZeyoCutException("no FFmpeg needed"), _ => { });

        var first = preview.Render(preview.Source(project, 1), 10);
        Assert.Null(first.Error);
        Assert.Equal((320, 180, 320 * 180 * 4), (first.Width, first.Height, first.Pixels!.Length));
        Assert.False(first.Cached);

        // The background, in BGRA.
        Assert.Equal(new byte[] { 0x20, 0x20, 0x20, 0xFF }, first.Pixels[..4]);

        var again = preview.Render(preview.Source(project, 1), 10);
        Assert.True(again.Cached);
        Assert.Same(first.Pixels, again.Pixels);

        // A frame the title doesn't change still comes from the cache after an edit; one it does doesn't.
        var edited = ProjectJsonCopy(project);
        ProjectEditor.UpdateLayer(edited, "title", new JsonObject { ["text"] = "Hello" });
        Assert.False(preview.Render(preview.Source(edited, 1), 10).Cached);
    }

    [Fact]
    public void AMatteFinishedWithoutASaveIsDrawnOnceTheFilesAreReadAgain()
    {
        var folder = Path.Combine(TestFiles.NewFolder("preview-matte"), "project");
        var matte = TestFiles.PendingMatte(folder);
        var project = ProjectStore.Load(folder);
        using var preview = new FramePreview(folder, () => throw new ZeyoCutException("no FFmpeg needed"), _ => { });

        // Until the matte is computed, the picture is drawn with its background.
        var before = preview.Render(preview.Source(project, 1), 0);
        Assert.False(TestFiles.OnlyBackground(before.Pixels!));

        // The server computes it (all background): project.json doesn't change.
        ZeyoCut.Render.Pixels.SaveGray(new byte[64 * 64], 64, 64, matte);
        preview.FilesChanged();
        var after = preview.Render(preview.Source(project, 1), 0);
        Assert.False(after.Cached);
        Assert.True(TestFiles.OnlyBackground(after.Pixels!));

        // Nothing changed since: the frame comes from memory.
        preview.FilesChanged();
        Assert.True(preview.Render(preview.Source(project, 1), 0).Cached);
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

        var frame = preview.Render(preview.Source(project, 1), 0);
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

        preview.Request(preview.Source(project, 1), 42);
        var frame = await drawn.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(42, frame.Frame);
        Assert.NotNull(frame.Pixels);
    }

    [Fact]
    public async Task PlayingDrawsSmallerFramesAheadAndKeepsThemApart()
    {
        var project = TestFiles.Project();
        var title = ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", FontSize = 40 });
        // Moving, so no two frames are alike.
        title.X.Keyframes.AddRange([new Keyframe { Time = 0, Value = 0 }, new Keyframe { Time = 2, Value = 300 }]);
        var folder = TestFiles.NewProject("preview-playing", project);
        using var preview = new FramePreview(folder, () => throw new ZeyoCutException("no FFmpeg needed"), _ => { });

        var paused = preview.Render(preview.Source(project, 1), 3);
        var half = preview.Render(preview.Source(project, 0.5), 3);
        Assert.Equal((320, 180), (paused.Width, paused.Height));
        Assert.Equal((160, 90), (half.Width, half.Height));
        Assert.False(half.Cached);

        // The frames ahead are drawn in the background, a step apart.
        var source = preview.Source(project, 0.5, step: 2);
        preview.Play(source, 0);
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (preview.Cached(source, 6) is null && DateTime.UtcNow < stop)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        preview.Pause();
        Assert.Equal((160, 90, 6, true), (preview.Cached(source, 6)!.Width, preview.Cached(source, 6)!.Height, preview.Cached(source, 6)!.Frame, preview.Cached(source, 6)!.Cached));
        Assert.Null(preview.Cached(source, 5));

        // A frame drawn before a proxy was made is drawn again after it.
        preview.ProxiesChanged();
        Assert.False(preview.Render(preview.Source(project, 1), 3).Cached);
    }

    [Fact]
    public void FramesAheadFitInHalfTheMemory()
    {
        var folder = TestFiles.NewProject("preview-ahead");
        using var preview = new FramePreview(folder, () => throw new ZeyoCutException("no FFmpeg needed"), _ => { });

        // Small frames: 5 s ahead. A 4K canvas at full size: never less than a second; at half size,
        // every second frame, about 2 s.
        Assert.Equal(5, preview.Source(TestFiles.Project(), 0.5).AheadSeconds);
        Assert.Equal(1, preview.Source(TestFiles.Project(3840, 2160), 1).AheadSeconds);
        Assert.InRange(preview.Source(TestFiles.Project(3840, 2160), 0.5, step: 2).AheadSeconds, 2.1, 2.2);
    }

    [Fact]
    public void ThePictureFollowsTheSound()
    {
        var clock = new PlaybackClock();
        Assert.False(clock.Follow(3));
        clock.Start(1, 2);
        Assert.True(clock.Running);
        Assert.InRange(clock.Now, 1, 1.5);

        // Within the drift it's left alone; past it, it's set to the sound.
        Assert.False(clock.Follow(clock.Now + PlaybackClock.Drift / 2));
        Assert.True(clock.Follow(5));
        Assert.InRange(clock.Now, 5, 5.5);
        Assert.Equal(2, clock.Speed);
        clock.Stop();
        Assert.False(clock.Running);
    }

    private static Project ProjectJsonCopy(Project project) => ZeyoCut.Core.Serialization.ProjectJson.Copy(project);
}
