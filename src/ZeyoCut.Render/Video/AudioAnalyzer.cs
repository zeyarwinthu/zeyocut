// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

// Sound levels in dBFS. silencedetect compares single samples with its threshold, so the noise floor is
// measured the same way: the peak level of 50 ms windows, taking a low percentile (the quiet parts).
public sealed record SoundLevels(double NoiseFloorDb, double LoudDb, double ThresholdDb, int Windows)
{
    public string Describe() =>
        $"noise floor {Db(NoiseFloorDb)} dB (peak level of the quietest 50 ms windows), loud parts {Db(LoudDb)} dB → silence below {Db(ThresholdDb)} dB";

    private static string Db(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

public static partial class AudioAnalyzer
{
    public const string Mono16k = "aformat=sample_fmts=flt:sample_rates=16000:channel_layouts=mono";
    public const double NoiseMarginDb = 6;
    public const double MinThresholdDb = -70;
    public const double SpeechMarginDb = 12;
    public const double SceneThreshold = 10;
    private const double Silent = -120;

    [GeneratedRegex(@"silence_start:\s*(-?[\d.]+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"silence_end:\s*(-?[\d.]+)")]
    private static partial Regex SilenceEnd();

    public static SoundLevels Levels(string ffmpeg, string file, double start, double length, CancellationToken cancellationToken = default)
    {
        var peaks = new List<double>();
        var args = Input(file, start, length);
        args.AddRange(["-vn", "-af", $"{Mono16k},asetnsamples=n=800:p=0,astats=metadata=1:reset=1:measure_overall=Peak_level:measure_perchannel=none,ametadata=mode=print:key=lavfi.astats.Overall.Peak_level:file=-", "-f", "null", "-"]);
        var result = ProcessRunner.RunLines(ffmpeg, args, line =>
        {
            const string prefix = "lavfi.astats.Overall.Peak_level=";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var text = line[prefix.Length..];
                peaks.Add(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && double.IsFinite(db) ? Math.Max(db, Silent) : Silent);
            }
        }, null, cancellationToken);
        Check(result, file, "measure the sound level of");
        return FromPeaks(peaks);
    }

    // Noise floor = 5th percentile of the window peaks that are not digital silence; the threshold is
    // NoiseMarginDb above it, but at least SpeechMarginDb below the loud parts and never under -70 dB.
    public static SoundLevels FromPeaks(IReadOnlyList<double> peaks)
    {
        var sound = peaks.Where(p => p > Silent).OrderBy(p => p).ToList();
        if (sound.Count == 0)
        {
            return new SoundLevels(Silent, Silent, MinThresholdDb, peaks.Count);
        }

        var floor = Percentile(sound, 0.05);
        var loud = Percentile(sound, 0.95);
        var threshold = Math.Max(MinThresholdDb, Math.Min(floor + NoiseMarginDb, loud - SpeechMarginDb));
        return new SoundLevels(Math.Round(floor, 1), Math.Round(loud, 1), Math.Round(threshold, 1), peaks.Count);
    }

    // Silences of at least minSilence seconds below thresholdDb, in source seconds.
    public static List<TimeRange> Silences(string ffmpeg, string file, double start, double length, double thresholdDb, double minSilence, CancellationToken cancellationToken = default)
    {
        var silences = new List<TimeRange>();
        double? open = null;
        var args = Input(file, start, length);
        args.AddRange(["-vn", "-af", $"{Mono16k},silencedetect=noise={Num(thresholdDb)}dB:duration={Num(minSilence)}", "-f", "null", "-"]);
        var result = ProcessRunner.RunLines(ffmpeg, args, null, line =>
        {
            if (SilenceStart().Match(line) is { Success: true } begin)
            {
                open = Math.Max(0, Parse(begin.Groups[1].Value)) + start;
            }
            else if (SilenceEnd().Match(line) is { Success: true } end && open is { } from)
            {
                silences.Add(new TimeRange(Math.Round(from, 4), Math.Round(Math.Min(Parse(end.Groups[1].Value) + start, start + length), 4)));
                open = null;
            }
        }, cancellationToken);
        Check(result, file, "detect silence in");
        if (open is { } last && start + length - last >= minSilence)
        {
            silences.Add(new TimeRange(Math.Round(last, 4), Math.Round(start + length, 4)));
        }

        return silences;
    }

    public static List<SceneCut> Scenes(string ffmpeg, string file, double duration, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var scenes = new List<SceneCut>();
        double? score = null;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-progress", "pipe:1",
            "-i", file, "-an", "-sn", "-dn",
            "-vf", $"scale='min(320,iw)':-2,scdet=threshold={Num(SceneThreshold)}:sc_pass=1,metadata=mode=print:file=-",
            "-f", "null", "-",
        };
        var result = ProcessRunner.RunLines(ffmpeg, args, line =>
        {
            if (line.StartsWith("lavfi.scd.score=", StringComparison.Ordinal))
            {
                score = Parse(line["lavfi.scd.score=".Length..]);
            }
            else if (line.StartsWith("lavfi.scd.time=", StringComparison.Ordinal))
            {
                scenes.Add(new SceneCut(Math.Round(Parse(line["lavfi.scd.time=".Length..]), 4), Math.Round(score ?? 0, 2)));
                score = null;
            }
            else if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && duration > 0
                && long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            {
                progress?.Invoke(Math.Clamp(micros / 1e6 / duration, 0, 1));
            }
        }, null, cancellationToken);
        Check(result, file, "detect scene cuts in");
        return scenes;
    }

    // Mono float samples of 'file' from 'start' for 'length' seconds (to the end when null).
    public static float[] Samples(string ffmpeg, string file, double start, double? length, int sampleRate, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error" };
        if (start > 0)
        {
            args.AddRange(["-ss", Num(start)]);
        }

        if (length is { } seconds)
        {
            args.AddRange(["-t", Num(seconds)]);
        }

        args.AddRange(["-i", file, "-vn", "-sn", "-dn", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"]);
        using var process = System.Diagnostics.Process.Start(ProcessRunner.StartInfo(ffmpeg, args))
            ?? throw new ZeyoCutException($"could not start {ffmpeg}");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new ZeyoCutException($"ffmpeg could not decode the sound of '{Path.GetFileName(file)}' (exit code {process.ExitCode}): {stderr.Result.Trim()}");
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length - (int)buffer.Length % 4);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    // The loudness of each frame of a lip flap plan, decoded from the source file at 16 kHz.
    public static double[] FrameLevels(string ffmpeg, string projectDirectory, LipFlapPlan plan, CancellationToken cancellationToken = default)
    {
        const int rate = 16000;
        var from = Math.Max(0, plan.Windows.Min(w => w.SourceStart));
        var to = plan.Windows.Max(w => w.SourceEnd);
        var samples = Samples(ffmpeg, ProjectStore.AssetPath(projectDirectory, plan.Asset), from, to - from, rate, cancellationToken);
        return WindowLevels(samples, rate, from, plan.Windows);
    }

    // RMS level in dBFS of each window (source seconds); 'samples' start at source time 'offset'.
    public static double[] WindowLevels(float[] samples, int sampleRate, double offset, IReadOnlyList<SoundWindow> windows)
    {
        var levels = new double[windows.Count];
        for (var w = 0; w < windows.Count; w++)
        {
            var first = Math.Clamp((int)Math.Floor((windows[w].SourceStart - offset) * sampleRate), 0, samples.Length);
            var last = Math.Clamp((int)Math.Ceiling((windows[w].SourceEnd - offset) * sampleRate), first, samples.Length);
            double sum = 0;
            for (var i = first; i < last; i++)
            {
                sum += samples[i] * (double)samples[i];
            }

            levels[w] = last > first && sum > 0 ? 10 * Math.Log10(sum / (last - first)) : Silent;
        }

        return levels;
    }

    public static void ExtractSpeechWav(string ffmpeg, string file, string output, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-y", "-i", file, "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", output };
        Check(ProcessRunner.RunLines(ffmpeg, args, null, null, cancellationToken), file, "extract the sound of");
    }

    private static List<string> Input(string file, double start, double length)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info" };
        if (start > 0)
        {
            args.AddRange(["-ss", Num(start)]);
        }

        args.AddRange(["-t", Num(length), "-i", file]);
        return args;
    }

    private static void Check(ProcessResult result, string file, string what)
    {
        if (result.ExitCode != 0)
        {
            throw new ZeyoCutException($"ffmpeg could not {what} '{Path.GetFileName(file)}' (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Floor(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

    private static double Parse(string text) => double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
