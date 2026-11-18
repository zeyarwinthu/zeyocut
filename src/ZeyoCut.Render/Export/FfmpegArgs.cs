// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// Length is in timeline seconds; the clip reads Length * Speed seconds of source from TrimIn.
// VolumeKeys (in timeline seconds) set its level over time instead of Volume; Gain (linear) and Pan
// (-1 left to 1 right) are its role's strip in the mixer.
public sealed record AudioClip(
    string LayerId,
    string Path,
    double Start,
    double TrimIn,
    double Length,
    double Volume,
    double FadeIn,
    double FadeOut,
    double Speed = 1,
    AudioRole Role = AudioRole.Music,
    IReadOnlyList<Keyframe>? VolumeKeys = null,
    double Gain = 1,
    double Pan = 0);

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

// DuckLevel (dB), DuckFadeIn and DuckFadeOut (seconds) shape the music under voice.
public sealed record AudioMix(
    bool Ducking,
    double? LoudnessTarget,
    double TruePeak,
    LoudnessMeasurement? Measured,
    double DuckLevel = AudioSettings.DefaultDuckLevel,
    double DuckFadeIn = AudioSettings.DefaultDuckFadeIn,
    double DuckFadeOut = AudioSettings.DefaultDuckFadeOut)
{
    public static AudioMix Of(AudioSettings settings) =>
        new(settings.Ducking, settings.LoudnessTarget, settings.TruePeak, null, settings.DuckLevel, settings.DuckFadeIn, settings.DuckFadeOut);
}

public static class FfmpegArgs
{
    public const int SampleRate = 48000;

    // A level that changes over time is worked out every 10 ms.
    public const int EnvelopeSamples = SampleRate / 100;

    // A duck fade of 0 still takes this long, so the music doesn't click.
    public const double MinDuckFade = 0.005;

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

            if (candidate is not AudioLayer layer || !layer.Visible || layer.Start >= end || project.Audio.For(layer.Role).Muted)
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

            var strip = project.Audio.For(layer.Role);
            clips.Add(new AudioClip(layer.Id, path, layer.Start, layer.TrimIn, length, layer.Volume.Value, layer.FadeIn, layer.FadeOut, Role: layer.Role,
                VolumeKeys: Keys(layer.Volume), Gain: Gain(strip), Pan: strip.Pan));
        }

        return clips;
    }

    private static AudioClip? VideoSound(Project project, string projectDirectory, VideoLayer layer, double end)
    {
        if (!layer.Visible || layer.Muted || layer.Start >= end || project.Audio.For(layer.Role).Muted)
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

        var strip = project.Audio.For(layer.Role);
        return new AudioClip(
            layer.Id,
            ProjectStore.AssetPath(projectDirectory, asset),
            layer.Start,
            layer.TrimIn,
            length,
            layer.Volume.Value,
            layer.FadeIn,
            layer.FadeOut,
            layer.Speed,
            layer.Role,
            Keys(layer.Volume),
            Gain(strip),
            strip.Pan);
    }

    private static List<Keyframe>? Keys(AnimatedValue volume) =>
        volume.Keyframes.Count == 0
            ? null
            : volume.Keyframes.Select(k => new Keyframe { Time = k.Time, Value = k.Value, Easing = k.Easing, Bezier = k.Bezier?.ToArray() }).ToList();

    private static double Gain(RoleMix strip) => Math.Pow(10, strip.Gain / 20);

    // A clip's level over its own seconds (0 at its start), from its volume keyframes: each segment's
    // ends, a step just before a held one's end, and points every 50 ms (at most 24) along an eased
    // one, so straight pieces between them follow its curve.
    public static List<(double Time, double Value)> VolumePoints(AudioClip clip)
    {
        var value = new AnimatedValue(clip.Volume) { Keyframes = [.. clip.VolumeKeys ?? []] };
        var (from, to) = (clip.Start, clip.Start + clip.Length);
        var times = new SortedSet<double> { from, to };
        var keys = value.Keyframes;
        for (var i = 0; i < keys.Count; i++)
        {
            times.Add(keys[i].Time);
            if (i + 1 == keys.Count || keys[i + 1].Time <= from || keys[i].Time >= to)
            {
                continue;
            }

            var (a, b) = (keys[i].Time, keys[i + 1].Time);
            if (keys[i].Easing == Easing.Hold)
            {
                times.Add(b - 0.002);
            }
            else if (keys[i].Easing != Easing.Linear)
            {
                var steps = Math.Clamp((int)Math.Ceiling((b - a) / 0.05), 2, 24);
                for (var step = 1; step < steps; step++)
                {
                    times.Add(a + (b - a) * step / steps);
                }
            }
        }

        return times.Where(t => t >= from - 1e-9 && t <= to + 1e-9)
            .Select(t => (Math.Round(t - from, 6), Interpolator.Evaluate(value, t)))
            .ToList();
    }

    // The music's level under ducking (1 elsewhere), in timeline seconds: down to the duck level
    // where voice clips play, over DuckFadeIn seconds before each and back up over DuckFadeOut after
    // it. Between close clips it comes back up only as far as the fades reach.
    public static List<(double Time, double Value)> DuckPoints(IReadOnlyList<AudioClip> audio, AudioMix mix)
    {
        var spans = new List<(double Start, double End)>();
        foreach (var (start, end) in audio.Where(c => c.Role == AudioRole.Voice).Select(c => (c.Start, c.Start + c.Length)).OrderBy(s => s.Item1))
        {
            if (spans.Count > 0 && start <= spans[^1].End)
            {
                spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
            }
            else
            {
                spans.Add((start, end));
            }
        }

        if (spans.Count == 0)
        {
            return [];
        }

        var down = Math.Max(mix.DuckFadeIn, MinDuckFade);
        var up = Math.Max(mix.DuckFadeOut, MinDuckFade);
        double Depth(double t) => spans.Max(s => t < s.Start ? Math.Max(0, 1 - (s.Start - t) / down) : t <= s.End ? 1 : Math.Max(0, 1 - (t - s.End) / up));

        var times = new SortedSet<double>();
        foreach (var (start, end) in spans)
        {
            times.Add(Math.Max(0, start - down));
            times.Add(start);
            times.Add(end);
            times.Add(end + up);
        }

        // Where one clip's way back up meets a later one's way down.
        for (var i = 0; i < spans.Count; i++)
        {
            for (var j = i + 1; j < spans.Count && spans[j].Start - spans[i].End < up + down; j++)
            {
                times.Add((spans[i].End * down + spans[j].Start * up) / (up + down));
            }
        }

        var level = Math.Pow(10, mix.DuckLevel / 20);
        return times.Where(t => t >= 0).Select(t => (Math.Round(t, 6), 1 - Depth(t) * (1 - level))).ToList();
    }

    // A function of t through the points, straight between them, as an ffmpeg expression: the first
    // value before them and the last after them. The ifs are nested as a balanced tree, so even a
    // long envelope is only a few levels deep.
    public static string Expression(IReadOnlyList<(double Time, double Value)> points)
    {
        if (points.Count == 0)
        {
            return "1";
        }

        // A clip's envelope starts at 0, where t starts, so nothing comes before its first point.
        var starts = new List<double>();
        var pieces = points[0].Time > 0 ? new List<string> { Num(points[0].Value) } : [];
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (t0, v0) = points[i];
            var (t1, v1) = points[i + 1];
            if (t1 - t0 < 1e-9)
            {
                continue;
            }

            var slope = (v1 - v0) / (t1 - t0);
            if (pieces.Count > 0)
            {
                starts.Add(t0);
            }

            pieces.Add(Math.Abs(slope) < 1e-9
                ? Num(v0)
                : $"{Num(v0)}{(slope < 0 ? "-" : "+")}{Num(Math.Abs(slope))}*(t-{Num(t0)})");
        }

        if (pieces.Count > 0)
        {
            starts.Add(points[^1].Time);
        }

        pieces.Add(Num(points[^1].Value));
        return Tree(starts, pieces, 0, pieces.Count - 1);
    }

    // pieces[i] holds from starts[i - 1]; pieces[0] from the beginning.
    private static string Tree(List<double> starts, List<string> pieces, int first, int last)
    {
        if (first == last)
        {
            return pieces[first];
        }

        var middle = (first + last + 1) / 2;
        return $"if(lt(t,{Num(starts[middle - 1])}),{Tree(starts, pieces, first, middle - 1)},{Tree(starts, pieces, middle, last)})";
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
        mix?.Ducking == true && mix.DuckLevel < 0 && audio.Any(c => c.Role == AudioRole.Voice) && audio.Any(c => c.Role == AudioRole.Music);

    // Each clip's chain, then (with ducking) the music lowered where voice clips play, then the mix,
    // then loudnorm: measuring (first pass) or applying the measurement linearly (second pass).
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
            List<string> Labels(AudioRole role) => Enumerable.Range(0, audio.Count).Where(i => audio[i].Role == role).Select(i => labels[i]).ToList();
            var music = Mix(filters, Labels(AudioRole.Music), "music");
            filters.Add($"{music}asetnsamples=n={EnvelopeSamples}:p=0,volume=volume='{Expression(DuckPoints(audio, mix!))}':eval=frame[ducked]");
            inputs = [.. Labels(AudioRole.Voice), "[ducked]", .. Labels(AudioRole.Effects)];
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
        if (clip.VolumeKeys is { Count: > 0 })
        {
            var level = Expression(VolumePoints(clip));
            parts.Add($"asetnsamples=n={EnvelopeSamples}:p=0");
            parts.Add($"volume=volume='{(Math.Abs(clip.Gain - 1) < 1e-9 ? level : $"{Num(clip.Gain)}*({level})")}':eval=frame");
        }
        else
        {
            parts.Add($"volume={Num(clip.Volume * clip.Gain)}");
        }

        if (Math.Abs(clip.Pan) > 1e-9)
        {
            parts.Add($"pan=stereo|c0={Num(clip.Pan > 0 ? 1 - clip.Pan : 1)}*c0|c1={Num(clip.Pan < 0 ? 1 + clip.Pan : 1)}*c1");
        }

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
