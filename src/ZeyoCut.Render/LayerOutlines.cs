// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// A layer as a frame draws it, for picking it in a preview and dragging it: the corners of its box on
// the canvas (its own top-left, top-right, bottom-right and bottom-left), where its anchor lands, and
// the matrix from its parent's pixels (where its x and y are) to the canvas. Parent is null when it
// can't be undone, as when the layer is scaled to 0.
public sealed record LayerOutline(string LayerId, IReadOnlyList<(double X, double Y)> Corners, (double X, double Y) Anchor, Affine2D? Parent)
{
    // Whether a canvas point is inside the box: on the same side of each edge.
    public bool Contains(double x, double y)
    {
        var sign = 0;
        for (var i = 0; i < Corners.Count; i++)
        {
            var (ax, ay) = Corners[i];
            var (bx, by) = Corners[(i + 1) % Corners.Count];
            var cross = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
            var side = Math.Sign(cross);
            if (side != 0 && sign != 0 && side != sign)
            {
                return false;
            }

            sign = side != 0 ? side : sign;
        }

        return sign != 0;
    }
}

public static class LayerOutlines
{
    // The box of each layer shown at time, bottom first as they're drawn. Layers with nothing to show
    // (no drawing, fully transparent) have none.
    public static IReadOnlyList<LayerOutline> At(FrameRenderer renderer, Project project, double time)
    {
        var outlines = new List<LayerOutline>();
        foreach (var layer in SceneEvaluator.Evaluate(project, time).Where(l => l.Shown && l.Opacity > 0))
        {
            if (LocalBounds(renderer, project, layer.Layer, time) is not { } bounds || bounds.IsEmpty)
            {
                continue;
            }

            var world = layer.World;
            var corners = new[] { world.Apply(bounds.Left, bounds.Top), world.Apply(bounds.Right, bounds.Top), world.Apply(bounds.Right, bounds.Bottom), world.Apply(bounds.Left, bounds.Bottom) };
            var parent = SceneEvaluator.LocalMatrix(layer.Layer, time).Inverse() is { } toLocal ? world * toLocal : (Affine2D?)null;
            outlines.Add(new LayerOutline(layer.Layer.Id, corners, world.Apply(layer.Layer.AnchorX, layer.Layer.AnchorY), parent));
        }

        return outlines;
    }

    // The topmost layer at a canvas point.
    public static LayerOutline? Hit(IReadOnlyList<LayerOutline> outlines, double x, double y) => outlines.LastOrDefault(o => o.Contains(x, y));

    // A layer's box in its own pixels: an image's drawing, a text's lines, a video's crop.
    public static SKRect? LocalBounds(FrameRenderer renderer, Project project, VisualLayer layer, double time)
    {
        switch (layer)
        {
            case ImageLayer image:
                var drawing = renderer.CurrentDrawing(project, image, time);
                if (drawing is null)
                {
                    return null;
                }

                if (image.FlipX)
                {
                    var flippedLeft = (float)(2 * image.AnchorX - drawing.Width);
                    return new SKRect(flippedLeft, 0, flippedLeft + drawing.Width, drawing.Height);
                }

                return new SKRect(0, 0, drawing.Width, drawing.Height);
            case TextLayer text:
                var layout = renderer.Text.Layout(project, renderer.ProjectDirectory, text);
                return new SKRect(0, 0, layout.BoxWidth, layout.BoxHeight);
            case VideoLayer video when project.FindAsset(video.Asset) is { } asset:
                var (width, height) = VideoTiming.CropSize(video, asset);
                return new SKRect(0, 0, (float)width, (float)height);
            default:
                return null;
        }
    }
}
