// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class ExportTools
{
    [McpServerTool(Name = "start_export")]
    [Description("Start exporting the open project to exports/<fileName> as H.264 MP4 (yuv420p) with all audio layers mixed: music ducked under voice and the mix loudness-normalized (two-pass loudnorm, -14 LUFS by default; see update_project). Runs in the background and returns a jobId; poll get_export_status. Later edits don't affect a running export. preset youtube_1080p or shorts_1080p encodes for upload (H.264 high profile, 2 B-frames, keyframe every half second) at the preset size; the canvas must have the preset's aspect ratio.")]
    public static string StartExport(
        EditorState state,
        ILoggerFactory loggers,
        [Description("Output file name, e.g. \"jump.mp4\" (no folders).")] string fileName,
        [Description("Quality 0-51, lower is better. Default 18 (visually lossless).")] int? crf = null,
        [Description("youtube_1080p or shorts_1080p.")] string? preset = null,
        [Description("Language version to export (en, ja, my, pt-BR): layers of other languages are left out and clips whose sound is in another language are muted. Default: every layer.")] string? language = null) => Guard(() =>
    {
        var chosen = string.IsNullOrWhiteSpace(preset) ? null : Presets.Get(preset);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ZeyoCutException($"fileName '{fileName}' must be a plain file name like \"jump.mp4\" without folders");
        }

        var name = fileName.Trim();
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            name += ".mp4";
        }
        else if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ZeyoCutException($"only .mp4 export is supported (got '{extension}')");
        }

        var ffmpeg = state.RequireFfmpeg();
        var session = state.Session;
        var project = session.Snapshot();
        var version = Languages.Choose(project, language, export: true);
        var output = Path.Combine(session.Directory, ProjectStore.ExportsFolder, name);
        var logger = loggers.CreateLogger("ZeyoCut.Export");
        Exporter.CheckPreset(project, chosen);
        var job = state.Exports.Start(project, session.Directory, output, crf ?? chosen?.Crf ?? 18, ffmpeg, line => logger.LogInformation("{Line}", line), chosen);
        logger.LogInformation("started {Job} -> {Output}", job.Id, output);
        var size = chosen is null ? $"{project.Width}x{project.Height}" : $"{chosen.Width}x{chosen.Height}, preset {chosen.Name}";
        return WithWarnings($"started {job.Id}: {FrameMath.FrameCount(project)} frames ({size} @ {project.Fps} fps{version.Note}) -> {output}\npoll get_export_status with jobId \"{job.Id}\"", version.Warnings);
    });

    [McpServerTool(Name = "get_export_status")]
    [Description("Progress of an export job; when finished, the output path, duration, resolution, fps, codecs and file size.")]
    public static string GetExportStatus(
        EditorState state,
        [Description("The jobId returned by start_export.")] string jobId) => Guard(() => Describe(state.Exports.Get(jobId)));

    [McpServerTool(Name = "report_render_stats")]
    [Description("Where a finished export's time went: per stage (evaluating keyframes, decoding video and images, redaction, background removal and cutouts, text, effects and masks, drawing layers, fitting the canvas into a preset, waiting for the encoder, measuring the loudness), the slowest layers and the slowest frames; times under 5 ms are left out. jobId is a start_export (export_N), export_batch (batch_N) or export_language_versions (versions_N) job; default: the newest finished one. Read-only; use it to find what makes an export slow.")]
    public static string ReportRenderStats(
        EditorState state,
        [Description("export_N, batch_N or versions_N. Default: the newest finished export.")] string? jobId = null) => Guard(() =>
    {
        var id = jobId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            id = FinishedExports(state).FirstOrDefault()
                ?? throw new ZeyoCutException("no export has finished yet; report_render_stats reports a finished start_export, export_batch or export_language_versions");
        }

        if (state.Exports.TryGet(id, out var export))
        {
            return export!.State switch
            {
                ExportState.Running => throw new ZeyoCutException($"{id} is still running ({Num(export.Percent)}%); its stats are ready when it finishes"),
                ExportState.Completed => StatsOf(id, export.Result!),
                _ => throw new ZeyoCutException($"{id} {EnumNames.ToName(export.State)}, so it has no stats: {export.Error}"),
            };
        }

        if (state.Jobs.TryGet(id, out var job))
        {
            if (job!.Kind is not ("batch" or "versions"))
            {
                throw new ZeyoCutException($"{id} isn't an export; report_render_stats reports start_export (export_N), export_batch (batch_N) and export_language_versions (versions_N) jobs");
            }

            if (job.State == JobState.Running)
            {
                throw new ZeyoCutException($"{id} is still running ({job.Percent}%); its stats are ready when it finishes");
            }

            var files = job.Exports ?? [];
            if (job.State != JobState.Completed && files.Count == 0)
            {
                throw new ZeyoCutException($"{id} {EnumNames.ToName(job.State)} before it finished a file, so it has no stats: {job.Error}");
            }

            var builder = new StringBuilder($"{id}: {files.Count} file(s), {job.Description}");
            if (job.State != JobState.Completed)
            {
                builder.Append($"\nthe job {EnumNames.ToName(job.State)} after these: {job.Error}");
            }

            foreach (var file in files)
            {
                builder.Append('\n').Append(StatsOf(null, file));
            }

            return builder.ToString();
        }

        var finished = FinishedExports(state).ToList();
        throw new ZeyoCutException($"export job '{id}' not found; finished exports: {(finished.Count == 0 ? "(none)" : string.Join(", ", finished))}");
    });

    // Stages and layers that took less are left out of report_render_stats.
    private const double ShownSeconds = 0.005;

    private static readonly (string Key, string Label)[] StageLabels =
    [
        ("scene", "evaluating keyframes and transforms"),
        ("decode", "decoding video and images"),
        ("redaction", "redaction"),
        ("matte", "background removal and cutouts"),
        ("text", "text"),
        ("effects", "effects and masks"),
        ("compositing", "drawing layers onto the frame"),
        ("fit", "fitting the canvas into the preset"),
        ("encode", "waiting for the encoder"),
    ];

    // The finished exports of this session and earlier ones, newest first.
    private static IEnumerable<string> FinishedExports(EditorState state) =>
        state.Exports.All.Concat(state.Exports.Earlier)
            .Where(j => j.State == ExportState.Completed)
            .Select(j => (j.Id, Finished: j.FinishedUtc ?? j.StartedUtc))
            .Concat(state.Jobs.All.Concat(state.Jobs.Earlier)
                .Where(j => j.Kind is "batch" or "versions" && j.State == JobState.Completed)
                .Select(j => (j.Id, Finished: j.FinishedUtc ?? j.StartedUtc)))
            .OrderByDescending(j => j.Finished)
            .Select(j => j.Id);

    // One file's times: the rendering stages from the slowest, what came before and after the rendering,
    // then the slowest layers and frames.
    private static string StatsOf(string? id, ExportResult result)
    {
        var name = Path.GetFileName(result.OutputPath);
        var total = result.Elapsed.TotalSeconds;
        var builder = new StringBuilder(id is null ? name : $"{id} ({name})");
        builder.Append($": {result.FrameCount} frames at {result.Media.Width}x{result.Media.Height} in {Num(Math.Round(total, 2))}s");
        if (result.Times is not { } times)
        {
            return builder.Append("; no stats: it was exported before ZeyoCut kept them").ToString();
        }

        if (total > 0)
        {
            builder.Append($", {Num(Math.Round(result.FrameCount / total, 1))} frames a second");
        }

        var rendered = times.Chunks - times.ReusedChunks;
        if (times.Chunks > 0)
        {
            builder.Append($"; {times.Chunks} chunk(s) of {Num(Exporter.ChunkSeconds)}s");
            builder.Append(rendered > 0 ? $", {rendered} rendered on {times.Workers} thread(s)" : "");
            builder.Append(times.ReusedChunks > 0 ? $", {times.ReusedChunks} from the cache" : "");
        }

        var stages = StageLabels.Select(s => (s.Label, Seconds: times.Stages.GetValueOrDefault(s.Key))).ToList();
        var rendering = stages.Sum(s => s.Seconds);
        if (rendering < ShownSeconds)
        {
            builder.Append(rendered == 0 && times.Chunks > 0 ? "\nrendering: none, every chunk came from the cache" : "\nrendering: under 5 ms");
        }
        else
        {
            builder.Append($"\nrendering, {Num(Math.Round(rendering, 3))}s{(times.Workers > 1 ? $" added up over the {times.Workers} threads, which took {Num(Math.Round(times.RenderSeconds, 3))}s" : "")}:");
            foreach (var (label, seconds) in stages.Where(s => s.Seconds >= ShownSeconds).OrderByDescending(s => s.Seconds))
            {
                var share = 100 * seconds / rendering;
                builder.Append($"\n- {label}: {Num(Math.Round(seconds, 3))}s ({(share < 1 ? "<1" : Num(Math.Round(share)))}%)");
            }
        }

        List<(string Label, double Seconds)> around =
        [
            ("measuring the loudness of the mix", times.AudioSeconds),
            ("finding the chunks that changed", times.KeySeconds),
            ("joining the chunks and mixing the audio", times.JoinSeconds),
        ];
        around.Add((times.Chunks > 0 ? "the rest (checking the file, trimming the cache)" : "the rest (starting ffmpeg, checking the file)", Math.Max(0, total - times.RenderSeconds - around.Sum(a => a.Seconds))));
        var shown = around.Where(a => a.Seconds >= ShownSeconds).ToList();
        if (shown.Count > 0)
        {
            builder.Append("\nbefore and after the rendering:");
            foreach (var (label, seconds) in shown)
            {
                builder.Append($"\n- {label}: {Num(Math.Round(seconds, 3))}s");
            }
        }

        if (times.EncoderCpuSeconds is { } cpu && cpu > 0)
        {
            builder.Append($"\nthe encoder{(rendered > 1 ? "s" : "")} (ffmpeg) used {Num(Math.Round(cpu, 2))}s of CPU time, running alongside the rendering");
        }

        var layers = times.Layers.Where(l => l.Seconds >= ShownSeconds).ToList();
        if (layers.Count > 0)
        {
            builder.Append("\nslowest layers (each with its own decoding, mattes, effects and drawing):");
            foreach (var layer in layers)
            {
                builder.Append($"\n- {layer.Id} [{layer.Type}]: {Num(Math.Round(layer.Seconds, 3))}s over {layer.Frames} frame(s), {Num(Math.Round(1000 * layer.Seconds / Math.Max(1, layer.Frames), 1))} ms a frame");
            }
        }

        if (times.SlowestFrames.Count > 0)
        {
            builder.Append($"\nslowest frames: {string.Join(", ", times.SlowestFrames.Select(f => $"f{f.Frame} ({Num(f.Milliseconds)} ms)"))}");
        }

        return builder.ToString();
    }

    [McpServerTool(Name = "export_batch")]
    [Description("Export the open project once per preset in one background job, one file after another, to exports/<fileName>_<preset>.mp4 (e.g. launch_youtube_1080p.mp4 and launch_shorts_1080p.mp4). Each file is encoded like start_export with that preset. A preset with another shape than the canvas (shorts_1080p from a landscape canvas, or youtube_1080p from a vertical one) gets the whole canvas fitted in the middle over a blurred, enlarged copy of itself, so nothing is cut off. Returns a jobId (batch_N); get_job_status shows which export is running and lists every finished file.")]
    public static string ExportBatch(
        EditorState state,
        ILoggerFactory loggers,
        [Description("Preset names, e.g. [\"youtube_1080p\", \"shorts_1080p\"].")] string[] presets,
        [Description("Base file name, e.g. \"launch\"; _<preset>.mp4 is added. Default: the project name.")] string? fileName = null) => Guard(() =>
    {
        var chosen = ChoosePresets(presets);
        var session = state.Session;
        var project = session.Snapshot();
        var baseName = BaseName(fileName, project.Name);
        ProjectValidator.ThrowIfInvalid(project);
        MatteFiles.ThrowIfPending(project, session.Directory);
        var ffmpeg = state.RequireFfmpeg();
        var directory = session.Directory;
        var outputs = chosen.Select(p => Path.Combine(directory, ProjectStore.ExportsFolder, $"{baseName}_{p.Name}.mp4")).ToList();
        var logger = loggers.CreateLogger("ZeyoCut.Export");
        var job = state.Jobs.Start(
            "batch",
            $"export_batch {string.Join(" + ", chosen.Select(p => p.Name))}",
            (job, ct) => RunBatch(job, ct, project, directory, chosen, outputs, ffmpeg, logger, state.ChooseEncoder),
            BatchResume(project, directory, chosen, outputs));
        logger.LogInformation("started {Job} -> {Outputs}", job.Id, string.Join(", ", outputs));
        var lines = chosen.Select((p, i) =>
            $"- {p.Name}: {p.Width}x{p.Height}{(Exporter.SameShape(project, p) ? "" : $", the {project.Width}x{project.Height} canvas fitted over a blurred copy")} -> {outputs[i]}");
        var used = Languages.Used(project);
        List<string> mixed = used.Count < 2 ? [] : [$"these exports include the layers of every language ({string.Join(", ", used)}); export_language_versions writes one file per language and preset"];
        return WithWarnings($"started {job.Id}: {chosen.Count} export(s) of {FrameMath.FrameCount(project)} frames @ {project.Fps} fps, one after another\n{string.Join("\n", lines)}\npoll get_job_status with jobId \"{job.Id}\"", mixed);
    });

    [McpServerTool(Name = "export_language_versions")]
    [Description("Export the open project once per language and preset in one background job, one file after another, to exports/<fileName>_<language>_<preset>.mp4 (e.g. launch_ja_shorts_1080p.mp4). Each language's version leaves out the layers tagged with other languages (with their children, such as another language's caption track) and mutes the clips whose sound is in another language; untagged layers are in every version. Each file is encoded like export_batch with that preset: a preset with another shape than the canvas gets the whole canvas fitted over a blurred copy of itself. Returns a jobId (versions_N); get_job_status shows which file is being written and lists every finished one. Like export_batch, it starts again if the server stops while it runs.")]
    public static string ExportLanguageVersions(
        EditorState state,
        ILoggerFactory loggers,
        [Description("Language tags, e.g. [\"en\", \"ja\", \"my\"].")] string[] languages,
        [Description("Preset names, e.g. [\"youtube_1080p\", \"shorts_1080p\"].")] string[] presets,
        [Description("Base file name, e.g. \"launch\"; _<language>_<preset>.mp4 is added. Default: the project name.")] string? fileName = null) => Guard(() =>
    {
        if (languages is null || languages.Length == 0)
        {
            throw new ZeyoCutException("languages must list at least one language, e.g. [\"en\", \"ja\"]");
        }

        var tags = languages.Select(l => Languages.Normalize(l)).ToList();
        var twice = tags.GroupBy(t => t).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
        {
            throw new ZeyoCutException($"language '{twice.Key}' is listed twice");
        }

        var chosen = ChoosePresets(presets);
        var session = state.Session;
        var project = session.Snapshot();
        var baseName = BaseName(fileName, project.Name);
        ProjectValidator.ThrowIfInvalid(project);
        MatteFiles.ThrowIfPending(project, session.Directory);
        var ffmpeg = state.RequireFfmpeg();
        var directory = session.Directory;
        var outputs = tags.SelectMany(t => chosen.Select(p => Path.Combine(directory, ProjectStore.ExportsFolder, $"{baseName}_{t}_{p.Name}.mp4"))).ToList();
        var (versions, items) = VersionItems(project, tags, chosen, outputs);
        var logger = loggers.CreateLogger("ZeyoCut.Export");
        var job = state.Jobs.Start(
            "versions",
            $"export_language_versions {string.Join(" + ", tags)} × {string.Join(" + ", chosen.Select(p => p.Name))}",
            (job, ct) => RunVersions(job, ct, directory, items, ffmpeg, logger, state.ChooseEncoder),
            VersionsResume(project, directory, tags, chosen, outputs));
        logger.LogInformation("started {Job} -> {Outputs}", job.Id, string.Join(", ", outputs));

        var used = Languages.Used(project);
        List<string> warnings = used.Count == 0
            ? ["no layer has a language tag, so every version is the same; tag layers with update_layer language, or add a caption track per language with add_caption_track"]
            : tags.Where(t => !used.Contains(t)).Select(t => $"no layer is tagged {t}, so its version has only the untagged layers; languages in the project: {string.Join(", ", used)}").ToList();
        var lines = versions.Select(v => "- " + v.Note[2..])
            .Concat(items.Select(i => $"- {i.Language} {i.Preset.Name}: {i.Preset.Width}x{i.Preset.Height}{(Exporter.SameShape(project, i.Preset) ? "" : $", the {project.Width}x{project.Height} canvas fitted over a blurred copy")} -> {i.Output}"));
        return WithWarnings($"started {job.Id}: {items.Count} export(s) of {FrameMath.FrameCount(project)} frames @ {project.Fps} fps, one after another\n{string.Join("\n", lines)}\npoll get_job_status with jobId \"{job.Id}\"", warnings);
    });

    [McpServerTool(Name = "export_thumbnail")]
    [Description("Save one frame as a PNG thumbnail in exports/ (default 1280x720, YouTube's thumbnail size) and return a preview of it. Titles and other overlays are ordinary text layers, so add and style them first; a layer that should only be in the thumbnail can start and end around this time. A size with another shape than the canvas is filled by enlarging the frame and cropping the rest evenly from the center. Warns when the PNG is over 2 MB, YouTube's limit.")]
    public static IEnumerable<ContentBlock> ExportThumbnail(
        EditorState state,
        [Description("Time in seconds; snaps to the nearest frame.")] double time,
        [Description("Width in pixels (16-7680). Default 1280.")] int width = 1280,
        [Description("Height in pixels (16-7680). Default 720.")] int height = 720,
        [Description("Output file name, e.g. \"thumb.png\" (no folders). Default thumbnail_<frame>.png.")] string? fileName = null,
        [Description("Language version to show (en, ja, my, pt-BR): layers of other languages are left out. Default: every layer.")] string? language = null) => Guard(() =>
    {
        var session = state.Session;
        var project = session.Snapshot();
        var version = Languages.Choose(project, language, export: true);
        var name = fileName is null ? $"thumbnail_{FrameMath.NearestFrame(project, time):D5}.png" : PngName(fileName);
        var path = Path.Combine(session.Directory, ProjectStore.ExportsFolder, name);
        ThumbnailResult result;
        lock (state.RenderGate)
        {
            result = FrameExport.Thumbnail(state.Renderer, project, time, width, height, path, 960);
        }

        var canvas = $"{project.Width}x{project.Height} canvas";
        var source = (result.Cropped ? $"cropped from the center of the {canvas}" : $"from the {canvas}")
            + (result.Enlarged > 1.0005 ? $", enlarged {Num(result.Enlarged)}x" : "");
        var text = $"thumbnail {result.Width}x{result.Height} of frame {result.Frame} (t={FrameMath.TimeOf(project, result.Frame):0.000}s), {source}; saved {result.Path} ({ExportSummary.FileSize(result.Bytes)}); preview {result.PreviewWidth}x{result.PreviewHeight}{version.Note}";
        return new ContentBlock[]
        {
            new TextContentBlock { Text = WithWarnings(text, version.Warnings.Concat(result.Warnings)) },
            ImageContentBlock.FromBytes(result.Preview, "image/png"),
        };
    });

    [McpServerTool(Name = "export_frames")]
    [Description("Write every frame from startTime to endTime (both included, snapped to frames) as numbered images at the canvas size, exactly as exported, to exports/<project>_frames/frame_<frame>.png, where <frame> is the project frame number in 5 digits (frame_00012.png). Frames an earlier export_frames left in that folder are deleted first, so it holds just this sequence. Runs in the background and returns a jobId (frames_N) for get_job_status.")]
    public static string ExportFrames(
        EditorState state,
        [Description("First time in seconds.")] double startTime,
        [Description("Last time in seconds (included).")] double endTime,
        [Description("png (default) or jpg (quality 95, much smaller files).")] string format = "png") => Guard(() =>
    {
        var chosen = FrameExport.CheckFormat(format);
        var session = state.Session;
        var project = session.Snapshot();
        var (first, last) = FrameExport.Frames(project, startTime, endTime);
        var directory = session.Directory;
        MatteFiles.ThrowIfPending(project, directory);
        var folder = Path.Combine(directory, ProjectStore.ExportsFolder, $"{ProjectTools.SafeFolderName(project.Name)}_frames");
        var job = state.Jobs.Start("frames", $"export_frames f{first}–f{last} as {chosen}", (job, ct) =>
        {
            using var renderer = new FrameRenderer(directory, () => state.RequireFfmpeg().Ffmpeg, streamVideo: true);
            return Describe(FrameExport.Sequence(renderer, project, first, last, chosen, folder, p => job.Report(p, "writing frames"), ct), project);
        });
        var times = $"{Num(FrameMath.TimeOf(project, first))}–{Num(FrameMath.TimeOf(project, last))}s";
        return $"writing {last - first + 1} frame(s) f{first}–f{last} ({times}, {project.Width}x{project.Height} {chosen}) to {folder} in the background as {job.Id}.\nPoll get_job_status with jobId \"{job.Id}\".";
    });

    // Runs again what a restart found unfinished (see EditorState.RecoverJobs): the same
    // presets and files, from the project as it was when export_batch was called.
    internal static Func<BackgroundJob, CancellationToken, string> ResumeBatch(EditorState state, ILogger logger, JsonObject resume)
    {
        var project = ProjectJson.Deserialize(resume["project"]!.ToJsonString());
        var directory = resume["projectDirectory"]!.GetValue<string>();
        var presets = resume["presets"]!.AsArray().Select(p => Presets.Get(p!.GetValue<string>())).ToList();
        var outputs = resume["outputs"]!.AsArray().Select(o => o!.GetValue<string>()).ToList();
        if (presets.Count == 0 || presets.Count != outputs.Count)
        {
            throw new ZeyoCutException("the saved batch doesn't list one file per preset");
        }

        return (job, ct) => RunBatch(job, ct, project, directory, presets, outputs, state.RequireFfmpeg(), logger, state.ChooseEncoder);
    }

    // Runs again what a restart found unfinished: the same languages, presets and files, from the
    // project as it was when export_language_versions was called.
    internal static Func<BackgroundJob, CancellationToken, string> ResumeVersions(EditorState state, ILogger logger, JsonObject resume)
    {
        var project = ProjectJson.Deserialize(resume["project"]!.ToJsonString());
        var directory = resume["projectDirectory"]!.GetValue<string>();
        var languages = resume["languages"]!.AsArray().Select(l => Languages.Normalize(l!.GetValue<string>())).ToList();
        var presets = resume["presets"]!.AsArray().Select(p => Presets.Get(p!.GetValue<string>())).ToList();
        var outputs = resume["outputs"]!.AsArray().Select(o => o!.GetValue<string>()).ToList();
        if (outputs.Count == 0 || languages.Count * presets.Count != outputs.Count)
        {
            throw new ZeyoCutException("the saved export_language_versions job doesn't list one file per language and preset");
        }

        var (_, items) = VersionItems(project, languages, presets, outputs);
        return (job, ct) => RunVersions(job, ct, directory, items, state.RequireFfmpeg(), logger, state.ChooseEncoder);
    }

    private sealed record VersionExport(string Language, Preset Preset, Project Project, string Output);

    // Each language's version of the project, and its files: one per preset, in the order of outputs.
    private static (List<LanguageVersion> Versions, List<VersionExport> Items) VersionItems(Project project, IReadOnlyList<string> languages, IReadOnlyList<Preset> presets, IReadOnlyList<string> outputs)
    {
        var versions = new List<LanguageVersion>();
        var items = new List<VersionExport>();
        foreach (var language in languages)
        {
            var copy = ProjectJson.Clone(project);
            versions.Add(Languages.Select(copy, language));
            ProjectValidator.ThrowIfInvalid(copy);
            foreach (var preset in presets)
            {
                items.Add(new VersionExport(language, preset, copy, outputs[items.Count]));
            }
        }

        return (versions, items);
    }

    private static JsonObject VersionsResume(Project project, string directory, IReadOnlyList<string> languages, IReadOnlyList<Preset> presets, IReadOnlyList<string> outputs) => new()
    {
        ["projectDirectory"] = directory,
        ["languages"] = new JsonArray(languages.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
        ["presets"] = new JsonArray(presets.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()),
        ["outputs"] = new JsonArray(outputs.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()),
        ["project"] = JsonNode.Parse(ProjectJson.Serialize(project)),
    };

    private static string RunVersions(BackgroundJob job, CancellationToken ct, string directory, IReadOnlyList<VersionExport> items, FfmpegPaths ffmpeg, ILogger logger, Func<int, EncoderChoice> encoder)
    {
        var results = new List<ExportResult>();
        job.Exports = results;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var index = i;
            var done = results.Count == 0 ? "" : $"; done: {string.Join(", ", results.Select(r => Path.GetFileName(r.OutputPath)))}";
            job.Report(index / (double)items.Count, $"exporting {item.Language} {item.Preset.Name}, {index + 1} of {items.Count}{done}");
            try
            {
                results.Add(Exporter.Export(
                    item.Project,
                    directory,
                    item.Output,
                    item.Preset.Crf,
                    ffmpeg,
                    (frames, total) => job.Report((index + frames / (double)total) / items.Count),
                    line => logger.LogInformation("{Line}", line),
                    ct,
                    item.Preset,
                    fitShape: true,
                    workers: Exporter.DefaultWorkers,
                    encoder: encoder(item.Preset.Crf)));
            }
            catch (ZeyoCutException ex)
            {
                logger.LogInformation("{Job} {Language} {Preset} failed: {Error}", job.Id, item.Language, item.Preset.Name, ex.Message);
                var finished = results.Count == 0 ? "" : $"\nfinished before it: {string.Join(", ", results.Select(r => r.OutputPath))}";
                throw new ZeyoCutException($"{item.Language} {item.Preset.Name} failed: {ex.Message}{finished}");
            }
        }

        var builder = new StringBuilder($"exported {results.Count} file(s):");
        for (var i = 0; i < results.Count; i++)
        {
            builder.Append($"\n- {items[i].Language} {items[i].Preset.Name}: {ExportSummary.Describe(results[i])}");
            foreach (var note in results[i].Notes ?? [])
            {
                builder.Append($"\n  processing: {note}");
            }
        }

        return WithWarnings(builder.ToString(), results.SelectMany(r => r.Warnings).Distinct());
    }

    // The presets of an export_batch or export_language_versions, each once.
    private static List<Preset> ChoosePresets(string[]? presets)
    {
        if (presets is null || presets.Length == 0)
        {
            throw new ZeyoCutException($"presets must list at least one preset: {Presets.Names}");
        }

        var chosen = presets.Select(Presets.Get).ToList();
        var twice = chosen.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
        {
            throw new ZeyoCutException($"preset '{twice.Key}' is listed twice");
        }

        return chosen;
    }

    private static JsonObject BatchResume(Project project, string directory, IReadOnlyList<Preset> presets, IReadOnlyList<string> outputs) => new()
    {
        ["projectDirectory"] = directory,
        ["presets"] = new JsonArray(presets.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()),
        ["outputs"] = new JsonArray(outputs.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()),
        ["project"] = JsonNode.Parse(ProjectJson.Serialize(project)),
    };

    private static string RunBatch(BackgroundJob job, CancellationToken ct, Project project, string directory, IReadOnlyList<Preset> chosen, IReadOnlyList<string> outputs, FfmpegPaths ffmpeg, ILogger logger, Func<int, EncoderChoice> encoder)
    {
        var results = new List<ExportResult>();
        job.Exports = results;
        for (var i = 0; i < chosen.Count; i++)
        {
            var preset = chosen[i];
            var index = i;
            var done = results.Count == 0 ? "" : $"; done: {string.Join(", ", results.Select(r => Path.GetFileName(r.OutputPath)))}";
            job.Report(index / (double)chosen.Count, $"exporting {preset.Name}, {index + 1} of {chosen.Count}{done}");
            try
            {
                results.Add(Exporter.Export(
                    project,
                    directory,
                    outputs[index],
                    preset.Crf,
                    ffmpeg,
                    (frames, total) => job.Report((index + frames / (double)total) / chosen.Count),
                    line => logger.LogInformation("{Line}", line),
                    ct,
                    preset,
                    fitShape: true,
                    workers: Exporter.DefaultWorkers,
                    encoder: encoder(preset.Crf)));
            }
            catch (ZeyoCutException ex)
            {
                logger.LogInformation("{Job} {Preset} failed: {Error}", job.Id, preset.Name, ex.Message);
                var finished = results.Count == 0 ? "" : $"\nfinished before it: {string.Join(", ", results.Select(r => r.OutputPath))}";
                throw new ZeyoCutException($"{preset.Name} failed: {ex.Message}{finished}");
            }
        }

        return BatchSummary(chosen, results);
    }

    public static string Describe(ExportJob job)
    {
        var elapsed = ((job.FinishedUtc ?? DateTime.UtcNow) - job.StartedUtc).TotalSeconds;
        switch (job.State)
        {
            case ExportState.Running:
                var phase = job.FramesDone >= job.TotalFrames ? "finishing encode" : "rendering";
                var left = job.FramesDone >= job.TotalFrames ? "" : JobTime.LeftText(job.Percent / 100, TimeSpan.FromSeconds(elapsed));
                return $"{job.Id}: running ({phase}) {Num(job.Percent)}% ({job.FramesDone}/{job.TotalFrames} frames), {elapsed:0.0}s elapsed{left}{JobTools.Again(job.Attempt)}";
            case ExportState.Completed:
                var result = job.Result!;
                var media = result.Media;
                var builder = new StringBuilder($"{job.Id}: completed{JobTools.Again(job.Attempt)}\n");
                builder.AppendLine($"output: {result.OutputPath}");
                builder.AppendLine($"duration: {media.Duration:0.000}s");
                builder.AppendLine($"resolution: {media.Width}x{media.Height}");
                builder.AppendLine($"fps: {Num(media.Fps ?? 0)}");
                builder.AppendLine($"video: {media.VideoCodec} {media.PixelFormat}, {result.FrameCount} frames");
                builder.AppendLine(media.HasAudio
                    ? $"audio: {media.AudioCodec} {media.AudioChannels}ch {media.SampleRate} Hz ({result.AudioClips} clip(s) mixed)"
                    : "audio: none");
                foreach (var note in result.Notes ?? [])
                {
                    builder.AppendLine($"processing: {note}");
                }

                builder.AppendLine($"file size: {ExportSummary.FileSize(media.FileSize)} ({media.FileSize} bytes)");
                builder.Append($"time: {elapsed:0.0}s");
                return WithWarnings(builder.ToString(), result.Warnings);
            default:
                return $"{job.Id}: {EnumNames.ToName(job.State)} after {elapsed:0.0}s: {job.Error}";
        }
    }

    private static string BatchSummary(IReadOnlyList<Preset> presets, IReadOnlyList<ExportResult> results)
    {
        var builder = new StringBuilder($"exported {results.Count} file(s):");
        for (var i = 0; i < results.Count; i++)
        {
            builder.Append($"\n- {presets[i].Name}: {ExportSummary.Describe(results[i])}");
            foreach (var note in results[i].Notes ?? [])
            {
                builder.Append($"\n  processing: {note}");
            }
        }

        return WithWarnings(builder.ToString(), results.SelectMany(r => r.Warnings).Distinct());
    }

    private static string Describe(FrameSequenceResult result, Core.Model.Project project)
    {
        var times = $"{Num(FrameMath.TimeOf(project, result.First))}–{Num(FrameMath.TimeOf(project, result.Last))}s";
        var files = result.Count == 1
            ? FrameExport.FileName(result.First, result.Format)
            : $"{FrameExport.FileName(result.First, result.Format)} … {FrameExport.FileName(result.Last, result.Format)}";
        var text = $"wrote {result.Count} {result.Format} frame(s) f{result.First}–f{result.Last} ({times}) at {result.Width}x{result.Height} to {result.Folder}: {files}, {ExportSummary.FileSize(result.Bytes)} in all";
        if (result.Replaced > 0)
        {
            text += $"\ndeleted {result.Replaced} frame(s) an earlier export_frames left there";
        }

        return WithWarnings(text, result.Warnings);
    }

    // A plain file name without folders; a trailing .mp4 is dropped. Without one, the project name.
    private static string BaseName(string? fileName, string projectName)
    {
        if (fileName is null)
        {
            return ProjectTools.SafeFolderName(projectName);
        }

        var name = fileName.Trim();
        if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ZeyoCutException($"fileName '{fileName}' must be a plain file name like \"launch\" without folders");
        }

        return name;
    }

    private static string PngName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ZeyoCutException($"fileName '{fileName}' must be a plain file name like \"thumb.png\" without folders");
        }

        var name = fileName.Trim();
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            return name + ".png";
        }

        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? name
            : throw new ZeyoCutException($"thumbnails are PNG files (got '{extension}')");
    }
}
