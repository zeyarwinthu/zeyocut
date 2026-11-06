// SPDX-License-Identifier: MIT

using System.Text;

namespace ZeyoCut.Tests.TestSupport;

// Sounds built sample by sample, so tests know exactly where every click and tone is.
public static class TestAudio
{
    // A short decaying 2 kHz click at each time.
    public static float[] Clicks(IEnumerable<double> times, double seconds, int sampleRate, double amplitude = 0.8)
    {
        var samples = new float[(int)Math.Round(seconds * sampleRate)];
        var length = (int)(0.02 * sampleRate);
        foreach (var time in times)
        {
            var first = (int)Math.Round(time * sampleRate);
            for (var i = 0; i < length && first + i < samples.Length; i++)
            {
                var t = i / (double)sampleRate;
                samples[first + i] += (float)(amplitude * Math.Exp(-t / 0.004) * Math.Sin(2 * Math.PI * 2000 * t));
            }
        }

        return samples;
    }

    // A kick on every beat, a quiet noisy hi-hat halfway between, and a steady low chord under it all
    // from the very start.
    public static float[] DrumLoop(IEnumerable<double> beats, double period, double seconds, int sampleRate)
    {
        var samples = new float[(int)Math.Round(seconds * sampleRate)];
        var random = new Random(1);
        foreach (var beat in beats)
        {
            var kick = (int)Math.Round(beat * sampleRate);
            for (var i = 0; i < (int)(0.25 * sampleRate) && kick + i < samples.Length; i++)
            {
                var t = i / (double)sampleRate;
                var fade = Math.Min(1, (0.25 - t) / 0.03);
                samples[kick + i] += (float)(0.7 * fade * Math.Exp(-t / 0.12) * Math.Sin(2 * Math.PI * (50 * t + 70 * 0.03 * (1 - Math.Exp(-t / 0.03)))));
            }

            var hat = (int)Math.Round((beat + period / 2) * sampleRate);
            for (var i = 0; i < (int)(0.05 * sampleRate) && hat + i < samples.Length; i++)
            {
                samples[hat + i] += (float)(0.15 * Math.Exp(-i / (double)sampleRate / 0.01) * (random.NextDouble() * 2 - 1));
            }
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)sampleRate;
            samples[i] += (float)(0.08 * Math.Sin(2 * Math.PI * 110 * t) + 0.05 * Math.Sin(2 * Math.PI * 164.8 * t));
        }

        return samples;
    }

    public static List<double> Beats(double bpm, double first, double seconds)
    {
        var beats = new List<double>();
        for (var t = first; t < seconds - 0.05; t += 60 / bpm)
        {
            beats.Add(t);
        }

        return beats;
    }

    // A 300 Hz tone where 'on' says so, silence elsewhere.
    public static float[] Tone(double seconds, int sampleRate, Func<double, bool> on, double amplitude = 0.4)
    {
        var samples = new float[(int)Math.Round(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)sampleRate;
            samples[i] = on(t) ? (float)(amplitude * Math.Sin(2 * Math.PI * 300 * t)) : 0;
        }

        return samples;
    }

    // Mono 16-bit PCM.
    public static string WriteWav(string path, float[] samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        var dataSize = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue));
        }

        return path;
    }
}
