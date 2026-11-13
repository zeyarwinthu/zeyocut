// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

// The preview frames the preview page and the editor share: drawn once, then kept, and drawn ahead
// while playing.
public class PreviewFramesTests
{
    private static PreviewSource Source(string name, int step = 1)
    {
        // The title moves, so no two frames look the same.
        var project = TestProjects.Empty(width: 160, height: 90, fps: 10, duration: 3);
        var title = ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi" });
        title.X.Keyframes.AddRange([new Keyframe { Time = 0, Value = 0 }, new Keyframe { Time = 3, Value = 150 }]);
        var directory = TestPaths.NewDirectory(name);
        ProjectSession.Create(directory, project);
        return new PreviewSource(project, directory, new FrameKeys(project, directory), 80, 45, step);
    }

    private static PreviewFrames<string> Frames(ConcurrentQueue<int> drawn, Func<int, bool>? fails = null) =>
        new(() => throw new ZeyoCutException("no FFmpeg needed"), 1024 * 1024, (renderer, source, frame) =>
        {
            if (fails?.Invoke(frame) == true)
            {
                throw new ZeyoCutException($"frame {frame} can't be drawn");
            }

            using var image = renderer.RenderImage(source.Project, frame);
            drawn.Enqueue(frame);
            return ($"{frame}: {image.Width}x{image.Height}", 100);
        });

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(done());
    }

    [Fact]
    public void AFrameIsDrawnOnceAndKept()
    {
        var source = Source("preview-frames-once");
        var drawn = new ConcurrentQueue<int>();
        using var frames = Frames(drawn);

        Assert.Null(frames.Cached(source, 4));
        Assert.Equal("4: 160x90", frames.Frame(source, 4, TestContext.Current.CancellationToken));
        Assert.Equal("4: 160x90", frames.Frame(source, 4, TestContext.Current.CancellationToken));
        Assert.Equal("4: 160x90", frames.Cached(source, 4));
        Assert.Equal([4], drawn);
        Assert.True(frames.DrawTime > TimeSpan.Zero);

        // The size is part of the key: the same frame at another size is drawn again.
        Assert.Null(frames.Cached(source with { Width = 40, Height = 22 }, 4));
        Assert.NotEqual(source.Key(4), (source with { Variant = "|proxied" }).Key(4));
    }

    [Fact]
    public async Task PlayingDrawsTheFramesAheadAStepApart()
    {
        var source = Source("preview-frames-ahead", step: 2) with { AheadSeconds = 1 };
        var drawn = new ConcurrentQueue<int>();
        using var frames = Frames(drawn);

        frames.Play(source, 3);
        Assert.True(frames.Playing);
        await Until(() => frames.Cached(source, 12) is not null);
        Assert.Equal([4, 6, 8, 10, 12], drawn.Order());

        // Nothing past AheadSeconds is drawn until playing goes on from a later frame.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(5, drawn.Count);
        frames.Play(source, 12);
        await Until(() => frames.Cached(source, 22) is not null);

        frames.Pause();
        Assert.False(frames.Playing);
    }

    [Fact]
    public async Task AFrameThatCantBeDrawnStopsThePlayingAhead()
    {
        var source = Source("preview-frames-error");
        var drawn = new ConcurrentQueue<int>();
        using var frames = Frames(drawn, frame => frame == 3);

        frames.Play(source, 0);
        await Until(() => !frames.Playing);
        Assert.Equal("frame 3 can't be drawn", frames.AheadError?.Message);
        Assert.Equal([1, 2], drawn);

        frames.Play(source, 3);
        Assert.Null(frames.AheadError);
        await Until(() => frames.Cached(source, 5) is not null);
    }
}
