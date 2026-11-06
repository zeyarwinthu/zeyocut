// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Server;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class MatteTools
{
    private static readonly object MatteJobGate = new();

    // The models use every CPU core, so matte jobs compute one at a time (downloads don't wait); the
    // system_check benchmark waits for them too.
    internal static readonly SemaphoreSlim ComputeGate = new(1, 1);

    [McpServerTool(Name = "remove_background")]
    [Description("Remove the background of an image or video layer so what is behind it shows through (one undo step; replaces an earlier removal, keeping refine_matte's shrink and feather). Methods: chroma keys out a screen color while drawing (green or blue screen; images and video): color is the screen's color (default: picked from the picture's edges), tolerance 0-1 how far from it still goes (default 0.4), softness 0-1 the soft edge after that (default 0.2), spill 0-1 how much of the screen's tint is taken out of what stays (default 1). edgeFill removes everything connected to the image's border that is close to the background color (art on a plain background; image layers): color is the background (default: the most common border color), tolerance 0-1 (default 0.1); areas of that color enclosed by the art stay. aiImage finds the subject of a photo or drawing with an AI model (BiRefNet; image layers); aiVideo follows a person through a clip with an AI model (Robust Video Matting; video layers). Both take no settings and are computed in the background as a matte_N job (the first use downloads the model, 224 MB or 15 MB); the removal applies at once, but until the job is done the layer is drawn with its background and exports refuse. Mattes are cached next to the asset. Check the edges with render_frame checkerboard=true layerId=<layer>, and fix halos with refine_matte. remove=true takes the removal off.")]
    public static string RemoveBackground(
        EditorState state,
        [Description("Image or video layer id.")] string layerId,
        [Description("chroma, edgeFill, aiImage or aiVideo. Required unless remove=true.")] string? method = null,
        [Description("chroma: the screen color; edgeFill: the background color. #RRGGBB. Default: picked from the picture.")] string? color = null,
        [Description("0-1. Default 0.4 (chroma), 0.1 (edgeFill).")] double? tolerance = null,
        [Description("chroma: 0-1, how soft the edge is. Default 0.2.")] double? softness = null,
        [Description("chroma: 0-1, how much screen tint to take out of the kept pixels. Default 1.")] double? spill = null,
        [Description("Take the background removal off.")] bool remove = false,
        [Description("aiImage or aiVideo: start even if it would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first.")] bool confirmLongJob = false) => Guard(() =>
    {
        var session = state.Session;
        if (remove)
        {
            if (method is not null || color is not null || tolerance is not null || softness is not null || spill is not null)
            {
                throw new ZeyoCutException("remove=true takes no method or settings");
            }

            return session.Mutate($"remove_background {layerId} remove", p => MatteEditor.Remove(p, layerId));
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ZeyoCutException($"method is required: {string.Join(", ", EnumNames.All<MatteMethod>())}");
        }

        var kind = EnumNames.Parse<MatteMethod>(method, "method");
        if (MatteFiles.IsAi(kind))
        {
            return RemoveWithAi(state, layerId, kind, new MatteValues(color, tolerance, softness, spill), confirmLongJob);
        }

        var project = session.Snapshot();
        var layer = MatteEditor.GetMattable(project, layerId);
        var notes = new List<string>();
        Matte matte;
        lock (state.RenderGate)
        {
            var renderer = state.Renderer;
            var picked = kind == MatteMethod.Chroma && color is null ? PickKey(renderer, project, layer, notes) : color;
            matte = MatteEditor.Build(layer, kind, new MatteValues(picked, tolerance, softness, spill));
            notes.AddRange(Preview(renderer, project, layer, matte));
        }

        var summary = session.Mutate($"remove_background {layerId} {EnumNames.ToName(kind)}", p => MatteEditor.Set(p, layerId, matte));
        notes.Add($"check the edges with render_frame checkerboard=true layerId=\"{layer.Id}\"; refine_matte shrinks or softens them");
        return summary + "\n" + string.Join("\n", notes.Select(n => "- " + n));
    });

    [McpServerTool(Name = "refine_matte")]
    [Description("Fix the edges of a removed background (one undo step): shrink pulls the edge in by that many pixels to cut off a halo of the old background (negative grows it back, whole pixels), feather softens it over about that many pixels. Both are in the layer's own pixels; values you leave out stay.")]
    public static string RefineMatte(
        EditorState state,
        [Description("Layer id with a removed background.")] string layerId,
        [Description("Pixels to pull the edge in, -100 to 100 (negative grows). Default: unchanged.")] double? shrink = null,
        [Description("Edge softness in pixels, 0-100. Default: unchanged.")] double? feather = null) => Guard(() =>
    {
        var change = state.Session.Mutate($"refine_matte {layerId}", p => MatteEditor.Refine(p, layerId, shrink, feather));
        return WithWarnings(change.Summary, change.Warnings);
    });

    // aiImage and aiVideo: the removal applies at once and its matte is computed by a matte_N job. A
    // matte that is already computed is used as it is; one that a running job is computing joins it.
    private static string RemoveWithAi(EditorState state, string layerId, MatteMethod kind, MatteValues values, bool confirmLongJob)
    {
        var session = state.Session;
        var project = session.Snapshot();
        var layer = MatteEditor.GetMattable(project, layerId);
        var matte = MatteEditor.Build(layer, kind, values);
        var directory = session.Directory;
        var targets = AiTargets(project, directory, layer, layer, matte);
        if (SystemTools.NotStarted(Estimate(state, kind, targets) is { } estimate ? [estimate] : [], confirmLongJob, "remove_background") is { } notStarted)
        {
            return notStarted;
        }

        var ffmpeg = kind == MatteMethod.AiVideo ? state.RequireFfmpeg() : null;
        var summary = session.Mutate($"remove_background {layerId} {EnumNames.ToName(kind)}", p => MatteEditor.Set(p, layerId, matte));
        var notes = StartAiMatte(state, layer.Id, cutout: false, kind, targets, ffmpeg, directory);
        notes.Add($"check the edges with render_frame checkerboard=true layerId=\"{layer.Id}\"; refine_matte shrinks or softens them");
        return summary + "\n" + string.Join("\n", notes.Select(n => "- " + n));
    }

    // The matte files an AI matte on 'owner' (a layer, or a subject cutout of 'subject') needs; the
    // assets' own files must be there.
    internal static List<MatteTarget> AiTargets(Project project, string directory, IMattable owner, IMattable subject, Matte matte)
    {
        var targets = MatteFiles.Assets(project, owner).Select(a => new MatteTarget(a, MatteFiles.CachePath(directory, a, matte), Label(subject, a))).ToList();
        foreach (var target in targets)
        {
            var file = ProjectStore.AssetPath(directory, target.Asset);
            if (!File.Exists(file))
            {
                throw new ZeyoCutException($"the file of asset '{target.Asset.Id}' is missing: {file}");
            }
        }

        return targets;
    }

    // After the matte was set on 'ownerId': notes which files are computed, joins the jobs computing
    // others and starts a matte_N job for the rest. Returns the reply lines.
    internal static List<string> StartAiMatte(EditorState state, string ownerId, bool cutout, MatteMethod kind, List<MatteTarget> targets, FfmpegPaths? ffmpeg, string directory, string retry = "text_behind_subject")
    {
        var notes = new List<string>();
        var missing = targets.Where(t => !File.Exists(t.Path)).ToList();
        foreach (var target in targets.Except(missing))
        {
            notes.Add($"{target.Label}: the matte is already computed: {MatteFiles.Relative(directory, target.Path)}");
        }

        if (missing.Count == 0)
        {
            return notes;
        }

        var jobs = new List<string>();
        lock (MatteJobGate)
        {
            var running = missing
                .Select(t => (Target: t, Job: state.MatteJobs.TryGetValue(t.Path, out var job) && job.State == JobState.Running ? job : null))
                .Where(r => r.Job is not null)
                .ToList();
            foreach (var group in running.GroupBy(r => r.Job!))
            {
                jobs.Add(group.Key.Id);
                notes.Add($"{string.Join(", ", group.Select(r => r.Target.Label))}: already being computed by {group.Key.Id} ({group.Key.Percent}%)");
            }

            var start = missing.Except(running.Select(r => r.Target)).ToList();
            if (start.Count > 0)
            {
                var model = AiModels.For(kind);
                var download = state.Models.Has(model) ? "" : $"; it first downloads {model.FileName} ({model.Size}, {model.License}) into {state.Models.Folder}";
                var estimate = Estimate(state, kind, start)!.Estimate;
                var job = StartMatteJob(state, ownerId, cutout, kind, start, ffmpeg, directory, retry);
                foreach (var target in start)
                {
                    state.MatteJobs[target.Path] = job;
                }

                jobs.Add(job.Id);
                notes.Add($"the matte is computed in the background as {job.Id}: {Work(state, kind, start)}, {estimate.Describe()}{download}");
            }
        }

        var ids = string.Join("\", \"", jobs.Distinct());
        var meanwhile = cutout
            ? "the subject isn't drawn over the text in previews yet (the text shows in front)"
            : $"previews draw '{ownerId}' with its background";
        notes.Add($"until it is done, {meanwhile} and exports refuse; poll get_job_status with jobId \"{ids}\"");
        return notes;
    }

    // How long computing the targets that aren't computed, or being computed, yet should take; null
    // when there are none.
    internal static SystemTools.JobEstimate? Estimate(EditorState state, MatteMethod kind, List<MatteTarget> targets)
    {
        var start = targets.Where(t => !File.Exists(t.Path) && !(state.MatteJobs.TryGetValue(t.Path, out var job) && job.State == JobState.Running)).ToList();
        if (start.Count == 0)
        {
            return null;
        }

        var feature = AiFeatures.For(kind);
        var device = state.PickDevice(feature).Device;
        var (width, height) = kind == MatteMethod.AiVideo ? MatteFiles.VideoMatteSize(start[0].Asset) : (0, 0);
        var units = kind == MatteMethod.AiVideo ? start[0].Asset.FrameCount ?? 1 : start.Count;
        var estimate = AiBenchmark.Estimate(feature, device, units, width, height, state.Benchmark());
        return new SystemTools.JobEstimate(estimate, $"the {EnumNames.ToName(kind)} matte ({Work(state, kind, start)})");
    }

    // "96 frames of clip 'talk' with Robust Video Matting (MobileNetV3) on the CPU".
    private static string Work(EditorState state, MatteMethod kind, List<MatteTarget> start)
    {
        var what = kind == MatteMethod.AiVideo
            ? $"{start[0].Asset.FrameCount} frames of {start[0].Label}"
            : string.Join(", ", start.Select(t => t.Label));
        var device = state.PickDevice(AiFeatures.For(kind)).Device == AiDevice.Gpu ? "the GPU (DirectML, or the CPU if it can't start)" : "the CPU";
        return $"{what} with {AiModels.For(kind).Name} on {device}";
    }

    private static BackgroundJob StartMatteJob(EditorState state, string layerId, bool cutout, MatteMethod kind, List<MatteTarget> targets, FfmpegPaths? ffmpeg, string directory, string retry)
    {
        var model = AiModels.For(kind);
        var store = state.Models;
        var device = state.PickDevice(AiFeatures.For(kind)).Device;
        var name = EnumNames.ToName(kind);
        var owner = cutout ? $"cutout '{layerId}'" : $"'{layerId}'";
        var description = kind == MatteMethod.AiVideo
            ? $"aiVideo matte of {owner} ({targets[0].Asset.Id}, {targets[0].Asset.FrameCount} frames)"
            : $"aiImage matte of {owner} ({targets.Count} drawing(s))";
        var failed = cutout
            ? $"cutout '{layerId}' keeps its {name} matte but isn't drawn until it is computed, and exports refuse; run {retry} again to retry, or take the cutout off with remove_layer"
            : $"'{layerId}' keeps its {name} background removal but is drawn with its background, and exports refuse; run remove_background again to retry, or take it off with remove=true";
        return state.Jobs.Start("matte", description, (job, ct) =>
        {
            try
            {
                var download = store.Has(model) ? 0 : kind == MatteMethod.AiImage ? 0.5 : 0.1;
                var path = store.Ensure(model, (done, total) => job.Report(download * done / total, $"downloading {model.FileName}: {Megabytes(done)} of {Megabytes(total)} MB"), ct);
                if (!ComputeGate.Wait(0, ct))
                {
                    job.Report(download, "waiting for another AI job to finish");
                    ComputeGate.Wait(ct);
                }

                try
                {
                    return Compute(job, model, path, device, owner, kind, targets, ffmpeg, directory, download, ct);
                }
                finally
                {
                    ComputeGate.Release();
                }
            }
            catch (ZeyoCutException ex)
            {
                throw new ZeyoCutException($"{ex.Message}\n{failed}");
            }
            finally
            {
                lock (MatteJobGate)
                {
                    foreach (var target in targets)
                    {
                        state.MatteJobs.TryRemove(new KeyValuePair<string, BackgroundJob>(target.Path, job));
                    }
                }
            }
        });
    }

    private static string Compute(
        BackgroundJob job,
        AiModel model,
        string path,
        AiDevice device,
        string owner,
        MatteMethod kind,
        List<MatteTarget> targets,
        FfmpegPaths? ffmpeg,
        string directory,
        double download,
        CancellationToken ct)
    {
        job.Report(download, $"loading {model.Name}");
        using var ai = AiSession.Open(path, device);
        if (kind == MatteMethod.AiVideo)
        {
            var target = targets[0];
            var result = AiMattes.Video(ai, ffmpeg!.Ffmpeg, ProjectStore.AssetPath(directory, target.Asset), target.Asset, target.Path,
                (done, total) => job.Report(download + (1 - download) * Math.Min(1, done / (double)total), $"matting frame {done} of {total}"), ct);
            var fps = result.Frames / Math.Max(0.001, result.Elapsed.TotalSeconds);
            return $"computed the aiVideo matte of {owner} with {model.Name} on the {ai.Device}: {result.Frames} frames at {result.Width}x{result.Height} in {AiMattes.Seconds(result.Elapsed)} ({Num(Math.Round(fps, 1))} frames/s); about {Percent(result.Background)} of the picture is background\n- matte {MatteFiles.Relative(directory, result.Path)}";
        }

        var stopwatch = Stopwatch.StartNew();
        var lines = new List<string>();
        using var images = new AssetCache();
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[i];
            job.Report(download + (1 - download) * i / targets.Count, $"matting {target.Label}, {i + 1} of {targets.Count}");
            var picture = images.GetImage(ProjectStore.AssetPath(directory, target.Asset));
            var alpha = AiMattes.Image(ai, picture);
            Pixels.SaveGray(alpha, picture.Width, picture.Height, target.Path);
            lines.Add($"{target.Label}: about {Percent(AiMattes.Background(alpha))} of the picture is background; matte {MatteFiles.Relative(directory, target.Path)}");
        }

        return $"computed the aiImage matte of {owner} with {model.Name} on the {ai.Device} in {AiMattes.Seconds(stopwatch.Elapsed)}\n" + string.Join("\n", lines.Select(l => "- " + l));
    }

    private static string Label(IMattable layer, Asset asset)
    {
        if (layer is not ImageLayer image)
        {
            return $"clip '{asset.Id}'";
        }

        var names = image.Drawings.Where(d => d.Value == asset.Id).Select(d => d.Key).Order(StringComparer.Ordinal).Select(n => $"'{n}'").ToList();
        return $"{(names.Count == 1 ? "drawing" : "drawings")} {string.Join(", ", names)} ({asset.Id})";
    }

    private static string Megabytes(long bytes) => Math.Round(bytes / 1e6).ToString("0", CultureInfo.InvariantCulture);

    internal sealed record MatteTarget(Asset Asset, string Path, string Label);

    // The screen color from the edges of the layer's first picture (its drawing or the clip's first frame).
    internal static string? PickKey(FrameRenderer renderer, Project project, IMattable layer, List<string> notes)
    {
        if (ChromaKey.Detect(FirstPicture(renderer, project, layer)) is { } key)
        {
            var hex = Hex(key);
            notes.Add($"key color {hex} was picked from the edges of the picture; pass color to choose another");
            return hex;
        }

        notes.Add($"the picture's edges have no clear screen color, so the key is {MatteEditor.DefaultKey}; pass color with the screen's color");
        return null;
    }

    // What the matte removes: per drawing for image layers, the first frame for video layers.
    internal static List<string> Preview(FrameRenderer renderer, Project project, IMattable layer, Matte matte)
    {
        var notes = new List<string>();
        switch (layer)
        {
            case ImageLayer image:
                foreach (var (name, assetId) in image.Drawings.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    var asset = ProjectEditor.GetAsset(project, assetId, AssetType.Image);
                    var picture = renderer.DrawingImage(asset);
                    if (matte.Method == MatteMethod.EdgeFill)
                    {
                        var result = Mattes.EdgeFill(renderer.ProjectDirectory, asset, picture, matte, out var path);
                        notes.Add($"drawing '{name}' ({asset.Id}): background {Hex(result.Background)}, {Percent(result.Removed)} of the pixels removed; matte {MatteFiles.Relative(renderer.ProjectDirectory, path)}");
                    }
                    else
                    {
                        var (keyed, alpha) = ChromaKey.Apply(picture, matte);
                        using (keyed)
                        using (alpha)
                        {
                            notes.Add($"drawing '{name}' ({asset.Id}): {Percent(Mattes.RemovedShare(alpha))} of the pixels keyed out");
                        }
                    }
                }

                break;
            case VideoLayer video:
                var (frame, frameAlpha) = ChromaKey.Apply(FirstPicture(renderer, project, video), matte);
                using (frame)
                using (frameAlpha)
                {
                    notes.Add($"at the clip's first frame (source {Num(video.TrimIn)}s) {Percent(Mattes.RemovedShare(frameAlpha))} of the picture is keyed out; the key runs on every frame as it is drawn");
                }

                break;
        }

        return notes;
    }

    private static SKImage FirstPicture(FrameRenderer renderer, Project project, IMattable layer) => layer switch
    {
        ImageLayer image => renderer.DrawingImage(renderer.CurrentDrawingAsset(project, image, image.Start)
            ?? ProjectEditor.GetAsset(project, image.Drawings.OrderBy(d => d.Key, StringComparer.Ordinal).First().Value, AssetType.Image)),
        VideoLayer video => renderer.VideoFrame(project, video, video.Start),
        _ => throw new ZeyoCutException($"'{layer.Id}' has no picture"),
    };

    private static string Hex(RgbaColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string Percent(double share) => (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
