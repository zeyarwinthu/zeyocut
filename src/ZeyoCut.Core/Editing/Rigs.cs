// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public sealed record RigPart(string Role, VisualLayer Layer);

public sealed record RigPlacement(string RootId, IReadOnlyList<string> LayerIds, IReadOnlyList<string> NewAssets, IReadOnlyList<string> ReusedAssets);

// save_rig / add_rig. A rig is a layer and everything parented under it.
public static class Rigs
{
    public const string RootRole = "root";

    // The rig's parts by role; the root is "root". A part without a role is named after its id
    // without the prefix the part ids share (tora_head → head).
    public static Dictionary<string, VisualLayer> Roles(Project project, string? rootId)
    {
        var (_, parts) = Parts(project, rootId);
        return parts.ToDictionary(p => p.Role, p => p.Layer, StringComparer.Ordinal);
    }

    public static (string RootName, List<RigPart> Parts) Parts(Project project, string? rootId)
    {
        var root = ProjectEditor.GetVisualLayer(project, rootId, "rig root");
        var layers = ProjectEditor.Subtree(project, root.Id).Cast<VisualLayer>().ToList();
        var prefix = SharedPrefix(layers.Skip(1).Select(l => l.Id).ToList());
        var parts = new List<RigPart> { new(RootRole, root) };
        var byRole = new Dictionary<string, string>(StringComparer.Ordinal) { [RootRole] = root.Id };
        foreach (var layer in layers.Skip(1))
        {
            var role = layer.RigRole ?? Strip(layer.Id, prefix);
            if (!byRole.TryAdd(role, layer.Id))
            {
                throw new ZeyoCutException($"parts '{byRole[role]}' and '{layer.Id}' of rig '{root.Id}' both have the role '{role}'; give one another role with update_layer, e.g. {{\"rigRole\": \"{role}_2\"}}");
            }

            parts.Add(new RigPart(role, layer));
        }

        var rootName = Strip(root.Id, prefix);
        if (byRole.ContainsKey(rootName) && byRole[rootName] != root.Id)
        {
            rootName = RootRole;
        }

        return (rootName, parts);
    }

    public static RigFile Save(Project project, string projectDirectory, Library library, string? rootId, string? name, double? time, bool overwrite)
    {
        var rigName = Library.CheckName(name, LibraryKind.Rig);
        var (rootName, parts) = Parts(project, rootId);
        var root = parts[0].Layer;
        if (parts.FirstOrDefault(p => p.Layer is VideoLayer) is { } video)
        {
            throw new ZeyoCutException($"'{video.Layer.Id}' is a video layer; rigs hold image and text layers");
        }

        var pose = time ?? root.Start;
        if (!double.IsFinite(pose) || pose < 0)
        {
            throw new ZeyoCutException($"time must be >= 0 (got {Num(pose)})");
        }

        var names = parts.ToDictionary(p => p.Layer.Id, p => p.Role == RootRole ? rootName : p.Role, StringComparer.Ordinal);
        var rig = new RigFile { Name = rigName, SavedUtc = DateTime.UtcNow, Root = rootName };
        var assetIds = new List<string>();
        foreach (var (role, layer) in parts)
        {
            var copy = (VisualLayer)ProjectJson.LayerFromNode(ProjectJson.LayerToNode(layer));
            copy.Id = names[layer.Id];
            copy.Name = copy.Id;
            copy.RigRole = role == RootRole ? null : role;
            copy.ParentId = role == RootRole ? null : names[layer.ParentId!];
            copy.Start = Math.Max(0, layer.Start - root.Start);
            copy.End = layer.End is { } end ? end - root.Start : null;
            Freeze(copy, pose);
            rig.Parts.Add(copy);
            assetIds.AddRange(AssetIds(copy));
        }

        library.Save(LibraryKind.Rig, rigName, overwrite, folder =>
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in assetIds.Distinct())
            {
                var asset = project.FindAsset(id) ?? throw new ZeyoCutException($"asset '{id}' is missing from the project");
                rig.Assets.Add(Library.CopyAsset(projectDirectory, asset, folder, used));
            }

            AnalysisFiles.Save(Path.Combine(folder, "rig.json"), rig);
        });
        return rig;
    }

    public static RigPlacement Add(Project project, string projectDirectory, Library library, string? name, double? x, double? y, double startTime, double scale, string? instanceName)
    {
        var rig = library.Load<RigFile>(LibraryKind.Rig, name);
        var folder = library.PathOf(LibraryKind.Rig, Library.CheckName(name, LibraryKind.Rig));
        if (!double.IsFinite(startTime) || startTime < 0)
        {
            throw new ZeyoCutException($"startTime must be >= 0 (got {Num(startTime)})");
        }

        if (!double.IsFinite(scale) || scale <= 0 || scale > 100)
        {
            throw new ZeyoCutException($"scale must be greater than 0 and at most 100 (got {Num(scale)})");
        }

        bool Taken(string instance) => rig.Parts.Any(p => project.FindLayer($"{instance}_{p.Id}") is not null);
        string instanceId;
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            instanceId = instanceName.Trim();
            Ids.EnsureValid($"{instanceId}_{rig.Root}", "layer");
            if (Taken(instanceId))
            {
                throw new ZeyoCutException($"layers named '{instanceId}_...' already exist; choose another instanceName or omit it");
            }
        }
        else
        {
            instanceId = Ids.Unique(Ids.Slug(rig.Name, "rig"), Taken);
        }

        var assetMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new List<string>();
        var reused = new List<string>();
        foreach (var asset in rig.Assets)
        {
            var before = project.Assets.Count;
            var adopted = AssetImporter.Adopt(project, projectDirectory, asset, ProjectStore.AssetPath(folder, asset), asset.Id);
            assetMap[asset.Id] = adopted.Id;
            (project.Assets.Count > before ? added : reused).Add(adopted.Id);
        }

        var visual = project.Layers.Where(l => l is VisualLayer or CutoutLayer).ToList();
        var zOffset = (visual.Count == 0 ? 0 : visual.Max(l => l.ZIndex) + 1) - rig.Parts.Min(p => p.ZIndex);
        var ids = new List<string>();
        foreach (var part in rig.Parts)
        {
            var layer = (VisualLayer)ProjectJson.LayerFromNode(ProjectJson.LayerToNode(part));
            layer.Id = $"{instanceId}_{part.Id}";
            layer.Name = layer.Id;
            layer.ParentId = part.ParentId is { } parent ? $"{instanceId}_{parent}" : null;
            layer.Start = KeyframeWindow.Round(startTime + part.Start);
            layer.End = part.End is { } end ? KeyframeWindow.Round(startTime + end) : null;
            layer.ZIndex = part.ZIndex + zOffset;
            switch (layer)
            {
                case ImageLayer image:
                    foreach (var drawing in image.Drawings.Keys.ToList())
                    {
                        image.Drawings[drawing] = assetMap[image.Drawings[drawing]];
                    }

                    break;
                case TextLayer { FontAsset: { } font } text:
                    text.FontAsset = assetMap[font];
                    break;
            }

            if (layer.Mask is { Asset: { } maskAsset } mask)
            {
                mask.Asset = assetMap[maskAsset];
            }

            if (part.Id == rig.Root)
            {
                layer.X.Value = x ?? project.Width / 2.0;
                layer.Y.Value = y ?? project.Height / 2.0;
                layer.ScaleX.Value *= scale;
                layer.ScaleY.Value *= scale;
            }

            project.Layers.Add(layer);
            ids.Add(layer.Id);
        }

        return new RigPlacement($"{instanceId}_{rig.Root}", ids, added, reused);
    }

    public static string Describe(RigFile rig)
    {
        var drawings = rig.Parts.OfType<ImageLayer>().Sum(p => p.Drawings.Count);
        return $"{rig.Parts.Count} part(s): {string.Join(", ", rig.Parts.Select(p => p.Id))}; {drawings} drawing(s)";
    }

    private static void Freeze(VisualLayer layer, double time)
    {
        var place = layer.MotionPath is { } path ? MotionPaths.At(path, time) : (PathPlace?)null;
        foreach (var (_, value) in AnimatableProperties.Numeric(layer))
        {
            value.Value = Interpolator.Evaluate(value, time);
            value.Keyframes.Clear();
        }

        if (place is { } at)
        {
            layer.X.Value = at.X;
            layer.Y.Value = at.Y;
            layer.Rotation.Value += layer.MotionPath!.AutoOrient ? at.Angle : 0;
            layer.MotionPath = null;
        }

        if (layer is ImageLayer image)
        {
            image.Drawing.Value = Interpolator.Evaluate(image.Drawing, time);
            image.Drawing.Keyframes.Clear();
        }
    }

    private static IEnumerable<string> AssetIds(Layer layer)
    {
        IEnumerable<string> ids = layer switch
        {
            ImageLayer image => image.Drawings.Values,
            TextLayer { FontAsset: { } font } => [font],
            _ => [],
        };
        return layer is VisualLayer { Mask.Asset: { } mask } ? ids.Append(mask) : ids;
    }

    // The longest prefix the ids share, cut back to its last '_' ("tora_head", "tora_tail" → "tora_").
    private static string SharedPrefix(List<string> ids)
    {
        if (ids.Count == 0)
        {
            return "";
        }

        var prefix = ids[0];
        foreach (var id in ids.Skip(1))
        {
            var n = 0;
            while (n < prefix.Length && n < id.Length && prefix[n] == id[n])
            {
                n++;
            }

            prefix = prefix[..n];
        }

        var cut = prefix.LastIndexOf('_');
        return cut < 0 ? "" : prefix[..(cut + 1)];
    }

    private static string Strip(string id, string prefix) =>
        prefix.Length > 0 && id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
