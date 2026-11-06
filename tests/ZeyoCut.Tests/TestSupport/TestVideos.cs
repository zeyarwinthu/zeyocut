// SPDX-License-Identifier: MIT

using System.Globalization;
using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Tests.TestSupport;

// Test clips are generated with ffmpeg at test time, from its lavfi sources or the photos in Fixtures.
public static class TestVideos
{
    // Frame n is coded in gray (lossless): the left half is (n mod 16) * 16, the right half (n / 16) * 16,
    // so two decoded pixels tell which source frame is shown even after a little filtering.
    public static string Ramp(string path, double seconds = 4, int fps = 30, int width = 64, int height = 48, bool withAudio = false) =>
        Encode(path, $"color=c=black:s={width}x{height}:r={fps}:d={Num(seconds)},format=gray,geq=lum='if(lt(X,W/2),mod(N,16)*16,floor(N/16)*16)'", seconds, fps, withAudio, lossless: true);

    // Reads the frame code of a Ramp clip drawn full-size at (left, top).
    public static int FrameCode(SKImage image, int left = 0, int top = 0, int width = 64, int height = 48)
    {
        var low = TestImages.Pixel(image, left + width / 4, top + height / 2).Red;
        var high = TestImages.Pixel(image, left + width * 3 / 4, top + height / 2).Red;
        return (int)Math.Round(high / 16.0) * 16 + (int)Math.Round(low / 16.0);
    }

    // Left half black, right half white.
    public static string Halves(string path, double seconds = 1, int fps = 30, int width = 64, int height = 48) =>
        Encode(path, $"color=c=black:s={width}x{height}:r={fps}:d={Num(seconds)},format=gray,geq=lum='if(lt(X,W/2),0,255)'", seconds, fps, withAudio: false, lossless: true);

    // Moving, colorful, lossy H.264 with B-frames: a realistic decode workload.
    public static string Pattern(string path, double seconds = 2, int fps = 30, int width = 320, int height = 240, bool withAudio = false) =>
        Encode(path, $"testsrc2=s={width}x{height}:r={fps}:d={Num(seconds)}", seconds, fps, withAudio, lossless: false);

    // 2 s at nominal 30 fps with three frames dropped (average 28.5 fps), like a phone recording.
    public static string VariableFrameRate(string path) =>
        Encode(path, "testsrc2=s=160x120:r=30:d=2,select='not(eq(n,10)+eq(n,20)+eq(n,40))'", 2, 30, withAudio: true, lossless: false, extra: ["-fps_mode", "vfr"]);

    // 4 s: red for 2 s then blue (a scene cut at 2 s); a 440 Hz tone 0–1 s and 3–4 s with quiet noise
    // (about -51 dB peak) under all of it, so the only silence is 1–3 s.
    public static string ToneGapTone(string path)
    {
        var ffmpeg = Ffmpeg.Require();
        var result = ProcessRunner.Run(
            ffmpeg.Ffmpeg,
            [
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "color=c=red:s=160x120:r=30:d=2",
                "-f", "lavfi", "-i", "color=c=blue:s=160x120:r=30:d=2",
                "-f", "lavfi", "-i", "sine=f=440:d=1:sample_rate=48000",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono:d=2",
                "-f", "lavfi", "-i", "sine=f=440:d=1:sample_rate=48000",
                "-f", "lavfi", "-i", "anoisesrc=d=4:a=0.003:sample_rate=48000:seed=1",
                "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v];[2:a][3:a][4:a]concat=n=3:v=0:a=1[a0];[a0][5:a]amix=inputs=2:normalize=0[a]",
                "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", path,
            ],
            TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    // A green screen (#28BE3C) with a red box (box x box) that starts at (8, top) and moves 4 px right
    // every frame at 10 fps. Lossless 4:4:4, so the colors and edges are exact.
    public static string GreenScreen(string path, double seconds = 1, int width = 96, int height = 64, int box = 24, int top = 16) =>
        Encode(path, $"color=c=0x28BE3C:s={width}x{height}:r=10:d={Num(seconds)}[bg];color=c=red:s={box}x{box}:r=10:d={Num(seconds)}[box];[bg][box]overlay=x='8+t*40':y={top}:format=yuv444[out0]", seconds, 10, withAudio: false, lossless: true, pixelFormat: "yuv444p");

    // A textured 40x40 square over a textured background, at (20 + 3n, 60 + n) on frame n: 320x180 at
    // 30 fps, lossless 4:4:4. With 'cover', a flat gray 90x90 square hides it from frame cover.First to
    // cover.Last.
    public static string MovingSquare(string path, double seconds = 2, (int First, int Last, int X, int Y)? cover = null)
    {
        var d = Num(seconds);
        var graph = $"color=c=gray:s=320x180:r=30:d={d},format=rgb24,geq=r='90+40*sin(X/9)*cos(Y/7)':g='100+40*sin(X/5+Y/11)':b='110+30*cos(X/13)'[bg];"
            + $"color=c=black:s=40x40:r=30:d={d},format=rgb24,geq=r='if(mod(floor(X/8)+floor(Y/8),2),240,30)':g='if(mod(floor(X/10)+floor(Y/5),2),200,40)':b='if(lt(X+Y,40),220,60)'[box];"
            + "[bg][box]overlay=x='20+3*n':y='60+n':format=rgb";
        if (cover is { } c)
        {
            graph += $"[moving];color=c=0x808080:s=90x90:r=30:d={d}[cover];[moving][cover]overlay=x={c.X}:y={c.Y}:enable='between(n,{c.First},{c.Last})':format=rgb";
        }

        return Encode(path, graph + ",format=yuv444p[out0]", seconds, 30, withAudio: false, lossless: true, pixelFormat: "yuv444p");
    }

    // Where MovingSquare's square is on frame n.
    public static (double X, double Y, double Size) SquareAt(int frame) => (20 + 3 * frame, 60 + frame, 40);

    // 10-bit H.264, optionally tagged as HDR (PQ / BT.2020).
    public static string DeepColor(string path, bool hdr) =>
        Encode(path, "testsrc2=s=96x64:r=25:d=1" + (hdr ? ",setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc" : ""),
            1, 25, withAudio: false, lossless: false, pixelFormat: "yuv420p10le");

    // Same pixels as 'source', with display-rotation metadata (degrees counter-clockwise).
    public static string Rotated(string source, string path, int degrees)
    {
        var ffmpeg = Ffmpeg.Require();
        var result = ProcessRunner.Run(
            ffmpeg.Ffmpeg,
            ["-v", "error", "-y", "-display_rotation", degrees.ToString(CultureInfo.InvariantCulture), "-i", source, "-c", "copy", path],
            TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    // A photo as a 24 fps clip: 'filter' takes it as [0:v] and ends in [out] (lossy H.264).
    public static string FromPhoto(string photo, string path, double seconds, string filter)
    {
        var ffmpeg = Ffmpeg.Require();
        var result = ProcessRunner.Run(
            ffmpeg.Ffmpeg,
            ["-v", "error", "-y", "-loop", "1", "-framerate", "24", "-i", photo, "-filter_complex", filter, "-map", "[out]",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-t", Num(seconds), path],
            TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    // The crew photo at 800x640 seen through a 640x360 frame that pans right and down from 40,20, and a dark bar,
    // like someone walking past, crossing from right to left between 0.6 and 1.3 s: 2 s, 48 frames.
    public static string Crew(string path) =>
        FromPhoto(TestFixtures.Group, path, 2,
            "color=c=0x202428:s=110x360:r=24:d=2[bar];[0:v]scale=800:-2,crop=640:360:'40+t*20':'20+t*5'[bg];[bg][bar]overlay=x='640-(t-0.6)*1000':y=0:enable='between(t,0.6,1.3)',format=yuv420p[out]");

    // The van photo at 640x426 seen through a 640x360 frame that pans down 30 px a second, and the same dark bar
    // crossing between 0.6 and 1.3 s: 2 s, 48 frames.
    public static string Vans(string path) =>
        FromPhoto(TestFixtures.Plates, path, 2,
            "color=c=0x202428:s=110x360:r=24:d=2[bar];[0:v]scale=640:-2,crop=640:360:0:'t*30'[bg];[bg][bar]overlay=x='640-(t-0.6)*1000':y=0:enable='between(t,0.6,1.3)',format=yuv420p[out]");

    // A talking-head stand-in: a moving pattern at 640x360, 30 fps, over the spoken 'lines' one after
    // another with 'pause' seconds of quiet noise between them (lossy H.264, AAC).
    public static string Talking(string path, double pause, params string[] lines)
    {
        var ffmpeg = Ffmpeg.Require();
        var arguments = new List<string> { "-v", "error", "-y" };
        var graph = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            arguments.AddRange(["-i", Speech.Speak(lines[i], Path.ChangeExtension(path, $".line{i}.wav"))]);
            graph.Add($"[{i}:a]aformat=sample_rates=48000:channel_layouts=mono,apad=pad_dur={(i < lines.Length - 1 ? Num(pause) : "0.3")}[s{i}]");
        }

        arguments.AddRange(["-f", "lavfi", "-i", "anoisesrc=d=60:a=0.002:sample_rate=48000:seed=1", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30"]);
        graph.Add(string.Concat(Enumerable.Range(0, lines.Length).Select(i => $"[s{i}]")) + $"concat=n={lines.Length}:v=0:a=1[voice]");
        graph.Add($"[voice][{lines.Length}:a]amix=inputs=2:duration=first:normalize=0[a]");
        arguments.AddRange(["-filter_complex", string.Join(";", graph), "-map", $"{lines.Length + 1}:v", "-map", "[a]", "-shortest",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", path]);
        var result = ProcessRunner.Run(ffmpeg.Ffmpeg, arguments, TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    // A face, then a street with vans and their plates: 1 s of the portrait at 640x360, then 1 s of the
    // van photo at 640x360 (lossy H.264, 24 fps).
    public static string FaceThenVans(string path)
    {
        var ffmpeg = Ffmpeg.Require();
        var result = ProcessRunner.Run(
            ffmpeg.Ffmpeg,
            ["-v", "error", "-y", "-loop", "1", "-framerate", "24", "-t", "1", "-i", TestFixtures.Portrait, "-loop", "1", "-framerate", "24", "-t", "1", "-i", TestFixtures.Plates,
                "-filter_complex", "[0:v]scale=640:-2,crop=640:360:0:100,setsar=1[face];[1:v]scale=640:-2,crop=640:360:0:0,setsar=1[vans];[face][vans]concat=n=2:v=1:a=0,format=yuv420p[out]",
                "-map", "[out]", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-r", "24", path],
            TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    public static Asset Import(ProjectSession session, string file, string id)
    {
        var ffmpeg = Ffmpeg.Require();
        var info = VideoProbe.Probe(ffmpeg.Ffprobe, file).ToAssetInfo();
        return session.Mutate("import", p => AssetImporter.Import(p, session.Directory, file, id, new AssetProbe(video: _ => info), AssetType.Video));
    }

    public static Asset AssetFor(string file, string id = "clip")
    {
        var ffmpeg = Ffmpeg.Require();
        var info = VideoProbe.Probe(ffmpeg.Ffprobe, file);
        return new Asset
        {
            Id = id,
            Type = AssetType.Video,
            File = file,
            Width = info.Width,
            Height = info.Height,
            Duration = info.Duration,
            FrameRate = info.FrameRate,
            FrameCount = info.FrameCount,
            HasAudio = info.HasAudio,
            Codec = info.Codec,
        };
    }

    private static string Encode(
        string path,
        string video,
        double seconds,
        int fps,
        bool withAudio,
        bool lossless,
        string pixelFormat = "yuv420p",
        IEnumerable<string>? extra = null)
    {
        var ffmpeg = Ffmpeg.Require();
        var args = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", video };
        if (withAudio)
        {
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={Num(seconds)}"]);
        }

        args.AddRange(["-c:v", "libx264", "-g", fps.ToString(CultureInfo.InvariantCulture), "-pix_fmt", pixelFormat]);
        args.AddRange(lossless ? ["-qp", "0"] : ["-crf", "20"]);
        args.AddRange(extra ?? []);
        if (withAudio)
        {
            args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
        }

        args.Add(path);
        var result = ProcessRunner.Run(ffmpeg.Ffmpeg, args, TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
