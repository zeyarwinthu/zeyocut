// SPDX-License-Identifier: MIT

using System.Text;

namespace ZeyoCut.Mcp.Demo;

public static class ToneGenerator
{
    public const int SampleRate = 48000;

    public static void WriteTone(string path, double frequency, double seconds, double amplitude = 0.4) =>
        Write(path, seconds, t => Math.Sin(2 * Math.PI * frequency * t) * amplitude * Envelope(t, seconds, 0.01, 0.05));

    public static void WriteBoing(string path, double seconds = 0.6, double amplitude = 0.45)
    {
        var phase = 0.0;
        var step = 1.0 / SampleRate;
        Write(path, seconds, t =>
        {
            var progress = t / seconds;
            var frequency = 180 + 520 * (1 - Math.Pow(1 - progress, 2)) + 35 * Math.Sin(2 * Math.PI * 14 * t);
            phase += 2 * Math.PI * frequency * step;
            var tone = Math.Sin(phase) + 0.25 * Math.Sin(2 * phase);
            return tone / 1.25 * amplitude * Envelope(t, seconds, 0.005, 0.25);
        });
    }

    public static double Duration(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Seek(28, SeekOrigin.Begin);
        var byteRate = reader.ReadInt32();
        reader.BaseStream.Seek(40, SeekOrigin.Begin);
        var dataSize = reader.ReadInt32();
        return dataSize / (double)byteRate;
    }

    private static double Envelope(double t, double length, double attack, double release)
    {
        if (t < attack)
        {
            return t / attack;
        }

        var remaining = length - t;
        return remaining < release ? Math.Max(0, remaining / release) : 1;
    }

    private static void Write(string path, double seconds, Func<double, double> sample)
    {
        var frames = (int)Math.Round(seconds * SampleRate);
        const short channels = 2;
        const short bitsPerSample = 16;
        var dataSize = frames * channels * bitsPerSample / 8;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var i = 0; i < frames; i++)
        {
            var value = (short)Math.Round(Math.Clamp(sample(i / (double)SampleRate), -1, 1) * short.MaxValue);
            writer.Write(value);
            writer.Write(value);
        }
    }
}
