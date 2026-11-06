// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

// Both preview and export decode through the same ffmpeg command: one input seek (-ss before -i),
// then raw RGBA frames in order on stdout. Only the seek point and the frame limit differ, so a frame
// decoded by seeking is bit-identical to the same frame reached by streaming.
public static class VideoDecoder
{
    public static List<string> Arguments(string file, double seekSeconds, int width, int height, int? maxFrames)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (seekSeconds > 0)
        {
            args.AddRange(["-ss", seekSeconds.ToString("0.######", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(
        [
            "-i", file,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"scale={width}:{height}:flags=bicubic,format=rgba",
            "-fps_mode", "passthrough",
        ]);
        if (maxFrames is { } count)
        {
            args.AddRange(["-frames:v", count.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"]);
        return args;
    }

    // Seeking to the middle of the previous frame makes the first decoded frame exactly 'frame',
    // whichever way the timestamps round.
    public static double SeekSeconds(Asset asset, int frame) =>
        frame <= 0 ? 0 : VideoTiming.TimeOfFrame(asset, frame) - VideoTiming.TimeOfFrame(asset, 1) / 2;

    // Frames are decoded at the source size, but never larger than twice the canvas.
    public static (int Width, int Height) DecodeSize(Asset asset, Project project)
    {
        var width = asset.Width ?? 0;
        var height = asset.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            throw new ZeyoCutException($"video asset '{asset.Id}' has no frame size");
        }

        var scale = Math.Min(1, Math.Min(2.0 * project.Width / width, 2.0 * project.Height / height));
        if (scale >= 1)
        {
            return (width, height);
        }

        return (Math.Max(2, (int)Math.Round(width * scale / 2) * 2), Math.Max(2, (int)Math.Round(height * scale / 2) * 2));
    }
}

public sealed class VideoFrameReader : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly Task _stderrTask;
    private readonly Queue<string> _stderr = new();
    private byte[] _current;
    private byte[] _scratch;
    private bool _ended;

    public VideoFrameReader(string ffmpeg, string file, int firstFrame, double seekSeconds, int width, int height, int? maxFrames)
    {
        File = file;
        Width = width;
        Height = height;
        NextFrame = firstFrame;
        _current = new byte[width * height * 4];
        _scratch = new byte[width * height * 4];
        _process = Process.Start(ProcessRunner.StartInfo(ffmpeg, VideoDecoder.Arguments(file, seekSeconds, width, height, maxFrames)))
            ?? throw new ZeyoCutException($"could not start {ffmpeg}");
        _stdout = _process.StandardOutput.BaseStream;
        _stderrTask = Task.Run(() =>
        {
            while (_process.StandardError.ReadLine() is { } line)
            {
                lock (_stderr)
                {
                    _stderr.Enqueue(line);
                    if (_stderr.Count > 20)
                    {
                        _stderr.Dequeue();
                    }
                }
            }
        });
    }

    public string File { get; }

    public int Width { get; }

    public int Height { get; }

    // Source frame index of the next frame Advance() will read.
    public int NextFrame { get; private set; }

    // Source frame index held in Current, or -1 before the first frame.
    public int CurrentFrame { get; private set; } = -1;

    public ReadOnlySpan<byte> Current => _current;

    public bool Advance()
    {
        if (_ended)
        {
            return false;
        }

        var read = _stdout.ReadAtLeast(_scratch, _scratch.Length, throwOnEndOfStream: false);
        if (read < _scratch.Length)
        {
            _ended = true;
            return false;
        }

        (_current, _scratch) = (_scratch, _current);
        CurrentFrame = NextFrame;
        NextFrame++;
        return true;
    }

    // Call after Advance() returned false: an ffmpeg error becomes an exception, a clean end of file does not.
    public void ThrowIfFailed()
    {
        if (!_process.WaitForExit(10000))
        {
            return;
        }

        _stderrTask.Wait(2000);
        if (_process.ExitCode != 0)
        {
            string tail;
            lock (_stderr)
            {
                tail = string.Join("\n", _stderr);
            }

            throw new ZeyoCutException($"ffmpeg could not decode '{System.IO.Path.GetFileName(File)}' (exit code {_process.ExitCode}): {tail}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}
