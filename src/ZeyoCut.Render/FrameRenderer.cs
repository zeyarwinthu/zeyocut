// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Text;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Render;

// Previews can leave the background transparent (checkerboard) or draw only some layers (a layer and
// its children, to look at one layer's edges); exports always draw everything. After draws only what
// is drawn above that layer (text_occlusion). Proxies draws footage from its proxy where it has one
// (previews only).
public sealed record RenderOptions(bool Transparent = false, IReadOnlySet<string>? Only = null, string? After = null, bool Proxies = false);

// The single drawing path shared by previews, contact sheets, the CLI and the exporter.
public sealed class FrameRenderer : IDisposable
{
    public static readonly SKSamplingOptions ImageSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly AssetCache _assets = new();
    private readonly MatteCache _mattes = new();
    private readonly ConcurrentDictionary<string, byte> _warnings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RedactionCover> _covers = new(StringComparer.Ordinal);

    // ffmpeg resolves the decoder lazily, so projects without video never need FFmpeg to render.
    // streamVideo is for the exporter: frames are requested in order and each clip keeps one decoder.
    public FrameRenderer(string projectDirectory, Func<string>? ffmpeg = null, bool streamVideo = false)
    {
        ProjectDirectory = Path.GetFullPath(projectDirectory);
        Text = new TextLayoutEngine(new FontResolver());
        Video = new VideoFrameSource(ffmpeg, streamVideo);
    }

    public string ProjectDirectory { get; }

    public TextLayoutEngine Text { get; }

    public VideoFrameSource Video { get; }

    // Set by the exporter to measure where the time goes; null measures nothing.
    public RenderStats? Stats { get; set; }

    // The previews shown so far (render_frame, render_contact_sheet).
    public PreviewCache Previews { get; } = new();

    public static SKImageInfo FrameInfo(Project project) =>
        new(project.Width, project.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

    public IReadOnlyList<string> TakeWarnings()
    {
        var warnings = _warnings.Keys.Order(StringComparer.Ordinal).ToList();
        _warnings.Clear();
        return warnings;
    }

    public void Render(Project project, int frame, SKCanvas canvas, RenderOptions? options = null)
    {
        var stats = Stats;
        var time = FrameMath.TimeOf(project, frame);
        canvas.Clear(options?.Transparent == true ? SKColors.Transparent : SkiaConvert.ParseColor(project.BackgroundColor));
        var reached = options?.After is null;
        var scene = SceneEvaluator.Evaluate(project, time, cutouts: true);
        stats?.Lap(RenderStage.Scene);
        foreach (var evaluated in scene)
        {
            if (!reached)
            {
                reached = evaluated.Cutout is null && evaluated.Layer.Id == options!.After;
                continue;
            }

            if (!evaluated.Shown || evaluated.Opacity <= 0 || options?.Only is { } only && !only.Contains(evaluated.Id))
            {
                continue;
            }

            stats?.Lap(RenderStage.Compositing);
            var started = stats is null ? 0 : RenderStats.Now;
            var saved = canvas.Save();
            var opacity = evaluated.Opacity;
            var layer = evaluated.Layer;
            var cutout = evaluated.Cutout;
            using var filter = LayerEffects.Filter(layer, time, LayerEffects.ScreenScale(evaluated), cutout is not null);
            using var effectPaint = filter is not null || layer.Mask is not null ? new SKPaint() : null;
            var matrix = evaluated.World.ToSkia();
            if (effectPaint is not null)
            {
                effectPaint.Color = SKColors.White.WithAlpha(SkiaConvert.Alpha(opacity));
                effectPaint.ImageFilter = filter;

                // Bounded to the content, the filters work on the layer's area instead of the whole canvas.
                if (ContentBounds(project, layer, time) is { } content)
                {
                    var bounds = matrix.MapRect(content);
                    bounds.Inflate(1, 1);
                    canvas.SaveLayer(bounds, effectPaint);
                }
                else
                {
                    canvas.SaveLayer(effectPaint);
                }

                opacity = 1;
            }

            var effects = effectPaint is null ? RenderStage.Compositing : RenderStage.Effects;
            stats?.Lap(effects);
            canvas.Concat(in matrix);
            canvas.Save();
            switch (layer)
            {
                case ImageLayer image:
                    DrawImage(project, image, time, opacity, canvas, cutout);
                    stats?.Lap(RenderStage.Compositing);
                    break;
                case TextLayer text:
                    DrawText(project, text, opacity, canvas);
                    stats?.Lap(RenderStage.Text);
                    break;
                case VideoLayer video:
                    DrawVideo(project, video, time, opacity, canvas, cutout, options?.Proxies == true);
                    stats?.Lap(RenderStage.Compositing);
                    break;
            }

            canvas.Restore();
            if (layer.Mask is { } mask)
            {
                LayerEffects.DrawMask(canvas, mask, time, MaskImage(project, layer, mask));
            }

            canvas.RestoreToCount(saved);
            if (stats is not null)
            {
                stats.Lap(effects);
                stats.Layer(evaluated.Id, cutout is null ? layer.TypeName : cutout.TypeName, started);
            }
        }

        stats?.Lap(RenderStage.Compositing);
    }

    // Everything the layer draws, in its own pixels: the image (mirrored with flipX), the video crop,
    // or the text's glyphs with their stroke and box.
    private SKRect? ContentBounds(Project project, VisualLayer layer, double time)
    {
        switch (layer)
        {
            case ImageLayer image:
                var drawing = CurrentDrawing(project, image, time);
                if (drawing is null)
                {
                    return SKRect.Empty;
                }

                if (!image.FlipX)
                {
                    return new SKRect(0, 0, drawing.Width, drawing.Height);
                }

                var axis = (float)(2 * image.AnchorX);
                return new SKRect(axis - drawing.Width, 0, axis, drawing.Height);
            case VideoLayer video:
                var asset = project.FindAsset(video.Asset);
                if (asset is null)
                {
                    return null;
                }

                var crop = VideoTiming.Crop(video, asset, time);
                return new SKRect(0, 0, (float)crop.Width, (float)crop.Height);
            case TextLayer text:
                var layout = Text.Layout(project, ProjectDirectory, text);
                SKRect? bounds = null;
                foreach (var line in layout.Lines.Where(l => l.Blob is not null))
                {
                    var glyphs = line.Blob!.Bounds;
                    glyphs.Offset(line.X, line.Baseline);
                    bounds = bounds is { } sofar ? SKRect.Union(sofar, glyphs) : glyphs;
                }

                if (bounds is { } ink && text.StrokeColor is not null && text.StrokeWidth > 0)
                {
                    ink.Inflate((float)text.StrokeWidth, (float)text.StrokeWidth);
                    bounds = ink;
                }

                if (text.Box is { } box && !layout.InkBounds.IsEmpty)
                {
                    var padding = (float)box.Padding;
                    var rect = new SKRect(layout.InkBounds.Left - padding, -padding, layout.InkBounds.Right + padding, layout.BoxHeight + padding);
                    bounds = bounds is { } sofar ? SKRect.Union(sofar, rect) : rect;
                }

                return bounds ?? SKRect.Empty;
            default:
                return null;
        }
    }

    private SKImage? MaskImage(Project project, VisualLayer layer, Mask mask)
    {
        if (mask.Type != MaskType.Image || mask.Asset is null)
        {
            return null;
        }

        var asset = project.FindAsset(mask.Asset)
            ?? throw new ZeyoCutException($"layer '{layer.Id}': mask asset '{mask.Asset}' not found");
        return _assets.GetImage(ProjectStore.AssetPath(ProjectDirectory, asset));
    }

    public SKImage RenderImage(Project project, int frame, RenderOptions? options = null)
    {
        using var surface = SKSurface.Create(FrameInfo(project))
            ?? throw new ZeyoCutException($"could not create a {project.Width}x{project.Height} drawing surface");
        Render(project, frame, surface.Canvas, options);
        return surface.Snapshot();
    }

    public SKImage? CurrentDrawing(Project project, ImageLayer layer, double time) =>
        CurrentDrawingAsset(project, layer, time) is { } asset ? DrawingImage(asset) : null;

    public Asset? CurrentDrawingAsset(Project project, ImageLayer layer, double time)
    {
        if (layer.Drawings.Count == 0)
        {
            return null;
        }

        var name = Interpolator.Evaluate(layer.Drawing, time);
        if (!layer.Drawings.TryGetValue(name, out var assetId))
        {
            return null;
        }

        return project.FindAsset(assetId)
            ?? throw new ZeyoCutException($"layer '{layer.Id}': asset '{assetId}' not found");
    }

    public SKImage DrawingImage(Asset asset) => _assets.GetImage(ProjectStore.AssetPath(ProjectDirectory, asset));

    // A drawing's picture and its refined matte (null until an AI matte is computed).
    public (SKImage Picture, SKImage Alpha)? MattedDrawing(Asset asset, Matte matte) =>
        _mattes.Image(ProjectDirectory, asset, DrawingImage(asset), matte);

    // proxy reads the clip's proxy where it has one, at most at the proxy's size.
    public SKImage VideoFrame(Project project, VideoLayer layer, double time, bool proxy = false)
    {
        var asset = project.FindAsset(layer.Asset)
            ?? throw new ZeyoCutException($"layer '{layer.Id}': asset '{layer.Asset}' not found");
        var frame = VideoTiming.FrameAt(asset, VideoTiming.SourceTime(layer, time));
        var (width, height) = VideoDecoder.DecodeSize(asset, project);
        var file = ProjectStore.AssetPath(ProjectDirectory, asset);
        if (proxy && Proxies.Ready(ProjectDirectory, asset) is { } proxyFile)
        {
            file = proxyFile;
            var (proxyWidth, proxyHeight) = Proxies.Size(asset);
            if (proxyWidth < width)
            {
                (width, height) = (proxyWidth, proxyHeight);
            }
        }

        return Video.GetFrame(layer.Id, file, asset, frame, width, height);
    }

    // A track's fail-safe boxes with a redaction's padding and hold, which ends at the scene cuts
    // analyze_video found. A track's file never changes (a new version is a new file), so they are
    // worked out once for each version of the footage's analysis.
    public RedactionCover Cover(Track track, Asset asset, RedactionLayer layer)
    {
        var analysis = new FileInfo(AnalysisFiles.AnalysisPath(ProjectDirectory, asset));
        var cuts = analysis.Exists ? $"{analysis.Length}|{analysis.LastWriteTimeUtc.Ticks}" : "none";
        return _covers.GetOrAdd(
            $"{track.File}|{asset.FrameCount}|{layer.PaddingPercent}|{layer.HoldFrames}|{cuts}",
            _ => RedactionCover.Of(TrackFiles.Load(ProjectDirectory, track).Frames, asset.FrameCount ?? 0, layer.PaddingPercent, layer.HoldFrames, RedactionCover.Cuts(ProjectDirectory, asset)));
    }

    public void Dispose()
    {
        _assets.Dispose();
        _mattes.Dispose();
        Video.Dispose();
        Previews.Dispose();
    }

    // A cutout draws the clip with its own matte, and nothing while that matte isn't computed. Both
    // draw the frame with apply_redaction's boxes hidden.
    private void DrawVideo(Project project, VideoLayer layer, double time, double opacity, SKCanvas canvas, CutoutLayer? cutout, bool proxy)
    {
        var asset = project.FindAsset(layer.Asset)
            ?? throw new ZeyoCutException($"layer '{layer.Id}': asset '{layer.Asset}' not found");
        var decoded = VideoFrame(project, layer, time, proxy);
        Stats?.Lap(RenderStage.Decode);
        using var redacted = Redacted(project, asset, VideoTiming.FrameAt(asset, VideoTiming.SourceTime(layer, time)), decoded);
        Stats?.Lap(RenderStage.Redaction);
        var image = redacted ?? decoded;
        var crop = VideoTiming.Crop(layer, asset, time);
        var toDecodedX = image.Width / (double)(asset.Width ?? image.Width);
        var toDecodedY = image.Height / (double)(asset.Height ?? image.Height);
        var source = new SKRect(
            (float)(crop.X * toDecodedX),
            (float)(crop.Y * toDecodedY),
            (float)((crop.X + crop.Width) * toDecodedX),
            (float)((crop.Y + crop.Height) * toDecodedY));
        var destination = new SKRect(0, 0, (float)crop.Width, (float)crop.Height);

        using var filter = ColorAdjust.Create(layer, time);
        using var paint = new SKPaint();
        paint.Color = SKColors.White.WithAlpha(SkiaConvert.Alpha(opacity));
        paint.IsAntialias = true;
        paint.ColorFilter = filter;
        var matte = cutout is null ? layer.Matte : cutout.Matte;
        if (matte is { Method: MatteMethod.AiVideo } ai)
        {
            using var matteFrame = VideoMatteFrame(layer, asset, VideoTiming.FrameAt(asset, VideoTiming.SourceTime(layer, time)), ai);
            if (matteFrame is not null)
            {
                var toMatteX = matteFrame.Width / (double)(asset.Width ?? image.Width);
                var toMatteY = matteFrame.Height / (double)(asset.Height ?? image.Height);
                var alphaSource = new SKRect(
                    (float)(crop.X * toMatteX),
                    (float)(crop.Y * toMatteY),
                    (float)((crop.X + crop.Width) * toMatteX),
                    (float)((crop.Y + crop.Height) * toMatteY));
                Mattes.Draw(canvas, image, source, matteFrame, alphaSource, destination, paint);
                Stats?.Lap(RenderStage.Matte);
                return;
            }

            NotComputed(layer.Id, ai, asset, cutout);
            if (cutout is not null)
            {
                return;
            }
        }

        if (matte is { Method: MatteMethod.Chroma } chroma)
        {
            // The key works on the decoded frame, so the edge sizes go from source to decoded pixels.
            var (picture, alpha) = ChromaKey.Apply(image, chroma);
            using (picture)
            using (alpha)
            {
                var refined = chroma.Shrink != 0 || chroma.Feather != 0 ? Mattes.Refine(alpha, chroma.Shrink * toDecodedX, chroma.Feather * toDecodedX) : null;
                using (refined)
                {
                    Mattes.Draw(canvas, picture, source, refined ?? alpha, source, destination, paint);
                }
            }

            Stats?.Lap(RenderStage.Matte);
            return;
        }

        if (cutout is null)
        {
            canvas.DrawImage(image, source, destination, ImageSampling, paint);
        }
    }

    // The decoded frame with apply_redaction's boxes hidden, or null when nothing on it is hidden.
    private SKImage? Redacted(Project project, Asset asset, int frame, SKImage decoded)
    {
        if (!project.Layers.Any(l => l is RedactionLayer { Visible: true }))
        {
            return null;
        }

        var areas = RedactionEditor.Areas(project, asset, frame, (track, layer) => Cover(track, asset, layer));
        return Redactor.Apply(decoded, areas, decoded.Width / (double)(asset.Width ?? decoded.Width), decoded.Height / (double)(asset.Height ?? decoded.Height));
    }

    private void DrawImage(Project project, ImageLayer layer, double time, double opacity, SKCanvas canvas, CutoutLayer? cutout)
    {
        var asset = CurrentDrawingAsset(project, layer, time);
        if (asset is null)
        {
            return;
        }

        var image = DrawingImage(asset);
        Stats?.Lap(RenderStage.Decode);

        if (layer.FlipX)
        {
            canvas.Scale(-1, 1, (float)layer.AnchorX, 0);
        }

        using var filter = ColorAdjust.Create(layer, time);
        using var paint = new SKPaint();
        paint.Color = SKColors.White.WithAlpha(SkiaConvert.Alpha(opacity));
        paint.IsAntialias = true;
        paint.ColorFilter = filter;
        if ((cutout is null ? layer.Matte : cutout.Matte) is { } matte)
        {
            if (MattedDrawing(asset, matte) is { } matted)
            {
                var rect = new SKRect(0, 0, image.Width, image.Height);
                Mattes.Draw(canvas, matted.Picture, rect, matted.Alpha, rect, rect, paint);
                Stats?.Lap(RenderStage.Matte);
                return;
            }

            NotComputed(layer.Id, matte, asset, cutout);
        }

        if (cutout is null)
        {
            canvas.DrawImage(image, 0, 0, ImageSampling, paint);
        }
    }

    private void NotComputed(string layerId, Matte matte, Asset asset, CutoutLayer? cutout) =>
        _warnings.TryAdd(
            cutout is null
                ? $"layer '{layerId}': the {EnumNames.ToName(matte.Method)} matte of '{asset.Id}' isn't computed yet, so it is drawn with its background; get_job_status shows its matte job, or run remove_background again"
                : $"cutout '{cutout.Id}': the {EnumNames.ToName(matte.Method)} matte of '{asset.Id}' isn't computed yet, so the subject of '{layerId}' isn't drawn over the text yet; get_job_status shows its matte job, or run {(cutout.CaptionGroup is null ? "text_behind_subject" : "generate_captions")} again",
            0);

    // One frame of a computed aiVideo matte, refined, or null until it is computed. The matte is read
    // like a clip of its own (one reader per layer), at the size it was computed at.
    private SKImage? VideoMatteFrame(VideoLayer layer, Asset asset, int frame, Matte matte)
    {
        var path = MatteFiles.CachePath(ProjectDirectory, asset, matte);
        if (!File.Exists(path))
        {
            return null;
        }

        var (width, height) = MatteFiles.VideoMatteSize(asset);
        var source = new Asset
        {
            Id = asset.Id,
            Type = AssetType.Video,
            File = path,
            Width = width,
            Height = height,
            FrameRate = asset.FrameRate,
            FrameCount = asset.FrameCount,
            Duration = asset.Duration,
        };
        var alpha = Mattes.FromGrayFrame(Video.GetFrame("matte:" + layer.Id, path, source, frame, width, height));
        if (matte.Shrink == 0 && matte.Feather == 0)
        {
            return alpha;
        }

        var scale = width / (double)(asset.Width ?? width);
        using (alpha)
        {
            return Mattes.Refine(alpha, matte.Shrink * scale, matte.Feather * scale);
        }
    }

    private void DrawText(Project project, TextLayer layer, double opacity, SKCanvas canvas)
    {
        var layout = Text.Layout(project, ProjectDirectory, layer);
        foreach (var warning in layout.Warnings)
        {
            _warnings.TryAdd($"layer '{layer.Id}': {warning}", 0);
        }

        using var groupPaint = new SKPaint();
        var group = opacity < 1;
        if (group)
        {
            groupPaint.Color = SKColors.White.WithAlpha(SkiaConvert.Alpha(opacity));
            canvas.SaveLayer(groupPaint);
        }

        if (layer.Box is { } box && !layout.InkBounds.IsEmpty)
        {
            var padding = (float)box.Padding;
            var rect = new SKRect(
                layout.InkBounds.Left - padding,
                -padding,
                layout.InkBounds.Right + padding,
                layout.BoxHeight + padding);
            using var boxPaint = new SKPaint();
            boxPaint.Color = SkiaConvert.ParseColor(box.Color);
            boxPaint.IsAntialias = true;
            boxPaint.Style = SKPaintStyle.Fill;
            var radius = (float)box.CornerRadius;
            canvas.DrawRoundRect(rect, radius, radius, boxPaint);
        }

        if (layer.StrokeColor is not null && layer.StrokeWidth > 0)
        {
            using var strokePaint = new SKPaint();
            strokePaint.Color = SkiaConvert.ParseColor(layer.StrokeColor);
            strokePaint.IsAntialias = true;
            strokePaint.Style = SKPaintStyle.Stroke;
            strokePaint.StrokeWidth = (float)(layer.StrokeWidth * 2);
            strokePaint.StrokeJoin = SKStrokeJoin.Round;
            DrawLines(canvas, layout, strokePaint);
        }

        using var fillPaint = new SKPaint();
        fillPaint.Color = SkiaConvert.ParseColor(layer.Color);
        fillPaint.IsAntialias = true;
        fillPaint.Style = SKPaintStyle.Fill;
        DrawLines(canvas, layout, fillPaint);

        if (group)
        {
            canvas.Restore();
        }
    }

    private static void DrawLines(SKCanvas canvas, TextLayout layout, SKPaint paint)
    {
        foreach (var line in layout.Lines)
        {
            if (line.Blob is not null)
            {
                canvas.DrawText(line.Blob, line.X, line.Baseline, paint);
            }
        }
    }
}
