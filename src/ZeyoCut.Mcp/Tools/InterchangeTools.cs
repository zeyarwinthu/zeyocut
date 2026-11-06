// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class InterchangeTools
{
    // One prerender job at a time, so two never write the same folder's prerender.json.
    private static readonly SemaphoreSlim PrerenderGate = new(1, 1);

    internal static string PrerenderFolder(string projectDirectory, Project project) =>
        Path.Combine(projectDirectory, ProjectStore.ExportsFolder, $"{ProjectTools.SafeFolderName(project.Name)}_prerender");

    [McpServerTool(Name = "prerender_layers")]
    [Description("Render layers alone, each with its children, on a transparent background as ProRes 4444 .mov files with alpha in exports/<project>_prerender/<layer id>.mov: every frame the layer is on, as render_frame draws it with layerId and a transparent background. They are for an editor that opens the timeline of export_otio or export_fcpxml, which put each prerendered layer on a track above the clips, so its text, keyframed motion, effects and camera move carry over. A layer whose prerender is current (nothing it shows has changed) isn't drawn again. Runs in the background and returns a jobId (prerender_N) for get_job_status.")]
    public static string PrerenderLayers(
        EditorState state,
        [Description("Layer ids; each is drawn with its children.")] string[] layerIds) => Guard(() =>
    {
        var ids = (layerIds ?? []).Select(id => id?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            throw new ZeyoCutException("layerIds is empty; give the ids of the layers to prerender");
        }

        var session = state.Session;
        var project = session.Snapshot();
        var directory = session.Directory;
        ProjectValidator.ThrowIfInvalid(project);
        MatteFiles.ThrowIfPending(project, directory);
        var plans = ids.Select(id =>
        {
            var (root, only) = Prerender.Choose(project, id);
            var (first, count) = Prerender.Span(project, root);
            if (count == 0)
            {
                throw new ZeyoCutException($"layer '{root.Id}' is on no frame of the timeline (it starts at {Num(root.Start)}s and the project ends at {Num(project.Duration)}s)");
            }

            return (root.Id, Only: only, First: first, Count: count);
        }).ToList();
        var warnings = plans
            .SelectMany(p => plans.Where(o => o.Id != p.Id && o.Only.Contains(p.Id)).Select(o => $"'{p.Id}' is a child of '{o.Id}', so it is in that prerender too"))
            .ToList();
        var ffmpeg = state.RequireFfmpeg();
        var folder = PrerenderFolder(directory, project);
        var frames = plans.Sum(p => p.Count);
        var job = state.Jobs.Start("prerender", $"prerender_layers {string.Join(", ", ids)}", (job, ct) =>
        {
            if (!PrerenderGate.Wait(0, ct))
            {
                job.Report(0, "waiting for another prerender job to finish");
                PrerenderGate.Wait(ct);
            }

            try
            {
                var results = new List<PrerenderResult>();
                var done = 0;
                foreach (var plan in plans)
                {
                    var before = done;
                    job.Report(before / (double)frames, $"prerendering '{plan.Id}'");
                    results.Add(Prerender.Make(project, directory, folder, plan.Id, ffmpeg, (i, _) => job.Report((before + i) / (double)frames), cancellationToken: ct));
                    done += plan.Count;
                }

                return Describe(results, folder);
            }
            finally
            {
                PrerenderGate.Release();
            }
        });
        var lines = plans.Select(p => $"- '{p.Id}': {Frames(p.First, p.Count, project.Fps)} -> {Prerender.FileName(p.Id)}");
        return WithWarnings($"prerendering {plans.Count} layer(s) as {Prerender.Format}, {project.Width}x{project.Height} @ {project.Fps} fps, to {folder} in the background as {job.Id}:\n{string.Join("\n", lines)}\nPoll get_job_status with jobId \"{job.Id}\".", warnings);
    });

    private static string Describe(List<PrerenderResult> results, string folder)
    {
        var lines = results.Select(r =>
        {
            var layer = r.Layer;
            var size = ExportSummary.FileSize(r.Bytes);
            return r.Reused
                ? $"- '{layer.LayerId}': already current, {Frames(layer.FirstFrame, layer.FrameCount, layer.Fps)}, {size}: {layer.File}"
                : $"- '{layer.LayerId}': {Frames(layer.FirstFrame, layer.FrameCount, layer.Fps)}, {size}, in {r.Took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s: {layer.File}";
        });
        var made = results.Count(r => !r.Reused);
        return WithWarnings(
            $"prerendered {results.Count} layer(s) as {Prerender.Format} to {folder}{(made < results.Count ? $" ({results.Count - made} already current)" : "")}; export_otio and export_fcpxml put them on tracks above the clips:\n{string.Join("\n", lines)}",
            results.SelectMany(r => r.Warnings).Distinct());
    }

    [McpServerTool(Name = "export_otio")]
    [Description("Write the timeline as OpenTimelineIO (.otio) to exports/<fileName>, for another editor such as DaVinci Resolve: each video clip's picture on video tracks (V1 at the bottom, a clip above the clips drawn before it that it overlaps), the layers prerendered by prerender_layers on tracks above them, the sound the export mixes on audio tracks, and the markers. Clips keep their timing, trims and speed and point at the files in the project's assets folder. The reply lists what the timeline can't hold: keyframes, effects, the camera, the place and size of clips that don't fill the frame, text and other layers that weren't prerendered (or changed since), volume, fades, ducking and loudness.")]
    public static string ExportOtio(
        EditorState state,
        [Description("Output file name, e.g. \"edit.otio\" (no folders). Default <project>.otio.")] string? fileName = null) => Guard(() =>
    {
        var session = state.Session;
        var project = session.Snapshot();
        var output = OutputPath(session.Directory, project, fileName, ".otio", "export_otio");
        var plan = EditPlan.Make(project, session.Directory, PrerenderFolder(session.Directory, project));
        OtioExport.Write(plan, output);
        return Summary(plan, output, "OpenTimelineIO");
    });

    [McpServerTool(Name = "export_fcpxml")]
    [Description("Write the timeline as Final Cut Pro XML (FCPXML 1.9, which DaVinci Resolve also reads) to exports/<fileName>: the first video track as the primary storyline with gaps through to the end, and every other clip connected above it (video, then the layers prerendered by prerender_layers) or below it (the sound the export mixes), with their timing, trims and speed in whole frames, and the markers (chapters as chapter markers). Clips point at the files in the project's assets folder. The reply lists what the timeline can't hold, as export_otio's does.")]
    public static string ExportFcpxml(
        EditorState state,
        [Description("Output file name, e.g. \"edit.fcpxml\" (no folders). Default <project>.fcpxml.")] string? fileName = null) => Guard(() =>
    {
        var session = state.Session;
        var project = session.Snapshot();
        var output = OutputPath(session.Directory, project, fileName, ".fcpxml", "export_fcpxml");
        var plan = EditPlan.Make(project, session.Directory, PrerenderFolder(session.Directory, project));
        FcpxmlExport.Write(plan, output);
        return Summary(plan, output, $"FCPXML {FcpxmlExport.Version}");
    });

    private static string OutputPath(string directory, Project project, string? fileName, string extension, string tool)
    {
        var name = $"{ProjectTools.SafeFolderName(project.Name)}{extension}";
        if (fileName is not null)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ZeyoCutException($"fileName '{fileName}' must be a plain file name like \"edit{extension}\" without folders");
            }

            name = fileName.Trim();
            var given = Path.GetExtension(name);
            if (given.Length == 0)
            {
                name += extension;
            }
            else if (!given.Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ZeyoCutException($"{tool} writes {extension} files (got '{given}')");
            }
        }

        var output = Path.Combine(directory, ProjectStore.ExportsFolder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        return output;
    }

    // What was written: the tracks with their clips, then what the timeline can't hold.
    private static string Summary(EditPlan plan, string output, string format)
    {
        var project = plan.Project;
        var video = plan.Tracks.Count(t => !t.Audio && !t.Prerendered);
        var prerendered = plan.Tracks.Count(t => t.Prerendered);
        var audio = plan.Tracks.Count(t => t.Audio);
        var text = new StringBuilder($"wrote {output} ({format}): {project.Width}x{project.Height} @ {project.Fps} fps, {Num(FrameMath.RenderedDuration(project))}s; "
            + $"{video} video track(s){(prerendered > 0 ? $", {prerendered} of prerendered layers" : "")}, {audio} audio track(s), {plan.Markers.Count} marker(s)");
        foreach (var track in plan.Tracks)
        {
            var clips = track.Clips.Take(6).Select(c => $"'{c.LayerId}' {Num(c.Start)}–{Num(c.Start + c.Length)}s{(Math.Abs(c.Speed - 1) > 1e-9 ? $" ({Num(c.Speed)}x)" : "")}");
            var more = track.Clips.Count > 6 ? $" and {track.Clips.Count - 6} more" : "";
            text.Append($"\n- {track.Name}{(track.Prerendered ? ", prerendered" : track.Audio ? ", sound" : "")}: {string.Join(", ", clips)}{more}");
        }

        if (plan.Tracks.Count == 0)
        {
            text.Append("\n(no clips: add_video_layer, add_audio_layer or prerender_layers put things on it)");
        }

        if (plan.Stale.Count > 0)
        {
            text.Append($"\nprerendered, but changed since, so left out: {string.Join(", ", plan.Stale.Select(s => $"'{s}'"))}; prerender_layers draws them again");
        }

        if (plan.Missing.Count > 0)
        {
            text.Append("\nnot in the timeline:\n- " + string.Join("\n- ", plan.Missing));
        }

        return text.ToString();
    }

    private static string Frames(int first, int count, int fps) =>
        $"frames {first}–{first + count - 1} ({Num(FrameMath.TimeOf(first, fps))}–{Num(FrameMath.TimeOf(first + count, fps))}s)";
}
