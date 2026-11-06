// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// Length is in timeline seconds; the clip reads Length * Speed seconds of source from TrimIn.
public sealed record AudioClip(string LayerId, string Path, double Start, double TrimIn, double Length, double Volume, double FadeIn, double FadeOut, double Speed = 1, AudioRole Role = AudioRole.Music);

// OutputWidth/OutputHeight scale the rendered frames (a preset with the same aspect ratio).
public sealed record EncodeSettings(
    int Width,
    int Height,
    int Fps,
    int FrameCount,
    int Crf,
    string X264Preset = "veryfast",
    IReadOnlyList<string>? VideoOptions = null,
    int AudioKbps = 192,
    int? OutputWidth = null,
    int? OutputHeight = null,
    VideoEncoder? Encoder = null);

// The first loudnorm pass: what the mix measures (LUFS, dBTP, LU).
public sealed record LoudnessMeasurement(double InputI, double InputTp, double InputLra, double InputThresh, double TargetOffset);

public sealed record AudioMix(bool Ducking, double? LoudnessTarget, double TruePeak, LoudnessMeasurement? Measured);

public static class FfmpegArgs
{
    public const int SampleRate = 48000;

    public static List<AudioClip> AudioClips(Project project, string projectDirectory)
    {
        var clips = new List<AudioClip>();
        var end = FrameMath.RenderedDuration(project);
        foreach (var candidate in project.Layers)
        {
            if (candidate is VideoLayer video)
            {
                if (VideoSound(project, projectDirectory, video, end) is { } sound)
                {
                    clips.Add(sound);
                }

                continue;
            }

            if (candidate is not AudioLayer layer || !layer.Visible || layer.Start >= end)
            {
                continue;
            }

            var asset = project.FindAsset(layer.Asset)
                ?? throw new ZeyoCutException($"audio layer '{layer.Id}': asset '{layer.Asset}' not found");
            var path = ProjectStore.AssetPath(projectDirectory, asset);
            var sourceEnd = layer.TrimOut ?? asset.Duration ?? double.MaxValue;
            if (asset.Duration is { } sourceDuration)
            {
                sourceEnd = Math.Min(sourceEnd, sourceDuration);
            }

            var layerEnd = Math.Min(layer.End ?? end, end);
            var length = Math.Min(sourceEnd - layer.TrimIn, layerEnd - layer.Start);
            if (length <= 0.0005)
            {
                continue;
            }

            clips.Add(new AudioClip(layer.Id, path, layer.Start, layer.TrimIn, length, layer.Volume, layer.FadeIn, layer.FadeOut, Role: layer.Role));
        }

        return clips;
    }

    private static AudioClip? VideoSound(Project project, string projectDirectory, VideoLayer layer, double end)
    {
        if (!layer.Visible || layer.Muted || layer.Start >= end)
        {
            return null;
        }

        var asset = project.FindAsset(layer.Asset)
            ?? throw new ZeyoCutException($"video layer '{layer.Id}': asset '{layer.Asset}' not found");
        if (asset.HasAudio != true)
        {
            return null;
        }

        var length = Math.Min(layer.Length, end - layer.Start);
        if (length <= 0.0005)
        {
            return null;
        }

        return new AudioClip(
            layer.Id,
            ProjectStore.AssetPath(projectDirectory, asset),
            layer.Start,
            layer.TrimIn,
            length,
            layer.Volume,
            layer.FadeIn,
            layer.FadeOut,
            layer.Speed,
            layer.Role);
    }

    public static List<string> Build(EncodeSettings settings, IReadOnlyList<AudioClip> audio, string output, AudioMix? mix = null)
    {
        var duration = settings.FrameCount / (double)settings.Fps;
        var encoder = settings.Encoder ?? VideoEncoder.X264;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
            "-f", "rawvideo",
            "-pix_fmt", "rgba",
            "-s", $"{settings.Width}x{settings.Height}",
            "-framerate", settings.Fps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
        };

        foreach (var clip in audio)
        {
            args.Add("-i");
            args.Add(clip.Path);
        }

        var scale = settings.OutputWidth is { } outWidth && settings.OutputHeight is { } outHeight && (outWidth != settings.Width || outHeight != settings.Height)
            ? $"scale={outWidth}:{outHeight}:flags=lanczos:out_color_matrix=bt709:out_range=tv"
            : "scale=out_color_matrix=bt709:out_range=tv";

        // ffmpeg takes the color tags from the frames, so set them here rather than as encoder options.
        var filters = new List<string>
        {
            $"[0:v]{scale},format={encoder.PixelFormat},setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv[v]",
        };

        if (audio.Count > 0)
        {
            filters.AddRange(AudioFilters(audio, 1, mix, measure: false));
        }

        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[v]"]);
        if (audio.Count > 0)
        {
            args.AddRange(["-map", "[aout]"]);
        }

        args.AddRange(encoder.Options(settings.Crf, settings.X264Preset));
        args.AddRange(
        [
            "-pix_fmt", encoder.PixelFormat,
            "-colorspace", "bt709",
            "-color_primaries", "bt709",
            "-color_trc", "bt709",
            "-color_range", "tv",
        ]);
        args.AddRange(settings.VideoOptions ?? []);

        if (audio.Count > 0)
        {
            args.AddRange(["-c:a", "aac", "-b:a", $"{settings.AudioKbps}k", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-t", Num(duration), "-movflags", "+faststart", output]);
        return args;
    }

    // Joins the chunks of a chunked export (a concat list of MP4 files, the video copied as it is) and
    // mixes the audio into the same file, as Build does.
    public static List<string> BuildJoin(string concatList, int frameCount, int fps, IReadOnlyList<AudioClip> audio, string output, AudioMix? mix = null, int audioKbps = 192)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
            "-f", "concat", "-safe", "0", "-i", concatList,
        };

        foreach (var clip in audio)
        {
            args.Add("-i");
            args.Add(clip.Path);
        }

        args.AddRange(["-map", "0:v"]);
        if (audio.Count > 0)
        {
            args.AddRange(["-filter_complex", string.Join(";", AudioFilters(audio, 1, mix, measure: false)), "-map", "[aout]"]);
        }

        args.AddRange(["-c:v", "copy"]);
        if (audio.Count > 0)
        {
            args.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-t", Num(frameCount / (double)fps), "-movflags", "+faststart", output]);
        return args;
    }

    // First loudnorm pass: the same mix (fades, ducking) measured into the null muxer.
    public static List<string> MeasureArguments(IReadOnlyList<AudioClip> audio, AudioMix mix, double duration)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info" };
        foreach (var clip in audio)
        {
            args.AddRange(["-i", clip.Path]);
        }

        args.AddRange(["-filter_complex", string.Join(";", AudioFilters(audio, 0, mix, measure: true)), "-map", "[aout]", "-t", Num(duration), "-f", "null", "-"]);
        return args;
    }

    // The exported mix (second loudnorm pass included) as raw 48 kHz stereo float samples on stdout.
    public static List<string> MixArguments(IReadOnlyList<AudioClip> audio, AudioMix mix, double duration)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error" };
        foreach (var clip in audio)
        {
            args.AddRange(["-i", clip.Path]);
        }

        args.AddRange(
        [
            "-filter_complex", string.Join(";", AudioFilters(audio, 0, mix, measure: false)), "-map", "[aout]", "-t", Num(duration),
            "-c:a", "pcm_f32le", "-ac", "2", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1",
        ]);
        return args;
    }

    // The same mix as AAC in an .m4a file, for the preview page.
    public static List<string> PreviewMixArguments(IReadOnlyList<AudioClip> audio, AudioMix mix, double duration, string output)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-y" };
        foreach (var clip in audio)
        {
            args.AddRange(["-i", clip.Path]);
        }

        args.AddRange(
        [
            "-filter_complex", string.Join(";", AudioFilters(audio, 0, mix, measure: false)), "-map", "[aout]", "-t", Num(duration),
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", output,
        ]);
        return args;
    }

    // Reads the JSON that loudnorm prints at the end of the first pass.
    public static LoudnessMeasurement ParseLoudness(string stderr)
    {
        var end = stderr.LastIndexOf('}');
        var start = end < 0 ? -1 : stderr.LastIndexOf('{', end);
        if (start < 0)
        {
            throw new ZeyoCutException($"ffmpeg loudnorm printed no measurement: {stderr[Math.Max(0, stderr.Length - 400)..].Trim()}");
        }

        using var document = System.Text.Json.JsonDocument.Parse(stderr[start..(end + 1)]);
        double Read(string name)
        {
            var text = document.RootElement.GetProperty(name).GetString() ?? "";
            return text.Trim() switch
            {
                "-inf" => double.NegativeInfinity,
                "inf" or "+inf" => double.PositiveInfinity,
                var value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            };
        }

        return new LoudnessMeasurement(Read("input_i"), Read("input_tp"), Read("input_lra"), Read("input_thresh"), Read("target_offset"));
    }

    public static bool Ducks(IReadOnlyList<AudioClip> audio, AudioMix? mix) =>
        mix?.Ducking == true && audio.Any(c => c.Role == AudioRole.Voice) && audio.Any(c => c.Role == AudioRole.Music);

    // Each clip's chain, then (with ducking) music compressed by the voice as sidechain, then the
    // mix, then loudnorm: measuring (first pass) or applying the measurement linearly (second pass).
    private static List<string> AudioFilters(IReadOnlyList<AudioClip> audio, int firstInput, AudioMix? mix, bool measure)
    {
        var filters = new List<string>();
        var labels = new List<string>();
        for (var i = 0; i < audio.Count; i++)
        {
            filters.Add(AudioChain(audio[i], i + firstInput));
            labels.Add($"[a{i + firstInput}]");
        }

        var inputs = labels;
        if (Ducks(audio, mix))
        {
            var voice = Mix(filters, Enumerable.Range(0, audio.Count).Where(i => audio[i].Role == AudioRole.Voice).Select(i => labels[i]).ToList(), "voice");
            var music = Mix(filters, Enumerable.Range(0, audio.Count).Where(i => audio[i].Role == AudioRole.Music).Select(i => labels[i]).ToList(), "music");
            filters.Add($"{voice}asplit=2[voicemix][voicekey]");
            filters.Add("[voicekey]apad[voicepad]");
            filters.Add($"{music}[voicepad]sidechaincompress=threshold=0.03:ratio=6:attack=20:release=400:makeup=1[ducked]");
            inputs = ["[voicemix]", "[ducked]", .. Enumerable.Range(0, audio.Count).Where(i => audio[i].Role == AudioRole.Effects).Select(i => labels[i])];
        }

        var tail = new List<string> { $"amix=inputs={inputs.Count}:duration=longest:dropout_transition=0:normalize=0" };
        if (Ducks(audio, mix))
        {
            tail.Add(Restamp);
        }

        if (mix?.LoudnessTarget is { } target)
        {
            if (measure)
            {
                tail.Add($"loudnorm=I={Num(target)}:TP={Num(mix.TruePeak)}:LRA=11:print_format=json");
            }
            else if (mix.Measured is { } m)
            {
                var range = Math.Clamp(Math.Ceiling(m.InputLra) + 1, 11, 50);
                tail.Add($"loudnorm=I={Num(target)}:TP={Num(mix.TruePeak)}:LRA={Num(range)}:measured_I={Num(m.InputI)}:measured_TP={Num(m.InputTp)}:measured_LRA={Num(m.InputLra)}:measured_thresh={Num(m.InputThresh)}:offset={Num(m.TargetOffset)}:linear=true:print_format=summary");
                tail.Add($"aresample={SampleRate}");
            }
        }

        if (!measure)
        {
            tail.Add("apad");
        }

        filters.Add($"{string.Concat(inputs)}{string.Join(",", tail)}[aout]");
        return filters;
    }

    private static string Mix(List<string> filters, List<string> labels, string name)
    {
        if (labels.Count == 1)
        {
            return labels[0];
        }

        filters.Add($"{string.Concat(labels)}amix=inputs={labels.Count}:duration=longest:dropout_transition=0:normalize=0,{Restamp}[{name}]");
        return $"[{name}]";
    }

    // After the ducking branch, amix can end with a frame that has no timestamp; the final apad then
    // pads from it forever and the export never finishes. Every mix starts at 0 and has no gaps, so
    // timestamps counted from the samples are exact.
    private const string Restamp = "asetpts=N/SR/TB";

    private static string AudioChain(AudioClip clip, int index)
    {
        var parts = new List<string>
        {
            $"atrim=start={Num(clip.TrimIn)}:duration={Num(clip.Length * clip.Speed)}",
            "asetpts=PTS-STARTPTS",
            $"aformat=sample_fmts=fltp:sample_rates={SampleRate}:channel_layouts=stereo",
        };
        parts.AddRange(Tempo(clip.Speed));
        parts.Add($"volume={Num(clip.Volume)}");

        var fadeIn = Math.Min(clip.FadeIn, clip.Length);
        if (fadeIn > 0)
        {
            parts.Add($"afade=t=in:st=0:d={Num(fadeIn)}");
        }

        var fadeOut = Math.Min(clip.FadeOut, clip.Length);
        if (fadeOut > 0)
        {
            parts.Add($"afade=t=out:st={Num(clip.Length - fadeOut)}:d={Num(fadeOut)}");
        }

        var delayMs = (long)Math.Round(clip.Start * 1000);
        if (delayMs > 0)
        {
            parts.Add($"adelay={delayMs}:all=1");
        }

        return $"[{index}:a]{string.Join(",", parts)}[a{index}]";
    }

    // atempo takes 0.5..100 per instance, so slower speeds chain several.
    public static IEnumerable<string> Tempo(double speed)
    {
        if (Math.Abs(speed - 1) < 1e-9)
        {
            yield break;
        }

        var remaining = speed;
        while (remaining < 0.5)
        {
            yield return "atempo=0.5";
            remaining /= 0.5;
        }

        yield return $"atempo={Num(remaining)}";
    }

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
