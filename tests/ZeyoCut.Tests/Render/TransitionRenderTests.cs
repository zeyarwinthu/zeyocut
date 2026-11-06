// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class TransitionRenderTests
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    // A 200x100 canvas at 30 fps: a red full-frame still 0–1s, then a blue one 1–2s drawn above it.
    // A 0.5s transition overlaps them over 0.75–1.25s, so frame 30 (1.0s) is its middle.
    private static (ProjectSession Session, string Directory) TwoShots(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 200, height: 100, fps: 30, duration: 2));
        var red = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "red.png"), 200, 100, Red), null, new AssetProbe()));
        var blue = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "blue.png"), 200, 100, Blue), null, new AssetProbe()));
        session.Mutate("add", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "a", Drawings = new Dictionary<string, string> { ["normal"] = red.Id }, AnchorX = 0, AnchorY = 0, X = 0, Y = 0, Start = 0, End = 1 });
            return ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "b", Drawings = new Dictionary<string, string> { ["normal"] = blue.Id }, AnchorX = 0, AnchorY = 0, X = 0, Y = 0, Start = 1, End = 2 });
        });
        return (session, directory);
    }

    private static TimelineChange Transition(ProjectSession session, TransitionType type, TransitionDirection? direction = null) =>
        session.Mutate("transition", p => TimelineEditor.AddTransition(p, "a", "b", type, 0.5, direction));

    private static SKImage Middle(ProjectSession session, string directory)
    {
        using var renderer = new FrameRenderer(directory);
        return renderer.RenderImage(session.Snapshot(), 30);
    }

    private static void Near(SKColor expected, SKColor actual, int tolerance = 8)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected about {expected}, got {actual}");
    }

    [Fact]
    public void WipeRevealsTheNewShotBehindAnEdgeThatCrossesTheFrame()
    {
        var (session, directory) = TwoShots("transition-wipe");
        var change = Transition(session, TransitionType.Wipe);
        Assert.Contains("wipe right 'a' → 'b' over 0.75–1.25s", change.Summary);
        using (var image = Middle(session, directory))
        {
            Assert.Equal(Blue, TestImages.Pixel(image, 90, 50));
            Assert.Equal(Red, TestImages.Pixel(image, 110, 50));
        }

        var (left, _) = TwoShots("transition-wipe-left");
        left.Mutate("transition", p => TimelineEditor.AddTransition(p, "a", "b", TransitionType.Wipe, 0.5, TransitionDirection.Left));
        using var leftImage = Middle(left, left.Directory);
        Assert.Equal(Red, TestImages.Pixel(leftImage, 90, 50));
        Assert.Equal(Blue, TestImages.Pixel(leftImage, 110, 50));
        using var renderer = new FrameRenderer(left.Directory);
        using var end = renderer.RenderImage(left.Snapshot(), 38);
        Assert.Equal(Blue, TestImages.Pixel(end, 1, 50));
    }

    [Fact]
    public void WipeAwayWhenTheOldShotIsOnTop()
    {
        var (session, directory) = TwoShots("transition-wipe-top");
        session.Mutate("z", p => ProjectEditor.UpdateLayer(p, "a", new System.Text.Json.Nodes.JsonObject { ["zIndex"] = 5 }));
        var change = Transition(session, TransitionType.Wipe, TransitionDirection.Down);
        Assert.Contains("'a' (drawn on top) is wiped away down", change.Summary);
        using var image = Middle(session, directory);
        Assert.Equal(Blue, TestImages.Pixel(image, 100, 45));
        Assert.Equal(Red, TestImages.Pixel(image, 100, 55));
    }

    [Fact]
    public void SlidePushesTheOldShotOutAndTheNewOneIn()
    {
        var (session, directory) = TwoShots("transition-slide");
        Transition(session, TransitionType.Slide);
        using var image = Middle(session, directory);
        Assert.Equal(Red, TestImages.Pixel(image, 90, 50));
        Assert.Equal(Blue, TestImages.Pixel(image, 110, 50));
        var project = session.Snapshot();
        Assert.Equal(0, Interpolator.Evaluate(((VisualLayer)project.FindLayer("b")!).X, 1.25), 9);
        Assert.Equal(200, Interpolator.Evaluate(((VisualLayer)project.FindLayer("b")!).X, 0.75), 9);
    }

    [Fact]
    public void ZoomInGrowsTheOldShotAndBringsTheNewOneInFromSmallAroundTheCenter()
    {
        var (session, directory) = TwoShots("transition-zoom");
        Transition(session, TransitionType.Zoom);
        using var image = Middle(session, directory);
        Near(new SKColor(128, 0, 128), TestImages.Pixel(image, 100, 50));
        Near(new SKColor(128, 0, 128), TestImages.Pixel(image, 30, 50));
        Assert.Equal(Red, TestImages.Pixel(image, 20, 50));
        Assert.Equal(Red, TestImages.Pixel(image, 100, 8));

        var project = session.Snapshot();
        var a = SceneEvaluator.Evaluate(project, 1.25).Single(e => e.Layer.Id == "a").World;
        Assert.Equal((100.0, 50.0), (Math.Round(a.Apply(100, 50).X, 6), Math.Round(a.Apply(100, 50).Y, 6)));
        Assert.Equal(2, a.A, 9);
    }

    [Fact]
    public void ZoomOutShrinksTheOldShot()
    {
        var (session, directory) = TwoShots("transition-zoom-out");
        session.Mutate("z", p => ProjectEditor.UpdateLayer(p, "a", new System.Text.Json.Nodes.JsonObject { ["zIndex"] = 5 }));
        Transition(session, TransitionType.Zoom, TransitionDirection.Out);
        using var image = Middle(session, directory);
        Near(new SKColor(128, 0, 128), TestImages.Pixel(image, 100, 50));
        Assert.Equal(Blue, TestImages.Pixel(image, 10, 50));
    }

    [Fact]
    public void WhipPanPushesWithMotionBlurThatPeaksInTheMiddle()
    {
        var (session, directory) = TwoShots("transition-whip");
        var change = Transition(session, TransitionType.WhipPan);
        Assert.Contains("motion blur up to 4 px at 1s", change.Summary);
        var b = (VisualLayer)session.Snapshot().FindLayer("b")!;
        Assert.Equal([0.0, 4.0, 0.0], b.Blur!.X.Keyframes.Select(k => k.Value));
        Assert.Equal([0.75, 1.0, 1.25], b.Blur.X.Keyframes.Select(k => k.Time));
        Assert.Equal(0, b.Blur.Y.Value);
        using var image = Middle(session, directory);
        Assert.Equal(Red, TestImages.Pixel(image, 60, 50));
        Assert.Equal(Blue, TestImages.Pixel(image, 140, 50));
        var seam = TestImages.Pixel(image, 99, 50);
        Assert.InRange(seam.Blue, 40, 215);
        Assert.InRange(seam.Red, 40, 215);
    }

    [Fact]
    public void DirectionsAndConflictsAreChecked()
    {
        var (session, _) = TwoShots("transition-errors");
        Assert.Contains("crossfade has no direction", Assert.Throws<ZeyoCutException>(() => Transition(session, TransitionType.Crossfade, TransitionDirection.Left)).Message);
        Assert.Contains("zoom goes in or out", Assert.Throws<ZeyoCutException>(() => Transition(session, TransitionType.Zoom, TransitionDirection.Up)).Message);
        Assert.Contains("in and out are for zoom", Assert.Throws<ZeyoCutException>(() => Transition(session, TransitionType.Slide, TransitionDirection.In)).Message);
        session.Mutate("mask", p => EffectEditor.SetMask(p, "b", new MaskValues(MaskType.Ellipse, null, null, null, null, null, null, null), false, null));
        Assert.Contains("'b' already has a mask", Assert.Throws<ZeyoCutException>(() => Transition(session, TransitionType.Wipe)).Message);
        session.Mutate("path", p => EffectEditor.SetMotionPath(p, "a", [new PathPointSpec(0, 0, 0, null, null, null, null, null), new PathPointSpec(1, 10, 0, null, null, null, null, null)], false, false));
        Assert.Contains("'a' moves on a motion path", Assert.Throws<ZeyoCutException>(() => Transition(session, TransitionType.WhipPan)).Message);
        Assert.Equal(1, session.Read(p => p.FindLayer("b")!.Start));
    }
}
