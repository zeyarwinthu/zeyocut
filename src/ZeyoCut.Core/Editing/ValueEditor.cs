// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// A layer's animatable numbers as the editor's inspector changes them: x, y, rotation, scale and
// opacity, its effects' numbers (shadow.x, blur.x, ...) and its color adjustments. A value with
// keyframes changes by a keyframe at the playhead, one without by its static value (update_layer, or
// set_effect for an effect's). ToggleKeyframe starts and ends animating a value by hand. A motion
// path's progress, a video's crop position and a mask's box are changed elsewhere.
public static class ValueEditor
{
    private static readonly Dictionary<string, EffectKind> EffectNames = new(StringComparer.Ordinal)
    {
        ["shadow"] = EffectKind.Shadow,
        ["outline"] = EffectKind.Outline,
        ["blur"] = EffectKind.Blur,
        ["tint"] = EffectKind.Tint,
    };

    // The edit that gives the layer these values at time. A value with keyframes gets a keyframe at
    // time, as set_keyframes adds one, or the keyframe on that frame changes (KeyAt); it keeps the
    // easing of the keyframe it replaces or the one before it, so the motion keeps its feel, and the
    // layer must be on the timeline then. A value without
    // keyframes changes its static value. x and y of a layer on a motion path aren't set: the path
    // places it, and changing them would take the path off.
    public static EditOperation Set(Project project, string layerId, IReadOnlyDictionary<string, double> values, double time)
    {
        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        if (values.Count == 0)
        {
            throw new ZeyoCutException("there are no values to set");
        }

        var statics = new Dictionary<string, double>(StringComparer.Ordinal);
        var keys = new List<KeyframeEntry>();
        foreach (var (property, value) in values)
        {
            var track = Track(layer, property);
            if (!double.IsFinite(value))
            {
                throw new ZeyoCutException($"{property} must be a finite number (got {value})");
            }

            if (track.Keyframes.Count == 0)
            {
                statics[property] = value;
                continue;
            }

            if (!SceneEvaluator.IsActive(layer, time, project.Duration))
            {
                throw new ZeyoCutException($"'{layer.Id}' animates {property} with keyframes, and a change adds one at the playhead; {MoveInto(project, layer)}");
            }

            var at = KeyAt(project, track, time)?.Time ?? time;
            keys.Add(new KeyframeEntry(layer.Id, property, at, JsonSerializer.SerializeToElement(value), EnumNames.ToName(EasingAt(track, at))));
        }

        var operations = Statics(layer.Id, statics);
        if (keys.Count > 0)
        {
            operations.Add(new SetKeyframesOperation(keys));
        }

        return operations.Count == 1 ? operations[0] : new BatchOperation($"{(keys.Count > 0 ? "set_keyframes" : "update_layer")} {layer.Id}", operations);
    }

    // The diamond button: adds a keyframe at time with the value shown there, which starts animating
    // the value, or takes away the keyframe on that frame (KeyAt). When that was the last one, the
    // value stays as it was there, as its static value.
    public static EditOperation ToggleKeyframe(Project project, string layerId, string property, double time)
    {
        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        var track = Track(layer, property);
        if (KeyAt(project, track, time) is { } here)
        {
            var remove = new RemoveKeyframesOperation([new KeyframeRef(layer.Id, property, here.Time)]);
            if (track.Keyframes.Count > 1 || track.Value == here.Value)
            {
                return remove;
            }

            return new BatchOperation($"remove_keyframes {layer.Id}", [remove, .. Statics(layer.Id, new Dictionary<string, double> { [property] = here.Value })]);
        }

        if (!SceneEvaluator.IsActive(layer, time, project.Duration))
        {
            throw new ZeyoCutException($"A keyframe goes at the playhead, and '{layer.Id}' isn't there; {MoveInto(project, layer)}");
        }

        var value = Interpolator.Evaluate(track, time);
        return new SetKeyframesOperation([new KeyframeEntry(layer.Id, property, time, JsonSerializer.SerializeToElement(value), EnumNames.ToName(EasingAt(track, time)))]);
    }

    // One diamond for several values, such as x and y: when each has a keyframe at time they are all
    // taken away, otherwise those without one get one, as one step.
    public static EditOperation ToggleKeyframes(Project project, string layerId, IReadOnlyList<string> properties, double time)
    {
        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        var missing = properties.Where(p => KeyAt(project, Track(layer, p), time) is null).ToList();
        if (missing.Count == 0)
        {
            var removals = properties.Select(p => ToggleKeyframe(project, layerId, p, time)).ToList();
            return removals.Count == 1 ? removals[0] : new BatchOperation($"remove_keyframes {layer.Id}", removals);
        }

        if (!SceneEvaluator.IsActive(layer, time, project.Duration))
        {
            throw new ZeyoCutException($"A keyframe goes at the playhead, and '{layer.Id}' isn't there; {MoveInto(project, layer)}");
        }

        return new SetKeyframesOperation(missing.Select(p =>
        {
            var track = Track(layer, p);
            return new KeyframeEntry(layer.Id, p, time, JsonSerializer.SerializeToElement(Interpolator.Evaluate(track, time)), EnumNames.ToName(EasingAt(track, time)));
        }).ToList());
    }

    // The keyframe on the frame at time: the playhead is on a frame, and a keyframe between frames
    // belongs to the frame nearest it. The nearest when there are more; null when there's none.
    public static Keyframe? KeyAt(Project project, AnimatedValue value, double time) =>
        value.Keyframes.Where(k => Frame(project, k.Time) == Frame(project, time)).MinBy(k => Math.Abs(k.Time - time));

    // The times at which any of the layer's values has a keyframe, in order.
    public static IReadOnlyList<double> KeyTimes(Project project, string layerId)
    {
        if (project.FindLayer(layerId) is not VisualLayer layer)
        {
            return [];
        }

        var times = AnimatableProperties.Numeric(layer).SelectMany(p => p.Value.Keyframes.Select(k => k.Time));
        if (layer is ImageLayer image)
        {
            times = times.Concat(image.Drawing.Keyframes.Select(k => k.Time));
        }

        var distinct = new List<double>();
        foreach (var time in times.Order())
        {
            if (distinct.Count == 0 || time - distinct[^1] >= KeyframeEditor.TimeTolerance)
            {
                distinct.Add(time);
            }
        }

        return distinct;
    }

    internal static ZeyoCutException OnPath(VisualLayer layer) =>
        new($"'{layer.Id}' follows a motion path, which places it; ask Claude to change the path (set_motion_path), or to take it off first");

    // The value, refused when it's changed another way.
    private static AnimatedValue Track(VisualLayer layer, string property)
    {
        if (property == AnimatableProperties.Path || property is "cropX" or "cropY" || property.StartsWith("mask.", StringComparison.Ordinal))
        {
            throw new ZeyoCutException($"{property} isn't changed here: a motion path's timing comes from set_motion_path, a crop from the Crop fields, and a mask from set_mask");
        }

        if (property is "x" or "y" && layer.MotionPath is not null)
        {
            throw OnPath(layer);
        }

        return AnimatableProperties.Numeric(layer).Where(p => p.Name == property).Select(p => p.Value).FirstOrDefault()
            ?? throw new ZeyoCutException(property.Contains('.')
                ? $"'{layer.Id}' has no {property[..property.IndexOf('.')]} effect; switch it on first"
                : $"'{layer.Id}' has no value '{property}'");
    }

    // Static values: the layer's own as update_layer sets them, an effect's as set_effect does.
    private static List<EditOperation> Statics(string layerId, IReadOnlyDictionary<string, double> values)
    {
        var operations = new List<EditOperation>();
        var own = new JsonObject();
        foreach (var (property, value) in values.Where(v => !v.Key.Contains('.')))
        {
            own[property] = value;
        }

        if (own.Count > 0)
        {
            operations.Add(new UpdateLayerOperation(layerId, own));
        }

        foreach (var effect in values.Where(v => v.Key.Contains('.')).GroupBy(v => v.Key[..v.Key.IndexOf('.')]))
        {
            double? Field(string name) => effect.FirstOrDefault(v => v.Key == $"{effect.Key}.{name}") is { Key: not null } found ? found.Value : null;
            var kind = EffectNames[effect.Key];
            var x = kind is EffectKind.Shadow or EffectKind.Blur ? Field("x") : null;
            var y = kind is EffectKind.Shadow or EffectKind.Blur ? Field("y") : null;
            operations.Add(new SetEffectOperation(layerId, kind, new EffectValues(null, x, y, Field("blur"), Field("opacity"), Field("width"), Field("amount"))));
        }

        return operations;
    }

    private static double Frame(Project project, double time) => Math.Round(time * project.Fps, MidpointRounding.AwayFromZero);

    // The easing of the keyframe at time, or else of the one before it: it shapes the segment the new
    // keyframe starts.
    private static Easing EasingAt(AnimatedValue track, double time) =>
        track.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < KeyframeEditor.TimeTolerance)?.Easing
        ?? track.Keyframes.LastOrDefault(k => k.Time < time)?.Easing
        ?? Easing.Linear;

    private static string MoveInto(Project project, VisualLayer layer) =>
        $"move the playhead into {Num(layer.Start)}–{Num(SceneEvaluator.EndOf(layer, project.Duration))}s, where the layer is";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
