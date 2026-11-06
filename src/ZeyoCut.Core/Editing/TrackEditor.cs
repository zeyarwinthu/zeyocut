// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// A run of frames whose confidence is under TrackEditor.DriftBelow (lost: how many of them the tracker
// lost outright).
public sealed record DriftSpan(int First, int Last, double Lowest, int Lost);

// One way to track: from the anchor at From to To (either way), both included.
public sealed record TrackRun(int From, int To)
{
    public int Frames => Math.Abs(To - From);
}

// track_object and reanchor_track: which frames and source pixels a box drawn on the canvas means,
// and which frames tracking covers.
public static class TrackEditor
{
    public const string DefaultPrefix = "track";

    // Frames under this confidence are possible drift.
    public const double DriftBelow = 0.5;

    // Drift spans this many good frames apart or less are one span.
    public const int DriftJoin = 2;

    // The box has to be at least this big in source pixels, inside the clip's picture.
    public const double MinBoxSide = 4;

    public static (VideoLayer Layer, Asset Asset) Clip(Project project, string? layerId)
    {
        var layer = ProjectEditor.GetLayer(project, layerId);
        if (layer is not VideoLayer video)
        {
            throw new ZeyoCutException($"tracking works on video layers; '{layer.Id}' is a {layer.TypeName} layer");
        }

        var asset = project.FindAsset(video.Asset)
            ?? throw new ZeyoCutException($"layer '{video.Id}': asset '{video.Asset}' not found");
        if (asset.Width is not > 0 || asset.Height is not > 0 || asset.FrameCount is not > 0)
        {
            throw new ZeyoCutException($"video asset '{asset.Id}' has no frame size or frame count; import it again with import_video");
        }

        return (video, asset);
    }

    // The source frames a clip shows: from trimIn to just before trimOut.
    public static (int First, int Last) Frames(VideoLayer layer, Asset asset)
    {
        var first = VideoTiming.FrameAt(asset, layer.TrimIn);
        return (first, Math.Max(first, VideoTiming.FrameAt(asset, layer.TrimOut - 1e-6)));
    }

    // The frame render_frame shows at 'time' (the nearest project frame), its time, and the source
    // frame the clip shows in it.
    public static (int Frame, double Time) SourceFrameAt(Project project, VideoLayer layer, Asset asset, double time)
    {
        if (!double.IsFinite(time) || time < 0)
        {
            throw new ZeyoCutException($"time must be 0 or more seconds (got {Num(time)})");
        }

        var shown = FrameMath.TimeOf(project, FrameMath.NearestFrame(project, time));
        if (!SceneEvaluator.IsActive(layer, shown, project.Duration))
        {
            throw new ZeyoCutException($"clip '{layer.Id}' isn't on the timeline at {Num(shown)}s (it plays {Num(layer.Start)}–{Num(layer.TimelineEnd)}s)");
        }

        var (first, last) = Frames(layer, asset);
        return (Math.Clamp(VideoTiming.FrameAt(asset, VideoTiming.SourceTime(layer, shown)), first, last), shown);
    }

    // A box drawn on the canvas at 'time' in the clip's source pixels: the bounding box of its corners
    // through the clip's placement, parents, the camera and the crop, cut to the part of the source the
    // clip shows.
    public static TrackBox SourceBox(Project project, VideoLayer layer, Asset asset, double time, int frame, double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            throw new ZeyoCutException($"box needs finite x and y and a width and height above 0 (got {Num(x)},{Num(y)} {Num(width)}x{Num(height)})");
        }

        var placed = SceneEvaluator.Evaluate(project, time).First(e => e.Cutout is null && e.Layer.Id == layer.Id);
        var inverse = placed.World.Inverse()
            ?? throw new ZeyoCutException($"clip '{layer.Id}' is scaled to nothing at {Num(time)}s, so a box on the canvas can't be mapped into it");
        var crop = VideoTiming.Crop(layer, asset, time);
        var corners = new[] { (x, y), (x + width, y), (x, y + height), (x + width, y + height) }
            .Select(c => inverse.Apply(c.Item1, c.Item2))
            .Select(c => (X: c.X + crop.X, Y: c.Y + crop.Y))
            .ToList();
        var minX = corners.Min(c => c.X);
        var maxX = corners.Max(c => c.X);
        var minY = corners.Min(c => c.Y);
        var maxY = corners.Max(c => c.Y);
        var left = Math.Max(minX, crop.X);
        var right = Math.Min(maxX, crop.X + crop.Width);
        var top = Math.Max(minY, crop.Y);
        var bottom = Math.Min(maxY, crop.Y + crop.Height);
        var inside = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        if (right - left < MinBoxSide || bottom - top < MinBoxSide || inside < 0.25 * (maxX - minX) * (maxY - minY))
        {
            throw new ZeyoCutException(
                $"the box {Num(x)},{Num(y)} {Num(width)}x{Num(height)} is mostly outside clip '{layer.Id}''s picture at {Num(time)}s, or under {Num(MinBoxSide)} source pixels; check where it is with render_frame");
        }

        return new TrackBox { Frame = frame, X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    // An id for a new track: the one asked for, or <prefix>_N, never one in use or reserved.
    public static string NewId(Project project, string? id, Func<string, bool> reserved, string prefix = DefaultPrefix)
    {
        if (id is not null)
        {
            var wanted = id.Trim();
            Ids.EnsureValid(wanted, "track");
            if (project.FindTrack(wanted) is not null || reserved(wanted))
            {
                throw new ZeyoCutException($"track '{wanted}' already exists; choose another id or correct it with reanchor_track");
            }

            return wanted;
        }

        for (var n = 1; ; n++)
        {
            var candidate = $"{prefix}_{n}";
            if (project.FindTrack(candidate) is null && !reserved(candidate))
            {
                return candidate;
            }
        }
    }

    // track_object's runs from the anchor, to the clip's last frame (forward) or its first (backward).
    public static List<TrackRun> Runs(TrackDirection direction, int anchor, int first, int last)
    {
        var runs = new List<TrackRun>();
        if (direction != TrackDirection.Forward)
        {
            runs.Add(new TrackRun(anchor, first));
        }

        if (direction != TrackDirection.Backward)
        {
            runs.Add(new TrackRun(anchor, last));
        }

        return runs;
    }

    // reanchor_track at 'frame' tracks again away from the origin, up to the next anchor on that side
    // (or the end of the track); at the origin itself, both ways the track went.
    public static List<TrackRun> Rerun(Track track, int frame)
    {
        var runs = new List<TrackRun>();
        if (frame < track.Origin || (frame == track.Origin && track.Direction != TrackDirection.Forward))
        {
            var stop = track.Anchors.Where(a => a.Frame < frame).Select(a => a.Frame + 1).DefaultIfEmpty(track.FirstFrame).Max();
            runs.Add(new TrackRun(frame, Math.Min(frame, stop)));
        }

        if (frame > track.Origin || (frame == track.Origin && track.Direction != TrackDirection.Backward))
        {
            var stop = track.Anchors.Where(a => a.Frame > frame).Select(a => a.Frame - 1).DefaultIfEmpty(track.LastFrame).Min();
            runs.Add(new TrackRun(frame, Math.Max(frame, stop)));
        }

        return runs;
    }

    // The anchors after setting 'box': it replaces an anchor at the same frame.
    public static List<TrackBox> WithAnchor(IEnumerable<TrackBox> anchors, TrackBox box) =>
        anchors.Where(a => a.Frame != box.Frame).Append(box).OrderBy(a => a.Frame).ToList();

    public static List<DriftSpan> Drift(IReadOnlyList<TrackedFrame> frames)
    {
        var spans = new List<DriftSpan>();
        var low = frames.Where(f => f.Confidence < DriftBelow).OrderBy(f => f.Frame).ToList();
        var i = 0;
        while (i < low.Count)
        {
            var j = i;
            while (j + 1 < low.Count && low[j + 1].Frame - low[j].Frame <= DriftJoin + 1)
            {
                j++;
            }

            var span = low.GetRange(i, j - i + 1);
            spans.Add(new DriftSpan(span[0].Frame, span[^1].Frame, span.Min(f => f.Confidence), span.Count(f => f.Lost)));
            i = j + 1;
        }

        return spans;
    }

    // The clip that shows a source frame of the track's footage: the one it was made on if it still
    // does, else the first that does.
    public static VideoLayer? ClipShowing(Project project, Track track, int frame)
    {
        var clips = project.Layers.OfType<VideoLayer>().Where(v => v.Asset == track.Asset).ToList();
        var asset = project.FindAsset(track.Asset);
        if (asset is null)
        {
            return null;
        }

        bool Shows(VideoLayer clip)
        {
            var (first, last) = Frames(clip, asset);
            return frame >= first && frame <= last;
        }

        return clips.FirstOrDefault(c => c.Id == track.Layer && Shows(c)) ?? clips.FirstOrDefault(Shows);
    }

    // The ids of the tracks a clip shows some frames of.
    public static List<string> TracksOf(Project project, VideoLayer clip)
    {
        if (project.FindAsset(clip.Asset) is not { } asset || project.Tracks.Count == 0)
        {
            return [];
        }

        var (first, last) = Frames(clip, asset);
        return project.Tracks.Where(t => t.Asset == clip.Asset && t.FirstFrame <= last && t.LastFrame >= first).Select(t => t.Id).ToList();
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
