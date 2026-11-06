// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// Exports in chunks of ChunkSeconds, counted from the project's first frame. Each chunk is rendered on
// one of several threads, with its own renderer and encoder, into an MP4 of its own; the chunks are
// then joined without encoding them again (FFmpeg's concat demuxer, video copied) while the audio is
// mixed in. A chunk is kept in <project>\cache\chunks under the key of its frames and encoding, so a
// re-export renders only the chunks whose frames changed, and an export started again after a restart
// goes on from the chunks it finished.
internal static class ChunkedExport
{
    public const int ChunkSeconds = 2;

    // The chunk cache of a project is trimmed to this size, oldest first.
    public const long CacheBytes = 2L * 1024 * 1024 * 1024;

    public const string CacheFolder = "cache";

    private sealed record Chunk(int Index, int Start, int End, string File)
    {
        public int Frames => End - Start;
    }

    private sealed record Worker(RenderStats Stats, IReadOnlyList<string> Warnings, double EncoderCpu);

    public static ExportResult Run(
        Project project,
        string projectDirectory,
        string output,
        string temp,
        EncodeSettings settings,
        IReadOnlyList<AudioClip> audio,
        AudioMix? mix,
        List<string> notes,
        TimeSpan audioTime,
        (int Width, int Height)? fit,
        FfmpegPaths ffmpeg,
        int workers,
        Action<int, int>? progress,
        Action<string>? log,
        CancellationToken cancellationToken,
        Stopwatch stopwatch)
    {
        var frameCount = settings.FrameCount;
        var folder = Path.Combine(projectDirectory, CacheFolder, "chunks");
        Directory.CreateDirectory(folder);

        var keysStarted = Stopwatch.GetTimestamp();
        var keys = new FrameKeys(project, projectDirectory);
        var encoding = string.Join("|", settings with { FrameCount = 0 }, string.Join(" ", settings.VideoOptions ?? []), fit);
        var length = Math.Max(1, project.Fps * ChunkSeconds);
        var chunks = new List<Chunk>();
        for (var start = 0; start < frameCount; start += length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(frameCount, start + length);
            var key = FrameKeys.Combine($"{encoding}|{start}-{end}", Enumerable.Range(start, end - start).Select(keys.Of));
            chunks.Add(new Chunk(chunks.Count, start, end, Path.Combine(folder, key + ".mp4")));
        }

        var keysTime = Stopwatch.GetElapsedTime(keysStarted);
        var todo = chunks.Where(c => !File.Exists(c.File)).ToList();
        foreach (var reused in chunks.Except(todo))
        {
            Touch(reused.File);
        }

        var gate = new object();
        var done = chunks.Except(todo).Sum(c => c.Frames);
        if (done > 0)
        {
            progress?.Invoke(done, frameCount);
        }

        void Report()
        {
            lock (gate)
            {
                done++;
                progress?.Invoke(done, frameCount);
            }
        }

        var threads = Math.Clamp(workers, 1, Math.Max(1, todo.Count));
        var pixels = Math.Max((long)project.Width * project.Height, (long)(settings.OutputWidth ?? settings.Width) * (settings.OutputHeight ?? settings.Height));
        if (SystemInfo.Hardware().FreeRamBytes is { } free && Exporter.ThreadsFor(threads, pixels, free) is var fitting && fitting < threads)
        {
            notes.Add($"rendered on {fitting} thread(s), not {threads}: {Gigabytes(free)} GB of memory was free, and each thread takes about {Gigabytes(Exporter.ThreadMemory(pixels))} GB at this size");
            threads = fitting;
        }

        // libx264 shares the processors between the chunks' encoders; a GPU encoder has its own.
        var chunkSettings = settings.Encoder is { IsHardware: true }
            ? settings
            : settings with
            {
                VideoOptions = [.. settings.VideoOptions ?? [], "-threads", Math.Max(1, Environment.ProcessorCount / threads).ToString(CultureInfo.InvariantCulture)],
            };
        var renderStarted = Stopwatch.GetTimestamp();
        var results = new Worker[threads];
        var claims = new Claims(todo.Count);
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var tasks = Enumerable.Range(0, threads).Select(w => Task.Factory.StartNew(() =>
            {
                ChunkEncoder? finishing = null;
                try
                {
                    var stats = new RenderStats();
                    using var renderer = new FrameRenderer(projectDirectory, () => ffmpeg.Ffmpeg, streamVideo: true) { Stats = stats };
                    using var surface = SKSurface.Create(FrameRenderer.FrameInfo(project))
                        ?? throw new ZeyoCutException($"could not create a {project.Width}x{project.Height} drawing surface");
                    using var blurred = fit is { } size ? new BlurredFit(project.Width, project.Height, size.Width, size.Height) : null;
                    var cpu = 0.0;
                    Chunk? previous = null;

                    // A chunk's encoder finishes while the next chunk renders into an encoder of its own.
                    for (var i = claims.Next(-1); i >= 0; i = claims.Next(i))
                    {
                        var chunk = todo[i];
                        if (previous is null || chunk.Start != previous.End)
                        {
                            renderer.Video.CloseReaders();
                        }

                        var encoder = Feed(project, chunk, chunkSettings with { FrameCount = chunk.Frames }, renderer, surface, blurred, stats, ffmpeg, log, Report, stop.Token);
                        if (finishing is not null)
                        {
                            cpu += finishing.Finish(stop.Token);
                            stats.Lap(RenderStage.Encode);
                            finishing.Dispose();
                        }

                        finishing = encoder;
                        previous = chunk;
                    }

                    if (finishing is not null)
                    {
                        cpu += finishing.Finish(stop.Token);
                        stats.Lap(RenderStage.Encode);
                    }

                    results[w] = new Worker(stats, renderer.TakeWarnings(), cpu);
                }
                catch
                {
                    stop.Cancel();
                    throw;
                }
                finally
                {
                    finishing?.Dispose();
                }
            }, stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = ex.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ex.InnerExceptions[0];
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }
        }

        var renderTime = Stopwatch.GetElapsedTime(renderStarted);
        var stats = new RenderStats();
        foreach (var result in results.Where(r => r is not null))
        {
            stats.Add(result.Stats);
        }

        var joinStarted = Stopwatch.GetTimestamp();
        var list = Path.Combine(folder, $"join-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(list, chunks.Select(c => $"file '{c.File.Replace("'", "'\\''")}'"));
        try
        {
            var arguments = FfmpegArgs.BuildJoin(list, frameCount, settings.Fps, audio, temp, mix, settings.AudioKbps);
            log?.Invoke($"ffmpeg {string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
            var joined = ProcessRunner.RunLines(ffmpeg.Ffmpeg, arguments, null, line => log?.Invoke($"ffmpeg: {line}"), cancellationToken);
            if (joined.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg could not join the {chunks.Count} chunk(s) (exit code {joined.ExitCode}):\n{joined.StandardError}");
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            TryDelete(list);
        }

        File.Move(temp, output, overwrite: true);
        var joinTime = Stopwatch.GetElapsedTime(joinStarted);
        var media = Ffprobe.Probe(ffmpeg.Ffprobe, output);
        Prune(folder, chunks.Select(c => c.File).ToHashSet(StringComparer.OrdinalIgnoreCase));
        stopwatch.Stop();
        var times = stats.Times(
            frameCount,
            audioTime.TotalSeconds,
            results.Where(r => r is not null).Sum(r => r.EncoderCpu),
            threads,
            renderTime.TotalSeconds,
            chunks.Count,
            chunks.Count - todo.Count,
            keysTime.TotalSeconds,
            joinTime.TotalSeconds);
        var warnings = results.Where(r => r is not null).SelectMany(r => r.Warnings).Distinct().Order(StringComparer.Ordinal).ToList();
        return new ExportResult(output, media, frameCount, audio.Count, stopwatch.Elapsed, warnings, notes, times);
    }

    // Renders a chunk's frames into an encoder of its own and closes its input; the encoder is then
    // left to finish.
    private static ChunkEncoder Feed(
        Project project,
        Chunk chunk,
        EncodeSettings settings,
        FrameRenderer renderer,
        SKSurface surface,
        BlurredFit? blurred,
        RenderStats stats,
        FfmpegPaths ffmpeg,
        Action<string>? log,
        Action report,
        CancellationToken cancellationToken)
    {
        var encoder = new ChunkEncoder(chunk, settings, ffmpeg, log);
        try
        {
            stats.Lap(RenderStage.Encode);
            for (var frame = chunk.Start; frame < chunk.End; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stats.BeginFrame(frame);
                renderer.Render(project, frame, surface.Canvas);
                var target = surface;
                if (blurred is not null)
                {
                    using var image = surface.Snapshot();
                    target = blurred.Draw(image);
                    stats.Lap(RenderStage.Fit);
                }

                stats.EndFrame();
                using var pixmap = target.PeekPixels();
                encoder.Write(frame, pixmap.GetPixelSpan());
                stats.Lap(RenderStage.Encode);
                report();
            }

            encoder.Close();
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    // Hands out the chunks so each thread works through neighbouring ones, which keeps its clips'
    // decoders going instead of seeking again: a thread goes on with the chunk after its last one while
    // that is free; otherwise it takes the longest run of chunks nobody has started, from its start if
    // no thread is working just before it, or else from its middle.
    private sealed class Claims(int count)
    {
        private const byte Free = 0;
        private const byte Working = 1;
        private const byte Done = 2;

        private readonly byte[] _state = new byte[count];

        // Marks the last chunk done and returns the next one to render, or -1 when none is left.
        public int Next(int last)
        {
            lock (_state)
            {
                if (last >= 0)
                {
                    _state[last] = Done;
                    if (last + 1 < count && _state[last + 1] == Free)
                    {
                        _state[last + 1] = Working;
                        return last + 1;
                    }
                }

                var (start, length) = (-1, 0);
                for (var i = 0; i < count;)
                {
                    if (_state[i] != Free)
                    {
                        i++;
                        continue;
                    }

                    var end = i;
                    while (end < count && _state[end] == Free)
                    {
                        end++;
                    }

                    if (end - i > length)
                    {
                        (start, length) = (i, end - i);
                    }

                    i = end;
                }

                if (start < 0)
                {
                    return -1;
                }

                var at = start > 0 && _state[start - 1] == Working ? start + length / 2 : start;
                _state[at] = Working;
                return at;
            }
        }
    }

    // One chunk's ffmpeg, writing <key>.<unique>.partial.mp4. Finish waits for it and renames the file
    // to <key>.mp4, unless another export running at the same time has put the same chunk there first.
    // Disposing an encoder that didn't finish (the export failed or was stopped) gives it a few seconds
    // if it has all its frames, so the chunk is kept for the next export, and otherwise stops it and
    // deletes its file.
    private sealed class ChunkEncoder : IDisposable
    {
        private readonly Chunk _chunk;
        private readonly string _partial;
        private readonly Process _process;
        private readonly Queue<string> _lines = new();
        private readonly Task _stderr;
        private readonly Task _stdout;
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _closed;
        private bool _finished;

        public ChunkEncoder(Chunk chunk, EncodeSettings settings, FfmpegPaths ffmpeg, Action<string>? log)
        {
            _chunk = chunk;
            _partial = Path.Combine(Path.GetDirectoryName(chunk.File)!, $"{Path.GetFileNameWithoutExtension(chunk.File)}.{Guid.NewGuid():N}.partial.mp4");
            _process = Process.Start(ProcessRunner.StartInfo(ffmpeg.Ffmpeg, FfmpegArgs.Build(settings, [], _partial), redirectInput: true))
                ?? throw new ZeyoCutException($"could not start {ffmpeg.Ffmpeg}");
            _stderr = Task.Run(() =>
            {
                while (_process.StandardError.ReadLine() is { } line)
                {
                    log?.Invoke($"ffmpeg (chunk {chunk.Index}): {line}");
                    lock (_lines)
                    {
                        _lines.Enqueue(line);
                        if (_lines.Count > 30)
                        {
                            _lines.Dequeue();
                        }
                    }
                }
            });
            _stdout = _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        }

        public void Write(int frame, ReadOnlySpan<byte> pixels)
        {
            try
            {
                _process.StandardInput.BaseStream.Write(pixels);
            }
            catch (IOException)
            {
                _process.WaitForExit(10000);
                _stderr.Wait(2000);
                throw new ZeyoCutException($"ffmpeg stopped while receiving frame {frame}:\n{StderrTail()}");
            }
        }

        public void Close()
        {
            _process.StandardInput.Close();
            _closed = true;
        }

        // Waits for the chunk's file and returns the CPU time the encoder used.
        public double Finish(CancellationToken cancellationToken)
        {
            var limit = Exporter.FinishTimeout + Stopwatch.GetElapsedTime(_started) / 10;
            if (!ProcessRunner.WaitForExit(_process, limit, cancellationToken))
            {
                throw new ZeyoCutException($"ffmpeg did not finish within {limit.TotalSeconds:0}s after the last frame of chunk {_chunk.Index}, so the export was stopped:\n{StderrTail()}");
            }

            _stderr.Wait();
            _stdout.Wait();
            if (_process.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg failed with exit code {_process.ExitCode}:\n{StderrTail()}");
            }

            var cpu = Exporter.CpuSeconds(_process);
            Keep();
            return cpu ?? 0;
        }

        public void Dispose()
        {
            try
            {
                if (!_finished && _closed && _process.WaitForExit(5000) && _process.ExitCode == 0)
                {
                    Keep();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }

            if (!_finished)
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(5000);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                TryDelete(_partial);
            }

            _process.Dispose();
        }

        private void Keep()
        {
            try
            {
                File.Move(_partial, _chunk.File);
            }
            catch (IOException) when (File.Exists(_chunk.File))
            {
                TryDelete(_partial);
            }

            _finished = true;
        }

        private string StderrTail()
        {
            lock (_lines)
            {
                return string.Join("\n", _lines);
            }
        }
    }

    // Keeps the cache under CacheBytes, dropping the chunks used longest ago (except this export's),
    // and the partial chunks of exports that stopped more than a day ago.
    private static void Prune(string folder, IReadOnlySet<string> keep)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var file in files)
            {
                if (file.Name.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    {
                        TryDelete(file.FullName);
                    }

                    continue;
                }

                total += file.Length;
                if (total > CacheBytes && !keep.Contains(file.FullName))
                {
                    TryDelete(file.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Gigabytes(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture);

    // A reused chunk counts as just used, so pruning keeps it.
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
