// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// A keyframe as the Animate view shows it: its time and its number (Value) or drawing (Name), and
// the easing of the segment it starts.
public sealed record AnimateKey(string LayerId, string Property, double Time, double Value, string? Name, Easing Easing, double[]? Bezier)
{
    public KeyframeRef Ref => new(LayerId, Property, Time);

    public bool Drawing => Name is not null;
}

// One animated value of a layer or the camera: a number with keyframes, or an image layer's drawing
// changes. Factor and Unit show it as the inspector does (a scale of 1.2 as 120%).
public sealed record AnimateRow(string LayerId, string Property, string Label, string Unit, double Factor, IReadOnlyList<AnimateKey> Keys)
{
    public string Key => $"{LayerId}.{Property}";

    public bool Drawing => Property == AnimatableProperties.Drawing;

    // A value that grows down the frame, such as Position Y: the graph draws it upside down, so its
    // curve goes the way the layer moves.
    public bool Downward => Property is "y" or "shadow.y" or "mask.y" or "shakeY";

    // A value as the property tree and the status line show it: 794.8, 120.1%, 5°.
    public string Format(double value) =>
        (value * Factor).ToString("0.#", CultureInfo.InvariantCulture) + (Unit == "px" ? "" : Unit);
}

// A layer in the property tree, with how deep it is under its parents and what kind it is ("group"
// for an image layer without drawings, which only carries its children).
public sealed record AnimateLayer(string LayerId, string Kind, int Depth, IReadOnlyList<AnimateRow> Rows);

// A tile of the Easing panel: its curve drawn in a 44 by 32 box, and whether the selected keyframes
// all have it (Chosen) or there are any to give it (Enabled).
public sealed record EasingTile(Easing Easing, string Name, Geometry Curve, bool Chosen, bool Enabled)
{
    public string Tip => $"The selected keyframes' segments {Name.ToLowerInvariant()}, as set_keyframes names it: {AnimateModel.EasingName(Easing)}";
}

// A frame the onion skin draws: how strongly, and whether it's after the playhead (tinted).
public sealed record OnionGhost(int Frame, double Opacity, bool After);

// What the Animate view shows of a project: its layers' animated values and their keyframes, read
// from the project as Core interpolates them. The view changes keyframes only through Core's
// operations (MoveKeyframesOperation, SetEasingOperation and the inspector's).
public static class AnimateModel
{
    // The Easing panel's eight tiles: Core's named easings, Back, Bounce and Elastic being its
    // ease-out kinds.
    public static readonly IReadOnlyList<(Easing Easing, string Name)> Tiles =
    [
        (Easing.Linear, "Linear"),
        (Easing.EaseIn, "Ease in"),
        (Easing.EaseOut, "Ease out"),
        (Easing.EaseInOut, "In-out"),
        (Easing.EaseOutBack, "Back"),
        (Easing.EaseOutBounce, "Bounce"),
        (Easing.EaseOutElastic, "Elastic"),
        (Easing.Hold, "Hold"),
    ];

    private static readonly Dictionary<string, (string Label, string Unit, double Factor)> Names = new(StringComparer.Ordinal)
    {
        ["x"] = ("Position X", "px", 1),
        ["y"] = ("Position Y", "px", 1),
        ["rotation"] = ("Rotation", "°", 1),
        ["scaleX"] = ("Scale X", "%", 100),
        ["scaleY"] = ("Scale Y", "%", 100),
        ["opacity"] = ("Opacity", "%", 100),
        ["brightness"] = ("Brightness", "%", 100),
        ["contrast"] = ("Contrast", "%", 100),
        ["saturation"] = ("Saturation", "%", 100),
        ["grade.exposure"] = ("Exposure", "", 1),
        ["grade.highlights"] = ("Highlights", "%", 100),
        ["grade.shadows"] = ("Shadows", "%", 100),
        ["grade.temperature"] = ("Temperature", "%", 100),
        ["grade.tint"] = ("Color tint", "%", 100),
        ["grade.lift.red"] = ("Shadows red", "", 100),
        ["grade.lift.green"] = ("Shadows green", "", 100),
        ["grade.lift.blue"] = ("Shadows blue", "", 100),
        ["grade.gamma.red"] = ("Midtones red", "", 100),
        ["grade.gamma.green"] = ("Midtones green", "", 100),
        ["grade.gamma.blue"] = ("Midtones blue", "", 100),
        ["grade.gain.red"] = ("Highlights red", "", 100),
        ["grade.gain.green"] = ("Highlights green", "", 100),
        ["grade.gain.blue"] = ("Highlights blue", "", 100),
        ["grade.look.amount"] = ("Look amount", "%", 100),
        ["cropX"] = ("Crop X", "px", 1),
        ["cropY"] = ("Crop Y", "px", 1),
        ["shadow.x"] = ("Shadow X", "px", 1),
        ["shadow.y"] = ("Shadow Y", "px", 1),
        ["shadow.blur"] = ("Shadow blur", "px", 1),
        ["shadow.opacity"] = ("Shadow opacity", "%", 100),
        ["outline.width"] = ("Outline width", "px", 1),
        ["blur.x"] = ("Blur X", "px", 1),
        ["blur.y"] = ("Blur Y", "px", 1),
        ["tint.amount"] = ("Tint", "%", 100),
        ["mask.x"] = ("Mask X", "px", 1),
        ["mask.y"] = ("Mask Y", "px", 1),
        ["mask.width"] = ("Mask width", "px", 1),
        ["mask.height"] = ("Mask height", "px", 1),
        ["mask.feather"] = ("Mask feather", "px", 1),
        ["zoom"] = ("Zoom", "%", 100),
        ["shakeX"] = ("Shake X", "px", 1),
        ["shakeY"] = ("Shake Y", "px", 1),
        ["shakeRotation"] = ("Shake rotation", "°", 1),
        [AnimatableProperties.Drawing] = ("Drawing", "", 1),
    };

    // The camera when it has keyframes, then the visual layers with animated values, each followed by
    // its children, and the selected layer even when nothing of it is animated yet. A motion path's
    // timing isn't listed: set_motion_path makes it, and the preview draws the path.
    public static IReadOnlyList<AnimateLayer> Build(Project project, string? selected)
    {
        var result = new List<AnimateLayer>();
        if (project.Camera is { } camera)
        {
            var rows = AnimatableProperties.Numeric(camera).Where(p => p.Value.Keyframes.Count > 0).Select(p => Row(Camera.Id, p.Name, p.Value)).ToList();
            if (rows.Count > 0)
            {
                result.Add(new AnimateLayer(Camera.Id, "camera", 0, rows));
            }
        }

        var ids = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var children = project.Layers.Where(l => l.ParentId is { } parent && ids.Contains(parent)).ToLookup(l => l.ParentId!, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(Layer layer, int depth)
        {
            if (!visited.Add(layer.Id))
            {
                return;
            }

            if (layer is VisualLayer visual)
            {
                var rows = Rows(visual);
                if (rows.Count > 0 || layer.Id == selected)
                {
                    result.Add(new AnimateLayer(layer.Id, layer is ImageLayer { Drawings.Count: 0 } ? "group" : layer.TypeName, depth, rows));
                }
            }

            foreach (var child in children[layer.Id])
            {
                Visit(child, depth + 1);
            }
        }

        foreach (var layer in project.Layers.Where(l => l.ParentId is not { } parent || !ids.Contains(parent)))
        {
            Visit(layer, 0);
        }

        return result;
    }

    // The value a row animates, or null for a drawing row or one that's gone.
    public static AnimatedValue? Value(Project project, string layerId, string property)
    {
        if (layerId == Camera.Id)
        {
            return project.Camera is { } camera ? AnimatableProperties.Numeric(camera).FirstOrDefault(p => p.Name == property).Value : null;
        }

        return project.FindLayer(layerId) is VisualLayer layer ? AnimatableProperties.Numeric(layer).FirstOrDefault(p => p.Name == property).Value : null;
    }

    // A row's value at a time, as the property tree shows it: a number, or the drawing shown.
    public static string ValueAt(Project project, AnimateRow row, double time)
    {
        if (row.Drawing)
        {
            return project.FindLayer(row.LayerId) is ImageLayer image ? Interpolator.Evaluate(image.Drawing, time) : "";
        }

        return Value(project, row.LayerId, row.Property) is { } value ? row.Format(Interpolator.Evaluate(value, time)) : "";
    }

    // The Easing panel's tiles for the selected keys: the one they all have is chosen.
    public static IReadOnlyList<EasingTile> TilesFor(IReadOnlyList<AnimateKey> selected)
    {
        var numbers = selected.Where(k => !k.Drawing).ToList();
        var shared = SharedEasing(numbers);
        return Tiles.Select(t => new EasingTile(t.Easing, t.Name, TileCurve(t.Easing, null), shared?.Easing == t.Easing, numbers.Count > 0)).ToList();
    }

    // The easing all the keys have, with its curve's numbers when it's cubicBezier; null when they
    // differ or there are none.
    public static (Easing Easing, double[]? Bezier)? SharedEasing(IReadOnlyList<AnimateKey> keys)
    {
        if (keys.Count == 0 || keys.Select(k => k.Easing).Distinct().Count() > 1)
        {
            return null;
        }

        var bezier = keys[0].Bezier;
        if (keys[0].Easing == Easing.CubicBezier && keys.Any(k => !(k.Bezier ?? []).SequenceEqual(bezier ?? [])))
        {
            return null;
        }

        return (keys[0].Easing, bezier);
    }

    // An easing's name as set_keyframes takes it: easeInOut, cubicBezier.
    public static string EasingName(Easing easing) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(easing.ToString());

    // An easing's curve in a tile: time from 4 to 40 across, value 0 at 28 and 1 at 4.
    public static Geometry TileCurve(Easing easing, double[]? bezier)
    {
        var points = Curve(easing, bezier, 48);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(4, 28), false, false);
            g.PolyLineTo(points.Skip(1).Select(p => new Point(4 + p.Time * 36, 28 - p.Value * 24)).ToList(), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    // An easing from 0 to 1 at steps + 1 even times, for drawing its curve.
    public static IReadOnlyList<(double Time, double Value)> Curve(Easing easing, double[]? bezier, int steps) =>
        Enumerable.Range(0, steps + 1).Select(i => (i / (double)steps, EasingFunctions.Apply(easing, i / (double)steps, bezier))).ToList();

    // The frames the onion skin draws around 'frame' (of 'frames'), farthest first so the nearer ones
    // are drawn over them: the nearer, the stronger.
    public static IReadOnlyList<OnionGhost> Ghosts(int frame, int frames, int before, int after)
    {
        var ghosts = new List<OnionGhost>();
        for (var k = before; k >= 1; k--)
        {
            if (frame - k >= 0)
            {
                ghosts.Add(new OnionGhost(frame - k, Strength(k, before), false));
            }
        }

        for (var k = after; k >= 1; k--)
        {
            if (frame + k < frames)
            {
                ghosts.Add(new OnionGhost(frame + k, Strength(k, after), true));
            }
        }

        return ghosts;
    }

    // The layer and the layers under it, which the onion skin draws.
    public static IReadOnlySet<string> WithChildren(Project project, string layerId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { layerId };
        for (var added = true; added;)
        {
            added = false;
            foreach (var layer in project.Layers)
            {
                if (layer.ParentId is { } parent && result.Contains(parent) && result.Add(layer.Id))
                {
                    added = true;
                }
            }
        }

        return result;
    }

    // Where the layer's anchor is on the canvas at each frame it's on screen (Path), and at the
    // times of its x and y keyframes or its motion path's points (Keys); empty when it stays put.
    public static (IReadOnlyList<Point> Path, IReadOnlyList<Point> Keys) MotionPath(Project project, string layerId)
    {
        if (project.FindLayer(layerId) is not VisualLayer layer)
        {
            return ([], []);
        }

        Point? At(double time)
        {
            var entry = SceneEvaluator.Evaluate(project, time).FirstOrDefault(e => e.Layer.Id == layerId && e.Cutout is null);
            if (entry is null)
            {
                return null;
            }

            var (x, y) = entry.World.Apply(layer.AnchorX, layer.AnchorY);
            return new Point(x, y);
        }

        var first = (int)Math.Ceiling(layer.Start * project.Fps - 1e-9);
        var last = Math.Min(FrameMath.FrameCount(project), (int)Math.Ceiling(SceneEvaluator.EndOf(layer, project.Duration) * project.Fps - 1e-9)) - 1;
        var step = Math.Max(1, (last - first + 1) / 2000);
        var path = new List<Point>();
        for (var frame = first; frame <= last; frame += step)
        {
            if (At(FrameMath.TimeOf(project, frame)) is { } point)
            {
                path.Add(point);
            }
        }

        if (path.Count < 2 || path.All(p => (p - path[0]).Length < 0.5))
        {
            return ([], []);
        }

        var times = layer.MotionPath is { } motion
            ? motion.Progress.Keyframes.Select(k => k.Time)
            : layer.X.Keyframes.Select(k => k.Time).Concat(layer.Y.Keyframes.Select(k => k.Time));
        var keys = times.Distinct().Order()
            .Where(t => t >= layer.Start && t < SceneEvaluator.EndOf(layer, project.Duration))
            .Select(At).OfType<Point>().ToList();
        return (path, keys);
    }

    private static double Strength(int distance, int count) => 0.5 * (count + 1 - distance) / (count + 1);

    private static List<AnimateRow> Rows(VisualLayer layer)
    {
        var rows = AnimatableProperties.Numeric(layer)
            .Where(p => p.Name != AnimatableProperties.Path && p.Value.Keyframes.Count > 0)
            .Select(p => Row(layer.Id, p.Name, p.Value))
            .ToList();
        if (layer is ImageLayer { Drawing.Keyframes.Count: > 0 } image)
        {
            var (label, unit, factor) = Names[AnimatableProperties.Drawing];
            rows.Add(new AnimateRow(layer.Id, AnimatableProperties.Drawing, label, unit, factor,
                image.Drawing.Keyframes.Select(k => new AnimateKey(layer.Id, AnimatableProperties.Drawing, k.Time, 0, k.Value, Easing.Hold, null)).ToList()));
        }

        return rows;
    }

    private static AnimateRow Row(string layerId, string property, AnimatedValue value)
    {
        var (label, unit, factor) = Names.TryGetValue(property, out var name) ? name : (property, "", 1);
        return new AnimateRow(layerId, property, label, unit, factor,
            value.Keyframes.Select(k => new AnimateKey(layerId, property, k.Time, k.Value, null, k.Easing, k.Easing == Easing.CubicBezier ? k.Bezier : null)).ToList());
    }
}
