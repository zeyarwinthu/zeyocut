// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

public static class EasingFunctions
{
    private const double BackC1 = 1.70158;
    private const double BackC3 = BackC1 + 1;
    private const double ElasticC4 = 2 * Math.PI / 3;

    public static double Apply(Easing easing, double t)
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
            default:
                return t;
        }
    }

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
