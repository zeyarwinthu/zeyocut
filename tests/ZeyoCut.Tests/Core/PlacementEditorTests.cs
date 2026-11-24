// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// What the editor's Transform and Crop fields and a drag in its preview change.
public class PlacementEditorTests
{
    private static T Layer<T>(Project project, string id) where T : Layer => Assert.IsType<T>(project.FindLayer(id));

    private static string Error(Action action) => Assert.Throws<ZeyoCutException>(action).Message;

    // Where a layer draws a point of its own pixels at time.
    private static (double X, double Y) Drawn(Project project, string id, double x, double y, double time) =>
        SceneEvaluator.Evaluate(project, time).Single(e => e.Layer.Id == id).World.Apply(x, y);

    private static void Near((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    [Fact]
    public void AValueWithoutKeyframesChangesAndOneWithThemGetsAKeyframeAtThePlayhead()
    {
        var project = TestProjects.Footage();
        var plain = PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["x"] = 100, ["opacity"] = 0.5 }, 1);
        Assert.Equal("update_layer title", plain.Label);
        Assert.Equal(["title"], plain.Layers);
        Assert.Equal("updated layer 'title'", plain.Apply(project));
        Assert.Equal((100.0, 0.5), (Layer<TextLayer>(project, "title").X.Value, Layer<TextLayer>(project, "title").Opacity.Value));

        // x slides in, easing in and out; a change at 1 s adds a keyframe there with that easing, and
        // y, which doesn't move, just changes.
        KeyframeEditor.SetKeyframes(project, [Key("x", 0, 0, "easeInOut"), Key("x", 2, 200, null)]);
        var both = PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["x"] = 150, ["y"] = 50 }, 1);
        Assert.Equal("set_keyframes title", both.Label);
        Assert.Equal("updated layer 'title'; set 1 keyframe(s) at 1s on title.x", both.Apply(project));
        var title = Layer<TextLayer>(project, "title");
        Assert.Equal([(0.0, 0.0, Easing.EaseInOut), (1, 150, Easing.EaseInOut), (2, 200, Easing.Linear)], title.X.Keyframes.Select(k => (k.Time, k.Value, k.Easing)));
        Assert.Equal((50.0, 0), (title.Y.Value, title.Y.Keyframes.Count));

        // Again at 1 s replaces it; before the first keyframe the new one is linear.
        var again = PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["x"] = 120 }, 1);
        Assert.IsType<SetKeyframesOperation>(again);
        again.Apply(project);
        PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["opacity"] = 1 }, 0.5).Apply(project);
        KeyframeEditor.SetKeyframes(project, [Key("rotation", 1, 10, "easeOut")]);
        PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["rotation"] = -5 }, 0.25).Apply(project);
        title = Layer<TextLayer>(project, "title");
        Assert.Equal([0.0, 120, 200], title.X.Keyframes.Select(k => k.Value));
        Assert.Equal([(0.25, -5.0, Easing.Linear), (1, 10, Easing.EaseOut)], title.Rotation.Keyframes.Select(k => (k.Time, k.Value, k.Easing)));

        // A keyframe goes where the layer is; a value without keyframes changes anywhere.
        Assert.Equal("'title' animates x with keyframes, and a change adds one at the playhead; move the playhead into 0–3s, where the layer is",
            Error(() => PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["x"] = 1 }, 5)));
        PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["scaleX"] = 2 }, 5).Apply(project);
        Assert.Equal(2, Layer<TextLayer>(project, "title").ScaleX.Value);
        Assert.Equal("'size' isn't one of x, y, rotation, scaleX, scaleY, opacity", Error(() => PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["size"] = 1 }, 0)));
        Assert.Equal("there are no values to set", Error(() => PlacementEditor.Set(project, "title", new Dictionary<string, double>(), 0)));
    }

    [Fact]
    public void ALayerOnAMotionPathKeepsItsPlace()
    {
        var project = TestProjects.Footage();
        var title = Layer<TextLayer>(project, "title");
        title.MotionPath = new MotionPath { Points = [new PathPoint { X = 0, Y = 0 }, new PathPoint { X = 100, Y = 100 }] };
        Assert.Null(PlacementEditor.PositionLocked(Layer<VideoLayer>(project, "clip_1")));
        var locked = "'title' follows a motion path, which places it; ask Claude to change the path (set_motion_path), or to take it off first";
        Assert.Equal(locked, PlacementEditor.PositionLocked(title));
        Assert.Equal(locked, Error(() => PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["y"] = 5 }, 1)));
        PlacementEditor.Set(project, "title", new Dictionary<string, double> { ["opacity"] = 0.25 }, 1).Apply(project);
        Assert.Equal(0.25, Layer<TextLayer>(project, "title").Opacity.Value);
        Assert.NotNull(Layer<TextLayer>(project, "title").MotionPath);
    }

    [Fact]
    public void ShiftingMovesLayersAtEveryMoment()
    {
        var project = TestProjects.Footage();
        TestProjects.AddCaptions(project);
        var cues = project.Layers.Where(l => l.ParentId == "captions_en").Select(l => l.Id).ToList();
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry(cues[1], "y", 2, JsonSerializer.SerializeToElement(300.0)), new KeyframeEntry(cues[1], "y", 3, JsonSerializer.SerializeToElement(310.0))]);
        Layer<TextLayer>(project, "title").MotionPath = new MotionPath { Points = [new PathPoint { X = 0, Y = 0, OutX = 5, OutY = 5 }, new PathPoint { X = 100, Y = 100 }] };
        var before = cues.Select(id => (Layer<TextLayer>(project, id).X.Value, Layer<TextLayer>(project, id).Y.Value)).ToList();

        var shift = new ShiftLayersOperation("update_layer captions_en", [.. cues, "title"], 10, -20);
        Assert.Equal(("update_layer captions_en", 3), (shift.Label, shift.Layers.Count));
        Assert.Equal("moved 3 layer(s) by +10, -20 px", shift.Apply(project));
        Assert.Equal(before.Select(b => (b.Item1 + 10, b.Item2 - 20)), cues.Select(id => (Layer<TextLayer>(project, id).X.Value, Layer<TextLayer>(project, id).Y.Value)));
        Assert.Equal([280.0, 290], Layer<TextLayer>(project, cues[1]).Y.Keyframes.Select(k => k.Value));
        var path = Layer<TextLayer>(project, "title").MotionPath!.Points;
        Assert.Equal([(10.0, -20.0), (110, 80)], path.Select(p => (p.X, p.Y)));
        Assert.Equal((5.0, 5.0), (path[0].OutX!.Value, path[0].OutY!.Value));
        Assert.Equal("there are no layers to move", Error(() => PlacementEditor.Shift(project, [], 1, 1)));
    }

    [Fact]
    public void CroppingKeepsWhatRemainsOfThePictureInPlace()
    {
        var project = TestProjects.Footage();
        var clip = Layer<VideoLayer>(project, "clip_2");
        clip.Rotation.Value = 30;
        var source = (X: 400.0, Y: 200.0);
        var pixel = Drawn(project, "clip_2", source.X, source.Y, 5);
        var label = Drawn(project, "label", 0, 0, 5);
        var anchor = (clip.AnchorX, clip.AnchorY);
        Assert.Equal(new CropSides(0, 0, 0, 0), PlacementEditor.Sides(clip, project.FindAsset("clip")!));

        var crop = new CropLayerOperation("clip_2", new CropSides(100, 50, 40, 10));
        Assert.Equal("crop_layer clip_2", crop.Label);
        Assert.Equal(["clip_2"], crop.Layers);
        Assert.Equal("cropped 'clip_2': it shows 500×300 px of its 640×360 source (left 100, top 50, right 40, bottom 10); 1 child layer(s) moved with the picture", crop.Apply(project));
        clip = Layer<VideoLayer>(project, "clip_2");
        Assert.Equal((100.0, 50.0, 500.0, 300.0), (clip.CropX.Value, clip.CropY.Value, clip.CropWidth!.Value, clip.CropHeight!.Value));
        Assert.Equal(new CropSides(100, 50, 40, 10), PlacementEditor.Sides(clip, project.FindAsset("clip")!));
        Near(pixel, Drawn(project, "clip_2", source.X - 100, source.Y - 50, 5));
        Near(label, Drawn(project, "label", 0, 0, 5));
        Assert.Empty(ProjectValidator.Validate(project));

        // Taking the crop off shows the whole frame where it was before.
        Assert.Equal("cropped 'clip_2': it shows the whole frame again (left 0, top 0, right 0, bottom 0); 1 child layer(s) moved with the picture",
            new CropLayerOperation("clip_2", new CropSides(0, 0, 0, 0)).Apply(project));
        clip = Layer<VideoLayer>(project, "clip_2");
        Assert.Equal((anchor.AnchorX, anchor.AnchorY, (double?)null, (double?)null), (clip.AnchorX, clip.AnchorY, clip.CropWidth, clip.CropHeight));
        Near(pixel, Drawn(project, "clip_2", source.X, source.Y, 5));
    }

    [Fact]
    public void ACropThatCantBeMadeSaysWhy()
    {
        var project = TestProjects.Footage();
        Assert.Equal("the left crop can't be less than 0 (got -1)", Error(() => PlacementEditor.Crop(project, "clip_1", new CropSides(-1, 0, 0, 0))));
        Assert.Equal("that leaves 0×360 px of the 640×360 source; at least 1 px each way must stay", Error(() => PlacementEditor.Crop(project, "clip_1", new CropSides(320, 0, 320, 0))));
        Assert.Equal("'title' isn't a video layer; only video is cropped", Error(() => PlacementEditor.Crop(project, "title", new CropSides(1, 0, 0, 0))));
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("clip_1", "cropX", 1, JsonSerializer.SerializeToElement(10.0))]);
        Assert.Equal("'clip_1' has a moving crop (cropX or cropY keyframes); change it with set_keyframes, or clear them first", Error(() => PlacementEditor.Crop(project, "clip_1", new CropSides(1, 0, 0, 0))));
        project.FindAsset("clip")!.Width = null;
        Assert.Equal("the size of 'clip' isn't known, so its crop can't be set by its sides", PlacementEditor.CropLocked(project, Layer<VideoLayer>(project, "clip_2")));
    }

    private static KeyframeEntry Key(string property, double time, double value, string? easing) =>
        new("title", property, time, JsonSerializer.SerializeToElement(value), easing);
}
