// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public static class LayerTree
{
    public static string Format(Project project)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"{project.Name}: {project.Width}x{project.Height} @ {project.Fps} fps, {Num(project.Duration)}s, {project.Layers.Count} layer(s), {project.Assets.Count} asset(s)");
        if (project.Camera is not null)
        {
            builder.AppendLine(CameraEditor.Describe(project));
        }

        if (project.Markers.Count > 0)
        {
            builder.AppendLine("markers: " + string.Join("; ", project.Markers.Select(MarkerEditor.Describe)));
        }

        if (Languages.Used(project) is { Count: > 0 } languages)
        {
            builder.AppendLine("languages: " + string.Join(", ", languages));
        }

        if (project.Layers.Count == 0)
        {
            builder.AppendLine("(no layers)");
            return builder.ToString();
        }

        var ids = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in project.Layers.Where(l => l.ParentId is null || !ids.Contains(l.ParentId)))
        {
            Write(builder, project, root, "", "", visited);
        }

        foreach (var orphan in project.Layers.Where(l => !visited.Contains(l.Id)))
        {
            builder.AppendLine($"{Describe(orphan)}  (in a parent cycle)");
        }

        var shown = project.Layers.OfType<VideoLayer>().SelectMany(v => TrackEditor.TracksOf(project, v)).ToHashSet(StringComparer.Ordinal);
        var unshown = project.Tracks.Where(t => !shown.Contains(t.Id)).Select(t => $"{t.Id} ({t.Asset} frames {t.FirstFrame}–{t.LastFrame})").ToList();
        if (unshown.Count > 0)
        {
            builder.AppendLine($"tracks no clip shows: {string.Join(", ", unshown)}");
        }

        return builder.ToString();
    }

    public static string Describe(Layer layer)
    {
        var parts = new List<string> { $"{layer.Id} [{layer.TypeName}]" };
        if (layer.Name != layer.Id)
        {
            parts.Add($"\"{layer.Name}\"");
        }

        if (layer is CutoutLayer cutout)
        {
            parts.Add($"z={cutout.ZIndex}");
            parts.Add($"follows={string.Join("/", cutout.Sources)}");
            if (cutout.CaptionGroup is { } group)
            {
                parts.Add($"for={group}");
            }
            if (!layer.Visible)
            {
                parts.Add("hidden");
            }

            if (cutout.Opacity.Keyframes.Count == 0 && cutout.Opacity.Value != 1)
            {
                parts.Add($"opacity={Num(cutout.Opacity.Value)}");
            }

            if (cutout.Matte is { } matte)
            {
                parts.Add("effects: " + MatteEditor.Short(matte));
            }

            if (cutout.Opacity.Keyframes.Count > 0)
            {
                parts.Add($"keys: opacity({cutout.Opacity.Keyframes.Count})");
            }

            return string.Join("  ", parts);
        }

        if (layer is RedactionLayer redaction)
        {
            parts.Add($"hides={string.Join("/", redaction.Tracks)}");
            parts.Add(RedactionEditor.Describe(redaction));
            if (!layer.Visible)
            {
                parts.Add("hidden");
            }

            if (redaction.Boxes is { Count: > 0 } boxes)
            {
                parts.Add("keys: " + string.Join(" ", boxes.SelectMany(b => new[] { ("x", b.Value.X), ("y", b.Value.Y), ("width", b.Value.Width), ("height", b.Value.Height) }
                    .Select(p => $"{b.Key}.{p.Item1}({p.Item2.Keyframes.Count})"))));
            }

            return string.Join("  ", parts);
        }

        if (layer is VisualLayer visual)
        {
            parts.Add($"z={visual.ZIndex}");
        }

        parts.Add($"t={Num(layer.Start)}–{(layer is VideoLayer clip ? Num(clip.TimelineEnd) : layer.End is { } end ? Num(end) : "end")}");
        if (!layer.Visible)
        {
            parts.Add("hidden");
        }

        if (layer.Language is { } language)
        {
            parts.Add(layer is VideoLayer ? $"lang={language} (sound)" : $"lang={language}");
        }

        switch (layer)
        {
            case ImageLayer image:
                parts.Add(image.Drawings.Count == 0 ? "null (no drawings)" : $"drawings={string.Join("/", image.Drawings.Keys)}");
                if (image.FlipX)
                {
                    parts.Add("flipX");
                }

                break;
            case TextLayer text:
                var shown = text.Text.Replace("\r", "").Replace("\n", "⏎");
                parts.Add($"text=\"{(shown.Length > 40 ? shown[..40] + "…" : shown)}\"");
                parts.Add($"font={(text.FontAsset ?? text.FontFamily)} {Num(text.FontSize)}px");
                break;
            case AudioLayer audio:
                parts.Add($"asset={audio.Asset} volume={Num(audio.Volume)}");
                break;
            case VideoLayer video:
                parts.Add($"asset={video.Asset} source={Num(video.TrimIn)}–{Num(video.TrimOut)}s speed={Num(video.Speed)}");
                parts.Add(video.Muted ? "muted" : $"volume={Num(video.Volume)}");
                if (video.CropWidth is { } cropWidth && video.CropHeight is { } cropHeight)
                {
                    parts.Add($"crop={Num(cropWidth)}x{Num(cropHeight)}");
                }

                break;
        }

        if (layer is VisualLayer v)
        {
            parts.Add($"anchor=({Num(v.AnchorX)},{Num(v.AnchorY)})");
            if (v.IgnoreCamera)
            {
                parts.Add("ignoreCamera");
            }

            if (EffectEditor.Effects(v) is { Count: > 0 } effects)
            {
                parts.Add("effects: " + string.Join(" ", effects));
            }

            var animated = AnimatableProperties.Numeric(v)
                .Where(p => p.Value.Keyframes.Count > 0)
                .Select(p => $"{p.Name}({p.Value.Keyframes.Count})")
                .ToList();
            if (v is ImageLayer { Drawing.Keyframes.Count: > 0 } img)
            {
                animated.Add($"drawing({img.Drawing.Keyframes.Count})");
            }

            if (animated.Count > 0)
            {
                parts.Add("keys: " + string.Join(" ", animated));
            }
        }

        return string.Join("  ", parts);
    }

    private static void Write(StringBuilder builder, Project project, Layer layer, string prefix, string childPrefix, HashSet<string> visited)
    {
        var tracks = layer is VideoLayer video ? TrackEditor.TracksOf(project, video) : [];
        builder.Append(prefix).AppendLine(Describe(layer) + (tracks.Count > 0 ? $"  tracks={string.Join("/", tracks)}" : ""));
        if (!visited.Add(layer.Id))
        {
            return;
        }

        var children = project.Layers.Where(l => l.ParentId == layer.Id).ToList();
        for (var i = 0; i < children.Count; i++)
        {
            var last = i == children.Count - 1;
            Write(builder, project, children[i], childPrefix + (last ? "└─ " : "├─ "), childPrefix + (last ? "   " : "│  "), visited);
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
