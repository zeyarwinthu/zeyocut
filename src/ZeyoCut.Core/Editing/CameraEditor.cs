// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record CameraChange(IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings);

public sealed record ShakeResult(int Keyframes, double Start, double End, IReadOnlyList<string> Warnings);

// set_camera, camera_shake, and keeping the camera's keyframes in step with ripple edits.
public static class CameraEditor
{
    public const double MaxShakeDuration = 60;
    public const double MaxIntensity = 1000;
    public const double MinFrequency = 0.5;
    public const double MaxFrequency = 60;

    // Degrees of shake rotation per pixel of intensity (20 px shakes about 1°).
    public const double RotationPerPixel = 0.05;

    // A cut in an animated camera holds the old value for this long before the jump.
    private const double JumpGap = 0.001;
    private const double Tolerance = 1e-9;

    public static Camera Ensure(Project project) =>
        project.Camera ??= new Camera { X = new(project.Width / 2.0), Y = new(project.Height / 2.0) };

    public static CameraChange Set(Project project, double? x, double? y, double? zoom, double? rotation, bool reset)
    {
        var changes = new List<string>();
        var warnings = new List<string>();
        if (reset)
        {
            if (project.Camera is null && x is null && y is null && zoom is null && rotation is null)
            {
                throw new ZeyoCutException("the project has no camera to reset");
            }

            project.Camera = null;
            changes.Add("removed the camera with its keyframes and shake (the canvas is shown as is)");
            if (x is null && y is null && zoom is null && rotation is null)
            {
                return new CameraChange(changes, warnings);
            }
        }

        if (x is null && y is null && zoom is null && rotation is null)
        {
            throw new ZeyoCutException("pass x, y, zoom or rotation (or reset=true)");
        }

        var camera = Ensure(project);
        foreach (var (name, value, number) in new[] { ("x", camera.X, x), ("y", camera.Y, y), ("zoom", camera.Zoom, zoom), ("rotation", camera.Rotation, rotation) })
        {
            if (number is not { } n)
            {
                continue;
            }

            if (!double.IsFinite(n))
            {
                throw new ZeyoCutException($"{name} must be a finite number");
            }

            changes.Add($"{name} {Num(value.Value)} → {Num(n)}");
            value.Value = n;
            if (value.Keyframes.Count > 0)
            {
                warnings.Add($"camera {name} has {value.Keyframes.Count} keyframe(s), so the value you set is only used after clear_keyframes");
            }
        }

        return new CameraChange(changes, warnings);
    }

    // Seeded shake on the shake channels: a key every 1/frequency seconds, alternating direction
    // with random strength, fading out to 0 at the end. The same seed always gives the same keys.
    public static ShakeResult Shake(Project project, double startTime, double duration, double intensity, double frequency, int seed)
    {
        if (!double.IsFinite(startTime) || startTime < 0)
        {
            throw new ZeyoCutException($"startTime must be >= 0 (got {Num(startTime)})");
        }

        if (!double.IsFinite(duration) || duration <= 0 || duration > MaxShakeDuration)
        {
            throw new ZeyoCutException($"duration must be greater than 0 and at most {Num(MaxShakeDuration)}s (got {Num(duration)})");
        }

        if (!double.IsFinite(intensity) || intensity <= 0 || intensity > MaxIntensity)
        {
            throw new ZeyoCutException($"intensity must be greater than 0 and at most {Num(MaxIntensity)} pixels (got {Num(intensity)})");
        }

        if (!double.IsFinite(frequency) || frequency < MinFrequency || frequency > MaxFrequency)
        {
            throw new ZeyoCutException($"frequency must be between {Num(MinFrequency)} and {Num(MaxFrequency)} per second (got {Num(frequency)})");
        }

        var warnings = new List<string>();
        if (frequency > project.Fps / 2.0)
        {
            warnings.Add($"frequency {Num(frequency)} is more than half the frame rate ({project.Fps} fps), so some direction changes fall between frames; use {Num(project.Fps / 2.0)} or less");
        }

        var camera = Ensure(project);
        var end = KeyframeWindow.Round(startTime + duration);
        var steps = Math.Max(2, (int)Math.Ceiling(duration * frequency - 1e-9));
        var random = new SplitMix((ulong)(long)seed);
        var count = 0;
        foreach (var (track, amplitude) in new[] { (camera.ShakeX, intensity), (camera.ShakeY, intensity), (camera.ShakeRotation, intensity * RotationPerPixel) })
        {
            track.Keyframes.RemoveAll(k => k.Time >= startTime - Tolerance && k.Time <= end + Tolerance);
            var sign = random.NextDouble() < 0.5 ? 1.0 : -1.0;
            for (var i = 0; i <= steps; i++)
            {
                var value = 0.0;
                if (i > 0 && i < steps)
                {
                    var fade = 1 - (double)i / steps;
                    value = Math.Round(sign * (0.4 + 0.6 * random.NextDouble()) * amplitude * fade, 3);
                    sign = -sign;
                }

                track.Keyframes.Add(new Keyframe { Time = KeyframeWindow.Round(startTime + duration * i / steps), Value = value, Easing = Easing.EaseInOut });
                count++;
            }

            track.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        if (end > project.Duration + Tolerance)
        {
            warnings.Add($"the shake runs until {Num(end)}s, past the project's end ({Num(project.Duration)}s)");
        }

        return new ShakeResult(count, startTime, end, warnings);
    }

    // The tracks that belong to the timeline rather than to a layer: the camera's, and the opacity of
    // the subject cutouts (they have no start of their own to move with).
    private static List<AnimatedValue> TimelineTracks(Project project) =>
        (project.Camera is { } camera ? AnimatableProperties.Numeric(camera).Select(p => p.Value) : [])
            .Concat(project.Layers.OfType<CutoutLayer>().Select(c => c.Opacity))
            .ToList();

    // Ripple: what played from 'end' on now plays from 'start'. Values before 'start' stay; the
    // camera jumps at the cut like the picture does.
    public static void RemoveTime(Project project, double start, double end)
    {
        if (end <= start + Tolerance)
        {
            return;
        }

        foreach (var value in TimelineTracks(project))
        {
            if (value.Keyframes.Count == 0)
            {
                continue;
            }

            var left = Copy(value);
            KeyframeWindow.Restrict(left, 0, start, keepOutside: false);
            var right = Copy(value);
            KeyframeWindow.Restrict(right, end, double.PositiveInfinity, keepOutside: false);
            KeyframeWindow.Shift(right, -(end - start));
            var keys = left.Keyframes;
            if (keys.Count > 0 && right.Keyframes.Count > 0 && Math.Abs(keys[^1].Time - right.Keyframes[0].Time) < KeyframeEditor.TimeTolerance)
            {
                var join = keys[^1];
                var hold = KeyframeWindow.Round(start - JumpGap);
                if (Math.Abs(join.Value - right.Keyframes[0].Value) < Tolerance || hold <= 0 || (keys.Count > 1 && keys[^2].Time >= hold - Tolerance))
                {
                    keys.RemoveAt(keys.Count - 1);
                }
                else
                {
                    join.Time = hold;
                    join.Value = Interpolator.Evaluate(value, hold);
                    join.Easing = Easing.Hold;
                }
            }

            value.Keyframes = [.. keys, .. right.Keyframes];
        }
    }

    // Ripple the other way: what played from 'at' on now plays 'length' seconds later, and the
    // camera holds still in between.
    public static void InsertTime(Project project, double at, double length)
    {
        if (length <= Tolerance)
        {
            return;
        }

        foreach (var value in TimelineTracks(project))
        {
            if (value.Keyframes.Count == 0 || value.Keyframes[^1].Time < at - Tolerance)
            {
                continue;
            }

            var left = Copy(value);
            KeyframeWindow.Restrict(left, 0, at, keepOutside: false);
            var right = Copy(value);
            KeyframeWindow.Restrict(right, at, double.PositiveInfinity, keepOutside: false);
            KeyframeWindow.Shift(right, length);
            value.Keyframes = [.. left.Keyframes, .. right.Keyframes];
        }
    }

    // A new canvas size keeps the camera's view relative to the canvas center.
    public static string? Recenter(Project project, int oldWidth, int oldHeight)
    {
        if (project.Camera is not { } camera || (oldWidth == project.Width && oldHeight == project.Height))
        {
            return null;
        }

        var dx = (project.Width - oldWidth) / 2.0;
        var dy = (project.Height - oldHeight) / 2.0;
        foreach (var (value, delta) in new[] { (camera.X, dx), (camera.Y, dy) })
        {
            value.Value += delta;
            foreach (var key in value.Keyframes)
            {
                key.Value += delta;
            }
        }

        return $"camera moved by ({Num(dx)},{Num(dy)}) to keep its view on the new canvas center";
    }

    public static string Describe(Project project)
    {
        if (project.Camera is not { } camera)
        {
            return "camera: none (the canvas is shown as is; set_camera, a camera keyframe or camera_shake adds one)";
        }

        var animated = AnimatableProperties.Numeric(camera).Where(p => p.Value.Keyframes.Count > 0).Select(p => $"{p.Name}({p.Value.Keyframes.Count})").ToList();
        return $"camera: x={Num(camera.X.Value)} y={Num(camera.Y.Value)} zoom={Num(camera.Zoom.Value)} rotation={Num(camera.Rotation.Value)}"
            + (animated.Count > 0 ? "  keys: " + string.Join(" ", animated) : "");
    }

    private static AnimatedValue Copy(AnimatedValue value) =>
        new(value.Value) { Keyframes = value.Keyframes.Select(k => new Keyframe { Time = k.Time, Value = k.Value, Easing = k.Easing }).ToList() };

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // SplitMix64: the same seed gives the same numbers on every machine and .NET version.
    private sealed class SplitMix(ulong seed)
    {
        private ulong _state = seed;

        public double NextDouble()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
