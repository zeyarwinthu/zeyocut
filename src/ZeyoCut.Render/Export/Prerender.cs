// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// A prerendered layer: File is the .mov in the prerender folder, FirstFrame and FrameCount the frames
// of the timeline it covers, Key the hash of those frames' keys with only the layer and its children
// drawn, so it is current while nothing it shows has changed.
public sealed record PrerenderedLayer(string LayerId, string File, int Width, int Height, int Fps, int FirstFrame, int FrameCount, string Key);

public sealed record PrerenderResult(PrerenderedLayer Layer, string Path, long Bytes, TimeSpan Took, bool Reused, IReadOnlyList<string> Warnings);

// A layer and its children drawn alone on a transparent canvas, over the frames the layer is on, and
// saved as ProRes 4444 with alpha (.mov), as render_frame draws it with layerId and a transparent
// background. An editor that opens an OTIO or FCPXML timeline lays them over the clips, so they carry
// what those formats can't: text, keyframed motion, effects and the camera. prerender.json in the
// folder lists them.
public static class Prerender
{
    public const string ManifestName = "prerender.json";
    public const string Format = "ProRes 4444 with alpha";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Manifest(List<PrerenderedLayer> Layers);

    // The layer to draw and the ids drawn with it: it and its children (or a subject cutout).
    public static (Layer Root, IReadOnlySet<string> Only) Choose(Project project, string layerId)
    {
        Layer root = ProjectEditor.GetLayer(project, layerId) is CutoutLayer cutout ? cutout : ProjectEditor.GetVisualLayer(project, layerId);
        if (!root.Visible)
        {
            throw new ZeyoCutException($"layer '{root.Id}' is hidden, so it has nothing to prerender; update_layer with visible true shows it");
        }

        return (root, ProjectEditor.Subtree(project, root.Id).Select(l => l.Id).ToHashSet(StringComparer.Ordinal));
    }

    // The frames the layer is on. A layer only shows while its parents do, and its children while it does.
    public static (int First, int Count) Span(Project project, Layer root)
    {
        var start = root.Start;
        var stop = SceneEvaluator.EndOf(root, project.Duration);
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        for (var parent = root.ParentId is { } id ? project.FindLayer(id) : null; parent is not null && seen.Add(parent.Id); parent = parent.ParentId is { } next ? project.FindLayer(next) : null)
        {
            start = Math.Max(start, parent.Start);
            stop = Math.Min(stop, SceneEvaluator.EndOf(parent, project.Duration));
        }

        var total = FrameMath.FrameCount(project);
        var first = Math.Clamp((int)Math.Ceiling(start * project.Fps - 1e-6), 0, total);
        var end = Math.Clamp((int)Math.Ceiling(stop * project.Fps - 1e-6), first, total);
        return (first, end - first);
    }

    public static string Key(Project project, string projectDirectory, IReadOnlySet<string> only, int first, int count)
    {
        var keys = new FrameKeys(project, projectDirectory);
        return FrameKeys.Combine($"prerender|{Format}", Enumerable.Range(first, count).Select(f => keys.Of(f, only)));
    }

    public static string FileName(string layerId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(layerId.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".mov";
    }

    public static List<string> Arguments(int width, int height, int fps, int frameCount, string output) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
        "-f", "rawvideo",
        "-pix_fmt", "rgba",
        "-s", $"{width}x{height}",
        "-framerate", fps.ToString(CultureInfo.InvariantCulture),
        "-i", "pipe:0",
        "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuva444p10le,setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv",
        "-c:v", "prores_ks",
        "-profile:v", "4444",
        "-alpha_bits", "16",
        "-vendor", "apl0",
        "-pix_fmt", "yuva444p10le",
        "-colorspace", "bt709",
        "-color_primaries", "bt709",
        "-color_trc", "bt709",
        "-color_range", "tv",
        "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture),
        output,
    ];

    // The prerenders listed in the folder, current or not.
    public static IReadOnlyList<PrerenderedLayer> List(string folder)
    {
        var path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json)?.Layers ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Draws the layer over its frames into folder\<layer id>.mov, unless the one there is current.
    public static PrerenderResult Make(
        Project project,
        string projectDirectory,
        string folder,
        string layerId,
        FfmpegPaths ffmpeg,
        Action<int, int>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var (root, only) = Choose(project, layerId);
        var (first, count) = Span(project, root);
        if (count == 0)
        {
            throw new ZeyoCutException($"layer '{root.Id}' is on no frame of the timeline (it starts at {root.Start.ToString("0.###", CultureInfo.InvariantCulture)}s and the project ends at {project.Duration.ToString("0.###", CultureInfo.InvariantCulture)}s)");
        }

        foreach (var video in project.Layers.OfType<VideoLayer>().Where(v => only.Contains(v.Id)))
        {
            var path = ProjectStore.AssetPath(projectDirectory, project.FindAsset(video.Asset)!);
            if (!File.Exists(path))
            {
                throw new ZeyoCutException($"video layer '{video.Id}': file is missing: {path}");
            }
        }

        var key = Key(project, projectDirectory, only, first, count);
        var file = FileName(root.Id);
        var output = Path.Combine(folder, file);
        var entry = new PrerenderedLayer(root.Id, file, project.Width, project.Height, project.Fps, first, count, key);
        if (List(folder).Any(l => l == entry) && File.Exists(output))
        {
            return new PrerenderResult(entry, output, new FileInfo(output).Length, stopwatch.Elapsed, true, []);
        }

        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ".partial.mov");
        var arguments = Arguments(project.Width, project.Height, project.Fps, count, temp);
        log?.Invoke($"ffmpeg {string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        using var process = Process.Start(ProcessRunner.StartInfo(ffmpeg.Ffmpeg, arguments, redirectInput: true))
            ?? throw new ZeyoCutException($"could not start {ffmpeg.Ffmpeg}");
        var lines = new Queue<string>();
        var stderr = Task.Run(() =>
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                log?.Invoke($"ffmpeg: {line}");
                lock (lines)
                {
                    lines.Enqueue(line);
                    if (lines.Count > 30)
                    {
                        lines.Dequeue();
                    }
                }
            }
        });
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);

        string Tail()
        {
            lock (lines)
            {
                return string.Join("\n", lines);
            }
        }

        IReadOnlyList<string> warnings;
        try
        {
            using var renderer = new FrameRenderer(projectDirectory, () => ffmpeg.Ffmpeg, streamVideo: true);
            using var surface = SKSurface.Create(FrameRenderer.FrameInfo(project))
                ?? throw new ZeyoCutException($"could not create a {project.Width}x{project.Height} drawing surface");

            // ffmpeg's rgba is straight alpha, and the canvas is premultiplied.
            using var straight = new SKBitmap(new SKImageInfo(project.Width, project.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var options = new RenderOptions(Transparent: true, Only: only);
            var stdin = process.StandardInput.BaseStream;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                renderer.Render(project, first + i, surface.Canvas, options);
                if (!surface.ReadPixels(straight.Info, straight.GetPixels(), straight.RowBytes, 0, 0))
                {
                    throw new ZeyoCutException($"could not read frame {first + i} of layer '{root.Id}'");
                }

                try
                {
                    stdin.Write(straight.GetPixelSpan());
                }
                catch (IOException)
                {
                    process.WaitForExit(10000);
                    stderr.Wait(2000);
                    throw new ZeyoCutException($"ffmpeg stopped while receiving frame {first + i}:\n{Tail()}");
                }

                progress?.Invoke(i + 1, count);
            }

            stdin.Close();
            var limit = Exporter.FinishTimeout + stopwatch.Elapsed / 10;
            if (!ProcessRunner.WaitForExit(process, limit, cancellationToken))
            {
                throw new ZeyoCutException($"ffmpeg did not finish within {limit.TotalSeconds:0}s after the last frame:\n{Tail()}");
            }

            stderr.Wait();
            stdout.Wait();
            if (process.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg failed with exit code {process.ExitCode}:\n{Tail()}");
            }

            warnings = renderer.TakeWarnings();
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

            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }

        File.Move(temp, output, overwrite: true);
        Save(folder, List(folder).Where(l => l.LayerId != root.Id).Append(entry).OrderBy(l => l.LayerId, StringComparer.Ordinal).ToList());
        stopwatch.Stop();
        return new PrerenderResult(entry, output, new FileInfo(output).Length, stopwatch.Elapsed, false, warnings);
    }

    private static void Save(string folder, List<PrerenderedLayer> layers)
    {
        var path = Path.Combine(folder, ManifestName);
        var temp = path + ".partial";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Manifest(layers), Json));
        File.Move(temp, path, overwrite: true);
    }
}
