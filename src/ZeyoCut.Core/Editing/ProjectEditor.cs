// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public sealed record ProjectUpdate(IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings);

public static class ProjectEditor
{
    public static string LayerIds(Project project) => Ids.List(project.Layers.Select(l => l.Id));

    public static ProjectUpdate UpdateProject(Project project, ProjectOptions options)
    {
        if (options.Name is null && options.Width is null && options.Height is null && options.Fps is null
            && options.Duration is null && options.BackgroundColor is null
            && options.Preset is null && options.LoudnessTarget is null && options.Ducking is null)
        {
            throw new ZeyoCutException("nothing to change; pass at least one of name, width, height, fps, duration, backgroundColor, preset, loudnessTarget, ducking");
        }

        if (options.Preset is { } presetName)
        {
            var preset = Presets.Get(presetName);
            options = options with
            {
                Width = options.Width ?? preset.Width,
                Height = options.Height ?? preset.Height,
                Fps = options.Fps ?? preset.Fps,
                LoudnessTarget = options.LoudnessTarget ?? preset.Loudness,
            };
        }

        if (options.LoudnessTarget is { } target && target != 0
            && (!double.IsFinite(target) || target < AudioSettings.MinLoudness || target > AudioSettings.MaxLoudness))
        {
            throw new ZeyoCutException($"loudnessTarget must be between {AudioSettings.MinLoudness} and {AudioSettings.MaxLoudness} LUFS (YouTube and most platforms: -14), or 0 to turn it off; got {Num(target)}");
        }

        var wasVertical = project.Height > project.Width;

        if (options.Name is not null && string.IsNullOrWhiteSpace(options.Name))
        {
            throw new ZeyoCutException("name can't be empty");
        }

        var (oldWidth, oldHeight, oldDuration) = (project.Width, project.Height, project.Duration);
        var changes = new List<string>();
        if (options.Name is { } name && name.Trim() != project.Name)
        {
            changes.Add($"name '{project.Name}' → '{name.Trim()}'");
            project.Name = name.Trim();
        }

        if (options.Width is { } width && width != project.Width)
        {
            changes.Add($"width {project.Width} → {width}");
            project.Width = width;
        }

        if (options.Height is { } height && height != project.Height)
        {
            changes.Add($"height {project.Height} → {height}");
            project.Height = height;
        }

        if (CameraEditor.Recenter(project, oldWidth, oldHeight) is { } recentered)
        {
            changes.Add(recentered);
        }

        if (options.Fps is { } fps && fps != project.Fps)
        {
            changes.Add($"fps {project.Fps} → {fps}");
            project.Fps = fps;
        }

        if (options.Duration is { } duration && duration != project.Duration)
        {
            changes.Add($"duration {Num(project.Duration)}s → {Num(duration)}s");
            project.Duration = duration;
        }

        if (options.BackgroundColor is { } color && !string.Equals(color.Trim(), project.BackgroundColor, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"backgroundColor {project.BackgroundColor} → {color.Trim()}");
            project.BackgroundColor = color.Trim();
        }

        if (options.LoudnessTarget is { } loudness)
        {
            double? newTarget = loudness == 0 ? null : loudness;
            if (newTarget != project.Audio.LoudnessTarget)
            {
                changes.Add($"loudness {Loudness(project.Audio.LoudnessTarget)} → {Loudness(newTarget)}");
                project.Audio.LoudnessTarget = newTarget;
            }
        }

        if (options.Ducking is { } ducking && ducking != project.Audio.Ducking)
        {
            changes.Add($"music ducking {(ducking ? "on" : "off")}");
            project.Audio.Ducking = ducking;
        }

        if (changes.Count == 0)
        {
            throw new ZeyoCutException(
                $"nothing changed; the project already is {project.Width}x{project.Height} @ {project.Fps} fps, {Num(project.Duration)}s, background {project.BackgroundColor}, loudness {Loudness(project.Audio.LoudnessTarget)}, ducking {(project.Audio.Ducking ? "on" : "off")}");
        }

        var warnings = new List<string>();
        if ((project.Width != oldWidth || project.Height != oldHeight) && project.Layers.Any(l => l is VisualLayer))
        {
            warnings.Add($"the canvas changed from {oldWidth}x{oldHeight} to {project.Width}x{project.Height}, but layer positions are in pixels and were not moved; check the layout with render_frame");
            if (project.Height > project.Width != wasVertical && project.Layers.Any(l => l is VideoLayer))
            {
                warnings.Add("the orientation changed; reframe_to_vertical crops and fits video layers to the new canvas");
            }
        }

        if (project.Duration != oldDuration)
        {
            DurationWarnings(project, oldDuration, warnings);
        }

        return new ProjectUpdate(changes, warnings);
    }

    private static void DurationWarnings(Project project, double oldDuration, List<string> warnings)
    {
        var end = project.Duration;
        foreach (var layer in project.Layers.Where(l => l.Start >= end))
        {
            warnings.Add($"layer '{layer.Id}' starts at {Num(layer.Start)}s, at or after the new end ({Num(end)}s), so it won't be {(layer is AudioLayer ? "heard" : "seen")}");
        }

        var late = new List<string>();
        foreach (var layer in project.Layers.OfType<VisualLayer>())
        {
            foreach (var (property, value) in AnimatableProperties.Numeric(layer))
            {
                var count = value.Keyframes.Count(k => k.Time > end);
                if (count > 0)
                {
                    late.Add($"{layer.Id}.{property} ({count})");
                }
            }

            if (layer is ImageLayer image && image.Drawing.Keyframes.Count(k => k.Time > end) is > 0 and var drawings)
            {
                late.Add($"{layer.Id}.drawing ({drawings})");
            }
        }

        if (late.Count > 0)
        {
            warnings.Add($"keyframes after the new end ({Num(end)}s) won't be reached: {string.Join(", ", late)}");
        }

        var markers = project.Markers.Where(m => m.Time > end + 1e-6).Select(m => $"{m.Id} ({Num(m.Time)}s)").ToList();
        if (markers.Count > 0)
        {
            warnings.Add($"markers after the new end ({Num(end)}s) are past the video: {string.Join(", ", markers)}; remove_markers removes them");
        }

        foreach (var audio in project.Layers.OfType<AudioLayer>().Where(a => a.Visible && a.Start < end))
        {
            if (project.FindAsset(audio.Asset)?.Duration is not { } sourceLength)
            {
                continue;
            }

            var soundEnd = audio.Start + (audio.TrimOut ?? sourceLength) - audio.TrimIn;
            if (audio.End is { } layerEnd)
            {
                soundEnd = Math.Min(soundEnd, layerEnd);
            }

            if (soundEnd > end + 1e-6)
            {
                warnings.Add($"audio '{audio.Id}' runs until {Num(soundEnd)}s and will be cut at {Num(end)}s");
            }
        }

        foreach (var video in project.Layers.OfType<VideoLayer>().Where(v => v.Start < end && v.TimelineEnd > end + 1e-6))
        {
            warnings.Add($"video '{video.Id}' runs until {Num(video.TimelineEnd)}s and will be cut at {Num(end)}s");
        }

        if (end > oldDuration)
        {
            var stuck = project.Layers.Where(l => l.End is { } e && Math.Abs(e - oldDuration) < 1e-6).Select(l => l.Id).ToList();
            if (stuck.Count > 0)
            {
                warnings.Add($"these layers end at the old project end ({Num(oldDuration)}s) and won't run to the new end: {string.Join(", ", stuck)}; set \"end\": null with update_layer to extend them");
            }
        }
    }

    public static string Loudness(double? target) => target is { } lufs ? $"{Num(lufs)} LUFS" : "off";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static Layer GetLayer(Project project, string? id, string role = "layer")
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ZeyoCutException($"{role} id is required; existing layers: {LayerIds(project)}");
        }

        return project.FindLayer(id)
            ?? throw new ZeyoCutException($"{role} '{id}' not found; existing layers: {LayerIds(project)}");
    }

    public static VisualLayer GetVisualLayer(Project project, string? id, string role = "layer")
    {
        var layer = GetLayer(project, id, role);
        return layer as VisualLayer
            ?? throw (layer is CutoutLayer cutout
                ? SubjectEditor.Follows(cutout)
                : layer is RedactionLayer redaction
                    ? RedactionEditor.Follows(redaction)
                    : new ZeyoCutException($"{role} '{id}' is an audio layer; only image, text and video layers can be used here"));
    }

    public static Asset GetAsset(Project project, string? id, AssetType type)
    {
        var name = EnumNames.ToName(type);
        var typed = Ids.List(project.Assets.Where(a => a.Type == type).Select(a => a.Id));
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ZeyoCutException($"an {name} asset id is required; {name} assets: {typed}");
        }

        var asset = project.FindAsset(id)
            ?? throw new ZeyoCutException($"asset '{id}' not found; {name} assets: {typed}. Import files with {(type == AssetType.Video ? "import_video" : "import_asset")} first");
        if (asset.Type != type)
        {
            throw new ZeyoCutException($"asset '{id}' is a {EnumNames.ToName(asset.Type)} asset, not {name}; {name} assets: {typed}");
        }

        return asset;
    }

    public static string NewLayerId(Project project, string? id, string? name, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            Ids.EnsureValid(id, "layer");
            if (project.FindLayer(id) is not null)
            {
                throw new ZeyoCutException($"layer id '{id}' already exists; choose another id or omit it to generate one");
            }

            return id;
        }

        var baseId = Ids.Slug(name, Ids.Slug(fallback, "layer"));
        return Ids.Unique(baseId, candidate => candidate == Camera.Id || project.FindLayer(candidate) is not null);
    }

    public static ImageLayer AddImageLayer(Project project, ImageLayerOptions options)
    {
        var drawings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, assetId) in options.Drawings ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ZeyoCutException("drawing names can't be empty");
            }

            drawings[name] = GetAsset(project, assetId, AssetType.Image).Id;
        }

        var drawing = "";
        if (!string.IsNullOrEmpty(options.Drawing))
        {
            if (!drawings.ContainsKey(options.Drawing))
            {
                throw new ZeyoCutException($"drawing '{options.Drawing}' not found; drawings: {Ids.List(drawings.Keys)}");
            }

            drawing = options.Drawing;
        }
        else if (drawings.Count > 0)
        {
            drawing = drawings.Keys.First();
        }

        var sizeAsset = drawing.Length > 0 ? project.FindAsset(drawings[drawing]) : null;
        var layer = new ImageLayer
        {
            Drawings = drawings,
            FlipX = options.FlipX ?? false,
        };
        layer.Drawing.Value = drawing;

        var fallbackId = drawings.Count > 0 ? drawings.Values.First() : "null";
        ApplyVisual(project, layer, options, (sizeAsset?.Width ?? 0) / 2.0, (sizeAsset?.Height ?? 0) / 2.0, fallbackId);
        project.Layers.Add(layer);
        return layer;
    }

    public static TextLayer AddTextLayer(Project project, TextLayerOptions options, ITextMeasurer? measurer = null)
    {
        if (string.IsNullOrEmpty(options.Text))
        {
            throw new ZeyoCutException("text is required");
        }

        var layer = new TextLayer
        {
            Text = options.Text,
            FontFamily = string.IsNullOrWhiteSpace(options.FontFamily) ? "Segoe UI" : options.FontFamily.Trim(),
            FontAsset = string.IsNullOrWhiteSpace(options.FontAsset) ? null : GetAsset(project, options.FontAsset, AssetType.Font).Id,
            Bold = options.Bold ?? false,
            FontSize = options.FontSize ?? 48,
            Color = options.Color ?? "#000000",
            StrokeColor = string.IsNullOrWhiteSpace(options.StrokeColor) ? null : options.StrokeColor,
            StrokeWidth = options.StrokeWidth ?? (string.IsNullOrWhiteSpace(options.StrokeColor) ? 0 : 4),
            Align = options.Align is null ? TextAlign.Left : EnumNames.Parse<TextAlign>(options.Align, "align"),
            MaxWidth = options.MaxWidth,
            LineHeight = options.LineHeight ?? 1.0,
            Box = options.Box,
        };

        double anchorX = 0;
        double anchorY = 0;
        if (measurer is not null && (options.AnchorX is null || options.AnchorY is null))
        {
            var (width, height) = measurer.Measure(layer);
            anchorX = width / 2;
            anchorY = height / 2;
        }

        var idSource = options.Text.Length > 24 ? options.Text[..24] : options.Text;
        ApplyVisual(project, layer, options, anchorX, anchorY, Ids.Slug(idSource, "text"));
        project.Layers.Add(layer);
        return layer;
    }

    public static AudioLayer AddAudioLayer(Project project, AudioLayerOptions options)
    {
        var asset = GetAsset(project, options.Asset, AssetType.Audio);
        var layer = new AudioLayer
        {
            Id = NewLayerId(project, options.Id, options.Name, asset.Id),
            Asset = asset.Id,
            Start = options.Start ?? 0,
            End = options.End,
            TrimIn = options.TrimIn ?? 0,
            TrimOut = options.TrimOut,
            Volume = options.Volume ?? 1,
            Role = options.Role is null ? AudioRole.Music : EnumNames.Parse<AudioRole>(options.Role, "role"),
            FadeIn = options.FadeIn ?? 0,
            FadeOut = options.FadeOut ?? 0,
            Language = Tag(options.Language),
        };
        layer.Name = string.IsNullOrWhiteSpace(options.Name) ? layer.Id : options.Name.Trim();
        project.Layers.Add(layer);
        return layer;
    }

    public static VideoLayer AddVideoLayer(Project project, VideoLayerOptions options)
    {
        var asset = GetAsset(project, options.Asset, AssetType.Video);
        var duration = asset.Duration ?? throw new ZeyoCutException($"video asset '{asset.Id}' has no duration; import it again with import_video");
        var trimIn = options.TrimIn ?? 0;
        var trimOut = options.TrimOut ?? duration;
        if (!double.IsFinite(trimIn) || trimIn < 0 || trimIn >= duration)
        {
            throw new ZeyoCutException($"trimIn must be between 0 and the source length ({Num(duration)}s); got {Num(trimIn)}");
        }

        if (!double.IsFinite(trimOut) || trimOut <= trimIn || trimOut > duration + 0.001)
        {
            throw new ZeyoCutException($"trimOut must be after trimIn ({Num(trimIn)}s) and at most the source length ({Num(duration)}s); got {Num(trimOut)}");
        }

        var speed = options.Speed ?? 1;
        if (!double.IsFinite(speed) || speed < VideoLayer.MinSpeed || speed > VideoLayer.MaxSpeed)
        {
            throw new ZeyoCutException($"speed must be between {Num(VideoLayer.MinSpeed)} and {Num(VideoLayer.MaxSpeed)}; got {Num(speed)}");
        }

        var layer = new VideoLayer
        {
            Asset = asset.Id,
            TrimIn = trimIn,
            TrimOut = Math.Min(trimOut, duration),
            Speed = speed,
            Volume = options.Volume ?? 1,
            Muted = options.Muted ?? false,
            Role = options.Role is null ? AudioRole.Voice : EnumNames.Parse<AudioRole>(options.Role, "role"),
            FadeIn = options.FadeIn ?? 0,
            FadeOut = options.FadeOut ?? 0,
            CropWidth = options.CropWidth,
            CropHeight = options.CropHeight,
        };

        var (width, height) = VideoTiming.CropSize(layer, asset);
        var fit = width > 0 && height > 0 ? Math.Min(project.Width / width, project.Height / height) : 1;
        var visual = options with { ScaleX = options.ScaleX ?? fit, ScaleY = options.ScaleY ?? fit, End = null };
        ApplyVisual(project, layer, visual, width / 2, height / 2, asset.Id);
        layer.End = null;
        project.Layers.Add(layer);
        ExtendToFit(project, layer.TimelineEnd);
        return layer;
    }

    // Grows the project so a clip that runs past the end is not cut off. Returns true when it grew.
    public static bool ExtendToFit(Project project, double end)
    {
        var rounded = Math.Ceiling(end * 1e6 - 1e-3) / 1e6;
        if (rounded <= project.Duration + 1e-9)
        {
            return false;
        }

        project.Duration = rounded;
        return true;
    }

    public static IReadOnlyList<string> UpdateLayer(Project project, string layerId, JsonObject changes)
    {
        var layer = GetLayer(project, layerId);
        if (changes.Count == 0)
        {
            throw new ZeyoCutException("changes is empty; pass the fields to change, e.g. {\"x\": 100, \"parentId\": \"tora_body\"}");
        }

        if (Languages.Untaggable(layer) is { } untaggable && changes.Any(c => string.Equals(c.Key, "language", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ZeyoCutException($"'{layer.Id}': {untaggable}");
        }

        var node = ProjectJson.LayerToNode(layer);
        var fields = node.Select(p => p.Key).Where(k => k is not "type").ToList();
        if (layer is CutoutLayer cutout)
        {
            if (changes.Select(c => c.Key).FirstOrDefault(k => !SubjectEditor.OwnFields.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } other
                && !string.Equals(other, "id", StringComparison.OrdinalIgnoreCase) && !string.Equals(other, "type", StringComparison.OrdinalIgnoreCase))
            {
                throw SubjectEditor.Follows(cutout);
            }

            fields = ["id", .. SubjectEditor.OwnFields];
        }

        if (layer is RedactionLayer redaction)
        {
            if (changes.Select(c => c.Key).FirstOrDefault(k => !RedactionEditor.OwnFields.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } other
                && !string.Equals(other, "id", StringComparison.OrdinalIgnoreCase) && !string.Equals(other, "type", StringComparison.OrdinalIgnoreCase))
            {
                throw RedactionEditor.Follows(redaction);
            }

            fields = ["id", .. RedactionEditor.OwnFields];
        }

        if (layer is VisualLayer && !fields.Contains("rigRole"))
        {
            fields.Insert(fields.IndexOf("name") + 1, "rigRole");
        }

        if (layer is VisualLayer && !fields.Contains("ignoreCamera"))
        {
            fields.Insert(fields.IndexOf("anchorY") + 1, "ignoreCamera");
        }

        if (Languages.Untaggable(layer) is null && !fields.Contains("language"))
        {
            fields.Insert(fields.IndexOf("visible") + 1, "language");
        }

        var warnings = new List<string>();
        var movesPosition = false;

        foreach (var (rawKey, value) in changes)
        {
            if (string.Equals(rawKey, "type", StringComparison.OrdinalIgnoreCase))
            {
                throw new ZeyoCutException("a layer's type can't be changed; add a new layer instead");
            }

            var key = fields.FirstOrDefault(f => f == rawKey)
                ?? fields.FirstOrDefault(f => string.Equals(f, rawKey, StringComparison.OrdinalIgnoreCase));
            if (key == "id")
            {
                throw new ZeyoCutException("a layer's id can't be changed; duplicate_layer can copy it under a new id");
            }

            if (key is null)
            {
                throw new ZeyoCutException(
                    $"unknown field '{rawKey}' for {layer.TypeName} layer '{layerId}'; fields: {string.Join(", ", fields.Where(f => f != "id"))}");
            }

            movesPosition |= key is "x" or "y";
            var existing = node[key];
            if (key is "parentId" or "rigRole" or "language" && value is JsonValue parentValue && parentValue.TryGetValue<string>(out var parentText) && parentText.Length == 0)
            {
                node[key] = null;
            }
            else if (key is "language" && value is JsonValue languageValue && languageValue.TryGetValue<string>(out var code))
            {
                node[key] = Languages.Normalize(code);
            }
            else if (existing is JsonObject animated && animated.ContainsKey("value") && value is JsonValue)
            {
                animated["value"] = value.DeepClone();
                if (animated["keyframes"] is JsonArray { Count: > 0 } keys)
                {
                    warnings.Add($"{key} has {keys.Count} keyframe(s), so the static value you set is only used after clear_keyframes");
                }
            }
            else if (existing is JsonObject target && value is JsonObject patch)
            {
                foreach (var (patchKey, patchValue) in patch)
                {
                    if (patchValue is null)
                    {
                        target.Remove(patchKey);
                    }
                    else
                    {
                        target[patchKey] = patchValue.DeepClone();
                    }
                }
            }
            else
            {
                node[key] = value?.DeepClone();
            }
        }

        if (movesPosition && layer is VisualLayer { MotionPath: not null } moved && node.ContainsKey("motionPath") && !changes.Any(c => string.Equals(c.Key, "motionPath", StringComparison.OrdinalIgnoreCase)))
        {
            node.Remove("motionPath");
            warnings.Add(EffectEditor.PathRemovedWarning(moved));
        }

        var updated = ProjectJson.LayerFromNode(node);
        project.Layers[project.Layers.IndexOf(layer)] = updated;
        return warnings;
    }

    public static IReadOnlyList<string> RemoveLayer(Project project, string layerId, bool withChildren)
    {
        GetLayer(project, layerId);
        var subtree = Subtree(project, layerId);
        if (subtree.Count > 1 && !withChildren)
        {
            throw new ZeyoCutException(
                $"layer '{layerId}' has child layers: {Ids.List(subtree.Skip(1).Select(l => l.Id))}. Pass withChildren=true to remove them too, or re-parent them first with update_layer");
        }

        var ids = subtree.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        project.Layers.RemoveAll(l => ids.Contains(l.Id));
        return [.. subtree.Select(l => l.Id), .. SubjectEditor.Follow(project)];
    }

    public static IReadOnlyDictionary<string, string> DuplicateLayer(Project project, DuplicateOptions options)
    {
        var root = SubjectEditor.GetEditableLayer(project, options.LayerId);
        var sources = options.IncludeChildren ? Subtree(project, root.Id) : [root];

        var newParent = root.ParentId;
        if (options.NewParentId is not null)
        {
            newParent = options.NewParentId.Length == 0 ? null : GetVisualLayer(project, options.NewParentId, "parent").Id;
        }

        if (newParent is not null && root is AudioLayer)
        {
            throw new ZeyoCutException("audio layers can't have a parent");
        }

        var replace = !string.IsNullOrEmpty(options.ReplaceFrom);
        var from = options.ReplaceFrom ?? "";
        var to = options.ReplaceTo ?? "";
        string Replaced(string text) => replace ? text.Replace(from, to, StringComparison.Ordinal) : text;

        var taken = new HashSet<string>(project.Layers.Select(l => l.Id), StringComparer.Ordinal);
        var needSuffix = sources.Where(s => Replaced(s.Id) == s.Id).ToList();
        var suffix = 2;
        while (needSuffix.Any(s => taken.Contains($"{s.Id}_{suffix}")))
        {
            suffix++;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var newId = Replaced(source.Id);
            if (newId == source.Id)
            {
                newId = $"{source.Id}_{suffix}";
            }

            Ids.EnsureValid(newId, "layer");
            if (!taken.Add(newId))
            {
                throw new ZeyoCutException($"can't copy '{source.Id}' as '{newId}': that id already exists");
            }

            map[source.Id] = newId;
        }

        foreach (var source in sources)
        {
            var copy = ProjectJson.LayerFromNode(ProjectJson.LayerToNode(source));
            copy.Id = map[source.Id];
            copy.Name = Replaced(source.Name);
            copy.ParentId = ReferenceEquals(source, root)
                ? newParent
                : source.ParentId is { } parentId && map.TryGetValue(parentId, out var mapped) ? mapped : source.ParentId;
            copy.ZIndex = source.ZIndex + options.ZIndexOffset;
            project.Layers.Add(copy);
        }

        return map;
    }

    public static List<Layer> Subtree(Project project, string rootId)
    {
        var included = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var layer in project.Layers)
            {
                if (layer.ParentId is { } parentId && included.Contains(parentId) && included.Add(layer.Id))
                {
                    added = true;
                }
            }
        }

        var root = project.FindLayer(rootId)!;
        var result = new List<Layer> { root };
        result.AddRange(project.Layers.Where(l => !ReferenceEquals(l, root) && included.Contains(l.Id)));
        return result;
    }

    private static void ApplyVisual(
        Project project,
        VisualLayer layer,
        VisualLayerOptions options,
        double defaultAnchorX,
        double defaultAnchorY,
        string fallbackId)
    {
        layer.Id = NewLayerId(project, options.Id, options.Name, fallbackId);
        layer.Name = string.IsNullOrWhiteSpace(options.Name) ? layer.Id : options.Name.Trim();

        var parent = string.IsNullOrWhiteSpace(options.ParentId) ? null : GetVisualLayer(project, options.ParentId, "parent");
        layer.ParentId = parent?.Id;
        layer.AnchorX = options.AnchorX ?? defaultAnchorX;
        layer.AnchorY = options.AnchorY ?? defaultAnchorY;
        layer.X = new AnimatedValue(options.X ?? parent?.AnchorX ?? project.Width / 2.0);
        layer.Y = new AnimatedValue(options.Y ?? parent?.AnchorY ?? project.Height / 2.0);
        layer.Rotation = new AnimatedValue(options.Rotation ?? 0);
        layer.ScaleX = new AnimatedValue(options.ScaleX ?? 1);
        layer.ScaleY = new AnimatedValue(options.ScaleY ?? 1);
        layer.Opacity = new AnimatedValue(options.Opacity ?? 1);
        layer.ZIndex = options.ZIndex ?? NextZIndex(project);
        layer.Start = options.Start ?? 0;
        layer.End = options.End;
        layer.Visible = options.Visible ?? true;
        layer.Language = Tag(options.Language);
    }

    private static string? Tag(string? language) => string.IsNullOrWhiteSpace(language) ? null : Languages.Normalize(language);

    private static int NextZIndex(Project project)
    {
        var visual = project.Layers.Where(l => l is VisualLayer or CutoutLayer).ToList();
        return visual.Count == 0 ? 0 : visual.Max(l => l.ZIndex) + 1;
    }
}
