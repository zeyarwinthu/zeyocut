// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCvSharp;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Render;

// One AI feature system_check measures: its model and one unit of its work at 1080p (a video frame,
// or one image for aiImage). ScalesWithPicture: the model's time grows with the frame's area (RVM
// works on a copy downsampled to about 512 pixels, so it doesn't). The rough times stand in until this
// computer has been measured.
public sealed record AiFeature(string Id, string Name, AiModel Model, bool PerFrame, bool ScalesWithPicture, double RoughMs, double RoughLoadMs, Func<AiSession, Action> Prepare);

public static class AiFeatures
{
    public static readonly AiFeature AiVideo = new(
        "aiVideo", "aiVideo matte", AiModels.RvmMobileNet, PerFrame: true, ScalesWithPicture: false, RoughMs: 120, RoughLoadMs: 500,
        session => AiMattes.VideoStep(session, AiBenchmark.Width, AiBenchmark.Height));

    public static readonly AiFeature AiImage = new(
        "aiImage", "aiImage matte", AiModels.BiRefNetLite, PerFrame: false, ScalesWithPicture: false, RoughMs: 7000, RoughLoadMs: 3000,
        session =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(AiBenchmark.Width, AiBenchmark.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new ZeyoCutException("could not create a surface for the benchmark");
            surface.Canvas.Clear(new SKColor(128, 128, 128));
            var picture = surface.Snapshot();
            return () => AiMattes.Image(session, picture);
        });

    public static readonly AiFeature Faces = new(
        "faces", "face detection", AiModels.YuNet, PerFrame: true, ScalesWithPicture: true, RoughMs: 60, RoughLoadMs: 100,
        session =>
        {
            var picture = new Mat(AiBenchmark.Height, AiBenchmark.Width, MatType.CV_8UC3, new Scalar(128, 128, 128));
            return () => FaceDetector.Detect(session, picture, 0.3);
        });

    public static readonly AiFeature Plates = new(
        "plates", "plate detection", AiModels.Plates, PerFrame: true, ScalesWithPicture: false, RoughMs: 150, RoughLoadMs: 200,
        session =>
        {
            var picture = new Mat(AiBenchmark.Height, AiBenchmark.Width, MatType.CV_8UC3, new Scalar(128, 128, 128));
            return () => PlateDetector.Detect(session, picture, 0.15);
        });

    public static IReadOnlyList<AiFeature> All { get; } = [AiVideo, AiImage, Faces, Plates];

    public static AiFeature Get(string id) =>
        All.FirstOrDefault(f => f.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ZeyoCutException($"unknown AI feature '{id}'; features: {string.Join(", ", All.Select(f => f.Id))}");

    public static AiFeature For(MatteMethod method) => method switch
    {
        MatteMethod.AiVideo => AiVideo,
        MatteMethod.AiImage => AiImage,
        _ => throw new ZeyoCutException($"{method} doesn't use a model"),
    };
}

public sealed record Measured(double LoadMs, double RunMs);

// The time of one feature on each device; a problem says why a device has no time.
public sealed class FeatureTiming
{
    public string Feature { get; set; } = "";

    public string Model { get; set; } = "";

    public double? CpuMs { get; set; }

    public double? CpuLoadMs { get; set; }

    public string? CpuProblem { get; set; }

    public double? GpuMs { get; set; }

    public double? GpuLoadMs { get; set; }

    public string? GpuProblem { get; set; }
}

// What system_check measured, saved as benchmark.json in the library folder.
public sealed class BenchmarkResults
{
    public DateTime CheckedUtc { get; set; }

    public string Machine { get; set; } = "";

    public double? DecodeMs { get; set; }

    public string? DecodeProblem { get; set; }

    public List<FeatureTiming> Features { get; set; } = [];

    public FeatureTiming? Find(AiFeature feature) =>
        Features.FirstOrDefault(f => f.Feature == feature.Id && f.Model == AiBenchmark.Identity(feature.Model));
}

public sealed record DeviceChoice(AiDevice Device, string Why);

public sealed record AiEstimate(double Seconds, bool Measured)
{
    // Longer jobs wait for confirmLongJob, so the user hears how long they take first.
    public const double LongSeconds = 300;

    public bool IsLong => Seconds > LongSeconds;

    public AiEstimate Plus(AiEstimate other) => new(Seconds + other.Seconds, Measured && other.Measured);

    public string About => $"about {AiBenchmark.Duration(Seconds)}";

    public string Describe() => $"{About} ({(Measured ? "measured by system_check" : "a rough guess; system_check measures this computer")})";
}

// How the benchmark runs each part; system_check runs DirectML in a copy of the server, so a driver
// that hangs can't hang the server.
public interface IBenchmarkRunner
{
    double? DecodeMs(CancellationToken cancellationToken);

    Measured Cpu(AiFeature feature, string modelPath, CancellationToken cancellationToken);

    Measured Gpu(AiFeature feature, string modelPath, CancellationToken cancellationToken);
}

public sealed partial class AiBenchmarkRunner(string? ffmpeg, string? executable, IReadOnlyList<string> prefix, TimeSpan? gpuTimeout = null) : IBenchmarkRunner
{
    public static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(60);

    public double? DecodeMs(CancellationToken cancellationToken) =>
        ffmpeg is null ? throw new ZeyoCutException("ffmpeg wasn't found, so decoding wasn't measured") : AiBenchmark.MeasureDecode(ffmpeg, cancellationToken);

    public Measured Cpu(AiFeature feature, string modelPath, CancellationToken cancellationToken) =>
        AiBenchmark.Measure(feature, modelPath, AiDevice.Cpu, cancellationToken);

    public Measured Gpu(AiFeature feature, string modelPath, CancellationToken cancellationToken)
    {
        if (executable is null)
        {
            throw new ZeyoCutException("not tried: the server can't start a copy of itself to try it");
        }

        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(executable, [.. prefix, "benchmark", feature.Id, modelPath, "gpu"], gpuTimeout ?? GpuTimeout);
        }
        catch (ZeyoCutException ex) when (ex.Message.Contains("timed out", StringComparison.Ordinal))
        {
            throw new ZeyoCutException($"no answer within {(gpuTimeout ?? GpuTimeout).TotalSeconds:0} s, so it was stopped (DirectML can hang with some GPU drivers)");
        }

        var json = result.StandardOutput.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (result.ExitCode != 0 || json is null)
        {
            var lines = result.StandardError.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            throw new ZeyoCutException(Problem(lines.FirstOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal))?["error: ".Length..]
                ?? lines.FirstOrDefault() ?? $"exit code {result.ExitCode}"));
        }

        return JsonSerializer.Deserialize<Measured>(json, AiBenchmark.Json) ?? throw new ZeyoCutException("the benchmark gave no result");
    }

    // ONNX Runtime's message, without its source paths: a driver failure keeps its code and sentence
    // ("the GPU driver failed: 887A0020 An internal issue prevented the driver from ...").
    internal static string Problem(string message)
    {
        if (DriverError().Match(message) is { Success: true } driver)
        {
            return $"the GPU driver failed: {driver.Groups[1].Value} {driver.Groups[2].Value.Trim().TrimEnd('.')}";
        }

        var text = ErrorCode().Replace(message, "").Trim();
        return text.Length <= 200 ? text : text[..200].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\b(887[0-9A-F]{5}) ([^.]+\.)")]
    private static partial Regex DriverError();

    [GeneratedRegex(@"^\[ErrorCode:[^\]]*\]\s*")]
    private static partial Regex ErrorCode();
}

public static class AiBenchmark
{
    public const int Width = 1920;
    public const int Height = 1080;
    public const int Fps = 30;
    public const string FileName = "benchmark.json";

    // DirectML is used for a model only when it takes at most this share of the CPU's time.
    public const double FasterShare = 0.8;

    // Decoding a 1080p frame until this computer is measured.
    public const double RoughDecodeMs = 8;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Identity(AiModel model) => $"{model.FileName}:{model.Sha256[..12]}";

    // Load, one warm-up run (the first run allocates, and DirectML compiles), then up to 3 timed runs
    // within 3 s; LoadMs is what a job spends once before its steady pace.
    public static Measured Measure(AiFeature feature, string modelPath, AiDevice device, CancellationToken cancellationToken)
    {
        var load = Stopwatch.StartNew();
        using var session = AiSession.Open(modelPath, device);
        if (device == AiDevice.Gpu && session.Device != AiSession.DirectMl)
        {
            var problem = session.Device.StartsWith(AiSession.DirectMlUnavailable, StringComparison.Ordinal)
                ? session.Device[AiSession.DirectMlUnavailable.Length..].TrimEnd(')')
                : session.Device;
            throw new ZeyoCutException($"DirectML can't start: {problem}");
        }

        var run = feature.Prepare(session);
        var opened = load.Elapsed.TotalMilliseconds;
        var warm = Stopwatch.StartNew();
        run();
        var warmMs = warm.Elapsed.TotalMilliseconds;
        var times = new List<double>();
        var total = Stopwatch.StartNew();
        while (times.Count == 0 || (times.Count < 3 && total.Elapsed < TimeSpan.FromSeconds(3)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var one = Stopwatch.StartNew();
            run();
            times.Add(one.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var steady = times[times.Count / 2];
        return new Measured(Math.Round(opened + Math.Max(0, warmMs - steady), 1), Math.Round(steady, 1));
    }

    // Decoding a 3 s 1080p test clip through the same ffmpeg command previews and jobs use.
    public static double MeasureDecode(string ffmpeg, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "zeyocut-benchmark-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "1080p.mp4");
            var made = ProcessRunner.Run(ffmpeg,
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size={Width}x{Height}:rate={Fps}:duration=3",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", file,
            ], TimeSpan.FromMinutes(2));
            if (made.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg could not make the test clip: {made.StandardError.Split('\n')[0].Trim()}");
            }

            var stopwatch = Stopwatch.StartNew();
            var frames = 0;
            using (var reader = new VideoFrameReader(ffmpeg, file, 0, 0, Width, Height, null))
            {
                while (reader.Advance())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    frames++;
                }

                reader.ThrowIfFailed();
            }

            if (frames == 0)
            {
                throw new ZeyoCutException("no frames could be decoded from the test clip");
            }

            return Math.Round(stopwatch.Elapsed.TotalMilliseconds / frames, 1);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static BenchmarkResults Run(
        IReadOnlyList<(AiFeature Feature, string ModelPath)> models,
        IBenchmarkRunner runner,
        string machine,
        Action<double, string>? progress,
        CancellationToken cancellationToken)
    {
        var steps = 1 + models.Count * 2;
        var step = 0;
        void Next(string stage) => progress?.Invoke(step++ / (double)steps, stage);

        var results = new BenchmarkResults { CheckedUtc = DateTime.UtcNow, Machine = machine };
        Next("decoding 1080p video");
        try
        {
            results.DecodeMs = runner.DecodeMs(cancellationToken);
        }
        catch (ZeyoCutException ex)
        {
            results.DecodeProblem = ex.Message;
        }

        foreach (var (feature, path) in models)
        {
            var timing = new FeatureTiming { Feature = feature.Id, Model = Identity(feature.Model) };
            Next($"{feature.Model.Name} on the CPU");
            try
            {
                var cpu = runner.Cpu(feature, path, cancellationToken);
                (timing.CpuMs, timing.CpuLoadMs) = (cpu.RunMs, cpu.LoadMs);
            }
            catch (ZeyoCutException ex)
            {
                timing.CpuProblem = ex.Message;
            }

            Next($"{feature.Model.Name} on DirectML");
            try
            {
                var gpu = runner.Gpu(feature, path, cancellationToken);
                (timing.GpuMs, timing.GpuLoadMs) = (gpu.RunMs, gpu.LoadMs);
            }
            catch (ZeyoCutException ex)
            {
                timing.GpuProblem = ex.Message;
            }

            results.Features.Add(timing);
        }

        return results;
    }

    // The saved results, or null when there are none or they were measured on other hardware.
    public static BenchmarkResults? Load(string path, string machine)
    {
        try
        {
            var results = File.Exists(path) ? JsonSerializer.Deserialize<BenchmarkResults>(File.ReadAllText(path), Json) : null;
            return results is not null && results.Machine == machine ? results : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, BenchmarkResults results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        File.WriteAllText(part, JsonSerializer.Serialize(results, Json));
        File.Move(part, path, overwrite: true);
    }

    // auto: DirectML only where the benchmark found it works and at least 20% faster than the CPU.
    public static DeviceChoice Pick(AiDevice setting, AiFeature feature, BenchmarkResults? saved)
    {
        switch (setting)
        {
            case AiDevice.Cpu:
                return new DeviceChoice(AiDevice.Cpu, "AiDevice=cpu");
            case AiDevice.Gpu:
                return new DeviceChoice(AiDevice.Gpu, "AiDevice=gpu");
        }

        var timing = saved?.Find(feature);
        if (timing is null)
        {
            return new DeviceChoice(AiDevice.Cpu, "system_check hasn't measured it yet");
        }

        if (timing.GpuMs is not { } gpu)
        {
            return new DeviceChoice(AiDevice.Cpu, "DirectML didn't work in system_check");
        }

        if (timing.CpuMs is not { } cpu)
        {
            return new DeviceChoice(AiDevice.Gpu, "it didn't run on the CPU in system_check");
        }

        return gpu <= cpu * FasterShare
            ? new DeviceChoice(AiDevice.Gpu, $"DirectML was {Ratio(cpu / gpu)}x as fast as the CPU in system_check")
            : new DeviceChoice(AiDevice.Cpu, $"DirectML wasn't fast enough in system_check: {Milliseconds(gpu)} against {Milliseconds(cpu)}");
    }

    // units frames (or images) of width x height on that device: the measured times, decoding (for
    // video) and the models that work at full size scaled by the picture's area, plus the model's loading.
    public static AiEstimate Estimate(AiFeature feature, AiDevice device, int units, int width, int height, BenchmarkResults? saved)
    {
        var timing = saved?.Find(feature);
        var gpu = device == AiDevice.Gpu && timing?.GpuMs is not null;
        var measured = gpu ? timing!.GpuMs : timing?.CpuMs;
        var load = gpu ? timing!.GpuLoadMs : timing?.CpuLoadMs;
        var scale = feature.PerFrame ? width * (double)height / (Width * Height) : 1;
        var perUnit = (measured ?? feature.RoughMs) * (feature.ScalesWithPicture ? scale : 1) + (feature.PerFrame ? (saved?.DecodeMs ?? RoughDecodeMs) * scale : 0);
        return new AiEstimate((perUnit * Math.Max(0, units) + (load ?? feature.RoughLoadMs)) / 1000, measured is not null);
    }

    // A minute of 1080p video (or one image) on the device auto, cpu or gpu would use.
    public static AiEstimate PerMinute(AiFeature feature, AiDevice device, BenchmarkResults? saved) =>
        Estimate(feature, device, feature.PerFrame ? 60 * Fps : 1, Width, Height, saved);

    public static List<string> Describe(BenchmarkResults results, AiDevice setting, string savedPath)
    {
        var lines = new List<string>
        {
            results.DecodeMs is { } decode
                ? $"decoding 1080p video: {Milliseconds(decode)} a frame"
                : $"decoding 1080p video: not measured ({results.DecodeProblem})",
        };
        foreach (var timing in results.Features)
        {
            var feature = AiFeatures.Get(timing.Feature);
            var cpu = timing.CpuMs is { } c ? $"CPU {Milliseconds(c)}" : $"CPU: {timing.CpuProblem}";
            var gpu = timing.GpuMs is { } g ? $"DirectML {Milliseconds(g)}"
                : timing.GpuProblem?.StartsWith("DirectML", StringComparison.Ordinal) == true ? timing.GpuProblem : $"DirectML: {timing.GpuProblem}";
            var choice = Pick(setting, feature, results);
            var unit = feature.PerFrame ? "a 1080p frame" : "an image";
            var pace = feature.PerFrame ? "per minute of 1080p video" : "per image";
            var estimate = PerMinute(feature, choice.Device, results);
            var why = choice.Why.Replace(" in system_check", "");
            lines.Add($"{feature.Name} ({feature.Model.Name}), {unit}: {cpu}; {gpu}; uses {(choice.Device == AiDevice.Gpu ? "DirectML" : "the CPU")} ({why}); {estimate.About}{(estimate.Measured ? "" : " (rough)")} {pace}");
        }

        lines.Add($"saved to {savedPath}; AI jobs estimate their time from it, and AiDevice=auto picks each model's device with it");
        return lines;
    }

    public static string Duration(double seconds)
    {
        if (seconds < 60)
        {
            return $"{Math.Max(1, Math.Ceiling(seconds)).ToString("0", CultureInfo.InvariantCulture)} s";
        }

        if (seconds < 3600)
        {
            return $"{Math.Round(seconds / 60, seconds < 600 ? 1 : 0).ToString("0.#", CultureInfo.InvariantCulture)} min";
        }

        var minutes = (int)Math.Round(seconds / 60);
        return $"{minutes / 60} h {minutes % 60} min";
    }

    public static string Milliseconds(double ms) =>
        ms >= 1000 ? (ms / 1000).ToString("0.0#", CultureInfo.InvariantCulture) + " s" : ms.ToString(ms < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " ms";

    private static string Ratio(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
