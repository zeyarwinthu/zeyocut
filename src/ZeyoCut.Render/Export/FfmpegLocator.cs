// SPDX-License-Identifier: MIT

using ZeyoCut.Core;

namespace ZeyoCut.Render.Export;

public sealed record FfmpegPaths(string Ffmpeg, string Ffprobe);

public static class FfmpegLocator
{
    public const string EnvironmentVariable = "FFMPEG_PATH";

    public static FfmpegPaths Locate(string? configuredPath = null) =>
        TryLocate(configuredPath, out var paths, out var error) ? paths! : throw new ZeyoCutException(error!);

    public static bool TryLocate(string? configuredPath, out FfmpegPaths? paths, out string? error)
    {
        paths = null;
        error = null;
        foreach (var (source, value) in new[] { ("config FfmpegPath", configuredPath), (EnvironmentVariable, Environment.GetEnvironmentVariable(EnvironmentVariable)) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var ffmpeg = ResolveExecutable(value.Trim().Trim('"'), "ffmpeg");
            if (ffmpeg is null)
            {
                error = $"{source} is set to '{value}', but no ffmpeg.exe was found there";
                return false;
            }

            var ffprobe = ResolveExecutable(Path.GetDirectoryName(ffmpeg)!, "ffprobe") ?? SearchPath("ffprobe");
            if (ffprobe is null)
            {
                error = $"found {ffmpeg} but no ffprobe.exe next to it or on PATH";
                return false;
            }

            paths = new FfmpegPaths(ffmpeg, ffprobe);
            return true;
        }

        var fromPath = SearchPath("ffmpeg");
        var probeFromPath = fromPath is null ? null : ResolveExecutable(Path.GetDirectoryName(fromPath)!, "ffprobe") ?? SearchPath("ffprobe");
        if (fromPath is null || probeFromPath is null)
        {
            error = "FFmpeg was not found. Install it (for example: winget install Gyan.FFmpeg) or set FFMPEG_PATH to ffmpeg.exe or its folder";
            return false;
        }

        paths = new FfmpegPaths(fromPath, probeFromPath);
        return true;
    }

    private static string? ResolveExecutable(string pathOrDirectory, string name)
    {
        if (File.Exists(pathOrDirectory))
        {
            return Path.GetFullPath(pathOrDirectory);
        }

        if (Directory.Exists(pathOrDirectory))
        {
            foreach (var candidate in new[] { name + ".exe", name, Path.Combine("bin", name + ".exe") })
            {
                var full = Path.Combine(pathOrDirectory, candidate);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
        }

        return null;
    }

    private static string? SearchPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name + ".exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
