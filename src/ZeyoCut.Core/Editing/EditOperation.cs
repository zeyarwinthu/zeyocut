// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// One edit as data: what the editor does when you drag, trim, split, delete or change a field. Each
// is applied through the same Core editor the matching MCP tool uses, so both make the same change.
// Label names it the way the tool would be named in history and backups (trim_layer clip_2).
public abstract record EditOperation
{
    public abstract string Label { get; }

    // The layers it works on. The editor drops an edit when another program changed one of them
    // after the editor last showed it, rather than make it on top of a version you didn't see.
    public abstract IReadOnlyList<string> Layers { get; }

    // Makes the change and returns a one-line summary of it.
    public abstract string Apply(Project project);

    protected static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record MoveLayerOperation(string LayerId, double Start) : EditOperation
{
    public override string Label => $"move_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => TimelineEditor.MoveLayer(project, LayerId, Start).Summary;
}

public sealed record TrimLayerOperation(string LayerId, double? Start, double? End) : EditOperation
{
    public override string Label => $"trim_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => TimelineEditor.TrimLayer(project, LayerId, Start, End).Summary;
}

public sealed record SplitLayerOperation(string LayerId, double Time) : EditOperation
{
    public override string Label => $"split_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project)
    {
        var result = TimelineEditor.SplitLayer(project, LayerId, Time);
        return $"split '{LayerId}' at {Num(Time)}s into '{result.LeftId}' and '{result.RightId}'";
    }
}

public sealed record RemoveLayerOperation(string LayerId, bool WithChildren) : EditOperation
{
    public override string Label => $"remove_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => $"removed: {string.Join(", ", ProjectEditor.RemoveLayer(project, LayerId, WithChildren))}";
}

// Any of a layer's fields, as update_layer takes them: {"volume": 0.5}, {"visible": false} (an
// audio layer's mute), {"fadeIn": 1}, {"text": "Hi"}, {"start": 2, "end": 4}.
public sealed record UpdateLayerOperation(string LayerId, JsonObject Changes) : EditOperation
{
    public override string Label => $"update_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    // A copy of the changes each time: a JSON node can't belong to two documents.
    public override string Apply(Project project)
    {
        var warnings = ProjectEditor.UpdateLayer(project, LayerId, (JsonObject)Changes.DeepClone());
        return $"updated layer '{LayerId}'" + string.Concat(warnings.Select(w => $"; {w}"));
    }
}

// The same fields of several layers in one step, such as the style of every caption in a track.
public sealed record UpdateLayersOperation(string Name, IReadOnlyList<string> LayerIds, JsonObject Changes) : EditOperation
{
    public override string Label => Name;

    public override IReadOnlyList<string> Layers => LayerIds;

    public override string Apply(Project project)
    {
        if (LayerIds.Count == 0)
        {
            throw new ZeyoCutException("there are no layers to change");
        }

        var warnings = LayerIds.SelectMany(id => ProjectEditor.UpdateLayer(project, id, (JsonObject)Changes.DeepClone())).Distinct().ToList();
        return $"updated {LayerIds.Count} layer(s): {string.Join(", ", Changes.Select(c => c.Key))}" + string.Concat(warnings.Select(w => $"; {w}"));
    }
}

// Draw order: a layer's zIndex (higher draws on top); the timeline's lanes follow it. Its children
// move with it. With Insert, it gets levels of its own from ZIndex up (see ProjectEditor.Reorder).
public sealed record ReorderLayerOperation(string LayerId, int ZIndex, bool Insert = false) : EditOperation
{
    public override string Label => $"reorder_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => ProjectEditor.Reorder(project, LayerId, ZIndex, Insert);
}

// Puts layers that never show at the same time on shared draw levels (ProjectEditor.PackLanes).
public sealed record PackLanesOperation : EditOperation
{
    public override string Label => "pack_lanes";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project) => ProjectEditor.PackLanes(project);
}

// A clip of an asset starting at Start, as add_video_layer, add_image_layer and add_audio_layer add
// one: footage fitted to the canvas, a still image for StillLength seconds (made smaller to fit the
// canvas when it's bigger), or sound with Role (music by default). Footage and stills go on draw
// level ZIndex, or on a level of their own on top when it's null. Footage and stills that run past
// the end make the project longer, as add_video_layer does; sound doesn't.
public sealed record AddClipOperation(AssetType Type, string AssetId, double Start, int? ZIndex = null, string? Role = null) : EditOperation
{
    public const double StillLength = 5;

    public override string Label => $"add_{EnumNames.ToName(Type)}_layer {AssetId}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        Layer layer;
        switch (Type)
        {
            case AssetType.Video:
                layer = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = AssetId, Start = Start, ZIndex = ZIndex });
                break;
            case AssetType.Image:
                var asset = project.FindAsset(AssetId);
                var fit = asset is { Width: > 0, Height: > 0 } ? Math.Min(1, Math.Min(project.Width / (double)asset.Width.Value, project.Height / (double)asset.Height.Value)) : 1;
                layer = ProjectEditor.AddImageLayer(project, new ImageLayerOptions
                {
                    Drawings = new Dictionary<string, string> { ["normal"] = AssetId },
                    Start = Start,
                    End = Start + StillLength,
                    ScaleX = fit,
                    ScaleY = fit,
                    ZIndex = ZIndex,
                });
                ProjectEditor.ExtendToFit(project, Start + StillLength);
                break;
            case AssetType.Audio:
                layer = ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = AssetId, Start = Start, Role = Role });
                break;
            default:
                throw new ZeyoCutException($"'{AssetId}' is a {EnumNames.ToName(Type)}: only video, images and sound go on the timeline");
        }

        var level = layer is VisualLayer visual ? $" on draw level {visual.ZIndex}" : layer is AudioLayer audio ? $" as {EnumNames.ToName(audio.Role)}" : "";
        return $"added {layer.TypeName} layer '{layer.Id}' at {Num(Start)}s{level}";
    }
}

// A file AssetImporter.Prepare copied into the project, added as an asset, as import_asset and
// import_video add it. A normalized video names its original and why it was normalized.
public sealed record AddAssetOperation(PreparedAsset Asset, string? Id = null, string? OriginalFile = null, string? Normalized = null) : EditOperation
{
    public override string Label => $"{(Asset.Type == AssetType.Video ? "import_video" : "import_asset")} {Path.GetFileName(OriginalFile ?? Asset.Source)}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        var added = AssetImporter.Add(project, Asset, Id, OriginalFile, Normalized);
        return $"imported '{added.Id}' ({EnumNames.ToName(added.Type)}, {added.File})";
    }
}

// Several operations as one undo step, such as a drag to another lane (move and reorder). They run
// in order; if one fails, none is kept.
public sealed record BatchOperation(string Name, IReadOnlyList<EditOperation> Operations) : EditOperation
{
    public override string Label => Name;

    public override IReadOnlyList<string> Layers => Operations.SelectMany(o => o.Layers).Distinct(StringComparer.Ordinal).ToList();

    public override string Apply(Project project)
    {
        if (Operations.Count == 0)
        {
            throw new ZeyoCutException("the batch has no operations");
        }

        return string.Join("; ", Operations.Select(o => o.Apply(project)));
    }
}
