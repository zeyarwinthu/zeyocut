// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public sealed record ActionResult(int Tracks, int Keyframes, double Start, double End, IReadOnlyList<string> Warnings);

// save_action / apply_action: a rig's keyframes over a time window, by part role.
public static class RigActions
{
    public const double MinSpeed = 0.05;
    public const double MaxSpeed = 20;
    public const int MaxLoops = 1000;
    private const double Epsilon = 1e-6;

    public static ActionFile Save(Project project, Library library, string? rootId, double startTime, double endTime, string? name, bool overwrite)
    {
        var actionName = Library.CheckName(name, LibraryKind.Action);
        var action = Capture(project, rootId, startTime, endTime, actionName);
        library.Save(LibraryKind.Action, actionName, overwrite, path => AnalysisFiles.Save(path, action));
        return action;
    }

    public static ActionFile Capture(Project project, string? rootId, double startTime, double endTime, string name)
    {
        if (!double.IsFinite(startTime) || startTime < 0 || !double.IsFinite(endTime) || endTime <= startTime)
        {
            throw new ZeyoCutException($"the window must have 0 <= startTime < endTime (got {Num(startTime)}–{Num(endTime)})");
        }

        var duration = endTime - startTime;
        var action = new ActionFile { Name = name, SavedUtc = DateTime.UtcNow, Duration = KeyframeWindow.Round(duration) };
        foreach (var (role, layer) in Rigs.Roles(project, rootId))
        {
            foreach (var (property, value) in AnimatableProperties.Numeric(layer).Where(p => p.Name != AnimatableProperties.Path))
            {
                if (Window(value, startTime, endTime) is not { } keys)
                {
                    continue;
                }

                var mode = role != Rigs.RootRole ? ActionMode.Absolute : property switch
                {
                    "x" or "y" or "rotation" => ActionMode.Offset,
                    "scaleX" or "scaleY" when Math.Abs(keys[0].Value) > 1e-9 => ActionMode.Factor,
                    _ => ActionMode.Absolute,
                };
                var origin = keys[0].Value;
                foreach (var key in keys)
                {
                    key.Value = mode switch
                    {
                        ActionMode.Offset => key.Value - origin,
                        ActionMode.Factor => key.Value / origin,
                        _ => key.Value,
                    };
                }

                action.Tracks.Add(new ActionTrack { Role = role, Property = property, Mode = mode, Keyframes = keys });
            }

            if (layer is ImageLayer { Drawing.Keyframes.Count: > 0 } image)
            {
                var first = Interpolator.Evaluate(image.Drawing, startTime);
                var inside = image.Drawing.Keyframes
                    .Where(k => k.Time > startTime + Epsilon && k.Time <= endTime + Epsilon)
                    .Select(k => new DrawingKeyframe { Time = KeyframeWindow.Round(Math.Min(k.Time, endTime) - startTime), Value = k.Value })
                    .ToList();
                if (inside.Count > 0 || Interpolator.Evaluate(image.Drawing, endTime) != first)
                {
                    action.Tracks.Add(new ActionTrack
                    {
                        Role = role,
                        Property = AnimatableProperties.Drawing,
                        Mode = ActionMode.Absolute,
                        Drawings = [new DrawingKeyframe { Time = 0, Value = first }, .. inside],
                    });
                }
            }
        }

        if (action.Tracks.Count == 0)
        {
            throw new ZeyoCutException($"nothing under '{rootId}' is animated between {Num(startTime)}s and {Num(endTime)}s; set keyframes on the rig first");
        }

        return action;
    }

    public static ActionResult Apply(Project project, ActionFile action, string? rootId, double startTime, double speed, int loops)
    {
        if (!double.IsFinite(startTime) || startTime < 0)
        {
            throw new ZeyoCutException($"startTime must be >= 0 (got {Num(startTime)})");
        }

        if (!double.IsFinite(speed) || speed < MinSpeed || speed > MaxSpeed)
        {
            throw new ZeyoCutException($"speed must be between {Num(MinSpeed)} and {Num(MaxSpeed)} (got {Num(speed)})");
        }

        if (loops < 1 || loops > MaxLoops)
        {
            throw new ZeyoCutException($"loops must be between 1 and {MaxLoops} (got {loops})");
        }

        var roles = Rigs.Roles(project, rootId);
        var missing = action.Tracks.Select(t => t.Role).Distinct().Where(r => !roles.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            throw new ZeyoCutException($"action '{action.Name}' moves part(s) {string.Join(", ", missing)}, which rig '{rootId}' doesn't have; its parts: {string.Join(", ", roles.Keys)}");
        }

        var span = action.Duration / speed;
        var end = KeyframeWindow.Round(startTime + span * loops);
        double At(int loop, double time) => KeyframeWindow.Round(startTime + (loop * action.Duration + time) / speed);

        var targets = new List<(ActionTrack Track, AnimatedValue? Value, DrawingTrack? Drawing, double Base)>();
        foreach (var track in action.Tracks)
        {
            var layer = roles[track.Role];
            if (track.Property == AnimatableProperties.Drawing)
            {
                if (layer is not ImageLayer image)
                {
                    throw new ZeyoCutException($"action '{action.Name}' swaps drawings on '{track.Role}', but '{layer.Id}' is a {layer.TypeName} layer");
                }

                var unknown = track.Drawings!.Select(k => k.Value).Distinct().Where(d => !image.Drawings.ContainsKey(d)).ToList();
                if (unknown.Count > 0)
                {
                    throw new ZeyoCutException($"action '{action.Name}' uses drawing(s) {string.Join(", ", unknown)} on '{track.Role}', but '{layer.Id}' only has {Ids.List(image.Drawings.Keys)}");
                }

                targets.Add((track, null, image.Drawing, 0));
            }
            else
            {
                var value = AnimatableProperties.GetNumeric(layer, track.Property);
                targets.Add((track, value, null, Interpolator.Evaluate(value, startTime)));
            }
        }

        var count = 0;
        foreach (var (track, value, drawing, origin) in targets)
        {
            if (drawing is not null)
            {
                drawing.Keyframes.RemoveAll(k => k.Time >= startTime - Epsilon && k.Time <= end + Epsilon);
                for (var loop = 0; loop < loops; loop++)
                {
                    foreach (var key in track.Drawings!)
                    {
                        Upsert(drawing.Keyframes, new DrawingKeyframe { Time = At(loop, key.Time), Value = key.Value }, k => k.Time);
                    }
                }

                drawing.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
                count += drawing.Keyframes.Count(k => k.Time >= startTime - Epsilon && k.Time <= end + Epsilon);
                continue;
            }

            var keys = track.Keyframes!;
            var step = track.Mode == ActionMode.Offset ? keys[^1].Value - keys[0].Value : 0;
            value!.Keyframes.RemoveAll(k => k.Time >= startTime - Epsilon && k.Time <= end + Epsilon);
            for (var loop = 0; loop < loops; loop++)
            {
                foreach (var key in keys)
                {
                    var v = track.Mode switch
                    {
                        ActionMode.Offset => origin + key.Value + loop * step,
                        ActionMode.Factor => origin * key.Value,
                        _ => key.Value,
                    };
                    Upsert(value.Keyframes, new Keyframe { Time = At(loop, key.Time), Value = v, Easing = key.Easing }, k => k.Time);
                }
            }

            value.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
            count += value.Keyframes.Count(k => k.Time >= startTime - Epsilon && k.Time <= end + Epsilon);
        }

        var warnings = new List<string>();
        if (end > project.Duration + Epsilon)
        {
            warnings.Add($"the action runs until {Num(end)}s, past the project's end ({Num(project.Duration)}s); extend it with update_project duration");
        }

        return new ActionResult(action.Tracks.Count, count, startTime, end, warnings);
    }

    public static string Describe(ActionFile action) =>
        $"{Num(action.Duration)}s, parts {string.Join(", ", action.Tracks.Select(t => t.Role).Distinct())} ({action.Tracks.Count} track(s): {string.Join(", ", action.Tracks.Select(t => $"{t.Role}.{t.Property}"))})";

    // The value's keyframes between the times (relative to startTime), with the curve's values at
    // both edges; null when it doesn't change in the window.
    private static List<Keyframe>? Window(AnimatedValue value, double startTime, double endTime)
    {
        var keys = value.Keyframes;
        if (keys.Count == 0)
        {
            return null;
        }

        var inside = keys.Where(k => k.Time > startTime + Epsilon && k.Time < endTime - Epsilon).ToList();
        var first = Interpolator.Evaluate(value, startTime);
        var last = Interpolator.Evaluate(value, endTime);
        if (inside.Count == 0 && Math.Abs(first - last) < 1e-9)
        {
            return null;
        }

        var result = new List<Keyframe> { new() { Time = 0, Value = first, Easing = EasingAt(keys, startTime) } };
        result.AddRange(inside.Select(k => new Keyframe { Time = KeyframeWindow.Round(k.Time - startTime), Value = k.Value, Easing = k.Easing }));
        result.Add(new Keyframe { Time = KeyframeWindow.Round(endTime - startTime), Value = last, Easing = EasingAt(keys, endTime) });
        return result;
    }

    // The easing of the segment that runs from 'time' (the keyframe at or before it).
    private static Easing EasingAt(List<Keyframe> keys, double time)
    {
        var key = keys.LastOrDefault(k => k.Time <= time + Epsilon);
        return key is null || key == keys[^1] ? Easing.Linear : key.Easing;
    }

    private static void Upsert<T>(List<T> keys, T item, Func<T, double> timeOf)
    {
        var index = keys.FindIndex(k => Math.Abs(timeOf(k) - timeOf(item)) < Epsilon);
        if (index >= 0)
        {
            keys[index] = item;
        }
        else
        {
            keys.Add(item);
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
