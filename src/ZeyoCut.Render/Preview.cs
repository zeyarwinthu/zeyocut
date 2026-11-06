// SPDX-License-Identifier: MIT

using System.Globalization;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Text;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Render;

// Proxied: the video assets drawn from their proxies.
public sealed record PreviewImage(
    byte[] Png,
    int Width,
    int Height,
    IReadOnlyList<int> Frames,
    string SavedPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Proxied = null);

public static class Preview
{
    public const int MinWidth = 64;
    public const int MaxWidth = 4096;

    // checkerboard shows transparent parts of the frame as gray squares instead of the background
    // color; layerId draws only that layer and its children. Footage is drawn from its proxy where it
    // has one. A frame shown before, with nothing it shows changed since, comes from the renderer's
    // preview cache.
    public static PreviewImage RenderFrame(FrameRenderer renderer, Project project, double time, int maxWidth, bool debug, bool checkerboard = false, string? layerId = null)
    {
        CheckMaxWidth(maxWidth);
        var frame = FrameMath.NearestFrame(project, time);
        var renders = RendersDirectory(renderer);
        Layer? solo = layerId is null ? null : ProjectEditor.GetLayer(project, layerId) is CutoutLayer cutout ? cutout : ProjectEditor.GetVisualLayer(project, layerId);
        var only = solo is null ? null : ProjectEditor.Subtree(project, solo.Id).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var suffix = (layerId is null ? "" : $"_{layerId}") + (checkerboard ? "_checker" : "");

        var warnings = new SortedSet<string>(renderer.TakeWarnings(), StringComparer.Ordinal);
        var proxied = Proxied(renderer, project, frame);
        var key = $"frame|{new FrameKeys(project, renderer.ProjectDirectory).Of(frame)}|{(checkerboard ? "checkerboard" : "")}|{(only is null ? "" : string.Join(",", only.Order(StringComparer.Ordinal)))}|{string.Join(",", proxied)}";
        var cached = renderer.Previews.Get<CachedFrame>(key);
        var changed = cached is null;
        SKImage? full = null;
        try
        {
            if (cached is null)
            {
                full = Checkered(renderer.RenderImage(project, frame, new RenderOptions(checkerboard, only, Proxies: true)), checkerboard);
                cached = new CachedFrame(Png.Encode(full), renderer.TakeWarnings());
            }

            warnings.UnionWith(cached.Warnings);
            var savedPath = Path.Combine(renders, $"frame_{frame:D5}{suffix}.png");
            File.WriteAllBytes(savedPath, cached.Png);

            var (width, height) = Png.FitWidth(project.Width, project.Height, maxWidth);
            byte[] png;
            if (debug)
            {
                full ??= Decode(cached.Png);
                using var preview = WithOverlay(renderer, project, full, frame, width, height);
                savedPath = Path.Combine(renders, $"frame_{frame:D5}{suffix}_debug.png");
                png = Png.Encode(preview);
                File.WriteAllBytes(savedPath, png);
            }
            else if (cached.Scaled is { } scaled && cached.ScaledWidth == width)
            {
                png = scaled;
            }
            else
            {
                full ??= Decode(cached.Png);
                using var preview = Png.Scale(full, width, height);
                png = Png.Encode(preview);
                cached = cached with { ScaledWidth = width, Scaled = png };
                changed = true;
            }

            if (changed)
            {
                renderer.Previews.Add(key, cached, cached.Bytes);
            }

            warnings.UnionWith(renderer.TakeWarnings());
            return new PreviewImage(png, width, height, [frame], savedPath, warnings.ToList(), proxied);
        }
        finally
        {
            full?.Dispose();
        }
    }

    public static SKImage RenderDebugFrame(FrameRenderer renderer, Project project, int frame, int width, int height)
    {
        using var full = renderer.RenderImage(project, frame, new RenderOptions(Proxies: true));
        return WithOverlay(renderer, project, full, frame, width, height);
    }

    public static PreviewImage RenderContactSheet(
        FrameRenderer renderer,
        Project project,
        double startTime,
        double endTime,
        int count,
        int columns,
        int maxWidth,
        bool debug)
    {
        CheckMaxWidth(maxWidth);
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || endTime < startTime)
        {
            throw new ZeyoCutException($"endTime ({endTime}) must be >= startTime ({startTime})");
        }

        if (count is < 1 or > 64)
        {
            throw new ZeyoCutException($"count must be between 1 and 64 (got {count})");
        }

        if (columns < 1 || columns > count)
        {
            throw new ZeyoCutException($"columns must be between 1 and count ({count}); got {columns}");
        }

        var frames = Enumerable.Range(0, count)
            .Select(i => count == 1 ? startTime : startTime + i * (endTime - startTime) / (count - 1))
            .Select(t => FrameMath.NearestFrame(project, t))
            .ToList();

        const int gap = 6;
        const int labelHeight = 22;
        var cellWidth = (maxWidth - gap * (columns + 1)) / columns;
        if (cellWidth < 32)
        {
            throw new ZeyoCutException($"maxWidth {maxWidth} is too small for {columns} columns");
        }

        var cellHeight = Math.Max(1, (int)Math.Round(cellWidth * (double)project.Height / project.Width));
        var rows = (count + columns - 1) / columns;
        var sheetWidth = gap * (columns + 1) + cellWidth * columns;
        var sheetHeight = gap * (rows + 1) + (cellHeight + labelHeight) * rows;

        using var surface = SKSurface.Create(new SKImageInfo(sheetWidth, sheetHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create the contact sheet surface");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x22, 0x22, 0x22));

        var typeface = SKFontManager.Default.MatchFamily("Segoe UI") ?? SKTypeface.Default;
        using var font = TextLayoutEngine.CreateFont(typeface, 14, embolden: false);
        using var labelPaint = new SKPaint();
        labelPaint.IsAntialias = true;
        labelPaint.Color = new SKColor(0xEE, 0xEE, 0xEE);

        // Cells shown before at this size come from the preview cache (but not with the debug overlay).
        var warnings = new SortedSet<string>(renderer.TakeWarnings(), StringComparer.Ordinal);
        var proxied = new SortedSet<string>(StringComparer.Ordinal);
        var keys = new FrameKeys(project, renderer.ProjectDirectory);
        for (var i = 0; i < frames.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            var x = gap + column * (cellWidth + gap);
            var y = gap + row * (cellHeight + labelHeight + gap);

            var fromProxies = Proxied(renderer, project, frames[i]);
            proxied.UnionWith(fromProxies);
            if (debug)
            {
                using var cell = RenderDebugFrame(renderer, project, frames[i], cellWidth, cellHeight);
                canvas.DrawImage(cell, x, y, new SKSamplingOptions(SKFilterMode.Nearest), null);
            }
            else
            {
                var key = $"cell|{keys.Of(frames[i])}|{cellWidth}x{cellHeight}|{string.Join(",", fromProxies)}";
                var cell = renderer.Previews.Get<CachedCell>(key)
                    ?? renderer.Previews.Add(key, new CachedCell(ScaledFrame(renderer, project, frames[i], cellWidth, cellHeight), renderer.TakeWarnings()), 4L * cellWidth * cellHeight);
                canvas.DrawImage(cell.Image, x, y, new SKSamplingOptions(SKFilterMode.Nearest), null);
                warnings.UnionWith(cell.Warnings);
            }

            var label = $"f{frames[i]} · {FrameMath.TimeOf(project, frames[i]).ToString("0.000", CultureInfo.InvariantCulture)}s";
            canvas.DrawText(label, x + 4, y + cellHeight + 16, SKTextAlign.Left, font, labelPaint);
        }

        using var sheet = surface.Snapshot();
        var renders = RendersDirectory(renderer);
        var savedPath = Path.Combine(renders, $"sheet_{frames[0]:D5}-{frames[^1]:D5}_{count}{(debug ? "_debug" : "")}.png");
        var png = Png.Encode(sheet);
        File.WriteAllBytes(savedPath, png);
        warnings.UnionWith(renderer.TakeWarnings());
        return new PreviewImage(png, sheetWidth, sheetHeight, frames, savedPath, warnings.ToList(), proxied.ToList());
    }

    // Frames of a video asset itself (not the timeline), labeled with source frame and timecode.
    public static PreviewImage RenderSourceSheet(
        FrameRenderer renderer,
        Asset asset,
        double startTime,
        double endTime,
        int count,
        int columns,
        int maxWidth)
    {
        CheckMaxWidth(maxWidth);
        if (asset.Type != AssetType.Video || asset.Width is not { } width || asset.Height is not { } height)
        {
            throw new ZeyoCutException($"asset '{asset.Id}' is not a video asset");
        }

        var duration = asset.Duration ?? 0;
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || startTime < 0 || endTime < startTime || startTime > duration)
        {
            throw new ZeyoCutException($"the source range must be inside 0–{duration.ToString("0.###", CultureInfo.InvariantCulture)}s with startTime <= endTime (got {startTime}–{endTime})");
        }

        if (count is < 1 or > 64)
        {
            throw new ZeyoCutException($"count must be between 1 and 64 (got {count})");
        }

        if (columns < 1 || columns > count)
        {
            throw new ZeyoCutException($"columns must be between 1 and count ({count}); got {columns}");
        }

        var lastFrame = Math.Max(0, (asset.FrameCount ?? 1) - 1);
        var frames = Enumerable.Range(0, count)
            .Select(i => count == 1 ? startTime : startTime + i * (Math.Min(endTime, duration) - startTime) / (count - 1))
            .Select(t => Math.Min(VideoTiming.FrameAt(asset, t), lastFrame))
            .ToList();

        const int gap = 6;
        const int labelHeight = 22;
        var cellWidth = (maxWidth - gap * (columns + 1)) / columns;
        if (cellWidth < 32)
        {
            throw new ZeyoCutException($"maxWidth {maxWidth} is too small for {columns} columns");
        }

        cellWidth = Math.Min(cellWidth, width);
        var cellHeight = Math.Max(2, (int)Math.Round(cellWidth * (double)height / width));
        var rows = (count + columns - 1) / columns;
        var sheetWidth = gap * (columns + 1) + cellWidth * columns;
        var sheetHeight = gap * (rows + 1) + (cellHeight + labelHeight) * rows;

        using var surface = SKSurface.Create(new SKImageInfo(sheetWidth, sheetHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create the contact sheet surface");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x22, 0x22, 0x22));

        var typeface = SKFontManager.Default.MatchFamily("Segoe UI") ?? SKTypeface.Default;
        using var font = TextLayoutEngine.CreateFont(typeface, 14, embolden: false);
        using var labelPaint = new SKPaint();
        labelPaint.IsAntialias = true;
        labelPaint.Color = new SKColor(0xEE, 0xEE, 0xEE);

        var proxy = Proxies.Wanted(asset) && cellWidth <= Proxies.Size(asset).Width ? Proxies.Ready(renderer.ProjectDirectory, asset) : null;
        var file = proxy ?? ProjectStore.AssetPath(renderer.ProjectDirectory, asset);
        for (var i = 0; i < frames.Count; i++)
        {
            var x = gap + i % columns * (cellWidth + gap);
            var y = gap + i / columns * (cellHeight + labelHeight + gap);
            var image = renderer.Video.GetFrame($"source:{asset.Id}", file, asset, frames[i], cellWidth, cellHeight);
            canvas.DrawImage(image, x, y, new SKSamplingOptions(SKFilterMode.Nearest), null);

            var label = $"src {VideoTiming.TimeOfFrame(asset, frames[i]).ToString("0.000", CultureInfo.InvariantCulture)}s · f{frames[i]}";
            canvas.DrawText(label, x + 4, y + cellHeight + 16, SKTextAlign.Left, font, labelPaint);
        }

        using var sheet = surface.Snapshot();
        var renders = RendersDirectory(renderer);
        var savedPath = Path.Combine(renders, $"source_{asset.Id}_{frames[0]:D6}-{frames[^1]:D6}_{count}.png");
        var png = Png.Encode(sheet);
        File.WriteAllBytes(savedPath, png);
        return new PreviewImage(png, sheetWidth, sheetHeight, frames, savedPath, renderer.TakeWarnings(), proxy is null ? [] : [asset.Id]);
    }

    // detect_and_track's sheet: one picture per track, labeled with its id and a detail line, saved as
    // renders/<name>.png.
    public static PreviewImage RenderTrackSheet(string projectDirectory, string name, IReadOnlyList<(string Id, string Detail, SKImage Picture)> cells)
    {
        if (cells.Count == 0)
        {
            throw new ZeyoCutException("there are no tracks to show");
        }

        const int gap = 6;
        const int cell = 160;
        const int labelHeight = 40;
        var columns = Math.Min(cells.Count, 5);
        var rows = (cells.Count + columns - 1) / columns;
        var sheetWidth = gap * (columns + 1) + cell * columns;
        var sheetHeight = gap * (rows + 1) + (cell + labelHeight) * rows;

        using var surface = SKSurface.Create(new SKImageInfo(sheetWidth, sheetHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException("could not create the contact sheet surface");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x22, 0x22, 0x22));

        var typeface = SKFontManager.Default.MatchFamily("Segoe UI") ?? SKTypeface.Default;
        using var font = TextLayoutEngine.CreateFont(typeface, 14, embolden: true);
        using var small = TextLayoutEngine.CreateFont(typeface, 12, embolden: false);
        using var labelPaint = new SKPaint();
        labelPaint.IsAntialias = true;
        labelPaint.Color = new SKColor(0xEE, 0xEE, 0xEE);

        for (var i = 0; i < cells.Count; i++)
        {
            var (id, detail, picture) = cells[i];
            var x = gap + i % columns * (cell + gap);
            var y = gap + i / columns * (cell + labelHeight + gap);
            var scale = Math.Min(cell / (float)picture.Width, cell / (float)picture.Height);
            var width = picture.Width * scale;
            var height = picture.Height * scale;
            var target = SKRect.Create(x + (cell - width) / 2, y + (cell - height) / 2, width, height);
            canvas.DrawImage(picture, new SKRect(0, 0, picture.Width, picture.Height), target, FrameRenderer.ImageSampling, null);
            canvas.DrawText(id, x + 4, y + cell + 16, SKTextAlign.Left, font, labelPaint);
            canvas.DrawText(detail, x + 4, y + cell + 33, SKTextAlign.Left, small, labelPaint);
        }

        using var sheet = surface.Snapshot();
        var renders = Path.Combine(projectDirectory, ProjectStore.RendersFolder);
        Directory.CreateDirectory(renders);
        var savedPath = Path.Combine(renders, name + ".png");
        var png = Png.Encode(sheet);
        File.WriteAllBytes(savedPath, png);
        return new PreviewImage(png, sheetWidth, sheetHeight, [], savedPath, []);
    }

    public static readonly SKColor CheckerLight = new(0xA0, 0xA0, 0xA0);
    public static readonly SKColor CheckerDark = new(0x70, 0x70, 0x70);

    // Square size of the checkerboard: a 40th of the frame's short side, at least 8 pixels.
    public static int CheckerSize(int width, int height) => Math.Max(8, (int)Math.Round(Math.Min(width, height) / 40.0));

    // The frame over a checkerboard (when asked), taking ownership of the frame.
    private static SKImage Checkered(SKImage frame, bool checkerboard)
    {
        if (!checkerboard)
        {
            return frame;
        }

        using (frame)
        {
            using var surface = SKSurface.Create(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new ZeyoCutException($"could not create a {frame.Width}x{frame.Height} drawing surface");
            var canvas = surface.Canvas;
            canvas.Clear(CheckerLight);
            var size = CheckerSize(frame.Width, frame.Height);
            using var dark = new SKPaint();
            dark.Color = CheckerDark;
            for (var y = 0; y * size < frame.Height; y++)
            {
                for (var x = (y % 2); x * size < frame.Width; x += 2)
                {
                    canvas.DrawRect(new SKRect(x * size, y * size, (x + 1) * size, (y + 1) * size), dark);
                }
            }

            canvas.DrawImage(frame, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            return surface.Snapshot();
        }
    }

    // The video assets shown at the frame that have a proxy, so a preview draws them from it.
    private static List<string> Proxied(FrameRenderer renderer, Project project, int frame) =>
        SceneEvaluator.Evaluate(project, FrameMath.TimeOf(project, frame), cutouts: true)
            .Where(e => e.Shown && e.Opacity > 0)
            .Select(e => e.Layer is VideoLayer video ? project.FindAsset(video.Asset) : null)
            .Where(a => a is not null && Proxies.Ready(renderer.ProjectDirectory, a) is not null)
            .Select(a => a!.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static SKImage Decode(byte[] png) =>
        SKImage.FromEncodedData(png) ?? throw new ZeyoCutException("could not read a cached preview");

    private static SKImage ScaledFrame(FrameRenderer renderer, Project project, int frame, int width, int height)
    {
        using var full = renderer.RenderImage(project, frame, new RenderOptions(Proxies: true));
        return Png.Scale(full, width, height);
    }

    private static SKImage WithOverlay(FrameRenderer renderer, Project project, SKImage full, int frame, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new ZeyoCutException($"could not create a {width}x{height} drawing surface");
        using var paint = new SKPaint();
        paint.IsAntialias = true;
        surface.Canvas.DrawImage(full, new SKRect(0, 0, width, height), FrameRenderer.ImageSampling, paint);
        DebugOverlay.Draw(surface.Canvas, renderer, project, FrameMath.TimeOf(project, frame), width / (float)project.Width);
        return surface.Snapshot();
    }

    private static string RendersDirectory(FrameRenderer renderer)
    {
        var directory = Path.Combine(renderer.ProjectDirectory, ProjectStore.RendersFolder);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CheckMaxWidth(int maxWidth)
    {
        if (maxWidth is < MinWidth or > MaxWidth)
        {
            throw new ZeyoCutException($"maxWidth must be between {MinWidth} and {MaxWidth} (got {maxWidth})");
        }
    }
}
