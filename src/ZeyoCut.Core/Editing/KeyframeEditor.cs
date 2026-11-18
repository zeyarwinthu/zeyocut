// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// Bezier: the four numbers of a cubicBezier easing, which they imply when Easing is left out.
public sealed record KeyframeEntry(string LayerId, string Property, double Time, JsonElement Value, string? Easing = null, IReadOnlyList<double>? Bezier = null);

public sealed record KeyframeRef(string LayerId, string Property, double Time);

// A keyframe dragged to another time and, in the Animate view's graph, to another value (null keeps it).
public sealed record KeyframeMove(string LayerId, string Property, double Time, double NewTime, double? Value = null);

public sealed record KeyframeChange(int Added, int Replaced, IReadOnlyList<string> Warnings);

public static class KeyframeEditor
{
    public const double TimeTolerance = 1e-6;

    public static KeyframeChange SetKeyframes(Project project, IReadOnlyList<KeyframeEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            throw new ZeyoCutException("entries is empty; pass at least one {layerId, property, time, value, easing}");
        }

        var errors = new List<string>();
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var added = 0;
        var replaced = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            try
            {
                if (entry is null)
                {
                    throw new ZeyoCutException("entry is empty");
                }

                if (SetOne(project, entry, warnings))
                {
                    replaced++;
                }
                else
                {
                    added++;
                }

                if (entry.Time > project.Duration && project.FindLayer(entry.LayerId) is not RedactionLayer)
                {
                    warnings.Add($"{entry.LayerId}.{entry.Property}: some keyframes are after the project end ({project.Duration}s)");
                }
            }
            catch (ZeyoCutException ex)
            {
                errors.Add($"entry {i} ({entry?.LayerId}.{entry?.Property} @ {entry?.Time}s): {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new ZeyoCutException(
                $"{errors.Count} of {entries.Count} entries are invalid, so nothing was changed:\n- " + string.Join("\n- ", errors));
        }

        return new KeyframeChange(added, replaced, warnings.ToList());
    }

    public static int RemoveKeyframes(Project project, IReadOnlyList<KeyframeRef>? refs)
    {
        if (refs is null || refs.Count == 0)
        {
            throw new ZeyoCutException("entries is empty; pass at least one {layerId, property, time}");
        }

        var errors = new List<string>();
        var removed = 0;
        for (var i = 0; i < refs.Count; i++)
        {
            var target = refs[i];
            try
            {
                if (target is null)
                {
                    throw new ZeyoCutException("entry is empty");
                }

                RemoveOne(project, target);
                removed++;
            }
            catch (ZeyoCutException ex)
            {
                errors.Add($"entry {i} ({target?.LayerId}.{target?.Property} @ {target?.Time}s): {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new ZeyoCutException(
                $"{errors.Count} of {refs.Count} entries are invalid, so nothing was changed:\n- " + string.Join("\n- ", errors));
        }

        return removed;
    }

    public static int ClearKeyframes(Project project, string layerId, string? property)
    {
        if (layerId == Camera.Id)
        {
            var camera = RequireCamera(project);
            var values = property is null ? AnimatableProperties.Numeric(camera).Select(p => p.Value).ToList() : [AnimatableProperties.GetNumeric(camera, property)];
            var cleared = values.Sum(v => v.Keyframes.Count);
            values.ForEach(v => v.Keyframes.Clear());
            return cleared;
        }

        if (project.FindLayer(layerId) is CutoutLayer cutout)
        {
            var opacity = CutoutOpacity(cutout, property ?? "opacity");
            var cleared = opacity.Keyframes.Count;
            opacity.Keyframes.Clear();
            return cleared;
        }

        if (project.FindLayer(layerId) is RedactionLayer redaction)
        {
            return RedactionEditor.ClearBoxes(redaction, property);
        }

        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        var count = 0;
        var properties = property is null
            ? AnimatableProperties.Numeric(layer).Select(p => p.Name).Where(n => n != AnimatableProperties.Path).Append(AnimatableProperties.Drawing).ToList()
            : [CanonicalProperty(property)];
        if (properties.Contains(AnimatableProperties.Path))
        {
            throw PathByHand();
        }

        if (property is null && layer.MotionPath is { } path)
        {
            count += path.Progress.Keyframes.Count;
            layer.MotionPath = null;
        }

        foreach (var name in properties)
        {
            if (name == AnimatableProperties.Drawing)
            {
                if (layer is ImageLayer image)
                {
                    var track = image.Drawing;
                    if (track.Keyframes.Count > 0 && !image.Drawings.ContainsKey(track.Value))
                    {
                        track.Value = track.Keyframes[0].Value;
                    }

                    count += track.Keyframes.Count;
                    track.Keyframes.Clear();
                }
                else if (property is not null)
                {
                    throw new ZeyoCutException($"'drawing' is only for image layers; '{layer.Id}' is a {layer.TypeName} layer");
                }

                continue;
            }

            var value = AnimatableProperties.GetNumeric(layer, name);
            count += value.Keyframes.Count;
            value.Keyframes.Clear();
        }

        return count;
    }

    // Keyframes dragged to other times, and in the Animate view's graph to other values, each keeping
    // its easing: all or nothing, and none lands on a keyframe that stays. Returns a one-line summary.
    public static string MoveKeyframes(Project project, IReadOnlyList<KeyframeMove>? moves)
    {
        if (moves is null || moves.Count == 0)
        {
            throw new ZeyoCutException("there are no keyframes to move");
        }

        var errors = new List<string>();
        var tracks = new Dictionary<object, (string Where, List<double> Times, Dictionary<int, (double Time, double? Value)> Moved)>(ReferenceEqualityComparer.Instance);
        var wheres = new List<string>();
        for (var i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            try
            {
                if (move is null)
                {
                    throw new ZeyoCutException("entry is empty");
                }

                if (!double.IsFinite(move.NewTime) || move.NewTime < 0)
                {
                    throw new ZeyoCutException($"time must be >= 0 (got {move.NewTime})");
                }

                if (move.Value is { } value && !double.IsFinite(value))
                {
                    throw new ZeyoCutException($"value must be a finite number (got {value})");
                }

                var (numbers, drawings, where) = KeysOf(project, move.LayerId, move.Property);
                if (drawings is not null && move.Value is not null)
                {
                    throw new ZeyoCutException($"{where} keyframes are drawing names, which a move keeps; set_keyframes changes one");
                }

                var list = (object?)numbers ?? drawings!;
                if (!tracks.TryGetValue(list, out var track))
                {
                    track = (where, numbers?.Select(k => k.Time).ToList() ?? drawings!.Select(k => k.Time).ToList(), []);
                    tracks[list] = track;
                }

                var index = track.Times.FindIndex(t => Math.Abs(t - move.Time) < TimeTolerance);
                if (index < 0)
                {
                    throw NoKeyframe(track.Times, move.Time, where);
                }

                if (!track.Moved.TryAdd(index, (KeyframeWindow.Round(move.NewTime), move.Value)))
                {
                    throw new ZeyoCutException($"the keyframe at {Num(move.Time)}s on {where} is moved twice");
                }

                if (!wheres.Contains(where))
                {
                    wheres.Add(where);
                }
            }
            catch (ZeyoCutException ex)
            {
                errors.Add(moves.Count == 1 ? ex.Message : $"entry {i} ({move?.LayerId}.{move?.Property} @ {move?.Time}s): {ex.Message}");
            }
        }

        foreach (var (where, times, moved) in tracks.Values)
        {
            var after = times.Select((t, i) => moved.TryGetValue(i, out var to) ? to.Time : t).ToList();
            foreach (var (index, to) in moved)
            {
                if (after.Where((t, i) => i != index && Math.Abs(t - to.Time) < TimeTolerance).Any())
                {
                    errors.Add($"{where} has a keyframe at {Num(to.Time)}s already, and a keyframe can't land on another");
                }
            }
        }

        Refuse(errors.Distinct().ToList());
        foreach (var (list, (_, _, moved)) in tracks)
        {
            if (list is List<Keyframe> numbers)
            {
                foreach (var (index, to) in moved)
                {
                    numbers[index].Time = to.Time;
                    numbers[index].Value = to.Value ?? numbers[index].Value;
                }

                numbers.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            else if (list is List<DrawingKeyframe> drawings)
            {
                foreach (var (index, to) in moved)
                {
                    drawings[index].Time = to.Time;
                }

                drawings.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
        }

        if (moves.Count > 1)
        {
            return $"moved {moves.Count} keyframes on {string.Join(", ", wheres)}";
        }

        var only = moves[0];
        return $"moved {wheres[0]}'s keyframe from {Num(only.Time)}s to {Num(only.NewTime)}s" + (only.Value is { } shown ? $", value {Num(shown)}" : "");
    }

    // The easing of keyframes, as set_keyframes gives it when it replaces them with their own time
    // and value: all or nothing. Drawings always hold. Returns how many changed.
    public static int SetEasing(Project project, IReadOnlyList<KeyframeRef>? refs, Easing easing, IReadOnlyList<double>? bezier = null)
    {
        if (refs is null || refs.Count == 0)
        {
            throw new ZeyoCutException("there are no keyframes to ease");
        }

        var numbers = easing == Easing.CubicBezier ? bezier?.ToArray() : null;
        if (easing == Easing.CubicBezier && EasingFunctions.BezierProblem(numbers) is { } problem)
        {
            throw new ZeyoCutException(problem);
        }

        if (easing != Easing.CubicBezier && bezier is not null)
        {
            throw new ZeyoCutException($"bezier goes with easing cubicBezier, not {EnumNames.ToName(easing)}");
        }

        var errors = new List<string>();
        var keys = new List<Keyframe>();
        for (var i = 0; i < refs.Count; i++)
        {
            var target = refs[i];
            try
            {
                if (target is null)
                {
                    throw new ZeyoCutException("entry is empty");
                }

                var (list, drawings, where) = KeysOf(project, target.LayerId, target.Property);
                if (drawings is not null)
                {
                    throw new ZeyoCutException($"{where} keyframes always hold");
                }

                keys.Add(list!.Find(k => Math.Abs(k.Time - target.Time) < TimeTolerance) ?? throw NoKeyframe(list.Select(k => k.Time), target.Time, where));
            }
            catch (ZeyoCutException ex)
            {
                errors.Add(refs.Count == 1 ? ex.Message : $"entry {i} ({target?.LayerId}.{target?.Property} @ {target?.Time}s): {ex.Message}");
            }
        }

        Refuse(errors);
        foreach (var key in keys)
        {
            key.Easing = easing;
            key.Bezier = numbers?.ToArray();
        }

        return keys.Count;
    }

    private static bool SetOne(Project project, KeyframeEntry entry, ISet<string> warnings)
    {
        if (entry.LayerId == Camera.Id)
        {
            if (!double.IsFinite(entry.Time) || entry.Time < 0)
            {
                throw new ZeyoCutException($"time must be >= 0 (got {entry.Time})");
            }

            var track = AnimatableProperties.GetNumeric(CameraEditor.Ensure(project), entry.Property);
            var (cameraEasing, cameraBezier) = EasingOf(entry);
            var key = new Keyframe { Time = entry.Time, Value = ReadNumber(entry.Value, entry.Property), Easing = cameraEasing, Bezier = cameraBezier };
            return Upsert(track.Keyframes, entry.Time, k => k.Time, key);
        }

        if (project.FindLayer(entry.LayerId) is CutoutLayer cutout)
        {
            var opacity = CutoutOpacity(cutout, entry.Property);
            if (!double.IsFinite(entry.Time) || entry.Time < 0)
            {
                throw new ZeyoCutException($"time must be >= 0 (got {entry.Time})");
            }

            var (cutoutEasing, cutoutBezier) = EasingOf(entry);
            var key = new Keyframe { Time = entry.Time, Value = ReadNumber(entry.Value, "opacity"), Easing = cutoutEasing, Bezier = cutoutBezier };
            return Upsert(opacity.Keyframes, entry.Time, k => k.Time, key);
        }

        // A redaction's keyframed boxes are in the footage's own seconds and pixels.
        if (project.FindLayer(entry.LayerId) is RedactionLayer redaction)
        {
            var (track, name, box) = RedactionEditor.BoxValue(redaction, entry.Property);
            if (!double.IsFinite(entry.Time) || entry.Time < 0)
            {
                throw new ZeyoCutException($"time must be >= 0 (got {entry.Time})");
            }

            var value = ReadNumber(entry.Value, $"{track}.{name}");
            if (name is "width" or "height" && value <= 0)
            {
                throw new ZeyoCutException($"{track}.{name} must be above 0 (got {value})");
            }

            var (boxEasing, boxBezier) = EasingOf(entry);
            return Upsert(box.Keyframes, entry.Time, k => k.Time, new Keyframe { Time = entry.Time, Value = value, Easing = boxEasing, Bezier = boxBezier });
        }

        var layer = ProjectEditor.GetVisualLayer(project, entry.LayerId);
        var property = CanonicalProperty(entry.Property);
        if (!double.IsFinite(entry.Time) || entry.Time < 0)
        {
            throw new ZeyoCutException($"time must be >= 0 (got {entry.Time})");
        }

        if (property == AnimatableProperties.Drawing)
        {
            if (layer is not ImageLayer image)
            {
                throw new ZeyoCutException($"'drawing' is only for image layers; '{layer.Id}' is a {layer.TypeName} layer");
            }

            var name = ReadString(entry.Value);
            if (!image.Drawings.ContainsKey(name))
            {
                throw new ZeyoCutException($"drawing '{name}' not found on layer '{image.Id}'; drawings: {Ids.List(image.Drawings.Keys)}");
            }

            return Upsert(image.Drawing.Keyframes, entry.Time, k => k.Time, new DrawingKeyframe { Time = entry.Time, Value = name });
        }

        if (property == AnimatableProperties.Path)
        {
            throw PathByHand();
        }

        var number = ReadNumber(entry.Value, property);
        var (easing, bezier) = EasingOf(entry);
        if (property is "x" or "y" && layer.MotionPath is not null)
        {
            layer.MotionPath = null;
            warnings.Add(EffectEditor.PathRemovedWarning(layer));
        }

        var target = AnimatableProperties.GetNumeric(layer, property);
        return Upsert(target.Keyframes, entry.Time, k => k.Time, new Keyframe { Time = entry.Time, Value = number, Easing = easing, Bezier = bezier });
    }

    // An entry's easing, with the four numbers of a cubicBezier one; numbers alone mean cubicBezier.
    private static (Easing Easing, double[]? Bezier) EasingOf(KeyframeEntry entry)
    {
        var easing = string.IsNullOrWhiteSpace(entry.Easing)
            ? entry.Bezier is null ? Easing.Linear : Easing.CubicBezier
            : EnumNames.Parse<Easing>(entry.Easing, "easing");
        if (easing != Easing.CubicBezier)
        {
            return entry.Bezier is null
                ? (easing, null)
                : throw new ZeyoCutException($"bezier goes with easing cubicBezier, not {EnumNames.ToName(easing)}; leave easing out");
        }

        if (entry.Bezier is null)
        {
            throw new ZeyoCutException("easing cubicBezier needs bezier [x1, y1, x2, y2], such as [0.42, 0, 0.58, 1]");
        }

        var numbers = entry.Bezier.ToArray();
        return EasingFunctions.BezierProblem(numbers) is { } problem ? throw new ZeyoCutException(problem) : (easing, numbers);
    }

    private static void RemoveOne(Project project, KeyframeRef target)
    {
        if (target.LayerId == Camera.Id)
        {
            var name = AnimatableProperties.CameraCanonical(target.Property);
            RemoveAt(AnimatableProperties.GetNumeric(RequireCamera(project), target.Property).Keyframes, target.Time, k => k.Time, $"camera.{name}");
            return;
        }

        if (project.FindLayer(target.LayerId) is CutoutLayer cutout)
        {
            RemoveAt(CutoutOpacity(cutout, target.Property).Keyframes, target.Time, k => k.Time, $"{cutout.Id}.opacity");
            return;
        }

        if (project.FindLayer(target.LayerId) is RedactionLayer redaction)
        {
            var (track, name, box) = RedactionEditor.BoxValue(redaction, target.Property);
            if (box.Keyframes.Count == 1 && Math.Abs(box.Keyframes[0].Time - target.Time) < TimeTolerance)
            {
                throw new ZeyoCutException($"that is the last keyframe of {track}.{name}; clear_keyframes property \"{track}\" removes the whole box and goes back to the box worked out from the track");
            }

            RemoveAt(box.Keyframes, target.Time, k => k.Time, $"{redaction.Id}.{track}.{name}");
            return;
        }

        var layer = ProjectEditor.GetVisualLayer(project, target.LayerId);
        var property = CanonicalProperty(target.Property);
        if (property == AnimatableProperties.Drawing)
        {
            if (layer is not ImageLayer image)
            {
                throw new ZeyoCutException($"'drawing' is only for image layers; '{layer.Id}' is a {layer.TypeName} layer");
            }

            RemoveAt(image.Drawing.Keyframes, target.Time, k => k.Time, $"{layer.Id}.drawing");
            if (image.Drawing.Keyframes.Count == 0 && !image.Drawings.ContainsKey(image.Drawing.Value) && image.Drawings.Count > 0)
            {
                image.Drawing.Value = image.Drawings.Keys.First();
            }

            return;
        }

        if (property == AnimatableProperties.Path)
        {
            throw PathByHand();
        }

        var value = AnimatableProperties.GetNumeric(layer, property);
        RemoveAt(value.Keyframes, target.Time, k => k.Time, $"{layer.Id}.{property}");
    }

    // The keyframes of one property, a value's or a drawing track's, and its name for messages.
    private static (List<Keyframe>? Numbers, List<DrawingKeyframe>? Drawings, string Where) KeysOf(Project project, string layerId, string property)
    {
        if (layerId == Camera.Id)
        {
            var keys = AnimatableProperties.GetNumeric(RequireCamera(project), property).Keyframes;
            return (keys, null, $"camera.{AnimatableProperties.CameraCanonical(property)}");
        }

        if (project.FindLayer(layerId) is CutoutLayer cutout)
        {
            return (CutoutOpacity(cutout, property).Keyframes, null, $"{cutout.Id}.opacity");
        }

        if (project.FindLayer(layerId) is RedactionLayer redaction)
        {
            throw new ZeyoCutException($"'{redaction.Id}' is a redaction, whose boxes are in its footage's own seconds; set_keyframes changes them");
        }

        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        var name = CanonicalProperty(property);
        if (name == AnimatableProperties.Drawing)
        {
            return layer is ImageLayer image
                ? (null, image.Drawing.Keyframes, $"{layer.Id}.drawing")
                : throw new ZeyoCutException($"'drawing' is only for image layers; '{layer.Id}' is a {layer.TypeName} layer");
        }

        if (name == AnimatableProperties.Path)
        {
            throw PathByHand();
        }

        return (AnimatableProperties.GetNumeric(layer, name).Keyframes, null, $"{layer.Id}.{name}");
    }

    private static ZeyoCutException NoKeyframe(IEnumerable<double> times, double time, string where)
    {
        var list = times.ToList();
        var shown = list.Count == 0 ? "(none)" : string.Join(", ", list.Select(t => t.ToString("0.######", CultureInfo.InvariantCulture)));
        return new ZeyoCutException($"no keyframe at {time}s on {where}; keyframe times: {shown}");
    }

    // One problem as it is, several as a list.
    private static void Refuse(IReadOnlyList<string> errors)
    {
        if (errors.Count == 1)
        {
            throw new ZeyoCutException(errors[0]);
        }

        if (errors.Count > 1)
        {
            throw new ZeyoCutException($"{errors.Count} problems, so nothing was changed:\n- " + string.Join("\n- ", errors));
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // A cutout's only animated setting is its opacity; the rest follows its sources.
    private static AnimatedValue CutoutOpacity(CutoutLayer cutout, string? property) =>
        string.Equals(property, "opacity", StringComparison.OrdinalIgnoreCase) ? cutout.Opacity : throw SubjectEditor.Follows(cutout);

    private static ZeyoCutException PathByHand() =>
        new("'path' keyframes come from set_motion_path (each point's time and easing); call it again to change the timing, or with remove=true to take the path off");

    private static Camera RequireCamera(Project project) =>
        project.Camera ?? throw new ZeyoCutException("the project has no camera yet; set_camera, a camera keyframe or camera_shake adds one");

    private static string CanonicalProperty(string? property) =>
        AnimatableProperties.Canonical(property)
        ?? throw new ZeyoCutException($"unknown property '{property}'; animatable properties: {AnimatableProperties.Describe()}");

    private static bool Upsert<T>(List<T> keys, double time, Func<T, double> timeOf, T item)
    {
        var existing = keys.FindIndex(k => Math.Abs(timeOf(k) - time) < TimeTolerance);
        if (existing >= 0)
        {
            keys[existing] = item;
            return true;
        }

        var insertAt = keys.FindIndex(k => timeOf(k) > time);
        if (insertAt < 0)
        {
            keys.Add(item);
        }
        else
        {
            keys.Insert(insertAt, item);
        }

        return false;
    }

    private static void RemoveAt<T>(List<T> keys, double time, Func<T, double> timeOf, string where)
    {
        var index = keys.FindIndex(k => Math.Abs(timeOf(k) - time) < TimeTolerance);
        if (index < 0)
        {
            throw NoKeyframe(keys.Select(timeOf), time, where);
        }

        keys.RemoveAt(index);
    }

    private static double ReadNumber(JsonElement value, string property)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        throw new ZeyoCutException($"value for '{property}' must be a number (got {Describe(value)})");
    }

    private static string ReadString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ZeyoCutException($"value for 'drawing' must be a drawing name string (got {Describe(value)})");

    private static string Describe(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? "nothing" : value.GetRawText();
}
