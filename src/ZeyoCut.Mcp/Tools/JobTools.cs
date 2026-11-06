// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render.Jobs;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class JobTools
{
    [McpServerTool(Name = "get_job_status")]
    [Description("Progress or result of a background job: import_N (import_video normalization), analyze_N (analyze_video), framediff_N (frame_diff), audiolevels_N (audio_levels), beats_N (detect_beats), batch_N (export_batch), versions_N (export_language_versions), frames_N (export_frames), prerender_N (prerender_layers), matte_N (remove_background, text_behind_subject or generate_captions with aiImage or aiVideo), occlusion_N (text_occlusion), captions_N (generate_captions behindSubject=auto), syscheck_N (system_check), tracking_N (track_object or reanchor_track over more than 150 frames), detect_N (detect_and_track; when done it also shows the contact sheet of the tracks), proxy_N (the 1920 px proxy of footage over 2560 px, made by import_video, import_folder and open_project) or export_N (start_export). A running job's status estimates the time left. Without jobId, lists this session's jobs and the latest ones of earlier sessions. Jobs are kept in the jobs folder for 7 days, so they outlast a restart: an export (export_N, batch_N or versions_N) that was running when the server stopped starts again under its id, up to 3 times; any other job that was running shows as failed, with how far it got, and has to be started again.")]
    public static IEnumerable<ContentBlock> GetJobStatus(
        EditorState state,
        [Description("The jobId returned by import_video, analyze_video, frame_diff, audio_levels, detect_beats, export_batch, export_language_versions, export_frames, remove_background or start_export. Omit to list all jobs.")] string? jobId = null) => Guard<IEnumerable<ContentBlock>>(() =>
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            var builder = new StringBuilder();
            foreach (var job in state.Jobs.All)
            {
                builder.AppendLine("- " + Describe(job).Split('\n')[0]);
            }

            foreach (var export in state.Exports.All)
            {
                builder.AppendLine("- " + ExportTools.Describe(export).Split('\n')[0]);
            }

            var earlier = state.Jobs.Earlier.Select(j => (j.Id, State: EnumNames.ToName(j.State), Finished: j.FinishedUtc ?? j.StartedUtc))
                .Concat(state.Exports.Earlier.Select(e => (e.Id, State: EnumNames.ToName(e.State), Finished: e.FinishedUtc ?? e.StartedUtc)))
                .OrderByDescending(j => j.Finished)
                .ToList();
            if (earlier.Count > 0)
            {
                var shown = string.Join(", ", earlier.Take(EarlierShown).Select(j => $"{j.Id} ({j.State})"));
                var more = earlier.Count > EarlierShown ? $" and {earlier.Count - EarlierShown} more" : "";
                builder.AppendLine($"earlier sessions, newest first: {shown}{more}; get_job_status with a jobId shows one");
            }

            return [new TextContentBlock { Text = builder.Length == 0 ? "(no jobs yet)" : builder.ToString().TrimEnd() }];
        }

        var id = jobId.Trim();
        if (state.Jobs.TryGet(id, out var background))
        {
            var text = new TextContentBlock { Text = Describe(background!) };
            return background!.State == JobState.Completed && background.Image is { } png
                ? [text, ImageContentBlock.FromBytes(png, "image/png")]
                : [text];
        }

        if (state.Exports.TryGet(id, out var exportJob))
        {
            return [new TextContentBlock { Text = ExportTools.Describe(exportJob!) }];
        }

        var all = state.Jobs.All.Select(j => j.Id).Concat(state.Exports.All.Select(e => e.Id))
            .Concat(state.Jobs.Earlier.Select(j => j.Id)).Concat(state.Exports.Earlier.Select(e => e.Id));
        throw new ZeyoCutException($"job '{id}' not found; jobs: {Ids.List(all)}");
    });

    private const int EarlierShown = 10;

    public static string Describe(BackgroundJob job)
    {
        var span = (job.FinishedUtc ?? DateTime.UtcNow) - job.StartedUtc;
        var elapsed = Seconds(span.TotalSeconds);
        return job.State switch
        {
            JobState.Running => $"{job.Id}: running ({job.Stage}) {job.Percent}%, {elapsed}s elapsed{JobTime.LeftText(job.Progress, span)} — {job.Description}{Again(job.Attempt)}",
            JobState.Completed => $"{job.Id}: completed in {elapsed}s — {job.Description}{Again(job.Attempt)}\n{job.Result}",
            _ => $"{job.Id}: {EnumNames.ToName(job.State)} after {elapsed}s — {job.Description}: {job.Error}",
        };
    }

    // "; started again after the server restarted (attempt 2 of 3)" for a job a restart resumed.
    internal static string Again(int attempt) =>
        attempt > 1 ? $"; started again after the server restarted (attempt {attempt} of {JobStore.MaxAttempts})" : "";

    private static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
