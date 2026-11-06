// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render;

// A downloadable ONNX model, pinned to one file by its size and SHA-256.
public sealed record AiModel(string Id, string FileName, string Url, long Bytes, string Sha256, string Name, string License)
{
    public string Size => Bytes < 1e6
        ? $"{Math.Round(Bytes / 1e3).ToString("0", CultureInfo.InvariantCulture)} KB"
        : $"{Math.Round(Bytes / 1e6).ToString("0", CultureInfo.InvariantCulture)} MB";
}

// The models of the AI background removal and of detect_and_track. They aren't shipped with ZeyoCut:
// each is downloaded the first time it is needed (RVM is GPL-3.0, so it stays a separate download).
public static class AiModels
{
    public static readonly AiModel BiRefNetLite = new(
        MatteFiles.ImageModel,
        "birefnet_lite.onnx",
        "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/de15b22ba131738a16dff04aab8bdf8dc32e3ac1/onnx/model.onnx",
        224005088,
        "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333",
        "BiRefNet lite",
        "MIT");

    public static readonly AiModel RvmMobileNet = new(
        MatteFiles.VideoModel,
        "rvm_mobilenetv3_fp32.onnx",
        "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx",
        14975696,
        "88d4531297118f595bf2fd60f6f566aec2e559393802d1f436c380f0cbbd2828",
        "Robust Video Matting (MobileNetV3)",
        "GPL-3.0");

    public static readonly AiModel YuNet = new(
        "yunet",
        "face_detection_yunet_2026may.onnx",
        "https://github.com/opencv/opencv_zoo/raw/26cc381e4d2594bb9f47a26eb8fd96c94a13660d/models/face_detection_yunet/face_detection_yunet_2026may.onnx",
        229738,
        "ebafce4e3c118d6554634be5c27ab333b4c047a9a8c3faf1d7cf93101c22f0f0",
        "YuNet",
        "MIT");

    // The repository is MIT; the weights' license and training data aren't stated.
    public static readonly AiModel Plates = new(
        "plates",
        "yolo-v9-t-640-license-plates-end2end.onnx",
        "https://github.com/ankandrew/open-image-models/releases/download/assets/yolo-v9-t-640-license-plates-end2end.onnx",
        7835770,
        "c3c1026ca7d0585dd88084d68182dd897113712fa734ae1557ca70174440c076",
        "open-image-models YOLOv9-t",
        "MIT code, the weights' license isn't stated");

    public static AiModel For(MatteMethod method) => method switch
    {
        MatteMethod.AiImage => BiRefNetLite,
        MatteMethod.AiVideo => RvmMobileNet,
        _ => throw new ZeyoCutException($"{EnumNames.ToName(method)} doesn't use a model"),
    };
}

// The models folder (library\models by default). Downloads go to <file>.part, are checked against
// the pinned SHA-256 and only then renamed, so a half-downloaded or altered file is never used.
public sealed class ModelStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    public ModelStore(string folder, HttpClient? http = null)
    {
        Folder = Path.GetFullPath(folder);
        _http = http ?? CreateClient();
    }

    public string Folder { get; }

    public string PathOf(AiModel model) => Path.Combine(Folder, model.FileName);

    public bool Has(AiModel model)
    {
        var info = new FileInfo(PathOf(model));
        return info.Exists && info.Length == model.Bytes;
    }

    // The model's path, downloading it first when it isn't there; progress gets the bytes so far and the total.
    public string Ensure(AiModel model, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        var path = PathOf(model);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            if (Has(model))
            {
                return path;
            }

            Directory.CreateDirectory(Folder);
            var part = path + ".part";
            try
            {
                Download(model, part, progress, cancellationToken);
                File.Move(part, path, overwrite: true);
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            return path;
        }
        finally
        {
            gate.Release();
        }
    }

    private void Download(AiModel model, string part, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        var host = new Uri(model.Url).Host;
        var help = $"check the internet connection, or download {model.Url} yourself and save it as {PathOf(model)}";
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            using var response = _http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new ZeyoCutException($"could not download {model.FileName} from {host}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}; {help}");
            }

            using var input = response.Content.ReadAsStream(stall.Token);
            using var output = File.Create(part);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long done = 0;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = input.ReadAsync(buffer, stall.Token).AsTask().GetAwaiter().GetResult();
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (done > model.Bytes)
                {
                    break;
                }

                progress?.Invoke(done, model.Bytes);
            }

            if (done != model.Bytes)
            {
                throw new ZeyoCutException($"the download of {model.FileName} from {host} has {done} bytes instead of {model.Bytes}, so it was deleted; {help}");
            }

            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!sha.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ZeyoCutException($"the download of {model.FileName} from {host} doesn't match its checksum (SHA-256 {sha[..12]}…, expected {model.Sha256[..12]}…), so it was deleted; {help}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ZeyoCutException($"the download of {model.FileName} from {host} stalled for {StallTimeout.TotalSeconds:0}s; {help}");
        }
        catch (HttpRequestException ex)
        {
            throw new ZeyoCutException($"could not download {model.FileName} from {host}: {ex.Message}; {help}");
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            throw new ZeyoCutException($"could not download {model.FileName} from {host}: {ex.Message}; {help}");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZeyoCut/1.0");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// Auto runs a model on DirectML only where system_check found it works and is faster (AiBenchmark.Pick).
public enum AiDevice
{
    Cpu,
    Gpu,
    Auto
}

// One loaded model. Gpu runs on DirectML and falls back to the CPU when DirectML can't start.
public sealed class AiSession : IDisposable
{
    public const string DirectMl = "GPU (DirectML)";
    public const string DirectMlUnavailable = "CPU (DirectML unavailable: ";

    private AiSession(InferenceSession session, string device)
    {
        Session = session;
        Device = device;
    }

    public InferenceSession Session { get; }

    // Where it runs, for replies: "CPU", "GPU (DirectML)" or "CPU (DirectML unavailable: …)".
    public string Device { get; }

    // On CPUs with efficiency cores, a thread per logical processor makes every step wait for the
    // slowest core (RVM at 720p went from 86 to 190-260 ms a frame); half of them, at most 8, is steady.
    public static int CpuThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public static AiDevice ParseDevice(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "gpu" or "directml" or "dml" => AiDevice.Gpu,
        "cpu" => AiDevice.Cpu,
        _ => AiDevice.Auto,
    };

    // ONNX Runtime's official Windows builds send usage events through Windows' own telemetry unless
    // that is turned off; ZeyoCut turns it off before it loads a model or asks for the version.
    public static OrtEnv Runtime()
    {
        var environment = OrtEnv.Instance();
        environment.DisableTelemetryEvents();
        return environment;
    }

    public static AiSession Open(string modelPath, AiDevice device)
    {
        try
        {
            Runtime();
            string? problem = null;
            if (device == AiDevice.Gpu)
            {
                using var gpu = Options();
                gpu.EnableMemoryPattern = false;
                gpu.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                try
                {
                    gpu.AppendExecutionProvider_DML(0);
                    return new AiSession(new InferenceSession(modelPath, gpu), DirectMl);
                }
                catch (OnnxRuntimeException ex)
                {
                    problem = FirstLine(ex.Message);
                }
            }

            using var cpu = Options();
            try
            {
                return new AiSession(new InferenceSession(modelPath, cpu), problem is null ? "CPU" : $"{DirectMlUnavailable}{problem})");
            }
            catch (OnnxRuntimeException ex)
            {
                throw new ZeyoCutException($"could not load the model {Path.GetFileName(modelPath)}: {FirstLine(ex.Message)}; delete it from {Path.GetDirectoryName(modelPath)} so it downloads again");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new ZeyoCutException($"the AI runtime (ONNX Runtime) could not be loaded: {ex.Message}; chroma and edgeFill still work");
        }
    }

    public void Dispose() => Session.Dispose();

    private static SessionOptions Options() => new()
    {
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = CpuThreads,
    };

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();
}
