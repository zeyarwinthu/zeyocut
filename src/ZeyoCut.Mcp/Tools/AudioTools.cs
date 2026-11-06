// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class AudioTools
{
    // Up to this long detect_beats answers directly; longer sounds run as a job.
    public const double DirectBeatSeconds = 120;

    private const int MaxListed = 200;

    [McpServerTool(Name = "list_sfx")]
    [Description("List the sound effects in the shared library's sfx folder (wav, mp3, ogg and other audio files; subfolders are categories), with their durations. Names are paths inside sfx without the extension (impacts/thud); add_sfx takes the full name or, when unique, the file name alone.")]
    public static string ListSfx(
        EditorState state,
        [Description("Only names containing this text (not case-sensitive).")] string? filter = null) => Guard(() =>
    {
        var library = state.Library;
        var effects = SoundEffects.List(library, filter);
        if (effects.Count == 0)
        {
            return SoundEffects.List(library).Count == 0
                ? $"no sound effects yet; put wav, mp3, ogg or other audio files in {library.SfxFolder} (subfolders become categories)"
                : $"no sound effect name contains '{filter}'";
        }

        var probe = state.AssetProbe;
        var builder = new StringBuilder($"sound effects in {library.SfxFolder} ({effects.Count}{(string.IsNullOrWhiteSpace(filter) ? "" : $" matching '{filter.Trim()}'")}):\n");
        string? category = null;
        foreach (var effect in effects.Take(MaxListed))
        {
            if (effect.Category != category)
            {
                category = effect.Category;
                builder.Append(category.Length == 0 ? "(top level)\n" : $"{category}/\n");
            }

            string length;
            try
            {
                length = probe.Probe(effect.File, AssetType.Audio).Duration is { } seconds ? $"{Num(seconds)}s" : "?";
            }
            catch (Exception ex) when (ex is ZeyoCutException or IOException or InvalidOperationException)
            {
                length = "?";
            }

            builder.Append($"- {effect.Name} ({length})\n");
        }

        if (effects.Count > MaxListed)
        {
            builder.Append($"... {effects.Count - MaxListed} more; pass a filter to narrow the list\n");
        }

        return builder.ToString().TrimEnd();
    });

    [McpServerTool(Name = "add_sfx")]
    [Description("Add a sound effect from the library at a time (one undo step): the file is imported into the project once (later adds of the same sound reuse the asset) and placed as an audio layer with role effects, so music ducking leaves it alone. Change it afterwards like any audio layer (update_layer, move_layer).")]
    public static string AddSfx(
        EditorState state,
        [Description("Sound effect name from list_sfx (impacts/thud, or thud when unique).")] string name,
        [Description("When it plays, seconds on the timeline.")] double time,
        [Description("Volume 0-10, 1 = as recorded. Default 1.")] double volume = 1) => Guard(() =>
    {
        var session = state.Session;
        var probe = state.AssetProbe;
        var added = session.Mutate($"add_sfx {name}", p => SoundEffects.Add(p, session.Directory, state.Library, name, time, volume, probe));
        var asset = added.Imported ? $"imported '{added.Effect.Name}' as asset '{added.Asset.Id}'" : $"reused asset '{added.Asset.Id}'";
        var length = added.Asset.Duration is { } seconds ? $", {Num(seconds)}s long" : "";
        return WithWarnings($"added sound effect '{added.Effect.Name}' as audio layer '{added.Layer.Id}' at {Num(time)}s (volume {Num(volume)}, role effects{length}); {asset}\n{LayerJson(added.Layer)}", added.Warnings);
    });

    [McpServerTool(Name = "detect_beats")]
    [Description("Find the beats of a music (or other audio or video) asset: onsets from the spectral flux of the sound, the tempo from their autocorrelation (60-200 BPM, favoring 120 between octaves), and a steady beat grid fitted to the onsets. Returns the BPM, the beat times, the onsets and how many beats land on an onset; saved next to the asset as <name>.beats.json. Times are source seconds of the asset: a layer starting at s0 with trimIn t0 plays source time t at s0 + t - t0. Sounds up to 120s answer directly; longer ones run in the background and return a jobId (beats_N) for get_job_status.")]
    public static string DetectBeats(
        EditorState state,
        [Description("Audio asset id, or a video asset with sound.")] string assetId) => Guard(() =>
    {
        var session = state.Session;
        var asset = session.Read(p => p.FindAsset(assetId) ?? throw new ZeyoCutException($"asset '{assetId}' not found; assets: {Ids.List(p.Assets.Select(a => a.Id))}"));
        if (asset.Type is not (AssetType.Audio or AssetType.Video))
        {
            throw new ZeyoCutException($"'{assetId}' is not an audio or video asset ({EnumNames.ToName(asset.Type)}); detect_beats works on audio assets and video assets with sound");
        }

        if (asset.Type == AssetType.Video && asset.HasAudio != true)
        {
            throw new ZeyoCutException($"video asset '{assetId}' has no sound");
        }

        var ffmpeg = state.RequireFfmpeg();
        var directory = session.Directory;
        var saved = AnalysisFiles.BeatsPath(directory, asset);
        string Run(Action<double>? progress, CancellationToken cancellationToken) =>
            BeatDetector.Describe(BeatDetector.Analyze(ffmpeg.Ffmpeg, directory, asset, progress, cancellationToken), saved);

        if ((asset.Duration ?? 0) <= DirectBeatSeconds)
        {
            return Run(null, CancellationToken.None);
        }

        var job = state.Jobs.Start("beats", $"detect_beats {assetId}", (job, ct) => Run(p => job.Report(p, "finding beats"), ct));
        return $"finding the beats of '{assetId}' ({Num(asset.Duration ?? 0)}s) in the background as {job.Id}.\nPoll get_job_status with jobId \"{job.Id}\".";
    });

    [McpServerTool(Name = "auto_lip_flap")]
    [Description("Animate a mouth to a voice (one undo step): measures the loudness of every frame of the sound as it plays on the timeline (start and trim respected), gives quiet frames the closed drawing and louder frames the open drawings (listed from slightly to wide open), holds every drawing at least 2 frames so the mouth doesn't flicker, and writes hold keyframes on the mouth's drawing over the time both play, replacing the drawing keyframes there. The drawing before and after stays as it was.")]
    public static string AutoLipFlap(
        EditorState state,
        [Description("Image layer with the mouth drawings.")] string mouthLayerId,
        [Description("Audio layer with the voice, or a video layer with sound.")] string audioLayerId,
        [Description("Drawing name of the closed mouth.")] string closedDrawing,
        [Description("Open drawing names, 1-8, from slightly to wide open.")] string[] openDrawings,
        [Description("0-1: higher opens the mouth on quieter sounds. Default 0.5.")] double sensitivity = LipFlap.DefaultSensitivity) => Guard(() =>
    {
        var session = state.Session;
        var open = openDrawings ?? [];
        var plan = session.Read(p => LipFlap.Plan(p, mouthLayerId, audioLayerId, closedDrawing, open, sensitivity));
        var ffmpeg = state.RequireFfmpeg();
        var levels = AudioAnalyzer.FrameLevels(ffmpeg.Ffmpeg, session.Directory, plan);
        var result = session.Mutate($"auto_lip_flap {mouthLayerId}", p =>
        {
            var current = LipFlap.Plan(p, mouthLayerId, audioLayerId, closedDrawing, open, sensitivity);
            if (current.Windows.Count != plan.Windows.Count || current.Start != plan.Start)
            {
                throw new ZeyoCutException("the project changed while the sound was measured; run auto_lip_flap again");
            }

            return LipFlap.Apply(p, mouthLayerId, closedDrawing, open, sensitivity, current, levels);
        });
        return WithWarnings(result.Describe(), result.Warnings);
    });
}
