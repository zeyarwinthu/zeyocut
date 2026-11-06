// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class RenderTests
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    private static (ProjectSession Session, string Directory) NewProject(string name, int size = 200)
    {
        var directory = TestPaths.NewDirectory(name);
        var project = TestProjects.Empty(width: size, height: size, fps: 30, duration: 2);
        return (ProjectSession.Create(directory, project), directory);
    }

    private static Asset Import(ProjectSession session, string file) =>
        session.Mutate("import", p => AssetImporter.Import(p, session.Directory, file, null, new AssetProbe()));

    [Fact]
    public void RotationIsClockwiseAroundAnchor()
    {
        var (session, directory) = NewProject("render-rotation");
        var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 20, 20, Red));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "square",
            Drawings = new Dictionary<string, string> { ["normal"] = red.Id },
            AnchorX = 0,
            AnchorY = 0,
            X = 100,
            Y = 100,
            Rotation = 90,
        }));

        using var renderer = new FrameRenderer(directory);
        using var image = renderer.RenderImage(session.Snapshot(), 0);
        Assert.Equal(Red, TestImages.Pixel(image, 90, 110));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 110, 110));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 90, 90));
    }

    [Fact]
    public void RotatingParentMovesChild()
    {
        var (session, directory) = NewProject("render-parenting");
        var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 20, 20, Red));
        session.Mutate("add", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "root", X = 100, Y = 100, Rotation = 90 });
            return ProjectEditor.AddImageLayer(p, new ImageLayerOptions
            {
                Id = "child",
                ParentId = "root",
                Drawings = new Dictionary<string, string> { ["normal"] = red.Id },
                X = 50,
                Y = 0,
            });
        });

        using var renderer = new FrameRenderer(directory);
        using var image = renderer.RenderImage(session.Snapshot(), 0);
        Assert.Equal(Red, TestImages.Pixel(image, 100, 150));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 150, 100));
    }

    [Fact]
    public void FlipXMirrorsAroundAnchorAndDrawingSwapsHold()
    {
        var (session, directory) = NewProject("render-flip");
        var split = Import(session, TestImages.SplitLeftRight(Path.Combine(directory, "split.png"), 20, 10, Red, Blue));
        var blue = Import(session, TestImages.Solid(Path.Combine(directory, "blue.png"), 20, 10, Blue));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "sign",
            Drawings = new Dictionary<string, string> { ["split"] = split.Id, ["blue"] = blue.Id },
            X = 100,
            Y = 100,
        }));
        session.Mutate("keys", p => KeyframeEditor.SetKeyframes(p,
        [
            new KeyframeEntry("sign", "drawing", 0, System.Text.Json.JsonSerializer.SerializeToElement("split")),
            new KeyframeEntry("sign", "drawing", 1, System.Text.Json.JsonSerializer.SerializeToElement("blue")),
        ]));

        using var renderer = new FrameRenderer(directory);
        using (var normal = renderer.RenderImage(session.Snapshot(), 0))
        {
            Assert.Equal(Red, TestImages.Pixel(normal, 95, 100));
            Assert.Equal(Blue, TestImages.Pixel(normal, 105, 100));
        }

        session.Mutate("flip", p => ProjectEditor.UpdateLayer(p, "sign", new JsonObject { ["flipX"] = true }));
        using (var flipped = renderer.RenderImage(session.Snapshot(), 29))
        {
            Assert.Equal(Blue, TestImages.Pixel(flipped, 95, 100));
            Assert.Equal(Red, TestImages.Pixel(flipped, 105, 100));
        }

        using var swapped = renderer.RenderImage(session.Snapshot(), 30);
        Assert.Equal(Blue, TestImages.Pixel(swapped, 95, 100));
    }

    [Fact]
    public void OpacityBlendsOverBackground()
    {
        var (session, directory) = NewProject("render-opacity");
        var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 40, 40, Red));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Drawings = new Dictionary<string, string> { ["normal"] = red.Id },
            Opacity = 0.5,
        }));

        using var renderer = new FrameRenderer(directory);
        using var image = renderer.RenderImage(session.Snapshot(), 0);
        var pixel = TestImages.Pixel(image, 100, 100);
        Assert.Equal(255, pixel.Red);
        Assert.InRange(pixel.Green, 126, 129);
        Assert.InRange(pixel.Blue, 126, 129);
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        var (session, directory) = NewProject("render-determinism", size: 320);
        var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 60, 30, Red));
        var blue = Import(session, TestImages.SplitLeftRight(Path.Combine(directory, "split.png"), 40, 40, Blue, Red));
        session.Mutate("build", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "body", Drawings = new Dictionary<string, string> { ["n"] = red.Id }, Rotation = 17.3, ScaleX = 1.37 });
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "head", ParentId = "body", Drawings = new Dictionary<string, string> { ["n"] = blue.Id }, Rotation = -33, Opacity = 0.7 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hello မင်္ဂလာပါ", FontFamily = "Myanmar Text", FontSize = 30, StrokeColor = "#FFFFFF", X = 10, Y = 10, AnchorX = 0, AnchorY = 0 });
            return KeyframeEditor.SetKeyframes(p,
            [
                new KeyframeEntry("body", "x", 0, System.Text.Json.JsonSerializer.SerializeToElement(50), "easeOutBack"),
                new KeyframeEntry("body", "x", 1, System.Text.Json.JsonSerializer.SerializeToElement(250)),
            ]);
        });

        var project = session.Snapshot();
        byte[] first;
        using (var renderer = new FrameRenderer(directory))
        using (var image = renderer.RenderImage(project, 15))
        {
            first = TestImages.Pixels(image);
            using var again = renderer.RenderImage(project, 15);
            Assert.Equal(first, TestImages.Pixels(again));
        }

        using var other = new FrameRenderer(directory);
        using var fresh = other.RenderImage(project, 15);
        Assert.Equal(first, TestImages.Pixels(fresh));

        using var later = other.RenderImage(project, 16);
        Assert.NotEqual(first, TestImages.Pixels(later));
    }

    [Fact]
    public void PreviewDownscalesAndSavesFullResolution()
    {
        var (session, directory) = NewProject("render-preview", size: 400);
        var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 40, 40, Red));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "dot", Drawings = new Dictionary<string, string> { ["n"] = red.Id } }));

        using var renderer = new FrameRenderer(directory);
        var preview = Preview.RenderFrame(renderer, session.Snapshot(), 0.51, 200, debug: false);
        Assert.Equal(200, preview.Width);
        Assert.Equal(15, preview.Frames.Single());
        Assert.Equal(Path.Combine(directory, "renders", "frame_00015.png"), preview.SavedPath);
        using var saved = SKImage.FromEncodedData(preview.SavedPath)!;
        Assert.Equal(400, saved.Width);

        var debug = Preview.RenderFrame(renderer, session.Snapshot(), 0, 300, debug: true);
        Assert.EndsWith("frame_00000_debug.png", debug.SavedPath);

        var sheet = Preview.RenderContactSheet(renderer, session.Snapshot(), 0, 1, 6, 3, 600, debug: false);
        Assert.Equal([0, 6, 12, 18, 24, 30], sheet.Frames);
        Assert.Equal(600, sheet.Width);
        Assert.True(File.Exists(sheet.SavedPath));
    }

    [Fact]
    public void CliRenderFrameWritesPng()
    {
        var (session, directory) = NewProject("render-cli");
        var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 40, 40, Red));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Drawings = new Dictionary<string, string> { ["n"] = red.Id } }));

        var output = new StringWriter();
        var error = new StringWriter();
        var outPath = Path.Combine(directory, "cli.png");
        var exit = Cli.Run(["render-frame", directory, "0.5", outPath], output, error);

        Assert.Equal(0, exit);
        Assert.Contains("frame 15", output.ToString());
        using var image = SKImage.FromEncodedData(outPath)!;
        Assert.Equal(Red, TestImages.Pixel(image, 100, 100));

        Assert.Equal(1, Cli.Run(["render-frame", Path.Combine(directory, "missing"), "0", outPath], output, error));
        Assert.Contains("no project.json", error.ToString());
    }

    [Fact]
    public void TheCameraMovesThePictureExceptLayersThatIgnoreIt()
    {
        var (session, directory) = NewProject("render-camera");
        var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 20, 20, Red));
        var blue = Import(session, TestImages.Solid(Path.Combine(directory, "blue.png"), 20, 20, Blue));
        session.Mutate("add", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "scene", Drawings = new Dictionary<string, string> { ["n"] = red.Id }, X = 100, Y = 100 });
            var badge = ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "badge", Drawings = new Dictionary<string, string> { ["n"] = blue.Id }, X = 30, Y = 30 });
            badge.IgnoreCamera = true;
            return CameraEditor.Set(p, x: 70, y: 100, zoom: 2, rotation: null, reset: false);
        });

        using var renderer = new FrameRenderer(directory);
        using var image = renderer.RenderImage(session.Snapshot(), 0);
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 100, 100));
        Assert.Equal(Red, TestImages.Pixel(image, 161, 100));
        Assert.Equal(Red, TestImages.Pixel(image, 175, 115));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 185, 100));
        Assert.Equal(Blue, TestImages.Pixel(image, 30, 30));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 42, 30));
    }

    [Fact]
    public void CameraShakeWithTheSameSeedRendersTheSameFrames()
    {
        byte[][] Frames(string name, int seed)
        {
            var (session, directory) = NewProject(name);
            var red = Import(session, TestImages.Solid(Path.Combine(directory, "red.png"), 40, 40, Red));
            session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Drawings = new Dictionary<string, string> { ["n"] = red.Id } }));
            session.Mutate("shake", p => CameraEditor.Shake(p, 0.2, 1, 15, 8, seed));
            using var renderer = new FrameRenderer(directory);
            return [.. Enumerable.Range(0, 45).Select(frame =>
            {
                using var image = renderer.RenderImage(session.Snapshot(), frame);
                return TestImages.Pixels(image);
            })];
        }

        var first = Frames("render-shake-a", 7);
        var second = Frames("render-shake-b", 7);
        var other = Frames("render-shake-c", 8);

        Assert.Equal(first, second);
        Assert.Equal(first[0], first[5]);
        Assert.NotEqual(first[0], first[15]);
        Assert.Equal(first[0], first[40]);
        Assert.NotEqual(first[15], other[15]);
    }
}
