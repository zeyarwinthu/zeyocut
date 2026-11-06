// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class MatteRenderTests
{
    private static readonly SKColor Screen = new(40, 190, 60);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Red = new(255, 0, 0);

    private static string Draw(string path, int width, int height, Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        Png.Save(image, path);
        return path;
    }

    private static void Fill(SKCanvas canvas, SKColor color, SKRect rect)
    {
        using var paint = new SKPaint();
        paint.Color = color;
        canvas.DrawRect(rect, paint);
    }

    // The image as layer 'art', top-left at (0, 0), on a blue canvas of the same size.
    private static ProjectSession Art(string name, string file, int width, int height)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: width, height: height, fps: 10, duration: 1));
        session.Mutate("background", p => ProjectEditor.UpdateProject(p, new ProjectOptions { BackgroundColor = "#0000FF" }));
        var asset = session.Mutate("import", p => AssetImporter.Import(p, directory, file, "art", new AssetProbe()));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "art",
            Drawings = new Dictionary<string, string> { ["normal"] = asset.Id },
            AnchorX = 0,
            AnchorY = 0,
            X = 0,
            Y = 0,
        }));
        return session;
    }

    private static void Remove(ProjectSession session, MatteMethod method, MatteValues values, string layerId = "art") =>
        session.Mutate("matte", p => MatteEditor.Set(p, layerId, MatteEditor.Build(MatteEditor.GetMattable(p, layerId), method, values)));

    private static SKImage Render(ProjectSession session, int frame = 0)
    {
        using var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg);
        return renderer.RenderImage(session.Snapshot(), frame);
    }

    private static void Near(SKColor expected, SKColor actual, int tolerance = 6)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected about {expected}, got {actual}");
    }

    // A 100x100 green screen with a red square from x = left to left + 40, y = 30 to 70.
    private static string Square(string directory, int left = 30) =>
        Draw(Path.Combine(directory, "..", $"square-{left}.png"), 100, 100, c =>
        {
            c.Clear(Screen);
            Fill(c, Red, new SKRect(left, 30, left + 40, 70));
        });

    [Fact]
    public void ChromaRemovesTheScreenAndItsShadesButKeepsTheSubject()
    {
        var file = Draw(Path.Combine(TestPaths.Root, "matte-chroma.png"), 120, 80, c =>
        {
            c.Clear(Screen);
            Fill(c, new SKColor(30, 150, 48), new SKRect(0, 0, 30, 80));
            Fill(c, Red, new SKRect(50, 20, 80, 60));
        });
        var session = Art("matte-chroma", file, 120, 80);
        Remove(session, MatteMethod.Chroma, new MatteValues("#28BE3C", null, null, null));
        using var image = Render(session);
        Assert.Equal(Blue, TestImages.Pixel(image, 5, 5));
        Assert.Equal(Blue, TestImages.Pixel(image, 100, 10));
        Assert.Equal(Red, TestImages.Pixel(image, 65, 40));
    }

    [Fact]
    public void ChromaPicksTheScreenColorFromThePicturesEdges()
    {
        using var surface = SKSurface.Create(new SKImageInfo(120, 80))!;
        surface.Canvas.Clear(Screen);
        Fill(surface.Canvas, Red, new SKRect(40, 20, 80, 60));
        using var screen = surface.Snapshot();
        var key = Assert.NotNull(ChromaKey.Detect(screen));
        Assert.Equal(new RgbaColor(40, 190, 60, 255), key);

        surface.Canvas.Clear(new SKColor(128, 128, 128));
        using var gray = surface.Snapshot();
        Assert.Null(ChromaKey.Detect(gray));
    }

    [Fact]
    public void SpillSuppressionTakesTheScreenTintOutOfWhatStays()
    {
        // Half skin, half screen, as on the edge of a face: kept, but tinted green.
        using var surface = SKSurface.Create(new SKImageInfo(20, 10))!;
        surface.Canvas.Clear(new SKColor(148, 205, 130));
        using var edge = surface.Snapshot();
        var matte = new Matte { Method = MatteMethod.Chroma, Color = "#28BE3C", Tolerance = 0.4, Softness = 0.2, Spill = 0 };

        var (plain, plainAlpha) = ChromaKey.Apply(edge, matte);
        matte.Spill = 1;
        var (clean, cleanAlpha) = ChromaKey.Apply(edge, matte);
        using (plain)
        using (plainAlpha)
        using (clean)
        using (cleanAlpha)
        {
            var before = TestImages.Pixel(plain, 5, 5);
            var after = TestImages.Pixel(clean, 5, 5);
            Assert.True(before.Green - before.Red > 50, $"without spill suppression {before}");
            Assert.True(after.Green - after.Red < 20, $"with spill suppression {after}");
            Assert.Equal(255, Pixels.ReadAlpha(cleanAlpha)[55]);
        }
    }

    [Fact]
    public void EdgeFillRemovesTheBorderBackgroundAndKeepsWhatTheArtEncloses()
    {
        var file = Draw(Path.Combine(TestPaths.Root, "matte-ring.png"), 100, 100, c =>
        {
            c.Clear(SKColors.White);
            using var ring = new SKPaint();
            ring.Color = SKColors.Black;
            ring.Style = SKPaintStyle.Stroke;
            ring.StrokeWidth = 4;
            ring.IsAntialias = true;
            c.DrawCircle(50, 50, 30, ring);
            using var dot = new SKPaint();
            dot.Color = Red;
            c.DrawCircle(50, 50, 5, dot);
        });
        var session = Art("matte-edgefill", file, 100, 100);
        using (var renderer = new FrameRenderer(session.Directory))
        {
            var project = session.Snapshot();
            var asset = project.FindAsset("art")!;
            var matte = MatteEditor.Build(MatteEditor.GetMattable(project, "art"), MatteMethod.EdgeFill, new MatteValues(null, null, null, null));
            var result = Mattes.EdgeFill(session.Directory, asset, renderer.DrawingImage(asset), matte, out var path);
            Assert.Equal(new RgbaColor(255, 255, 255, 255), result.Background);
            Assert.InRange(result.Removed, 0.66, 0.70);
            Assert.Matches(@"matte-ring\.matte-edgefill-[0-9a-f]{8}\.png$", path);
            Assert.True(File.Exists(path));
        }

        Remove(session, MatteMethod.EdgeFill, new MatteValues(null, null, null, null));
        using var image = Render(session);
        Assert.Equal(Blue, TestImages.Pixel(image, 5, 5));
        Assert.Equal(Blue, TestImages.Pixel(image, 50, 12));
        Near(SKColors.Black, TestImages.Pixel(image, 50, 20), 40);
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 50, 35));
        Assert.Equal(Red, TestImages.Pixel(image, 50, 50));
    }

    [Fact]
    public void EdgeFillUsesTheGivenBackgroundColorAndTolerance()
    {
        var file = Draw(Path.Combine(TestPaths.Root, "matte-gray.png"), 60, 60, c =>
        {
            c.Clear(new SKColor(128, 128, 128));
            Fill(c, new SKColor(140, 140, 140), new SKRect(0, 0, 60, 10));
            Fill(c, SKColors.White, new SKRect(20, 20, 40, 40));
        });
        var session = Art("matte-edgefill-color", file, 60, 60);
        Remove(session, MatteMethod.EdgeFill, new MatteValues("#808080", 0.02, null, null));
        using (var tight = Render(session))
        {
            Assert.Equal(new SKColor(140, 140, 140), TestImages.Pixel(tight, 30, 5));
            Assert.Equal(Blue, TestImages.Pixel(tight, 30, 15));
            Assert.Equal(SKColors.White, TestImages.Pixel(tight, 30, 30));
        }

        Remove(session, MatteMethod.EdgeFill, new MatteValues("#808080", 0.1, null, null));
        using var loose = Render(session);
        Assert.Equal(Blue, TestImages.Pixel(loose, 30, 5));
        Assert.Equal(SKColors.White, TestImages.Pixel(loose, 30, 30));
    }

    [Fact]
    public void ShrinkAndFeatherMoveAndSoftenTheEdge()
    {
        var directory = TestPaths.NewDirectory("matte-refine-art");
        var session = Art("matte-refine", Square(directory), 100, 100);
        Remove(session, MatteMethod.Chroma, new MatteValues("#28BE3C", null, null, null));
        using (var sharp = Render(session))
        {
            Assert.Equal(Red, TestImages.Pixel(sharp, 31, 50));
            Assert.Equal(Blue, TestImages.Pixel(sharp, 29, 50));
        }

        session.Mutate("shrink", p => MatteEditor.Refine(p, "art", 4, null));
        using (var shrunk = Render(session))
        {
            Assert.Equal(Blue, TestImages.Pixel(shrunk, 32, 50));
            Assert.Equal(Red, TestImages.Pixel(shrunk, 35, 50));
        }

        session.Mutate("grow", p => MatteEditor.Refine(p, "art", -3, null));
        using (var grown = Render(session))
        {
            Assert.NotEqual(Blue, TestImages.Pixel(grown, 28, 50));
            Assert.Equal(Blue, TestImages.Pixel(grown, 26, 50));
        }

        session.Mutate("feather", p => MatteEditor.Refine(p, "art", 0, 8));
        using var soft = Render(session);
        var edge = TestImages.Pixel(soft, 30, 50);
        Assert.True(edge.Red > 60 && edge.Blue > 60, $"the edge pixel mixes red and blue: {edge}");
        Assert.True(TestImages.Pixel(soft, 22, 50).Blue > 230);
        Assert.True(TestImages.Pixel(soft, 38, 50).Red > 230);
    }

    [Fact]
    public void ShrinkKeepsASubjectThatTouchesThePictureEdge()
    {
        var directory = TestPaths.NewDirectory("matte-frame-edge-art");
        var session = Art("matte-frame-edge", Square(directory, left: 0), 100, 100);
        Remove(session, MatteMethod.Chroma, new MatteValues("#28BE3C", null, null, null));
        session.Mutate("refine", p => MatteEditor.Refine(p, "art", 4, 2));
        using var image = Render(session);
        Assert.Equal(Red, TestImages.Pixel(image, 0, 50));
        Assert.Equal(Red, TestImages.Pixel(image, 30, 50));
        Assert.Equal(Blue, TestImages.Pixel(image, 40, 50));
    }

    [Fact]
    public void ChromaKeysEveryFrameOfAVideo()
    {
        Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("matte-video");
        var file = TestVideos.GreenScreen(Path.Combine(directory, "screen.mp4"));
        var session = ProjectSession.Create(Path.Combine(directory, "project"), TestProjects.Empty(width: 96, height: 64, fps: 10, duration: 1));
        session.Mutate("background", p => ProjectEditor.UpdateProject(p, new ProjectOptions { BackgroundColor = "#0000FF" }));
        TestVideos.Import(session, file, "screen");
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "screen" }));

        using (var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg))
        {
            var project = session.Snapshot();
            var key = Assert.NotNull(ChromaKey.Detect(renderer.VideoFrame(project, (VideoLayer)project.FindLayer("clip")!, 0)));
            Near(Screen, key.ToSkia(), 3);
        }

        Remove(session, MatteMethod.Chroma, new MatteValues("#28BE3C", null, null, null), "clip");
        using (var first = Render(session))
        {
            Near(Red, TestImages.Pixel(first, 20, 28), 3);
            Assert.Equal(Blue, TestImages.Pixel(first, 60, 28));
            Assert.Equal(Blue, TestImages.Pixel(first, 20, 5));
        }

        using var later = Render(session, 5);
        Near(Red, TestImages.Pixel(later, 40, 28), 3);
        Assert.Equal(Blue, TestImages.Pixel(later, 20, 28));
    }

    [Fact]
    public void CheckerboardShowsTransparencyAndLayerIdDrawsOnlyThatLayer()
    {
        var directory = TestPaths.NewDirectory("matte-checker-art");
        var session = Art("matte-checker", Square(directory), 100, 100);
        var yellow = session.Mutate("import", p => AssetImporter.Import(p, session.Directory, TestImages.Solid(Path.Combine(directory, "yellow.png"), 100, 100, SKColors.Yellow), "yellow", new AssetProbe()));
        session.Mutate("back", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "back",
            Drawings = new Dictionary<string, string> { ["normal"] = yellow.Id },
            AnchorX = 0,
            AnchorY = 0,
            X = 0,
            Y = 0,
            ZIndex = -1,
        }));
        Remove(session, MatteMethod.Chroma, new MatteValues("#28BE3C", null, null, null));

        using var renderer = new FrameRenderer(session.Directory);
        var project = session.Snapshot();
        var whole = Preview.RenderFrame(renderer, project, 0, 960, debug: false, checkerboard: true);
        using (var image = SKImage.FromEncodedData(whole.Png))
        {
            Assert.Equal(SKColors.Yellow, TestImages.Pixel(image, 5, 5));
            Assert.Equal(Red, TestImages.Pixel(image, 50, 50));
        }

        Assert.Equal(8, Preview.CheckerSize(100, 100));
        var solo = Preview.RenderFrame(renderer, project, 0, 960, debug: false, checkerboard: true, layerId: "art");
        Assert.EndsWith("frame_00000_art_checker.png", solo.SavedPath);
        using (var image = SKImage.FromEncodedData(solo.Png))
        {
            Assert.Equal(Preview.CheckerDark, TestImages.Pixel(image, 5, 5));
            Assert.Equal(Preview.CheckerLight, TestImages.Pixel(image, 12, 5));
            Assert.Equal(Preview.CheckerLight, TestImages.Pixel(image, 5, 12));
            Assert.Equal(Red, TestImages.Pixel(image, 50, 50));
        }

        var onBackground = Preview.RenderFrame(renderer, project, 0, 960, debug: false, layerId: "back");
        using (var image = SKImage.FromEncodedData(onBackground.Png))
        {
            Assert.Equal(SKColors.Yellow, TestImages.Pixel(image, 50, 50));
        }
    }
}
