// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

// Footage over 2560 px gets a copy 1920 px on its long side in the project's proxies folder. Previews
// (render_frame, render_contact_sheet, view_source) and finding and tracking objects read it instead
// of the original: a frame is found in less than half the time and played through twice as fast.
// Exports always read the original. The proxy keeps every frame at its time (H.264 CRF 20, a
// keyframe every second, no sound); its name holds the original's size and time, so a changed file
// gets a new one.
public static class Proxies
{
    public const string Folder = "proxies";
    public const int Above = 2560;
    public const int Side = 1920;
    public const int Crf = 20;

    public static bool Wanted(Asset asset) =>
        asset.Type == AssetType.Video && asset.Width is { } width && asset.Height is { } height && Math.Max(width, height) > Above;

    // The proxy's frame size: 1920 px on the long side, both sides even.
    public static (int Width, int Height) Size(Asset asset)
    {
        var width = asset.Width ?? 0;
        var height = asset.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            throw new ZeyoCutException($"video asset '{asset.Id}' has no frame size");
        }

        var scale = Side / (double)Math.Max(width, height);
        return (Math.Max(2, (int)Math.Round(width * scale / 2) * 2), Math.Max(2, (int)Math.Round(height * scale / 2) * 2));
    }

    public static string PathOf(string projectDirectory, Asset asset)
    {
        var file = new FileInfo(ProjectStore.AssetPath(projectDirectory, asset));
        var stamp = file.Exists ? $"{file.Length}|{file.LastWriteTimeUtc.Ticks}" : "missing";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{asset.File}|{stamp}|{Side}|{Crf}")))[..12];
        return Path.Combine(projectDirectory, Folder, $"{Path.GetFileNameWithoutExtension(asset.File)}.{hash}.mp4");
    }

    // The proxy to read for the asset, or null when it has none (yet).
    public static string? Ready(string projectDirectory, Asset asset) =>
        Wanted(asset) && PathOf(projectDirectory, asset) is var path && File.Exists(path) ? path : null;

    public static List<string> Arguments(string source, Asset asset, string output)
    {
        var (width, height) = Size(asset);
        var keyframeInterval = Math.Max(1, (int)Math.Round(VideoTiming.Fps(asset)));
        return
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-nostats", "-progress", "pipe:1",
            "-i", source,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"scale={width}:{height}:flags=bicubic,format=yuv420p",
            "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", Crf.ToString(CultureInfo.InvariantCulture),
            "-g", keyframeInterval.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-map_metadata", "-1",
            "-movflags", "+faststart",
            output,
        ];
    }

    // Makes the asset's proxy (written as .partial.mp4 and renamed when done) and returns its path.
    public static string Make(string ffmpeg, string projectDirectory, Asset asset, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = ProjectStore.AssetPath(projectDirectory, asset);
        if (!File.Exists(source))
        {
            throw new ZeyoCutException($"the file of asset '{asset.Id}' is missing ({source})");
        }

        var output = PathOf(projectDirectory, asset);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = Path.ChangeExtension(output, ".partial.mp4");
        var duration = asset.Duration ?? 0;
        using var process = Process.Start(ProcessRunner.StartInfo(ffmpeg, Arguments(source, asset, temp)))
            ?? throw new ZeyoCutException($"could not start {ffmpeg}");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)
                    && duration > 0)
                {
                    progress?.Invoke(Math.Clamp(micros / 1e6 / duration, 0, 0.99));
                }
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg could not make the proxy of '{asset.Id}' (exit code {process.ExitCode}): {stderr.Result.Trim()}");
            }

            File.Move(temp, output, overwrite: true);
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

            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        progress?.Invoke(1);
        return output;
    }
}
