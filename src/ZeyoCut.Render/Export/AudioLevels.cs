// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// One 100 ms step of the mix: the K-weighted energy summed over both channels and all samples,
// the sample peak and how many samples reached full scale.
public sealed record AudioHop(double Energy, int Samples, double Peak, int Clipped);

public sealed record AudioSection(double Start, double End, double Lufs, double PeakDb, int Clipped);

public sealed record AudioFlag(string Kind, double Start, double End, string Message);

public sealed record AudioLevelsReport(
    double Duration,
    int Clips,
    IReadOnlyList<string> Notes,
    double Lufs,
    double PeakDb,
    double SectionSeconds,
    IReadOnlyList<AudioSection> Sections,
    IReadOnlyList<AudioFlag> Flags,
    IReadOnlyList<TimeRange> EdgeSilences)
{
    public string Describe()
    {
        var builder = new StringBuilder(
            $"audio_levels 0–{Num(Duration)}s, the mix as exported ({Clips} clip(s); {string.Join("; ", Notes)}): {LufsText(Lufs)} integrated, peak {DbText(PeakDb)}");
        builder.Append($"\nsections ({Num(SectionSeconds)}s):");
        foreach (var section in Sections)
        {
            builder.Append($"\n- {Num(section.Start)}–{Num(section.End)}s: {(double.IsFinite(section.Lufs) ? $"{LufsText(section.Lufs)}, peak {DbText(section.PeakDb)}" : "silent")}");
            if (section.Clipped > 0)
            {
                builder.Append($", {section.Clipped} clipped sample(s)");
            }
        }

        if (Flags.Count == 0)
        {
            builder.Append("\nno clipping, no too-quiet sections and no silences in the middle");
        }
        else
        {
            builder.Append($"\nflagged ({Flags.Count}):");
            foreach (var flag in Flags)
            {
                builder.Append($"\n- {Num(flag.Start)}–{Num(flag.End)}s {flag.Kind}: {flag.Message}");
            }
        }

        if (EdgeSilences.Count > 0)
        {
            builder.Append($"\nnot flagged: silence at {string.Join(" and ", EdgeSilences.Select(s => $"{(s.Start <= 0 ? "the start" : "the end")} ({Num(s.Start)}–{Num(s.End)}s)"))}");
        }

        return builder.ToString();
    }

    internal static string LufsText(double value) => double.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS" : "silent";

    internal static string DbText(double value) => double.IsFinite(value) ? value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dBFS" : "-inf dBFS";

    internal static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

// ITU-R BS.1770 loudness at 48 kHz stereo: K-weighting, 400 ms blocks every 100 ms, gated at
// -70 LUFS and 10 LU below the ungated mean.
public sealed class LoudnessMeter
{
    public const int SampleRate = 48000;
    public const int HopSamples = SampleRate / 10;
    public const double AbsoluteGate = -70;
    public const double RelativeGate = 10;
    public const float FullScale = 0.999f;

    private readonly Biquad[] _shelf = [Biquad.Shelf(), Biquad.Shelf()];
    private readonly Biquad[] _highPass = [Biquad.HighPass(), Biquad.HighPass()];
    private readonly List<AudioHop> _hops = [];
    private double _energy;
    private int _samples;
    private double _peak;
    private int _clipped;

    public IReadOnlyList<AudioHop> Hops => _hops;

    // Interleaved stereo samples.
    public void Add(ReadOnlySpan<float> samples)
    {
        for (var i = 0; i + 1 < samples.Length; i += 2)
        {
            var left = samples[i];
            var right = samples[i + 1];
            var l = _highPass[0].Next(_shelf[0].Next(left));
            var r = _highPass[1].Next(_shelf[1].Next(right));
            _energy += l * l + r * r;
            var peak = Math.Max(Math.Abs(left), Math.Abs(right));
            _peak = Math.Max(_peak, peak);
            if (Math.Abs(left) >= FullScale)
            {
                _clipped++;
            }

            if (Math.Abs(right) >= FullScale)
            {
                _clipped++;
            }

            if (++_samples == HopSamples)
            {
                Flush();
            }
        }
    }

    public void Finish()
    {
        if (_samples > 0)
        {
            Flush();
        }
    }

    public static double Loudness(double meanSquare) => meanSquare > 0 ? -0.691 + 10 * Math.Log10(meanSquare) : double.NegativeInfinity;

    public static double Decibels(double peak) => peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;

    // Gated loudness of hops [first, first + count): the 400 ms blocks that fit, or one short block.
    public static double Integrated(IReadOnlyList<AudioHop> hops, int first, int count)
    {
        var blocks = new List<double>();
        for (var start = first; start + 4 <= first + count; start++)
        {
            blocks.Add(MeanSquare(hops, start, 4));
        }

        if (blocks.Count == 0 && count > 0)
        {
            blocks.Add(MeanSquare(hops, first, count));
        }

        var loud = blocks.Where(b => Loudness(b) > AbsoluteGate).ToList();
        if (loud.Count == 0)
        {
            return double.NegativeInfinity;
        }

        var gate = Loudness(loud.Average()) - RelativeGate;
        var kept = loud.Where(b => Loudness(b) > gate).ToList();
        return Loudness(kept.Average());
    }

    private static double MeanSquare(IReadOnlyList<AudioHop> hops, int first, int count)
    {
        double energy = 0;
        long samples = 0;
        for (var i = first; i < first + count; i++)
        {
            energy += hops[i].Energy;
            samples += hops[i].Samples;
        }

        return samples > 0 ? energy / samples : 0;
    }

    private void Flush()
    {
        _hops.Add(new AudioHop(_energy, _samples, _peak, _clipped));
        _energy = 0;
        _samples = 0;
        _peak = 0;
        _clipped = 0;
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1;
        private double _z2;

        public static Biquad Shelf() => new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);

        public static Biquad HighPass() => new(1, -2, 1, -1.99004745483398, 0.99007225036621);

        public double Next(double x)
        {
            var y = b0 * x + _z1;
            _z1 = b1 * x - a1 * y + _z2;
            _z2 = b2 * x - a2 * y;
            return y;
        }
    }
}

// audio_levels: mixes the project's audio exactly as the export would (fades, ducking, loudness
// normalization) into a pipe and meters it per section.
public static class AudioLevels
{
    public const double DefaultSection = 5;
    public const double MinSection = 1;
    public const double MaxSection = 60;

    // A section this many LU below the whole mix is too quiet.
    public const double QuietBelow = 10;

    // Without normalization, a whole mix below this is too quiet for most platforms.
    public const double QuietMix = -24;

    // Like ffmpeg's silencedetect, the sample level decides: a stretch whose peak stays below this
    // (dBFS) for at least MinSilence seconds is silence.
    public const double SilenceDb = -50;
    public const double MinSilence = 1;

    public static void CheckSection(double sectionSeconds)
    {
        if (!double.IsFinite(sectionSeconds) || sectionSeconds < MinSection || sectionSeconds > MaxSection)
        {
            throw new ZeyoCutException($"sectionSeconds must be between {MinSection} and {MaxSection} (got {AudioLevelsReport.Num(sectionSeconds)})");
        }
    }

    public static AudioLevelsReport? Measure(Project project, string projectDirectory, FfmpegPaths ffmpeg, double sectionSeconds, Action<double>? progress, CancellationToken cancellationToken)
    {
        CheckSection(sectionSeconds);
        var audio = FfmpegArgs.AudioClips(project, projectDirectory);
        if (audio.Count == 0)
        {
            return null;
        }

        foreach (var clip in audio.Where(c => !File.Exists(c.Path)))
        {
            throw new ZeyoCutException($"layer '{clip.LayerId}': file is missing: {clip.Path}");
        }

        var duration = FrameMath.FrameCount(project) / (double)project.Fps;
        var notes = new List<string>();
        progress?.Invoke(0);
        var mix = Exporter.PlanAudio(project, audio, duration, ffmpeg, notes, null, cancellationToken)!;
        progress?.Invoke(0.3);

        var meter = new LoudnessMeter();
        var expected = (long)Math.Round(duration * LoudnessMeter.SampleRate) * 8;
        using (var process = Process.Start(ProcessRunner.StartInfo(ffmpeg.Ffmpeg, FfmpegArgs.MixArguments(audio, mix, duration)))
            ?? throw new ZeyoCutException($"could not start {ffmpeg.Ffmpeg}"))
        {
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
            var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[LoudnessMeter.HopSamples * 8];
            long total = 0;
            int read;
            while ((read = stdout.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
            {
                var whole = read - read % 8;
                meter.Add(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole)));
                total += whole;
                progress?.Invoke(0.3 + 0.7 * Math.Min(1, total / (double)Math.Max(1, expected)));
            }

            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg could not mix the audio (exit code {process.ExitCode}):\n{stderr.Result.Trim()}");
            }
        }

        meter.Finish();
        return Report(project, audio, notes, meter.Hops, duration, sectionSeconds);
    }

    public static AudioLevelsReport Report(Project project, IReadOnlyList<AudioClip> audio, IReadOnlyList<string> notes, IReadOnlyList<AudioHop> hops, double duration, double sectionSeconds)
    {
        var lufs = LoudnessMeter.Integrated(hops, 0, hops.Count);
        var peak = LoudnessMeter.Decibels(hops.Select(h => h.Peak).DefaultIfEmpty(0).Max());
        var flags = new List<AudioFlag>();
        var sections = new List<AudioSection>();
        var perSection = Math.Max(1, (int)Math.Round(sectionSeconds * 10));
        for (var first = 0; first < hops.Count; first += perSection)
        {
            var count = Math.Min(perSection, hops.Count - first);
            var start = first / 10.0;
            var end = Math.Min(duration, (first + count) / 10.0);
            var silent = Enumerable.Range(first, count).Count(i => Silent(hops[i]));
            var section = new AudioSection(
                start,
                end,
                silent == count ? double.NegativeInfinity : Math.Round(LoudnessMeter.Integrated(hops, first, count), 2),
                Math.Round(LoudnessMeter.Decibels(Enumerable.Range(first, count).Max(i => hops[i].Peak)), 2),
                Enumerable.Range(first, count).Sum(i => hops[i].Clipped));
            sections.Add(section);
            if (section.Clipped > 0)
            {
                flags.Add(new AudioFlag("clipping", start, end, $"{section.Clipped} sample(s) at full scale (peak {AudioLevelsReport.DbText(section.PeakDb)}); lower the volume of {Playing(audio, start, end)}, or turn on loudness normalization (update_project loudnessTarget=-14)"));
            }

            if (double.IsFinite(section.Lufs) && double.IsFinite(lufs) && end - start >= MinSection && silent * 2 < count && section.Lufs < lufs - QuietBelow)
            {
                flags.Add(new AudioFlag("too quiet", start, end, $"{AudioLevelsReport.LufsText(section.Lufs)} is {AudioLevelsReport.Num(Math.Round(lufs - section.Lufs, 1))} LU below the whole mix; raise the volume of {Playing(audio, start, end)} (update_layer volume)"));
            }
        }

        if (!double.IsFinite(lufs))
        {
            flags.Add(new AudioFlag("silent", 0, duration, $"the whole mix is silent although {audio.Count} clip(s) play; check their volume and source files"));
        }
        else if (project.Audio.LoudnessTarget is null && lufs < QuietMix)
        {
            flags.Add(new AudioFlag("too quiet", 0, duration, $"the whole mix is {AudioLevelsReport.LufsText(lufs)} with loudness normalization off; most platforms play at about -14 LUFS, so set update_project loudnessTarget=-14"));
        }

        var edges = new List<TimeRange>();
        if (double.IsFinite(lufs))
        {
            foreach (var (first, count) in Silences(hops))
            {
                var start = first / 10.0;
                var end = Math.Min(duration, (first + count) / 10.0);
                if (first == 0 || first + count == hops.Count)
                {
                    edges.Add(new TimeRange(start, end));
                    continue;
                }

                var playing = audio.Where(c => c.Start < end && c.Start + c.Length > start).Select(c => $"'{c.LayerId}'").ToList();
                flags.Add(new AudioFlag("silence", start, end, playing.Count == 0
                    ? $"{AudioLevelsReport.Num(end - start)}s with no audio clip playing; fill it with music or a sound effect, or close the gap with remove_range"
                    : $"{AudioLevelsReport.Num(end - start)}s of silence while {string.Join(", ", playing)} play{(playing.Count == 1 ? "s" : "")}; check the source, or tighten it with remove_silences"));
            }
        }

        return new AudioLevelsReport(duration, audio.Count, notes, Math.Round(lufs, 2), Math.Round(peak, 2), sectionSeconds, sections, [.. flags.OrderBy(f => f.Start)], edges);
    }

    // Runs of silent 100 ms hops lasting at least MinSilence.
    private static IEnumerable<(int First, int Count)> Silences(IReadOnlyList<AudioHop> hops)
    {
        var minimum = (int)Math.Round(MinSilence * 10);
        var first = -1;
        for (var i = 0; i <= hops.Count; i++)
        {
            var silent = i < hops.Count && Silent(hops[i]);
            if (silent && first < 0)
            {
                first = i;
            }
            else if (!silent && first >= 0)
            {
                if (i - first >= minimum)
                {
                    yield return (first, i - first);
                }

                first = -1;
            }
        }
    }

    private static bool Silent(AudioHop hop) => LoudnessMeter.Decibels(hop.Peak) < SilenceDb;

    private static string Playing(IReadOnlyList<AudioClip> audio, double start, double end)
    {
        var playing = audio.Where(c => c.Start < end && c.Start + c.Length > start).Select(c => $"'{c.LayerId}'").ToList();
        return playing.Count == 0 ? "the clips there" : string.Join(", ", playing);
    }
}
