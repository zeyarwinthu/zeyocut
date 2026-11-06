// SPDX-License-Identifier: MIT

using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Serialization;

public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project Deserialize(string json)
    {
        Project? project;
        try
        {
            project = JsonSerializer.Deserialize<Project>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ZeyoCutException($"invalid project JSON: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ZeyoCutException($"invalid project JSON (every layer needs a \"type\" of image, text, audio or video): {ex.Message}", ex);
        }

        if (project is null)
        {
            throw new ZeyoCutException("invalid project JSON: the document is empty");
        }

        Normalize(project);
        return project;
    }

    public static Project Clone(Project project) => Deserialize(Serialize(project));

    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    public static JsonObject LayerToNode(Layer layer) =>
        JsonSerializer.SerializeToNode(layer, Options)!.AsObject();

    public static Layer LayerFromNode(JsonObject node)
    {
        Layer? layer;
        try
        {
            layer = node.Deserialize<Layer>(Options);
        }
        catch (JsonException ex)
        {
            throw new ZeyoCutException($"invalid layer value: {ex.Message}", ex);
        }

        if (layer is null)
        {
            throw new ZeyoCutException("invalid layer value");
        }

        NormalizeLayer(layer);
        return layer;
    }

    public static string LayerToJson(Layer layer) => JsonSerializer.Serialize(layer, Options);

    public static void Normalize(Project project)
    {
        project.FormatVersion = Math.Max(project.FormatVersion, Project.CurrentFormatVersion);
        project.Name ??= "";
        project.BackgroundColor ??= "#FFFFFF";
        project.Audio ??= new AudioSettings();
        project.Assets ??= [];
        project.Layers ??= [];
        project.Tracks ??= [];
        project.Markers ??= [];
        project.Assets.RemoveAll(a => a is null);
        project.Layers.RemoveAll(l => l is null);
        project.Tracks.RemoveAll(t => t is null);
        foreach (var track in project.Tracks)
        {
            track.Id ??= "";
            track.Asset ??= "";
            track.Layer ??= "";
            track.File ??= "";
            track.Anchors ??= [];
            track.Anchors.RemoveAll(a => a is null);
            track.Anchors = track.Anchors.OrderBy(a => a.Frame).ToList();
        }

        project.Markers.RemoveAll(m => m is null);
        foreach (var marker in project.Markers)
        {
            marker.Id ??= "";
            marker.Name ??= "";
        }

        project.Markers = project.Markers.OrderBy(m => m.Time).ToList();

        if (project.Camera is { } camera)
        {
            camera.X = NormalizeValue(camera.X, project.Width / 2.0);
            camera.Y = NormalizeValue(camera.Y, project.Height / 2.0);
            camera.Zoom = NormalizeValue(camera.Zoom, 1);
            camera.Rotation = NormalizeValue(camera.Rotation, 0);
            camera.ShakeX = NormalizeValue(camera.ShakeX, 0);
            camera.ShakeY = NormalizeValue(camera.ShakeY, 0);
            camera.ShakeRotation = NormalizeValue(camera.ShakeRotation, 0);
        }

        foreach (var layer in project.Layers)
        {
            NormalizeLayer(layer);
        }
    }

    private static void NormalizeLayer(Layer layer)
    {
        layer.Id ??= "";
        layer.Name ??= "";
        if (layer is VisualLayer visual)
        {
            visual.X = NormalizeValue(visual.X, 0);
            visual.Y = NormalizeValue(visual.Y, 0);
            visual.Rotation = NormalizeValue(visual.Rotation, 0);
            visual.ScaleX = NormalizeValue(visual.ScaleX, 1);
            visual.ScaleY = NormalizeValue(visual.ScaleY, 1);
            visual.Opacity = NormalizeValue(visual.Opacity, 1);
            NormalizeEffects(visual);
        }

        if (layer is IColorAdjustable color)
        {
            color.Brightness = NormalizeValue(color.Brightness, 0);
            color.Contrast = NormalizeValue(color.Contrast, 1);
            color.Saturation = NormalizeValue(color.Saturation, 1);
        }

        switch (layer)
        {
            case ImageLayer image:
                image.Drawings ??= new Dictionary<string, string>(StringComparer.Ordinal);
                image.Drawing ??= new DrawingTrack();
                image.Drawing.Value ??= "";
                image.Drawing.Keyframes ??= [];
                image.Drawing.Keyframes.RemoveAll(k => k is null);
                image.Drawing.Keyframes = image.Drawing.Keyframes.OrderBy(k => k.Time).ToList();
                break;
            case TextLayer text:
                text.Text ??= "";
                text.FontFamily ??= "";
                text.Color ??= "#000000";
                break;
            case AudioLayer audio:
                audio.Asset ??= "";
                break;
            case VideoLayer video:
                video.Asset ??= "";
                video.End = null;
                video.CropX = NormalizeValue(video.CropX, 0);
                video.CropY = NormalizeValue(video.CropY, 0);
                break;
            case CutoutLayer cutout:
                cutout.Sources ??= [];
                cutout.Sources.RemoveAll(s => s is null);
                cutout.Opacity = NormalizeValue(cutout.Opacity, 1);
                break;
            case RedactionLayer redaction:
                redaction.Tracks ??= [];
                redaction.Tracks.RemoveAll(t => t is null);
                redaction.Color ??= "#000000";
                if (redaction.Boxes is { } boxes)
                {
                    foreach (var key in boxes.Where(b => b.Value is null).Select(b => b.Key).ToList())
                    {
                        boxes.Remove(key);
                    }

                    foreach (var box in boxes.Values)
                    {
                        box.X = NormalizeValue(box.X, 0);
                        box.Y = NormalizeValue(box.Y, 0);
                        box.Width = NormalizeValue(box.Width, 0);
                        box.Height = NormalizeValue(box.Height, 0);
                    }
                }

                break;
        }
    }

    private static void NormalizeEffects(VisualLayer layer)
    {
        if (layer.Shadow is { } shadow)
        {
            shadow.Color ??= "#000000";
            shadow.X = NormalizeValue(shadow.X, 8);
            shadow.Y = NormalizeValue(shadow.Y, 8);
            shadow.Blur = NormalizeValue(shadow.Blur, 6);
            shadow.Opacity = NormalizeValue(shadow.Opacity, 0.5);
        }

        if (layer.Outline is { } outline)
        {
            outline.Color ??= "#FFFFFF";
            outline.Width = NormalizeValue(outline.Width, 4);
        }

        if (layer.Blur is { } blur)
        {
            blur.X = NormalizeValue(blur.X, 4);
            blur.Y = NormalizeValue(blur.Y, 4);
        }

        if (layer.Tint is { } tint)
        {
            tint.Color ??= "#FFFFFF";
            tint.Amount = NormalizeValue(tint.Amount, 0.5);
        }

        if (layer.Mask is { } mask)
        {
            mask.X = NormalizeValue(mask.X, 0);
            mask.Y = NormalizeValue(mask.Y, 0);
            mask.Width = NormalizeValue(mask.Width, 100);
            mask.Height = NormalizeValue(mask.Height, 100);
            mask.Feather = NormalizeValue(mask.Feather, 0);
        }

        if (layer.MotionPath is { } path)
        {
            path.Points ??= [];
            path.Points.RemoveAll(p => p is null);
            path.Progress = NormalizeValue(path.Progress, 0);
        }
    }

    private static AnimatedValue NormalizeValue(AnimatedValue? value, double fallback)
    {
        value ??= new AnimatedValue(fallback);
        value.Keyframes ??= [];
        value.Keyframes.RemoveAll(k => k is null);
        value.Keyframes = value.Keyframes.OrderBy(k => k.Time).ToList();
        return value;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            AllowOutOfOrderMetadataProperties = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitEmptyKeyframes, OmitVideoEnd, OmitNoTracks } },
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly();
        return options;
    }

    private static void OmitEmptyKeyframes(JsonTypeInfo info)
    {
        if (info.Type != typeof(AnimatedValue) && info.Type != typeof(DrawingTrack))
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            if (property.Name == "keyframes")
            {
                property.ShouldSerialize = static (_, value) => value is ICollection { Count: > 0 };
            }
        }
    }

    private static void OmitNoTracks(JsonTypeInfo info)
    {
        if (info.Type != typeof(Project))
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            if (property.Name is "tracks" or "markers")
            {
                property.ShouldSerialize = static (_, value) => value is ICollection { Count: > 0 };
            }
        }
    }

    // A video clip's timeline length follows from trimIn, trimOut and speed, so it has no 'end' field.
    private static void OmitVideoEnd(JsonTypeInfo info)
    {
        if (info.Type != typeof(VideoLayer))
        {
            return;
        }

        for (var i = info.Properties.Count - 1; i >= 0; i--)
        {
            if (info.Properties[i].Name == "end")
            {
                info.Properties.RemoveAt(i);
            }
        }
    }
}
