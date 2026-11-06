// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public static class ProjectValidator
{
    public static void ThrowIfInvalid(Project project)
    {
        var errors = Validate(project);
        if (errors.Count == 1)
        {
            throw new ZeyoCutException(errors[0]);
        }

        if (errors.Count > 1)
        {
            throw new ZeyoCutException("the project is invalid:\n- " + string.Join("\n- ", errors));
        }
    }

    public static List<string> Validate(Project project)
    {
        var errors = new List<string>();
        ValidateProject(project, errors);

        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var asset in project.Assets)
        {
            if (!Ids.IsValid(asset.Id))
            {
                errors.Add($"asset id '{asset.Id}' is invalid");
            }
            else if (!assets.TryAdd(asset.Id, asset))
            {
                errors.Add($"duplicate asset id '{asset.Id}'");
            }

            if (string.IsNullOrWhiteSpace(asset.File))
            {
                errors.Add($"asset '{asset.Id}' has no file");
            }
            else if (!ProjectStore.IsAssetFile(asset.File))
            {
                errors.Add($"asset '{asset.Id}': file '{asset.File}' must be a file in the project's {ProjectStore.AssetsFolder} folder");
            }

            if (asset.Type == AssetType.Video)
            {
                ValidateVideoAsset(asset, errors);
            }
        }

        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        foreach (var layer in project.Layers)
        {
            if (!Ids.IsValid(layer.Id))
            {
                errors.Add($"layer id '{layer.Id}' is invalid: use letters, digits, '_' or '-'");
            }
            else if (layer.Id == Camera.Id)
            {
                errors.Add($"layer id '{Camera.Id}' is reserved for the project camera; choose another id");
            }
            else if (!layers.TryAdd(layer.Id, layer))
            {
                errors.Add($"duplicate layer id '{layer.Id}'");
            }
        }

        foreach (var layer in project.Layers)
        {
            ValidateLayer(project, layer, layers, assets, errors);
        }

        ValidateCycles(project, layers, errors);
        if (project.Camera is { } camera)
        {
            ValidateCamera(camera, errors);
        }

        ValidateTracks(project, assets, errors);
        ValidateMarkers(project, errors);
        return errors;
    }

    private static void ValidateMarkers(Project project, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in project.Markers)
        {
            if (!Ids.IsValid(marker.Id))
            {
                errors.Add($"marker id '{marker.Id}' is invalid: use letters, digits, '_' or '-'");
            }
            else if (!ids.Add(marker.Id))
            {
                errors.Add($"duplicate marker id '{marker.Id}'");
            }

            if (!double.IsFinite(marker.Time) || marker.Time < 0)
            {
                errors.Add($"marker '{marker.Id}': time must be 0 or more seconds");
            }

            if (string.IsNullOrWhiteSpace(marker.Name))
            {
                errors.Add($"marker '{marker.Id}' has no name");
            }
            else if (marker.Name.Any(char.IsControl))
            {
                errors.Add($"marker '{marker.Id}': the name must be one line of text");
            }

            if (!Enum.IsDefined(marker.Kind))
            {
                errors.Add($"marker '{marker.Id}': kind must be chapter or scene");
            }
        }
    }

    private static void ValidateTracks(Project project, Dictionary<string, Asset> assets, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in project.Tracks)
        {
            if (!Ids.IsValid(track.Id))
            {
                errors.Add($"track id '{track.Id}' is invalid: use letters, digits, '_' or '-'");
            }
            else if (!ids.Add(track.Id))
            {
                errors.Add($"duplicate track id '{track.Id}'");
            }

            var prefix = $"track '{track.Id}'";
            if (!assets.TryGetValue(track.Asset, out var asset))
            {
                errors.Add($"{prefix}: asset '{track.Asset}' not found");
            }
            else if (asset.Type != AssetType.Video)
            {
                errors.Add($"{prefix}: asset '{track.Asset}' is {Article(asset.Type)}, not a video");
            }

            if (!ProjectStore.IsFileIn(TrackFiles.Folder, track.File))
            {
                errors.Add($"{prefix}: file '{track.File}' must be a file in the project's {TrackFiles.Folder} folder");
            }

            if (track.FirstFrame < 0 || track.LastFrame < track.FirstFrame || track.Origin < track.FirstFrame || track.Origin > track.LastFrame)
            {
                errors.Add($"{prefix}: frames {track.FirstFrame}–{track.LastFrame} must run forward from 0 and include its origin frame {track.Origin}");
            }

            foreach (var anchor in track.Anchors)
            {
                if (anchor.Frame < track.FirstFrame || anchor.Frame > track.LastFrame
                    || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || !(anchor.Width > 0) || !(anchor.Height > 0) || !double.IsFinite(anchor.Width) || !double.IsFinite(anchor.Height))
                {
                    errors.Add($"{prefix}: the anchor at frame {anchor.Frame} must be inside the track with a finite box above 0 px");
                }
            }
        }
    }

    private static void ValidateCamera(Camera camera, List<string> errors)
    {
        foreach (var (name, value) in AnimatableProperties.Numeric(camera))
        {
            if (!double.IsFinite(value.Value) || value.Keyframes.Any(k => !double.IsFinite(k.Value)))
            {
                errors.Add($"camera.{name}: values must be finite numbers");
            }

            ValidateTimes(value.Keyframes.Select(k => k.Time).ToList(), $"camera.{name}", errors);
        }

        var zooms = camera.Zoom.Keyframes.Select(k => k.Value).Prepend(camera.Zoom.Value);
        if (zooms.Any(z => z < Camera.MinZoom || z > Camera.MaxZoom))
        {
            errors.Add($"camera.zoom must be between {Camera.MinZoom} and {Camera.MaxZoom} (1 = the whole canvas, 2 = twice as close)");
        }
    }

    private static void ValidateProject(Project project, List<string> errors)
    {
        if (project.FormatVersion > Project.CurrentFormatVersion)
        {
            errors.Add($"formatVersion {project.FormatVersion} is newer than this version of ZeyoCut supports ({Project.CurrentFormatVersion})");
        }

        if (project.Width < 16 || project.Width > 8192 || project.Width % 2 != 0)
        {
            errors.Add($"width must be an even number between 16 and 8192 (got {project.Width}); H.264 yuv420p needs even sizes");
        }

        if (project.Height < 16 || project.Height > 8192 || project.Height % 2 != 0)
        {
            errors.Add($"height must be an even number between 16 and 8192 (got {project.Height}); H.264 yuv420p needs even sizes");
        }

        if (project.Fps < 1 || project.Fps > 120)
        {
            errors.Add($"fps must be between 1 and 120 (got {project.Fps})");
        }

        if (!double.IsFinite(project.Duration) || project.Duration <= 0 || project.Duration > 7200)
        {
            errors.Add($"duration must be greater than 0 and at most 7200 seconds (got {project.Duration})");
        }

        if (!RgbaColor.TryParse(project.BackgroundColor, out _))
        {
            errors.Add($"backgroundColor '{project.BackgroundColor}' is not a color; use #RRGGBB or #RRGGBBAA");
        }

        if (project.Audio.LoudnessTarget is { } loudness && (!double.IsFinite(loudness) || loudness < AudioSettings.MinLoudness || loudness > AudioSettings.MaxLoudness))
        {
            errors.Add($"audio.loudnessTarget must be between {AudioSettings.MinLoudness} and {AudioSettings.MaxLoudness} LUFS, or null for off (got {loudness})");
        }

        if (!double.IsFinite(project.Audio.TruePeak) || project.Audio.TruePeak < -9 || project.Audio.TruePeak > 0)
        {
            errors.Add($"audio.truePeak must be between -9 and 0 dBTP (got {project.Audio.TruePeak})");
        }
    }

    private static void ValidateLayer(
        Project project,
        Layer layer,
        Dictionary<string, Layer> layers,
        Dictionary<string, Asset> assets,
        List<string> errors)
    {
        var prefix = $"layer '{layer.Id}'";

        if (!double.IsFinite(layer.Start) || layer.Start < 0)
        {
            errors.Add($"{prefix}: start must be >= 0 (got {layer.Start})");
        }

        if (layer.End is { } end && (!double.IsFinite(end) || end <= layer.Start))
        {
            errors.Add($"{prefix}: end ({end}) must be greater than start ({layer.Start})");
        }

        if (layer.RigRole is { } role && (!Ids.IsValid(role) || role == Rigs.RootRole))
        {
            errors.Add($"{prefix}: rigRole '{role}' is invalid: use letters, digits, '_' or '-' (and not '{Rigs.RootRole}', which always means the rig's root)");
        }

        if (layer.Language is { } language)
        {
            if (Languages.Untaggable(layer) is { } untaggable)
            {
                errors.Add($"{prefix}: {untaggable}");
            }
            else if (!Languages.TryNormalize(language, out var normalized))
            {
                errors.Add($"{prefix}: language '{language}' isn't a language code; use one such as en, ja, my or pt-BR");
            }
            else if (normalized != language)
            {
                errors.Add($"{prefix}: write language '{language}' as {normalized}");
            }
            else if (TaggedAncestor(layer, layers) is { } tagged && tagged.Language != language)
            {
                errors.Add($"{prefix}: its language {language} differs from {tagged.Language} on '{tagged.Id}', which it is part of; a child is only in its parent's version, so tag one of them or give it another parent");
            }
        }

        if (layer is VisualLayer { IgnoreCamera: true } && layer.ParentId is { } cameraParent)
        {
            errors.Add($"{prefix}: ignoreCamera only works on top-level layers, and children follow their parent; set it on '{cameraParent}' or on the top of its hierarchy instead");
        }

        if (layer.ParentId is { } parentId)
        {
            if (layer is AudioLayer)
            {
                errors.Add($"{prefix}: audio layers can't have a parent");
            }
            else if (layer is CutoutLayer)
            {
                errors.Add($"{prefix}: a cutout has no parent; it follows its sources");
            }
            else if (layer is RedactionLayer)
            {
                errors.Add($"{prefix}: a redaction has no parent; it works on the clips that play its tracks' footage");
            }
            else if (parentId == layer.Id)
            {
                errors.Add($"{prefix}: a layer can't be its own parent");
            }
            else if (!layers.TryGetValue(parentId, out var parent))
            {
                errors.Add($"{prefix}: parent '{parentId}' not found; existing layers: {Ids.List(layers.Keys)}");
            }
            else if (parent is AudioLayer)
            {
                errors.Add($"{prefix}: parent '{parentId}' is an audio layer; only image, text and video layers can be parents");
            }
            else if (parent is CutoutLayer)
            {
                errors.Add($"{prefix}: parent '{parentId}' is a subject cutout; only image, text and video layers can be parents");
            }
            else if (parent is RedactionLayer)
            {
                errors.Add($"{prefix}: parent '{parentId}' is a redaction; only image, text and video layers can be parents");
            }
        }

        if (layer is VisualLayer visual)
        {
            ValidateVisual(visual, prefix, errors);
            ValidateEffects(visual, prefix, assets, errors);
        }

        if (layer is IMattable mattable and not CutoutLayer)
        {
            MatteEditor.Validate(mattable, prefix, errors);
        }

        switch (layer)
        {
            case ImageLayer image:
                ValidateImage(image, prefix, assets, errors);
                break;
            case TextLayer text:
                ValidateText(text, prefix, assets, errors);
                break;
            case AudioLayer audio:
                ValidateAudio(audio, prefix, assets, errors);
                break;
            case VideoLayer video:
                ValidateVideo(video, prefix, assets, errors);
                break;
            case CutoutLayer cutout:
                ValidateCutout(cutout, prefix, layers, errors);
                break;
            case RedactionLayer redaction:
                ValidateRedaction(project, redaction, prefix, errors);
                break;
        }
    }

    // The nearest layer this one is part of that has a language tag; a video's tag is only its sound's.
    private static Layer? TaggedAncestor(Layer layer, Dictionary<string, Layer> layers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { layer.Id };
        var parentId = layer.ParentId;
        while (parentId is not null && layers.TryGetValue(parentId, out var parent) && seen.Add(parent.Id))
        {
            if (parent is not VideoLayer && parent.Language is not null)
            {
                return parent;
            }

            parentId = parent.ParentId;
        }

        return null;
    }

    private static void ValidateRedaction(Project project, RedactionLayer layer, string prefix, List<string> errors)
    {
        if (layer.Start != 0 || layer.End is not null)
        {
            errors.Add($"{prefix}: a redaction has no start or end of its own; it works on its tracks' frames wherever a clip plays them");
        }

        errors.AddRange(RedactionEditor.Problems(layer).Select(p => $"{prefix}: {p}"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in layer.Tracks)
        {
            if (!seen.Add(id))
            {
                errors.Add($"{prefix}: track '{id}' is listed twice");
            }
            else if (project.FindTrack(id) is null)
            {
                errors.Add($"{prefix}: track '{id}' not found; tracks: {Ids.List(project.Tracks.Select(t => t.Id))}");
            }
            else if (RedactionEditor.Of(project, id) is { } first && first != layer)
            {
                errors.Add($"{prefix}: track '{id}' is already hidden by '{first.Id}'; a track has one redaction");
            }
        }

        if (layer.Boxes is not { } boxes)
        {
            return;
        }

        foreach (var (id, box) in boxes)
        {
            if (!layer.Tracks.Contains(id))
            {
                errors.Add($"{prefix}: boxes has '{id}', which isn't one of its tracks");
            }

            foreach (var (name, value) in new[] { ("x", box.X), ("y", box.Y), ("width", box.Width), ("height", box.Height) })
            {
                var size = name is "width" or "height";
                if (value.Keyframes.Count == 0)
                {
                    errors.Add($"{prefix}: {id}.{name} has no keyframes; tracks_to_keyframes makes a box's keyframes");
                }
                else if (value.Keyframes.Any(k => !double.IsFinite(k.Time) || k.Time < 0 || !double.IsFinite(k.Value) || size && k.Value <= 0))
                {
                    errors.Add($"{prefix}: {id}.{name} keyframes need times of 0 or more and finite values{(size ? " above 0" : "")}");
                }
            }
        }
    }

    private static void ValidateCutout(CutoutLayer layer, string prefix, Dictionary<string, Layer> layers, List<string> errors)
    {
        if (layer.Start != 0 || layer.End is not null)
        {
            errors.Add($"{prefix}: a cutout has no start or end of its own; it shows while its sources do");
        }

        if (layer.Sources.Count == 0)
        {
            errors.Add($"{prefix}: a cutout needs at least one source layer");
        }

        if (layer.CaptionGroup is { } group && !(layers.TryGetValue(group, out var captions) && captions is ImageLayer { Drawings.Count: 0 }))
        {
            errors.Add($"{prefix}: captionGroup '{group}' is not a caption group (a null layer) of this project");
        }

        Layer? subject = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in layer.Sources)
        {
            if (!seen.Add(id))
            {
                errors.Add($"{prefix}: source '{id}' is listed twice");
            }
            else if (!layers.TryGetValue(id, out var source))
            {
                errors.Add($"{prefix}: source '{id}' not found; existing layers: {Ids.List(layers.Keys)}");
            }
            else if (source is not (VideoLayer or ImageLayer { Drawings.Count: > 0 }))
            {
                errors.Add($"{prefix}: source '{id}' is {(source is ImageLayer ? "a null layer" : $"a {source.TypeName} layer")}; a cutout's sources are video layers or image layers with drawings");
            }
            else if (subject is not null && source.GetType() != subject.GetType())
            {
                errors.Add($"{prefix}: sources '{subject.Id}' and '{id}' are different kinds of layer; a cutout's sources are the pieces of one clip or picture");
            }
            else
            {
                subject ??= source;
            }
        }

        if (layer.Matte is null)
        {
            errors.Add($"{prefix}: a cutout needs a matte (its method: aiVideo, aiImage, chroma or edgeFill)");
        }
        else if (subject is not null)
        {
            MatteEditor.Validate(layer, prefix, errors, subject);
        }

        if (!double.IsFinite(layer.Opacity.Value))
        {
            errors.Add($"{prefix}: opacity must be a finite number");
        }

        ValidateTimes(layer.Opacity.Keyframes.Select(k => k.Time).ToList(), $"{prefix}.opacity", errors);
        if (layer.Opacity.Keyframes.Any(k => !double.IsFinite(k.Value)))
        {
            errors.Add($"{prefix}.opacity: keyframe values must be finite numbers");
        }
    }

    private static void ValidateVisual(VisualLayer layer, string prefix, List<string> errors)
    {
        if (!double.IsFinite(layer.AnchorX) || !double.IsFinite(layer.AnchorY))
        {
            errors.Add($"{prefix}: anchorX/anchorY must be finite numbers");
        }

        foreach (var (name, value) in AnimatableProperties.Numeric(layer))
        {
            if (!double.IsFinite(value.Value))
            {
                errors.Add($"{prefix}: {name} must be a finite number");
            }

            ValidateTimes(value.Keyframes.Select(k => k.Time).ToList(), $"{prefix}.{name}", errors);
            if (value.Keyframes.Any(k => !double.IsFinite(k.Value)))
            {
                errors.Add($"{prefix}.{name}: keyframe values must be finite numbers");
            }
        }
    }

    private static void ValidateEffects(VisualLayer layer, string prefix, Dictionary<string, Asset> assets, List<string> errors)
    {
        if (layer.Shadow is { } shadow)
        {
            ValidateColor(shadow.Color, $"{prefix}: shadow.color", errors);
            ValidateRange(shadow.Blur, 0, EffectEditor.MaxBlur, $"{prefix}: shadow.blur", errors);
            ValidateRange(shadow.Opacity, 0, 1, $"{prefix}: shadow.opacity", errors);
        }

        if (layer.Outline is { } outline)
        {
            ValidateColor(outline.Color, $"{prefix}: outline.color", errors);
            ValidateRange(outline.Width, 0, EffectEditor.MaxOutline, $"{prefix}: outline.width", errors);
        }

        if (layer.Blur is { } blur)
        {
            ValidateRange(blur.X, 0, EffectEditor.MaxBlur, $"{prefix}: blur.x", errors);
            ValidateRange(blur.Y, 0, EffectEditor.MaxBlur, $"{prefix}: blur.y", errors);
        }

        if (layer.Tint is { } tint)
        {
            ValidateColor(tint.Color, $"{prefix}: tint.color", errors);
            ValidateRange(tint.Amount, 0, 1, $"{prefix}: tint.amount", errors);
        }

        if (layer.Mask is { } mask)
        {
            ValidateRange(mask.Width, 0, double.MaxValue, $"{prefix}: mask.width", errors);
            ValidateRange(mask.Height, 0, double.MaxValue, $"{prefix}: mask.height", errors);
            ValidateRange(mask.Feather, 0, EffectEditor.MaxFeather, $"{prefix}: mask.feather", errors);
            if (mask.Type != MaskType.Image)
            {
                if (mask.Asset is not null)
                {
                    errors.Add($"{prefix}: mask.asset is only for image masks (this one is a {EnumNames.ToName(mask.Type)})");
                }
            }
            else if (mask.Asset is null || !assets.TryGetValue(mask.Asset, out var maskAsset))
            {
                errors.Add($"{prefix}: the image mask needs an image asset (got '{mask.Asset}'); image assets: {ImageAssets(assets)}");
            }
            else if (maskAsset.Type != AssetType.Image)
            {
                errors.Add($"{prefix}: mask asset '{mask.Asset}' is {Article(maskAsset.Type)}, not an image");
            }
        }

        if (layer.MotionPath is { } path)
        {
            if (path.Points.Count < 2)
            {
                errors.Add($"{prefix}: a motion path needs at least 2 points (got {path.Points.Count})");
            }

            for (var i = 0; i < path.Points.Count; i++)
            {
                var point = path.Points[i];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
                    || new[] { point.InX, point.InY, point.OutX, point.OutY }.Any(h => h is { } v && !double.IsFinite(v)))
                {
                    errors.Add($"{prefix}: motion path point {i} must have finite x, y and handles");
                }

                if (point.InX is null != point.InY is null || point.OutX is null != point.OutY is null)
                {
                    errors.Add($"{prefix}: motion path point {i}: give a handle's x and y together (inX with inY, outX with outY)");
                }
            }

            ValidateRange(path.Progress, 0, Math.Max(0, path.Points.Count - 1), $"{prefix}: path", errors);
        }
    }

    private static void ValidateRange(AnimatedValue value, double min, double max, string where, List<string> errors)
    {
        var bad = value.Keyframes.Select(k => k.Value).Prepend(value.Value).Where(v => double.IsFinite(v) && (v < min || v > max)).ToList();
        if (bad.Count > 0)
        {
            var range = max == double.MaxValue ? $"at least {min}" : $"between {min} and {max}";
            errors.Add($"{where} must be {range} (got {bad[0]})");
        }
    }

    private static void ValidateColor(string? color, string where, List<string> errors)
    {
        if (!RgbaColor.TryParse(color, out _))
        {
            errors.Add($"{where} '{color}' is not a color; use #RRGGBB or #RRGGBBAA");
        }
    }

    private static void ValidateImage(ImageLayer layer, string prefix, Dictionary<string, Asset> assets, List<string> errors)
    {
        foreach (var (name, assetId) in layer.Drawings)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{prefix}: drawing names can't be empty");
            }

            if (assetId is null || !assets.TryGetValue(assetId, out var asset))
            {
                errors.Add($"{prefix}: drawing '{name}' uses asset '{assetId}', which is not in the project; image assets: {ImageAssets(assets)}");
            }
            else if (asset.Type != AssetType.Image)
            {
                errors.Add($"{prefix}: drawing '{name}' uses asset '{assetId}', which is {Article(asset.Type)}, not an image");
            }
        }

        if (layer.Drawings.Count == 0)
        {
            if (layer.Drawing.Value.Length > 0 || layer.Drawing.Keyframes.Count > 0)
            {
                errors.Add($"{prefix}: has no drawings, so its drawing property must be empty");
            }

            return;
        }

        var names = Ids.List(layer.Drawings.Keys);
        if (!layer.Drawings.ContainsKey(layer.Drawing.Value) && layer.Drawing.Keyframes.Count == 0)
        {
            errors.Add($"{prefix}: drawing '{layer.Drawing.Value}' not found; drawings: {names}");
        }

        foreach (var key in layer.Drawing.Keyframes)
        {
            if (!layer.Drawings.ContainsKey(key.Value))
            {
                errors.Add($"{prefix}.drawing: keyframe at {key.Time}s uses drawing '{key.Value}', which doesn't exist; drawings: {names}");
            }
        }

        ValidateTimes(layer.Drawing.Keyframes.Select(k => k.Time).ToList(), $"{prefix}.drawing", errors);
    }

    private static void ValidateText(TextLayer layer, string prefix, Dictionary<string, Asset> assets, List<string> errors)
    {
        if (!double.IsFinite(layer.FontSize) || layer.FontSize <= 0 || layer.FontSize > 2000)
        {
            errors.Add($"{prefix}: fontSize must be between 0 and 2000 (got {layer.FontSize})");
        }

        if (!RgbaColor.TryParse(layer.Color, out _))
        {
            errors.Add($"{prefix}: color '{layer.Color}' is not a color; use #RRGGBB or #RRGGBBAA");
        }

        if (layer.StrokeColor is not null && !RgbaColor.TryParse(layer.StrokeColor, out _))
        {
            errors.Add($"{prefix}: strokeColor '{layer.StrokeColor}' is not a color; use #RRGGBB or #RRGGBBAA");
        }

        if (!double.IsFinite(layer.StrokeWidth) || layer.StrokeWidth < 0)
        {
            errors.Add($"{prefix}: strokeWidth must be >= 0");
        }

        if (!double.IsFinite(layer.LineHeight) || layer.LineHeight <= 0)
        {
            errors.Add($"{prefix}: lineHeight must be greater than 0 (it is a multiple of the font's natural line spacing; 1 = normal)");
        }

        if (layer.MaxWidth is { } maxWidth && (!double.IsFinite(maxWidth) || maxWidth <= 0))
        {
            errors.Add($"{prefix}: maxWidth must be greater than 0, or null for no wrapping");
        }

        if (string.IsNullOrWhiteSpace(layer.FontFamily) && layer.FontAsset is null)
        {
            errors.Add($"{prefix}: set fontFamily or fontAsset");
        }

        if (layer.FontAsset is { } fontAsset)
        {
            if (!assets.TryGetValue(fontAsset, out var asset))
            {
                errors.Add($"{prefix}: fontAsset '{fontAsset}' not found; font assets: {Ids.List(assets.Values.Where(a => a.Type == AssetType.Font).Select(a => a.Id))}");
            }
            else if (asset.Type != AssetType.Font)
            {
                errors.Add($"{prefix}: fontAsset '{fontAsset}' is {Article(asset.Type)}, not a font");
            }
        }

        if (layer.Box is { } box)
        {
            if (!RgbaColor.TryParse(box.Color, out _))
            {
                errors.Add($"{prefix}: box.color '{box.Color}' is not a color; use #RRGGBB or #RRGGBBAA");
            }

            if (!double.IsFinite(box.Padding) || box.Padding < 0)
            {
                errors.Add($"{prefix}: box.padding must be >= 0");
            }

            if (!double.IsFinite(box.CornerRadius) || box.CornerRadius < 0)
            {
                errors.Add($"{prefix}: box.cornerRadius must be >= 0");
            }
        }
    }

    private static void ValidateAudio(AudioLayer layer, string prefix, Dictionary<string, Asset> assets, List<string> errors)
    {
        if (!assets.TryGetValue(layer.Asset, out var asset))
        {
            errors.Add($"{prefix}: asset '{layer.Asset}' not found; audio assets: {Ids.List(assets.Values.Where(a => a.Type == AssetType.Audio).Select(a => a.Id))}");
        }
        else if (asset.Type != AssetType.Audio)
        {
            errors.Add($"{prefix}: asset '{layer.Asset}' is {Article(asset.Type)}, not audio");
        }

        if (!double.IsFinite(layer.Volume) || layer.Volume < 0 || layer.Volume > 10)
        {
            errors.Add($"{prefix}: volume must be between 0 and 10 (1 = original level)");
        }

        if (!double.IsFinite(layer.TrimIn) || layer.TrimIn < 0)
        {
            errors.Add($"{prefix}: trimIn must be >= 0");
        }

        if (layer.TrimOut is { } trimOut && (!double.IsFinite(trimOut) || trimOut <= layer.TrimIn))
        {
            errors.Add($"{prefix}: trimOut ({trimOut}) must be greater than trimIn ({layer.TrimIn})");
        }

        if (!double.IsFinite(layer.FadeIn) || layer.FadeIn < 0 || !double.IsFinite(layer.FadeOut) || layer.FadeOut < 0)
        {
            errors.Add($"{prefix}: fadeIn and fadeOut must be >= 0");
        }
    }

    private static void ValidateVideoAsset(Asset asset, List<string> errors)
    {
        if (asset.Width is not > 0 || asset.Height is not > 0)
        {
            errors.Add($"video asset '{asset.Id}' needs width and height");
        }

        if (!VideoTiming.IsValidRate(asset.FrameRate))
        {
            errors.Add($"video asset '{asset.Id}' has an invalid frameRate '{asset.FrameRate}'");
        }

        if (asset.FrameCount is not > 0 || asset.Duration is not > 0)
        {
            errors.Add($"video asset '{asset.Id}' needs a frameCount and duration; import it again with import_video");
        }
    }

    private static void ValidateVideo(VideoLayer layer, string prefix, Dictionary<string, Asset> assets, List<string> errors)
    {
        if (!assets.TryGetValue(layer.Asset, out var asset))
        {
            errors.Add($"{prefix}: asset '{layer.Asset}' not found; video assets: {Ids.List(assets.Values.Where(a => a.Type == AssetType.Video).Select(a => a.Id))}");
        }
        else if (asset.Type != AssetType.Video)
        {
            errors.Add($"{prefix}: asset '{layer.Asset}' is {Article(asset.Type)}, not a video");
            asset = null;
        }

        if (!double.IsFinite(layer.Speed) || layer.Speed < VideoLayer.MinSpeed || layer.Speed > VideoLayer.MaxSpeed)
        {
            errors.Add($"{prefix}: speed must be between {VideoLayer.MinSpeed} and {VideoLayer.MaxSpeed} (got {layer.Speed})");
        }

        if (!double.IsFinite(layer.TrimIn) || layer.TrimIn < 0)
        {
            errors.Add($"{prefix}: trimIn must be >= 0");
        }

        if (!double.IsFinite(layer.TrimOut) || layer.TrimOut <= layer.TrimIn)
        {
            errors.Add($"{prefix}: trimOut ({layer.TrimOut}) must be greater than trimIn ({layer.TrimIn})");
        }
        else if (asset?.Duration is { } duration && layer.TrimOut > duration + 0.001)
        {
            errors.Add($"{prefix}: trimOut ({layer.TrimOut}) is past the end of '{asset.Id}' ({duration}s)");
        }

        if (!double.IsFinite(layer.Volume) || layer.Volume < 0 || layer.Volume > 10)
        {
            errors.Add($"{prefix}: volume must be between 0 and 10 (1 = original level)");
        }

        if (!double.IsFinite(layer.FadeIn) || layer.FadeIn < 0 || !double.IsFinite(layer.FadeOut) || layer.FadeOut < 0)
        {
            errors.Add($"{prefix}: fadeIn and fadeOut must be >= 0");
        }

        if (layer.CropWidth is null != layer.CropHeight is null)
        {
            errors.Add($"{prefix}: set both cropWidth and cropHeight, or neither (null shows the whole frame)");
        }
        else if (layer.CropWidth is { } cropWidth && layer.CropHeight is { } cropHeight)
        {
            if (!double.IsFinite(cropWidth) || !double.IsFinite(cropHeight) || cropWidth < 1 || cropHeight < 1)
            {
                errors.Add($"{prefix}: cropWidth and cropHeight must be at least 1 pixel");
            }
            else if (asset is { Width: { } width, Height: { } height } && (cropWidth > width + 1e-6 || cropHeight > height + 1e-6))
            {
                errors.Add($"{prefix}: the crop ({cropWidth}x{cropHeight}) is larger than the source ({width}x{height})");
            }
        }
    }

    private static void ValidateTimes(List<double> times, string where, List<string> errors)
    {
        for (var i = 0; i < times.Count; i++)
        {
            if (!double.IsFinite(times[i]) || times[i] < 0)
            {
                errors.Add($"{where}: keyframe time {times[i]} must be >= 0");
            }

            if (i > 0 && Math.Abs(times[i] - times[i - 1]) < KeyframeEditor.TimeTolerance)
            {
                errors.Add($"{where}: two keyframes at {times[i]}s");
            }
        }
    }

    private static void ValidateCycles(Project project, Dictionary<string, Layer> layers, List<string> errors)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in project.Layers)
        {
            var chain = new List<string> { layer.Id };
            var seen = new HashSet<string>(StringComparer.Ordinal) { layer.Id };
            var current = layer;
            while (current.ParentId is { } parentId && layers.TryGetValue(parentId, out var parent))
            {
                chain.Add(parent.Id);
                if (parent.Id == layer.Id)
                {
                    var key = string.Join(",", chain.Skip(1).Order(StringComparer.Ordinal));
                    if (reported.Add(key))
                    {
                        errors.Add($"parent cycle: {string.Join(" → ", chain)}");
                    }

                    break;
                }

                if (!seen.Add(parent.Id))
                {
                    break;
                }

                current = parent;
            }
        }
    }

    private static string ImageAssets(Dictionary<string, Asset> assets) =>
        Ids.List(assets.Values.Where(a => a.Type == AssetType.Image).Select(a => a.Id));

    private static string Article(AssetType type) => type switch
    {
        AssetType.Image => "an image",
        AssetType.Audio => "audio",
        AssetType.Video => "a video",
        _ => "a font"
    };
}
