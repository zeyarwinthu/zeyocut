// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

// A cutout's entries draw its source (Layer) again with the cutout's matte, where the cutout is in
// the draw order; Id is the cutout's.
public sealed record EvaluatedLayer(VisualLayer Layer, int Index, Affine2D World, double Opacity, bool Shown, CutoutLayer? Cutout = null)
{
    public string Id => Cutout?.Id ?? Layer.Id;
}

public static class SceneEvaluator
{
    public static Affine2D LocalMatrix(VisualLayer layer, double time)
    {
        var x = Interpolator.Evaluate(layer.X, time);
        var y = Interpolator.Evaluate(layer.Y, time);
        var rotation = Interpolator.Evaluate(layer.Rotation, time);
        if (layer.MotionPath is { Points.Count: > 0 } path)
        {
            var place = MotionPaths.At(path, time);
            (x, y) = (place.X, place.Y);
            rotation += path.AutoOrient ? place.Angle : 0;
        }

        var scaleX = Interpolator.Evaluate(layer.ScaleX, time);
        var scaleY = Interpolator.Evaluate(layer.ScaleY, time);
        return Affine2D.Translation(x, y)
            * Affine2D.Rotation(rotation)
            * Affine2D.Scaling(scaleX, scaleY)
            * Affine2D.Translation(-layer.AnchorX, -layer.AnchorY);
    }

    // Scene to screen: the camera's x, y lands at the frame's center, turned and zoomed around it,
    // then the shake offsets in screen pixels.
    public static Affine2D CameraMatrix(Project project, double time)
    {
        if (project.Camera is not { } camera)
        {
            return Affine2D.Identity;
        }

        var zoom = Math.Clamp(Interpolator.Evaluate(camera.Zoom, time), Camera.MinZoom, Camera.MaxZoom);
        var rotation = Interpolator.Evaluate(camera.Rotation, time) + Interpolator.Evaluate(camera.ShakeRotation, time);
        return Affine2D.Translation(project.Width / 2.0 + Interpolator.Evaluate(camera.ShakeX, time), project.Height / 2.0 + Interpolator.Evaluate(camera.ShakeY, time))
            * Affine2D.Rotation(-rotation)
            * Affine2D.Scaling(zoom, zoom)
            * Affine2D.Translation(-Interpolator.Evaluate(camera.X, time), -Interpolator.Evaluate(camera.Y, time));
    }

    public static double EndOf(Layer layer, double projectDuration) =>
        layer is VideoLayer video ? video.TimelineEnd : layer.End ?? projectDuration;

    public static bool IsActive(Layer layer, double time, double projectDuration) =>
        time >= layer.Start && time < EndOf(layer, projectDuration);

    // Returns every visual layer in draw order (zIndex, then position in the layers array). With
    // cutouts, each subject cutout adds an entry per source, in the cutout's place.
    public static IReadOnlyList<EvaluatedLayer> Evaluate(Project project, double time, bool cutouts = false)
    {
        var layers = project.Layers;
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < layers.Count; i++)
        {
            indexById.TryAdd(layers[i].Id, i);
        }

        var camera = CameraMatrix(project, time);
        var resolved = new (Affine2D World, double Opacity, bool Shown)?[layers.Count];
        var visiting = new bool[layers.Count];

        (Affine2D World, double Opacity, bool Shown) Resolve(int i)
        {
            if (resolved[i] is { } done)
            {
                return done;
            }

            if (visiting[i])
            {
                throw new ZeyoCutException($"parent cycle involving layer '{layers[i].Id}'");
            }

            visiting[i] = true;
            var layer = layers[i];
            var world = Affine2D.Identity;
            var opacity = 1.0;
            var shown = layer.Visible && IsActive(layer, time, project.Duration);
            if (layer is VisualLayer visual)
            {
                world = LocalMatrix(visual, time);
                opacity = Math.Clamp(Interpolator.Evaluate(visual.Opacity, time), 0, 1);
            }

            if (layer.ParentId is { } parentId && indexById.TryGetValue(parentId, out var parentIndex))
            {
                var parent = Resolve(parentIndex);
                world = parent.World * world;
                opacity *= parent.Opacity;
                shown &= parent.Shown;
            }
            else if (layer is VisualLayer { IgnoreCamera: false } && project.Camera is not null)
            {
                world = camera * world;
            }

            visiting[i] = false;
            var result = (world, opacity, shown);
            resolved[i] = result;
            return result;
        }

        var entries = new List<(EvaluatedLayer Layer, int Z, int Order)>();
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i] is VisualLayer visual)
            {
                var (world, opacity, shown) = Resolve(i);
                entries.Add((new EvaluatedLayer(visual, i, world, opacity, shown), visual.ZIndex, 0));
            }
            else if (cutouts && layers[i] is CutoutLayer { Matte: not null } cutout)
            {
                var own = Math.Clamp(Interpolator.Evaluate(cutout.Opacity, time), 0, 1);
                foreach (var source in cutout.Sources)
                {
                    if (indexById.TryGetValue(source, out var s) && layers[s] is VisualLayer sourceLayer)
                    {
                        var (world, opacity, shown) = Resolve(s);
                        entries.Add((new EvaluatedLayer(sourceLayer, i, world, opacity * own, shown && cutout.Visible, cutout), cutout.ZIndex, s));
                    }
                }
            }
        }

        entries.Sort((a, b) =>
        {
            var byZ = a.Z.CompareTo(b.Z);
            if (byZ != 0)
            {
                return byZ;
            }

            var byIndex = a.Layer.Index.CompareTo(b.Layer.Index);
            return byIndex != 0 ? byIndex : a.Order.CompareTo(b.Order);
        });
        return entries.Select(e => e.Layer).ToList();
    }
}
