// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class VideoTools
{
    [McpServerTool(Name = "import_video")]
    [Description("Import a video file (mp4/mov/m4v/mkv/webm/avi) as a video asset. A constant-frame-rate, upright, 8-bit file is copied into the project's assets folder and registered at once. Anything else (variable frame rate from phones, rotation metadata, 10-bit or HDR, non-square pixels) is transcoded in the background to H.264 CRF 16 at a constant standard frame rate, upright, 8-bit BT.709, with a keyframe every second: the call then returns a jobId (import_N) to poll with get_job_status, and the asset is added when the job completes, as its own undo step. Footage over 2560 px also gets a 1920 px proxy in a proxy_N job, which previews and object tracking read once it is done (exports read the original).")]
    public static string ImportVideo(
        EditorState state,
        [Description("Absolute path of the video file, in an import folder, the library or the projects folder.")] string path,
        [Description("Optional asset id (letters, digits, _ or -). Default: derived from the file name.")] string? id = null) => Guard(() =>
    {
        var session = state.Session;
        var source = AssetImporter.CheckSource(AllowedFolders.Readable(state.Settings, path), AssetType.Video);
        session.Read(p =>
        {
            AssetImporter.CheckNewId(p, id);
            return 0;
        });

        var ffmpeg = state.RequireFfmpeg();
        var probe = VideoProbe.Probe(ffmpeg.Ffprobe, source);
        var reasons = probe.NormalizeReasons();
        if (reasons.Count == 0)
        {
            var info = probe.ToAssetInfo();
            var asset = session.Mutate($"import_video {Path.GetFileName(source)}", p => AssetImporter.Import(p, session.Directory, source, id, new AssetProbe(video: _ => info), AssetType.Video));
            return $"imported {AssetTools.Describe(asset)} (copied as it is: constant frame rate, upright, 8-bit)" + state.StartProxies(session, [asset.Id]);
        }

        var output = AssetImporter.NewAssetPath(session.Directory, source, ".mp4");
        var rate = VideoNormalizer.TargetRate(probe);
        var job = state.Jobs.Start("import", $"normalize {Path.GetFileName(source)}", (job, ct) =>
        {
            var stopwatch = Stopwatch.StartNew();
            job.Report(0, "transcoding");
            var normalized = VideoNormalizer.Normalize(ffmpeg, source, probe, output, p => job.Report(p * 0.98), ct);
            job.Report(0.99, "registering the asset");
            try
            {
                var info = normalized.ToAssetInfo();
                var asset = session.Mutate($"import_video {Path.GetFileName(source)}", p =>
                {
                    var added = AssetImporter.Import(p, session.Directory, output, id, new AssetProbe(video: _ => info), AssetType.Video);
                    added.OriginalFile = source;
                    added.Normalized = string.Join("; ", reasons);
                    return added;
                });
                return $"imported {AssetTools.Describe(asset)}\nnormalized from {source} in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s" + state.StartProxies(session, [asset.Id]);
            }
            catch
            {
                File.Delete(output);
                throw;
            }
        });

        return $"'{Path.GetFileName(source)}' needs normalizing: {string.Join("; ", reasons)}.\n"
            + $"Transcoding in the background as {job.Id} (H.264 CRF {VideoNormalizer.Crf}, constant {rate} fps, upright, 8-bit BT.709, keyframe every second) to {output}.\n"
            + $"Poll get_job_status with jobId \"{job.Id}\"; the asset is added when it completes.";
    });

    [McpServerTool(Name = "add_video_layer")]
    [Description("Add a video clip from a video asset. The clip plays source seconds trimIn..trimOut at 'speed', starting at timelineStart; it lasts (trimOut - trimIn) / speed on the timeline (video layers have no 'end'). By default it is centered and scaled to fit the canvas. It takes every transform and keyframe like an image layer, plus brightness (-1..1, 0 = unchanged; -1 is black), contrast and saturation (0..4, 1 = unchanged) and an optional crop (cropWidth/cropHeight in source pixels via update_layer; cropX/cropY are animatable). Its sound is mixed into the export unless muted. A clip that runs past the project end extends the project.")]
    public static string AddVideoLayer(
        EditorState state,
        [Description("Video asset id (from import_video).")] string assetId,
        [Description("Timeline time where the clip starts, seconds. Default 0.")] double timelineStart = 0,
        [Description("Start point inside the source, seconds. Default 0.")] double trimIn = 0,
        [Description("End point inside the source, seconds. Default: end of the source.")] double? trimOut = null,
        [Description("Playback speed, 0.1..10. Default 1.")] double speed = 1.0,
        [Description("Linear gain for the clip's sound, 1 = original. Default 1.")] double volume = 1.0,
        [Description("Drop the clip's sound.")] bool muted = false,
        [Description("Layer id. Default: derived from name or asset.")] string? id = null,
        string? name = null,
        [Description("Parent layer id; the clip then moves with the parent.")] string? parentId = null,
        [Description("Where the anchor lands. Default: canvas center.")] double? x = null,
        double? y = null,
        [Description("Pivot in the clip's own pixels. Default: frame center.")] double? anchorX = null,
        double? anchorY = null,
        double? rotation = null,
        [Description("Default: fit the canvas.")] double? scaleX = null,
        [Description("Default: fit the canvas.")] double? scaleY = null,
        double? opacity = null,
        [Description("Draw order (higher is in front). Default: on top.")] int? zIndex = null,
        [Description("Sound fade-in, seconds.")] double? fadeIn = null,
        [Description("Sound fade-out, seconds.")] double? fadeOut = null,
        [Description("What the clip's sound is in the mix: voice (default), music or effects. Music is ducked under voice.")] string? role = null,
        [Description("Language of the clip's sound (en, ja, my, pt-BR): other languages' versions mute it; its picture is in every version. Default: every version.")] string? language = null) => Guard(() =>
    {
        var options = new VideoLayerOptions
        {
            Asset = assetId,
            Start = timelineStart,
            TrimIn = trimIn,
            TrimOut = trimOut,
            Speed = speed,
            Volume = volume,
            Muted = muted,
            Role = role,
            FadeIn = fadeIn,
            FadeOut = fadeOut,
            Id = id,
            Name = name,
            ParentId = parentId,
            X = x,
            Y = y,
            AnchorX = anchorX,
            AnchorY = anchorY,
            Rotation = rotation,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Opacity = opacity,
            ZIndex = zIndex,
            Language = language,
        };
        var (layer, oldDuration, newDuration) = state.Session.Mutate($"add_video_layer {id ?? assetId}", p =>
        {
            var before = p.Duration;
            var added = ProjectEditor.AddVideoLayer(p, options);
            return (added, before, p.Duration);
        });
        var extended = newDuration > oldDuration ? $"\nthe project was extended from {Num(oldDuration)}s to {Num(newDuration)}s to fit the clip" : "";
        return $"added video layer '{layer.Id}': source {Num(layer.TrimIn)}–{Num(layer.TrimOut)}s at {Num(layer.Speed)}x → timeline {Num(layer.Start)}–{Num(layer.TimelineEnd)}s, scale {Num(layer.ScaleX.Value)}, z={layer.ZIndex}{extended}\n{LayerJson(layer)}";
    });

    [McpServerTool(Name = "reframe_to_vertical")]
    [Description("Fit landscape video layers to a vertical canvas (switch it first with update_project preset=shorts_1080p): each clip gets a full-height crop with the canvas' aspect ratio at focusX, centered and scaled to fill the frame. Zoom differences between pieces (punch-in) are kept. cropX is animatable: keyframe it (set_keyframes, source pixels) to follow the subject.")]
    public static string ReframeToVertical(
        EditorState state,
        [Description("Video layer to reframe, or \"all\" for every video layer. Default all.")] string layerId = "all",
        [Description("Horizontal position of the crop: 0 = left edge, 0.5 = center (default), 1 = right edge.")] double focusX = 0.5) => Guard(() =>
    {
        List<string> warnings = [];
        var results = state.Session.Mutate($"reframe_to_vertical {layerId}", p => Reframe.ToVertical(p, layerId, focusX, out warnings));
        var lines = results.Select(r => $"- {r.LayerId}: crop {Num(r.CropWidth)}x{Num(r.CropHeight)} at x={Num(r.CropX)} y={Num(r.CropY)}, scale {Num(r.Scale)}");
        return WithWarnings($"reframed {results.Count} video layer(s):\n{string.Join("\n", lines)}", warnings);
    });
}
