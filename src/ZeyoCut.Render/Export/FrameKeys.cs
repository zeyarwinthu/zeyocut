// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Render.Export;

// A hash of everything a frame's pixels depend on: the canvas, and for each layer drawn on it its
// settings with every animated value as it is at that frame, where it lands, how opaque it is, the
// files it reads (stamped with size and time) and, for a clip, the source frame. Keyframes at other
// times don't change it, so an edit only changes the keys of the frames it changes. Redactions (with
// the stamp of their footage's scene cuts) and cutouts are taken whole, keyframes and all, which is
// safe but changes more keys than needed.
public sealed class FrameKeys
{
    // Rebuilding ZeyoCut changes every key, so frames from older code are never reused.
    private static readonly string Code = string.Join("|",
        typeof(FrameRenderer).Assembly.ManifestModule.ModuleVersionId,
        typeof(SceneEvaluator).Assembly.ManifestModule.ModuleVersionId);

    private static readonly JsonSerializerOptions Plain = new(ProjectJson.Options) { WriteIndented = false };

    private static readonly JsonSerializerOptions AtTime = CreateAtTime();

    [ThreadStatic]
    private static double _time;

    private readonly Project _project;
    private readonly string _directory;
    private readonly string _canvas;
    private readonly Dictionary<string, string> _layers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _redactions;

    public FrameKeys(Project project, string projectDirectory)
    {
        _project = project;
        _directory = projectDirectory;
        _canvas = $"{Code}|{project.Width}x{project.Height}|{project.Fps}|{project.BackgroundColor}";
        var redactions = project.Layers.OfType<RedactionLayer>().Where(r => r.Visible).ToList();
        var tracks = redactions.SelectMany(r => r.Tracks).ToHashSet(StringComparer.Ordinal);
        _redactions = redactions.Count == 0
            ? ""
            : string.Join("|", redactions.Select(r => JsonSerializer.Serialize<Layer>(r, Plain))
                .Concat(project.Tracks.Where(t => tracks.Contains(t.Id)).Select(t => JsonSerializer.Serialize(t, Plain)))
                .Concat(project.Tracks.Where(t => tracks.Contains(t.Id)).Select(t => t.Asset).Distinct(StringComparer.Ordinal)
                    .Select(id => "cuts " + (project.FindAsset(id) is { } asset ? FileStamp(AnalysisFiles.AnalysisPath(projectDirectory, asset)) : "none"))));
    }

    public string Of(int frame) => Of(frame, null);

    // only: the key of the frame with just these layers drawn (RenderOptions.Only).
    public string Of(int frame, IReadOnlySet<string>? only)
    {
        var time = FrameMath.TimeOf(_project, frame);
        var builder = new StringBuilder(_canvas);
        foreach (var evaluated in SceneEvaluator.Evaluate(_project, time, cutouts: true))
        {
            if (!evaluated.Shown || evaluated.Opacity <= 0 || only is not null && !only.Contains(evaluated.Id))
            {
                continue;
            }

            var layer = evaluated.Layer;
            var world = evaluated.World;
            builder.Append('\n').Append(evaluated.Id).Append('|')
                .Append(Num(world.A)).Append(',').Append(Num(world.B)).Append(',').Append(Num(world.C)).Append(',')
                .Append(Num(world.D)).Append(',').Append(Num(world.E)).Append(',').Append(Num(world.F)).Append('|')
                .Append(Num(evaluated.Opacity)).Append('|')
                .Append(LayerAt(layer, time));
            if (evaluated.Cutout is { } cutout)
            {
                builder.Append('|').Append(JsonSerializer.Serialize<Layer>(cutout, Plain));
            }

            foreach (var assetId in AssetIds(layer, time))
            {
                builder.Append('|').Append(AssetStamp(assetId));
            }

            if ((evaluated.Cutout?.Matte ?? Matte(layer)) is { } matte && MatteAsset(layer, time) is { } matted)
            {
                builder.Append("|matte ").Append(FileStamp(MatteFiles.CachePath(_directory, matted, matte)));
            }

            if (layer is VideoLayer video && _project.FindAsset(video.Asset) is { } source)
            {
                builder.Append("|source frame ").Append(VideoTiming.FrameAt(source, VideoTiming.SourceTime(video, time))).Append('|').Append(_redactions);
            }
        }

        return Hash(builder.ToString());
    }

    // One key for a run of frames, with the settings they are encoded with.
    public static string Combine(string settings, IEnumerable<string> frameKeys) =>
        Hash(settings + "\n" + string.Join("\n", frameKeys));

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    // The layer's settings as JSON, with its animated values as they are at the time. A layer without
    // keyframes is the same at every time, so it is written once.
    private string LayerAt(VisualLayer layer, double time)
    {
        if (!_layers.TryGetValue(layer.Id, out var plain))
        {
            plain = JsonSerializer.Serialize<Layer>(layer, Plain);
            _layers[layer.Id] = plain;
        }

        if (!plain.Contains("\"keyframes\"", StringComparison.Ordinal))
        {
            return plain;
        }

        _time = time;
        return JsonSerializer.Serialize<Layer>(layer, AtTime);
    }

    private static Matte? Matte(VisualLayer layer) => layer switch
    {
        ImageLayer image => image.Matte,
        VideoLayer video => video.Matte,
        _ => null,
    };

    // The asset a matte is made from: the clip, or the drawing shown at the time.
    private Asset? MatteAsset(VisualLayer layer, double time) => layer switch
    {
        VideoLayer video => _project.FindAsset(video.Asset),
        ImageLayer image when image.Drawings.TryGetValue(Interpolator.Evaluate(image.Drawing, time), out var id) => _project.FindAsset(id),
        _ => null,
    };

    private static IEnumerable<string> AssetIds(VisualLayer layer, double time)
    {
        switch (layer)
        {
            case ImageLayer image when image.Drawings.TryGetValue(Interpolator.Evaluate(image.Drawing, time), out var drawing):
                yield return drawing;
                break;
            case VideoLayer video:
                yield return video.Asset;
                break;
            case TextLayer { FontAsset: { } font }:
                yield return font;
                break;
        }

        if (layer.Mask?.Asset is { } mask)
        {
            yield return mask;
        }
    }

    private string AssetStamp(string id)
    {
        if (_files.TryGetValue("asset:" + id, out var stamp))
        {
            return stamp;
        }

        stamp = _project.FindAsset(id) is { } asset
            ? JsonSerializer.Serialize(asset, Plain) + FileStamp(ProjectStore.AssetPath(_directory, asset))
            : "missing asset " + id;
        _files["asset:" + id] = stamp;
        return stamp;
    }

    private string FileStamp(string path)
    {
        if (!_files.TryGetValue(path, out var stamp))
        {
            var info = new FileInfo(path);
            stamp = info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "missing";
            _files[path] = stamp;
        }

        return stamp;
    }

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateAtTime()
    {
        var options = new JsonSerializerOptions(ProjectJson.Options) { WriteIndented = false };
        options.Converters.Add(new ValueAtTime());
        options.Converters.Add(new DrawingAtTime());
        return options;
    }

    private sealed class ValueAtTime : JsonConverter<AnimatedValue>
    {
        public override AnimatedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, AnimatedValue value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(Interpolator.Evaluate(value, _time));
    }

    private sealed class DrawingAtTime : JsonConverter<DrawingTrack>
    {
        public override DrawingTrack Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DrawingTrack value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Interpolator.Evaluate(value, _time));
    }
}
