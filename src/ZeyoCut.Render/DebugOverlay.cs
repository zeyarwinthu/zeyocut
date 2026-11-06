// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Text;

namespace ZeyoCut.Render;

public static class DebugOverlay
{
    private static readonly SKColor[] Palette =
    [
        new(0xFF, 0x3B, 0x30), new(0x34, 0xC7, 0x59), new(0x00, 0x7A, 0xFF), new(0xFF, 0x95, 0x00),
        new(0xAF, 0x52, 0xDE), new(0x00, 0xC7, 0xBE), new(0xFF, 0x2D, 0x55), new(0x8E, 0x8E, 0x00),
    ];

    private static readonly SKColor TrackColor = new(0xFF, 0xD6, 0x0A);
    private static readonly SKColor DriftColor = new(0xFF, 0x3B, 0x30);

    public static void Draw(SKCanvas canvas, FrameRenderer renderer, Project project, double time, float scale)
    {
        var scene = SceneEvaluator.Evaluate(project, time).Where(l => l.Shown).ToList();
        var byId = scene.ToDictionary(l => l.Layer.Id, StringComparer.Ordinal);

        var typeface = SKFontManager.Default.MatchFamily("Segoe UI") ?? SKTypeface.Default;
        using var font = TextLayoutEngine.CreateFont(typeface, 12, embolden: false);
        using var bone = new SKPaint();
        bone.IsAntialias = true;
        bone.Style = SKPaintStyle.Stroke;
        bone.StrokeWidth = 1.5f;
        bone.Color = new SKColor(0x40, 0x40, 0x40, 0xC0);
        bone.PathEffect = SKPathEffect.CreateDash([5, 4], 0);

        foreach (var layer in scene)
        {
            if (layer.Layer.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                canvas.DrawLine(
                    parent.World.Map(parent.Layer.AnchorX, parent.Layer.AnchorY, scale),
                    layer.World.Map(layer.Layer.AnchorX, layer.Layer.AnchorY, scale),
                    bone);
            }
        }

        var placed = new List<SKRect>();
        foreach (var layer in scene)
        {
            var color = Palette[layer.Index % Palette.Length];
            using var outline = new SKPaint();
            outline.IsAntialias = true;
            outline.Style = SKPaintStyle.Stroke;
            outline.StrokeWidth = 1.5f;
            outline.Color = color;

            if (LocalBounds(renderer, project, layer.Layer, time) is { } bounds)
            {
                using var builder = new SKPathBuilder();
                builder.MoveTo(layer.World.Map(bounds.Left, bounds.Top, scale));
                builder.LineTo(layer.World.Map(bounds.Right, bounds.Top, scale));
                builder.LineTo(layer.World.Map(bounds.Right, bounds.Bottom, scale));
                builder.LineTo(layer.World.Map(bounds.Left, bounds.Bottom, scale));
                builder.Close();
                using var path = builder.Detach();
                canvas.DrawPath(path, outline);
            }

            var anchor = layer.World.Map(layer.Layer.AnchorX, layer.Layer.AnchorY, scale);
            canvas.DrawLine(anchor.X - 7, anchor.Y, anchor.X + 7, anchor.Y, outline);
            canvas.DrawLine(anchor.X, anchor.Y - 7, anchor.X, anchor.Y + 7, outline);
            canvas.DrawCircle(anchor, 3.5f, outline);

            var label = layer.Layer.Id;
            var width = font.MeasureText(label);
            var labelRect = PlaceLabel(new SKRect(anchor.X + 6, anchor.Y - 20, anchor.X + 12 + width, anchor.Y - 5), canvas.DeviceClipBounds, placed);
            placed.Add(labelRect);
            using var background = new SKPaint();
            background.Color = new SKColor(0, 0, 0, 0xB0);
            canvas.DrawRoundRect(labelRect, 3, 3, background);
            using var text = new SKPaint();
            text.IsAntialias = true;
            text.Color = SKColors.White;
            canvas.DrawText(label, labelRect.Left + 3, labelRect.Bottom - 3.5f, SKTextAlign.Left, font, text);
            using var swatch = new SKPaint();
            swatch.Color = color;
            canvas.DrawRect(new SKRect(labelRect.Left, labelRect.Bottom - 2, labelRect.Right, labelRect.Bottom), swatch);
        }

        foreach (var layer in scene)
        {
            if (layer.Layer is VideoLayer video)
            {
                DrawTracks(canvas, renderer, project, video, layer, time, scale, font, placed);
            }
        }
    }

    // The box of each track of a clip's footage at the frame it shows: dashed, with the track's id and
    // confidence; red where it is possible drift.
    private static void DrawTracks(SKCanvas canvas, FrameRenderer renderer, Project project, VideoLayer video, EvaluatedLayer layer, double time, float scale, SKFont font, List<SKRect> placed)
    {
        if (project.Tracks.Count == 0 || project.FindAsset(video.Asset) is not { FrameCount: > 0 } asset)
        {
            return;
        }

        var frame = VideoTiming.FrameAt(asset, VideoTiming.SourceTime(video, time));
        var crop = VideoTiming.Crop(video, asset, time);
        foreach (var track in project.Tracks.Where(t => t.Asset == video.Asset && frame >= t.FirstFrame && frame <= t.LastFrame))
        {
            TrackedFrame? box;
            try
            {
                box = TrackFiles.Load(renderer.ProjectDirectory, track).Frames.Find(f => f.Frame == frame);
            }
            catch (ZeyoCutException)
            {
                continue;
            }

            if (box is null)
            {
                continue;
            }

            var color = box.Confidence < TrackEditor.DriftBelow ? DriftColor : TrackColor;
            using var stroke = new SKPaint();
            stroke.IsAntialias = true;
            stroke.Style = SKPaintStyle.Stroke;
            stroke.StrokeWidth = 2;
            stroke.Color = color;
            stroke.PathEffect = SKPathEffect.CreateDash([6, 4], 0);
            var (left, top) = (box.X - crop.X, box.Y - crop.Y);
            using var builder = new SKPathBuilder();
            builder.MoveTo(layer.World.Map(left, top, scale));
            builder.LineTo(layer.World.Map(left + box.Width, top, scale));
            builder.LineTo(layer.World.Map(left + box.Width, top + box.Height, scale));
            builder.LineTo(layer.World.Map(left, top + box.Height, scale));
            builder.Close();
            using var path = builder.Detach();
            canvas.DrawPath(path, stroke);

            var label = $"{track.Id} {box.Confidence:0.00}{(box.Lost ? " lost" : "")}";
            var corner = layer.World.Map(left, top, scale);
            var labelRect = PlaceLabel(new SKRect(corner.X, corner.Y - 17, corner.X + 6 + font.MeasureText(label), corner.Y - 2), canvas.DeviceClipBounds, placed);
            placed.Add(labelRect);
            using var background = new SKPaint();
            background.Color = color;
            canvas.DrawRoundRect(labelRect, 3, 3, background);
            using var text = new SKPaint();
            text.IsAntialias = true;
            text.Color = SKColors.Black;
            canvas.DrawText(label, labelRect.Left + 3, labelRect.Bottom - 3.5f, SKTextAlign.Left, font, text);
        }
    }

    private static SKRect PlaceLabel(SKRect rect, SKRectI bounds, List<SKRect> placed)
    {
        rect.Offset(Math.Max(0, bounds.Left - rect.Left) - Math.Max(0, rect.Right - bounds.Right), Math.Max(0, bounds.Top - rect.Top));
        for (var attempt = 0; attempt < 20 && placed.Any(p => p.IntersectsWith(rect)); attempt++)
        {
            rect.Offset(0, rect.Height + 2);
        }

        return rect;
    }

    private static SKRect? LocalBounds(FrameRenderer renderer, Project project, VisualLayer layer, double time)
    {
        switch (layer)
        {
            case ImageLayer image:
                var drawing = renderer.CurrentDrawing(project, image, time);
                if (drawing is null)
                {
                    return null;
                }

                if (image.FlipX)
                {
                    var flippedLeft = (float)(2 * image.AnchorX - drawing.Width);
                    return new SKRect(flippedLeft, 0, flippedLeft + drawing.Width, drawing.Height);
                }

                return new SKRect(0, 0, drawing.Width, drawing.Height);
            case TextLayer text:
                var layout = renderer.Text.Layout(project, renderer.ProjectDirectory, text);
                return new SKRect(0, 0, layout.BoxWidth, layout.BoxHeight);
            case VideoLayer video when project.FindAsset(video.Asset) is { } asset:
                var (width, height) = VideoTiming.CropSize(video, asset);
                return new SKRect(0, 0, (float)width, (float)height);
            default:
                return null;
        }
    }
}
