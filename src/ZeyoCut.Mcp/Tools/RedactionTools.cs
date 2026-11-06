// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class RedactionTools
{
    [McpServerTool(Name = "apply_redaction")]
    [Description("Hide tracked faces, plates or objects (one undo step): adds a redaction layer (type redaction) that hides the box of each track on the video frame before the clip is drawn, in every clip that plays the track's footage, so moves, effects, the camera and subject cutouts all show it hidden, and splits, trims and speed changes keep it in step. Styles: pixelate (default), blur, solid (a color). Strength scales with the box: the pixelate block (or blur radius) is the box's long side over 20 at strength 0 down to 4 at 100 (12 at the default 50), never under 4 source pixels, so a small plate is as unreadable as a big face. The fail-safe: every box is padded by paddingPercent; frames the track lost between two detections get a box moved straight between them that grows with each missed frame, so no frame between them is uncovered; the box is held (and grows) for holdFrames before the first detection and after the last, but not across a scene cut that analyze_video found in the footage; and the box's motion is smoothed while always containing the frame's own box. feather adds a soft edge outside the box. Running it again on the same tracks changes their redaction; tracks in another redaction move to the new one. update_layer changes the settings (style, strength, paddingPercent, feather, holdFrames, color, visible); tracks_to_keyframes turns a track's box into keyframes to edit by hand. Check with render_frame.")]
    public static string ApplyRedaction(
        EditorState state,
        [Description("Track ids to hide (list_layers shows a clip's tracks as tracks=), or [\"all\"] for every track.")] string[] trackIds,
        [Description("pixelate, blur or solid. Default pixelate.")] string style = "pixelate",
        [Description("0-100: pixelate blocks or blur across the box, 20 at 0 down to 4 at 100. Default 50.")] double strength = 50,
        [Description("Each side of the box grows by this percent of its width or height. Default 15.")] double paddingPercent = 15,
        [Description("Soft edge outside the box, in source pixels. Default 4.")] double feather = 4,
        [Description("Frames the box is held, and grows, before a track's first detection and after its last; a scene cut that analyze_video found ends the hold sooner. Default 15.")] int holdFrames = 15,
        [Description("solid: the fill, #RRGGBB or #RRGGBBAA. Default #000000.")] string color = "#000000") => Guard(() =>
    {
        var kind = EnumNames.Parse<RedactionStyle>(style, "style");
        var session = state.Session;
        var label = string.Join(",", (trackIds ?? []).Select(t => t?.Trim()));
        return session.Mutate($"apply_redaction {label}", p =>
        {
            var change = RedactionEditor.Apply(p, trackIds, new RedactionSettings(kind, strength, paddingPercent, feather, holdFrames, color));
            return Describe(p, session.Directory, change);
        });
    });

    [McpServerTool(Name = "tracks_to_keyframes")]
    [Description("Turn a hidden track's box into keyframes on its redaction (one undo step), to edit by hand: x, y, width and height keyframes named <track>.x, <track>.y, <track>.width and <track>.height on the redaction layer, made from the fail-safe box (padding, gaps, holds and smoothing included) and kept only where needed to stay within 0.2 px of it. The box is 0.5 px bigger on each side, so on every frame it contains the box it was made from. The keyframes are in the footage's own seconds and pixels, like the track, so the box keeps following trims, speed changes and splits; the redaction then hides that box from its first keyframe to its last, and paddingPercent and holdFrames no longer change it. Edit it with set_keyframes and remove_keyframes (time in the footage's seconds); clear_keyframes with property \"<track>\" goes back to the box worked out from the track. Running it again remakes the keyframes from the track.")]
    public static string TracksToKeyframes(
        EditorState state,
        [Description("The track whose box to turn into keyframes; apply_redaction must hide it first.")] string trackId) => Guard(() =>
    {
        var session = state.Session;
        return session.Mutate($"tracks_to_keyframes {trackId}", p =>
        {
            var track = p.FindTrack(trackId?.Trim() ?? "")
                ?? throw new ZeyoCutException($"track '{trackId}' not found; tracks: {Ids.List(p.Tracks.Select(t => t.Id))}");
            var layer = RedactionEditor.Of(p, track.Id)
                ?? throw new ZeyoCutException($"track '{track.Id}' isn't hidden by a redaction yet; apply_redaction trackIds=[\"{track.Id}\"] first");
            var asset = p.FindAsset(track.Asset)!;
            var cover = RedactionCover.Of(TrackFiles.Load(session.Directory, track).Frames, asset.FrameCount ?? 0, layer.PaddingPercent, layer.HoldFrames, RedactionCover.Cuts(session.Directory, asset));
            if (cover.Boxes.Count == 0)
            {
                throw new ZeyoCutException($"track '{track.Id}' has no frame where the tracker found the object, so it has no box to make keyframes from");
            }

            var box = RedactionEditor.ToKeyframes(cover, asset);
            layer.Boxes ??= new Dictionary<string, RedactionBox>(StringComparer.Ordinal);
            var replaced = layer.Boxes.ContainsKey(track.Id);
            layer.Boxes[track.Id] = box;
            var lines = new List<string>
            {
                "the times are the footage's own seconds and the values its pixels, like the track's, so the box keeps following trims, speed changes and splits",
                "on every frame the box contains the fail-safe box it was made from (0.5 px bigger on each side); paddingPercent and holdFrames no longer change it",
                $"edit it with set_keyframes or remove_keyframes on layerId \"{layer.Id}\", property \"{track.Id}.x\", \"{track.Id}.y\", \"{track.Id}.width\" or \"{track.Id}.height\" (time in the footage's seconds); clear_keyframes property \"{track.Id}\" goes back to the box worked out from the track",
            };
            if (replaced)
            {
                lines.Insert(0, "it replaces the keyframes the box had");
            }

            var first = cover.Boxes[0].Frame;
            var last = cover.Boxes[^1].Frame;
            return $"made {track.Id}'s box on '{layer.Id}' keyframes: x {box.X.Keyframes.Count}, y {box.Y.Keyframes.Count}, width {box.Width.Keyframes.Count}, height {box.Height.Keyframes.Count} over frames {first}–{last} of '{asset.Id}' (source {Num(VideoTiming.TimeOfFrame(asset, first))}–{Num(VideoTiming.TimeOfFrame(asset, last))}s; {Plays(p, asset, first, last)})"
                + string.Concat(lines.Select(l => "\n- " + l));
        });
    });

    [McpServerTool(Name = "redaction_report")]
    [Description("Check how well the redactions hide their tracks; changes nothing. For each track: the redaction that hides it, the frames its box covers, how many come from a detection, how many the fail-safe holds between detections or before and after them, and how many frames the track spans that no box covers; its confidence and box sizes. Then the spans to look at, with their times on the timeline (only parts of the footage some clip plays): frames not covered; confidence under 0.5 (a tracker drifting, or a weak detection); no detection for 0.5 s or more, where the box only moves straight between detections; fast motion (the box moving a quarter of its size or more a frame); boxes under 12 px, where padding adds little; and footage that plays before a track's box starts or after it is let go while the box is inside the frame (more than 2% of the frame's size from every edge), where the face, plate or object may still show uncovered. Tracks no redaction hides are listed too. A redaction works in every clip that plays its footage, so no clip is left out. Look at each span with render_frame (debug=true draws each track's box).")]
    public static string RedactionReport(
        EditorState state,
        [Description("Track ids to report on, or [\"all\"]. Default: every track.")] string[]? trackIds = null) => Guard(() =>
    {
        var session = state.Session;
        return session.Read(p =>
        {
            if (p.Tracks.Count == 0)
            {
                return "no tracks to report on: track_object follows an object from a box, and detect_and_track finds faces or plates";
            }

            var tracks = RedactionEditor.Tracks(p, trackIds is { Length: > 0 } ? trackIds : ["all"]);
            var reports = tracks.Select(t => RedactionRisks.Of(p, t, TrackFiles.Load(session.Directory, t), p.FindAsset(t.Asset) is { } footage ? RedactionCover.Cuts(session.Directory, footage) : null)).ToList();
            var lines = new List<string>();
            foreach (var asset in reports.Select(r => r.Asset).DistinctBy(a => a.Id))
            {
                var clips = RedactionEditor.Clips(p, asset.Id);
                lines.Add(clips.Count == 0
                    ? $"no clip plays '{asset.Id}' now, so nothing of it shows"
                    : $"'{asset.Id}' plays in {string.Join(", ", clips.Select(c => $"'{c.Id}'"))}: a redaction hides its tracks in every clip that plays it");
            }

            lines.AddRange(reports.Select(r => Summary(p, r)));
            var checks = reports.SelectMany(r => r.Risks.Select(risk => $"{r.Track.Id}, {Times(p, r.Asset, risk.First, risk.Last)}: {Risk(r, risk)}")).ToList();
            var hidden = reports.Count(r => r.Hidden);
            var head = $"redaction report: {Count(reports.Count, "track")}, {hidden} hidden; {(checks.Count == 0 ? "nothing" : Count(checks.Count, "span"))} to check";
            var text = head + string.Concat(lines.Select(l => "\n- " + l));
            return checks.Count == 0
                ? text + $"\n- nothing to check: no frame left uncovered, no confidence under {Num(RedactionRisks.LowConfidence)}, no {Num(RedactionRisks.LongHoldSeconds)} s without a detection, no fast motion, no box under {Num(RedactionRisks.SmallBox)} px, and no footage playing before or after a track away from the frame's edge"
                : text + "\nto check (render_frame with debug=true draws each track's box):" + string.Concat(checks.Select(c => "\n- " + c));
        });
    });

    private static string Summary(Project project, TrackReport report)
    {
        var id = report.Track.Id;
        var asset = report.Asset;
        var found = report.Found;
        if (found.Count == 0)
        {
            return $"{id}: the tracker found the object on none of its frames, so nothing hides it; track it again with track_object";
        }

        var average = Num(Math.Round(found.Average(f => f.Confidence), 2));
        var lowest = Num(Math.Round(found.Min(f => f.Confidence), 2));
        var smallest = Num(Math.Round(found.Min(RedactionRisks.Side)));
        var largest = Num(Math.Round(found.Max(RedactionRisks.Side)));
        var stats = (average == lowest ? $"confidence {average}" : $"confidence {average} on average, {lowest} at the lowest")
            + (smallest == largest ? $"; box {smallest} px" : $"; boxes {smallest}–{largest} px");
        if (report.Hidden)
        {
            var by = $"hidden by '{report.Layer!.Id}'{(report.Keyed ? " with its keyframed box" : "")}";
            return $"{id}, {by}: frames {report.CoverFirst}–{report.CoverLast} of '{asset.Id}' covered ({Times(project, asset, report.CoverFirst, report.CoverLast)}): {report.Detected} detected, {report.HeldBetween} held between detections, {report.HeldAtEnds} held before or after, {report.Uncovered} uncovered; {stats}";
        }

        var why = report.Layer is null
            ? $"apply_redaction trackIds=[\"{id}\"] hides it"
            : $"its redaction '{report.Layer.Id}' is switched off; update_layer with visible=true turns it on";
        return $"{id}, not hidden: found on frames {found[0].Frame}–{found[^1].Frame} of '{asset.Id}' ({Times(project, asset, found[0].Frame, found[^1].Frame)}); {stats}; {why}";
    }

    private static string Risk(TrackReport report, RiskSpan risk)
    {
        var thing = report.Track.Method switch
        {
            TrackMethod.Faces => "face",
            TrackMethod.Plates => "plate",
            _ => "object",
        };
        var layer = report.Layer ?? new RedactionLayer();
        var found = report.Track.Method is TrackMethod.Faces or TrackMethod.Plates;
        var cut = PastCut(report, layer, risk);
        return risk.Kind switch
        {
            RiskKind.Uncovered => report.Keyed
                ? $"not covered: it is outside the keyframed box; run tracks_to_keyframes again, or clear_keyframes with property \"{report.Track.Id}\" to go back to the box worked out from the track"
                : cut
                    ? $"not covered: the tracker lost the {thing} past a scene cut that analyze_video found, where the box stops being held; if the {thing} is in that shot too, track it there with track_object"
                    : $"not covered: the tracker lost the {thing} more than holdFrames ({layer.HoldFrames}) frames from where it found it; correct the track with reanchor_track, or raise holdFrames",
            RiskKind.LowConfidence => found
                ? $"confidence down to {Num(Math.Round(risk.Value, 2))}: weak detections, which may not be a {thing} or only part of one"
                : $"confidence down to {Num(Math.Round(risk.Value, 2))}: the box may have drifted off the {thing}; reanchor_track corrects it",
            RiskKind.LongHold => $"no detection for {Num(risk.Value)} frames: the box only moves straight from the detection before to the one after, growing as it goes; check that the {thing} stays inside it",
            RiskKind.FastMotion => $"the box moves up to {Num(Math.Round(risk.Value, 2))} of its size a frame: fast motion, or part of the {thing} hidden; check that the box keeps up",
            RiskKind.SmallBox => $"the box is only {Num(Math.Round(risk.Value))} px across, so padding {Num(layer.PaddingPercent)}% adds about {Num(Math.Round(risk.Value * layer.PaddingPercent / 100, 1))} px a side and a box a few pixels off leaves an edge showing; raise paddingPercent",
            RiskKind.BeforeStart => $"plays before the box starts{(cut ? $" (at a scene cut that analyze_video found, {Count(report.Found[0].Frame - report.CoverFirst, "frame")} before the first detection)" : "")}, and the first box is inside the frame, {Num(Math.Round(risk.Value))} px from its nearest edge, so the {thing} may already be there uncovered; if it is, track it there with track_object{(found ? " or detect_and_track" : "")}",
            _ => $"plays after the box is let go{(cut ? $" (at a scene cut that analyze_video found, {Count(report.CoverLast - report.Found[^1].Frame, "frame")} after the last detection)" : report.Hidden ? $" ({layer.HoldFrames} frames after the last detection)" : "")}, and the last box is inside the frame, {Num(Math.Round(risk.Value))} px from its nearest edge, so the {thing} may still be there uncovered; if it is, track it there with track_object{(found ? " or detect_and_track" : "")}",
        };
    }

    // Whether a scene cut ended the box's hold on the span's side: the box was held for less than
    // holdFrames there, which only a cut does when footage plays past the hold.
    private static bool PastCut(TrackReport report, RedactionLayer layer, RiskSpan risk) =>
        !report.Keyed && report.Found.Count > 0 && report.CoverLast >= 0
        && ((risk.First > report.CoverLast && report.CoverLast - report.Found[^1].Frame < layer.HoldFrames)
            || (risk.Last < report.CoverFirst && report.Found[0].Frame - report.CoverFirst < layer.HoldFrames));

    // "1.5–1.583s on 'guest', 4s on 'replay'": where frames first-last of the footage play.
    private static string Times(Project project, Asset asset, int first, int last)
    {
        var spans = new List<string>();
        foreach (var clip in RedactionEditor.Clips(project, asset.Id).OrderBy(c => c.Start))
        {
            var (shownFirst, shownLast) = TrackEditor.Frames(clip, asset);
            var from = Math.Max(first, shownFirst);
            var to = Math.Min(last, shownLast);
            if (from <= to)
            {
                var a = TrackTools.Timeline(clip, asset, from);
                var b = TrackTools.Timeline(clip, asset, to);
                spans.Add(from == to ? $"{Num(a)}s on '{clip.Id}'" : $"{Num(Math.Min(a, b))}–{Num(Math.Max(a, b))}s on '{clip.Id}'");
            }
        }

        return spans.Count > 0
            ? string.Join(", ", spans)
            : $"source {Num(VideoTiming.TimeOfFrame(asset, first))}–{Num(VideoTiming.TimeOfFrame(asset, last))}s, which no clip shows";
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Describe(Project project, string directory, RedactionChange change)
    {
        var layer = change.Layer;
        var tracks = layer.Tracks.Select(id => project.FindTrack(id)!).ToList();
        var how = layer.Style switch
        {
            RedactionStyle.Pixelate => $"pixelate, strength {Num(layer.Strength)} ({Num(RedactionEditor.BlocksAcross(layer.Strength))} blocks across each box)",
            RedactionStyle.Blur => $"blur, strength {Num(layer.Strength)} (a blur radius of 1/{Num(RedactionEditor.BlocksAcross(layer.Strength))} of each box's long side)",
            _ => $"solid {layer.Color}",
        };
        var lines = new List<string>();
        var footage = tracks.Select(t => t.Asset).Distinct(StringComparer.Ordinal).Select(asset =>
        {
            var clips = RedactionEditor.Clips(project, asset);
            return clips.Count == 0 ? $"no clip plays '{asset}' now" : $"'{asset}' plays in {string.Join(", ", clips.Select(c => $"'{c.Id}'"))}";
        });
        lines.Add($"it hides them on the video frame before a clip is drawn, in every clip that plays the footage ({string.Join("; ", footage)}), so moves, effects, the camera and subject cutouts all show it hidden");
        foreach (var track in tracks)
        {
            var asset = project.FindAsset(track.Asset)!;
            if (layer.Boxes is not null && layer.Boxes.TryGetValue(track.Id, out var keyed))
            {
                var keys = new[] { keyed.X, keyed.Y, keyed.Width, keyed.Height }.Where(v => v.Keyframes.Count > 0).ToList();
                lines.Add($"{track.Id}: its keyframed box, source {Num(keys.Min(v => v.Keyframes[0].Time))}–{Num(keys.Max(v => v.Keyframes[^1].Time))}s of '{asset.Id}'");
                continue;
            }

            var data = TrackFiles.Load(directory, track);
            var cover = RedactionCover.Of(data.Frames, asset.FrameCount ?? 0, layer.PaddingPercent, layer.HoldFrames, RedactionCover.Cuts(directory, asset));
            if (cover.Boxes.Count == 0)
            {
                lines.Add($"{track.Id}: not covered at all: the tracker found the object on none of its frames; track it again with track_object");
                continue;
            }

            var count = (CoverKind kind) => cover.Boxes.Count(b => b.Kind == kind);
            lines.Add($"{track.Id}: frames {cover.First}–{cover.Last} of '{asset.Id}' covered ({Plays(project, asset, cover.First, cover.Last)}): {count(CoverKind.Detected)} detected, {count(CoverKind.Gap)} filled between detections, {count(CoverKind.Held)} held");
            var cuts = new[] { cover.CutBefore, cover.CutAfter }.OfType<int>().ToList();
            if (cuts.Count > 0)
            {
                lines.Add($"{track.Id}: the box isn't held across the scene cut{(cuts.Count == 1 ? "" : "s")} that analyze_video found at {string.Join(" and ", cuts.Select(c => Times(project, asset, c, c)))}");
            }

            var uncovered = Spans(data.Frames.Select(f => f.Frame).Where(f => cover.At(f) is null).ToList());
            if (uncovered.Count > 0)
            {
                lines.Add($"{track.Id}: frames {string.Join(", ", uncovered.Select(s => s.First == s.Last ? $"{s.First}" : $"{s.First}–{s.Last}"))} aren't covered: the tracker lost the object there, more than holdFrames ({layer.HoldFrames}) frames from where it found it{(cuts.Count > 0 ? " or past a scene cut" : "")}; correct the track with reanchor_track, or raise holdFrames");
            }
        }

        lines.AddRange(change.Notes);
        lines.Add("check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand");
        var verb = change.Created ? "hid" : "changed how";
        var what = change.Created ? $"{Ids.List(layer.Tracks)} with '{layer.Id}'" : $"'{layer.Id}' hides {Ids.List(layer.Tracks)}";
        return $"{verb} {what}: {how}, padding {Num(layer.PaddingPercent)}%, feather {Num(layer.Feather)} px, held {layer.HoldFrames} frames before and after"
            + string.Concat(lines.Select(l => "\n- " + l));
    }

    // Where frames first-last of the footage play: "0–1.958s on 'guest', 2–5.958s on 'guest_2'".
    internal static string Plays(Project project, Asset asset, int first, int last)
    {
        var spans = new List<string>();
        foreach (var clip in RedactionEditor.Clips(project, asset.Id).OrderBy(c => c.Start))
        {
            var (shownFirst, shownLast) = TrackEditor.Frames(clip, asset);
            var from = Math.Max(first, shownFirst);
            var to = Math.Min(last, shownLast);
            if (from <= to)
            {
                spans.Add($"{Num(TrackTools.Timeline(clip, asset, from))}–{Num(TrackTools.Timeline(clip, asset, to))}s on '{clip.Id}'");
            }
        }

        return spans.Count > 0
            ? string.Join(", ", spans)
            : $"source {Num(VideoTiming.TimeOfFrame(asset, first))}–{Num(VideoTiming.TimeOfFrame(asset, last))}s, which no clip shows";
    }

    private static List<(int First, int Last)> Spans(List<int> frames)
    {
        var spans = new List<(int First, int Last)>();
        foreach (var frame in frames.Order())
        {
            if (spans.Count > 0 && spans[^1].Last == frame - 1)
            {
                spans[^1] = (spans[^1].First, frame);
            }
            else
            {
                spans.Add((frame, frame));
            }
        }

        return spans;
    }
}
