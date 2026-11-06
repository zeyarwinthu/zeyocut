// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class AnalysisTools
{
    private const int MaxTranscriptWords = 1500;

    [McpServerTool(Name = "analyze_video")]
    [Description("Analyze a video (or audio) asset in the background: scene cuts (ffmpeg scdet), the noise floor and silences, and a word-level transcript (whisper-cli, if WHISPER_PATH/WHISPER_MODEL are set or whisper-cli is on PATH). Results are saved next to the asset as <name>.analysis.json, <name>.transcript.json and <name>.srt. Returns a jobId (analyze_N) to poll with get_job_status. Afterwards use get_transcript, cut_by_transcript and remove_silences mode=transcript. All times in the results are source seconds of the asset.")]
    public static string AnalyzeVideo(
        EditorState state,
        [Description("Video or audio asset id.")] string assetId,
        [Description("Spoken language code for whisper (en, de, ja, ...), or auto. Default auto.")] string language = "auto") => Guard(() =>
    {
        var session = state.Session;
        var asset = session.Read(p => p.FindAsset(assetId) ?? throw new ZeyoCutException($"asset '{assetId}' not found; assets: {Ids.List(p.Assets.Select(a => a.Id))}"));
        if (asset.Type is not (AssetType.Video or AssetType.Audio))
        {
            throw new ZeyoCutException($"asset '{assetId}' is a {asset.Type.ToString().ToLowerInvariant()} asset; analyze_video works on video and audio assets");
        }

        var ffmpeg = state.RequireFfmpeg();
        var whisper = state.FindWhisper(out var problem);
        var directory = session.Directory;
        var job = state.Jobs.Start("analyze", $"analyze {assetId}", (job, ct) =>
        {
            var output = MediaAnalyzer.Analyze(ffmpeg, whisper, problem, directory, asset, language, (p, stage) => job.Report(p, stage), ct);
            return MediaAnalyzer.Describe(output);
        });

        var transcript = whisper is not null ? $"transcript with whisper ({Path.GetFileName(whisper.Model)})" : $"no transcript ({problem})";
        return $"analyzing '{assetId}' in the background as {job.Id}: {(asset.Type == AssetType.Video ? "scene cuts, " : "")}noise floor and silences, {transcript}.\nPoll get_job_status with jobId \"{job.Id}\".";
    });

    [McpServerTool(Name = "get_transcript")]
    [Description("Read the word-level transcript of an analyzed asset (run analyze_video first). Each line is a phrase with its source time range; [n] before a word is its index for cut_by_transcript. Pauses of 0.5s or more start a new line. Times are source seconds of the asset, not timeline times.")]
    public static string GetTranscript(
        EditorState state,
        [Description("Video or audio asset id.")] string assetId,
        [Description("Only words from this source time on, seconds.")] double? startTime = null,
        [Description("Only words up to this source time, seconds.")] double? endTime = null) => Guard(() =>
    {
        var session = state.Session;
        var asset = session.Read(p => p.FindAsset(assetId) ?? throw new ZeyoCutException($"asset '{assetId}' not found; assets: {Ids.List(p.Assets.Select(a => a.Id))}"));
        var transcript = AnalysisFiles.RequireTranscript(session.Directory, asset);
        var from = startTime ?? 0;
        var to = endTime ?? double.PositiveInfinity;
        var words = transcript.Words;
        var indices = Enumerable.Range(0, words.Count).Where(i => words[i].End > from && words[i].Start < to).ToList();
        var builder = new StringBuilder();
        builder.Append($"transcript of '{assetId}' ({transcript.Language}, {words.Count} words");
        if (indices.Count != words.Count)
        {
            builder.Append($"; showing {indices.Count} between {Num(from)} and {(double.IsPositiveInfinity(to) ? "the end" : Num(to) + "s")}");
        }

        builder.Append("). Times are source seconds; [n] = word index for cut_by_transcript.\n");
        if (indices.Count == 0)
        {
            builder.Append("(no words in that range)");
            return builder.ToString();
        }

        var shown = indices.Take(MaxTranscriptWords).ToList();
        var offset = shown[0];
        var lines = AnalysisFiles.Lines(shown.Select(i => words[i]).ToList(), maxWords: 14, maxChars: 100);
        double? previousEnd = null;
        foreach (var line in lines)
        {
            if (previousEnd is { } end && line.Start - end >= 0.5)
            {
                builder.Append($"   (pause {Num(line.Start - end)}s)\n");
            }

            builder.Append($"{line.Start.ToString("0.00", CultureInfo.InvariantCulture)}–{line.End.ToString("0.00", CultureInfo.InvariantCulture)}  ");
            builder.Append(string.Join(" ", Enumerable.Range(line.First, line.Last - line.First + 1).Select(i => $"[{offset + i}] {words[offset + i].Text}")));
            builder.Append('\n');
            previousEnd = line.End;
        }

        if (indices.Count > shown.Count)
        {
            builder.Append($"... {indices.Count - shown.Count} more words; pass startTime={Num(words[indices[shown.Count]].Start)} to continue\n");
        }

        return builder.ToString().TrimEnd();
    });

    [McpServerTool(Name = "view_source")]
    [Description("Contact sheet of a video asset's own frames (not the timeline), each labeled with its source time and frame number, to find trim and cut points before or after adding the clip.")]
    public static IEnumerable<ContentBlock> ViewSource(
        EditorState state,
        [Description("Video asset id.")] string assetId,
        [Description("First source time, seconds. Default 0.")] double startTime = 0,
        [Description("Last source time, seconds. Default: the end of the clip.")] double? endTime = null,
        [Description("Number of frames (1-64). Default 12.")] int count = 12,
        [Description("Grid columns. Default 4.")] int columns = 4,
        [Description("Width of the whole sheet. Default 1600.")] int maxWidth = 1600) => Guard(() =>
    {
        var asset = state.Session.Read(p => ProjectEditor.GetAsset(p, assetId, AssetType.Video));
        var end = endTime ?? VideoTiming.TimeOfFrame(asset, Math.Max(0, (asset.FrameCount ?? 1) - 1));
        PreviewImage sheet;
        lock (state.RenderGate)
        {
            sheet = Preview.RenderSourceSheet(state.Renderer, asset, startTime, end, count, Math.Min(columns, count), maxWidth);
        }

        var frames = string.Join(", ", sheet.Frames.Select(f => $"f{f}={VideoTiming.TimeOfFrame(asset, f).ToString("0.000", CultureInfo.InvariantCulture)}s"));
        return new ContentBlock[]
        {
            new TextContentBlock { Text = WithWarnings($"source frames of '{assetId}' {sheet.Width}x{sheet.Height} (left to right, top to bottom): {frames}; saved {sheet.SavedPath}{PreviewTools.ProxyNote(sheet)}", sheet.Warnings) },
            ImageContentBlock.FromBytes(sheet.Png, "image/png"),
        };
    });

    [McpServerTool(Name = "remove_silences")]
    [Description("Cut the silent parts out of a video or audio layer with ripple (later layers move left and the project gets shorter), keeping 'padding' seconds of quiet next to the remaining sound. mode=audio measures the clip's noise floor first and treats sound below noise floor + 6 dB as silence (ffmpeg silencedetect; override with thresholdDb). mode=transcript uses the gaps between spoken words instead (needs analyze_video), which works for clips with background music. dryRun=true only reports the planned cuts. punchIn=true zooms every second piece to punchInZoom to hide the jump cuts. Pieces are named clip, clip_2, clip_3, ... in time order.")]
    public static string RemoveSilences(
        EditorState state,
        [Description("Video or audio layer.")] string layerId,
        [Description("Shortest pause to cut, seconds. Default 0.7.")] double minSilence = 0.7,
        [Description("Quiet kept before and after each remaining sound, seconds. Default 0.1.")] double padding = 0.1,
        [Description("audio (silencedetect on the sound) or transcript (gaps between words). Default audio.")] string mode = "audio",
        [Description("mode=audio: silence threshold in dBFS instead of the measured noise floor + 6 dB, e.g. -40.")] double? thresholdDb = null,
        [Description("Only report the planned cuts and the new duration; change nothing.")] bool dryRun = false,
        [Description("Zoom every second piece to hide the jump cuts.")] bool punchIn = false,
        [Description("Zoom of the punched-in pieces. Default 1.1.")] double punchInZoom = SilenceCutter.DefaultPunchIn) => Guard(() =>
    {
        var session = state.Session;
        if (!double.IsFinite(minSilence) || minSilence < 0.1)
        {
            throw new ZeyoCutException($"minSilence must be at least 0.1s (got {Num(minSilence)})");
        }

        if (padding * 2 >= minSilence)
        {
            throw new ZeyoCutException($"padding ({Num(padding)}s) must be less than half of minSilence ({Num(minSilence)}s), or nothing would be cut");
        }

        var (layer, asset, range) = session.Read(p =>
        {
            var (l, a) = SilenceCutter.MediaLayer(p, layerId);
            return (l, a, SilenceCutter.SourceRange(p, l));
        });
        if (punchIn && layer is not VideoLayer)
        {
            throw new ZeyoCutException($"punchIn needs a video layer; '{layerId}' is a {layer.TypeName} layer");
        }

        List<TimeRange> silences;
        string detection;
        switch (mode.Trim().ToLowerInvariant())
        {
            case "audio":
                if (asset.Type == AssetType.Video && asset.HasAudio == false)
                {
                    throw new ZeyoCutException($"'{asset.Id}' has no sound; use mode=transcript or cut by hand with remove_range");
                }

                var ffmpeg = state.RequireFfmpeg();
                var file = ProjectStore.AssetPath(session.Directory, asset);
                double threshold;
                if (thresholdDb is { } manual)
                {
                    threshold = manual;
                    detection = $"silence below {manual.ToString("0.0", CultureInfo.InvariantCulture)} dB (given)";
                }
                else
                {
                    var levels = AudioAnalyzer.Levels(ffmpeg.Ffmpeg, file, range.Start, range.Length);
                    threshold = levels.ThresholdDb;
                    detection = levels.Describe();
                }

                silences = AudioAnalyzer.Silences(ffmpeg.Ffmpeg, file, range.Start, range.Length, threshold, minSilence);
                break;
            case "transcript":
                var transcript = AnalysisFiles.RequireTranscript(session.Directory, asset);
                silences = SilenceCutter.TranscriptGaps(transcript.Words, range, minSilence);
                detection = $"gaps of {Num(minSilence)}s or more between the {transcript.Words.Count(w => w.End > range.Start && w.Start < range.End)} spoken words";
                break;
            default:
                throw new ZeyoCutException($"unknown mode '{mode}'; use audio or transcript");
        }

        if (dryRun)
        {
            var plan = session.Read(p => SilenceCutter.Plan(p, ProjectEditor.GetLayer(p, layerId), silences, padding));
            return $"dry run, nothing changed. {detection}\n{SilenceCutter.Describe(plan)}";
        }

        var result = session.Mutate($"remove_silences {layerId}", p =>
        {
            var plan = SilenceCutter.Plan(p, ProjectEditor.GetLayer(p, layerId), silences, padding);
            return plan.Cuts.Count == 0 ? null : SilenceCutter.Apply(p, plan, punchIn ? punchInZoom : 1);
        });
        if (result is null)
        {
            return $"no silences to cut in '{layerId}' ({detection}); nothing changed";
        }

        var lines = new List<string> { detection, SilenceCutter.Describe(result.Plan), $"pieces: {string.Join(", ", result.Pieces)}" };
        if (punchIn && result.Pieces.Count > 1)
        {
            lines.Add($"punched in to {Num(punchInZoom)}x: {string.Join(", ", result.Pieces.Where((_, i) => i % 2 == 1))}");
        }

        return WithWarnings(string.Join("\n", lines), result.Warnings);
    });

    [McpServerTool(Name = "cut_by_transcript")]
    [Description("Remove words from a video or audio layer by their transcript indices (the [n] numbers from get_transcript), with ripple. Each range cuts from the first word's start to the next kept word's start, so the pause after the removed words goes too; a quiet sliver under 0.3s left at the clip's edge goes as well. Word timing comes from whisper, fitted to the measured silences, and can still be off by a few tenths of a second; check the result with render_frame or view_source.")]
    public static string CutByTranscript(
        EditorState state,
        [Description("Video or audio layer that plays the words.")] string layerId,
        [Description("Word index ranges to remove: [{from, to}] (inclusive).")] WordRangeArg[] wordRanges,
        [Description("Only report the planned cuts; change nothing.")] bool dryRun = false) => Guard(() =>
    {
        var session = state.Session;
        var (layer, asset, range) = session.Read(p =>
        {
            var (l, a) = SilenceCutter.MediaLayer(p, layerId);
            return (l, a, SilenceCutter.SourceRange(p, l));
        });
        var transcript = AnalysisFiles.RequireTranscript(session.Directory, asset);
        var pairs = (wordRanges ?? []).Select(r => (r.From, r.To)).ToList();
        var sourceRanges = SilenceCutter.WordRanges(transcript, pairs);
        for (var i = 0; i < pairs.Count; i++)
        {
            var (from, to) = pairs[i];
            if (sourceRanges[i].End <= range.Start || sourceRanges[i].Start >= range.End)
            {
                var other = session.Read(p => p.Layers.FirstOrDefault(l => l.Id != layerId
                    && (l is VideoLayer v && v.Asset == asset.Id || l is AudioLayer a && a.Asset == asset.Id)
                    && SilenceCutter.SourceRange(p, l) is var r && sourceRanges[i].Start < r.End && sourceRanges[i].End > r.Start)?.Id);
                throw new ZeyoCutException($"words {from}–{to} (source {Num(sourceRanges[i].Start)}–{Num(sourceRanges[i].End)}s) are not in '{layerId}', which plays source {Num(range.Start)}–{Num(range.End)}s{(other is null ? "" : $"; they are in '{other}'")}");
            }
        }

        sourceRanges = SilenceCutter.SnapToClipEdges(transcript.Words, sourceRanges, range);
        var said = string.Join(" | ", pairs.Select(r => string.Join(" ", transcript.Words.Skip(r.From).Take(r.To - r.From + 1).Select(w => w.Text))));
        if (dryRun)
        {
            var plan = session.Read(p => SilenceCutter.Plan(p, ProjectEditor.GetLayer(p, layerId), sourceRanges, 0));
            return $"dry run, nothing changed. Would remove: {said}\n{SilenceCutter.Describe(plan)}";
        }

        var result = session.Mutate($"cut_by_transcript {layerId}", p =>
            SilenceCutter.Apply(p, SilenceCutter.Plan(p, ProjectEditor.GetLayer(p, layerId), sourceRanges, 0)));
        return WithWarnings($"removed: {said}\n{SilenceCutter.Describe(result.Plan)}\npieces: {string.Join(", ", result.Pieces)}", result.Warnings);
    });
}
