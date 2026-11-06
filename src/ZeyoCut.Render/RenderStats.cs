// SPDX-License-Identifier: MIT

using System.Diagnostics;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Render;

// The stages of rendering and encoding an export, in the order a frame goes through them.
public enum RenderStage
{
    Scene,
    Decode,
    Redaction,
    Matte,
    Text,
    Effects,
    Compositing,
    Fit,
    Encode
}

public sealed record LayerTime(string Id, string Type, double Seconds, int Frames);

public sealed record FrameTime(int Frame, double Milliseconds);

// Where an export's time went (report_render_stats). Stages are keyed by their camelCase names, in
// seconds summed over the threads that rendered; the layers and frames are the slowest ones.
// RenderSeconds is how long the rendering took from start to end. A chunked export (Chunks > 0) also
// gives how many chunks came from the cache, and the time it took to find them and to join them.
public sealed record RenderTimes(
    int Frames,
    int Workers,
    IReadOnlyDictionary<string, double> Stages,
    IReadOnlyList<LayerTime> Layers,
    IReadOnlyList<FrameTime> SlowestFrames,
    double AudioSeconds,
    double? EncoderCpuSeconds,
    double RenderSeconds = 0,
    int Chunks = 0,
    int ReusedChunks = 0,
    double KeySeconds = 0,
    double JoinSeconds = 0);

// Collects the times of one renderer, on one thread. Each Lap adds the time since the last one to a
// stage, so the stages add up to all the time measured.
public sealed class RenderStats
{
    public const int KeptLayers = 10;
    public const int KeptFrames = 5;

    private readonly long[] _stages = new long[Enum.GetValues<RenderStage>().Length];
    private readonly Dictionary<string, (string Type, long Ticks, int Frames, int LastFrame)> _layers = new(StringComparer.Ordinal);
    private readonly List<(int Frame, long Ticks)> _slowest = [];
    private long _last = Stopwatch.GetTimestamp();
    private int _frame = -1;
    private long _frameStarted;

    public static long Now => Stopwatch.GetTimestamp();

    // Starts measuring from now, leaving out the time before.
    public void Restart() => _last = Stopwatch.GetTimestamp();

    public void Lap(RenderStage stage)
    {
        var now = Stopwatch.GetTimestamp();
        _stages[(int)stage] += now - _last;
        _last = now;
    }

    public void BeginFrame(int frame)
    {
        _frame = frame;
        _frameStarted = Stopwatch.GetTimestamp();
    }

    public void EndFrame()
    {
        var ticks = Stopwatch.GetTimestamp() - _frameStarted;
        if (_slowest.Count == KeptFrames && ticks <= _slowest[^1].Ticks)
        {
            return;
        }

        var at = _slowest.FindIndex(f => f.Ticks < ticks);
        _slowest.Insert(at < 0 ? _slowest.Count : at, (_frame, ticks));
        if (_slowest.Count > KeptFrames)
        {
            _slowest.RemoveAt(KeptFrames);
        }
    }

    // A layer's own drawing (its children are layers of their own), from started until now.
    public void Layer(string id, string type, long started)
    {
        var ticks = Stopwatch.GetTimestamp() - started;
        _layers[id] = _layers.TryGetValue(id, out var sofar)
            ? (sofar.Type, sofar.Ticks + ticks, sofar.Frames + (sofar.LastFrame == _frame ? 0 : 1), _frame)
            : (type, ticks, 1, _frame);
    }

    // Adds the times of another thread's renderer.
    public void Add(RenderStats other)
    {
        for (var i = 0; i < _stages.Length; i++)
        {
            _stages[i] += other._stages[i];
        }

        foreach (var (id, time) in other._layers)
        {
            _layers[id] = _layers.TryGetValue(id, out var sofar)
                ? (sofar.Type, sofar.Ticks + time.Ticks, sofar.Frames + time.Frames, sofar.LastFrame)
                : time;
        }

        foreach (var (frame, ticks) in other._slowest)
        {
            _frame = frame;
            _frameStarted = Stopwatch.GetTimestamp() - ticks;
            EndFrame();
        }
    }

    public RenderTimes Times(
        int frames,
        double audioSeconds,
        double? encoderCpuSeconds,
        int workers = 1,
        double renderSeconds = 0,
        int chunks = 0,
        int reusedChunks = 0,
        double keySeconds = 0,
        double joinSeconds = 0) => new(
        frames,
        workers,
        Enum.GetValues<RenderStage>().ToDictionary(s => EnumNames.ToName(s), s => Seconds(_stages[(int)s])),
        _layers
            .OrderByDescending(l => l.Value.Ticks)
            .ThenBy(l => l.Key, StringComparer.Ordinal)
            .Take(KeptLayers)
            .Select(l => new LayerTime(l.Key, l.Value.Type, Seconds(l.Value.Ticks), l.Value.Frames))
            .ToList(),
        _slowest.Select(f => new FrameTime(f.Frame, Math.Round(f.Ticks * 1000.0 / Stopwatch.Frequency, 1))).ToList(),
        Math.Round(audioSeconds, 3),
        encoderCpuSeconds is { } cpu ? Math.Round(cpu, 3) : null,
        Math.Round(renderSeconds, 6),
        chunks,
        reusedChunks,
        Math.Round(keySeconds, 6),
        Math.Round(joinSeconds, 6));

    private static double Seconds(long ticks) => Math.Round(ticks / (double)Stopwatch.Frequency, 6);
}
