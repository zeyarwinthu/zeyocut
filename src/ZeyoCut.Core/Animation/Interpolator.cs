// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

public static class Interpolator
{
    public static double Evaluate(AnimatedValue property, double time)
    {
        var keys = property.Keyframes;
        if (keys.Count == 0)
        {
            return property.Value;
        }

        if (time <= keys[0].Time)
        {
            return keys[0].Value;
        }

        var last = keys[^1];
        if (time >= last.Time)
        {
            return last.Value;
        }

        var i = FindSegment(keys, time);
        var a = keys[i];
        var b = keys[i + 1];
        var span = b.Time - a.Time;
        if (span <= 0)
        {
            return b.Value;
        }

        var u = (time - a.Time) / span;
        return a.Value + (b.Value - a.Value) * EasingFunctions.Apply(a.Easing, u);
    }

    public static string Evaluate(DrawingTrack track, double time)
    {
        var keys = track.Keyframes;
        if (keys.Count == 0)
        {
            return track.Value;
        }

        var value = keys[0].Value;
        foreach (var key in keys)
        {
            if (key.Time > time)
            {
                break;
            }

            value = key.Value;
        }

        return value;
    }

    private static int FindSegment(List<Keyframe> keys, double time)
    {
        var lo = 0;
        var hi = keys.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid].Time <= time)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
