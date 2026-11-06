// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Server;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Text;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class CheckTools
{
    // Up to this many frames frame_diff answers directly; longer ranges run as a job.
    public const int DirectFrames = 150;

    // Up to this long audio_levels answers directly; longer mixes run as a job.
    public const double DirectAudioSeconds = 120;

    [McpServerTool(Name = "frame_diff")]
    [Description("Find glitch frames: renders every frame of the range small (160 px wide), scores how much each one changed from the previous one (0-100% mean color difference) and flags 'glitch' (a single frame unlike both neighbors while they match each other, e.g. a stray one-frame layer) and 'spike' (a large change with no layer starting, ending or swapping drawings there). Layer starts and ends, drawing swaps, hold keyframes and analyzed scene cuts are listed as expected cuts. The scores are saved to renders/frame_diff_<first>-<last>.json. Up to 150 frames answers directly; longer ranges run in the background and return a jobId (framediff_N) for get_job_status.")]
    public static string FrameDiffTool(
        EditorState state,
        [Description("First time to compare, seconds. Default 0.")] double startTime = 0,
        [Description("Last time to compare, seconds. Default: the end of the project.")] double? endTime = null) => Guard(() =>
    {
        var session = state.Session;
        var project = session.Snapshot();
        var (first, last) = FrameDiff.Frames(project, startTime, endTime);
        var directory = session.Directory;
        string Run(Action<double>? progress, CancellationToken cancellationToken)
        {
            using var renderer = new FrameRenderer(directory, () => state.RequireFfmpeg().Ffmpeg, streamVideo: true);
            var result = FrameDiff.Run(renderer, project, startTime, endTime, progress, cancellationToken);
            return WithWarnings(result.Describe(), renderer.TakeWarnings());
        }

        var frames = last - first + 1;
        if (frames <= DirectFrames)
        {
            return Run(null, CancellationToken.None);
        }

        var job = state.Jobs.Start("framediff", $"frame_diff f{first}–f{last}", (job, ct) => Run(p => job.Report(p, "rendering frames"), ct));
        return $"comparing {frames} frames (f{first}–f{last}) in the background as {job.Id}.\nPoll get_job_status with jobId \"{job.Id}\".";
    });

    [McpServerTool(Name = "audio_levels")]
    [Description("Measure the sound exactly as the export would mix it (fades, music ducked under voice, loudness normalization): integrated loudness (LUFS, ITU-R BS.1770) and sample peak (dBFS) for the whole mix and per section, and flags clipping (samples at full scale), sections more than 10 LU quieter than the whole mix, a whole mix below -24 LUFS with normalization off, and silences of 1s or more in the middle (silence at the very start or end is listed but not flagged). Mixes up to 120s answer directly; longer ones run in the background and return a jobId (audiolevels_N) for get_job_status.")]
    public static string AudioLevelsTool(
        EditorState state,
        [Description("Section length in seconds, 1-60. Default 5.")] double sectionSeconds = AudioLevels.DefaultSection) => Guard(() =>
    {
        AudioLevels.CheckSection(sectionSeconds);
        var session = state.Session;
        var project = session.Snapshot();
        var directory = session.Directory;
        if (FfmpegArgs.AudioClips(project, directory).Count == 0)
        {
            return "audio_levels: the project has no sound (no audio layers and no video layers with sound), so the export will be silent";
        }

        var ffmpeg = state.RequireFfmpeg();
        string Run(Action<double>? progress, CancellationToken cancellationToken) =>
            AudioLevels.Measure(project, directory, ffmpeg, sectionSeconds, progress, cancellationToken)!.Describe();

        var duration = FrameMath.FrameCount(project) / (double)project.Fps;
        if (duration <= DirectAudioSeconds)
        {
            return Run(null, CancellationToken.None);
        }

        var job = state.Jobs.Start("audiolevels", $"audio_levels {Num(duration)}s", (job, ct) => Run(p => job.Report(p, "mixing and measuring"), ct));
        return $"measuring the {Num(duration)}s mix in the background as {job.Id}.\nPoll get_job_status with jobId \"{job.Id}\".";
    });

    [McpServerTool(Name = "check_project")]
    [Description("Check the open project for problems before export: assets whose files are missing, layers that are never on the canvas while shown, text reaching into the 5% title-safe margin (on vertical 9:16 canvases: the Shorts player UI, top 8%, bottom 25%, right 12%), characters a text's font has no glyphs for (each text is shaped with HarfBuzz, as it is drawn), stretches where nothing but the background shows, keyframes outside a layer's time that never play, and assets no layer uses. Read-only; every frame is checked.")]
    public static string CheckProject(EditorState state) => Guard(() =>
    {
        var session = state.Session;
        var renderer = state.Renderer;
        return session.Read(p => ProjectChecker.Check(p, session.Directory, new TextMeasurer(renderer, p), state.Settings.Fonts).Describe());
    });
}
