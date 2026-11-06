// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class TransformTests
{
    private const double Tolerance = 1e-9;

    private static ImageLayer Layer(string id, double x, double y, double anchorX = 0, double anchorY = 0, string? parent = null, double rotation = 0, double scale = 1, int z = 0) => new()
    {
        Id = id,
        Name = id,
        ParentId = parent,
        AnchorX = anchorX,
        AnchorY = anchorY,
        X = new AnimatedValue(x),
        Y = new AnimatedValue(y),
        Rotation = new AnimatedValue(rotation),
        ScaleX = new AnimatedValue(scale),
        ScaleY = new AnimatedValue(scale),
        ZIndex = z,
    };

    private static void AssertPoint(Affine2D m, double x, double y, double expectedX, double expectedY)
    {
        var (px, py) = m.Apply(x, y);
        Assert.Equal(expectedX, px, Tolerance);
        Assert.Equal(expectedY, py, Tolerance);
    }

    private static EvaluatedLayer Find(IReadOnlyList<EvaluatedLayer> scene, string id) => scene.Single(l => l.Layer.Id == id);

    [Fact]
    public void AnchorLandsAtPosition()
    {
        var layer = Layer("a", 100, 200, anchorX: 50, anchorY: 50, rotation: 37, scale: 2);
        AssertPoint(SceneEvaluator.LocalMatrix(layer, 0), 50, 50, 100, 200);
    }

    [Fact]
    public void PositiveRotationIsClockwiseAroundAnchor()
    {
        var layer = Layer("a", 100, 100, rotation: 90);
        AssertPoint(SceneEvaluator.LocalMatrix(layer, 0), 10, 0, 100, 110);
    }

    [Fact]
    public void ScaleHappensAroundAnchor()
    {
        var layer = Layer("a", 50, 50, anchorX: 50, anchorY: 50, scale: 2);
        AssertPoint(SceneEvaluator.LocalMatrix(layer, 0), 60, 50, 70, 50);
    }

    [Fact]
    public void RotatingParentMovesChildAroundParentAnchor()
    {
        var project = TestProjects.Empty();
        var parent = Layer("body", 500, 500, anchorX: 100, anchorY: 100);
        var child = Layer("head", 200, 100, parent: "body");
        project.Layers.AddRange([parent, child]);

        AssertPoint(Find(SceneEvaluator.Evaluate(project, 0), "head").World, 0, 0, 600, 500);

        parent.Rotation = new AnimatedValue(90);
        var world = Find(SceneEvaluator.Evaluate(project, 0), "head").World;
        AssertPoint(world, 0, 0, 500, 600);
        AssertPoint(world, 10, 0, 500, 610);
    }

    [Fact]
    public void ParentScaleScalesChildOffset()
    {
        var project = TestProjects.Empty();
        project.Layers.AddRange([Layer("body", 0, 0, scale: 2), Layer("head", 30, 40, parent: "body")]);
        AssertPoint(Find(SceneEvaluator.Evaluate(project, 0), "head").World, 0, 0, 60, 80);
    }

    [Fact]
    public void ThreeLevelChain()
    {
        var project = TestProjects.Empty();
        project.Layers.AddRange(
        [
            Layer("root", 100, 100, rotation: 90),
            Layer("arm", 50, 0, parent: "root", rotation: 90),
            Layer("hand", 20, 0, parent: "arm"),
        ]);

        var scene = SceneEvaluator.Evaluate(project, 0);
        AssertPoint(Find(scene, "arm").World, 0, 0, 100, 150);
        AssertPoint(Find(scene, "hand").World, 0, 0, 80, 150);
    }

    [Fact]
    public void ChildFollowsAnimatedParent()
    {
        var project = TestProjects.Empty();
        var parent = Layer("body", 0, 0);
        parent.X.Keyframes.AddRange([new Keyframe { Time = 0, Value = 0 }, new Keyframe { Time = 1, Value = 100 }]);
        project.Layers.AddRange([parent, Layer("head", 10, 0, parent: "body")]);
        AssertPoint(Find(SceneEvaluator.Evaluate(project, 0.5), "head").World, 0, 0, 60, 0);
    }

    [Fact]
    public void OpacityVisibilityAndTimingAreInherited()
    {
        var project = TestProjects.Empty();
        var parent = Layer("body", 0, 0);
        parent.Opacity = new AnimatedValue(0.5);
        parent.End = 1;
        var child = Layer("head", 0, 0, parent: "body");
        child.Opacity = new AnimatedValue(0.5);
        project.Layers.AddRange([parent, child]);

        var early = Find(SceneEvaluator.Evaluate(project, 0.5), "head");
        Assert.True(early.Shown);
        Assert.Equal(0.25, early.Opacity, Tolerance);

        Assert.False(Find(SceneEvaluator.Evaluate(project, 1.5), "head").Shown);

        parent.End = null;
        parent.Visible = false;
        Assert.False(Find(SceneEvaluator.Evaluate(project, 0.5), "head").Shown);
    }

    [Fact]
    public void DrawOrderIsZIndexThenArrayOrder()
    {
        var project = TestProjects.Empty();
        project.Layers.AddRange(
        [
            Layer("body", 0, 0, z: 10),
            Layer("near_leg", 0, 0, parent: "body", z: 11),
            Layer("far_leg", 0, 0, parent: "body", z: 9),
            Layer("tie_a", 0, 0, z: 10),
        ]);

        var order = SceneEvaluator.Evaluate(project, 0).Select(l => l.Layer.Id).ToArray();
        Assert.Equal(["far_leg", "body", "tie_a", "near_leg"], order);
    }

    [Fact]
    public void CyclesAreRejected()
    {
        var project = TestProjects.Rig();
        var session = ProjectSession.Create(TestPaths.NewDirectory("transform-cycle"), project);

        var ex = Assert.Throws<ZeyoCutException>(() => session.Mutate("cycle", p =>
            ProjectEditor.UpdateLayer(p, "tora_body", new System.Text.Json.Nodes.JsonObject { ["parentId"] = "tora_head" })));
        Assert.Contains("parent cycle", ex.Message);
        Assert.Null(session.Read(p => p.FindLayer("tora_body")!.ParentId));
    }
}
