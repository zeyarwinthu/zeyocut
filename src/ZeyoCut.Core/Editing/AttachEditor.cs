// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// A stretch of the timeline where a layer follows a track through one clip: its first and last frame
// times.
public sealed record AttachRun(string Clip, double From, double To);

// attach_to_track's result: where the layer follows the track; its anchor's offset from the box's
// center in canvas pixels at the first frame (given, or where the layer was); the keyframes made per
// property and how many of the layer's own they replaced; and with followScale, the smallest and
// largest size against the one at the first frame.
public sealed record AttachResult(
    VisualLayer Layer,
    List<AttachRun> Runs,
    double OffsetX,
    double OffsetY,
    bool OffsetGiven,
    IReadOnlyList<(string Property, int Keys)> Keys,
    int Replaced,
    double Smallest,
    double Largest,
    List<string> Warnings);

// attach_to_track: x and y keyframes (and scaleX and scaleY with followScale) that keep a text or
// image layer on a track's box on every frame where a clip shows the footage the track found the
// object in. The keyframes are on the timeline, so a later trim or speed change doesn't move them.
public static class AttachEditor
{
    // Keyframes are kept where needed to stay within these of every frame: pixels for x and y, and a
    // share of the size for the scale (0.5 px on a 250 px layer).
    public const double PlaceTolerance = 0.5;
    public const double ScaleTolerance = 0.002;

    // One frame where the layer follows the box: the clip showing it, the box's center and size on the
    // canvas, and the matrix the layer's x and y are in (its parent's, the camera's, or none).
    private sealed record Sample(int Frame, double Time, string Clip, double X, double Y, double Size, Affine2D Parent, Affine2D Inverse);

    public static AttachResult Attach(Project project, string? layerId, Track track, TrackData data, (double X, double Y)? offset, bool followScale)
    {
        var target = ProjectEditor.GetLayer(project, layerId);
        if (target is not (TextLayer or ImageLayer))
        {
            throw new ZeyoCutException($"attach_to_track moves text and image layers; '{target.Id}' is a {target.TypeName} layer");
        }

        var layer = (VisualLayer)target;
        if (offset is { } given && (!double.IsFinite(given.X) || !double.IsFinite(given.Y)))
        {
            throw new ZeyoCutException($"offset needs finite x and y (got {Num(given.X)},{Num(given.Y)})");
        }

        var asset = project.FindAsset(track.Asset)
            ?? throw new ZeyoCutException($"track '{track.Id}': asset '{track.Asset}' not found");
        var clips = RedactionEditor.Clips(project, asset.Id);
        foreach (var clip in clips)
        {
            for (var parent = clip.ParentId; parent is not null; parent = project.FindLayer(parent)?.ParentId)
            {
                if (parent == layer.Id)
                {
                    throw new ZeyoCutException($"clip '{clip.Id}' plays the track's footage and is a child of '{layer.Id}', so moving '{layer.Id}' would move the footage too; attach a layer the clip doesn't hang from");
                }
            }
        }

        var found = data.Frames
            .Where(f => !f.Lost && f.Width > 0 && f.Height > 0 && double.IsFinite(f.X + f.Y + f.Width + f.Height))
            .OrderBy(f => f.Frame)
            .ToList();
        if (found.Count == 0)
        {
            throw new ZeyoCutException($"track '{track.Id}' has no frame where the object was found, so there is nothing to follow");
        }

        var frames = found.Select(f => f.Frame).ToArray();
        var samples = new List<Sample>();
        for (var n = 0; n < FrameMath.FrameCount(project); n++)
        {
            var time = FrameMath.TimeOf(project, n);
            if (!SceneEvaluator.IsActive(layer, time, project.Duration))
            {
                continue;
            }

            var scene = SceneEvaluator.Evaluate(project, time);
            var showing = scene.Where(e => e.Cutout is null && e.Shown && e.Layer is VideoLayer v && v.Asset == asset.Id).ToList();
            if ((showing.FirstOrDefault(e => e.Layer.Id == track.Layer) ?? showing.LastOrDefault()) is not { } placed)
            {
                continue;
            }

            var video = (VideoLayer)placed.Layer;
            var (first, last) = TrackEditor.Frames(video, asset);
            var frame = Math.Clamp(VideoTiming.FrameAt(asset, VideoTiming.SourceTime(video, time)), first, last);
            if (BoxAt(found, frames, frame) is not { } box)
            {
                continue;
            }

            var crop = VideoTiming.Crop(video, asset, time);
            var (x, y) = placed.World.Apply(box.X + box.Width / 2 - crop.X, box.Y + box.Height / 2 - crop.Y);
            var size = Scale(placed.World) * Math.Sqrt(box.Width * box.Height);
            var frameOf = layer.ParentId is { } parentId
                ? scene.First(e => e.Cutout is null && e.Layer.Id == parentId).World
                : !layer.IgnoreCamera && project.Camera is not null ? SceneEvaluator.CameraMatrix(project, time) : Affine2D.Identity;
            if (size > 0 && frameOf.Inverse() is { } inverse)
            {
                samples.Add(new Sample(n, time, video.Id, x, y, size, frameOf, inverse));
            }
        }

        if (samples.Count == 0)
        {
            throw Never(project, layer, track, asset, found, clips);
        }

        var start = samples[0];
        var anchor = SceneEvaluator.Evaluate(project, start.Time).First(e => e.Cutout is null && e.Layer.Id == layer.Id).World.Apply(layer.AnchorX, layer.AnchorY);
        var (offsetX, offsetY) = offset ?? (anchor.X - start.X, anchor.Y - start.Y);
        var baseX = Interpolator.Evaluate(layer.ScaleX, start.Time);
        var baseY = Interpolator.Evaluate(layer.ScaleY, start.Time);

        var warnings = new List<string>();
        if (layer.MotionPath is not null)
        {
            layer.MotionPath = null;
            warnings.Add(EffectEditor.PathRemovedWarning(layer));
        }

        var values = new List<(string Name, AnimatedValue Value, Func<Sample, double> Of, int Digits, double Tolerance)>
        {
            ("x", layer.X, s => Place(s).X, 2, PlaceTolerance),
            ("y", layer.Y, s => Place(s).Y, 2, PlaceTolerance),
        };
        if (followScale)
        {
            values.Add(("scaleX", layer.ScaleX, s => baseX * Grows(s), 4, ScaleTolerance * Math.Abs(baseX)));
            values.Add(("scaleY", layer.ScaleY, s => baseY * Grows(s), 4, ScaleTolerance * Math.Abs(baseY)));
        }

        var runs = Runs(samples);
        var keys = values.Select(v => (v.Name, 0)).ToList();
        var replaced = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var (_, value, of, digits, tolerance) = values[i];
            foreach (var run in runs)
            {
                replaced += value.Keyframes.RemoveAll(k => k.Time > run[0].Time - KeyframeEditor.TimeTolerance && k.Time < run[^1].Time + KeyframeEditor.TimeTolerance);
                var made = RedactionEditor.Thin(
                    run.Select(s => Math.Round(s.Time, 6)).ToArray(),
                    run.Select(s => Math.Round(of(s), digits)).ToArray(),
                    Math.Max(tolerance, Math.Pow(10, -digits))).Keyframes;
                value.Keyframes.AddRange(made);
                keys[i] = (keys[i].Item1, keys[i].Item2 + made.Count);
            }

            value.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        var grown = samples.Select(Grows).ToList();
        var shown = Runs(samples, byClip: true).Select(r => new AttachRun(r[0].Clip, r[0].Time, r[^1].Time)).ToList();
        return new AttachResult(layer, shown, offsetX, offsetY, offset is not null, keys, replaced,
            followScale ? grown.Min() : 1, followScale ? grown.Max() : 1, warnings);

        // How much bigger the box is on the canvas than at the first frame.
        double Ratio(Sample s) => s.Size / start.Size;

        // The layer's own scale against the first frame: the box's growth, less what its parent or
        // the camera already scales it by.
        double Grows(Sample s) => Ratio(s) * Scale(start.Parent) / Scale(s.Parent);

        // The layer's x and y: the box's center plus the offset (growing with the box), in the
        // layer's parent's space.
        (double X, double Y) Place(Sample s)
        {
            var by = followScale ? Ratio(s) : 1;
            return s.Inverse.Apply(s.X + offsetX * by, s.Y + offsetY * by);
        }
    }

    // The box on a source frame: where the track found the object, or moved straight between the
    // frames found around it; null before the first or after the last.
    private static (double X, double Y, double Width, double Height)? BoxAt(List<TrackedFrame> found, int[] frames, int frame)
    {
        var index = Array.BinarySearch(frames, frame);
        if (index >= 0)
        {
            var f = found[index];
            return (f.X, f.Y, f.Width, f.Height);
        }

        index = ~index;
        if (index == 0 || index == frames.Length)
        {
            return null;
        }

        var (a, b) = (found[index - 1], found[index]);
        var u = (frame - a.Frame) / (double)(b.Frame - a.Frame);
        return (a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Width + (b.Width - a.Width) * u, a.Height + (b.Height - a.Height) * u);
    }

    // Runs of consecutive frames (and, byClip, of one clip).
    private static List<List<Sample>> Runs(List<Sample> samples, bool byClip = false)
    {
        var runs = new List<List<Sample>>();
        foreach (var sample in samples)
        {
            if (runs.Count > 0 && runs[^1][^1].Frame == sample.Frame - 1 && (!byClip || runs[^1][^1].Clip == sample.Clip))
            {
                runs[^1].Add(sample);
            }
            else
            {
                runs.Add([sample]);
            }
        }

        return runs;
    }

    // How much a matrix scales areas, as a length.
    private static double Scale(Affine2D m) => Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));

    private static ZeyoCutException Never(Project project, VisualLayer layer, Track track, Asset asset, List<TrackedFrame> found, List<VideoLayer> clips)
    {
        var plays = new List<string>();
        foreach (var clip in clips)
        {
            var (first, last) = TrackEditor.Frames(clip, asset);
            var from = Math.Max(first, found[0].Frame);
            var to = Math.Min(last, found[^1].Frame);
            if (from <= to)
            {
                plays.Add($"{Num(Timeline(clip, asset, from))}–{Num(Timeline(clip, asset, to))}s on '{clip.Id}'");
            }
        }

        if (plays.Count == 0)
        {
            return new ZeyoCutException($"no clip plays the frames where track '{track.Id}' found the object (frames {found[0].Frame}–{found[^1].Frame} of '{asset.Id}'), so there is nothing for '{layer.Id}' to follow");
        }

        return new ZeyoCutException(
            $"'{layer.Id}' is on the timeline {Num(layer.Start)}–{Num(SceneEvaluator.EndOf(layer, project.Duration))}s, and no visible clip shows the frames where track '{track.Id}' found the object then; they play {string.Join(", ", plays)}; change '{layer.Id}''s start and end with update_layer so they meet");
    }

    private static double Timeline(VideoLayer clip, Asset asset, int frame) =>
        VideoTiming.TimelineTime(clip, Math.Max(clip.TrimIn, VideoTiming.TimeOfFrame(asset, frame)));

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
