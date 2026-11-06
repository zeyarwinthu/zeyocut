// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class SystemCheckTests
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public void SystemCheckWithoutAGpuReportsTheCpuFallback()
    {
        var hardware = new HardwareInfo("Test CPU", 4, 8L << 30, 3L << 30, []);
        var described = SystemInfo.Describe(hardware);
        Assert.Contains($"CPU: Test CPU, 4 logical processors (the AI models use {AiSession.CpuThreads} threads)", described);
        Assert.Contains("RAM: 8.0 GB, 3.0 GB free", described);
        Assert.Contains("GPU: none found (DirectML needs a DirectX 12 GPU)", described);
        Assert.Contains(described, l => l.StartsWith("ONNX Runtime 1.", StringComparison.Ordinal));
        Assert.Equal("Test CPU | no GPU", hardware.Machine);

        var runner = new FakeRunner(decodeMs: 6, cpuMs: 80, gpu: () => throw new ZeyoCutException("DirectML can't start: no DirectX 12 adapter was found"));
        var stages = new List<string>();
        var results = AiBenchmark.Run([(AiFeatures.AiVideo, "rvm.onnx"), (AiFeatures.AiImage, "birefnet.onnx")], runner, hardware.Machine, (_, stage) => stages.Add(stage), CancellationToken.None);
        Assert.Equal(["decoding 1080p video", "Robust Video Matting (MobileNetV3) on the CPU", "Robust Video Matting (MobileNetV3) on DirectML", "BiRefNet lite on the CPU", "BiRefNet lite on DirectML"], stages);
        var timing = results.Find(AiFeatures.AiVideo)!;
        Assert.Equal(80, timing.CpuMs);
        Assert.Null(timing.GpuMs);
        Assert.Equal("DirectML can't start: no DirectX 12 adapter was found", timing.GpuProblem);

        var choice = AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiVideo, results);
        Assert.Equal(AiDevice.Cpu, choice.Device);
        Assert.Equal("DirectML didn't work in system_check", choice.Why);
        var lines = AiBenchmark.Describe(results, AiDevice.Auto, @"C:\library\benchmark.json");
        Assert.Equal("decoding 1080p video: 6 ms a frame", lines[0]);
        Assert.Equal("aiVideo matte (Robust Video Matting (MobileNetV3)), a 1080p frame: CPU 80 ms; DirectML can't start: no DirectX 12 adapter was found; uses the CPU (DirectML didn't work); about 2.6 min per minute of 1080p video", lines[1]);
        Assert.Equal("aiImage matte (BiRefNet lite), an image: CPU 80 ms; DirectML can't start: no DirectX 12 adapter was found; uses the CPU (DirectML didn't work); about 1 s per image", lines[2]);
        Assert.Equal(@"saved to C:\library\benchmark.json; AI jobs estimate their time from it, and AiDevice=auto picks each model's device with it", lines[3]);

        // AiDevice=gpu still asks for DirectML; the session falls back to the CPU when it can't start.
        Assert.Equal(new DeviceChoice(AiDevice.Gpu, "AiDevice=gpu"), AiBenchmark.Pick(AiDevice.Gpu, AiFeatures.AiVideo, results));
        Assert.Equal(AiDevice.Gpu, AiSession.ParseDevice("directml"));
        Assert.Equal(AiDevice.Cpu, AiSession.ParseDevice(" CPU "));
        Assert.Equal(AiDevice.Auto, AiSession.ParseDevice(null));
        Assert.Equal(AiDevice.Auto, AiSession.ParseDevice("auto"));
    }

    [Fact]
    public void AutoRunsAModelOnDirectMlOnlyWhereItIsClearlyFaster()
    {
        BenchmarkResults Results(double cpu, double? gpu) => new()
        {
            Machine = "m",
            Features = [new FeatureTiming { Feature = "aiVideo", Model = AiBenchmark.Identity(AiModels.RvmMobileNet), CpuMs = cpu, GpuMs = gpu }],
        };

        Assert.Equal(new DeviceChoice(AiDevice.Cpu, "system_check hasn't measured it yet"), AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiVideo, null));
        Assert.Equal(new DeviceChoice(AiDevice.Cpu, "DirectML wasn't fast enough in system_check: 65 ms against 75 ms"), AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiVideo, Results(75, 65)));
        Assert.Equal(new DeviceChoice(AiDevice.Gpu, "DirectML was 2.5x as fast as the CPU in system_check"), AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiVideo, Results(75, 30)));
        Assert.Equal(AiDevice.Gpu, AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiVideo, Results(100, 80)).Device);
        Assert.Equal(AiDevice.Cpu, AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiVideo, Results(100, 81)).Device);
        Assert.Equal(new DeviceChoice(AiDevice.Cpu, "AiDevice=cpu"), AiBenchmark.Pick(AiDevice.Cpu, AiFeatures.AiVideo, Results(75, 30)));

        // A timing of another model file (or of another feature) doesn't count.
        var other = Results(75, 30);
        other.Features[0].Model = "rvm_mobilenetv3_fp32.onnx:000000000000";
        Assert.Equal(AiDevice.Cpu, AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiVideo, other).Device);
        Assert.Equal(AiDevice.Cpu, AiBenchmark.Pick(AiDevice.Auto, AiFeatures.AiImage, Results(75, 30)).Device);
    }

    [Fact]
    public void EstimatesScaleWithFramesAndPictureSizeAndFlagJobsOverFiveMinutes()
    {
        var saved = new BenchmarkResults
        {
            Machine = "m",
            DecodeMs = 10,
            Features =
            [
                new FeatureTiming { Feature = "aiVideo", Model = AiBenchmark.Identity(AiModels.RvmMobileNet), CpuMs = 90, CpuLoadMs = 200, GpuMs = 40, GpuLoadMs = 3000 },
            ],
        };

        // A minute of 1080p at 30 fps: 1800 x (90 + 10) ms + 0.2 s.
        var minute = AiBenchmark.Estimate(AiFeatures.AiVideo, AiDevice.Cpu, 1800, 1920, 1080, saved);
        Assert.Equal(180.2, minute.Seconds, 3);
        Assert.True(minute.Measured);
        Assert.False(minute.IsLong);
        Assert.Equal("about 3 min (measured by system_check)", minute.Describe());

        // A quarter of the pixels decodes in a quarter of the time, but RVM works on a downsampled copy,
        // so it takes as long; DirectML uses its own times.
        Assert.Equal((1800 * 92.5 + 200) / 1000.0, AiBenchmark.Estimate(AiFeatures.AiVideo, AiDevice.Cpu, 1800, 960, 540, saved).Seconds, 3);
        Assert.Equal((1800 * 50 + 3000) / 1000.0, AiBenchmark.Estimate(AiFeatures.AiVideo, AiDevice.Gpu, 1800, 1920, 1080, saved).Seconds, 3);

        // Two minutes is over five minutes.
        var two = AiBenchmark.Estimate(AiFeatures.AiVideo, AiDevice.Cpu, 3600, 1920, 1080, saved);
        Assert.True(two.IsLong);
        Assert.Equal("about 6 min", two.About);

        // Not measured yet: rough times, said so.
        var rough = AiBenchmark.Estimate(AiFeatures.AiImage, AiDevice.Auto, 2, 0, 0, saved);
        Assert.False(rough.Measured);
        Assert.Equal((2 * AiFeatures.AiImage.RoughMs + AiFeatures.AiImage.RoughLoadMs) / 1000, rough.Seconds, 3);
        Assert.Equal("about 17 s (a rough guess; system_check measures this computer)", rough.Describe());
        Assert.False(rough.Plus(minute).Measured);
        Assert.Equal(rough.Seconds + minute.Seconds, rough.Plus(minute).Seconds, 3);

        Assert.Equal("1 s", AiBenchmark.Duration(0.2));
        Assert.Equal("59 s", AiBenchmark.Duration(58.4));
        Assert.Equal("4.5 min", AiBenchmark.Duration(270));
        Assert.Equal("12 min", AiBenchmark.Duration(725));
        Assert.Equal("1 h 30 min", AiBenchmark.Duration(5400));
        Assert.Equal(300, AiEstimate.LongSeconds);
    }

    [Fact]
    public void BenchmarkResultsAreSavedAndOnlyUsedOnTheSameHardware()
    {
        var directory = TestPaths.NewDirectory("system-check-saved");
        var path = Path.Combine(directory, "library", AiBenchmark.FileName);
        Assert.Null(AiBenchmark.Load(path, "cpu | gpu"));

        var results = AiBenchmark.Run([(AiFeatures.AiVideo, "rvm.onnx")], new FakeRunner(decodeMs: 7.5, cpuMs: 70, gpu: () => new Measured(2500, 30)), "cpu | gpu", null, CancellationToken.None);
        AiBenchmark.Save(path, results);
        Assert.False(File.Exists(path + ".part"));

        var loaded = AiBenchmark.Load(path, "cpu | gpu")!;
        Assert.Equal(7.5, loaded.DecodeMs);
        var timing = loaded.Find(AiFeatures.AiVideo)!;
        Assert.Equal((70.0, 100.0, 30.0, 2500.0), (timing.CpuMs!.Value, timing.CpuLoadMs!.Value, timing.GpuMs!.Value, timing.GpuLoadMs!.Value));
        Assert.Contains("\"cpuMs\": 70", File.ReadAllText(path));
        Assert.DoesNotContain("gpuProblem", File.ReadAllText(path));

        Assert.Null(AiBenchmark.Load(path, "cpu | another gpu"));
        File.WriteAllText(path, "{ not json");
        Assert.Null(AiBenchmark.Load(path, "cpu | gpu"));
    }

    [Fact]
    public void DirectMlRunsInAProcessOfItsOwnThatIsStoppedWhenItHangs()
    {
        Assert.SkipWhen(!File.Exists(PowerShell), "Windows PowerShell isn't here; skipping the child process test");

        // A copy that never answers is stopped after the time limit.
        var hung = new AiBenchmarkRunner(null, PowerShell, ["-NoProfile", "-Command", "Start-Sleep -Seconds 30;"], TimeSpan.FromSeconds(2));
        var stopped = Assert.Throws<ZeyoCutException>(() => hung.Gpu(AiFeatures.AiVideo, "rvm.onnx", CancellationToken.None));
        Assert.Equal("no answer within 2 s, so it was stopped (DirectML can hang with some GPU drivers)", stopped.Message);

        // A driver failure is reported by its code and sentence, without ONNX Runtime's source paths.
        const string failure = "error: [ErrorCode:RuntimeException] Non-zero status code returned while running DmlFusedNode_15_51 node. Status Message: E:\\_work\\1\\s\\onnxruntime\\core\\providers\\dml\\DmlGraphFusionHelper.cpp(1078)\\onnxruntime.dll!00007FF97BB091CD: (caller: 00007FF97BB957E3) Exception(2) tid(7f50) 887A0020 An internal issue prevented the driver from carrying out the specified operation. The driver's state is probably suspect,";
        var broken = new AiBenchmarkRunner(null, PowerShell, ["-NoProfile", "-Command", $"[Console]::Error.WriteLine('log line'); [Console]::Error.WriteLine('{failure.Replace("'", "''")}'); exit 1;"]);
        var failed = Assert.Throws<ZeyoCutException>(() => broken.Gpu(AiFeatures.AiImage, "birefnet.onnx", CancellationToken.None));
        Assert.Equal("the GPU driver failed: 887A0020 An internal issue prevented the driver from carrying out the specified operation", failed.Message);

        // An answer is one line of JSON.
        var answering = new AiBenchmarkRunner(null, PowerShell, ["-NoProfile", "-Command", "Write-Output 'loading'; [ordered]@{ loadMs = 3289.3; runMs = 65.1 } | ConvertTo-Json -Compress; exit 0;"]);
        Assert.Equal(new Measured(3289.3, 65.1), answering.Gpu(AiFeatures.AiVideo, "rvm.onnx", CancellationToken.None));

        var nobody = new AiBenchmarkRunner(null, null, []);
        Assert.Equal("not tried: the server can't start a copy of itself to try it", Assert.Throws<ZeyoCutException>(() => nobody.Gpu(AiFeatures.AiVideo, "rvm.onnx", CancellationToken.None)).Message);
        Assert.Equal("ffmpeg wasn't found, so decoding wasn't measured", Assert.Throws<ZeyoCutException>(() => nobody.DecodeMs(CancellationToken.None)).Message);
    }

    [Fact]
    public void DecodingIsMeasuredOnAGenerated1080pClip()
    {
        var ffmpeg = Ffmpeg.Require();
        var ms = AiBenchmark.MeasureDecode(ffmpeg.Ffmpeg, CancellationToken.None);
        Assert.InRange(ms, 0.1, 1000);
    }

    [Fact]
    public void TheVideoModelIsMeasuredOnTheCpu()
    {
        var store = new ModelStore(Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models"));
        Assert.SkipWhen(!store.Has(AiModels.RvmMobileNet), $"{AiModels.RvmMobileNet.FileName} isn't in {store.Folder}; skipping the benchmark smoke test");
        var measured = AiBenchmark.Measure(AiFeatures.AiVideo, store.PathOf(AiModels.RvmMobileNet), AiDevice.Cpu, CancellationToken.None);
        Assert.InRange(measured.RunMs, 1, 5000);
        Assert.InRange(measured.LoadMs, 0, 30000);
    }

    private sealed class FakeRunner(double decodeMs, double cpuMs, Func<Measured> gpu) : IBenchmarkRunner
    {
        public double? DecodeMs(CancellationToken cancellationToken) => decodeMs;

        public Measured Cpu(AiFeature feature, string modelPath, CancellationToken cancellationToken) => new(100, cpuMs);

        public Measured Gpu(AiFeature feature, string modelPath, CancellationToken cancellationToken) => gpu();
    }
}
