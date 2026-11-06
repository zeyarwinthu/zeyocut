// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class TrackTools
{
    // Up to this many frames are tracked before the reply; more run as a tracking_N job.
    public const int DirectFrames = 150;

    // Lists at most this many drift spans.
    private const int ShownSpans = 8;

    internal static readonly object TrackGate = new();

    [McpServerTool(Name = "track_object")]
    [Description("Follow an object through a video clip with OpenCV and save its box on every frame as a track (one undo step). Draw the box around the object in canvas pixels at 'time', as render_frame shows that frame; it is mapped into the clip's own frames and pixels (through its placement, parents, the camera and its crop), so the track keeps fitting after trims, speed changes and splits, and every clip of the same footage shares it. direction forward tracks to the end of the clip's trimmed range, backward to its start, both both ways. Methods: csrt (default; OpenCV's CSRT tracker: follows changes of shape and size, which can wobble a little), template (finds the starting picture near the last box: fast and a fixed size, for things that don't turn or grow), opticalFlow (moves the box with the points inside it and scales it with their spread: for textured things). Every frame gets a confidence (0-1: how well the starting picture still matches in the box); the reply lists spans under 0.5 as possible drift, and where the tracker loses the object it holds the box before. Look at those frames with render_frame debug=true, which draws each track's box, and correct the box with reanchor_track. Up to 150 frames answers directly; longer runs are a tracking_N job (poll get_job_status) that adds the track as its own undo step. list_layers shows each clip's tracks.")]
    public static string TrackObject(
        EditorState state,
        [Description("The video layer (clip) the object is in.")] string layerId,
        [Description("Timeline time in seconds of the frame the box is drawn on (the nearest frame, as render_frame shows it).")] double time,
        [Description("The object's box in canvas pixels at that time: x, y (top-left corner), width, height.")] BoxArg box,
        [Description("forward (to the end of the clip), backward (to its start) or both. Default forward.")] string direction = "forward",
        [Description("csrt, template or opticalFlow. Default csrt.")] string method = "csrt",
        [Description("Track id. Default track_N.")] string? id = null) => Guard(() =>
    {
        var how = EnumNames.Parse<TrackMethod>(method, "method");
        if (how is TrackMethod.Faces or TrackMethod.Plates)
        {
            throw new ZeyoCutException($"unknown method '{method}'; valid values: csrt, template, opticalFlow (detect_and_track finds faces and plates)");
        }

        var way = EnumNames.Parse<TrackDirection>(direction, "direction");
        ArgumentNullException.ThrowIfNull(box);
        var session = state.Session;
        Track track;
        Asset asset;
        string clip;
        double shown;
        List<TrackRun> runs;
        string headline;
        Func<string, Action<double>?, CancellationToken, string> work;
        BackgroundJob? job = null;
        lock (TrackGate)
        {
            (track, asset, clip, shown) = session.Read(p =>
            {
                var (layer, footage) = TrackEditor.Clip(p, layerId);
                var (frame, at) = TrackEditor.SourceFrameAt(p, layer, footage, time);
                var anchor = TrackEditor.SourceBox(p, layer, footage, at, frame, box.X, box.Y, box.Width, box.Height);
                var (first, last) = TrackEditor.Frames(layer, footage);
                var made = new Track
                {
                    Id = TrackEditor.NewId(p, id, state.TrackWork.ContainsKey),
                    Asset = footage.Id,
                    Layer = layer.Id,
                    Method = how,
                    Direction = way,
                    FirstFrame = way == TrackDirection.Forward ? frame : first,
                    LastFrame = way == TrackDirection.Backward ? frame : last,
                    Origin = frame,
                    Anchors = [anchor],
                };
                return (made, footage, layer.Id, at);
            });
            runs = TrackEditor.Runs(way, track.Origin, track.FirstFrame, track.LastFrame);
            work = Work(state, session, track, asset, null, runs, track.Anchors[0], "track_object");
            var frames = runs.Sum(r => r.Frames);
            headline = $"tracked the box on '{clip}' as {track.Id} with {EnumNames.ToName(how)}, {EnumNames.ToName(way)}";
            if (frames <= DirectFrames)
            {
                state.TrackWork[track.Id] = null;
            }
            else
            {
                job = state.Jobs.Start("tracking", $"track_object {track.Id} ({EnumNames.ToName(how)}, {frames} frames of '{asset.Id}')", (job, ct) =>
                {
                    try
                    {
                        return work(headline, p => job.Report(p, "tracking"), ct);
                    }
                    finally
                    {
                        lock (TrackGate)
                        {
                            state.TrackWork.TryRemove(new KeyValuePair<string, BackgroundJob?>(track.Id, job));
                        }
                    }
                });
                state.TrackWork[track.Id] = job;
            }
        }

        if (job is null)
        {
            try
            {
                return work(headline, null, CancellationToken.None);
            }
            finally
            {
                state.TrackWork.TryRemove(track.Id, out _);
            }
        }

        var anchor = track.Anchors[0];
        return $"tracking the box on '{clip}' as {track.Id} with {EnumNames.ToName(how)}, {EnumNames.ToName(way)}, in the background as {job.Id}: {runs.Sum(r => r.Frames)} frames of '{asset.Id}' (frames {track.FirstFrame}–{track.LastFrame})"
            + $"\n- drawn at {Num(shown)}s: frame {anchor.Frame}, {Box(anchor.X, anchor.Y, anchor.Width, anchor.Height)} in the source pixels of '{asset.Id}'"
            + $"\n- poll get_job_status with jobId \"{job.Id}\"; the track is added as its own undo step when it is done";
    });

    [McpServerTool(Name = "reanchor_track")]
    [Description("Correct a track where it drifted (one undo step): set the box at 'time' (canvas pixels, as render_frame shows that frame) and track again from there, away from the frame track_object started at, up to the next box set by hand or the end of the track. Frames on the other side stay as they are. Time and box are mapped through the clip the track was made on, or layerId (another clip of the same footage, such as a piece split off it). Up to 150 frames answers directly; more run as a tracking_N job.")]
    public static string ReanchorTrack(
        EditorState state,
        [Description("The track to correct (track_object's id).")] string trackId,
        [Description("Timeline time in seconds of the frame to correct (the nearest frame, as render_frame shows it).")] double time,
        [Description("The object's box in canvas pixels at that time: x, y (top-left corner), width, height.")] BoxArg box,
        [Description("The clip to map time and box through. Default: the clip the track was made on.")] string? layerId = null) => Guard(() =>
    {
        ArgumentNullException.ThrowIfNull(box);
        var session = state.Session;
        Track track;
        Asset asset;
        string clip;
        double shown;
        TrackBox anchor;
        List<TrackRun> runs;
        string headline;
        Func<string, Action<double>?, CancellationToken, string> work;
        BackgroundJob? job = null;
        lock (TrackGate)
        {
            if (state.TrackWork.TryGetValue(trackId, out var running))
            {
                throw new ZeyoCutException($"track '{trackId}' is still being tracked{(running is null ? "" : $" by {running.Id} ({running.Percent}%)")}; correct it when that is done");
            }

            (track, asset, clip, shown, anchor) = session.Read(p =>
            {
                var found = p.FindTrack(trackId)
                    ?? throw new ZeyoCutException($"track '{trackId}' not found; tracks: {Ids.List(p.Tracks.Select(t => t.Id))}");
                if (found.Method is TrackMethod.Faces or TrackMethod.Plates)
                {
                    throw new ZeyoCutException(
                        $"track '{trackId}' was found on every frame by detect_and_track, not tracked from a box, so reanchor_track can't correct it; where it missed the face or plate, track_object makes a track from a box there, which apply_redaction can hide too");
                }

                if (layerId is null && (p.FindLayer(found.Layer) is not VideoLayer original || original.Asset != found.Asset))
                {
                    var clips = p.Layers.OfType<VideoLayer>().Where(v => v.Asset == found.Asset).Select(v => v.Id);
                    throw new ZeyoCutException($"clip '{found.Layer}', which track '{trackId}' was made on, is gone or plays other footage; pass layerId, a clip of '{found.Asset}': {Ids.List(clips)}");
                }

                var (layer, footage) = TrackEditor.Clip(p, layerId ?? found.Layer);
                if (layer.Asset != found.Asset)
                {
                    throw new ZeyoCutException($"clip '{layer.Id}' plays '{layer.Asset}', but track '{trackId}' follows '{found.Asset}'");
                }

                var (frame, at) = TrackEditor.SourceFrameAt(p, layer, footage, time);
                if (frame < found.FirstFrame || frame > found.LastFrame)
                {
                    throw new ZeyoCutException(
                        $"{Num(at)}s on '{layer.Id}' shows frame {frame} of '{footage.Id}', outside track '{trackId}' (frames {found.FirstFrame}–{found.LastFrame}); reanchor_track corrects frames inside a track, and track_object starts a new one");
                }

                var set = TrackEditor.SourceBox(p, layer, footage, at, frame, box.X, box.Y, box.Width, box.Height);
                return (ProjectJson.Copy(found), footage, layer.Id, at, set);
            });
            var old = TrackFiles.Load(session.Directory, track);
            runs = TrackEditor.Rerun(track, anchor.Frame);
            var replacing = track.File;
            track.Anchors = TrackEditor.WithAnchor(track.Anchors, anchor);
            work = Work(state, session, track, asset, (old, replacing), runs, anchor, "reanchor_track");
            var frames = runs.Sum(r => r.Frames);
            var low = runs.Min(r => Math.Min(r.From, r.To));
            var high = runs.Max(r => Math.Max(r.From, r.To));
            headline = $"reanchored {track.Id} at frame {anchor.Frame} ({Num(shown)}s on '{clip}') and tracked frames {low}–{high} again with {EnumNames.ToName(track.Method)}; its other frames stayed as they were";
            if (frames <= DirectFrames)
            {
                state.TrackWork[track.Id] = null;
            }
            else
            {
                job = state.Jobs.Start("tracking", $"reanchor_track {track.Id} ({EnumNames.ToName(track.Method)}, {frames} frames of '{asset.Id}')", (job, ct) =>
                {
                    try
                    {
                        return work(headline, p => job.Report(p, "tracking"), ct);
                    }
                    finally
                    {
                        lock (TrackGate)
                        {
                            state.TrackWork.TryRemove(new KeyValuePair<string, BackgroundJob?>(track.Id, job));
                        }
                    }
                });
                state.TrackWork[track.Id] = job;
            }
        }

        if (job is null)
        {
            try
            {
                return work(headline, null, CancellationToken.None);
            }
            finally
            {
                state.TrackWork.TryRemove(track.Id, out _);
            }
        }

        return $"reanchoring {track.Id} at frame {anchor.Frame} ({Num(shown)}s on '{clip}') in the background as {job.Id}: tracking {runs.Sum(r => r.Frames)} frames of '{asset.Id}' again with {EnumNames.ToName(track.Method)}"
            + $"\n- poll get_job_status with jobId \"{job.Id}\"; the corrected track replaces the old one as its own undo step when it is done";
    });

    [McpServerTool(Name = "attach_to_track")]
    [Description("Keep a text or image layer on a track's box (one undo step), such as a name over a face or a sticker on a sign: x and y keyframes, and scaleX and scaleY with followScale, on every frame where a clip shows the footage the track found the object in, through the clip's placement, parents and crop, the camera, and the layer's own parent. Frames the track lost are filled in straight between the frames around them. The layer's anchor sits at the box's center plus offset (canvas pixels at the first frame it follows); without offset it stays where it is at that frame, so place it first. With followScale (default) the layer and its offset grow and shrink with the box, from their size at that first frame. The keyframes are kept only where needed to stay within 0.5 px of every frame, and replace the layer's own x and y (and scale) keyframes in that time, and its motion path. They are timeline keyframes: after trimming, moving or changing the speed of the clip, run attach_to_track again.")]
    public static string AttachToTrack(
        EditorState state,
        [Description("The text or image layer to move with the track.")] string layerId,
        [Description("The track to follow (from track_object or detect_and_track).")] string trackId,
        [Description("The layer's anchor from the center of the track's box, in canvas pixels at the first frame it follows: x, y. Default: where the layer is at that frame.")] OffsetArg? offset = null,
        [Description("Scale the layer, and the offset, with the box's size from the first frame it follows. Default true.")] bool followScale = true) => Guard(() =>
    {
        var session = state.Session;
        var id = trackId?.Trim() ?? "";
        if (state.TrackWork.TryGetValue(id, out var running))
        {
            throw new ZeyoCutException($"track '{id}' is still being tracked{(running is null ? "" : $" by {running.Id} ({running.Percent}%)")}; attach to it when that is done");
        }

        return session.Mutate($"attach_to_track {layerId} {id}", p =>
        {
            var track = p.FindTrack(id)
                ?? throw new ZeyoCutException($"track '{id}' not found; tracks: {Ids.List(p.Tracks.Select(t => t.Id))}");
            var result = AttachEditor.Attach(p, layerId, track, TrackFiles.Load(session.Directory, track), offset is null ? null : (offset.X, offset.Y), followScale);
            var start = result.Runs[0].From;
            var spans = string.Join(", ", result.Runs.Select(r => $"{Num(r.From)}–{Num(r.To)}s on '{r.Clip}'"));
            var lines = new List<string>
            {
                $"its anchor sits {Num(result.OffsetX)},{Num(result.OffsetY)} canvas px from the box's center at {Num(start)}s{(result.OffsetGiven ? "" : " (where it was then)")}{(followScale ? "; the offset scales with the box" : "")}",
                $"keyframes: {string.Join(", ", result.Keys.Select(k => $"{k.Property} {k.Keys}"))}, kept to stay within {Num(AttachEditor.PlaceTolerance)} px{(followScale ? $" and {Num(AttachEditor.ScaleTolerance * 100)}% of the size" : "")} of every frame"
                    + (result.Replaced > 0 ? $"; they replaced {result.Replaced} keyframes it had in that time" : "")
                    + "; outside it, its own keyframes and values apply",
            };
            if (followScale)
            {
                lines.Add($"its size goes from {Two(result.Smallest)} to {Two(result.Largest)} times its size at {Num(start)}s");
            }

            lines.Add("these are timeline keyframes: after trimming, moving or changing the speed of a clip that plays the footage, run attach_to_track again");
            lines.Add("check it with render_frame; debug=true draws the track's box");
            var text = $"attached '{result.Layer.Id}' to {track.Id}: it follows the box {spans}{(followScale ? " and scales with it" : "")}"
                + string.Concat(lines.Select(l => "\n- " + l));
            return WithWarnings(text, result.Warnings);
        });
    });

    // Tracks the runs, saves the boxes (over 'old', for reanchor_track) and puts the track in the
    // project as one undo step; returns the reply.
    private static Func<string, Action<double>?, CancellationToken, string> Work(
        EditorState state,
        ProjectSession session,
        Track track,
        Asset asset,
        (TrackData Data, string File)? old,
        List<TrackRun> runs,
        TrackBox anchor,
        string tool)
    {
        var file = Proxies.Ready(session.Directory, asset) ?? ProjectStore.AssetPath(session.Directory, asset);
        if (!File.Exists(file))
        {
            throw new ZeyoCutException($"the file of asset '{asset.Id}' is missing ({file})");
        }

        var ffmpeg = state.RequireFfmpeg().Ffmpeg;
        return (headline, progress, ct) =>
        {
            var frames = old?.Data.Frames.ToDictionary(f => f.Frame) ?? [];
            var total = Math.Max(1, runs.Sum(r => r.Frames));
            var done = 0;
            foreach (var run in runs)
            {
                var offset = done;
                var from = new TrackBox { Frame = run.From, X = anchor.X, Y = anchor.Y, Width = anchor.Width, Height = anchor.Height };
                foreach (var frame in ObjectTracker.Track(ffmpeg, file, asset, from, track.Method, run.To, p => progress?.Invoke((offset + p * run.Frames) / total), ct))
                {
                    frames[frame.Frame] = frame;
                }

                done += run.Frames;
            }

            var data = new TrackData
            {
                Track = track.Id,
                Asset = asset.Id,
                Width = asset.Width ?? 0,
                Height = asset.Height ?? 0,
                Frames = frames.Values.Where(f => f.Frame >= track.FirstFrame && f.Frame <= track.LastFrame).OrderBy(f => f.Frame).ToList(),
            };
            track.File = TrackFiles.Save(session.Directory, data);
            session.Mutate($"{tool} {track.Id}", p =>
            {
                if (old is { } before)
                {
                    var index = p.Tracks.FindIndex(t => t.Id == track.Id);
                    if (index < 0 || p.Tracks[index].File != before.File)
                    {
                        throw new ZeyoCutException($"track '{track.Id}' changed or was removed while it was being tracked, so the correction wasn't saved; run reanchor_track again");
                    }

                    p.Tracks[index] = track;
                }
                else
                {
                    if (p.FindTrack(track.Id) is not null)
                    {
                        throw new ZeyoCutException($"a track '{track.Id}' appeared while this one was being tracked, so it wasn't saved; run track_object again");
                    }

                    p.Tracks.Add(track);
                }

                return 0;
            });
            return session.Read(p => Describe(p, track, data, headline));
        };
    }

    private static string Describe(Project project, Track track, TrackData data, string headline)
    {
        var asset = project.FindAsset(track.Asset)!;
        var first = data.Frames[0];
        var last = data.Frames[^1];
        var lines = new List<string>
        {
            $"frames {first.Frame}–{last.Frame} of '{asset.Id}' ({data.Frames.Count} frames): {Span(project, track, asset, first.Frame, last.Frame)}",
            $"the box in source pixels: {Box(first.X, first.Y, first.Width, first.Height)} at frame {first.Frame}, {Box(last.X, last.Y, last.Width, last.Height)} at frame {last.Frame}",
        };
        var lowest = data.Frames.MinBy(f => f.Confidence)!;
        lines.Add($"confidence: average {Two(data.Frames.Average(f => f.Confidence))}, lowest {Two(lowest.Confidence)} at frame {lowest.Frame} ({At(project, track, asset, lowest.Frame)})");
        var drift = TrackEditor.Drift(data.Frames);
        if (drift.Count == 0)
        {
            lines.Add($"no possible drift: every frame's confidence is {Num(TrackEditor.DriftBelow)} or more");
        }
        else
        {
            var spans = drift.Take(ShownSpans).Select(d =>
                $"{Span(project, track, asset, d.First, d.Last)} (frames {d.First}–{d.Last}, lowest {Two(d.Lowest)}{(d.Lost > 0 ? $", lost on {d.Lost}" : "")})").ToList();
            if (drift.Count > ShownSpans)
            {
                spans.Add($"{drift.Count - ShownSpans} more");
            }

            lines.Add($"possible drift (confidence under {Num(TrackEditor.DriftBelow)}): {string.Join("; ", spans)}");
            lines.Add($"look at those frames with render_frame debug=true (it draws the box); where it is off the object, correct it with reanchor_track trackId=\"{track.Id}\"");
        }

        return headline + string.Concat(lines.Select(l => "\n- " + l));
    }

    // Where a source frame plays: "1.2s on 'clip'", or its source time when no clip shows it.
    private static string At(Project project, Track track, Asset asset, int frame) =>
        TrackEditor.ClipShowing(project, track, frame) is { } clip
            ? $"{Num(Timeline(clip, asset, frame))}s on '{clip.Id}'"
            : $"source {Num(VideoTiming.TimeOfFrame(asset, frame))}s, which no clip shows";

    private static string Span(Project project, Track track, Asset asset, int first, int last)
    {
        if (TrackEditor.ClipShowing(project, track, first) is { } clip)
        {
            var (_, shownLast) = TrackEditor.Frames(clip, asset);
            return $"{Num(Timeline(clip, asset, first))}–{Num(Timeline(clip, asset, Math.Min(last, shownLast)))}s on '{clip.Id}'";
        }

        return $"source {Num(VideoTiming.TimeOfFrame(asset, first))}–{Num(VideoTiming.TimeOfFrame(asset, last))}s, which no clip shows";
    }

    internal static double Timeline(VideoLayer clip, Asset asset, int frame) =>
        VideoTiming.TimelineTime(clip, Math.Max(clip.TrimIn, VideoTiming.TimeOfFrame(asset, frame)));

    private static string Box(double x, double y, double width, double height) => $"{Num(x)},{Num(y)} {Num(width)}x{Num(height)}";

    private static string Two(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
