// SPDX-License-Identifier: MIT

using System.Globalization;
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

    // The edit that gives the layer these placement values at time (ValueEditor.Set): a keyframe at
    // time for a value with keyframes, its static value for one without. x and y of a layer on a
    // motion path aren't set: the path places it.
    public static EditOperation Set(Project project, string layerId, IReadOnlyDictionary<string, double> values, double time)
    {
        if (values.Keys.FirstOrDefault(name => !Properties.Contains(name)) is { } other)
        {
            throw new ZeyoCutException($"'{other}' isn't one of {string.Join(", ", Properties)}");
        }

        return ValueEditor.Set(project, layerId, values, time);
    }

    // Why x and y can't be set on a layer: it follows a motion path. Null when they can.
    public static string? PositionLocked(VisualLayer layer) =>
        layer.MotionPath is null ? null : ValueEditor.OnPath(layer).Message;

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

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
