// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class MotionPathTests
{
    private static Project Ball()
    {
        var project = TestProjects.WithImageAssets("ball");
        project.Duration = 4;
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "ball", Drawings = new Dictionary<string, string> { ["normal"] = "ball" }, X = 10, Y = 10 });
        return project;
    }

    private static PathPointSpec Point(double time, double x, double y, string? easing = null) => new(time, x, y, null, null, null, null, easing);

    private static (double X, double Y) Where(Project project, double time)
    {
        var layer = SceneEvaluator.Evaluate(project, time).Single(e => e.Layer.Id == "ball");
        return layer.World.Apply(layer.Layer.AnchorX, layer.Layer.AnchorY);
    }

    private static double Rotation(Project project, double time)
    {
        var world = SceneEvaluator.Evaluate(project, time).Single(e => e.Layer.Id == "ball").World;
        return Math.Atan2(world.B, world.A) * 180 / Math.PI;
    }

    [Fact]
    public void TheLayerPassesEachPointAtItsTime()
    {
        var project = Ball();
        EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0), Point(1, 100, 100), Point(2, 200, 0)], autoOrient: false, remove: false);
        Assert.Equal((0.0, 0.0), Where(project, 0));
        var (x1, y1) = Where(project, 1);
        Assert.Equal(100, x1, 6);
        Assert.Equal(100, y1, 6);
        var (x2, y2) = Where(project, 2);
        Assert.Equal(200, x2, 6);
        Assert.Equal(0, y2, 6);
        var (_, yHalf) = Where(project, 0.5);
        Assert.InRange(yHalf, 30, 70);
        Assert.Equal(Where(project, 2), Where(project, 3));
    }

    [Fact]
    public void InsideASegmentTheLayerMovesAtAnEvenSpeedAlongTheCurve()
    {
        var project = Ball();
        // The bezier's own parameter would put the half-way time at x = 83.75 with these handles.
        EffectEditor.SetMotionPath(project, "ball", [new PathPointSpec(0, 0, 0, null, null, 90, 0, null), new PathPointSpec(1, 100, 0, 0, 0, null, null, null)], false, false);
        Assert.Equal(50, Where(project, 0.5).X, 0);
        Assert.Equal(25, Where(project, 0.25).X, 0);
        Assert.Equal(100, MotionPaths.Length(((VisualLayer)project.FindLayer("ball")!).MotionPath!), 3);
    }

    [Fact]
    public void A_PointsEasingShapesTheSegmentThatStartsThere()
    {
        var project = Ball();
        EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0, "easeIn"), Point(1, 100, 0), Point(2, 200, 0)], false, false);
        Assert.InRange(Where(project, 0.5).X, 1, 40);
        Assert.Equal(150, Where(project, 1.5).X, 3);
    }

    [Fact]
    public void AutoOrientTurnsTheLayerAlongThePathOnTopOfItsRotation()
    {
        var project = Ball();
        EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0), Point(1, 100, 0), Point(2, 100, 100)], autoOrient: true, remove: false);
        ProjectEditor.UpdateLayer(project, "ball", new JsonObject { ["rotation"] = 10 });
        Assert.Equal(10, Rotation(project, 0), 3);
        Assert.Equal(100, Rotation(project, 2), 3);
        Assert.InRange(Rotation(project, 1), 45, 65);
    }

    [Fact]
    public void A_PathClearsXyKeysAndXyKeysOrUpdatesTakeThePathOff()
    {
        var project = Ball();
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("ball", "x", 0, JsonSerializer.SerializeToElement(5)), new KeyframeEntry("ball", "x", 1, JsonSerializer.SerializeToElement(50))]);
        var set = EffectEditor.SetMotionPath(project, "ball", [Point(0, 20, 30), Point(1, 120, 30)], false, false);
        Assert.Contains("cleared 2 x/y keyframe(s)", set.Warnings[0]);
        var ball = (VisualLayer)project.FindLayer("ball")!;
        Assert.Equal((20.0, 30.0), (ball.X.Value, ball.Y.Value));

        var keys = KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("ball", "y", 0, JsonSerializer.SerializeToElement(200))]);
        Assert.Contains("had a motion path", keys.Warnings[0]);
        Assert.Null(((VisualLayer)project.FindLayer("ball")!).MotionPath);
        Assert.Equal((20.0, 200.0), Where(project, 0.5));

        EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0), Point(1, 100, 0)], false, false);
        var update = ProjectEditor.UpdateLayer(project, "ball", new JsonObject { ["x"] = 7 });
        Assert.Contains("had a motion path", update[0]);
        Assert.Null(((VisualLayer)project.FindLayer("ball")!).MotionPath);
    }

    [Fact]
    public void PathKeysAreNotSetByHandButMoveWithTheLayer()
    {
        var project = Ball();
        EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0), Point(1, 100, 0)], false, false);
        var error = Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("ball", "path", 0, JsonSerializer.SerializeToElement(1))]));
        Assert.Contains("come from set_motion_path", error.Message);

        TimelineEditor.MoveLayer(project, "ball", 1);
        Assert.Equal(50, Where(project, 1.5).X, 6);
        var tree = LayerTree.Describe(project.FindLayer("ball")!);
        Assert.Contains("path(2)", tree);
        Assert.Contains("effects: path(2 points)", tree);

        var cleared = KeyframeEditor.ClearKeyframes(project, "ball", null);
        Assert.Equal(2, cleared);
        Assert.Null(((VisualLayer)project.FindLayer("ball")!).MotionPath);
    }

    [Fact]
    public void BadPathsAreRejectedWithReasons()
    {
        var project = Ball();
        Assert.Contains("at least 2 points", Assert.Throws<ZeyoCutException>(() => EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0)], false, false)).Message);
        Assert.Contains("must come after point 0", Assert.Throws<ZeyoCutException>(() => EffectEditor.SetMotionPath(project, "ball", [Point(1, 0, 0), Point(1, 5, 5)], false, false)).Message);
        Assert.Contains("inX with inY", Assert.Throws<ZeyoCutException>(() => EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0), new PathPointSpec(1, 5, 5, 3, null, null, null, null)], false, false)).Message);
        Assert.Contains("unknown easing", Assert.Throws<ZeyoCutException>(() => EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0, "wobble"), Point(1, 5, 5)], false, false)).Message);
        Assert.Contains("has no motion path", Assert.Throws<ZeyoCutException>(() => EffectEditor.SetMotionPath(project, "ball", null, false, true)).Message);
    }

    [Fact]
    public void EffectsMasksAndPathsSurviveSavingAndLoading()
    {
        var project = Ball();
        EffectEditor.SetMotionPath(project, "ball", [Point(0, 0, 0), new PathPointSpec(1, 100, 0, -20, 30, null, null, "easeOut")], true, false);
        EffectEditor.SetEffect(project, "ball", EffectKind.Shadow, new EffectValues("#112233", 4, 5, 6, 0.7, null, null), false);
        EffectEditor.SetEffect(project, "ball", EffectKind.Tint, new EffectValues("#FF0000", null, null, null, null, null, 0.25), false);
        EffectEditor.SetMask(project, "ball", new MaskValues(MaskType.Ellipse, null, 1, 2, 30, 40, 5, true), false, null);
        var json = ProjectJson.Serialize(project);
        var copy = ProjectJson.Deserialize(json);
        Assert.Empty(ProjectValidator.Validate(copy));
        Assert.Equal(json, ProjectJson.Serialize(copy));
        var ball = (VisualLayer)copy.FindLayer("ball")!;
        Assert.Equal("#112233", ball.Shadow!.Color);
        Assert.Equal(0.25, ball.Tint!.Amount.Value);
        Assert.True(ball.Mask!.Invert);
        Assert.Equal(-20, ball.MotionPath!.Points[1].InX);
        Assert.Null(ball.MotionPath.Points[1].OutX);
        Assert.Equal(Easing.EaseOut, ball.MotionPath.Progress.Keyframes[1].Easing);

        var plain = ProjectJson.Serialize(Ball());
        Assert.DoesNotContain("shadow", plain);
        Assert.DoesNotContain("motionPath", plain);
        Assert.DoesNotContain("mask", plain);
    }
}
