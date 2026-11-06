// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Render;

public sealed record VideoMatteResult(string Path, int Frames, int Width, int Height, double Background, TimeSpan Elapsed);

// The AI mattes: BiRefNet for stills and Robust Video Matting for clips. RVM carries a recurrent state
// from frame to frame, so a clip's matte is computed once for the whole file, in order, and saved as
// a gray FFV1 video (every frame a keyframe, so any frame can be read on its own).
public static class AiMattes
{
    private const int ImageSize = 1024;

    // RVM works on a copy downsampled to about this size on the long side (its guide: 0.25 for 1080p).
    private const double VideoWorkingSide = 512;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Deviation = [0.229f, 0.224f, 0.225f];

    // BiRefNet sees the picture over white, stretched to 1024x1024; its matte is scaled back to the picture.
    public static byte[] Image(AiSession session, SKImage picture)
    {
        var plane = ImageSize * ImageSize;
        byte[] rgba;
        using (var surface = SKSurface.Create(new SKImageInfo(ImageSize, ImageSize, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create a surface for the AI matte"))
        {
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawImage(picture, new SKRect(0, 0, picture.Width, picture.Height), new SKRect(0, 0, ImageSize, ImageSize), FrameRenderer.ImageSampling, null);
            using var resized = surface.Snapshot();
            rgba = Pixels.Read(resized);
        }

        var input = new DenseTensor<float>([1, 3, ImageSize, ImageSize]);
        var values = input.Buffer.Span;
        for (var i = 0; i < plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                values[c * plane + i] = (rgba[i * 4 + c] / 255f - Mean[c]) / Deviation[c];
            }
        }

        var mask = new byte[plane];
        var name = session.Session.InputMetadata.Keys.First();
        using (var results = session.Session.Run([NamedOnnxValue.CreateFromTensor(name, input)]))
        {
            var output = Dense(results.First().AsTensor<float>());
            if (output.Length != plane)
            {
                throw new ZeyoCutException($"the image model returned {output.Length} values instead of {plane}");
            }

            // The model's output is a logit per pixel.
            var logits = output.Buffer.Span;
            for (var i = 0; i < plane; i++)
            {
                mask[i] = (byte)Math.Round(255 / (1 + Math.Exp(-logits[i])));
            }
        }

        using var small = Pixels.Alpha(mask, ImageSize, ImageSize);
        using var back = SKSurface.Create(new SKImageInfo(picture.Width, picture.Height, SKColorType.Alpha8, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create a matte surface");
        back.Canvas.DrawImage(small, new SKRect(0, 0, ImageSize, ImageSize), new SKRect(0, 0, picture.Width, picture.Height), FrameRenderer.ImageSampling, null);
        using var result = back.Snapshot();
        return Pixels.ReadAlpha(result);
    }

    // Runs RVM over every frame of the clip and writes the matte to path (through path.part).
    public static VideoMatteResult Video(
        AiSession session,
        string ffmpeg,
        string file,
        Asset asset,
        string path,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        var (width, height) = MatteFiles.VideoMatteSize(asset);
        var plane = width * height;
        var total = Math.Max(1, asset.FrameCount ?? 1);
        var stopwatch = Stopwatch.StartNew();
        var part = path + ".part";
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{width}x{height}", "-framerate", asset.FrameRate ?? "30", "-i", "pipe:0",
            "-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "gray", "-f", "matroska", part,
        };

        using var reader = new VideoFrameReader(ffmpeg, file, 0, 0, width, height, null);
        using var encoder = Process.Start(ProcessRunner.StartInfo(ffmpeg, arguments, redirectInput: true))
            ?? throw new ZeyoCutException($"could not start {ffmpeg}");
        var errors = new Queue<string>();
        var errorTask = Task.Run(() =>
        {
            while (encoder.StandardError.ReadLine() is { } line)
            {
                lock (errors)
                {
                    errors.Enqueue(line);
                    if (errors.Count > 20)
                    {
                        errors.Dequeue();
                    }
                }
            }
        });
        var outputTask = encoder.StandardOutput.BaseStream.CopyToAsync(Stream.Null);

        string Errors()
        {
            lock (errors)
            {
                return string.Join("\n", errors);
            }
        }

        var source = new DenseTensor<float>([1, 3, height, width]);
        var ratio = new DenseTensor<float>(new[] { (float)Math.Min(1, VideoWorkingSide / Math.Max(width, height)) }, [1]);
        var state = Enumerable.Range(0, 4).Select(_ => new DenseTensor<float>([1, 1, 1, 1])).ToArray();
        var alpha = new byte[plane];
        long background = 0;
        var frames = 0;
        try
        {
            var stdin = encoder.StandardInput.BaseStream;
            while (reader.Advance())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rgba = reader.Current;
                var values = source.Buffer.Span;
                for (var i = 0; i < plane; i++)
                {
                    values[i] = rgba[i * 4] / 255f;
                    values[plane + i] = rgba[i * 4 + 1] / 255f;
                    values[2 * plane + i] = rgba[i * 4 + 2] / 255f;
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("src", source),
                    NamedOnnxValue.CreateFromTensor("downsample_ratio", ratio),
                };
                for (var k = 0; k < 4; k++)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor($"r{k + 1}i", state[k]));
                }

                using (var results = session.Session.Run(inputs))
                {
                    var pha = Dense(results.First(r => r.Name == "pha").AsTensor<float>()).Buffer.Span;
                    for (var i = 0; i < plane; i++)
                    {
                        var value = (byte)Math.Round(Math.Clamp(pha[i], 0f, 1f) * 255);
                        alpha[i] = value;
                        if (value < 128)
                        {
                            background++;
                        }
                    }

                    for (var k = 0; k < 4; k++)
                    {
                        state[k] = Dense(results.First(r => r.Name == $"r{k + 1}o").AsTensor<float>());
                    }
                }

                try
                {
                    stdin.Write(alpha);
                }
                catch (IOException)
                {
                    encoder.WaitForExit(10000);
                    errorTask.Wait(2000);
                    throw new ZeyoCutException($"ffmpeg stopped while writing the matte of frame {frames}:\n{Errors()}");
                }

                frames++;
                progress?.Invoke(frames, total);
            }

            reader.ThrowIfFailed();
            if (frames == 0)
            {
                throw new ZeyoCutException($"no frames could be decoded from '{Path.GetFileName(file)}'");
            }

            stdin.Close();
            if (!ProcessRunner.WaitForExit(encoder, TimeSpan.FromMinutes(2), cancellationToken))
            {
                throw new ZeyoCutException($"ffmpeg did not finish writing the matte:\n{Errors()}");
            }

            errorTask.Wait();
            outputTask.Wait();
            if (encoder.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg could not write the matte (exit code {encoder.ExitCode}):\n{Errors()}");
            }

            File.Move(part, path, overwrite: true);
        }
        catch
        {
            if (!encoder.HasExited)
            {
                try
                {
                    encoder.Kill(entireProcessTree: true);
                    encoder.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                }
            }

            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
            }

            throw;
        }

        return new VideoMatteResult(path, frames, width, height, background / ((double)plane * frames), stopwatch.Elapsed);
    }

    // One frame of Video's work at width x height (system_check's benchmark): the same tensors, steps
    // and recurrent state, on a gray picture.
    internal static Action VideoStep(AiSession session, int width, int height)
    {
        var plane = width * height;
        var rgba = new byte[plane * 4];
        Array.Fill(rgba, (byte)128);
        var source = new DenseTensor<float>([1, 3, height, width]);
        var ratio = new DenseTensor<float>(new[] { (float)Math.Min(1, VideoWorkingSide / Math.Max(width, height)) }, [1]);
        var state = Enumerable.Range(0, 4).Select(_ => new DenseTensor<float>([1, 1, 1, 1])).ToArray();
        var alpha = new byte[plane];
        return () =>
        {
            var values = source.Buffer.Span;
            for (var i = 0; i < plane; i++)
            {
                values[i] = rgba[i * 4] / 255f;
                values[plane + i] = rgba[i * 4 + 1] / 255f;
                values[2 * plane + i] = rgba[i * 4 + 2] / 255f;
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("src", source),
                NamedOnnxValue.CreateFromTensor("downsample_ratio", ratio),
            };
            for (var k = 0; k < 4; k++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor($"r{k + 1}i", state[k]));
            }

            using var results = session.Session.Run(inputs);
            var pha = Dense(results.First(r => r.Name == "pha").AsTensor<float>()).Buffer.Span;
            for (var i = 0; i < plane; i++)
            {
                alpha[i] = (byte)Math.Round(Math.Clamp(pha[i], 0f, 1f) * 255);
            }

            for (var k = 0; k < 4; k++)
            {
                state[k] = Dense(results.First(r => r.Name == $"r{k + 1}o").AsTensor<float>());
            }
        };
    }

    // Share of a matte's pixels that are mostly background.
    public static double Background(byte[] alpha) =>
        alpha.Count(a => a < 128) / (double)Math.Max(1, alpha.Length);

    public static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    // A copy that outlives the results it came from.
    private static DenseTensor<float> Dense(Tensor<float> tensor)
    {
        var copy = new DenseTensor<float>(tensor.Dimensions.ToArray());
        if (tensor is DenseTensor<float> dense)
        {
            dense.Buffer.Span.CopyTo(copy.Buffer.Span);
        }
        else
        {
            for (var i = 0; i < tensor.Length; i++)
            {
                copy.SetValue(i, tensor.GetValue(i));
            }
        }

        return copy;
    }
}
