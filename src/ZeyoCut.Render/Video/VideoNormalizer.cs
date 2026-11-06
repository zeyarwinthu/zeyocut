// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using ZeyoCut.Core;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

// Transcodes footage the editor can't use directly into constant-frame-rate, upright, 8-bit BT.709
// H.264 with a keyframe every second (fast seeks) and AAC sound.
public static class VideoNormalizer
{
    public const int Crf = 16;

    private static readonly (string Rate, double Fps)[] StandardRates =
    [
        ("24000/1001", 24000 / 1001.0), ("24", 24), ("25", 25), ("30000/1001", 30000 / 1001.0), ("30", 30),
        ("48", 48), ("50", 50), ("60000/1001", 60000 / 1001.0), ("60", 60), ("120", 120),
    ];

    public static string TargetRate(VideoProbeResult probe)
    {
        if (!probe.IsVariableFrameRate && probe.Fps is >= 1 and <= 120)
        {
            return probe.FrameRate;
        }

        var fps = probe.AverageFps > 0 ? probe.AverageFps : probe.Fps;
        return StandardRates.MinBy(r => Math.Abs(r.Fps - fps)).Rate;
    }

    public static string VideoFilter(VideoProbeResult probe)
    {
        var filters = new List<string>();
        if (probe.IsHdr)
        {
            filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        }

        var square = probe.SampleAspectRatio is null or "1:1" or "0:1" or "N/A";
        var width = square ? "trunc(iw/2)*2" : "trunc(iw*sar/2)*2";
        filters.Add($"scale={width}:trunc(ih/2)*2:out_color_matrix=bt709:out_range=tv");
        filters.Add("setsar=1");
        filters.Add("format=yuv420p");

        // ffmpeg takes the color tags from the frames, so set them here rather than as encoder options.
        filters.Add("setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv");
        return string.Join(",", filters);
    }

    // Rotation metadata is applied by ffmpeg's autorotate (on by default), so the output is upright.
    public static List<string> Arguments(string source, VideoProbeResult probe, string output)
    {
        var rate = TargetRate(probe);
        var keyframeInterval = Math.Max(1, (int)Math.Round(VideoProbe.RateValue(rate)));
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-nostats", "-progress", "pipe:1",
            "-i", source,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", VideoFilter(probe),
            "-fps_mode", "cfr", "-r", rate,
            "-c:v", "libx264", "-preset", "medium", "-crf", Crf.ToString(CultureInfo.InvariantCulture),
            "-g", keyframeInterval.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-sn", "-dn", "-map_metadata", "-1",
            "-movflags", "+faststart",
            output,
        };
        return args;
    }

    public static VideoProbeResult Normalize(
        FfmpegPaths ffmpeg,
        string source,
        VideoProbeResult probe,
        string output,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".partial" + Path.GetExtension(output));
        var sourceDuration = probe.Duration;
        using var process = Process.Start(ProcessRunner.StartInfo(ffmpeg.Ffmpeg, Arguments(source, probe, temp)))
            ?? throw new ZeyoCutException($"could not start {ffmpeg.Ffmpeg}");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)
                    && sourceDuration > 0)
                {
                    progress?.Invoke(Math.Clamp(micros / 1e6 / sourceDuration, 0, 0.99));
                }
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg could not normalize '{Path.GetFileName(source)}' (exit code {process.ExitCode}): {stderr.Result.Trim()}");
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            TryDelete(temp);
            throw;
        }

        var result = VideoProbe.Probe(ffmpeg.Ffprobe, temp);
        var left = result.NormalizeReasons();
        if (left.Count > 0)
        {
            TryDelete(temp);
            throw new ZeyoCutException($"normalizing '{Path.GetFileName(source)}' did not fix: {string.Join("; ", left)}");
        }

        File.Move(temp, output, overwrite: true);
        progress?.Invoke(1);
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
