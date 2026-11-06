// SPDX-License-Identifier: MIT

using ZeyoCut.Render.Export;

namespace ZeyoCut.Tests.TestSupport;

public static class Ffmpeg
{
    private static readonly Lazy<FfmpegPaths?> Paths = new(() =>
        FfmpegLocator.TryLocate(null, out var paths, out _) ? paths : null);

    public static FfmpegPaths Require()
    {
        Assert.SkipWhen(Paths.Value is null, "FFmpeg not found (install it or set FFMPEG_PATH); skipping export test");
        return Paths.Value!;
    }

    public static byte[] ExtractFrameRgba(FfmpegPaths ffmpeg, string video, int frame, int width, int height)
    {
        var result = ProcessRunner.RunBinary(
            ffmpeg.Ffmpeg,
            [
                "-v", "error", "-i", video,
                "-vf", $"select=eq(n\\,{frame}),scale=in_color_matrix=bt709:in_range=tv,format=rgba",
                "-fps_mode", "passthrough", "-frames:v", "1", "-f", "rawvideo", "pipe:1",
            ],
            TimeSpan.FromSeconds(60),
            out var bytes);
        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(width * height * 4, bytes.Length);
        return bytes;
    }

    public static double MeanAbsoluteError(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long total = 0;
        long count = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                total += Math.Abs(a[i + c] - b[i + c]);
                count++;
            }
        }

        return total / (double)count;
    }

    // Mean absolute difference of Rec.709 luma. Unlike RGB it is not affected by 4:2:0 chroma
    // subsampling, which dominates the error on saturated, detailed footage.
    public static double MeanLumaError(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double total = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var lumaA = 0.2126 * a[i] + 0.7152 * a[i + 1] + 0.0722 * a[i + 2];
            var lumaB = 0.2126 * b[i] + 0.7152 * b[i + 1] + 0.0722 * b[i + 2];
            total += Math.Abs(lumaA - lumaB);
        }

        return total / (a.Length / 4);
    }
}
