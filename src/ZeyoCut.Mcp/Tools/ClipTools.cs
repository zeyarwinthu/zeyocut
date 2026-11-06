// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

// Not an [McpServerToolType]: the assembly scan skips it, and ClipDescribers.WithClipDescriber adds it
// only when a clip describer is configured, so without one describe_clip doesn't appear at all.
public static class ClipTools
{
    // The longest stretch one call sends to the provider.
    public const double MaxSeconds = 60;

    // Moments this far outside the requested range still count (providers round their times).
    private const double Slack = 0.5;

    [McpServerTool(Name = "describe_clip")]
    [Description("Describe what happens in a stretch of video, with times. The configured provider watches the segment and returns timestamped notes such as '2.4s: a man picks up a cup'. Give assetId (times in source seconds) or layerId (times on the timeline, while the layer plays). At most 60 s per call. The segment is uploaded to the provider, and each call may cost money. Runs in the background and returns a jobId (describe_N) for get_job_status.")]
    public static string DescribeClip(
        EditorState state,
        IClipDescriber describer,
        [Description("First time in seconds.")] double startTime,
        [Description("Last time in seconds, at most 60 s after startTime.")] double endTime,
        [Description("Video asset id; the times are source seconds of the file.")] string? assetId = null,
        [Description("Video layer id; the times are timeline seconds while the layer plays.")] string? layerId = null) => Guard(() =>
    {
        var session = state.Session;
        var project = session.Snapshot();
        var byLayer = !string.IsNullOrWhiteSpace(layerId);
        if (byLayer == !string.IsNullOrWhiteSpace(assetId))
        {
            throw new ZeyoCutException("give either assetId (source times) or layerId (timeline times), not both");
        }

        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || startTime < 0 || endTime <= startTime)
        {
            throw new ZeyoCutException($"the range must have 0 <= startTime < endTime (got {Num(startTime)}–{Num(endTime)})");
        }

        if (endTime - startTime > MaxSeconds + 1e-9)
        {
            throw new ZeyoCutException($"describe at most {Num(MaxSeconds)}s per call; {Num(startTime)}–{Num(endTime)}s is {Num(endTime - startTime)}s, so split it");
        }

        VideoLayer? layer = null;
        Asset asset;
        if (byLayer)
        {
            layer = ProjectEditor.GetLayer(project, layerId) as VideoLayer
                ?? throw new ZeyoCutException($"layer '{layerId}' is not a video layer; describe_clip works on video layers and video assets");
            if (startTime < layer.Start - 1e-6 || endTime > layer.TimelineEnd + 1e-6)
            {
                throw new ZeyoCutException($"layer '{layer.Id}' plays {Num(layer.Start)}–{Num(layer.TimelineEnd)}s on the timeline; give times in that range");
            }

            asset = ProjectEditor.GetAsset(project, layer.Asset, AssetType.Video);
        }
        else
        {
            asset = ProjectEditor.GetAsset(project, assetId, AssetType.Video);
            if (asset.Duration is { } duration && endTime > duration + 1e-6)
            {
                throw new ZeyoCutException($"asset '{asset.Id}' is {Num(duration)}s long; give source times up to that");
            }
        }

        var path = ProjectStore.AssetPath(session.Directory, asset);
        if (!File.Exists(path))
        {
            throw new ZeyoCutException($"asset '{asset.Id}': file is missing: {path}");
        }

        var request = layer is null
            ? new ClipRequest(asset.Id, null, path, startTime, endTime)
            : new ClipRequest(asset.Id, layer.Id, path, VideoTiming.SourceTime(layer, startTime), VideoTiming.SourceTime(layer, endTime));
        var what = $"{(layer is null ? "asset" : "layer")} '{layer?.Id ?? asset.Id}' {Num(startTime)}–{Num(endTime)}s";
        var job = state.Jobs.Start("describe", $"describe_clip {what} with {describer.Provider}", (job, ct) =>
        {
            job.Report(0, $"waiting for {describer.Provider}");
            var moments = describer.DescribeAsync(request, ct).GetAwaiter().GetResult();
            return Describe(describer.Provider, request, layer, moments);
        });
        return $"describing {what} with {describer.Provider} in the background as {job.Id}.\nPoll get_job_status with jobId \"{job.Id}\".";
    });

    private static string Describe(string provider, ClipRequest request, VideoLayer? layer, IReadOnlyList<ClipMoment> moments)
    {
        var kept = moments
            .Where(m => double.IsFinite(m.Time) && m.Time >= request.Start - Slack && m.Time <= request.End + Slack && !string.IsNullOrWhiteSpace(m.Description))
            .OrderBy(m => m.Time)
            .ToList();
        var range = $"{Num(request.Start)}–{Num(request.End)}s";
        var builder = new StringBuilder(layer is null
            ? $"asset '{request.AssetId}' {range} (source seconds), described by {provider}: {kept.Count} moment(s)"
            : $"layer '{layer.Id}' {Num(VideoTiming.TimelineTime(layer, request.Start))}–{Num(VideoTiming.TimelineTime(layer, request.End))}s on the timeline (source {range} of '{request.AssetId}'), described by {provider}: {kept.Count} moment(s)");
        foreach (var moment in kept)
        {
            var time = layer is null
                ? $"{Num(moment.Time)}s"
                : $"{Num(VideoTiming.TimelineTime(layer, moment.Time))}s (source {Num(moment.Time)}s)";
            builder.Append($"\n- {time}: {moment.Description.ReplaceLineEndings(" ").Trim()}");
        }

        var left = moments.Count - kept.Count;
        return WithWarnings(builder.ToString(), left > 0 ? [$"{left} moment(s) outside the range or without text were left out"] : []);
    }
}
