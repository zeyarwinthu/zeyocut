// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ZeyoCut.Render.Export;

public static class ExportSummary
{
    public static string Describe(ExportResult result)
    {
        var media = result.Media;
        var parts = new List<string>
        {
            $"{Num(media.Duration, "0.000")}s",
            $"{media.Width}x{media.Height} @ {Num(media.Fps ?? 0, "0.##")} fps",
            $"{media.VideoCodec} {media.PixelFormat}",
            media.HasAudio ? $"audio {media.AudioCodec} {media.AudioChannels}ch {media.SampleRate} Hz" : "no audio",
            FileSize(media.FileSize),
            $"{result.FrameCount} frames",
            $"took {Num(result.Elapsed.TotalSeconds, "0.0")}s",
        };
        return $"{result.OutputPath} — {string.Join(", ", parts)}";
    }

    public static string FileSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{Num(bytes / 1024.0 / 1024.0, "0.0")} MB",
        >= 1024 => $"{Num(bytes / 1024.0, "0.0")} KB",
        _ => $"{bytes} bytes",
    };

    private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
