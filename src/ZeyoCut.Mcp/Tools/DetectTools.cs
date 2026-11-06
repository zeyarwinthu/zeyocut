// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using OpenCvSharp;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class DetectTools
{
    public const double DefaultFaceConfidence = 0.6;
    public const double DefaultPlateConfidence = 0.3;

    // Lists at most this many spans a track was missed on, and brief detections.
    private const int ShownSpans = 3;
    private const int ShownBrief = 5;

    // What a target finds and how: its model and detector, and the words the replies use for it.
    private sealed record Finder(string Noun, string Plural, AiFeature Feature, double DefaultConfidence, TrackMethod Method, Func<AiSession, Mat, double, List<Detection>> Detect, string Missed, string Unrecognized);

    private static Finder For(DetectTarget target) => target == DetectTarget.Faces
        ? new("face", "faces", AiFeatures.Faces, DefaultFaceConfidence, TrackMethod.Faces, FaceDetector.Detect,
            "faces under about 10 px across, turned well away or mostly hidden can be missed",
            "faces are linked only by where their boxes go, not recognized: someone gone for more than {0} s comes back as a new track")
        : new("plate", "plates", AiFeatures.Plates, DefaultPlateConfidence, TrackMethod.Plates, PlateDetector.Detect,
            "plates narrower than about 2% of the frame (40 px in 1080p), seen at a steep angle, blurred or mostly hidden can be missed",
            "plates are linked only by where their boxes go, not read: a car gone for more than {0} s comes back as a new track");

    [McpServerTool(Name = "detect_and_track")]
    [Description("Find every face or license plate in a video clip with an AI model and link each into a track (face_1, face_2, ... or plate_1, plate_2, ...) to hide with apply_redaction. It runs as a detect_N job (poll get_job_status) that searches every frame from startTime to endTime (default: the whole clip) at up to 1920 px. Faces: YuNet (230 KB, MIT), also searched at a third and an eighth of that size, so faces from about 10 px across to the whole frame are found. Plates: open-image-models' YOLOv9-t (8 MB; MIT code, the weights' license isn't stated), which sees the frame at 640 px, so each corner of it is searched too (2x2 overlapping tiles), finding plates down to about 2% of the frame's width (40 px in 1080p). The model is downloaded on first use. Boxes are linked from frame to frame like ByteTrack: confident detections (minConfidence, default 0.6 for faces, 0.3 for plates) start and continue tracks, weaker ones (down to half of it) only continue a track found on the frame before, and a track ends after 1 s without one. Nothing identifies a person or reads a plate: someone or a car gone for longer comes back as a new track. Detections on fewer than 3 frames are left out and listed. When it is done, the tracks are added as one undo step, saved in the clip's own frames and pixels like track_object's, and get_job_status shows a contact sheet with one labeled picture per track and lists each track's span, frames found and missed, and average size. Before starting it estimates the time; a job over 5 minutes isn't started without confirmLongJob=true. render_frame debug=true draws each track's box.")]
    public static string DetectAndTrack(
        EditorState state,
        [Description("The video layer (clip) to search.")] string layerId,
        [Description("What to find: faces or plates.")] string target,
        [Description("Timeline time in seconds to start at. Default: where the clip starts.")] double? startTime = null,
        [Description("Timeline time in seconds to stop at. Default: where the clip ends.")] double? endTime = null,
        [Description("How sure the model must be to start a track: above 0, at most 1. Default 0.6 for faces, 0.3 for plates. Lower finds more, and more false ones; detections down to half of it keep a track going.")] double? minConfidence = null,
        [Description("Start even if it would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first.")] bool confirmLongJob = false) => Guard(() =>
    {
        var finder = For(EnumNames.Parse<DetectTarget>(target, "target"));
        var confident = minConfidence ?? finder.DefaultConfidence;
        if (!double.IsFinite(confident) || confident <= 0 || confident > 1)
        {
            throw new ZeyoCutException($"minConfidence must be above 0 and at most 1 (got {Num(confident)})");
        }

        var weak = confident / 2;
        var session = state.Session;
        var (clip, asset, first, last, shown) = session.Read(p => Range(p, layerId, startTime, endTime));
        var proxy = Proxies.Ready(session.Directory, asset);
        var file = proxy ?? ProjectStore.AssetPath(session.Directory, asset);
        if (!File.Exists(file))
        {
            throw new ZeyoCutException($"the file of asset '{asset.Id}' is missing ({file})");
        }

        var ffmpeg = state.RequireFfmpeg().Ffmpeg;
        var feature = finder.Feature;
        var model = feature.Model;
        var device = state.PickDevice(feature).Device;
        var frames = last - first + 1;
        var (width, height) = ObjectTracker.TrackingSize(asset);
        var estimate = AiBenchmark.Estimate(feature, device, frames, width, height, state.Benchmark());
        var work = $"finding {finder.Plural} on {frames} frames of '{asset.Id}' at {width}x{height}{(proxy is null ? "" : " (its proxy)")} with {model.Name} on {(device == AiDevice.Gpu ? "the GPU (DirectML, or the CPU if it can't start)" : "the CPU")}";
        if (SystemTools.NotStarted([new SystemTools.JobEstimate(estimate, work)], confirmLongJob, "detect_and_track") is { } notStarted)
        {
            return notStarted;
        }

        var store = state.Models;
        var download = store.Has(model) ? "" : $"; it first downloads {model.FileName} ({model.Size}, {model.License}) into {store.Folder}";
        var job = state.Jobs.Start("detect", $"detect_and_track {finder.Plural} on '{clip}' ({frames} frames of '{asset.Id}')", (job, ct) =>
        {
            var share = store.Has(model) ? 0 : 0.02;
            var path = store.Ensure(model, (done, total) => job.Report(share * done / total, $"downloading {model.FileName}"), ct);
            if (!MatteTools.ComputeGate.Wait(0, ct))
            {
                job.Report(share, "waiting for another AI job to finish");
                MatteTools.ComputeGate.Wait(ct);
            }

            FindResult result;
            string runsOn;
            try
            {
                job.Report(share, $"loading {model.Name}");
                using var ai = AiSession.Open(path, device);
                runsOn = ai.Device;
                result = ObjectFinder.Find(ffmpeg, file, asset, first, last, bgr => finder.Detect(ai, bgr, weak), confident, weak,
                    (done, total) => job.Report(share + (1 - share) * done / total, $"finding {finder.Plural}: frame {done} of {total}"), ct);
            }
            finally
            {
                MatteTools.ComputeGate.Release();
            }

            using (result)
            {
                return Add(state, session, job, finder, clip, asset, first, last, confident, result, $"with {model.Name} on the {runsOn}");
            }
        });

        return $"finding {finder.Plural} on '{clip}' in the background as {job.Id}: {work}, {estimate.Describe()}{download}"
            + $"\n- frames {first}–{last} of '{asset.Id}' ({shown})"
            + $"\n- poll get_job_status with jobId \"{job.Id}\"; when it is done, it adds a track for each {finder.Noun} as one undo step and shows a contact sheet with their ids";
    });

    // The source frames of the clip from startTime to endTime (its whole span by default).
    private static (string Clip, Asset Asset, int First, int Last, string Shown) Range(Project project, string layerId, double? startTime, double? endTime)
    {
        var (layer, asset) = TrackEditor.Clip(project, layerId);
        var from = startTime ?? layer.Start;
        var to = endTime ?? layer.TimelineEnd;
        if (!double.IsFinite(from) || !double.IsFinite(to) || from < layer.Start - 1e-9 || to > layer.TimelineEnd + 1e-9 || to <= from)
        {
            throw new ZeyoCutException(
                $"startTime and endTime must be inside clip '{layer.Id}' ({Num(layer.Start)}–{Num(layer.TimelineEnd)}s) with startTime before endTime (got {Num(from)}–{Num(to)}s)");
        }

        var (shownFirst, shownLast) = TrackEditor.Frames(layer, asset);
        var first = Math.Clamp(VideoTiming.FrameAt(asset, VideoTiming.SourceTime(layer, from)), shownFirst, shownLast);
        var last = Math.Clamp(VideoTiming.FrameAt(asset, VideoTiming.SourceTime(layer, to) - 1e-6), first, shownLast);
        var shown = $"{Num(TrackTools.Timeline(layer, asset, first))}–{Num(TrackTools.Timeline(layer, asset, last))}s on '{layer.Id}'";
        return (layer.Id, asset, first, last, shown);
    }

    // Adds the found tracks as one undo step and describes them; the contact sheet goes on the job.
    private static string Add(EditorState state, ProjectSession session, BackgroundJob job, Finder finder, string clip, Asset asset, int first, int last, double confident, FindResult result, string how)
    {
        var pace = $"in {AiMattes.Seconds(result.Elapsed)} ({Num(Math.Round(result.Frames / Math.Max(0.001, result.Elapsed.TotalSeconds), 1))} frames/s)";
        if (result.Tracks.Count == 0)
        {
            var none = new List<string>();
            none.AddRange(session.Read(p => Brief(p, clip, asset, result.Brief, finder.Plural)));
            none.Add($"{finder.Missed}; a lower minConfidence (now {Num(confident)}) finds weaker ones");
            return $"found no {finder.Plural} in '{clip}' {how}: frames {first}–{last} of '{asset.Id}' {pace}; nothing was changed" + string.Concat(none.Select(l => "\n- " + l));
        }

        List<string> ids;
        lock (TrackTools.TrackGate)
        {
            ids = session.Read(p =>
            {
                var taken = new List<string>();
                foreach (var _ in result.Tracks)
                {
                    taken.Add(TrackEditor.NewId(p, null, i => state.TrackWork.ContainsKey(i) || taken.Contains(i), finder.Noun));
                }

                return taken;
            });
            foreach (var id in ids)
            {
                state.TrackWork[id] = job;
            }
        }

        try
        {
            var tracks = new List<Track>();
            for (var i = 0; i < ids.Count; i++)
            {
                var frames = result.Tracks[i].Frames;
                var data = new TrackData { Track = ids[i], Asset = asset.Id, Width = asset.Width ?? 0, Height = asset.Height ?? 0, Frames = frames };
                tracks.Add(new Track
                {
                    Id = ids[i],
                    Asset = asset.Id,
                    Layer = clip,
                    Method = finder.Method,
                    Direction = TrackDirection.Forward,
                    FirstFrame = frames[0].Frame,
                    LastFrame = frames[^1].Frame,
                    Origin = frames[0].Frame,
                    File = TrackFiles.Save(session.Directory, data),
                });
            }

            session.Mutate($"detect_and_track {finder.Plural} {clip}", p =>
            {
                if (p.FindAsset(asset.Id) is null)
                {
                    throw new ZeyoCutException($"asset '{asset.Id}' was removed while its {finder.Plural} were being found, so no track was added; run detect_and_track again");
                }

                foreach (var track in tracks)
                {
                    if (p.FindTrack(track.Id) is not null)
                    {
                        throw new ZeyoCutException($"a track '{track.Id}' appeared while the {finder.Plural} were being found, so no track was added; run detect_and_track again");
                    }

                    p.Tracks.Add(track);
                }

                return 0;
            });
        }
        finally
        {
            foreach (var id in ids)
            {
                state.TrackWork.TryRemove(new KeyValuePair<string, BackgroundJob?>(id, job));
            }
        }

        var (lines, cells) = session.Read(p => Describe(p, clip, asset, ids, result, finder.Plural));
        var sheet = Preview.RenderTrackSheet(session.Directory, $"{finder.Plural}_{clip}_{first:D6}-{last:D6}", cells);
        job.Image = sheet.Png;
        lines.Add($"the contact sheet shows each {finder.Noun} with its id; saved {sheet.SavedPath}");
        lines.Add($"hide them with apply_redaction trackIds=[{string.Join(", ", ids.Select(id => $"\"{id}\""))}] or [\"all\"]; render_frame debug=true draws each track's box");
        lines.Add(string.Format(CultureInfo.InvariantCulture, finder.Unrecognized, Num(ObjectFinder.BufferSeconds)));
        var count = ids.Count == 1 ? $"1 {finder.Noun}" : $"{ids.Count} {finder.Plural}";
        return $"found {count} in '{clip}' {how}: frames {first}–{last} of '{asset.Id}' {pace}" + string.Concat(lines.Select(l => "\n- " + l));
    }

    private static (List<string> Lines, List<(string Id, string Detail, SKImage Picture)> Cells) Describe(Project project, string clip, Asset asset, List<string> ids, FindResult result, string plural)
    {
        var lines = new List<string>();
        var cells = new List<(string Id, string Detail, SKImage Picture)>();
        var layer = project.FindLayer(clip) as VideoLayer;
        for (var i = 0; i < ids.Count; i++)
        {
            var frames = result.Tracks[i].Frames;
            var found = frames.Where(f => !f.Lost).ToList();
            var width = found.Average(f => f.Width);
            var height = found.Average(f => f.Height);
            var missed = Missed(frames);
            var gaps = missed.Count == 0
                ? ""
                : $" (missed {string.Join(", ", missed.Take(ShownSpans).Select(m => Times(layer, asset, m.First, m.Last)))}{(missed.Count > ShownSpans ? $" and {missed.Count - ShownSpans} more" : "")})";
            lines.Add($"{ids[i]}: {RedactionTools.Plays(project, asset, frames[0].Frame, frames[^1].Frame)}; found on {found.Count} of {frames.Count} frames{gaps}, average {Whole(width)}x{Whole(height)} px, confidence {Two(found.Average(f => f.Confidence))}");
            cells.Add((ids[i], $"{Times(layer, asset, frames[0].Frame, frames[^1].Frame)} · {Whole(Math.Max(width, height))} px", result.Tracks[i].Picture));
        }

        lines.AddRange(Brief(project, clip, asset, result.Brief, plural));
        return (lines, cells);
    }

    private static List<string> Brief(Project project, string clip, Asset asset, List<int> brief, string plural)
    {
        if (brief.Count == 0)
        {
            return [];
        }

        var layer = project.FindLayer(clip) as VideoLayer;
        var times = string.Join(", ", brief.Take(ShownBrief).Select(f => Times(layer, asset, f, f)));
        var more = brief.Count > ShownBrief ? $" and {brief.Count - ShownBrief} more" : "";
        return [$"left out {brief.Count} detection{(brief.Count == 1 ? "" : "s")} on fewer than {DetectionLinker.MinDetections} frames, probably not {plural}: at {times}{more}{(layer is null ? "" : $" on '{clip}'")}"];
    }

    // Runs of frames no detection matched.
    private static List<(int First, int Last)> Missed(List<TrackedFrame> frames)
    {
        var spans = new List<(int First, int Last)>();
        foreach (var frame in frames.Where(f => f.Lost))
        {
            if (spans.Count > 0 && spans[^1].Last == frame.Frame - 1)
            {
                spans[^1] = (spans[^1].First, frame.Frame);
            }
            else
            {
                spans.Add((frame.Frame, frame.Frame));
            }
        }

        return spans;
    }

    // "1.5–1.583s" on the clip searched, or in the footage's seconds when that clip is gone.
    private static string Times(VideoLayer? layer, Asset asset, int first, int last)
    {
        double At(int frame) => layer is null ? VideoTiming.TimeOfFrame(asset, frame) : TrackTools.Timeline(layer, asset, frame);
        return first == last ? $"{Num(At(first))}s" : $"{Num(At(first))}–{Num(At(last))}s";
    }

    private static string Whole(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static string Two(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
