// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

public sealed record VideoProbeResult(
    int Width,
    int Height,
    string FrameRate,
    string AverageFrameRate,
    int FrameCount,
    double Duration,
    string Codec,
    string PixelFormat,
    int BitDepth,
    int Rotation,
    string? ColorTransfer,
    double StartTime,
    string? SampleAspectRatio,
    bool HasAudio)
{
    public double Fps => VideoProbe.RateValue(FrameRate);

    public double AverageFps => VideoProbe.RateValue(AverageFrameRate);

    public bool IsVariableFrameRate => AverageFps > 0 && Math.Abs(AverageFps - Fps) / Fps > 0.002;

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    // Why the file can't be used as it is; empty when it can be copied into the project unchanged.
    public IReadOnlyList<string> NormalizeReasons()
    {
        var reasons = new List<string>();
        if (IsVariableFrameRate)
        {
            reasons.Add($"variable frame rate (average {AverageFps.ToString("0.###", CultureInfo.InvariantCulture)} fps, nominal {Fps.ToString("0.###", CultureInfo.InvariantCulture)} fps)");
        }
        else if (Fps is < 1 or > 120)
        {
            reasons.Add($"unusual frame rate {FrameRate}");
        }

        if (Rotation != 0)
        {
            reasons.Add($"rotated {Rotation}° by metadata");
        }

        if (BitDepth > 8)
        {
            reasons.Add($"{BitDepth}-bit color ({PixelFormat})");
        }

        if (IsHdr)
        {
            reasons.Add($"HDR ({ColorTransfer})");
        }

        if (Fps > 0 && Math.Abs(StartTime) > 0.5 / Fps)
        {
            reasons.Add($"video starts at {StartTime.ToString("0.###", CultureInfo.InvariantCulture)}s instead of 0");
        }

        if (SampleAspectRatio is { } sar && sar is not ("1:1" or "0:1" or "N/A"))
        {
            reasons.Add($"non-square pixels (SAR {sar})");
        }

        return reasons;
    }

    public AssetInfo ToAssetInfo() =>
        new(Width, Height, Duration, FrameRate: FrameRate, FrameCount: FrameCount, HasAudio: HasAudio, Codec: Codec);
}

public static partial class VideoProbe
{
    [GeneratedRegex(@"(\d{2})(le|be)$")]
    private static partial Regex DeepPixelFormat();

    public static VideoProbeResult Probe(string ffprobePath, string file)
    {
        var result = ProcessRunner.Run(
            ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file],
            TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0)
        {
            throw new ZeyoCutException($"ffprobe could not read '{file}': {result.StandardError.Trim()}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        JsonElement? video = null;
        var hasAudio = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = Text(stream, "codec_type");
                var attachedPicture = stream.TryGetProperty("disposition", out var disposition)
                    && disposition.TryGetProperty("attached_pic", out var pic) && pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1;
                if (type == "video" && video is null && !attachedPicture)
                {
                    video = stream;
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                }
            }
        }

        if (video is not { } v)
        {
            throw new ZeyoCutException($"'{Path.GetFileName(file)}' has no video stream");
        }

        var width = Int(v, "width") ?? 0;
        var height = Int(v, "height") ?? 0;
        if (width <= 0 || height <= 0)
        {
            throw new ZeyoCutException($"could not read the frame size of '{Path.GetFileName(file)}'");
        }

        var rate = Text(v, "r_frame_rate") ?? "0/0";
        var average = Text(v, "avg_frame_rate") ?? rate;
        if (RateValue(rate) <= 0)
        {
            rate = average;
        }

        if (RateValue(rate) <= 0)
        {
            throw new ZeyoCutException($"could not read the frame rate of '{Path.GetFileName(file)}'");
        }

        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streamDuration = Double(v, "duration") ?? (format.ValueKind == JsonValueKind.Object ? Double(format, "duration") : null) ?? 0;
        var frames = Int(v, "nb_frames") ?? CountFrames(ffprobePath, file) ?? (int)Math.Round(streamDuration * RateValue(rate));
        if (frames <= 0)
        {
            throw new ZeyoCutException($"could not count the frames of '{Path.GetFileName(file)}'");
        }

        var pixelFormat = Text(v, "pix_fmt") ?? "";
        var bitDepth = Int(v, "bits_per_raw_sample")
            ?? (DeepPixelFormat().Match(pixelFormat) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 8);
        if (pixelFormat.StartsWith("p010", StringComparison.Ordinal))
        {
            bitDepth = Math.Max(bitDepth, 10);
        }

        var parsedRate = RationalText(rate);
        var (numerator, denominator) = VideoTiming.ParseRate(parsedRate);
        return new VideoProbeResult(
            width,
            height,
            parsedRate,
            RationalText(average),
            frames,
            frames * (double)denominator / numerator,
            Text(v, "codec_name") ?? "unknown",
            pixelFormat,
            bitDepth,
            Rotation(v),
            Text(v, "color_transfer"),
            Double(v, "start_time") ?? 0,
            Text(v, "sample_aspect_ratio"),
            hasAudio);
    }

    public static double RateValue(string? rate)
    {
        var parts = (rate ?? "").Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator))
        {
            return denominator > 0 ? numerator / denominator : 0;
        }

        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string RationalText(string rate)
    {
        var parts = rate.Split('/');
        if (parts.Length == 2 && long.TryParse(parts[0], out var numerator) && long.TryParse(parts[1], out var denominator) && denominator > 0)
        {
            var divisor = Gcd(numerator, denominator);
            return denominator / divisor == 1 ? (numerator / divisor).ToString(CultureInfo.InvariantCulture) : $"{numerator / divisor}/{denominator / divisor}";
        }

        return rate;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Max(1, Math.Abs(a));
    }

    private static int? CountFrames(string ffprobePath, string file)
    {
        var result = ProcessRunner.Run(
            ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", file],
            TimeSpan.FromMinutes(5));
        return result.ExitCode == 0 && int.TryParse(result.StandardOutput.Trim().TrimEnd(','), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    private static int Rotation(JsonElement stream)
    {
        double? rotation = null;
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var value) && value.ValueKind == JsonValueKind.Number)
                {
                    rotation = value.GetDouble();
                }
            }
        }

        if (rotation is null && stream.TryGetProperty("tags", out var tags) && Double(tags, "rotate") is { } tagged)
        {
            rotation = tagged;
        }

        var degrees = (int)Math.Round(rotation ?? 0);
        return ((degrees % 360) + 360) % 360;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static int? Int(JsonElement element, string name) =>
        int.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? Double(JsonElement element, string name) =>
        double.TryParse(Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
