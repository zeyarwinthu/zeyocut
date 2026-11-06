// SPDX-License-Identifier: MIT

using System.Diagnostics;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

public sealed record ExportResult(
    string OutputPath,
    MediaInfo Media,
    int FrameCount,
    int AudioClips,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Notes = null,
    RenderTimes? Times = null);

public static class Exporter
{
    internal static readonly TimeSpan FinishTimeout = TimeSpan.FromMinutes(2);

    // Threads for a chunked export: a quarter of the logical processors, from 1 to 4, so the encoders
    // (one per thread) and the decoders have cores left and the memory stays within a few GB.
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    // A chunked export renders and caches the frames in chunks of this many seconds.
    public const int ChunkSeconds = ChunkedExport.ChunkSeconds;

    // The memory one thread of a chunked export takes, by the pixels of the canvas or the file, whichever
    // is bigger. Measured with 4 threads on a 16-core laptop: about 1 GB at 1920x1080 (x264 medium, with
    // two encoders while one finishes) and 2 GB at 3840x2160 (veryfast).
    public static long ThreadMemory(long pixels) => 512L * 1024 * 1024 + 250 * pixels;

    // The threads that fit in half the free memory, from 1 to wanted.
    public static int ThreadsFor(int wanted, long pixels, long freeBytes) =>
        (int)Math.Clamp(freeBytes / 2 / ThreadMemory(pixels), 1, Math.Max(1, wanted));

    public static ExportResult Export(
        Project project,
        string projectDirectory,
        string outputPath,
        int crf,
        FfmpegPaths ffmpeg,
        Action<int, int>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Preset? preset = null,
        bool fitShape = false,
        int? workers = null,
        EncoderChoice? encoder = null)
    {
        if (crf is < 0 or > 51)
        {
            throw new ZeyoCutException($"crf must be between 0 (lossless) and 51 (worst); 18 is visually lossless (got {crf})");
        }

        ProjectValidator.ThrowIfInvalid(project);
        MatteFiles.ThrowIfPending(project, projectDirectory);
        if (!fitShape)
        {
            CheckPreset(project, preset);
        }

        var fitted = fitShape && preset is not null && !SameShape(project, preset);
        var stopwatch = Stopwatch.StartNew();
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".partial" + Path.GetExtension(output));

        var audio = FfmpegArgs.AudioClips(project, projectDirectory);
        foreach (var clip in audio.Where(c => !File.Exists(c.Path)))
        {
            throw new ZeyoCutException($"layer '{clip.LayerId}': file is missing: {clip.Path}");
        }

        foreach (var video in project.Layers.OfType<VideoLayer>())
        {
            var asset = project.FindAsset(video.Asset)!;
            var path = ProjectStore.AssetPath(projectDirectory, asset);
            if (!File.Exists(path))
            {
                throw new ZeyoCutException($"video layer '{video.Id}': file is missing: {path}");
            }
        }

        var frameCount = FrameMath.FrameCount(project);
        var notes = new List<string>();
        var audioStarted = Stopwatch.GetTimestamp();
        var mix = PlanAudio(project, audio, frameCount / (double)project.Fps, ffmpeg, notes, log, cancellationToken);
        var audioTime = Stopwatch.GetElapsedTime(audioStarted);
        var settings = preset is null
            ? new EncodeSettings(project.Width, project.Height, project.Fps, frameCount, crf)
            : new EncodeSettings(
                fitted ? preset.Width : project.Width,
                fitted ? preset.Height : project.Height,
                project.Fps,
                frameCount,
                crf,
                "medium",
                ["-profile:v", "high", "-bf", "2", "-g", Math.Max(1, project.Fps / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)],
                preset.AudioKbps,
                preset.Width,
                preset.Height);
        if (preset is not null)
        {
            notes.Add($"preset {preset.Name}: {preset.Width}x{preset.Height}, H.264 high profile, CRF {crf}, AAC {preset.AudioKbps}k"
                + (project.Fps != preset.Fps ? $" (the project is {project.Fps} fps; the preset suggests {preset.Fps})" : ""));
        }

        if (fitted)
        {
            notes.Add($"the {project.Width}x{project.Height} canvas is fitted whole into {preset!.Width}x{preset.Height} over a blurred, enlarged copy of itself");
        }

        // Without a choice the export uses libx264 and says nothing about it.
        if (encoder is not null)
        {
            settings = settings with { Encoder = encoder.Encoder };
            notes.Add($"encoder: {encoder.Encoder.Describe(crf)}: {encoder.Why}");
        }

        // With workers, the export is rendered in chunks on that many threads (ChunkedExport); without,
        // in one pass that renders and encodes every frame in order.
        if (workers is { } threads)
        {
            return ChunkedExport.Run(project, projectDirectory, output, temp, settings, audio, mix, notes, audioTime, fitted ? (preset!.Width, preset.Height) : null, ffmpeg, threads, progress, log, cancellationToken, stopwatch);
        }

        var arguments = FfmpegArgs.Build(settings, audio, temp, mix);
        log?.Invoke($"ffmpeg {string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

        using var process = Process.Start(ProcessRunner.StartInfo(ffmpeg.Ffmpeg, arguments, redirectInput: true))
            ?? throw new ZeyoCutException($"could not start {ffmpeg.Ffmpeg}");
        var stderrLines = new Queue<string>();
        var stderrTask = Task.Run(() =>
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                log?.Invoke($"ffmpeg: {line}");
                lock (stderrLines)
                {
                    stderrLines.Enqueue(line);
                    if (stderrLines.Count > 30)
                    {
                        stderrLines.Dequeue();
                    }
                }
            }
        });
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);

        string StderrTail()
        {
            lock (stderrLines)
            {
                return string.Join("\n", stderrLines);
            }
        }

        var stats = new RenderStats();
        using var renderer = new FrameRenderer(projectDirectory, () => ffmpeg.Ffmpeg, streamVideo: true) { Stats = stats };
        double? encoderCpu = null;
        var renderStarted = Stopwatch.GetTimestamp();
        var renderTime = TimeSpan.Zero;
        try
        {
            using var surface = SKSurface.Create(FrameRenderer.FrameInfo(project))
                ?? throw new ZeyoCutException($"could not create a {project.Width}x{project.Height} drawing surface");
            using var fit = fitted ? new BlurredFit(project.Width, project.Height, preset!.Width, preset.Height) : null;
            var stdin = process.StandardInput.BaseStream;
            stats.Restart();
            renderStarted = Stopwatch.GetTimestamp();
            for (var frame = 0; frame < frameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stats.BeginFrame(frame);
                renderer.Render(project, frame, surface.Canvas);
                var target = surface;
                if (fit is not null)
                {
                    using var image = surface.Snapshot();
                    target = fit.Draw(image);
                    stats.Lap(RenderStage.Fit);
                }

                stats.EndFrame();
                using var pixmap = target.PeekPixels();
                try
                {
                    stdin.Write(pixmap.GetPixelSpan());
                }
                catch (IOException)
                {
                    process.WaitForExit(10000);
                    stderrTask.Wait(2000);
                    throw new ZeyoCutException($"ffmpeg stopped while receiving frame {frame}:\n{StderrTail()}");
                }

                stats.Lap(RenderStage.Encode);
                progress?.Invoke(frame + 1, frameCount);
            }

            stdin.Close();

            // ffmpeg only has the encoder's last frames and the faststart rewrite left to do.
            var limit = FinishTimeout + stopwatch.Elapsed / 10;
            if (!ProcessRunner.WaitForExit(process, limit, cancellationToken))
            {
                throw new ZeyoCutException($"ffmpeg did not finish within {limit.TotalSeconds:0}s after the last frame, so the export was stopped:\n{StderrTail()}");
            }

            stderrTask.Wait();
            stdoutTask.Wait();
            stats.Lap(RenderStage.Encode);
            renderTime = Stopwatch.GetElapsedTime(renderStarted);
            if (process.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg failed with exit code {process.ExitCode}:\n{StderrTail()}");
            }

            encoderCpu = CpuSeconds(process);
        }
        catch
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                }
            }

            TryDelete(temp);
            throw;
        }

        File.Move(temp, output, overwrite: true);
        var media = Ffprobe.Probe(ffmpeg.Ffprobe, output);
        stopwatch.Stop();
        return new ExportResult(output, media, frameCount, audio.Count, stopwatch.Elapsed, renderer.TakeWarnings(), notes, stats.Times(frameCount, audioTime.TotalSeconds, encoderCpu, renderSeconds: renderTime.TotalSeconds));
    }

    // The CPU time a finished process used, if Windows still tells.
    internal static double? CpuSeconds(Process process)
    {
        try
        {
            return process.TotalProcessorTime.TotalSeconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // Ducking and the first loudnorm pass (an audio-only run over the same mix).
    internal static AudioMix? PlanAudio(Project project, List<AudioClip> audio, double duration, FfmpegPaths ffmpeg, List<string> notes, Action<string>? log, CancellationToken cancellationToken)
    {
        if (audio.Count == 0)
        {
            return null;
        }

        var settings = project.Audio;
        var mix = new AudioMix(settings.Ducking, settings.LoudnessTarget, settings.TruePeak, null);
        if (FfmpegArgs.Ducks(audio, mix))
        {
            notes.Add("music ducked under voice");
        }

        if (settings.LoudnessTarget is not { } target)
        {
            notes.Add("loudness normalization off");
            return mix;
        }

        var arguments = FfmpegArgs.MeasureArguments(audio, mix, duration);
        log?.Invoke($"ffmpeg {string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        var stderr = new System.Text.StringBuilder();
        var result = ProcessRunner.RunLines(ffmpeg.Ffmpeg, arguments, null, line => stderr.AppendLine(line), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ZeyoCutException($"ffmpeg could not measure the loudness of the mix (exit code {result.ExitCode}):\n{result.StandardError}");
        }

        var measured = FfmpegArgs.ParseLoudness(stderr.ToString());
        if (!double.IsFinite(measured.InputI) || measured.InputI < -70)
        {
            notes.Add("loudness: the mix is silent, so it was not normalized");
            return mix;
        }

        var lufs = System.Globalization.CultureInfo.InvariantCulture;
        notes.Add($"loudness: {measured.InputI.ToString("0.0", lufs)} LUFS → {target.ToString("0.#", lufs)} LUFS (true peak at most {settings.TruePeak.ToString("0.#", lufs)} dBTP)");
        return mix with { Measured = measured };
    }

    public static void CheckPreset(Project project, Preset? preset)
    {
        if (preset is null)
        {
            return;
        }

        if (!SameShape(project, preset))
        {
            var shape = preset.IsVertical ? "vertical" : "landscape";
            throw new ZeyoCutException(
                $"the project is {project.Width}x{project.Height} but {preset.Name} is {preset.Width}x{preset.Height} ({shape}); change the canvas with update_project preset={preset.Name}{(preset.IsVertical ? " and fit video layers with reframe_to_vertical" : "")} first");
        }
    }

    // Within a pixel of the preset's aspect ratio.
    public static bool SameShape(Project project, Preset preset) =>
        Math.Abs(project.Width * (double)preset.Height - project.Height * (double)preset.Width) <= Math.Max(project.Width, project.Height);

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
