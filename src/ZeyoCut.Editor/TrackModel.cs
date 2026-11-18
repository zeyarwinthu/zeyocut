// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// A span of a track on the timeline, in seconds: tracked, or unsure where the tracker lost the
// object or its confidence fell under TrackEditor.DriftBelow (TrackEditor.Drift).
public sealed record TrackSpan(double From, double To, bool Unsure);

// A part of a tracked object's bar: From and To are shares of the track's time on the clip.
public sealed record TrackPart(double From, double To, bool Unsure);

// A tracked object in the Track & blur view's list: its title and color, how it's hidden (Hide:
// Blur, Pixelate, Solid or Not hidden), its time on the clip, its bar, and the first span the
// tracker lost it or wasn't sure, with the frame Fix goes to. First is the frame its time on the
// clip starts at.
public sealed record TrackCard(string Id, string Title, string Color, string Hide, string Span, IReadOnlyList<TrackPart> Parts, string? Problem, int? ProblemFrame,
    int? First, bool Selected);

// A track's box on the frame at the playhead: its corners on the canvas, its label, and whether it
// can be moved (a track detect_and_track found can't be corrected).
public sealed record TrackShape(string Id, string Color, IReadOnlyList<Point> Corners, string Label, bool Correctable)
{
    public Rect Bounds
    {
        get
        {
            var left = Corners.Min(c => c.X);
            var top = Corners.Min(c => c.Y);
            return new Rect(left, top, Corners.Max(c => c.X) - left, Corners.Max(c => c.Y) - top);
        }
    }
}

// A track's row on the tracking timeline: its spans and the times of the boxes set by hand, in
// timeline seconds.
public sealed record TrackRow(string Id, string Title, string Color, IReadOnlyList<TrackSpan> Spans, IReadOnlyList<double> Anchors);

// The Track & blur view: the tracks of the clip at the playhead, as cards, boxes on the frame and
// rows of the tracking timeline, from their track files (the boxes, their confidence and the frames
// lost).
public static class TrackModel
{
    // A color for each track, in the project's order.
    public static readonly IReadOnlyList<string> Colors = ["#5BE0C8", "#F27AD0", "#F0A94B", "#7B93FF", "#8EE0B0", "#FF9B7A"];

    // The motion trail shows where the selected box was on this many frames before the playhead.
    public const int TrailFrames = 12;

    public static string ColorOf(Project project, string trackId) =>
        Colors[Math.Max(0, project.Tracks.FindIndex(t => t.Id == trackId)) % Colors.Count];

    public static string Kind(Track track) => track.Method switch
    {
        TrackMethod.Faces => "Face",
        TrackMethod.Plates => "Plate",
        _ => "Object",
    };

    public static string Title(Track track) => $"{track.Id} · {Kind(track)}";

    // reanchor_track corrects a track drawn as a box; one detect_and_track found on every frame can't
    // be.
    public static bool Correctable(Track track) => track.Method is not (TrackMethod.Faces or TrackMethod.Plates);

    // The clip the view works on: the selected layer when it's a video clip on the timeline at
    // 'time', else the top video clip there, else the one it worked on before ('last') while that's
    // still a clip.
    public static VideoLayer? Clip(Project project, string? selected, double time, string? last)
    {
        bool Usable(VideoLayer clip) => project.FindAsset(clip.Asset) is { Width: > 0, Height: > 0, FrameCount: > 0 };
        bool Showing(VideoLayer clip) => Usable(clip) && SceneEvaluator.IsActive(clip, time, project.Duration);
        if (selected is not null && project.FindLayer(selected) is VideoLayer chosen && Showing(chosen))
        {
            return chosen;
        }

        var top = SceneEvaluator.Evaluate(project, time).Where(e => e.Cutout is null).Select(e => e.Layer).OfType<VideoLayer>().LastOrDefault(Showing);
        return top ?? (last is not null && project.FindLayer(last) is VideoLayer before && Usable(before) ? before : null);
    }

    // Whether the clip is on the timeline at 'time'.
    public static bool Showing(Project project, VideoLayer clip, double time) => SceneEvaluator.IsActive(clip, time, project.Duration);

    // The tracks of the clip's footage it shows frames of, in the project's order.
    public static List<Track> TracksOn(Project project, VideoLayer clip) =>
        TrackEditor.TracksOf(project, clip).Select(project.FindTrack).OfType<Track>().ToList();

    // A track's frames the clip shows.
    public static List<TrackedFrame> FramesOn(VideoLayer clip, Asset asset, TrackData? data)
    {
        if (data is null)
        {
            return [];
        }

        var (first, last) = TrackEditor.Frames(clip, asset);
        return data.Frames.Where(f => f.Frame >= first && f.Frame <= last).OrderBy(f => f.Frame).ToList();
    }

    // A track's frames on the clip as timeline spans: tracked, and unsure where TrackEditor.Drift
    // finds them (lost frames among them). Each frame lasts until the next one, up to the clip's end.
    public static List<TrackSpan> Spans(VideoLayer clip, Asset asset, IReadOnlyList<TrackedFrame> frames)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        double At(int frame) => Math.Min(clip.TimelineEnd, TrackEditor.TimelineTime(clip, asset, frame));
        var spans = new List<TrackSpan>();
        var from = frames[0].Frame;
        foreach (var drift in TrackEditor.Drift(frames))
        {
            if (drift.First > from)
            {
                spans.Add(new TrackSpan(At(from), At(drift.First), false));
            }

            spans.Add(new TrackSpan(At(drift.First), At(drift.Last + 1), true));
            from = drift.Last + 1;
        }

        if (from <= frames[^1].Frame)
        {
            spans.Add(new TrackSpan(At(from), At(frames[^1].Frame + 1), false));
        }

        return spans;
    }

    public static List<TrackCard> Cards(Project project, VideoLayer clip, Func<Track, TrackData?> data, string? selected)
    {
        var asset = project.FindAsset(clip.Asset)!;
        var cards = new List<TrackCard>();
        foreach (var track in TracksOn(project, clip))
        {
            var hide = RedactionEditor.Of(project, track.Id) is { } redaction
                ? redaction.Style switch { RedactionStyle.Blur => "Blur", RedactionStyle.Pixelate => "Pixelate", _ => "Solid" }
                : "Not hidden";
            var frames = FramesOn(clip, asset, data(track));
            if (frames.Count == 0)
            {
                cards.Add(new TrackCard(track.Id, Title(track), ColorOf(project, track.Id), hide, "Its boxes can't be read", [], null, null, null, track.Id == selected));
                continue;
            }

            var spans = Spans(clip, asset, frames);
            var (start, end) = (spans[0].From, spans[^1].To);
            var length = Math.Max(1e-9, end - start);
            var parts = spans.Select(s => new TrackPart((s.From - start) / length, (s.To - start) / length, s.Unsure)).ToList();
            string? problem = null;
            int? at = null;
            var drift = TrackEditor.Drift(frames);
            if (drift.Count > 0)
            {
                var first = drift[0];
                var count = first.Last - first.First + 1;
                var time = TrackEditor.TimelineTime(clip, asset, first.First);
                problem = $"{(first.Lost > 0 ? "Lost" : "Unsure")} for {count} frame{(count == 1 ? "" : "s")} at {Number(Math.Round(time, 2))} s"
                    + (drift.Count > 1 ? $" (+{drift.Count - 1} more)" : "");
                at = FirstFrameAt(project, time);
            }

            cards.Add(new TrackCard(track.Id, Title(track), ColorOf(project, track.Id), hide, $"{Seconds(start)} – {Seconds(end)} s · {frames.Count} frames", parts, problem, at,
                FirstFrameAt(project, start), track.Id == selected));
        }

        return cards;
    }

    // Each track's box on the frame at 'time' (a project frame's time), where the clip shows it.
    public static List<TrackShape> Shapes(Project project, VideoLayer clip, double time, Func<Track, TrackData?> data)
    {
        var asset = project.FindAsset(clip.Asset)!;
        if (!Showing(project, clip, time))
        {
            return [];
        }

        var (frame, _) = TrackEditor.SourceFrameAt(project, clip, asset, time);
        var shapes = new List<TrackShape>();
        foreach (var track in TracksOn(project, clip))
        {
            if (data(track) is not { } boxes || At(boxes, frame) is not { } box
                || TrackEditor.CanvasCorners(project, clip, asset, time, box.X, box.Y, box.Width, box.Height) is not { } corners)
            {
                continue;
            }

            var label = track.Anchors.Any(a => a.Frame == frame) ? $"{track.Id} · set by hand"
                : box.Lost ? $"{track.Id} · lost"
                : $"{track.Id} · {Math.Round(box.Confidence * 100).ToString("0", CultureInfo.InvariantCulture)}%";
            shapes.Add(new TrackShape(track.Id, ColorOf(project, track.Id), corners.Select(c => new Point(c.X, c.Y)).ToList(), label, Correctable(track)));
        }

        return shapes;
    }

    // Where the middle of a track's box was on the canvas on the TrailFrames frames before the
    // playhead, oldest first.
    public static List<Point> Trail(Project project, VideoLayer clip, int playhead, TrackData data)
    {
        var asset = project.FindAsset(clip.Asset)!;
        var points = new List<Point>();
        for (var frame = Math.Max(0, playhead - TrailFrames); frame < playhead; frame++)
        {
            var time = FrameMath.TimeOf(project, frame);
            if (!Showing(project, clip, time))
            {
                continue;
            }

            var (source, _) = TrackEditor.SourceFrameAt(project, clip, asset, time);
            if (At(data, source) is { } box && TrackEditor.CanvasCorners(project, clip, asset, time, box.X, box.Y, box.Width, box.Height) is { } corners)
            {
                points.Add(new Point(corners.Average(c => c.X), corners.Average(c => c.Y)));
            }
        }

        return points;
    }

    public static List<TrackRow> Rows(Project project, VideoLayer clip, Func<Track, TrackData?> data)
    {
        var asset = project.FindAsset(clip.Asset)!;
        var (first, last) = TrackEditor.Frames(clip, asset);
        return TracksOn(project, clip).Select(track => new TrackRow(track.Id, Title(track), ColorOf(project, track.Id), Spans(clip, asset, FramesOn(clip, asset, data(track))),
            track.Anchors.Where(a => a.Frame >= first && a.Frame <= last).Select(a => TrackEditor.TimelineTime(clip, asset, a.Frame)).ToList())).ToList();
    }

    // A track's box on a source frame.
    public static TrackedFrame? At(TrackData data, int frame)
    {
        var frames = data.Frames;
        if (frames.Count == 0)
        {
            return null;
        }

        var guess = frame - frames[0].Frame;
        if (guess >= 0 && guess < frames.Count && frames[guess].Frame == frame)
        {
            return frames[guess];
        }

        return frames.Find(f => f.Frame == frame);
    }

    // The first project frame at or after a time (the frame that shows it).
    private static int FirstFrameAt(Project project, double time) => Math.Max(0, (int)Math.Ceiling(time * project.Fps - 1e-6));

    private static string Seconds(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
