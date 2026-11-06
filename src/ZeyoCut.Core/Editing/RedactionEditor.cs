// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record RedactionSettings(RedactionStyle Style, double Strength, double PaddingPercent, double Feather, int HoldFrames, string Color);

// apply_redaction's result: the redaction that hides the tracks now (made, or the one that already
// hid exactly those tracks), and notes.
public sealed record RedactionChange(RedactionLayer Layer, bool Created, IReadOnlyList<string> Notes);

// A box hidden on one frame, in source pixels. Kind is where the fail-safe got it; null for a box
// from keyframes.
public sealed record RedactionArea(string Track, double X, double Y, double Width, double Height, CoverKind? Kind);

// apply_redaction, tracks_to_keyframes and the boxes the renderer hides on a frame.
public static class RedactionEditor
{
    public const string IdPrefix = "redaction";
    public const double MaxPadding = 100;
    public const double MaxFeather = 100;
    public const int MaxHold = 300;

    // Pixelate blocks and blur radii are never under this many source pixels.
    public const double MinBlock = 4;

    // tracks_to_keyframes keeps the keyframes needed to stay within KeyTolerance px of the fail-safe
    // box on every frame, and grows the box by KeyMargin px on each side, so it always contains it.
    public const double KeyTolerance = 0.2;
    public const double KeyMargin = 0.5;

    public static readonly IReadOnlyList<string> OwnFields = ["name", "visible", "style", "strength", "paddingPercent", "feather", "holdFrames", "color"];

    public static readonly IReadOnlyList<string> BoxProperties = ["x", "y", "width", "height"];

    public static ZeyoCutException Follows(RedactionLayer layer) => new(
        $"'{layer.Id}' is a redaction of {Ids.List(layer.Tracks)}: it isn't drawn or placed on the timeline itself, it hides those tracks' boxes in every clip that plays their footage, so make this change on the clips (its own settings are {string.Join(", ", OwnFields)}; apply_redaction changes its tracks and tracks_to_keyframes its boxes)");

    // Blocks across the box: 20 at strength 0 down to 4 at 100. The pixelate block (and the blur
    // radius) is the box's long side over that, so a small box is as unreadable as a big one.
    public static double BlockSize(double width, double height, double strength) =>
        Math.Max(MinBlock, Math.Max(width, height) / BlocksAcross(strength));

    public static double BlocksAcross(double strength) => 20 + (4 - 20) * Math.Clamp(strength, 0, 100) / 100;

    // What is wrong with a redaction's own settings (empty when nothing is).
    public static List<string> Problems(RedactionLayer layer)
    {
        var problems = new List<string>();
        if (layer.Tracks.Count == 0)
        {
            problems.Add("a redaction needs at least one track");
        }

        if (!double.IsFinite(layer.Strength) || layer.Strength < 0 || layer.Strength > 100)
        {
            problems.Add($"strength must be between 0 and 100 (got {Num(layer.Strength)})");
        }

        if (!double.IsFinite(layer.PaddingPercent) || layer.PaddingPercent < 0 || layer.PaddingPercent > MaxPadding)
        {
            problems.Add($"paddingPercent must be between 0 and {Num(MaxPadding)} (got {Num(layer.PaddingPercent)})");
        }

        if (!double.IsFinite(layer.Feather) || layer.Feather < 0 || layer.Feather > MaxFeather)
        {
            problems.Add($"feather must be between 0 and {Num(MaxFeather)} source pixels (got {Num(layer.Feather)})");
        }

        if (layer.HoldFrames < 0 || layer.HoldFrames > MaxHold)
        {
            problems.Add($"holdFrames must be between 0 and {MaxHold} (got {layer.HoldFrames})");
        }

        if (!RgbaColor.TryParse(layer.Color, out _))
        {
            problems.Add($"color '{layer.Color}' is not a color; use #RRGGBB or #RRGGBBAA");
        }

        return problems;
    }

    // The tracks by id; ["all"] is every track of the project.
    public static List<Track> Tracks(Project project, IReadOnlyList<string>? trackIds)
    {
        var ids = (trackIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();
        if (ids.Count == 0)
        {
            throw new ZeyoCutException($"trackIds is empty; pass track ids (list_layers shows a clip's tracks as tracks=) or [\"all\"]; tracks: {Ids.List(project.Tracks.Select(t => t.Id))}");
        }

        if (ids.Count == 1 && string.Equals(ids[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            return project.Tracks.Count > 0
                ? project.Tracks.ToList()
                : throw new ZeyoCutException("the project has no tracks yet; track_object makes one");
        }

        return ids.Distinct(StringComparer.Ordinal)
            .Select(id => project.FindTrack(id) ?? throw new ZeyoCutException($"track '{id}' not found; tracks: {Ids.List(project.Tracks.Select(t => t.Id))}"))
            .ToList();
    }

    // apply_redaction: the tracks are hidden by one redaction with these settings. A redaction that
    // hides exactly these tracks is changed; otherwise a new one is made and the tracks leave the
    // redactions that hid them before (taking their keyframed boxes along), and a redaction left with
    // no tracks goes.
    public static RedactionChange Apply(Project project, IReadOnlyList<string>? trackIds, RedactionSettings settings)
    {
        var ids = Tracks(project, trackIds).Select(t => t.Id).ToList();
        var wanted = new RedactionLayer
        {
            Tracks = ids,
            Style = settings.Style,
            Strength = settings.Strength,
            PaddingPercent = settings.PaddingPercent,
            Feather = settings.Feather,
            HoldFrames = settings.HoldFrames,
            Color = settings.Color,
        };
        if (Problems(wanted).FirstOrDefault() is { } problem)
        {
            throw new ZeyoCutException(problem);
        }

        var notes = new List<string>();
        var redactions = project.Layers.OfType<RedactionLayer>().ToList();
        var layer = redactions.FirstOrDefault(r => r.Tracks.Count == ids.Count && r.Tracks.All(ids.Contains));
        var created = layer is null;
        if (layer is null)
        {
            var boxes = new Dictionary<string, RedactionBox>(StringComparer.Ordinal);
            foreach (var other in redactions)
            {
                var moved = other.Tracks.Where(ids.Contains).ToList();
                if (moved.Count == 0)
                {
                    continue;
                }

                other.Tracks.RemoveAll(moved.Contains);
                foreach (var id in moved)
                {
                    if (other.Boxes is not null && other.Boxes.Remove(id, out var box))
                    {
                        boxes[id] = box;
                    }
                }

                if (other.Boxes is { Count: 0 })
                {
                    other.Boxes = null;
                }

                if (other.Tracks.Count == 0)
                {
                    project.Layers.Remove(other);
                    notes.Add($"removed '{other.Id}', which hid only {Ids.List(moved)}");
                }
                else
                {
                    notes.Add($"took {Ids.List(moved)} from '{other.Id}', which still hides {Ids.List(other.Tracks)}");
                }
            }

            var newId = NewId(project);
            layer = new RedactionLayer { Id = newId, Name = newId, Tracks = ids, Boxes = boxes.Count > 0 ? boxes : null };
            project.Layers.Add(layer);
            if (boxes.Count > 0)
            {
                notes.Add($"{Ids.List(boxes.Keys)} {(boxes.Count == 1 ? "keeps its" : "keep their")} keyframed box from tracks_to_keyframes, which paddingPercent and holdFrames don't change; clear_keyframes property \"<track>\" goes back to the box worked out from the track");
            }
        }
        else if (!layer.Visible)
        {
            notes.Add($"'{layer.Id}' was hidden (visible false); it hides its tracks again");
        }

        layer.Visible = true;
        layer.Style = wanted.Style;
        layer.Strength = wanted.Strength;
        layer.PaddingPercent = wanted.PaddingPercent;
        layer.Feather = wanted.Feather;
        layer.HoldFrames = wanted.HoldFrames;
        layer.Color = wanted.Color;
        return new RedactionChange(layer, created, notes);
    }

    // After tracks were removed: redactions drop them (and their boxes), and a redaction left with no
    // tracks goes. Returns the ids of the redactions removed.
    public static List<string> DropMissingTracks(Project project)
    {
        var removed = new List<string>();
        foreach (var layer in project.Layers.OfType<RedactionLayer>().ToList())
        {
            layer.Tracks.RemoveAll(id => project.FindTrack(id) is null);
            if (layer.Boxes is { } boxes)
            {
                foreach (var id in boxes.Keys.Where(id => !layer.Tracks.Contains(id)).ToList())
                {
                    boxes.Remove(id);
                }

                if (boxes.Count == 0)
                {
                    layer.Boxes = null;
                }
            }

            if (layer.Tracks.Count == 0)
            {
                project.Layers.Remove(layer);
                removed.Add(layer.Id);
            }
        }

        return removed;
    }

    // The redaction that hides a track, if any.
    public static RedactionLayer? Of(Project project, string trackId) =>
        project.Layers.OfType<RedactionLayer>().FirstOrDefault(r => r.Tracks.Contains(trackId));

    // The clips a redaction of this footage works on: every clip that plays it.
    public static List<VideoLayer> Clips(Project project, string assetId) =>
        project.Layers.OfType<VideoLayer>().Where(v => v.Asset == assetId).ToList();

    // The boxes to hide on a source frame of this footage, from every redaction that is visible: a
    // keyframed box, or the track's fail-safe box ('cover' gives a track's RedactionCover with the
    // redaction's padding and holdFrames).
    public static List<(RedactionLayer Layer, RedactionArea Area)> Areas(Project project, Asset asset, int frame, Func<Track, RedactionLayer, RedactionCover> cover)
    {
        var areas = new List<(RedactionLayer, RedactionArea)>();
        foreach (var layer in project.Layers.OfType<RedactionLayer>().Where(l => l.Visible))
        {
            foreach (var id in layer.Tracks)
            {
                if (project.FindTrack(id) is not { } track || track.Asset != asset.Id)
                {
                    continue;
                }

                if (layer.Boxes is not null && layer.Boxes.TryGetValue(id, out var keyed))
                {
                    if (KeyedBox(keyed, asset, frame) is { } box)
                    {
                        areas.Add((layer, new RedactionArea(id, box.X, box.Y, box.Width, box.Height, null)));
                    }
                }
                else if (cover(track, layer).At(frame) is { } box)
                {
                    areas.Add((layer, new RedactionArea(id, box.X, box.Y, box.Width, box.Height, box.Kind)));
                }
            }
        }

        return areas;
    }

    // A keyframed box on a source frame, or null outside its first and last keyframe.
    public static (double X, double Y, double Width, double Height)? KeyedBox(RedactionBox box, Asset asset, int frame)
    {
        var values = new[] { box.X, box.Y, box.Width, box.Height };
        if (values.All(v => v.Keyframes.Count == 0))
        {
            return null;
        }

        var from = values.Where(v => v.Keyframes.Count > 0).Min(v => v.Keyframes[0].Time);
        var to = values.Where(v => v.Keyframes.Count > 0).Max(v => v.Keyframes[^1].Time);
        var time = VideoTiming.TimeOfFrame(asset, frame);
        if (time < from - KeyframeEditor.TimeTolerance || time > to + KeyframeEditor.TimeTolerance)
        {
            return null;
        }

        var width = Interpolator.Evaluate(box.Width, time);
        var height = Interpolator.Evaluate(box.Height, time);
        return width > 0 && height > 0 ? (Interpolator.Evaluate(box.X, time), Interpolator.Evaluate(box.Y, time), width, height) : null;
    }

    // tracks_to_keyframes: the fail-safe box as linear keyframes at the footage's frame times, grown
    // by KeyMargin and thinned to the ones needed to stay within KeyTolerance of every frame.
    public static RedactionBox ToKeyframes(RedactionCover cover, Asset asset)
    {
        var boxes = cover.Boxes;
        if (boxes.Count == 0)
        {
            throw new ZeyoCutException("the track has no frames to make keyframes from");
        }

        var times = boxes.Select(b => Math.Round(VideoTiming.TimeOfFrame(asset, b.Frame), 6)).ToArray();
        return new RedactionBox
        {
            X = Thin(times, boxes.Select(b => Math.Round(b.X - KeyMargin, 2)).ToArray()),
            Y = Thin(times, boxes.Select(b => Math.Round(b.Y - KeyMargin, 2)).ToArray()),
            Width = Thin(times, boxes.Select(b => Math.Round(b.Width + 2 * KeyMargin, 2)).ToArray()),
            Height = Thin(times, boxes.Select(b => Math.Round(b.Height + 2 * KeyMargin, 2)).ToArray()),
        };
    }

    // set_keyframes and remove_keyframes on a redaction: one of its keyframed boxes' properties,
    // <track>.x, .y, .width or .height.
    public static (string Track, string Name, AnimatedValue Value) BoxValue(RedactionLayer layer, string? property)
    {
        var text = property?.Trim() ?? "";
        var dot = text.LastIndexOf('.');
        var track = dot > 0 ? text[..dot] : text;
        var name = dot > 0 ? BoxProperties.FirstOrDefault(p => string.Equals(p, text[(dot + 1)..], StringComparison.OrdinalIgnoreCase)) : null;
        if (name is null || !layer.Tracks.Contains(track))
        {
            throw new ZeyoCutException(
                $"'{layer.Id}' animates only its tracks' boxes: <track>.x, .y, .width or .height (in the footage's own seconds and pixels) for {Ids.List(layer.Tracks)}; got '{property}'");
        }

        if (layer.Boxes is null || !layer.Boxes.TryGetValue(track, out var box))
        {
            throw new ZeyoCutException($"{track} has no keyframed box on '{layer.Id}' yet; tracks_to_keyframes trackId=\"{track}\" makes one from the track");
        }

        return (track, name, name switch
        {
            "x" => box.X,
            "y" => box.Y,
            "width" => box.Width,
            _ => box.Height,
        });
    }

    // clear_keyframes on a redaction: every keyframed box (property null) or one track's ("face_1");
    // a box is only cleared whole. Returns how many keyframes went.
    public static int ClearBoxes(RedactionLayer layer, string? property)
    {
        static int Count(RedactionBox box) => box.X.Keyframes.Count + box.Y.Keyframes.Count + box.Width.Keyframes.Count + box.Height.Keyframes.Count;
        if (property is null)
        {
            var all = layer.Boxes?.Values.Sum(Count) ?? 0;
            layer.Boxes = null;
            return all;
        }

        var text = property.Trim();
        var dot = text.LastIndexOf('.');
        if (dot > 0 && layer.Tracks.Contains(text[..dot]))
        {
            throw new ZeyoCutException($"a redaction's box is cleared whole: clear_keyframes property \"{text[..dot]}\" goes back to the box worked out from the track");
        }

        if (!layer.Tracks.Contains(text))
        {
            throw new ZeyoCutException($"'{layer.Id}' hides {Ids.List(layer.Tracks)}; clear_keyframes takes one of them as property (its keyframed box), or none for all");
        }

        if (layer.Boxes is null || !layer.Boxes.Remove(text, out var box))
        {
            return 0;
        }

        if (layer.Boxes.Count == 0)
        {
            layer.Boxes = null;
        }

        return Count(box);
    }

    public static string Describe(RedactionLayer layer)
    {
        var style = layer.Style switch
        {
            RedactionStyle.Solid => $"solid {layer.Color}",
            _ => $"{EnumNames.ToName(layer.Style)} strength={Num(layer.Strength)}",
        };
        return $"{style} padding={Num(layer.PaddingPercent)}% feather={Num(layer.Feather)} hold={layer.HoldFrames}";
    }

    private static string NewId(Project project)
    {
        for (var n = 1; ; n++)
        {
            var candidate = $"{IdPrefix}_{n}";
            if (candidate != Camera.Id && project.FindLayer(candidate) is null)
            {
                return candidate;
            }
        }
    }

    // Linear keyframes through the values, kept only where needed to stay within 'tolerance' of every
    // one; attach_to_track thins its keyframes with it too.
    internal static AnimatedValue Thin(double[] times, double[] values, double tolerance = KeyTolerance)
    {
        var keys = new List<Keyframe> { new() { Time = times[0], Value = values[0] } };
        var start = 0;
        while (start < values.Length - 1)
        {
            var end = start + 1;
            while (end + 1 < values.Length && Fits(times, values, start, end + 1, tolerance))
            {
                end++;
            }

            keys.Add(new Keyframe { Time = times[end], Value = values[end] });
            start = end;
        }

        return new AnimatedValue(values[0]) { Keyframes = keys };
    }

    // Whether a straight line from 'from' to 'to' passes within 'tolerance' of every value between.
    private static bool Fits(double[] times, double[] values, int from, int to, double tolerance)
    {
        for (var i = from + 1; i < to; i++)
        {
            var u = (times[i] - times[from]) / (times[to] - times[from]);
            if (Math.Abs(values[from] + (values[to] - values[from]) * u - values[i]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
