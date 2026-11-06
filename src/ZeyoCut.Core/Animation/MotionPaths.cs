// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

// Where a motion path puts the layer, and which way the path points there (degrees, clockwise
// from +x, which is what autoOrient adds to the rotation).
public readonly record struct PathPlace(double X, double Y, double Angle);

public readonly record struct Bezier(double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3)
{
    public (double X, double Y) Point(double t)
    {
        var u = 1 - t;
        var b0 = u * u * u;
        var b1 = 3 * u * u * t;
        var b2 = 3 * u * t * t;
        var b3 = t * t * t;
        return (b0 * X0 + b1 * X1 + b2 * X2 + b3 * X3, b0 * Y0 + b1 * Y1 + b2 * Y2 + b3 * Y3);
    }

    public (double X, double Y) Tangent(double t)
    {
        var u = 1 - t;
        var a = 3 * u * u;
        var b = 6 * u * t;
        var c = 3 * t * t;
        return (a * (X1 - X0) + b * (X2 - X1) + c * (X3 - X2), a * (Y1 - Y0) + b * (Y2 - Y1) + c * (Y3 - Y2));
    }
}

public static class MotionPaths
{
    private const int Samples = 48;
    private const double Tiny = 1e-9;

    public static PathPlace At(MotionPath path, double time) =>
        AtProgress(path, Interpolator.Evaluate(path.Progress, time));

    // Progress i + f is the share f of the way along segment i, measured along the curve.
    public static PathPlace AtProgress(MotionPath path, double progress)
    {
        var points = path.Points;
        if (points.Count == 0)
        {
            return default;
        }

        if (points.Count == 1)
        {
            return new PathPlace(points[0].X, points[0].Y, 0);
        }

        var p = Math.Clamp(double.IsFinite(progress) ? progress : 0, 0, points.Count - 1);
        var i = Math.Min((int)Math.Floor(p), points.Count - 2);
        var curve = Segment(points, i);
        var t = ParameterAt(curve, p - i);
        var (x, y) = curve.Point(t);
        return new PathPlace(x, y, Angle(curve, t));
    }

    public static double Length(MotionPath path)
    {
        var total = 0.0;
        for (var i = 0; i + 1 < path.Points.Count; i++)
        {
            total += Lengths(Segment(path.Points, i))[^1];
        }

        return total;
    }

    // Segment i runs from point i (with its out handle) to point i + 1 (with its in handle).
    public static Bezier Segment(IReadOnlyList<PathPoint> points, int i)
    {
        var a = points[i];
        var b = points[i + 1];
        var (outX, outY) = OutHandle(points, i);
        var (inX, inY) = InHandle(points, i + 1);
        return new Bezier(a.X, a.Y, a.X + outX, a.Y + outY, b.X + inX, b.Y + inY, b.X, b.Y);
    }

    // A handle that isn't given mirrors the point's other handle, or else follows the line through
    // the neighbors (a third of the way to the next point at the ends).
    public static (double X, double Y) OutHandle(IReadOnlyList<PathPoint> points, int i)
    {
        var p = points[i];
        if (p.OutX is { } x && p.OutY is { } y)
        {
            return (x, y);
        }

        if (p.InX is { } inX && p.InY is { } inY)
        {
            return (-inX, -inY);
        }

        return Smooth(points, i);
    }

    public static (double X, double Y) InHandle(IReadOnlyList<PathPoint> points, int i)
    {
        var p = points[i];
        if (p.InX is { } x && p.InY is { } y)
        {
            return (x, y);
        }

        if (p.OutX is { } outX && p.OutY is { } outY)
        {
            return (-outX, -outY);
        }

        var (sx, sy) = Smooth(points, i);
        return (-sx, -sy);
    }

    private static (double X, double Y) Smooth(IReadOnlyList<PathPoint> points, int i)
    {
        var previous = points[Math.Max(i - 1, 0)];
        var next = points[Math.Min(i + 1, points.Count - 1)];
        var divisor = i == 0 || i == points.Count - 1 ? 3.0 : 6.0;
        return ((next.X - previous.X) / divisor, (next.Y - previous.Y) / divisor);
    }

    private static double[] Lengths(Bezier curve)
    {
        var lengths = new double[Samples + 1];
        var (px, py) = curve.Point(0);
        for (var s = 1; s <= Samples; s++)
        {
            var (x, y) = curve.Point((double)s / Samples);
            lengths[s] = lengths[s - 1] + Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            (px, py) = (x, y);
        }

        return lengths;
    }

    // The curve parameter at the given share of the curve's length.
    private static double ParameterAt(Bezier curve, double share)
    {
        var lengths = Lengths(curve);
        var total = lengths[^1];
        if (total < Tiny)
        {
            return share;
        }

        var target = Math.Clamp(share, 0, 1) * total;
        var s = Array.BinarySearch(lengths, target);
        if (s >= 0)
        {
            return (double)s / Samples;
        }

        var above = Math.Min(~s, Samples);
        var below = above - 1;
        var span = lengths[above] - lengths[below];
        var f = span < Tiny ? 0 : (target - lengths[below]) / span;
        return (below + f) / Samples;
    }

    private static double Angle(Bezier curve, double t)
    {
        var (dx, dy) = curve.Tangent(t);
        if (dx * dx + dy * dy < Tiny)
        {
            var (ax, ay) = curve.Point(Math.Max(0, t - 0.01));
            var (bx, by) = curve.Point(Math.Min(1, t + 0.01));
            (dx, dy) = (bx - ax, by - ay);
        }

        if (dx * dx + dy * dy < Tiny)
        {
            (dx, dy) = (curve.X3 - curve.X0, curve.Y3 - curve.Y0);
        }

        return dx * dx + dy * dy < Tiny ? 0 : Math.Atan2(dy, dx) * 180 / Math.PI;
    }
}
