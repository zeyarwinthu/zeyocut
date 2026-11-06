// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class PreviewCacheTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // 200x150 at 10 fps for 20 s: a logo all the way, a title that moves from 8 s to 12 s, and a label
    // at 9–11 s.
    private static ProjectSession Timeline(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 200, height: 150, fps: 10, duration: 20));
        session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "src_logo.png"), 40, 40, new SKColor(220, 30, 30)), "logo", new AssetProbe()));
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["n"] = "logo" }, X = 40, Y = 40 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Cached", FontFamily = "Segoe UI", FontSize = 24, Color = "#202020", Y = 110 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "label", Text = "Late", FontFamily = "Segoe UI", FontSize = 20, Color = "#0000FF", Y = 70, Start = 9, End = 11 });
            return KeyframeEditor.SetKeyframes(p, [new KeyframeEntry("title", "x", 8, Json(60)), new KeyframeEntry("title", "x", 12, Json(140))]);
        });
        return session;
    }

    private static (int Hits, int Misses) Counts(FrameRenderer renderer) => (renderer.Previews.Hits, renderer.Previews.Misses);

    [Fact]
    public void AFrameShownAgainComesFromTheCacheAsTheSamePng()
    {
        var session = Timeline("preview-cache-again");
        using var renderer = new FrameRenderer(session.Directory);
        var first = Preview.RenderFrame(renderer, session.Snapshot(), 2, 100, debug: false);
        var saved = File.ReadAllBytes(first.SavedPath);
        File.Delete(first.SavedPath);

        var again = Preview.RenderFrame(renderer, session.Snapshot(), 2, 100, debug: false);
        Assert.Equal((1, 1), Counts(renderer));
        Assert.Equal(first.Png, again.Png);
        Assert.Equal(saved, File.ReadAllBytes(again.SavedPath));

        var wider = Preview.RenderFrame(renderer, session.Snapshot(), 2, 150, debug: false);
        var debug = Preview.RenderFrame(renderer, session.Snapshot(), 2, 150, debug: true);
        Assert.Equal((3, 1), Counts(renderer));

        using var fresh = new FrameRenderer(session.Directory);
        Assert.Equal(Preview.RenderFrame(fresh, session.Snapshot(), 2, 150, debug: false).Png, wider.Png);
        Assert.Equal(Preview.RenderFrame(fresh, session.Snapshot(), 2, 150, debug: true).Png, debug.Png);
        Assert.Equal(saved, File.ReadAllBytes(first.SavedPath));
    }

    [Fact]
    public void AnEditAtAnotherTimeLeavesTheFrameCachedButAChangedFileDoesNot()
    {
        var session = Timeline("preview-cache-edit");
        using var renderer = new FrameRenderer(session.Directory);
        Preview.RenderFrame(renderer, session.Snapshot(), 2, 200, debug: false);
        Preview.RenderFrame(renderer, session.Snapshot(), 10, 200, debug: false);

        session.Mutate("title", p => KeyframeEditor.SetKeyframes(p, [new KeyframeEntry("title", "x", 12, Json(180))]));
        Preview.RenderFrame(renderer, session.Snapshot(), 2, 200, debug: false);
        Assert.Equal((1, 2), Counts(renderer));
        var moved = Preview.RenderFrame(renderer, session.Snapshot(), 10, 200, debug: false);
        Assert.Equal((1, 3), Counts(renderer));
        using var fresh = new FrameRenderer(session.Directory);
        Assert.Equal(Preview.RenderFrame(fresh, session.Snapshot(), 10, 200, debug: false).Png, moved.Png);

        var logo = ProjectStore.AssetPath(session.Directory, session.Snapshot().FindAsset("logo")!);
        TestImages.Solid(logo, 40, 40, new SKColor(30, 30, 220));
        File.SetLastWriteTimeUtc(logo, DateTime.UtcNow.AddSeconds(2));
        var blue = Preview.RenderFrame(renderer, session.Snapshot(), 2, 200, debug: false);
        Assert.Equal((1, 4), Counts(renderer));
        using var saved = SKImage.FromEncodedData(blue.SavedPath)!;
        Assert.Equal(new SKColor(30, 30, 220), TestImages.Pixel(saved, 40, 40));
    }

    [Fact]
    public void AContactSheetDrawsOnlyTheCellsThatChanged()
    {
        var session = Timeline("preview-cache-sheet");
        using var renderer = new FrameRenderer(session.Directory);
        var first = Preview.RenderContactSheet(renderer, session.Snapshot(), 0, 15, 4, 4, 800, debug: false);
        Assert.Equal([0, 50, 100, 150], first.Frames);
        // 0 s and 5 s show the same, so the second is drawn once.
        Assert.Equal((1, 3), Counts(renderer));

        session.Mutate("label", p => ProjectEditor.UpdateLayer(p, "label", new JsonObject { ["color"] = "#FF00FF" }));
        var second = Preview.RenderContactSheet(renderer, session.Snapshot(), 0, 15, 4, 4, 800, debug: false);
        Assert.Equal((4, 4), Counts(renderer));
        Assert.NotEqual(first.Png, second.Png);
        using var fresh = new FrameRenderer(session.Directory);
        Assert.Equal(Preview.RenderContactSheet(fresh, session.Snapshot(), 0, 15, 4, 4, 800, debug: false).Png, second.Png);

        Preview.RenderContactSheet(renderer, session.Snapshot(), 0, 15, 4, 4, 800, debug: true);
        Assert.Equal((4, 4), Counts(renderer));
    }

    [Fact]
    public void ACachedPreviewGivesTheWarningsDrawingItGave()
    {
        var session = Timeline("preview-cache-warnings");
        session.Mutate("font", p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["fontFamily"] = "No Such Font Zq" }));
        using var renderer = new FrameRenderer(session.Directory);
        var first = Preview.RenderFrame(renderer, session.Snapshot(), 2, 200, debug: false);
        Assert.Contains(first.Warnings, w => w.StartsWith("layer 'title': ", StringComparison.Ordinal));
        var again = Preview.RenderFrame(renderer, session.Snapshot(), 2, 200, debug: false);
        Assert.Equal((1, 1), Counts(renderer));
        Assert.Equal(first.Warnings, again.Warnings);

        var sheet = Preview.RenderContactSheet(renderer, session.Snapshot(), 0, 15, 4, 4, 800, debug: false);
        var sheetAgain = Preview.RenderContactSheet(renderer, session.Snapshot(), 0, 15, 4, 4, 800, debug: false);
        Assert.Equal((6, 4), Counts(renderer));
        Assert.Equal(first.Warnings, sheet.Warnings);
        Assert.Equal(sheet.Warnings, sheetAgain.Warnings);
    }

    [Fact]
    public void TheCacheDropsWhatWasUsedLongestAgo()
    {
        using var cache = new PreviewCache(budget: 100);
        using var surface = SKSurface.Create(new SKImageInfo(4, 4))!;
        var image = surface.Snapshot();
        cache.Add("a", "A", 40);
        cache.Add("b", new CachedCell(image, []), 40);
        Assert.Equal("A", cache.Get<string>("a"));

        cache.Add("c", "C", 40);
        Assert.Null(cache.Get<CachedCell>("b"));
        Assert.Equal(IntPtr.Zero, image.Handle);
        Assert.Equal("A", cache.Get<string>("a"));
        Assert.Equal("C", cache.Get<string>("c"));
        Assert.Equal(80, cache.Bytes);

        cache.Add("huge", "H", 500);
        Assert.Equal("H", cache.Get<string>("huge"));
        Assert.Equal(500, cache.Bytes);
        cache.Add("next", "N", 10);
        Assert.Null(cache.Get<string>("huge"));
        cache.Add("next", "M", 20);
        Assert.Equal("M", cache.Get<string>("next"));
        Assert.Equal(20, cache.Bytes);
    }
}
