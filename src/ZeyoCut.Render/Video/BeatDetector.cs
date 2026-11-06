// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Render.Video;

public sealed record BeatGrid(double? Bpm, double Confidence, List<double> Beats, List<Onset> Onsets, List<string> Notes);

// detect_beats: onsets from spectral flux (a hand-written FFT), each placed on the steepest rise of the
// sound near it; the tempo from the autocorrelation of the flux, favoring 120 BPM between octaves; then
// a constant beat grid placed on the loudest hits and fitted to the onsets by least squares.
public static class BeatDetector
{
    public const int SampleRate = 22050;
    public const int WindowSize = 1024;
    public const int Hop = 256;
    public const double MinBpm = 60;
    public const double MaxBpm = 200;
    public const double PreferredBpm = 120;
    public const int MinOnsets = 4;

    private const double HopSeconds = Hop / (double)SampleRate;
    private const int Block = 64;
    private const int Lookback = 8;
    private const int JumpSpan = 1024;
    private const double MinWeight = 0.01;
    private const int PeakRadius = 3;
    private const int MeanRadius = 8;
    private const double Delta = 0.07;
    private const double MinStrength = 0.1;
    private const double MinGap = 0.045;

    public static BeatAnalysis Analyze(string ffmpeg, string projectDirectory, Asset asset, Action<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Invoke(0);
        var samples = AudioAnalyzer.Samples(ffmpeg, ProjectStore.AssetPath(projectDirectory, asset), 0, null, SampleRate, cancellationToken);
        progress?.Invoke(0.4);
        var grid = Detect(samples, cancellationToken);
        var analysis = new BeatAnalysis
        {
            Asset = asset.Id,
            File = asset.File,
            Duration = Math.Round(samples.Length / (double)SampleRate, 3),
            AnalyzedUtc = DateTime.UtcNow,
            Bpm = grid.Bpm,
            Confidence = grid.Confidence,
            Beats = grid.Beats,
            Onsets = grid.Onsets,
            Notes = grid.Notes,
        };
        AnalysisFiles.Save(AnalysisFiles.BeatsPath(projectDirectory, asset), analysis);
        return analysis;
    }

    // Mono samples at SampleRate.
    public static BeatGrid Detect(float[] samples, CancellationToken cancellationToken = default)
    {
        var duration = samples.Length / (double)SampleRate;
        var flux = OnsetStrength(samples, cancellationToken);
        var onsets = Onsets(samples, flux);
        if (onsets.Count < MinOnsets)
        {
            return new BeatGrid(null, 0, [], onsets, [$"only {onsets.Count} onset(s), too few to find a tempo"]);
        }

        if (Tempo(flux) is not { } period)
        {
            return new BeatGrid(null, 0, [], onsets, [$"the sound is too short ({Num(duration)}s) to find a tempo"]);
        }

        var (phase, fitted, matched) = Fit(onsets, period);
        if (matched.Count < 2)
        {
            return new BeatGrid(null, 0, [], onsets, ["the onsets don't follow a steady beat"]);
        }

        var first = matched[0].Beat;
        var last = matched[^1].Beat;
        var beats = Enumerable.Range(first, last - first + 1)
            .Select(i => Math.Round(phase + i * fitted, 4))
            .Where(t => t >= 0 && t <= duration)
            .ToList();
        var confidence = Math.Round(matched.Count / (double)(last - first + 1), 3);
        var notes = new List<string>();
        if (confidence < 0.5)
        {
            notes.Add($"only {Math.Round(confidence * 100)}% of the beats have an onset on them, so the tempo may be off or the rhythm is loose");
        }

        return new BeatGrid(Math.Round(60 / fitted, 2), confidence, beats, onsets, notes);
    }

    // Spectral flux per hop: the summed rise of log magnitudes. Frame f covers samples
    // [f*Hop - WindowSize, f*Hop), so a sound that starts loud has an onset at 0; frames stop at the
    // last sample, so the end of the file isn't heard as a hit.
    public static double[] OnsetStrength(float[] samples, CancellationToken cancellationToken = default)
    {
        var count = samples.Length / Hop + 1;
        var window = new double[WindowSize];
        for (var i = 0; i < WindowSize; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / WindowSize);
        }

        const int bins = WindowSize / 2 + 1;
        var previous = new double[bins];
        var current = new double[bins];
        var re = new double[WindowSize];
        var im = new double[WindowSize];
        var flux = new double[count];
        for (var f = 0; f < count; f++)
        {
            if (f % 512 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var offset = f * Hop - WindowSize;
            for (var i = 0; i < WindowSize; i++)
            {
                var index = offset + i;
                re[i] = index >= 0 && index < samples.Length ? samples[index] * window[i] : 0;
                im[i] = 0;
            }

            Fft(re, im);
            double sum = 0;
            for (var k = 0; k < bins; k++)
            {
                current[k] = Math.Log(1 + 1000 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / WindowSize);
                sum += Math.Max(0, current[k] - previous[k]);
            }

            flux[f] = f == 0 ? 0 : sum;
            (previous, current) = (current, previous);
        }

        return flux;
    }

    // In-place radix-2 FFT; the length must be a power of two.
    public static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        if (n == 0 || (n & (n - 1)) != 0 || im.Length != n)
        {
            throw new ArgumentException("the FFT length must be a power of two, the same for both parts");
        }

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var half = length / 2;
            var angle = -2 * Math.PI / length;
            for (var k = 0; k < half; k++)
            {
                var cRe = Math.Cos(angle * k);
                var cIm = Math.Sin(angle * k);
                for (var a = k; a < n; a += length)
                {
                    var b = a + half;
                    var tRe = re[b] * cRe - im[b] * cIm;
                    var tIm = re[b] * cIm + im[b] * cRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                }
            }
        }
    }

    // Peaks of the flux that stand above their neighborhood, each moved to the steepest rise of the
    // sound in the half window before it (in 64-sample blocks). Strength is how much louder the sound
    // gets there, relative to the biggest jump in the file, so loud hits outweigh bright quiet ones.
    private static List<Onset> Onsets(float[] samples, double[] flux)
    {
        var positive = flux.Where(v => v > 0).Order().ToList();
        if (positive.Count == 0)
        {
            return [];
        }

        var scale = Math.Max(positive[Math.Clamp((int)Math.Floor(0.98 * (positive.Count - 1)), 0, positive.Count - 1)], 1e-9);
        var found = new List<(double Time, double Jump)>();
        for (var f = 1; f < flux.Length; f++)
        {
            var value = flux[f] / scale;
            if (value < MinStrength || !IsPeak(flux, f))
            {
                continue;
            }

            var from = Math.Max(0, f - MeanRadius);
            var to = Math.Min(flux.Length - 1, f + MeanRadius);
            double mean = 0;
            for (var i = from; i <= to; i++)
            {
                mean += flux[i] / scale;
            }

            if (value < mean / (to - from + 1) + Delta)
            {
                continue;
            }

            var time = Rise(samples, f);
            var onset = (Time: time, Jump: Jump(samples, time));
            if (found.Count > 0 && onset.Time - found[^1].Time < MinGap)
            {
                if (onset.Jump > found[^1].Jump)
                {
                    found[^1] = onset;
                }

                continue;
            }

            found.Add(onset);
        }

        var loudest = found.Count == 0 ? 0 : found.Max(o => o.Jump);
        return [.. found.Select(o => new Onset(Math.Round(o.Time, 4), Math.Round(loudest > 0 ? Math.Max(MinWeight, o.Jump / loudest) : 1, 3)))];
    }

    // Mean power of the JumpSpan samples after 'time' minus the mean power of those before, split two
    // blocks early so the attack is on the after side even when 'time' is a block late.
    private static double Jump(float[] samples, double time)
    {
        var at = (int)Math.Round(time * SampleRate) - 2 * Block;
        double Power(int from, int to)
        {
            from = Math.Clamp(from, 0, samples.Length);
            to = Math.Clamp(to, from, samples.Length);
            double sum = 0;
            for (var i = from; i < to; i++)
            {
                sum += samples[i] * (double)samples[i];
            }

            return to > from ? sum / (to - from) : 0;
        }

        return Math.Max(0, Power(at, at + JumpSpan) - Power(at - JumpSpan, at));
    }

    private static bool IsPeak(double[] flux, int f)
    {
        for (var k = 1; k <= PeakRadius; k++)
        {
            if ((f - k >= 0 && flux[f - k] >= flux[f]) || (f + k < flux.Length && flux[f + k] > flux[f]))
            {
                return false;
            }
        }

        return true;
    }

    // The middle of the block whose energy rises most above the loudest of the Lookback blocks before
    // it (so the swell within each cycle of a low steady tone doesn't count), between half a window
    // before frame f's end and half a hop after it. The rise is measured on the sound and on its
    // sample-to-sample difference, which hides low tones, and the larger of the two counts.
    private static double Rise(float[] samples, int f)
    {
        var end = f * Hop;
        var start = end - WindowSize / 2 - Hop;
        var blocks = (WindowSize / 2 + Hop + Hop / 2) / Block;
        var energy = new double[Lookback + blocks];
        var edges = new double[Lookback + blocks];
        for (var b = 0; b < energy.Length; b++)
        {
            var first = start + (b - Lookback) * Block;
            double sum = 0;
            double diff = 0;
            for (var i = Math.Max(1, first); i < Math.Min(samples.Length, first + Block); i++)
            {
                sum += samples[i] * (double)samples[i];
                var d = samples[i] - (double)samples[i - 1];
                diff += d * d;
            }

            energy[b] = sum;
            edges[b] = diff;
        }

        var rises = Rises(energy).Zip(Rises(edges), Math.Max).ToList();
        var best = rises.IndexOf(rises.Max());
        var center = start + best * Block + Block / 2;
        return Math.Max(0, center / (double)SampleRate);
    }

    // For each block after the first Lookback: log of its energy over the most of the Lookback before.
    private static IEnumerable<double> Rises(double[] energy)
    {
        var floor = Math.Max(energy.Max() * 1e-3, 1e-12);
        for (var b = Lookback; b < energy.Length; b++)
        {
            var before = 0.0;
            for (var k = b - Lookback; k < b; k++)
            {
                before = Math.Max(before, energy[k]);
            }

            yield return Math.Log((energy[b] + floor) / (before + floor));
        }
    }

    // The beat period in seconds: the autocorrelation lag of the onset strength between MinBpm and
    // MaxBpm, weighted toward PreferredBpm (within about an octave), refined between lags.
    private static double? Tempo(double[] flux)
    {
        var minLag = (int)Math.Floor(60 / MaxBpm / HopSeconds);
        var maxLag = (int)Math.Ceiling(60 / MinBpm / HopSeconds);
        if (flux.Length < maxLag * 2)
        {
            maxLag = flux.Length / 2;
        }

        if (maxLag <= minLag + 1)
        {
            return null;
        }

        var mean = flux.Average();
        var correlation = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double sum = 0;
            for (var t = 0; t + lag < flux.Length; t++)
            {
                sum += (flux[t] - mean) * (flux[t + lag] - mean);
            }

            correlation[lag] = sum / (flux.Length - lag);
        }

        var best = -1;
        var bestScore = double.NegativeInfinity;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var octaves = Math.Log2(60 / (lag * HopSeconds) / PreferredBpm);
            var score = correlation[lag] * Math.Exp(-0.5 * octaves * octaves / 0.81);
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        if (correlation[best] <= 0)
        {
            return null;
        }

        var (left, middle, right) = (correlation[best - 1], correlation[best], correlation[best + 1]);
        var bend = left - 2 * middle + right;
        var shift = bend < 0 ? Math.Clamp(0.5 * (left - right) / bend, -0.5, 0.5) : 0;
        return (best + shift) * HopSeconds;
    }

    // The phase that puts the most onset strength on the grid, then least-squares fits of the beats
    // to the onsets they catch, so the tempo follows the whole file.
    private static (double Phase, double Period, List<(int Beat, double Time)> Matched) Fit(List<Onset> onsets, double period)
    {
        var sigma = Math.Min(0.03, period * 0.06);
        var phase = onsets[0].Time;
        var bestScore = double.NegativeInfinity;
        foreach (var candidate in onsets)
        {
            double score = 0;
            foreach (var onset in onsets)
            {
                var offset = onset.Time - candidate.Time;
                var d = offset - period * Math.Round(offset / period);
                score += onset.Strength * Math.Exp(-0.5 * d * d / (sigma * sigma));
            }

            if (score > bestScore)
            {
                bestScore = score;
                phase = candidate.Time;
            }
        }

        var matched = Match(onsets, phase, period);
        for (var pass = 0; pass < 3 && matched.Count >= MinOnsets; pass++)
        {
            var meanBeat = matched.Average(m => m.Beat);
            var meanTime = matched.Average(m => m.Time);
            var spread = matched.Sum(m => (m.Beat - meanBeat) * (m.Beat - meanBeat));
            if (spread <= 0)
            {
                break;
            }

            var slope = matched.Sum(m => (m.Beat - meanBeat) * (m.Time - meanTime)) / spread;
            if (Math.Abs(slope - period) > period * 0.1)
            {
                break;
            }

            period = slope;
            phase = meanTime - slope * meanBeat;
            matched = Match(onsets, phase, period);
        }

        return (phase, period, matched);
    }

    // For each beat of the grid, the strongest onset within 12% of a period of it.
    private static List<(int Beat, double Time)> Match(List<Onset> onsets, double phase, double period)
    {
        var tolerance = period * 0.12;
        var byBeat = new Dictionary<int, Onset>();
        foreach (var onset in onsets)
        {
            var beat = (int)Math.Round((onset.Time - phase) / period);
            var d = onset.Time - (phase + beat * period);
            if (Math.Abs(d) <= tolerance && (!byBeat.TryGetValue(beat, out var held) || onset.Strength > held.Strength))
            {
                byBeat[beat] = onset;
            }
        }

        return [.. byBeat.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Time))];
    }

    public static string Describe(BeatAnalysis analysis, string savedPath)
    {
        var builder = new StringBuilder($"beats of '{analysis.Asset}' ({Num(analysis.Duration)}s)");
        if (analysis.Bpm is { } bpm && analysis.Beats.Count > 0)
        {
            builder.Append($": {bpm.ToString("0.0", CultureInfo.InvariantCulture)} BPM, {analysis.Beats.Count} beats every {Num(Math.Round(60 / bpm, 4))}s from {Num(analysis.Beats[0])}s to {Num(analysis.Beats[^1])}s; {Math.Round(analysis.Confidence * 100)}% of them land on one of the {analysis.Onsets.Count} onsets");
        }
        else
        {
            builder.Append($": no tempo; {analysis.Onsets.Count} onset(s)");
        }

        foreach (var note in analysis.Notes)
        {
            builder.Append($"\nnote: {note}");
        }

        if (analysis.Beats.Count > 0)
        {
            builder.Append($"\nbeats (s): {Times(analysis.Beats)}");
        }

        if (analysis.Onsets.Count > 0)
        {
            builder.Append($"\nonsets (s): {Times(analysis.Onsets.Select(o => o.Time).ToList())}");
        }

        builder.Append($"\nsaved {savedPath}");
        builder.Append("\ntimes are source seconds of the asset: a layer with start s0 and trimIn t0 plays source time t at s0 + t - t0");
        return builder.ToString();
    }

    private static string Times(List<double> times) =>
        string.Join(", ", times.Take(32).Select(Num)) + (times.Count > 32 ? $", … ({times.Count - 32} more in the file)" : "");

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
