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

// Draw order: a layer's zIndex (higher draws on top); the timeline's lanes follow it.
public sealed record ReorderLayerOperation(string LayerId, int ZIndex) : EditOperation
{
    public override string Label => $"reorder_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project)
    {
        ProjectEditor.UpdateLayer(project, LayerId, new JsonObject { ["zIndex"] = ZIndex });
        return $"'{LayerId}' now draws at z={ZIndex}";
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
