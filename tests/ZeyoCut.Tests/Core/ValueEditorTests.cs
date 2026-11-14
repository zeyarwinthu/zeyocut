// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// The inspector's animatable numbers beyond placement: effects and color, and the diamond button that
// starts and ends animating a value.
public class ValueEditorTests
{
    private static readonly EffectValues Defaults = new(null, null, null, null, null, null, null);

    private static T Layer<T>(Project project, string id) where T : Layer => Assert.IsType<T>(project.FindLayer(id));

    private static string Error(Action action) => Assert.Throws<ZeyoCutException>(action).Message;

    private static Dictionary<string, double> Values(string property, double value) => new() { [property] = value };

    private static KeyframeEntry Key(string layerId, string property, double time, double value) => new(layerId, property, time, JsonSerializer.SerializeToElement(value));

    [Fact]
    public void EffectAndColorValuesChangeAsTheirToolsChangeThem()
    {
        var project = TestProjects.Footage();
        EffectEditor.SetEffect(project, "clip_1", EffectKind.Blur, Defaults, remove: false);
        EffectEditor.SetEffect(project, "clip_1", EffectKind.Shadow, Defaults, remove: false);

        var blur = ValueEditor.Set(project, "clip_1", Values("blur.x", 10), 1);
        Assert.Equal("set_effect clip_1 blur", blur.Label);
        Assert.Equal("updated blur on 'clip_1': x 10, y 4", blur.Apply(project));

        var brightness = ValueEditor.Set(project, "clip_1", Values("brightness", 0.2), 1);
        Assert.Equal("update_layer clip_1", brightness.Label);
        brightness.Apply(project);
        var both = ValueEditor.Set(project, "clip_1", new Dictionary<string, double> { ["contrast"] = 1.5, ["shadow.opacity"] = 0.3 }, 1);
        Assert.Equal(("update_layer clip_1", 2), (both.Label, ((BatchOperation)both).Operations.Count));
        both.Apply(project);
        var clip = Layer<VideoLayer>(project, "clip_1");
        Assert.Equal((10.0, 4.0, 0.2, 1.5, 0.3), (clip.Blur!.X.Value, clip.Blur.Y.Value, clip.Brightness.Value, clip.Contrast.Value, clip.Shadow!.Opacity.Value));

        // Animated, an effect's number gets a keyframe at the playhead.
        KeyframeEditor.SetKeyframes(project, [Key("clip_1", "shadow.opacity", 0, 0), Key("clip_1", "shadow.opacity", 2, 1)]);
        var keyed = ValueEditor.Set(project, "clip_1", Values("shadow.opacity", 0.8), 1);
        Assert.Equal("set_keyframes clip_1", keyed.Label);
        keyed.Apply(project);
        Assert.Equal([0.0, 0.8, 1], Layer<VideoLayer>(project, "clip_1").Shadow!.Opacity.Keyframes.Select(k => k.Value));

        Assert.Equal("'clip_1' has no tint effect; switch it on first", Error(() => ValueEditor.Set(project, "clip_1", Values("tint.amount", 1), 1)));
        Assert.Equal("'title' has no value 'brightness'", Error(() => ValueEditor.Set(project, "title", Values("brightness", 1), 1)));
        Assert.StartsWith("cropX isn't changed here", Error(() => ValueEditor.Set(project, "clip_1", Values("cropX", 1), 1)));
    }

    [Fact]
    public void TheDiamondStartsAndEndsAnimatingAValue()
    {
        var project = TestProjects.Footage();
        var x = Layer<TextLayer>(project, "title").X.Value;

        // The first keyframe holds the value shown; a change elsewhere then animates it.
        var first = ValueEditor.ToggleKeyframe(project, "title", "x", 1);
        Assert.Equal("set_keyframes title", first.Label);
        Assert.Equal("set 1 keyframe(s) at 1s on title.x", first.Apply(project));
        ValueEditor.Set(project, "title", Values("x", x + 100), 2).Apply(project);
        ValueEditor.ToggleKeyframe(project, "title", "x", 1.5).Apply(project);
        Assert.Equal([(1.0, x), (1.5, x + 50), (2, x + 100)], Layer<TextLayer>(project, "title").X.Keyframes.Select(k => (k.Time, k.Value)));

        // At a keyframe it takes it away; the last one leaves its value as the static one.
        var remove = ValueEditor.ToggleKeyframe(project, "title", "x", 1.5);
        Assert.Equal(("remove_keyframes title", "removed 1 keyframe(s) at 1.5s from title.x"), (remove.Label, remove.Apply(project)));
        ValueEditor.ToggleKeyframe(project, "title", "x", 1).Apply(project);
        var last = ValueEditor.ToggleKeyframe(project, "title", "x", 2);
        Assert.Equal("remove_keyframes title", last.Label);
        Assert.IsType<BatchOperation>(last);
        last.Apply(project);
        Assert.Equal((x + 100, 0), (Layer<TextLayer>(project, "title").X.Value, Layer<TextLayer>(project, "title").X.Keyframes.Count));

        Assert.Equal("A keyframe goes at the playhead, and 'title' isn't there; move the playhead into 0–3s, where the layer is",
            Error(() => ValueEditor.ToggleKeyframe(project, "title", "opacity", 5)));

        // An effect's number too; with the static value already the keyframe's, removing is all.
        EffectEditor.SetEffect(project, "clip_1", EffectKind.Blur, Defaults, remove: false);
        ValueEditor.ToggleKeyframe(project, "clip_1", "blur.y", 1).Apply(project);
        Assert.IsType<RemoveKeyframesOperation>(ValueEditor.ToggleKeyframe(project, "clip_1", "blur.y", 1));
    }

    [Fact]
    public void AKeyframeBetweenFramesIsAtTheFrameNearestIt()
    {
        // At 30 fps, 1.01s is nearest frame 30 (1s): there a change moves it, and the diamond takes it away.
        var project = TestProjects.Footage();
        KeyframeEditor.SetKeyframes(project, [Key("title", "x", 0, 0), Key("title", "x", 1.01, 100)]);
        var x = Layer<TextLayer>(project, "title").X;
        Assert.Equal(1.01, ValueEditor.KeyAt(project, x, 1)!.Time);
        Assert.Null(ValueEditor.KeyAt(project, x, 1.04));

        ValueEditor.Set(project, "title", Values("x", 50), 1).Apply(project);
        Assert.Equal([(0.0, 0.0), (1.01, 50)], Layer<TextLayer>(project, "title").X.Keyframes.Select(k => (k.Time, k.Value)));
        Assert.Equal("removed 1 keyframe(s) at 1.01s from title.x", ValueEditor.ToggleKeyframe(project, "title", "x", 1).Apply(project));
    }

    [Fact]
    public void KeyTimesAreEveryTimeALayerHasAKeyframe()
    {
        var project = TestProjects.Footage();
        Assert.Empty(ValueEditor.KeyTimes(project, "title"));
        KeyframeEditor.SetKeyframes(project, [Key("title", "x", 2, 1), Key("title", "x", 1, 0), Key("title", "rotation", 2, 5), Key("title", "opacity", 0.5, 1)]);
        Assert.Equal([0.5, 1, 2], ValueEditor.KeyTimes(project, "title"));
        Assert.Empty(ValueEditor.KeyTimes(project, "music"));
        Assert.Empty(ValueEditor.KeyTimes(project, "nothing"));
    }
}
