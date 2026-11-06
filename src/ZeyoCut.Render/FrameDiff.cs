// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Render;

// Change is the mean color difference to the previous frame, 0-100%. Label: glitch, spike or cut.
public sealed record FrameScore(int Frame, double Time, double Change, string? Label = null, string? Note = null);

// Shake: camera shake ranges (seconds), which move on purpose and get no glitch or spike flags.
public sealed record FrameDiffFile(int Fps, int Width, int Height, int StartFrame, int EndFrame, double Median, List<FrameScore> Frames, List<TimeRange> Shake);

public sealed record FrameDiffResult(FrameDiffFile Scores, string SavedPath)
{
    public string Describe()
    {
        var frames = Scores.Frames;
        var scored = frames.Skip(1).ToList();
        var largest = scored.MaxBy(f => f.Change)!;
        var builder = new StringBuilder(
            $"frame_diff {Num(frames[0].Time)}–{Num(frames[^1].Time)}s (f{Scores.StartFrame}–f{Scores.EndFrame}, {frames.Count} frames compared at {Scores.Width}x{Scores.Height}): median change {Pct(Scores.Median)}, largest {Pct(largest.Change)} at f{largest.Frame} ({Num(largest.Time)}s)");
        var flagged = frames.Where(f => f.Label is "glitch" or "spike").ToList();
        if (flagged.Count == 0)
        {
            builder.Append("\nno glitches and no unexplained spikes");
        }
        else
        {
            builder.Append($"\nflagged ({flagged.Count}):");
            foreach (var frame in flagged.Take(30))
            {
                builder.Append($"\n- f{frame.Frame} {Num(frame.Time)}s {frame.Label}: {frame.Note}");
            }

            if (flagged.Count > 30)
            {
                builder.Append($"\n- … and {flagged.Count - 30} more (see the scores file)");
            }
        }

        var cuts = frames.Where(f => f.Label == "cut").ToList();
        if (cuts.Count > 0)
        {
            builder.Append($"\nexpected cuts ({cuts.Count}): {string.Join(", ", cuts.Take(12).Select(c => $"f{c.Frame} {Num(c.Time)}s ({c.Note})"))}{(cuts.Count > 12 ? ", …" : "")}");
        }

        if (Scores.Shake.Count > 0)
        {
            builder.Append($"\nnot checked for glitches and spikes: camera shake at {string.Join(", ", Scores.Shake.Select(r => $"{Num(r.Start)}–{Num(r.End)}s"))}");
        }

        builder.Append($"\nscores: {SavedPath}");
        return builder.ToString();
    }

    private static string Pct(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

// frame_diff: renders each frame small, scores how much it changed from the previous one and flags
// single-frame glitches (a frame unlike both neighbors while they match) and unexplained spikes.
// Layer starts and ends, drawing swaps, hold keyframes and analyzed scene cuts are expected cuts.
public static class FrameDiff
{
    public const int SampleWidth = 160;
    public const int MinFrames = 3;

    // A glitch frame's 'excess' is the part of its change that comes back on the next frame:
    // (in + out - neighbors' difference) / 2, zero for smooth motion. It must be at least MinGlitch
    // (percent) and three times the typical excess, at least GlitchShare of the change in and out,
    // and the frames next to it must not be glitch-like too (shake and steady flicker are wanted).
    public const double MinGlitch = 0.5;
    public const double GlitchShare = 0.5;

    // A spike changes at least this much (percent), stands far above the median and is SpikeRatio
    // times the change of the frames around it, so fast but smooth motion is not a spike.
    public const double MinSpike = 3;
    public const double SpikeRatio = 3;

    public static (int First, int Last) Frames(Project project, double startTime, double? endTime)
    {
        var end = endTime ?? project.Duration;
        if (!double.IsFinite(startTime) || startTime < 0 || !double.IsFinite(end) || end <= startTime)
        {
            throw new ZeyoCutException($"the range must have 0 <= startTime < endTime (got {Num(startTime)}–{Num(end)})");
        }

        var count = FrameMath.FrameCount(project);
        var first = Math.Clamp(FrameMath.NearestFrame(project, startTime), 0, count - 1);
        var last = Math.Clamp(FrameMath.NearestFrame(project, end), 0, count - 1);
        if (last - first + 1 < MinFrames)
        {
            throw new ZeyoCutException($"compare at least {MinFrames} frames; {Num(startTime)}–{Num(end)}s covers {Math.Max(0, last - first + 1)} at {project.Fps} fps");
        }

        return (first, last);
    }

    public static FrameDiffResult Run(FrameRenderer renderer, Project project, double startTime, double? endTime, Action<double>? progress, CancellationToken cancellationToken)
    {
        var (first, last) = Frames(project, startTime, endTime);
        var width = Math.Min(SampleWidth, project.Width);
        var height = Math.Max(1, (int)Math.Round(project.Height * width / (double)project.Width));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new ZeyoCutException($"could not create a {width}x{height} drawing surface");

        var count = last - first + 1;
        var change = new double[count];
        var skip = new double[count];
        byte[]? beforePrevious = null;
        byte[]? previous = null;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canvas = surface.Canvas;
            canvas.ResetMatrix();
            canvas.Scale(width / (float)project.Width, height / (float)project.Height);
            renderer.Render(project, first + i, canvas);
            canvas.ResetMatrix();
            byte[] pixels;
            using (var pixmap = surface.PeekPixels())
            {
                pixels = pixmap.GetPixelSpan().ToArray();
            }

            if (previous is not null)
            {
                change[i] = Difference(previous, pixels);
            }

            if (beforePrevious is not null)
            {
                skip[i - 1] = Difference(beforePrevious, pixels);
            }

            beforePrevious = previous;
            previous = pixels;
            progress?.Invoke((i + 1) / (double)count);
        }

        var scores = Label(project, renderer.ProjectDirectory, first, change, skip);
        var shaking = Shaking(project, first, count);
        var shake = new List<TimeRange>();
        for (var i = 0; i < count; i++)
        {
            if (shaking[i] && (i == 0 || !shaking[i - 1]))
            {
                var end = i;
                while (end + 1 < count && shaking[end + 1])
                {
                    end++;
                }

                shake.Add(new TimeRange(Math.Round(FrameMath.TimeOf(project, first + i), 3), Math.Round(FrameMath.TimeOf(project, first + end), 3)));
            }
        }

        var file = new FrameDiffFile(project.Fps, width, height, first, last, Median(change.Skip(1)), scores, shake);
        var path = Path.Combine(renderer.ProjectDirectory, ProjectStore.RendersFolder, $"frame_diff_{first:D5}-{last:D5}.json");
        AnalysisFiles.Save(path, file);
        return new FrameDiffResult(file, path);
    }

    // change[i]: frame i against i-1; skip[i]: frame i-1 against i+1 (both 0 where there's no pair).
    public static List<FrameScore> Label(Project project, string projectDirectory, int first, IReadOnlyList<double> change, IReadOnlyList<double> skip)
    {
        var count = change.Count;
        var median = Median(change.Skip(1));
        var spread = Median(change.Skip(1).Select(c => Math.Abs(c - median)));
        var excess = new double[count];
        for (var i = 1; i < count - 1; i++)
        {
            excess[i] = Math.Max(0, (change[i] + change[i + 1] - skip[i]) / 2);
        }

        var glitchMin = Math.Max(MinGlitch, 3 * Median(excess.Skip(1).Take(Math.Max(0, count - 2))));
        var spikeMin = Math.Max(MinSpike, Math.Max(median + 10 * spread, 4 * median));
        var expected = ExpectedChanges(project, projectDirectory);
        var shaking = Shaking(project, first, count);
        var scores = Enumerable.Range(0, count).Select(i => new FrameScore(first + i, FrameMath.TimeOf(project, first + i), Math.Round(change[i], 3))).ToList();
        var glitch = new bool[count];
        for (var i = 1; i < count - 1; i++)
        {
            var into = change[i];
            var outOf = change[i + 1];
            if (!shaking[i] && excess[i] >= glitchMin && excess[i] >= GlitchShare * Math.Min(into, outOf) && excess[i - 1] < glitchMin && excess[i + 1] < glitchMin)
            {
                glitch[i] = true;
                var why = expected.TryGetValue(first + i, out var reason) ? $"; {reason} here" : "";
                scores[i] = scores[i] with
                {
                    Label = "glitch",
                    Note = $"differs from both neighbors ({Pct(into)}, {Pct(outOf)}), which are closer to each other ({Pct(skip[i])}){why}",
                };
            }
        }

        for (var i = 1; i < count; i++)
        {
            if (glitch[i] || glitch[i - 1] || shaking[i] || change[i] < spikeMin)
            {
                continue;
            }

            if (expected.TryGetValue(first + i, out var reason))
            {
                scores[i] = scores[i] with { Label = "cut", Note = reason };
                continue;
            }

            var around = Math.Max(i > 1 ? change[i - 1] : 0, i + 1 < count ? change[i + 1] : 0);
            if (change[i] < SpikeRatio * around)
            {
                continue;
            }

            scores[i] = scores[i] with
            {
                Label = "spike",
                Note = $"{Pct(change[i])} change{(median > 0.01 ? $" ({Math.Round(change[i] / median)}× the median)" : "")} with no layer starting, ending or swapping drawings there",
            };
        }

        return scores;
    }

    // Frames (relative to 'first') next to or inside a camera shake.
    public static bool[] Shaking(Project project, int first, int count)
    {
        var moving = new bool[count + 2];
        if (project.Camera is { } camera && (camera.ShakeX.Keyframes.Count + camera.ShakeY.Keyframes.Count + camera.ShakeRotation.Keyframes.Count) > 0)
        {
            for (var i = 0; i < count + 2; i++)
            {
                var time = FrameMath.TimeOf(project, Math.Max(0, first + i - 1));
                moving[i] = Math.Abs(Interpolator.Evaluate(camera.ShakeX, time)) + Math.Abs(Interpolator.Evaluate(camera.ShakeY, time)) + Math.Abs(Interpolator.Evaluate(camera.ShakeRotation, time)) > 1e-6;
            }
        }

        return [.. Enumerable.Range(0, count).Select(i => moving[i] || moving[i + 1] || moving[i + 2])];
    }

    // Frames where a jump is expected, with the reason.
    public static Dictionary<int, string> ExpectedChanges(Project project, string projectDirectory)
    {
        var expected = new Dictionary<int, string>();
        void Add(double time, string reason)
        {
            var frame = (int)Math.Ceiling(time * project.Fps - 1e-6);
            expected.TryAdd(frame, reason);
        }

        foreach (var layer in project.Layers.OfType<VisualLayer>())
        {
            Add(layer.Start, $"'{layer.Id}' starts");
            Add(SceneEvaluator.EndOf(layer, project.Duration), $"'{layer.Id}' ends");
            if (layer is ImageLayer image)
            {
                foreach (var key in image.Drawing.Keyframes)
                {
                    Add(key.Time, $"'{layer.Id}' swaps to '{key.Value}'");
                }
            }

            foreach (var (name, value) in AnimatableProperties.Numeric(layer))
            {
                HoldJumps(value, $"'{layer.Id}'.{name}", Add);
            }

            if (layer is VideoLayer video && project.FindAsset(video.Asset) is { } asset && AnalysisFiles.LoadAnalysis(projectDirectory, asset) is { } analysis)
            {
                foreach (var cut in analysis.Scenes.Where(c => c.Time > video.TrimIn && c.Time < video.TrimOut))
                {
                    Add(video.Start + (cut.Time - video.TrimIn) / video.Speed, $"scene cut in '{video.Id}'");
                }
            }
        }

        if (project.Camera is { } camera)
        {
            foreach (var (name, value) in AnimatableProperties.Numeric(camera))
            {
                HoldJumps(value, $"camera.{name}", Add);
            }
        }

        return expected;
    }

    private static void HoldJumps(AnimatedValue value, string what, Action<double, string> add)
    {
        for (var i = 0; i + 1 < value.Keyframes.Count; i++)
        {
            if (value.Keyframes[i].Easing == Easing.Hold && Math.Abs(value.Keyframes[i + 1].Value - value.Keyframes[i].Value) > 1e-9)
            {
                add(value.Keyframes[i + 1].Time, $"{what} jumps (hold)");
            }
        }
    }

    // Mean of |dR|, |dG|, |dB| over all pixels, as a percentage of full scale.
    private static double Difference(byte[] a, byte[] b)
    {
        long total = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            total += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        return total * 100.0 / (a.Length / 4 * 3 * 255.0);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static string Pct(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
