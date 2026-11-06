// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public enum TransitionType
{
    Crossfade,
    FadeToBlack,
    Wipe,
    Slide,
    Zoom,
    WhipPan
}

// Which way a wipe's edge, a slide or a whip pan travels; a zoom goes in or out.
public enum TransitionDirection
{
    Left,
    Right,
    Up,
    Down,
    In,
    Out
}

public sealed record SplitResult(string LeftId, string RightId, IReadOnlyDictionary<string, string> Pieces, IReadOnlyList<string> Reparented);

public sealed record RangeResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Cut,
    IReadOnlyDictionary<string, string> Pieces,
    IReadOnlyList<string> Shifted,
    double RemovedSeconds,
    double OldDuration,
    double NewDuration,
    IReadOnlyList<string> Warnings);

public sealed record TimelineChange(string Summary, double OldDuration, double NewDuration, IReadOnlyList<string> Warnings);

// Timeline edits. Keyframe times are absolute, so every edit that moves a layer in time moves its
// keyframes (and its children) with it. Cuts keep every remaining frame exactly as it was.
public static partial class TimelineEditor
{
    public const double Epsilon = 1e-6;

    // A zoom transition scales the clips by this factor; a whip pan blurs them by this share of
    // the frame at its fastest; a wipe's mask reaches this many pixels past the layer's edges.
    public const double ZoomFactor = 2;
    public const double WhipBlur = 0.02;
    private const double WipeMargin = 2;

    [GeneratedRegex(@"^(.+)_(\d+)$")]
    private static partial Regex NumberedId();

    // Where a layer's content ends on the timeline (image/text without 'end' run to the project end).
    public static double ContentEnd(Project project, Layer layer) => layer switch
    {
        VideoLayer video => video.TimelineEnd,
        AudioLayer audio => AudioEnd(project, audio),
        _ => layer.End ?? project.Duration,
    };

    public static SplitResult SplitLayer(Project project, string layerId, double time)
    {
        var root = SubjectEditor.GetEditableLayer(project, layerId);
        var rootEnd = ContentEnd(project, root);
        if (!double.IsFinite(time) || time <= root.Start + Epsilon || time >= rootEnd - Epsilon)
        {
            throw new ZeyoCutException($"layer '{layerId}' runs {Num(root.Start)}–{Num(rootEnd)}s; the split time must be inside that (got {Num(time)})");
        }

        var targets = ProjectEditor.Subtree(project, layerId);
        var sets = Cut(project, targets, time, time, mergeStatic: false);
        var pieces = AssignIds(project, sets);
        var reparented = FixParents(project, sets, targets);
        Rebuild(project, sets);
        SubjectEditor.Follow(project, PieceIds(sets));
        var rootSet = sets[layerId];
        return new SplitResult(rootSet.Pieces[0].Id, rootSet.Pieces[^1].Id, pieces, reparented);
    }

    // Removes [start, end) from one layer (and its children) or from every layer ("all").
    // With ripple, layers starting at or after 'end' move left by the removed length together with
    // their keyframes and children, layers that start inside the range move to 'start', layers
    // starting before 'start' stay, and the project gets shorter. Without ripple a gap is left.
    public static RangeResult RemoveRange(Project project, string? layerId, double start, double end, bool ripple)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start + Epsilon)
        {
            throw new ZeyoCutException($"the range must have 0 <= start < end (got {Num(start)}–{Num(end)})");
        }

        if (start >= project.Duration - Epsilon)
        {
            throw new ZeyoCutException($"start ({Num(start)}s) is at or after the project end ({Num(project.Duration)}s)");
        }

        end = Math.Min(end, project.Duration);
        var removed = end - start;
        var all = string.IsNullOrWhiteSpace(layerId) || string.Equals(layerId.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var targets = all
            ? project.Layers.Where(l => l is not (CutoutLayer or RedactionLayer)).ToList()
            : ProjectEditor.Subtree(project, SubjectEditor.GetEditableLayer(project, layerId!.Trim()).Id);
        if (!all)
        {
            var root = targets[0];
            var rootEnd = ContentEnd(project, root);
            if (rootEnd <= start + Epsilon || root.Start >= end - Epsilon)
            {
                throw new ZeyoCutException($"layer '{root.Id}' runs {Num(root.Start)}–{Num(rootEnd)}s, so it has nothing between {Num(start)} and {Num(end)}s");
            }
        }

        if (ripple && project.Duration - removed <= Epsilon)
        {
            throw new ZeyoCutException("that would remove the whole project");
        }

        var oldDuration = project.Duration;
        var sets = Cut(project, targets, start, end, mergeStatic: ripple);
        var pieces = AssignIds(project, sets);
        var shifted = new List<string>();
        if (ripple)
        {
            foreach (var set in sets.Values)
            {
                foreach (var piece in set.Pieces.Where(p => p.Start >= end - Epsilon))
                {
                    Shift(piece, -removed);
                    shifted.Add(piece.Id);
                }
            }

            if (!all)
            {
                var targetIds = targets.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var root in Roots(project).Where(r => !targetIds.Contains(r.Id) && r.Start >= start - Epsilon))
                {
                    var amount = -Math.Min(removed, root.Start - start);
                    if (Math.Abs(amount) < Epsilon)
                    {
                        continue;
                    }

                    foreach (var layer in ProjectEditor.Subtree(project, root.Id).Where(l => !targetIds.Contains(l.Id)))
                    {
                        Shift(layer, amount);
                        shifted.Add(layer.Id);
                    }
                }
            }
        }

        FixParents(project, sets, targets);
        Rebuild(project, sets);
        var followed = SubjectEditor.Follow(project, PieceIds(sets));
        if (ripple)
        {
            CameraEditor.RemoveTime(project, start, end);
            MarkerEditor.RemoveTime(project, start, end);
            project.Duration = KeyframeWindow.Round(oldDuration - removed);
        }

        var cut = sets.Values.Where(s => s.Changed && s.Pieces.Count > 0).Select(s => s.Original.Id).ToList();
        var gone = sets.Values.Where(s => s.Pieces.Count == 0).Select(s => s.Original.Id).Concat(followed).ToList();
        return new RangeResult(gone, cut, pieces, shifted.Distinct().ToList(), removed, oldDuration, project.Duration, EndWarnings(project));
    }

    public static TimelineChange MoveLayer(Project project, string layerId, double newStart)
    {
        var layer = SubjectEditor.GetEditableLayer(project, layerId);
        if (!double.IsFinite(newStart) || newStart < 0)
        {
            throw new ZeyoCutException($"start must be >= 0 (got {Num(newStart)})");
        }

        var delta = newStart - layer.Start;
        if (Math.Abs(delta) < Epsilon)
        {
            throw new ZeyoCutException($"layer '{layerId}' already starts at {Num(layer.Start)}s");
        }

        var subtree = ProjectEditor.Subtree(project, layerId);
        if (subtree.FirstOrDefault(l => l.Start + delta < -Epsilon) is { } early)
        {
            throw new ZeyoCutException($"can't move '{layerId}' by {Num(delta)}s: child '{early.Id}' starts at {Num(early.Start)}s and would start before 0");
        }

        var oldDuration = project.Duration;
        foreach (var item in subtree)
        {
            Shift(item, delta);
        }

        ProjectEditor.ExtendToFit(project, subtree.Max(l => ContentEnd(project, l)));
        var moved = subtree.Count > 1 ? $" with {subtree.Count - 1} child layer(s)" : "";
        return new TimelineChange($"moved '{layerId}'{moved} by {Signed(delta)}s: now {Num(layer.Start)}–{Num(ContentEnd(project, layer))}s", oldDuration, project.Duration, EndWarnings(project));
    }

    public static TimelineChange SetSpeed(Project project, string layerId, double speed, bool ripple)
    {
        var layer = SubjectEditor.GetEditableLayer(project, layerId);
        if (layer is not VideoLayer video)
        {
            throw new ZeyoCutException($"set_speed works on video layers; '{layerId}' is a {layer.TypeName} layer");
        }

        if (!double.IsFinite(speed) || speed < VideoLayer.MinSpeed || speed > VideoLayer.MaxSpeed)
        {
            throw new ZeyoCutException($"speed must be between {Num(VideoLayer.MinSpeed)} and {Num(VideoLayer.MaxSpeed)}; got {Num(speed)}");
        }

        if (Math.Abs(speed - video.Speed) < 1e-9)
        {
            throw new ZeyoCutException($"layer '{layerId}' already plays at {Num(speed)}x");
        }

        var oldDuration = project.Duration;
        var oldSpeed = video.Speed;
        var oldEnd = video.TimelineEnd;
        var factor = video.Speed / speed;
        var origin = video.Start;
        var subtree = ProjectEditor.Subtree(project, layerId);
        video.Speed = speed;
        foreach (var item in subtree)
        {
            if (!ReferenceEquals(item, video))
            {
                item.Start = KeyframeWindow.Round(origin + (item.Start - origin) * factor);
                if (item is not VideoLayer && item.End is { } itemEnd)
                {
                    item.End = KeyframeWindow.Round(origin + (itemEnd - origin) * factor);
                }
            }

            ScaleTracks(item, origin, factor);
        }

        var delta = video.TimelineEnd - oldEnd;
        var shifted = new List<string>();
        if (ripple)
        {
            var inSubtree = subtree.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var root in Roots(project).Where(r => !inSubtree.Contains(r.Id) && r.Start >= oldEnd - Epsilon))
            {
                foreach (var item in ProjectEditor.Subtree(project, root.Id).Where(l => !inSubtree.Contains(l.Id)))
                {
                    Shift(item, delta);
                    shifted.Add(item.Id);
                }
            }

            if (delta > 0)
            {
                CameraEditor.InsertTime(project, oldEnd, delta);
                MarkerEditor.InsertTime(project, oldEnd, delta);
            }
            else
            {
                CameraEditor.RemoveTime(project, oldEnd + delta, oldEnd);
                MarkerEditor.RemoveTime(project, oldEnd + delta, oldEnd);
            }

            project.Duration = KeyframeWindow.Round(Math.Max(project.Duration + delta, video.TimelineEnd));
        }
        else
        {
            ProjectEditor.ExtendToFit(project, video.TimelineEnd);
        }

        var rippled = shifted.Count > 0 ? $"; moved {shifted.Count} later layer(s) by {Signed(delta)}s" : "";
        return new TimelineChange(
            $"'{layerId}' now plays at {Num(speed)}x (was {Num(oldSpeed)}x): {Num(video.Start)}–{Num(video.TimelineEnd)}s, {Num(video.Length)}s long{rippled}",
            oldDuration,
            project.Duration,
            EndWarnings(project));
    }

    // Transitions are baked into keyframes and audio fades. A crossfade overlaps the two clips by
    // 'duration': it extends A and starts B earlier using unused source ("handles", up to half the
    // duration each, more from one side if the other has none) and ripples B and everything after it
    // left for whatever is still missing. B fades in on top (or A fades out when A is drawn above B).
    // fadeToBlack needs A and B back to back: A darkens to black over its last half, B comes up from
    // black over its first half. wipe, slide, zoom and whipPan overlap the clips like a crossfade and
    // then animate a mask, position, scale and blur instead of only opacity.
    public static TimelineChange AddTransition(
        Project project,
        string layerAId,
        string layerBId,
        TransitionType type,
        double duration,
        TransitionDirection? direction = null,
        ITextMeasurer? measurer = null)
    {
        var a = SubjectEditor.GetEditableLayer(project, layerAId, "layerA");
        var b = SubjectEditor.GetEditableLayer(project, layerBId, "layerB");
        if (ReferenceEquals(a, b))
        {
            throw new ZeyoCutException("layerA and layerB must be different layers");
        }

        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new ZeyoCutException($"duration must be greater than 0 (got {Num(duration)})");
        }

        if (b.Start < a.Start - Epsilon)
        {
            throw new ZeyoCutException($"layerB ('{b.Id}' at {Num(b.Start)}s) must start after layerA ('{a.Id}' at {Num(a.Start)}s)");
        }

        var oldDuration = project.Duration;
        var frame = 1.0 / project.Fps;
        var aEnd = ContentEnd(project, a);
        var gap = b.Start - aEnd;
        if (gap > frame + Epsilon)
        {
            throw new ZeyoCutException($"'{a.Id}' ends at {Num(aEnd)}s but '{b.Id}' starts at {Num(b.Start)}s; close the gap first (move_layer) so they are back to back");
        }

        var way = Direction(type, direction);
        return type switch
        {
            TransitionType.FadeToBlack => FadeToBlack(project, a, b, duration, oldDuration),
            TransitionType.Crossfade => Crossfade(project, a, b, duration, oldDuration),
            _ => Moving(project, a, b, type, way!.Value, duration, oldDuration, measurer),
        };
    }

    private static TransitionDirection? Direction(TransitionType type, TransitionDirection? direction)
    {
        var name = EnumNames.ToName(type);
        if (type is TransitionType.Crossfade or TransitionType.FadeToBlack)
        {
            return direction is null ? null : throw new ZeyoCutException($"{name} has no direction; direction is for wipe, slide, zoom and whipPan");
        }

        if (type == TransitionType.Zoom)
        {
            var zoom = direction ?? TransitionDirection.In;
            return zoom is TransitionDirection.In or TransitionDirection.Out
                ? zoom
                : throw new ZeyoCutException($"zoom goes in or out (got {EnumNames.ToName(zoom)})");
        }

        var way = direction ?? (type == TransitionType.Wipe ? TransitionDirection.Right : TransitionDirection.Left);
        return way is TransitionDirection.In or TransitionDirection.Out
            ? throw new ZeyoCutException($"{name} goes left, right, up or down (got {EnumNames.ToName(way)}); in and out are for zoom")
            : way;
    }

    private static TimelineChange Moving(Project project, Layer a, Layer b, TransitionType type, TransitionDirection way, double duration, double oldDuration, ITextMeasurer? measurer)
    {
        var name = EnumNames.ToName(type);
        if (a is not VisualLayer visualA || b is not VisualLayer visualB)
        {
            throw new ZeyoCutException($"{name} needs two image, text or video layers; '{(a is VisualLayer ? b.Id : a.Id)}' is an audio layer (use crossfade for sound)");
        }

        var top = DrawnAbove(project, visualB, visualA) ? visualB : visualA;
        if (type == TransitionType.Wipe && top.Mask is not null)
        {
            throw new ZeyoCutException($"'{top.Id}' already has a mask, and a wipe animates one of its own; remove it first (set_mask remove=true) or use another transition");
        }

        if (type is TransitionType.Slide or TransitionType.WhipPan && new[] { visualA, visualB }.FirstOrDefault(l => l.MotionPath is not null) is { } onPath)
        {
            throw new ZeyoCutException($"'{onPath.Id}' moves on a motion path, and a {name} moves x/y; remove the path first (set_motion_path remove=true) or use another transition");
        }

        var notes = new List<string>();
        var (t0, t1) = Overlap(project, a, b, duration, notes);
        switch (type)
        {
            case TransitionType.Wipe:
                Wipe(project, top, top == visualB, way, t0, t1, measurer, notes);
                break;
            case TransitionType.Slide:
                Push(project, visualA, visualB, way, t0, t1, notes);
                break;
            case TransitionType.Zoom:
                var (factorA, factorB) = way == TransitionDirection.In ? (ZoomFactor, 1 / ZoomFactor) : (1 / ZoomFactor, ZoomFactor);
                ScaleAround(project, visualA, t0, t1, 1, factorA, measurer, notes);
                ScaleAround(project, visualB, t0, t1, factorB, 1, measurer, notes);
                FadeTop(project, visualA, visualB, t0, t1, notes);
                break;
            default:
                Push(project, visualA, visualB, way, t0, t1, notes);
                WhipBlurs(project, [visualA, visualB], way, t0, t1, notes);
                break;
        }

        SetFade(a, fadeOut: duration);
        SetFade(b, fadeIn: duration);
        return new TimelineChange(
            $"{name} {EnumNames.ToName(way)} '{a.Id}' → '{b.Id}' over {Num(t0)}–{Num(t1)}s: {string.Join("; ", notes)}",
            oldDuration,
            project.Duration,
            EndWarnings(project));
    }

    // A rectangle mask on the top layer: it grows behind the moving edge to reveal B, or shrinks
    // ahead of it to take A away.
    private static void Wipe(Project project, VisualLayer top, bool revealing, TransitionDirection way, double t0, double t1, ITextMeasurer? measurer, List<string> notes)
    {
        var box = ProjectChecker.ContentBox(project, top, revealing ? t1 : t0, measurer)
            ?? throw new ZeyoCutException($"the size of '{top.Id}' isn't known, so it can't be wiped; use another transition");
        var mask = new Mask
        {
            Type = MaskType.Rectangle,
            X = new(box.Left - WipeMargin),
            Y = new(box.Top - WipeMargin),
            Width = new(box.Width + 2 * WipeMargin),
            Height = new(box.Height + 2 * WipeMargin),
        };
        top.Mask = mask;
        var horizontal = way is TransitionDirection.Left or TransitionDirection.Right;
        var forward = way is TransitionDirection.Right or TransitionDirection.Down;
        var (position, size) = horizontal ? (mask.X, mask.Width) : (mask.Y, mask.Height);
        var start = position.Value;
        var length = size.Value;
        if (revealing)
        {
            Ramp(size, t0, 0, t1, length);
            if (!forward)
            {
                Ramp(position, t0, start + length, t1, start);
            }

            notes.Add($"'{top.Id}' is revealed by a mask wiping {EnumNames.ToName(way)}");
        }
        else
        {
            Ramp(size, t0, length, t1, 0);
            if (forward)
            {
                Ramp(position, t0, start, t1, start + length);
            }

            notes.Add($"'{top.Id}' (drawn on top) is wiped away {EnumNames.ToName(way)} by a mask");
        }
    }

    // B pushes A out of the frame: both move by the canvas width or height, easing in and out.
    private static void Push(Project project, VisualLayer a, VisualLayer b, TransitionDirection way, double t0, double t1, List<string> notes)
    {
        var (dx, dy) = way switch
        {
            TransitionDirection.Left => (-project.Width, 0),
            TransitionDirection.Right => (project.Width, 0),
            TransitionDirection.Up => (0, -project.Height),
            _ => (0, project.Height),
        };
        foreach (var (layer, from, to) in new[] { (a, 0.0, 1.0), (b, -1.0, 0.0) })
        {
            if (dx != 0)
            {
                Ramp(layer.X, t0, Interpolator.Evaluate(layer.X, t0) + from * dx, t1, Interpolator.Evaluate(layer.X, t1) + to * dx, Easing.EaseInOut);
            }

            if (dy != 0)
            {
                Ramp(layer.Y, t0, Interpolator.Evaluate(layer.Y, t0) + from * dy, t1, Interpolator.Evaluate(layer.Y, t1) + to * dy, Easing.EaseInOut);
            }
        }

        notes.Add($"'{b.Id}' pushes '{a.Id}' out {EnumNames.ToName(way)} by {Math.Abs(dx + dy)} px");
        if (a.ParentId is not null || b.ParentId is not null)
        {
            notes.Add("a layer with a parent moves that far in its parent's pixels");
        }
    }

    // Scales the layer from 'from' times its scale at t0 to 'to' times at t1, keeping the center of
    // its content in place.
    private static void ScaleAround(Project project, VisualLayer layer, double t0, double t1, double from, double to, ITextMeasurer? measurer, List<string> notes)
    {
        var box = ProjectChecker.ContentBox(project, layer, t0, measurer);
        var (cx, cy) = box is { } b ? (b.Left + b.Width / 2, b.Top + b.Height / 2) : (layer.AnchorX, layer.AnchorY);
        if (box is null)
        {
            notes.Add($"the size of '{layer.Id}' isn't known, so it zooms around its anchor");
        }

        var moves = Math.Abs(cx - layer.AnchorX) > Epsilon || Math.Abs(cy - layer.AnchorY) > Epsilon;
        if (moves && layer.MotionPath is not null)
        {
            notes.Add($"'{layer.Id}' moves on a motion path, so it zooms around its anchor");
            moves = false;
        }

        var ends = new[] { (Time: t0, Factor: from), (Time: t1, Factor: to) }.Select(end =>
        {
            var scaleX = Interpolator.Evaluate(layer.ScaleX, end.Time);
            var scaleY = Interpolator.Evaluate(layer.ScaleY, end.Time);
            var (dx, dy) = (Affine2D.Rotation(Interpolator.Evaluate(layer.Rotation, end.Time)) * Affine2D.Scaling(scaleX, scaleY))
                .Apply(cx - layer.AnchorX, cy - layer.AnchorY);
            return (
                ScaleX: scaleX * end.Factor,
                ScaleY: scaleY * end.Factor,
                X: Interpolator.Evaluate(layer.X, end.Time) + (1 - end.Factor) * dx,
                Y: Interpolator.Evaluate(layer.Y, end.Time) + (1 - end.Factor) * dy);
        }).ToArray();
        Ramp(layer.ScaleX, t0, ends[0].ScaleX, t1, ends[1].ScaleX, Easing.EaseInOut);
        Ramp(layer.ScaleY, t0, ends[0].ScaleY, t1, ends[1].ScaleY, Easing.EaseInOut);
        if (moves)
        {
            Ramp(layer.X, t0, ends[0].X, t1, ends[1].X, Easing.EaseInOut);
            Ramp(layer.Y, t0, ends[0].Y, t1, ends[1].Y, Easing.EaseInOut);
        }

        notes.Add($"'{layer.Id}' scales {Num(from)}x → {Num(to)}x");
    }

    // The layer drawn on top fades: B in, or A out.
    private static void FadeTop(Project project, VisualLayer a, VisualLayer b, double t0, double t1, List<string> notes)
    {
        if (DrawnAbove(project, b, a))
        {
            Ramp(b.Opacity, t0, 0, t1, Interpolator.Evaluate(b.Opacity, t1));
            notes.Add($"'{b.Id}' fades in");
        }
        else
        {
            Ramp(a.Opacity, t0, Interpolator.Evaluate(a.Opacity, t0), t1, 0);
            notes.Add($"'{a.Id}' (drawn on top) fades out");
        }
    }

    // Motion blur along the push, strongest in the middle where the push is fastest. A layer gets
    // a blur effect if it has none.
    private static void WhipBlurs(Project project, VisualLayer[] layers, TransitionDirection way, double t0, double t1, List<string> notes)
    {
        var middle = KeyframeWindow.Round((t0 + t1) / 2);
        var horizontal = way is TransitionDirection.Left or TransitionDirection.Right;
        var peak = WhipBlur * (horizontal ? project.Width : project.Height);
        var scales = SceneEvaluator.Evaluate(project, middle).ToDictionary(e => e.Layer.Id, e => Math.Sqrt(Math.Abs(e.World.A * e.World.D - e.World.B * e.World.C)), StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            var blur = layer.Blur ??= new BlurEffect { X = new(0), Y = new(0) };
            var value = horizontal ? blur.X : blur.Y;
            var amount = Math.Min(peak / Math.Max(scales[layer.Id], 1e-6), EffectEditor.MaxBlur);
            var (v0, vm, v1) = (Interpolator.Evaluate(value, t0), Interpolator.Evaluate(value, middle), Interpolator.Evaluate(value, t1));
            var top = Math.Min(vm + amount, EffectEditor.MaxBlur);
            Ramp(value, t0, v0, middle, top);
            Ramp(value, middle, top, t1, v1);
        }

        notes.Add($"motion blur up to {Num(peak)} px at {Num(middle)}s");
    }

    private static TimelineChange FadeToBlack(Project project, Layer a, Layer b, double duration, double oldDuration)
    {
        if (a is not IColorAdjustable darkA || b is not IColorAdjustable darkB)
        {
            throw new ZeyoCutException($"fadeToBlack needs image or video layers (it animates brightness); '{(a is IColorAdjustable ? b.Id : a.Id)}' is a {(a is IColorAdjustable ? b : a).TypeName} layer. Use crossfade instead");
        }

        var aEnd = ContentEnd(project, a);
        if (Math.Abs(b.Start - aEnd) > 1.0 / project.Fps + Epsilon)
        {
            throw new ZeyoCutException($"fadeToBlack needs '{a.Id}' (ends {Num(aEnd)}s) and '{b.Id}' (starts {Num(b.Start)}s) back to back");
        }

        var cut = b.Start;
        var half = duration / 2;
        if (half > cut - a.Start + Epsilon || half > ContentEnd(project, b) - cut + Epsilon)
        {
            throw new ZeyoCutException($"a {Num(duration)}s fadeToBlack needs at least {Num(half)}s of each clip");
        }

        Ramp(darkA.Brightness, cut - half, Interpolator.Evaluate(darkA.Brightness, cut - half), cut, -1);
        Ramp(darkB.Brightness, cut, -1, cut + half, Interpolator.Evaluate(darkB.Brightness, cut + half));
        SetFade(a, fadeOut: half);
        SetFade(b, fadeIn: half);
        return new TimelineChange(
            $"fadeToBlack '{a.Id}' → '{b.Id}': '{a.Id}' darkens {Num(cut - half)}–{Num(cut)}s, '{b.Id}' brightens {Num(cut)}–{Num(cut + half)}s, sound fades {Num(half)}s each side",
            oldDuration,
            project.Duration,
            EndWarnings(project));
    }

    private static TimelineChange Crossfade(Project project, Layer a, Layer b, double duration, double oldDuration)
    {
        var aVisual = a is VisualLayer;
        var bVisual = b is VisualLayer;
        if (aVisual != bVisual)
        {
            throw new ZeyoCutException($"crossfade needs two visual layers or two audio layers; '{a.Id}' is {a.TypeName}, '{b.Id}' is {b.TypeName}");
        }

        var notes = new List<string>();
        var (t0, t1) = Overlap(project, a, b, duration, notes);
        if (a is VisualLayer visualA && b is VisualLayer visualB)
        {
            if (DrawnAbove(project, visualB, visualA))
            {
                Ramp(visualB.Opacity, t0, 0, t1, Interpolator.Evaluate(visualB.Opacity, t1));
                notes.Add($"'{b.Id}' fades in {Num(t0)}–{Num(t1)}s");
            }
            else
            {
                Ramp(visualA.Opacity, t0, Interpolator.Evaluate(visualA.Opacity, t0), t1, 0);
                notes.Add($"'{a.Id}' (drawn on top) fades out {Num(t0)}–{Num(t1)}s");
            }
        }

        SetFade(a, fadeOut: duration);
        SetFade(b, fadeIn: duration);
        return new TimelineChange(
            $"crossfade '{a.Id}' → '{b.Id}' over {Num(t0)}–{Num(t1)}s: {string.Join("; ", notes)}",
            oldDuration,
            project.Duration,
            EndWarnings(project));
    }

    // Makes A and B overlap by 'duration' and returns the overlap's start and end.
    private static (double T0, double T1) Overlap(Project project, Layer a, Layer b, double duration, List<string> notes)
    {
        var overlap = Math.Max(0, ContentEnd(project, a) - b.Start);
        var need = duration - overlap;
        if (need > Epsilon)
        {
            var availableA = HandleAfter(project, a);
            var availableB = HandleBefore(b);
            var fromA = Math.Min(need / 2, availableA);
            var fromB = Math.Min(need - fromA, availableB);
            fromA = Math.Min(need - fromB, availableA);
            var rest = need - fromA - fromB;
            if (rest > Epsilon && b.Start - fromB - rest < -Epsilon)
            {
                throw new ZeyoCutException($"a {Num(duration)}s crossfade doesn't fit: '{b.Id}' would have to start before 0");
            }

            if (fromA > Epsilon)
            {
                ExtendEnd(project, a, fromA);
                notes.Add($"{Num(fromA)}s handle from '{a.Id}'");
            }

            if (fromB > Epsilon)
            {
                ExtendStart(b, fromB);
                notes.Add($"{Num(fromB)}s handle from '{b.Id}'");
            }

            if (rest > Epsilon)
            {
                var bStart = b.Start;
                var keep = ProjectEditor.Subtree(project, a.Id).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
                var moved = new HashSet<string>(StringComparer.Ordinal);
                foreach (var root in Roots(project).Where(r => !keep.Contains(r.Id) && (r.Id == RootOf(project, b).Id || r.Start >= bStart - Epsilon)))
                {
                    foreach (var item in ProjectEditor.Subtree(project, root.Id).Where(l => !keep.Contains(l.Id) && moved.Add(l.Id)))
                    {
                        Shift(item, -rest);
                    }
                }

                CameraEditor.RemoveTime(project, bStart - rest, bStart);
                MarkerEditor.RemoveTime(project, bStart - rest, bStart);
                project.Duration = KeyframeWindow.Round(project.Duration - rest);
                notes.Add($"moved '{b.Id}' and {moved.Count - 1} later layer(s) left by {Num(rest)}s");
            }
        }

        return (b.Start, b.Start + duration);
    }

    public static List<string> EndWarnings(Project project)
    {
        var warnings = new List<string>();
        foreach (var layer in project.Layers.Where(l => l is VideoLayer or AudioLayer || l.End is not null))
        {
            var end = ContentEnd(project, layer);
            if (layer.Start < project.Duration && end > project.Duration + 1e-3)
            {
                warnings.Add($"{layer.TypeName} '{layer.Id}' runs until {Num(end)}s and is cut at the project end ({Num(project.Duration)}s)");
            }
        }

        return warnings;
    }

    private sealed class PieceSet
    {
        public required Layer Original { get; init; }

        public List<Layer> Pieces { get; } = [];

        public bool Changed { get; set; }
    }

    private static Dictionary<string, PieceSet> Cut(Project project, IReadOnlyList<Layer> targets, double start, double end, bool mergeStatic)
    {
        var removed = end - start;
        var sets = new Dictionary<string, PieceSet>(StringComparer.Ordinal);
        foreach (var layer in targets)
        {
            var set = new PieceSet { Original = layer };
            var layerEnd = ContentEnd(project, layer);
            if (layerEnd <= start + Epsilon || layer.Start >= end - Epsilon)
            {
                set.Pieces.Add(layer);
            }
            else if (mergeStatic && IsStatic(layer) && layer.Start < start - Epsilon && layerEnd > end + Epsilon)
            {
                if (layer.End is { } layerEndTime)
                {
                    layer.End = KeyframeWindow.Round(layerEndTime - removed);
                }

                set.Pieces.Add(layer);
                set.Changed = true;
            }
            else
            {
                if (Piece(project, layer, double.NegativeInfinity, start) is { } left)
                {
                    set.Pieces.Add(left);
                }

                if (Piece(project, layer, end, double.PositiveInfinity) is { } right)
                {
                    set.Pieces.Add(right);
                }

                set.Changed = true;
            }

            sets[layer.Id] = set;
        }

        return sets;
    }

    // The part of 'layer' between timeline times from and to, or null when nothing is left.
    private static Layer? Piece(Project project, Layer layer, double from, double to)
    {
        var layerEnd = ContentEnd(project, layer);
        var start = Math.Max(layer.Start, from);
        var end = Math.Min(layerEnd, to);
        if (end - start < Epsilon)
        {
            return null;
        }

        var keepsStart = start <= layer.Start + Epsilon;
        var keepsEnd = end >= layerEnd - Epsilon;
        var piece = ProjectJson.LayerFromNode(ProjectJson.LayerToNode(layer));
        switch (piece)
        {
            case VideoLayer video:
                var original = (VideoLayer)layer;
                video.TrimIn = KeyframeWindow.Round(VideoTiming.SourceTime(original, start));
                video.TrimOut = KeyframeWindow.Round(VideoTiming.SourceTime(original, end));
                video.FadeIn = keepsStart ? video.FadeIn : 0;
                video.FadeOut = keepsEnd ? video.FadeOut : 0;
                break;
            case AudioLayer audio:
                audio.TrimIn = KeyframeWindow.Round(audio.TrimIn + (start - layer.Start));
                audio.End = keepsEnd ? audio.End : KeyframeWindow.Round(end);
                audio.FadeIn = keepsStart ? audio.FadeIn : 0;
                audio.FadeOut = keepsEnd ? audio.FadeOut : 0;
                break;
            default:
                piece.End = keepsEnd ? piece.End : KeyframeWindow.Round(end);
                break;
        }

        piece.Start = KeyframeWindow.Round(start);
        if (piece is VisualLayer visual)
        {
            RestrictTracks(visual, keepsStart ? double.NegativeInfinity : start, keepsEnd ? double.PositiveInfinity : end);
        }

        return piece;
    }

    private static Dictionary<string, string> AssignIds(Project project, Dictionary<string, PieceSet> sets)
    {
        var taken = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var pieces = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var set in sets.Values)
        {
            for (var i = 0; i < set.Pieces.Count; i++)
            {
                var piece = set.Pieces[i];
                if (i == 0)
                {
                    piece.Id = set.Original.Id;
                    continue;
                }

                piece.Id = NextId(set.Original.Id, taken);
                if (piece.Name == set.Original.Name && set.Original.Name != set.Original.Id)
                {
                    piece.Name = $"{set.Original.Name} ({i + 1})";
                }
                else if (piece.Name == set.Original.Id)
                {
                    piece.Name = piece.Id;
                }

                taken.Add(piece.Id);
                pieces[piece.Id] = set.Original.Id;
            }
        }

        return pieces;
    }

    // Children follow the piece of their parent that is showing when they start; a child piece that
    // no longer overlaps any parent piece could never be seen, so it is dropped.
    private static List<string> FixParents(Project project, Dictionary<string, PieceSet> sets, IReadOnlyList<Layer> targets)
    {
        var reparented = new List<string>();
        foreach (var layer in targets.OrderBy(l => Depth(project, l)))
        {
            var set = sets[layer.Id];
            if (layer.ParentId is not { } parentId || !sets.TryGetValue(parentId, out var parentSet))
            {
                continue;
            }

            foreach (var piece in set.Pieces.ToList())
            {
                var pieceEnd = ContentEnd(project, piece);
                var parent = parentSet.Pieces.FirstOrDefault(p => piece.Start >= p.Start - Epsilon && piece.Start < OpenEnd(project, p) - Epsilon)
                    ?? parentSet.Pieces.FirstOrDefault(p => piece.Start < OpenEnd(project, p) - Epsilon && pieceEnd > p.Start + Epsilon);
                if (parent is null)
                {
                    set.Pieces.Remove(piece);
                    continue;
                }

                if (piece.ParentId != parent.Id)
                {
                    piece.ParentId = parent.Id;
                    reparented.Add(piece.Id);
                }
            }
        }

        return reparented;
    }

    private static Dictionary<string, List<string>> PieceIds(Dictionary<string, PieceSet> sets) =>
        sets.ToDictionary(e => e.Key, e => e.Value.Pieces.Select(p => p.Id).ToList(), StringComparer.Ordinal);

    private static void Rebuild(Project project, Dictionary<string, PieceSet> sets)
    {
        var rebuilt = new List<Layer>();
        foreach (var layer in project.Layers)
        {
            if (sets.TryGetValue(layer.Id, out var set) && ReferenceEquals(set.Original, layer))
            {
                rebuilt.AddRange(set.Pieces);
            }
            else
            {
                rebuilt.Add(layer);
            }
        }

        project.Layers = rebuilt;
    }

    private static void Shift(Layer layer, double delta)
    {
        if (layer.Start + delta < -Epsilon)
        {
            throw new ZeyoCutException($"layer '{layer.Id}' would start before 0");
        }

        layer.Start = KeyframeWindow.Round(Math.Max(0, layer.Start + delta));
        if (layer is not VideoLayer && layer.End is { } end)
        {
            layer.End = KeyframeWindow.Round(end + delta);
        }

        if (layer is VisualLayer visual)
        {
            foreach (var (_, value) in AnimatableProperties.Numeric(visual))
            {
                KeyframeWindow.Shift(value, delta);
            }

            if (visual is ImageLayer image)
            {
                KeyframeWindow.Shift(image.Drawing, delta);
            }
        }
    }

    private static void ScaleTracks(Layer layer, double origin, double factor)
    {
        if (layer is not VisualLayer visual)
        {
            return;
        }

        foreach (var (_, value) in AnimatableProperties.Numeric(visual))
        {
            KeyframeWindow.Scale(value, origin, factor);
        }

        if (visual is ImageLayer image)
        {
            KeyframeWindow.Scale(image.Drawing, origin, factor);
        }
    }

    private static void RestrictTracks(VisualLayer layer, double from, double to)
    {
        foreach (var (_, value) in AnimatableProperties.Numeric(layer))
        {
            KeyframeWindow.Restrict(value, from, to);
        }

        if (layer is ImageLayer image)
        {
            KeyframeWindow.Restrict(image.Drawing, from, to);
        }
    }

    private static bool IsStatic(Layer layer) =>
        layer is ImageLayer or TextLayer
        && AnimatableProperties.Numeric((VisualLayer)layer).All(p => p.Value.Keyframes.Count == 0)
        && layer is not ImageLayer { Drawing.Keyframes.Count: > 0 };

    private static double OpenEnd(Project project, Layer layer) =>
        layer is ImageLayer or TextLayer && layer.End is null ? double.PositiveInfinity : ContentEnd(project, layer);

    private static double AudioEnd(Project project, AudioLayer audio)
    {
        var end = audio.End ?? double.PositiveInfinity;
        if ((audio.TrimOut ?? project.FindAsset(audio.Asset)?.Duration) is { } sourceEnd)
        {
            end = Math.Min(end, audio.Start + sourceEnd - audio.TrimIn);
        }

        return double.IsPositiveInfinity(end) ? project.Duration : end;
    }

    // Unused source after the layer's end, in timeline seconds.
    private static double HandleAfter(Project project, Layer layer)
    {
        switch (layer)
        {
            case VideoLayer video:
                var duration = project.FindAsset(video.Asset)?.Duration ?? video.TrimOut;
                return Math.Max(0, (duration - video.TrimOut) / video.Speed);
            case AudioLayer audio:
                var sourceLength = project.FindAsset(audio.Asset)?.Duration ?? 0;
                return Math.Max(0, sourceLength - audio.TrimIn - (ContentEnd(project, audio) - audio.Start));
            default:
                return double.PositiveInfinity;
        }
    }

    // Unused source before the layer's start, in timeline seconds (never earlier than time 0).
    private static double HandleBefore(Layer layer) => layer switch
    {
        VideoLayer video => Math.Min(video.TrimIn / video.Speed, video.Start),
        AudioLayer audio => Math.Min(audio.TrimIn, audio.Start),
        _ => layer.Start,
    };

    private static void ExtendEnd(Project project, Layer layer, double seconds)
    {
        var end = ContentEnd(project, layer);
        switch (layer)
        {
            case VideoLayer video:
                video.TrimOut = KeyframeWindow.Round(video.TrimOut + seconds * video.Speed);
                break;
            case AudioLayer audio:
                if (audio.TrimOut is { } trimOut)
                {
                    audio.TrimOut = KeyframeWindow.Round(trimOut + seconds);
                }

                if (audio.End is not null)
                {
                    audio.End = KeyframeWindow.Round(end + seconds);
                }

                break;
            default:
                layer.End = KeyframeWindow.Round(end + seconds);
                break;
        }
    }

    private static void ExtendStart(Layer layer, double seconds)
    {
        layer.Start = KeyframeWindow.Round(layer.Start - seconds);
        switch (layer)
        {
            case VideoLayer video:
                video.TrimIn = KeyframeWindow.Round(Math.Max(0, video.TrimIn - seconds * video.Speed));
                break;
            case AudioLayer audio:
                audio.TrimIn = KeyframeWindow.Round(Math.Max(0, audio.TrimIn - seconds));
                break;
        }
    }

    // A ramp from (t0, v0) to (t1, v1), linear unless eased, replacing the keys in between.
    private static void Ramp(AnimatedValue value, double t0, double v0, double t1, double v1, Easing easing = Easing.Linear)
    {
        t0 = KeyframeWindow.Round(t0);
        t1 = KeyframeWindow.Round(t1);
        value.Keyframes.RemoveAll(k => k.Time >= t0 - Epsilon && k.Time <= t1 + Epsilon);
        value.Keyframes.Add(new Keyframe { Time = t0, Value = v0, Easing = easing });
        value.Keyframes.Add(new Keyframe { Time = t1, Value = v1, Easing = Easing.Linear });
        value.Keyframes = value.Keyframes.OrderBy(k => k.Time).ToList();
    }

    private static void SetFade(Layer layer, double fadeIn = 0, double fadeOut = 0)
    {
        switch (layer)
        {
            case VideoLayer video:
                video.FadeIn = Math.Max(video.FadeIn, fadeIn);
                video.FadeOut = Math.Max(video.FadeOut, fadeOut);
                break;
            case AudioLayer audio:
                audio.FadeIn = Math.Max(audio.FadeIn, fadeIn);
                audio.FadeOut = Math.Max(audio.FadeOut, fadeOut);
                break;
        }
    }

    private static bool DrawnAbove(Project project, VisualLayer top, VisualLayer bottom) =>
        top.ZIndex > bottom.ZIndex || (top.ZIndex == bottom.ZIndex && project.Layers.IndexOf(top) > project.Layers.IndexOf(bottom));

    // Top-level layers; cutouts don't move on their own, they follow their sources, and redactions
    // follow their tracks' footage.
    private static IEnumerable<Layer> Roots(Project project)
    {
        var ids = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        return project.Layers.Where(l => l is not (CutoutLayer or RedactionLayer) && (l.ParentId is null || !ids.Contains(l.ParentId))).ToList();
    }

    private static Layer RootOf(Project project, Layer layer)
    {
        var current = layer;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current.ParentId is { } parentId && project.FindLayer(parentId) is { } parent && seen.Add(current.Id))
        {
            current = parent;
        }

        return current;
    }

    private static int Depth(Project project, Layer layer)
    {
        var depth = 0;
        var current = layer;
        while (current.ParentId is { } parentId && project.FindLayer(parentId) is { } parent && depth < 64)
        {
            current = parent;
            depth++;
        }

        return depth;
    }

    private static string NextId(string id, HashSet<string> taken)
    {
        var match = NumberedId().Match(id);
        var stem = match.Success ? match.Groups[1].Value : id;
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}_{n}";
            if (candidate.Length <= 64 && !taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Signed(double value) => (value >= 0 ? "+" : "") + Num(value);
}
