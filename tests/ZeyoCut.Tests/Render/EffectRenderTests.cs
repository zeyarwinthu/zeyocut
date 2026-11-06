// SPDX-License-Identifier: MIT

using System.Text.Json;
using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class EffectRenderTests
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    // A 200x200 white canvas with a 40x40 red square whose top-left corner is at (40, 40).
    private static (ProjectSession Session, string Directory) Square(string name, int size = 40, double scale = 1)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 200, height: 200, fps: 30, duration: 2));
        var red = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "red.png"), size, size, Red), null, new AssetProbe()));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "square",
            Drawings = new Dictionary<string, string> { ["normal"] = red.Id },
            AnchorX = 0,
            AnchorY = 0,
            X = 40,
            Y = 40,
            ScaleX = scale,
            ScaleY = scale,
        }));
        return (session, directory);
    }

    private static SKImage Render(ProjectSession session, string directory, int frame = 0)
    {
        using var renderer = new FrameRenderer(directory);
        return renderer.RenderImage(session.Snapshot(), frame);
    }

    private static void Effect(ProjectSession session, EffectKind kind, EffectValues values) =>
        session.Mutate("effect", p => EffectEditor.SetEffect(p, "square", kind, values, remove: false));

    private static void Mask(ProjectSession session, MaskValues values) =>
        session.Mutate("mask", p => EffectEditor.SetMask(p, "square", values, remove: false, measurer: null));

    private static void Near(SKColor expected, SKColor actual, int tolerance = 6)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected about {expected}, got {actual}");
    }

    [Fact]
    public void DropShadowDrawsAnOffsetCopyBehindTheLayer()
    {
        var (session, directory) = Square("effect-shadow");
        Effect(session, EffectKind.Shadow, new EffectValues("#000000", 20, 20, 0, 1, null, null));
        using var image = Render(session, directory);
        Assert.Equal(Red, TestImages.Pixel(image, 70, 70));
        Near(SKColors.Black, TestImages.Pixel(image, 90, 90));
        Near(SKColors.Black, TestImages.Pixel(image, 70, 95));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 50, 95));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 105, 105));
    }

    [Fact]
    public void ShadowOpacityAndLayerOpacityBothFadeTheShadow()
    {
        var (session, directory) = Square("effect-shadow-opacity");
        Effect(session, EffectKind.Shadow, new EffectValues("#000000", 20, 20, 0, 0.5, null, null));
        using (var image = Render(session, directory))
        {
            Near(new SKColor(128, 128, 128), TestImages.Pixel(image, 90, 90));
        }

        session.Mutate("fade", p => ProjectEditor.UpdateLayer(p, "square", new System.Text.Json.Nodes.JsonObject { ["opacity"] = 0.5 }));
        using (var image = Render(session, directory))
        {
            Near(new SKColor(191, 191, 191), TestImages.Pixel(image, 90, 90));
            Near(new SKColor(255, 128, 128), TestImages.Pixel(image, 50, 50));
        }
    }

    [Fact]
    public void EffectSizesGrowWithTheLayersScale()
    {
        var (session, directory) = Square("effect-shadow-scale", size: 20, scale: 2);
        Effect(session, EffectKind.Shadow, new EffectValues("#000000", 10, 10, 0, 1, null, null));
        using var image = Render(session, directory);
        Assert.Equal(Red, TestImages.Pixel(image, 70, 70));
        Near(SKColors.Black, TestImages.Pixel(image, 95, 95));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 105, 105));
    }

    [Fact]
    public void OutlineGrowsTheShapeInTheOutlineColorWithRoundCorners()
    {
        var (session, directory) = Square("effect-outline");
        Effect(session, EffectKind.Outline, new EffectValues("#0000FF", null, null, null, null, 6, null));
        using var image = Render(session, directory);
        Assert.Equal(Red, TestImages.Pixel(image, 77, 60));
        Near(Blue, TestImages.Pixel(image, 83, 60));
        Near(Blue, TestImages.Pixel(image, 60, 36));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 90, 60));
        Near(Blue, TestImages.Pixel(image, 83, 83));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 86, 86));
    }

    [Fact]
    public void BlurSoftensOnlyAlongItsAxis()
    {
        var (session, directory) = Square("effect-blur");
        Effect(session, EffectKind.Blur, new EffectValues(null, 4, 0, null, null, null, null));
        using var image = Render(session, directory);
        var edge = TestImages.Pixel(image, 39, 60);
        Assert.InRange(edge.Green, 60, 200);
        Assert.Equal(255, edge.Red);
        Assert.Equal(Red, TestImages.Pixel(image, 60, 60));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 60, 38));
        Assert.Equal(Red, TestImages.Pixel(image, 60, 41));
    }

    [Fact]
    public void TintMixesTheLayerTowardTheColorAndLeavesTheRestAlone()
    {
        var (session, directory) = Square("effect-tint");
        Effect(session, EffectKind.Tint, new EffectValues("#0000FF", null, null, null, null, null, 0.5));
        using (var image = Render(session, directory))
        {
            Near(new SKColor(128, 0, 128), TestImages.Pixel(image, 60, 60));
            Assert.Equal(SKColors.White, TestImages.Pixel(image, 20, 20));
        }

        Effect(session, EffectKind.Tint, new EffectValues(null, null, null, null, null, null, 1));
        using (var image = Render(session, directory))
        {
            Assert.Equal(Blue, TestImages.Pixel(image, 60, 60));
        }
    }

    [Fact]
    public void EffectsAreAnimatableWithSetKeyframes()
    {
        var (session, directory) = Square("effect-animated");
        Effect(session, EffectKind.Shadow, new EffectValues("#000000", 0, 0, 0, 1, null, null));
        var change = session.Mutate("keys", p => KeyframeEditor.SetKeyframes(p,
        [
            new KeyframeEntry("square", "shadow.x", 0, JsonSerializer.SerializeToElement(0)),
            new KeyframeEntry("square", "Shadow.X", 1, JsonSerializer.SerializeToElement(40)),
        ]));
        Assert.Equal(1, change.Added + change.Replaced - 1);
        using var image = Render(session, directory, frame: 15);
        Near(SKColors.Black, TestImages.Pixel(image, 95, 60));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 105, 60));
    }

    [Fact]
    public void EffectsStayOnTheirLayerAndSkipItsChildren()
    {
        var (session, directory) = Square("effect-children");
        var blue = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "blue.png"), 20, 20, Blue), null, new AssetProbe()));
        session.Mutate("child", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "badge",
            ParentId = "square",
            Drawings = new Dictionary<string, string> { ["normal"] = blue.Id },
            AnchorX = 0,
            AnchorY = 0,
            X = 100,
            Y = 0,
            ZIndex = 1,
        }));
        Effect(session, EffectKind.Tint, new EffectValues("#00FF00", null, null, null, null, null, 1));
        using var image = Render(session, directory);
        Assert.Equal(new SKColor(0, 255, 0), TestImages.Pixel(image, 60, 60));
        Assert.Equal(Blue, TestImages.Pixel(image, 150, 50));
    }

    [Fact]
    public void RectangleMaskKeepsTheBoxAndInvertKeepsTheRest()
    {
        var (session, directory) = Square("mask-rect");
        Mask(session, new MaskValues(MaskType.Rectangle, null, 0, 0, 20, 40, null, null));
        using (var image = Render(session, directory))
        {
            Assert.Equal(Red, TestImages.Pixel(image, 50, 60));
            Assert.Equal(SKColors.White, TestImages.Pixel(image, 70, 60));
        }

        Mask(session, new MaskValues(null, null, null, null, null, null, null, true));
        using (var image = Render(session, directory))
        {
            Assert.Equal(SKColors.White, TestImages.Pixel(image, 50, 60));
            Assert.Equal(Red, TestImages.Pixel(image, 70, 60));
        }
    }

    [Fact]
    public void A_NewMaskFitsTheLayerAndAnEllipseCutsTheCorners()
    {
        var (session, directory) = Square("mask-ellipse");
        Mask(session, new MaskValues(MaskType.Ellipse, null, null, null, null, null, null, null));
        var mask = session.Read(p => ((VisualLayer)p.FindLayer("square")!).Mask!);
        Assert.Equal((0.0, 0.0, 40.0, 40.0), (mask.X.Value, mask.Y.Value, mask.Width.Value, mask.Height.Value));
        using var image = Render(session, directory);
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 43, 43));
        Assert.Equal(Red, TestImages.Pixel(image, 60, 60));
        Assert.Equal(Red, TestImages.Pixel(image, 41, 60));
    }

    [Fact]
    public void ImageMaskUsesTheImagesAlphaStretchedOverTheBox()
    {
        var (session, directory) = Square("mask-image");
        var file = TestImages.SplitLeftRight(Path.Combine(directory, "half.png"), 10, 10, SKColors.Black, SKColors.Transparent);
        var half = session.Mutate("import", p => AssetImporter.Import(p, directory, file, null, new AssetProbe()));
        Mask(session, new MaskValues(MaskType.Image, half.Id, null, null, null, null, null, null));
        using var image = Render(session, directory);
        Assert.Equal(Red, TestImages.Pixel(image, 45, 60));
        Assert.Equal(SKColors.White, TestImages.Pixel(image, 75, 60));
    }

    [Fact]
    public void FeatherSoftensTheMaskEdgeAndIsAnimatable()
    {
        var (session, directory) = Square("mask-feather");
        Mask(session, new MaskValues(MaskType.Rectangle, null, 0, 0, 20, 40, 8, null));
        using (var image = Render(session, directory))
        {
            var edge = TestImages.Pixel(image, 60, 60);
            Assert.InRange(edge.Green, 90, 170);
            Near(Red, TestImages.Pixel(image, 50, 60), tolerance: 8);
            Assert.Equal(SKColors.White, TestImages.Pixel(image, 72, 60));
        }

        session.Mutate("keys", p => KeyframeEditor.SetKeyframes(p,
        [
            new KeyframeEntry("square", "mask.width", 0, JsonSerializer.SerializeToElement(0)),
            new KeyframeEntry("square", "mask.width", 1, JsonSerializer.SerializeToElement(40)),
            new KeyframeEntry("square", "mask.feather", 0, JsonSerializer.SerializeToElement(0)),
        ]));
        using (var image = Render(session, directory, frame: 15))
        {
            Assert.Equal(Red, TestImages.Pixel(image, 58, 60));
            Assert.Equal(SKColors.White, TestImages.Pixel(image, 62, 60));
        }
    }

    [Fact]
    public void SetEffectRejectsFieldsOfOtherEffectsAndMissingEffects()
    {
        var (session, _) = Square("effect-errors");
        var wrong = Assert.Throws<ZeyoCut.Core.ZeyoCutException>(() => Effect(session, EffectKind.Outline, new EffectValues(null, 4, null, null, null, null, null)));
        Assert.Contains("outline takes color, width; x is not for outline", wrong.Message);
        var tint = Assert.Throws<ZeyoCut.Core.ZeyoCutException>(() => Effect(session, EffectKind.Tint, new EffectValues(null, null, null, null, null, null, 0.5)));
        Assert.Contains("needs a color", tint.Message);
        var range = Assert.Throws<ZeyoCut.Core.ZeyoCutException>(() => Effect(session, EffectKind.Shadow, new EffectValues(null, null, null, null, 2, null, null)));
        Assert.Contains("shadow.opacity must be between 0 and 1", range.Message);
        var keys = Assert.Throws<ZeyoCut.Core.ZeyoCutException>(() => session.Mutate("keys", p => KeyframeEditor.SetKeyframes(p,
            [new KeyframeEntry("square", "blur.x", 0, JsonSerializer.SerializeToElement(3))])));
        Assert.Contains("has no blur effect; add one with set_effect first", keys.Message);
        var mask = Assert.Throws<ZeyoCut.Core.ZeyoCutException>(() => Mask(session, new MaskValues(null, null, 0, null, null, null, null, null)));
        Assert.Contains("a new mask needs a type", mask.Message);
        Assert.Equal(0, session.Read(p => ((VisualLayer)p.FindLayer("square")!).Shadow is null ? 0 : 1));
    }

    // A layer with effects or a mask is drawn into a drawing layer the size of its content; a mask
    // that keeps everything must then change nothing, so no glyph, stroke, box or flipped edge is cut.
    [Fact]
    public void TheEffectLayerHoldsAllOfTheLayersContent()
    {
        var directory = TestPaths.NewDirectory("effect-bounds");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 400, height: 300, fps: 30, duration: 1));
        var split = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.SplitLeftRight(Path.Combine(directory, "split.png"), 60, 40, Red, Blue), null, new AssetProbe()));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = "flipped",
            Drawings = new Dictionary<string, string> { ["normal"] = split.Id },
            AnchorX = 10,
            AnchorY = 0,
            X = 120,
            Y = 60,
            Rotation = 20,
            FlipX = true,
        }));
        session.Mutate("add", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions
        {
            Id = "words",
            Text = "Wavy\nglyphs",
            FontSize = 48,
            Bold = true,
            Color = "#FFD400",
            StrokeColor = "#000000",
            StrokeWidth = 8,
            Box = new TextBackground { Color = "#00800080", Padding = 12, CornerRadius = 6 },
            X = 250,
            Y = 140,
            Rotation = -12,
            ScaleX = 1.3,
            ScaleY = 1.3,
        }));
        var plain = Render(session, directory);

        foreach (var id in new[] { "flipped", "words" })
        {
            session.Mutate("mask", p => EffectEditor.SetMask(p, id, new MaskValues(MaskType.Rectangle, null, -1000, -1000, 5000, 5000, null, null), remove: false, measurer: null));
        }

        using var masked = Render(session, directory);
        var before = TestImages.Pixels(plain);
        var after = TestImages.Pixels(masked);
        plain.Dispose();
        var worst = Enumerable.Range(0, before.Length).Max(i => Math.Abs(before[i] - after[i]));
        Assert.True(worst <= 3, $"a channel differs by {worst}");
    }
}
