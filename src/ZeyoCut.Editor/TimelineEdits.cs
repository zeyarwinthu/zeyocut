// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// Where times are across the timeline: PixelsPerSecond, and Offset, the pixels scrolled past 0 s.
public readonly record struct TimelineScale(double PixelsPerSecond, double Offset)
{
    private static readonly double[] Steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    public double XOf(double time) => time * PixelsPerSecond - Offset;

    public double TimeAt(double x) => (x + Offset) / PixelsPerSecond;

    // Zoomed to pixelsPerSecond with the time at x staying under x.
    public TimelineScale ZoomedAt(double pixelsPerSecond, double x) => new(pixelsPerSecond, TimeAt(x) * pixelsPerSecond - x);

    // Seconds between labelled ticks, the shortest that leaves room for a label.
    public static double LabelStep(double pixelsPerSecond, double minimumPixels = 64)
    {
        foreach (var step in Steps)
        {
            if (step * pixelsPerSecond >= minimumPixels)
            {
                return step;
            }
        }

        return Steps[^1];
    }

    // How far to scroll on each step while a drag is held at x, near or past an edge of the lanes
    // (left to right): left of 0, right of 0, or 0 away from the edges. The deeper into the edge, the
    // faster, up to max pixels a step.
    public static double EdgeScroll(double x, double left, double right, double band = 24, double max = 24)
    {
        var depth = x < left + band ? x - (left + band) : x > right - band ? x - (right - band) : 0;
        return Math.Clamp(depth / 2, -max, max);
    }

    // The frame shown at a time: frame f covers f/fps to (f+1)/fps.
    public static int FrameAt(double time, int fps, int frames) =>
        frames <= 0 ? 0 : Math.Clamp((int)Math.Floor(time * fps + 1e-6), 0, frames - 1);
}

public enum DragPart
{
    Body,
    Start,
    End,
}

// Where a dragged clip is: Start to End, and the time it snapped to, for the guide line.
public sealed record DragPlace(double Start, double End, double? Snapped);

// A draw level a dragged clip goes to: another lane's, or (Insert) a level of its own there.
public sealed record LaneDrop(int ZIndex, bool Insert);

// What a drag on the timeline does, worked out apart from the control that draws it. Times land on
// whole frames, or on a snap point within reach; the result goes to Core as an EditOperation.
public static class TimelineEdits
{
    // The times a clip snaps to: the other clips' edges, 0, the playhead and the markers.
    public static IReadOnlyList<double> SnapPoints(IEnumerable<TimelineLane> lanes, string except, double playhead, IEnumerable<double> markers) =>
        lanes.SelectMany(l => l.Clips)
            .Where(c => c.LayerId != except)
            .SelectMany(c => new[] { c.Start, c.End })
            .Append(0)
            .Append(playhead)
            .Concat(markers)
            .Distinct()
            .Order()
            .ToList();

    // Where a clip is after moving the part dragged by delta seconds: a move keeps the length and snaps
    // either edge; a trim keeps the other edge and at least a frame. Never past the limits.
    public static DragPlace Place(TimelineClip clip, DragPart part, double delta, TimelineLimits limits, int fps, IReadOnlyList<double> snaps, double reach)
    {
        var frame = 1.0 / fps;
        switch (part)
        {
            case DragPart.Body:
                {
                    var length = clip.End - clip.Start;
                    var start = OnFrame(clip.Start + delta, fps);
                    double? snapped = null;
                    var byStart = Nearest(snaps, clip.Start + delta, reach);
                    var byEnd = Nearest(snaps, clip.End + delta, reach);
                    if (byStart is { } s && (byEnd is not { } e0 || Math.Abs(s - clip.Start - delta) <= Math.Abs(e0 - clip.End - delta)))
                    {
                        (start, snapped) = (s, s);
                    }
                    else if (byEnd is { } e)
                    {
                        (start, snapped) = (e - length, e);
                    }

                    if (start < limits.EarliestMove)
                    {
                        (start, snapped) = (limits.EarliestMove, null);
                    }

                    return new DragPlace(start, start + length, snapped);
                }

            case DragPart.Start:
                {
                    var (time, snapped) = Snapped(clip.Start + delta, fps, snaps, reach);
                    var latest = Math.Max(limits.EarliestStart, clip.End - frame);
                    if (time < limits.EarliestStart || time > latest)
                    {
                        (time, snapped) = (Math.Clamp(time, limits.EarliestStart, latest), null);
                    }

                    return new DragPlace(time, clip.End, snapped);
                }

            default:
                {
                    var (time, snapped) = Snapped(clip.End + delta, fps, snaps, reach);
                    var earliest = Math.Min(limits.LatestEnd, clip.Start + frame);
                    if (time < earliest || time > limits.LatestEnd)
                    {
                        (time, snapped) = (Math.Clamp(time, earliest, limits.LatestEnd), null);
                    }

                    return new DragPlace(clip.Start, time, snapped);
                }
        }
    }

    // Where a visual clip dragged up or down goes: lane (an index into lanes, the visual and caption
    // lanes top first) or gap (0 above the first lane, i between lane i-1 and lane i, Count below
    // the last). Null when it stays: its own lane, a caption lane, or a gap next to a lane it has to
    // itself.
    public static LaneDrop? Drop(IReadOnlyList<TimelineLane> lanes, TimelineLane from, int? lane, int? gap)
    {
        if (lane is { } i && i >= 0 && i < lanes.Count)
        {
            var target = lanes[i];
            return target.Kind == LaneKind.Visual && target.ZIndex != from.ZIndex ? new LaneDrop(target.ZIndex, false) : null;
        }

        if (gap is { } g && g >= 0 && g <= lanes.Count && lanes.Count > 0)
        {
            var own = IndexOf(lanes, from);
            if (from.Clips.Count == 1 && (g == own || g == own + 1))
            {
                return null;
            }

            // The level just above the lane below the gap; below the last lane, that lane's own level,
            // which moves up.
            return new LaneDrop(g < lanes.Count ? lanes[g].ZIndex + 1 : lanes[^1].ZIndex, true);
        }

        return null;
    }

    // The edit a finished drag makes; null when it changes nothing.
    public static EditOperation? Operation(TimelineClip clip, DragPart part, DragPlace place, LaneDrop? drop)
    {
        var moved = Math.Abs(place.Start - clip.Start) > TimelineEditor.Epsilon;
        switch (part)
        {
            case DragPart.Body:
                var operations = new List<EditOperation>();
                if (moved)
                {
                    operations.Add(new MoveLayerOperation(clip.LayerId, place.Start));
                }

                if (drop is not null)
                {
                    operations.Add(new ReorderLayerOperation(clip.LayerId, drop.ZIndex, drop.Insert));
                }

                return operations.Count switch
                {
                    0 => null,
                    1 => operations[0],
                    _ => new BatchOperation($"drag {clip.LayerId}", operations),
                };
            case DragPart.Start:
                return moved ? new TrimLayerOperation(clip.LayerId, place.Start, null) : null;
            default:
                return Math.Abs(place.End - clip.End) > TimelineEditor.Epsilon ? new TrimLayerOperation(clip.LayerId, null, place.End) : null;
        }
    }

    private static int IndexOf(IReadOnlyList<TimelineLane> lanes, TimelineLane lane)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            if (lanes[i].Key == lane.Key)
            {
                return i;
            }
        }

        return -1;
    }

    private static double OnFrame(double time, int fps) => Math.Round(time * fps) / fps;

    private static (double Time, double? Snapped) Snapped(double time, int fps, IReadOnlyList<double> snaps, double reach) =>
        Nearest(snaps, time, reach) is { } snap ? (snap, snap) : (OnFrame(time, fps), null);

    private static double? Nearest(IReadOnlyList<double> points, double time, double reach)
    {
        double? best = null;
        foreach (var point in points)
        {
            if (Math.Abs(point - time) <= reach && (best is not { } b || Math.Abs(point - time) < Math.Abs(b - time)))
            {
                best = point;
            }
        }

        return best;
    }
}
