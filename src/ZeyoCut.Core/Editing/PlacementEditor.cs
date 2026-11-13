// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// The sides of a video cut off by its crop, in source pixels.
public readonly record struct CropSides(double Left, double Top, double Right, double Bottom);

// A layer's place on the canvas, as the editor's Transform and Crop fields and a drag in its preview
// change it: x, y, rotation, scaleX, scaleY and opacity, and a video's crop.
public static class PlacementEditor
{
    public static readonly IReadOnlyList<string> Properties = ["x", "y", "rotation", "scaleX", "scaleY", "opacity"];

    // The edit that gives the layer these values at time. A value with keyframes gets a keyframe at
    // time, as set_keyframes adds one, with the easing of the keyframe it replaces or the one before
    // it, so the motion keeps its feel; the layer must be on the timeline then. A value without
    // keyframes changes its static value, as update_layer does. x and y of a layer on a motion path
    // aren't set: the path places it, and changing them would take the path off.
    public static EditOperation Set(Project project, string layerId, IReadOnlyDictionary<string, double> values, double time)
    {
        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        if (values.Count == 0)
        {
            throw new ZeyoCutException("there are no values to set");
        }

        var statics = new JsonObject();
        var keys = new List<KeyframeEntry>();
        foreach (var (name, value) in values)
        {
            var property = Properties.FirstOrDefault(p => p == name) ?? throw new ZeyoCutException($"'{name}' isn't one of {string.Join(", ", Properties)}");
            if (!double.IsFinite(value))
            {
                throw new ZeyoCutException($"{property} must be a finite number (got {value})");
            }

            if (property is "x" or "y" && layer.MotionPath is not null)
            {
                throw OnPath(layer);
            }

            var track = AnimatableProperties.GetNumeric(layer, property);
            if (track.Keyframes.Count == 0)
            {
                statics[property] = value;
                continue;
            }

            if (!SceneEvaluator.IsActive(layer, time, project.Duration))
            {
                throw new ZeyoCutException(
                    $"'{layer.Id}' animates {property} with keyframes, and a change adds one at the playhead; move the playhead into {Num(layer.Start)}–{Num(SceneEvaluator.EndOf(layer, project.Duration))}s, where the layer is");
            }

            keys.Add(new KeyframeEntry(layer.Id, property, time, JsonSerializer.SerializeToElement(value), EnumNames.ToName(EasingAt(track, time))));
        }

        EditOperation? update = statics.Count > 0 ? new UpdateLayerOperation(layer.Id, statics) : null;
        EditOperation? keyframes = keys.Count > 0 ? new SetKeyframesOperation(keys) : null;
        return (update, keyframes) switch
        {
            ({ } only, null) => only,
            (null, { } only) => only,
            _ => new BatchOperation($"set_keyframes {layer.Id}", [update!, keyframes!]),
        };
    }

    // Why x and y can't be set on a layer: it follows a motion path. Null when they can.
    public static string? PositionLocked(VisualLayer layer) =>
        layer.MotionPath is null ? null : OnPath(layer).Message;

    // Moves layers by dx, dy at every moment: their x and y, each keyframe of them and the points of
    // their motion path. Returns how many moved.
    public static int Shift(Project project, IReadOnlyList<string> layerIds, double dx, double dy)
    {
        if (layerIds.Count == 0)
        {
            throw new ZeyoCutException("there are no layers to move");
        }

        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            throw new ZeyoCutException($"the move must be finite numbers (got {dx}, {dy})");
        }

        foreach (var layer in layerIds.Select(id => ProjectEditor.GetVisualLayer(project, id)).ToList())
        {
            Shift(layer, dx, dy);
        }

        return layerIds.Count;
    }

    // The crop's sides. Without a crop they're all 0.
    public static CropSides Sides(VideoLayer layer, Asset asset)
    {
        var crop = VideoTiming.Crop(layer, asset, layer.Start);
        return new CropSides(crop.X, crop.Y, (asset.Width ?? 0) - crop.X - crop.Width, (asset.Height ?? 0) - crop.Y - crop.Height);
    }

    // Why the crop can't be changed by its sides: it moves (cropX or cropY has keyframes), or the
    // source's size isn't known. Null when it can.
    public static string? CropLocked(Project project, VideoLayer layer)
    {
        if (project.FindAsset(layer.Asset) is not { Width: > 0, Height: > 0 })
        {
            return $"the size of '{layer.Asset}' isn't known, so its crop can't be set by its sides";
        }

        return layer.CropX.Keyframes.Count > 0 || layer.CropY.Keyframes.Count > 0
            ? $"'{layer.Id}' has a moving crop (cropX or cropY keyframes); change it with set_keyframes, or clear them first"
            : null;
    }

    // Cuts the sides off a video, leaving what remains of the picture where it was: the anchor, the
    // mask and the direct child layers move with the cut, so the layer still turns and scales around
    // the same point of the picture and its children stay on it.
    public static string Crop(Project project, string layerId, CropSides sides)
    {
        if (ProjectEditor.GetVisualLayer(project, layerId) is not VideoLayer video)
        {
            throw new ZeyoCutException($"'{layerId}' isn't a video layer; only video is cropped");
        }

        if (CropLocked(project, video) is { } locked)
        {
            throw new ZeyoCutException(locked);
        }

        var asset = project.FindAsset(video.Asset)!;
        var (width, height) = ((double)asset.Width!.Value, (double)asset.Height!.Value);
        foreach (var (side, value) in new[] { ("left", sides.Left), ("top", sides.Top), ("right", sides.Right), ("bottom", sides.Bottom) })
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ZeyoCutException($"the {side} crop can't be less than 0 (got {Num(value)})");
            }
        }

        var keptWidth = width - sides.Left - sides.Right;
        var keptHeight = height - sides.Top - sides.Bottom;
        if (keptWidth < 1 || keptHeight < 1)
        {
            throw new ZeyoCutException($"that leaves {Num(Math.Max(0, keptWidth))}×{Num(Math.Max(0, keptHeight))} px of the {Num(width)}×{Num(height)} source; at least 1 px each way must stay");
        }

        var before = Sides(video, asset);
        var (dx, dy) = (sides.Left - before.Left, sides.Top - before.Top);
        var whole = sides is { Left: 0, Top: 0, Right: 0, Bottom: 0 };
        video.CropX.Value = sides.Left;
        video.CropY.Value = sides.Top;
        video.CropWidth = whole ? null : keptWidth;
        video.CropHeight = whole ? null : keptHeight;
        video.AnchorX -= dx;
        video.AnchorY -= dy;
        if (video.Mask is { } mask)
        {
            Offset(mask.X, -dx);
            Offset(mask.Y, -dy);
        }

        var children = project.Layers.OfType<VisualLayer>().Where(l => l.ParentId == video.Id).ToList();
        foreach (var child in children)
        {
            Shift(child, -dx, -dy);
        }

        var kept = whole ? "shows the whole frame again" : $"shows {Num(keptWidth)}×{Num(keptHeight)} px of its {Num(width)}×{Num(height)} source";
        return $"cropped '{video.Id}': it {kept} (left {Num(sides.Left)}, top {Num(sides.Top)}, right {Num(sides.Right)}, bottom {Num(sides.Bottom)})"
            + (children.Count > 0 && (dx != 0 || dy != 0) ? $"; {children.Count} child layer(s) moved with the picture" : "");
    }

    private static void Shift(VisualLayer layer, double dx, double dy)
    {
        Offset(layer.X, dx);
        Offset(layer.Y, dy);
        foreach (var point in layer.MotionPath?.Points ?? [])
        {
            point.X += dx;
            point.Y += dy;
        }
    }

    private static void Offset(AnimatedValue value, double by)
    {
        value.Value += by;
        foreach (var key in value.Keyframes)
        {
            key.Value += by;
        }
    }

    // The easing of the keyframe at time, or else of the one before it: it shapes the segment the new
    // keyframe starts.
    private static Easing EasingAt(AnimatedValue track, double time) =>
        track.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < KeyframeEditor.TimeTolerance)?.Easing
        ?? track.Keyframes.LastOrDefault(k => k.Time < time)?.Easing
        ?? Easing.Linear;

    private static ZeyoCutException OnPath(VisualLayer layer) =>
        new($"'{layer.Id}' follows a motion path, which places it; ask Claude to change the path (set_motion_path), or to take it off first");

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
