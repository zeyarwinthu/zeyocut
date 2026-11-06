// SPDX-License-Identifier: MIT

using System.Globalization;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

public sealed record ThumbnailResult(
    string Path,
    int Frame,
    int Width,
    int Height,
    long Bytes,
    bool Cropped,
    double Enlarged,
    byte[] Preview,
    int PreviewWidth,
    int PreviewHeight,
    IReadOnlyList<string> Warnings);

public sealed record FrameSequenceResult(
    string Folder,
    int First,
    int Last,
    string Format,
    int Width,
    int Height,
    long Bytes,
    int Replaced,
    IReadOnlyList<string> Warnings)
{
    public int Count => Last - First + 1;
}

// Still images from the timeline: a thumbnail (export_thumbnail) and numbered frames (export_frames).
public static class FrameExport
{
    public const int MinSize = 16;
    public const int MaxSize = 7680;

    // YouTube rejects custom thumbnails over 2 MB.
    public const long ThumbnailLimit = 2 * 1024 * 1024;

    public const int JpegQuality = 95;

    public static readonly IReadOnlyList<string> Formats = ["png", "jpg"];

    public static string FileName(int frame, string format) => $"frame_{frame:D5}.{format}";

    public static ThumbnailResult Thumbnail(FrameRenderer renderer, Project project, double time, int width, int height, string path, int previewWidth)
    {
        CheckSize(width, "width");
        CheckSize(height, "height");
        MatteFiles.ThrowIfPending(project, renderer.ProjectDirectory);
        if (!double.IsFinite(time) || time < 0 || time > project.Duration)
        {
            throw new ZeyoCutException($"time must be between 0 and the end of the project ({Num(project.Duration)}s); got {Num(time)}");
        }

        var frame = FrameMath.NearestFrame(project, time);
        using var full = renderer.RenderImage(project, frame);
        using var thumbnail = Png.Cover(full, width, height);
        var png = Png.Encode(thumbnail);
        var output = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, png);

        var warnings = renderer.TakeWarnings().ToList();
        if (png.Length > ThumbnailLimit)
        {
            warnings.Add($"the PNG is {ExportSummary.FileSize(png.Length)}, over YouTube's 2 MB thumbnail limit, so YouTube would reject it; a smaller size or a frame with less fine detail compresses smaller");
        }

        var (previewW, previewH) = Png.FitWidth(width, height, previewWidth);
        byte[] preview;
        if (previewW == width)
        {
            preview = png;
        }
        else
        {
            using var small = Png.Scale(thumbnail, previewW, previewH);
            preview = Png.Encode(small);
        }

        var cropped = !SameShape(project.Width, project.Height, width, height);
        var enlarged = Math.Max(width / (double)project.Width, height / (double)project.Height);
        return new ThumbnailResult(output, frame, width, height, png.Length, cropped, enlarged, preview, previewW, previewH, warnings);
    }

    // Both ends included, snapped to the nearest frame; an end past the project stops at the last frame.
    public static (int First, int Last) Frames(Project project, double startTime, double endTime)
    {
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || startTime < 0 || endTime < startTime)
        {
            throw new ZeyoCutException($"the range must have 0 <= startTime <= endTime (got {Num(startTime)}–{Num(endTime)})");
        }

        if (startTime > project.Duration)
        {
            throw new ZeyoCutException($"startTime {Num(startTime)}s is after the end of the project ({Num(project.Duration)}s)");
        }

        return (FrameMath.NearestFrame(project, startTime), FrameMath.NearestFrame(project, endTime));
    }

    public static string CheckFormat(string? format)
    {
        var name = (format ?? "").Trim().ToLowerInvariant();
        if (name == "jpeg")
        {
            name = "jpg";
        }

        return Formats.Contains(name)
            ? name
            : throw new ZeyoCutException($"unknown format '{format}'; valid values: {string.Join(", ", Formats)}");
    }

    // Writes frame_<number>.<format> for every frame from first to last into folder, after deleting
    // the frames an earlier run left there, so the folder holds exactly this sequence.
    public static FrameSequenceResult Sequence(
        FrameRenderer renderer,
        Project project,
        int first,
        int last,
        string format,
        string folder,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        format = CheckFormat(format);
        MatteFiles.ThrowIfPending(project, renderer.ProjectDirectory);
        var output = Path.GetFullPath(folder);
        Directory.CreateDirectory(output);
        var replaced = 0;
        foreach (var old in Formats.SelectMany(f => Directory.GetFiles(output, $"frame_*.{f}")))
        {
            File.Delete(old);
            replaced++;
        }

        var encoding = format == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        var quality = format == "jpg" ? JpegQuality : 100;
        using var surface = SKSurface.Create(FrameRenderer.FrameInfo(project))
            ?? throw new ZeyoCutException($"could not create a {project.Width}x{project.Height} drawing surface");
        long bytes = 0;
        for (var frame = first; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            renderer.Render(project, frame, surface.Canvas);
            using var image = surface.Snapshot();
            using var data = image.Encode(encoding, quality)
                ?? throw new ZeyoCutException($"{format} encoding failed at frame {frame}");
            var file = Path.Combine(output, FileName(frame, format));
            using (var stream = File.Create(file))
            {
                data.SaveTo(stream);
            }

            bytes += data.Size;
            progress?.Invoke((frame - first + 1) / (double)(last - first + 1));
        }

        return new FrameSequenceResult(output, first, last, format, project.Width, project.Height, bytes, replaced, renderer.TakeWarnings());
    }

    private static void CheckSize(int value, string name)
    {
        if (value is < MinSize or > MaxSize)
        {
            throw new ZeyoCutException($"{name} must be between {MinSize} and {MaxSize} pixels (got {value})");
        }
    }

    private static bool SameShape(int width, int height, int otherWidth, int otherHeight) =>
        Math.Abs(width * (double)otherHeight - height * (double)otherWidth) <= Math.Max(width, height);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
