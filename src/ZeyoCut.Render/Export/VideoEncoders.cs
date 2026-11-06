// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZeyoCut.Core;

namespace ZeyoCut.Render.Export;

public enum VideoEncoderKind
{
    X264,
    Nvenc,
    Qsv,
    Amf
}

// An H.264 encoder FFmpeg can export with: libx264 on the CPU, or a GPU's (NVIDIA NVENC, Intel Quick
// Sync, AMD AMF). The quality follows the export's CRF; for Quick Sync, global_quality CRF + 1 at the
// same preset matched libx264's SSIM on photo pans with and without grain (an Intel Core Ultra 9 285H).
// NVENC and AMF get the equivalent settings, untested here.
public sealed record VideoEncoder(VideoEncoderKind Kind, string Codec, string Label)
{
    public static readonly VideoEncoder X264 = new(VideoEncoderKind.X264, "libx264", "CPU");
    public static readonly VideoEncoder Nvenc = new(VideoEncoderKind.Nvenc, "h264_nvenc", "NVIDIA NVENC");
    public static readonly VideoEncoder Qsv = new(VideoEncoderKind.Qsv, "h264_qsv", "Intel Quick Sync");
    public static readonly VideoEncoder Amf = new(VideoEncoderKind.Amf, "h264_amf", "AMD AMF");

    // The GPU encoders, in the order auto tries them.
    public static IReadOnlyList<VideoEncoder> Hardware { get; } = [Nvenc, Qsv, Amf];

    public static IReadOnlyList<VideoEncoder> All { get; } = [X264, Nvenc, Qsv, Amf];

    public string Name => VideoEncoders.NameOf(Kind);

    public bool IsHardware => Kind != VideoEncoderKind.X264;

    // The pixel format FFmpeg hands the encoder.
    public string PixelFormat => Kind is VideoEncoderKind.Qsv or VideoEncoderKind.Amf ? "nv12" : "yuv420p";

    // The options for a CRF (0–51) and libx264 preset (veryfast for plain exports, medium for delivery).
    public IReadOnlyList<string> Options(int crf, string x264Preset)
    {
        var slow = x264Preset is "medium" or "slow" or "slower" or "veryslow";
        var quality = Math.Min(51, crf + 1).ToString(CultureInfo.InvariantCulture);
        return Kind switch
        {
            VideoEncoderKind.Nvenc => ["-c:v", Codec, "-preset", slow ? "p6" : "p4", "-tune", "hq", "-rc", "vbr", "-cq", quality, "-b:v", "0"],
            VideoEncoderKind.Qsv => ["-c:v", Codec, "-preset", slow ? "medium" : "veryfast", "-global_quality", quality],
            VideoEncoderKind.Amf => ["-c:v", Codec, "-quality", slow ? "quality" : "balanced", "-rc", "cqp", "-qp_i", quality, "-qp_p", Math.Min(51, crf + 3).ToString(CultureInfo.InvariantCulture), "-qp_b", Math.Min(51, crf + 5).ToString(CultureInfo.InvariantCulture)],
            _ => ["-c:v", Codec, "-preset", x264Preset, "-crf", crf.ToString(CultureInfo.InvariantCulture)],
        };
    }

    // The encoder and its quality for the export's notes, e.g. "h264_qsv (Intel Quick Sync, global_quality 19)".
    public string Describe(int crf) => Kind switch
    {
        VideoEncoderKind.X264 => $"{Codec} ({Label}, CRF {crf})",
        VideoEncoderKind.Amf => $"{Codec} ({Label}, QP {Math.Min(51, crf + 1)})",
        VideoEncoderKind.Nvenc => $"{Codec} ({Label}, CQ {Math.Min(51, crf + 1)})",
        _ => $"{Codec} ({Label}, global_quality {Math.Min(51, crf + 1)})",
    };
}

// The encoder an export uses, and why.
public sealed record EncoderChoice(VideoEncoder Encoder, string Why);

// What the test encodes found on one computer with one FFmpeg (<library>\encoders.json).
public sealed class EncoderTests
{
    public string Machine { get; set; } = "";

    public string Ffmpeg { get; set; } = "";

    public List<EncoderTest> Results { get; set; } = [];
}

public sealed record EncoderTest(string Encoder, bool Works, string? Problem, DateTime CheckedUtc);

public static class VideoEncoders
{
    public const string FileName = "encoders.json";

    public const string Auto = "auto";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string NameOf(VideoEncoderKind kind) => kind switch
    {
        VideoEncoderKind.Nvenc => "nvenc",
        VideoEncoderKind.Qsv => "qsv",
        VideoEncoderKind.Amf => "amf",
        _ => "x264",
    };

    // The VideoEncoder setting: auto (or empty) tries the GPU encoders, or one of x264, nvenc, qsv, amf
    // (libx264, h264_nvenc, h264_qsv and h264_amf work too). Null for auto; an unknown name is an error.
    public static VideoEncoder? Parse(string? setting)
    {
        var text = setting?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text == Auto)
        {
            return null;
        }

        return VideoEncoder.All.FirstOrDefault(e => e.Name == text || e.Codec == text || text == "cpu" && e.Kind == VideoEncoderKind.X264)
            ?? throw new ZeyoCutException($"unknown video encoder '{setting}'; use auto, x264, nvenc, qsv or amf");
    }

    // Encodes half a second of a test pattern the way a delivery preset would; null when it works, or
    // the first thing FFmpeg said about why not. A hung driver gives up after 30 s.
    public static string? Try(FfmpegPaths ffmpeg, VideoEncoder encoder)
    {
        List<string> arguments =
        [
            "-hide_banner", "-nostdin", "-v", "error",
            "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=0.5",
            "-vf", $"format={encoder.PixelFormat}",
            .. encoder.Options(18, "medium"),
            "-profile:v", "high", "-bf", "2", "-g", "15",
            "-f", "null", "-",
        ];
        try
        {
            var result = ProcessRunner.Run(ffmpeg.Ffmpeg, arguments, TimeSpan.FromSeconds(30));
            return result.ExitCode == 0 ? null : Problem(result.StandardError, result.ExitCode);
        }
        catch (ZeyoCutException ex)
        {
            return ex.Message;
        }
    }

    // FFmpeg's first message, without its "[h264_nvenc @ 0x...]" prefix.
    public static string Problem(string stderr, int exitCode)
    {
        var line = stderr.Split('\n').Select(l => Regex.Replace(l, @"^(\[[^\]]*\]\s*)+", "").Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? $"ffmpeg exit code {exitCode}";
    }

    // Which encoder an export uses. Lossless (CRF 0) is always libx264. Otherwise a named encoder is
    // used if it works, and auto takes the first GPU encoder that works (NVENC, Quick Sync, AMF), else
    // libx264. Each GPU encoder is tried once per computer and FFmpeg, and the result is kept in
    // testsPath; try is the test encode (Try, but replaceable for tests).
    public static EncoderChoice Choose(VideoEncoder? setting, int crf, string testsPath, string machine, string ffmpegStamp, Func<VideoEncoder, string?> test)
    {
        if (crf == 0)
        {
            return new EncoderChoice(VideoEncoder.X264, "CRF 0 (lossless) always uses libx264");
        }

        if (setting is { IsHardware: false })
        {
            return new EncoderChoice(VideoEncoder.X264, "the VideoEncoder setting is x264");
        }

        var tests = Load(testsPath, machine, ffmpegStamp) ?? new EncoderTests { Machine = machine, Ffmpeg = ffmpegStamp };
        var changed = false;

        string? ProblemOf(VideoEncoder encoder)
        {
            var known = tests.Results.FirstOrDefault(r => r.Encoder == encoder.Name);
            if (known is null)
            {
                var problem = test(encoder);
                known = new EncoderTest(encoder.Name, problem is null, problem, DateTime.UtcNow);
                tests.Results.Add(known);
                changed = true;
            }

            return known.Works ? null : known.Problem ?? "it didn't work";
        }

        try
        {
            if (setting is { } named)
            {
                return ProblemOf(named) is { } problem
                    ? new EncoderChoice(VideoEncoder.X264, $"the VideoEncoder setting is {named.Name}, but {named.Codec} doesn't work on this computer ({problem}), so libx264 was used")
                    : new EncoderChoice(named, $"the VideoEncoder setting is {named.Name}");
            }

            var problems = new List<string>();
            foreach (var encoder in VideoEncoder.Hardware)
            {
                if (ProblemOf(encoder) is not { } problem)
                {
                    return new EncoderChoice(encoder, "the first GPU encoder that works on this computer");
                }

                problems.Add($"{encoder.Name}: {problem}");
            }

            return new EncoderChoice(VideoEncoder.X264, $"no GPU encoder works on this computer ({string.Join("; ", problems)})");
        }
        finally
        {
            if (changed)
            {
                Save(testsPath, tests);
            }
        }
    }

    // The FFmpeg an encoder test belongs to: its path, size and time (of the file a link such as
    // WinGet's points to), so updating FFmpeg tests again.
    public static string Stamp(FfmpegPaths ffmpeg)
    {
        var info = new FileInfo(ffmpeg.Ffmpeg);
        try
        {
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
            {
                info = target;
            }
        }
        catch (IOException)
        {
        }

        return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : ffmpeg.Ffmpeg;
    }

    public static EncoderTests? Load(string path, string machine, string ffmpegStamp)
    {
        try
        {
            var tests = File.Exists(path) ? JsonSerializer.Deserialize<EncoderTests>(File.ReadAllText(path), Json) : null;
            return tests is not null && tests.Machine == machine && tests.Ffmpeg == ffmpegStamp ? tests : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save(string path, EncoderTests tests)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var part = path + ".part";
            File.WriteAllText(part, JsonSerializer.Serialize(tests, Json));
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
