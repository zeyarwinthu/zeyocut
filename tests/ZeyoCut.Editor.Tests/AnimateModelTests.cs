// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Editor.Tests;

public class AnimateModelTests
{
    private static Project ToraJump() =>
        ProjectJson.Deserialize(File.ReadAllText(Path.Combine(TestFiles.RepositoryRoot, "examples", "tora-jump", ProjectStore.ProjectFileName)));

    [Fact]
    public void TheTreeListsTheAnimatedLayersUnderTheirParentsWithTheirValues()
    {
        var project = ToraJump();
        var layers = AnimateModel.Build(project, null);

        Assert.Equal(["clouds", "shadow", "tora", "tora_body", "tora_head", "tora_tail", "title", "subtitle"], layers.Select(l => l.LayerId));
        Assert.Equal([0, 0, 0, 1, 2, 2, 0, 0], layers.Select(l => l.Depth));
        Assert.Equal(["image", "image", "group", "image", "image", "image", "text", "text"], layers.Select(l => l.Kind));
        var tora = layers.Single(l => l.LayerId == "tora");
        Assert.Equal(["Position X", "Position Y", "Rotation"], tora.Rows.Select(r => r.Label));
        Assert.Equal([0.9, 1.35, 1.8], tora.Rows[1].Keys.Select(k => k.Time));
        Assert.Equal([Easing.EaseOut, Easing.EaseIn, Easing.Linear], tora.Rows[1].Keys.Select(k => k.Easing));
        var head = layers.Single(l => l.LayerId == "tora_head");
        Assert.Equal(["Position Y", "Rotation", "Drawing"], head.Rows.Select(r => r.Label));
        Assert.Equal(["normal", "happy", "normal"], head.Rows[2].Keys.Select(k => k.Name));

        // Values at the playhead, as the inspector shows them.
        Assert.Equal("605", AnimateModel.ValueAt(project, tora.Rows[1], 0.5));
        Assert.Equal("360", AnimateModel.ValueAt(project, tora.Rows[1], 1.35));
        Assert.Equal("5°", AnimateModel.ValueAt(project, tora.Rows[2], 1.15));
        var body = layers.Single(l => l.LayerId == "tora_body");
        Assert.Equal("125%", AnimateModel.ValueAt(project, body.Rows[0], 0.9));
        Assert.Equal("happy", AnimateModel.ValueAt(project, head.Rows[2], 1));

        // The selected layer is listed even when nothing of it moves.
        Assert.Equal("ground", AnimateModel.Build(project, "ground")[1].LayerId);
        Assert.Empty(AnimateModel.Build(project, "ground")[1].Rows);
    }

    [Fact]
    public void TheCameraComesFirstWhenItIsAnimated()
    {
        var project = ToraJump();
        KeyframeEditor.SetKeyframes(project,
        [
            new KeyframeEntry("camera", "zoom", 0, System.Text.Json.JsonSerializer.SerializeToElement(1)),
            new KeyframeEntry("camera", "zoom", 2, System.Text.Json.JsonSerializer.SerializeToElement(1.5)),
        ]);
        var camera = AnimateModel.Build(project, null)[0];
        Assert.Equal(("camera", "camera"), (camera.LayerId, camera.Kind));
        Assert.Equal("Zoom", camera.Rows.Single().Label);
        Assert.Equal("125%", AnimateModel.ValueAt(project, camera.Rows[0], 1));
    }

    [Fact]
    public void TheEasingTilesAreCoresEightNamedEasings()
    {
        Assert.Equal(Enum.GetValues<Easing>().Where(e => e != Easing.CubicBezier).Order(), AnimateModel.Tiles.Select(t => t.Easing).Order());
        Assert.Equal(["Linear", "Ease in", "Ease out", "In-out", "Back", "Bounce", "Elastic", "Hold"], AnimateModel.Tiles.Select(t => t.Name));

        // Each tile draws its easing's own curve.
        var back = AnimateModel.Curve(Easing.EaseOutBack, null, 20);
        Assert.Equal(21, back.Count);
        Assert.Equal(EasingFunctions.Apply(Easing.EaseOutBack, 0.5), back[10].Value);
        Assert.True(back.Max(p => p.Value) > 1);
        Assert.Equal(0.765988682530208, AnimateModel.Curve(Easing.CubicBezier, [0, 0, 0.4, 1], 2)[1].Value, 1e-9);
    }

    [Fact]
    public void TheOnionSkinDrawsTheFramesAroundThePlayheadNearerStronger()
    {
        var ghosts = AnimateModel.Ghosts(10, 90, 3, 2);
        Assert.Equal([7, 8, 9, 12, 11], ghosts.Select(g => g.Frame));
        Assert.Equal([false, false, false, true, true], ghosts.Select(g => g.After));
        Assert.Equal([0.125, 0.25, 0.375], ghosts.Take(3).Select(g => g.Opacity));
        Assert.Equal([1 / 6.0, 1 / 3.0], ghosts.Skip(3).Select(g => g.Opacity));

        // None before the first frame or after the last.
        Assert.Equal([0, 2], AnimateModel.Ghosts(1, 3, 3, 2).Select(g => g.Frame));
        Assert.Equal(["tora", "tora_body", "tora_head", "tora_tail"], AnimateModel.WithChildren(ToraJump(), "tora").Order());
    }

    [Fact]
    public void TheMotionPathIsWhereTheAnchorIsAtEachFrame()
    {
        var project = ToraJump();
        var tora = (VisualLayer)project.FindLayer("tora")!;
        var (path, keys) = AnimateModel.MotionPath(project, "tora");
        Assert.Equal(90, path.Count);
        for (var frame = 0; frame < 90; frame++)
        {
            var time = FrameMath.TimeOf(project, frame);
            Assert.Equal((Interpolator.Evaluate(tora.X, time), Interpolator.Evaluate(tora.Y, time)), (path[frame].X, path[frame].Y));
        }

        Assert.Equal([(780.0, 605.0), (880.0, 360.0), (980.0, 605.0)], keys.Select(k => (Math.Round(k.X, 9), Math.Round(k.Y, 9))));

        // A child goes where its parents take it; a layer that stays put has none.
        Assert.Equal(90, AnimateModel.MotionPath(project, "tora_tail").Path.Count);
        Assert.Empty(AnimateModel.MotionPath(project, "tora_tail").Keys);
        Assert.Empty(AnimateModel.MotionPath(project, "ground").Path);
    }
}
