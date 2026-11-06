// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using ZeyoCut.Core;

namespace ZeyoCut.Render.Export;

public sealed record MediaInfo(
    double Duration,
    long FileSize,
    bool HasVideo,
    int? Width,
    int? Height,
    double? Fps,
    string? VideoCodec,
    string? PixelFormat,
    int? VideoFrames,
    bool HasAudio,
    string? AudioCodec,
    int? AudioChannels,
    int? SampleRate);

public static class Ffprobe
{
    public static MediaInfo Probe(string ffprobePath, string file)
    {
        var result = ProcessRunner.Run(
            ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-count_packets", file],
            TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0)
        {
            throw new ZeyoCutException($"ffprobe could not read '{file}': {result.StandardError.Trim()}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var duration = ParseDouble(format, "duration") ?? 0;

        JsonElement? video = null;
        JsonElement? audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && video is null)
                {
                    video = stream;
                }
                else if (type == "audio" && audio is null)
                {
                    audio = stream;
                }
            }
        }

        return new MediaInfo(
            duration,
            new FileInfo(file).Length,
            video is not null,
            ParseInt(video, "width"),
            ParseInt(video, "height"),
            ParseRate(video, "r_frame_rate"),
            ParseString(video, "codec_name"),
            ParseString(video, "pix_fmt"),
            ParseInt(video, "nb_read_packets"),
            audio is not null,
            ParseString(audio, "codec_name"),
            ParseInt(audio, "channels"),
            ParseInt(audio, "sample_rate"));
    }

    public static double AudioDuration(string ffprobePath, string file)
    {
        var info = Probe(ffprobePath, file);
        if (!info.HasAudio)
        {
            throw new ZeyoCutException($"'{file}' has no audio stream");
        }

        if (info.Duration <= 0)
        {
            throw new ZeyoCutException($"could not read the duration of '{file}'");
        }

        return info.Duration;
    }

    private static string? ParseString(JsonElement? element, string name) =>
        element is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static int? ParseInt(JsonElement? element, string name) =>
        int.TryParse(ParseString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? ParseDouble(JsonElement? element, string name) =>
        double.TryParse(ParseString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? ParseRate(JsonElement? element, string name)
    {
        var text = ParseString(element, name);
        if (text is null)
        {
            return null;
        }

        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : null;
    }
}
