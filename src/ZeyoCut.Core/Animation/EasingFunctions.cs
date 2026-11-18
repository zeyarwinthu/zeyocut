// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

public static class EasingFunctions
{
    private const double BackC1 = 1.70158;
    private const double BackC3 = BackC1 + 1;
    private const double ElasticC4 = 2 * Math.PI / 3;

    // bezier: a cubicBezier easing's x1, y1, x2, y2; without valid ones it's linear.
    public static double Apply(Easing easing, double t, double[]? bezier = null)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        switch (easing)
        {
            case Easing.Linear:
                return t;
            case Easing.Hold:
                return 0;
            case Easing.EaseIn:
                return t * t * t;
            case Easing.EaseOut:
                {
                    var u = 1 - t;
                    return 1 - u * u * u;
                }
            case Easing.EaseInOut:
                {
                    if (t < 0.5)
                    {
                        return 4 * t * t * t;
                    }

                    var u = -2 * t + 2;
                    return 1 - u * u * u / 2;
                }
            case Easing.EaseOutBack:
                {
                    var u = t - 1;
                    return 1 + BackC3 * u * u * u + BackC1 * u * u;
                }
            case Easing.EaseOutBounce:
                return Bounce(t);
            case Easing.EaseOutElastic:
                return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * ElasticC4) + 1;
            case Easing.CubicBezier:
                return bezier is [var x1, var y1, var x2, var y2] && BezierProblem(bezier) is null ? CubicBezier(x1, y1, x2, y2, t) : t;
            default:
                return t;
        }
    }

    // CSS's cubic-bezier(): the curve from (0, 0) to (1, 1) pulled toward (x1, y1) and (x2, y2), read
    // as its y where its x is t. With x1 and x2 from 0 to 1 there's one y for each t; y1 and y2 can go
    // past 0 and 1 to overshoot.
    public static double CubicBezier(double x1, double y1, double x2, double y2, double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        return Bezier(y1, y2, SolveX(x1, x2, t));
    }

    // What's wrong with a cubicBezier easing's numbers, or null when they're fine.
    public static string? BezierProblem(double[]? bezier)
    {
        if (bezier is not { Length: 4 })
        {
            return $"bezier must be 4 numbers, [x1, y1, x2, y2] (got {Show(bezier)})";
        }

        if (bezier.Any(v => !double.IsFinite(v)))
        {
            return $"bezier's numbers must be finite (got {Show(bezier)})";
        }

        return bezier[0] is < 0 or > 1 || bezier[2] is < 0 or > 1
            ? $"bezier's x1 and x2 must be from 0 to 1, so the curve has one value at each time (got {Show(bezier)})"
            : null;
    }

    // One coordinate of the curve at s, from 0 at s = 0 to 1 at s = 1 with handles p1 and p2.
    private static double Bezier(double p1, double p2, double s)
    {
        var u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }

    private static double Slope(double p1, double p2, double s)
    {
        var u = 1 - s;
        return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
    }

    // The s where the curve's x is t. x only grows with s, so Newton's steps are kept inside an
    // interval that halves whenever a step would leave it.
    private static double SolveX(double x1, double x2, double t)
    {
        var low = 0.0;
        var high = 1.0;
        var s = t;
        for (var i = 0; i < 100; i++)
        {
            var error = Bezier(x1, x2, s) - t;
            if (Math.Abs(error) < 1e-12)
            {
                break;
            }

            if (error > 0)
            {
                high = s;
            }
            else
            {
                low = s;
            }

            var slope = Slope(x1, x2, s);
            var next = slope > 1e-9 ? s - error / slope : double.NaN;
            s = next > low && next < high ? next : (low + high) / 2;
        }

        return s;
    }

    private static string Show(double[]? numbers) =>
        numbers is null ? "none" : "[" + string.Join(", ", numbers.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture))) + "]";

    private static double Bounce(double t)
    {
        const double n1 = 7.5625;
        const double d1 = 2.75;

        if (t < 1 / d1)
        {
            return n1 * t * t;
        }

        if (t < 2 / d1)
        {
            t -= 1.5 / d1;
            return n1 * t * t + 0.75;
        }

        if (t < 2.5 / d1)
        {
            t -= 2.25 / d1;
            return n1 * t * t + 0.9375;
        }

        t -= 2.625 / d1;
        return n1 * t * t + 0.984375;
    }
}
