// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

// Time edits on keyframe tracks (keyframe times are absolute timeline seconds).
public static class KeyframeWindow
{
    private const double Tolerance = 1e-9;

    // Drops keys outside [from, to] while every value inside the window stays exactly as before:
    // a linear or hold segment crossing an edge gets a boundary key on the edge; an eased segment keeps
    // its outside key (re-timing an ease would change its shape). With keepOutside false the edge always
    // gets a boundary key, which is exact for linear/hold and close for eases.
    public static void Restrict(AnimatedValue value, double from, double to, bool keepOutside = true)
    {
        var keys = value.Keyframes;
        if (keys.Count == 0)
        {
            return;
        }

        var result = new List<Keyframe>();
        var inside = keys.Where(k => k.Time >= from - Tolerance && k.Time <= to + Tolerance).ToList();
        var before = keys.LastOrDefault(k => k.Time < from - Tolerance);
        var after = keys.FirstOrDefault(k => k.Time > to + Tolerance);

        if (before is not null && (inside.Count == 0 || inside[0].Time > from + Tolerance))
        {
            var hasNext = inside.Count > 0 || after is not null;
            var crossesEased = hasNext && before.Easing is not (Easing.Linear or Easing.Hold);
            result.Add(crossesEased && keepOutside
                ? Copy(before)
                : new Keyframe { Time = from, Value = Interpolator.Evaluate(value, from), Easing = before.Easing });
        }

        result.AddRange(inside.Select(Copy));

        if (after is not null && (result.Count == 0 || result[^1].Time < to - Tolerance))
        {
            var last = result.Count > 0 ? result[^1] : null;
            if (last is not null && last.Easing is not (Easing.Linear or Easing.Hold) && keepOutside)
            {
                result.Add(Copy(after));
            }
            else
            {
                result.Add(new Keyframe { Time = to, Value = Interpolator.Evaluate(value, to), Easing = Easing.Linear });
            }
        }

        value.Keyframes = result;
    }

    public static void Restrict(DrawingTrack track, double from, double to)
    {
        var keys = track.Keyframes;
        if (keys.Count == 0)
        {
            return;
        }

        var result = new List<DrawingKeyframe>();
        var inside = keys.Where(k => k.Time >= from - Tolerance && k.Time <= to + Tolerance).ToList();
        if (keys.Any(k => k.Time < from - Tolerance) && (inside.Count == 0 || inside[0].Time > from + Tolerance))
        {
            result.Add(new DrawingKeyframe { Time = from, Value = Interpolator.Evaluate(track, from) });
        }

        result.AddRange(inside.Select(k => new DrawingKeyframe { Time = k.Time, Value = k.Value }));
        if (result.Count == 0)
        {
            track.Value = Interpolator.Evaluate(track, from);
        }

        track.Keyframes = result;
    }

    public static void Shift(AnimatedValue value, double delta)
    {
        foreach (var key in value.Keyframes)
        {
            key.Time = Round(key.Time + delta);
        }

        if (value.Keyframes.Any(k => k.Time < 0))
        {
            Restrict(value, 0, double.PositiveInfinity, keepOutside: false);
        }
    }

    public static void Shift(DrawingTrack track, double delta)
    {
        foreach (var key in track.Keyframes)
        {
            key.Time = Round(key.Time + delta);
        }

        if (track.Keyframes.Any(k => k.Time < 0))
        {
            Restrict(track, 0, double.PositiveInfinity);
        }
    }

    // Stretches time around 'origin' by 'factor' (2 = twice as long).
    public static void Scale(AnimatedValue value, double origin, double factor)
    {
        foreach (var key in value.Keyframes)
        {
            key.Time = Round(origin + (key.Time - origin) * factor);
        }

        if (value.Keyframes.Any(k => k.Time < 0))
        {
            Restrict(value, 0, double.PositiveInfinity, keepOutside: false);
        }
    }

    public static void Scale(DrawingTrack track, double origin, double factor)
    {
        foreach (var key in track.Keyframes)
        {
            key.Time = Round(origin + (key.Time - origin) * factor);
        }

        if (track.Keyframes.Any(k => k.Time < 0))
        {
            Restrict(track, 0, double.PositiveInfinity);
        }
    }

    public static double Round(double time) => Math.Round(time, 9);

    private static Keyframe Copy(Keyframe key) => new() { Time = key.Time, Value = key.Value, Easing = key.Easing };
}
