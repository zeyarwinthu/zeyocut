// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// A run of frames whose confidence is under TrackEditor.DriftBelow (lost: how many of them the tracker
// lost outright).
public sealed record DriftSpan(int First, int Last, double Lowest, int Lost);

// One way to track: from the anchor at From to To (either way), both included.
public sealed record TrackRun(int From, int To)
{
    public int Frames => Math.Abs(To - From);
}

// What track_object or reanchor_track tracks: the track as it is saved once tracked (TrackRunner
// gives it its file), its footage, the clip the box was drawn on and the time it was drawn at, the box
// tracking starts from and the runs from it. Replacing is the file of the version a correction
// replaces; null for a new track.
public sealed record TrackPlan(Track Track, Asset Asset, string Clip, double Shown, TrackBox Anchor, IReadOnlyList<TrackRun> Runs, string? Replacing)
{
    public int Frames => Runs.Sum(r => r.Frames);
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

    // Saves a track track_object made, or the one reanchor_track made in place of the version whose
    // file was 'replacing'. Refused when the project changed meanwhile so that it would be wrong.
    public static void SaveTrack(Project project, Track track, string? replacing)
    {
        if (replacing is not null)
        {
            var index = project.Tracks.FindIndex(t => t.Id == track.Id);
            if (index < 0 || project.Tracks[index].File != replacing)
            {
                throw new ZeyoCutException($"track '{track.Id}' changed or was removed while it was being tracked, so the correction wasn't saved; run reanchor_track again");
            }

            project.Tracks[index] = track;
        }
        else
        {
            if (project.FindTrack(track.Id) is not null)
            {
                throw new ZeyoCutException($"a track '{track.Id}' appeared while this one was being tracked, so it wasn't saved; run track_object again");
            }

            project.Tracks.Add(track);
        }
    }

    // Adds the tracks detect_and_track found in one asset's footage ('plural': faces, plates), all
    // or none.
    public static void AddDetected(Project project, string assetId, IReadOnlyList<Track> tracks, string plural)
    {
        if (project.FindAsset(assetId) is null)
        {
            throw new ZeyoCutException($"asset '{assetId}' was removed while its {plural} were being found, so no track was added; run detect_and_track again");
        }

        foreach (var track in tracks)
        {
            if (project.FindTrack(track.Id) is not null)
            {
                throw new ZeyoCutException($"a track '{track.Id}' appeared while the {plural} were being found, so no track was added; run detect_and_track again");
            }

            project.Tracks.Add(track);
        }
    }

    // track_object: a new track from a box drawn on the canvas at 'time' on a clip (x, y, width,
    // height in canvas pixels), tracked from that frame to the end of the clip's trimmed range, its
    // start, or both. Its id is the one asked for or track_N, never one in use or reserved.
    public static TrackPlan Plan(Project project, string? layerId, double time, double x, double y, double width, double height, TrackDirection direction, TrackMethod method,
        string? id, Func<string, bool> reserved)
    {
        if (method is TrackMethod.Faces or TrackMethod.Plates)
        {
            throw new ZeyoCutException($"unknown method '{EnumNames.ToName(method)}'; valid values: csrt, template, opticalFlow (detect_and_track finds faces and plates)");
        }

        var (layer, footage) = Clip(project, layerId);
        var (frame, at) = SourceFrameAt(project, layer, footage, time);
        var anchor = SourceBox(project, layer, footage, at, frame, x, y, width, height);
        var (first, last) = Frames(layer, footage);
        var track = new Track
        {
            Id = NewId(project, id, reserved),
            Asset = footage.Id,
            Layer = layer.Id,
            Method = method,
            Direction = direction,
            FirstFrame = direction == TrackDirection.Forward ? frame : first,
            LastFrame = direction == TrackDirection.Backward ? frame : last,
            Origin = frame,
            Anchors = [anchor],
        };
        return new TrackPlan(track, footage, layer.Id, at, anchor, Runs(direction, track.Origin, track.FirstFrame, track.LastFrame), null);
    }

    // reanchor_track: the track with the box at 'time' set by hand (canvas pixels, mapped through the
    // clip it was made on, or layerId, another clip of its footage), tracked again from there away
    // from its origin up to the next box set by hand (Rerun). Track is a copy; the project's is left
    // as it is.
    public static TrackPlan PlanCorrection(Project project, string trackId, double time, double x, double y, double width, double height, string? layerId)
    {
        var found = project.FindTrack(trackId)
            ?? throw new ZeyoCutException($"track '{trackId}' not found; tracks: {Ids.List(project.Tracks.Select(t => t.Id))}");
        if (found.Method is TrackMethod.Faces or TrackMethod.Plates)
        {
            throw new ZeyoCutException(
                $"track '{trackId}' was found on every frame by detect_and_track, not tracked from a box, so reanchor_track can't correct it; where it missed the face or plate, track_object makes a track from a box there, which apply_redaction can hide too");
        }

        if (layerId is null && (project.FindLayer(found.Layer) is not VideoLayer original || original.Asset != found.Asset))
        {
            var clips = project.Layers.OfType<VideoLayer>().Where(v => v.Asset == found.Asset).Select(v => v.Id);
            throw new ZeyoCutException($"clip '{found.Layer}', which track '{trackId}' was made on, is gone or plays other footage; pass layerId, a clip of '{found.Asset}': {Ids.List(clips)}");
        }

        var (layer, footage) = Clip(project, layerId ?? found.Layer);
        if (layer.Asset != found.Asset)
        {
            throw new ZeyoCutException($"clip '{layer.Id}' plays '{layer.Asset}', but track '{trackId}' follows '{found.Asset}'");
        }

        var (frame, at) = SourceFrameAt(project, layer, footage, time);
        if (frame < found.FirstFrame || frame > found.LastFrame)
        {
            throw new ZeyoCutException(
                $"{Num(at)}s on '{layer.Id}' shows frame {frame} of '{footage.Id}', outside track '{trackId}' (frames {found.FirstFrame}–{found.LastFrame}); reanchor_track corrects frames inside a track, and track_object starts a new one");
        }

        var anchor = SourceBox(project, layer, footage, at, frame, x, y, width, height);
        var track = ProjectJson.Copy(found);
        var runs = Rerun(track, anchor.Frame);
        var replacing = track.File;
        track.Anchors = WithAnchor(track.Anchors, anchor);
        return new TrackPlan(track, footage, layer.Id, at, anchor, runs, replacing);
    }

    // The timeline time a clip shows a source frame of its footage at (its trim in for a frame
    // before it).
    public static double TimelineTime(VideoLayer clip, Asset asset, int frame) =>
        VideoTiming.TimelineTime(clip, Math.Max(clip.TrimIn, VideoTiming.TimeOfFrame(asset, frame)));

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
