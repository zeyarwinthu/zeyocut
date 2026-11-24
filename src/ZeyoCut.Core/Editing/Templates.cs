// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// add_template's result: the template's name, the layers added, the asset files copied in and those
// the project had already, and what to look at.
public sealed record TemplatePlacement(string Name, double At, IReadOnlyList<string> Layers, IReadOnlyList<string> AddedAssets, IReadOnlyList<string> ReusedAssets, IReadOnlyList<string> Warnings);

// save_template / new_project_from_template / add_template.
public static class Templates
{
    public static TemplateFile Save(Project project, string projectDirectory, Library library, string? name, IReadOnlyList<string>? layerIds, IReadOnlyList<string>? pinToEnd, bool overwrite)
    {
        var templateName = Library.CheckName(name, LibraryKind.Template);
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in layerIds is { Count: > 0 } ids ? ids : project.Layers.Select(l => l.Id).ToList())
        {
            chosen.UnionWith(ProjectEditor.Subtree(project, ProjectEditor.GetLayer(project, id).Id).Select(l => l.Id));
        }

        if (chosen.Count == 0)
        {
            throw new ZeyoCutException("the project has no layers to save as a template");
        }

        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pinToEnd ?? [])
        {
            var layer = ProjectEditor.GetLayer(project, id);
            if (!chosen.Contains(layer.Id))
            {
                throw new ZeyoCutException($"pinToEnd layer '{layer.Id}' is not among the layers being saved");
            }

            pinned.UnionWith(ProjectEditor.Subtree(project, layer.Id).Select(l => l.Id));
        }

        var copy = ProjectJson.Clone(project);
        copy.Camera = null;
        copy.Layers.RemoveAll(l => !chosen.Contains(l.Id) && l is not RedactionLayer);
        SubjectEditor.Follow(copy);
        var assetIds = copy.Layers.SelectMany(AssetIds).ToHashSet(StringComparer.Ordinal);

        // The tracks of the footage it keeps go along, and so do their redactions, so the footage
        // stays hidden in projects made from it.
        copy.Tracks.RemoveAll(t => !assetIds.Contains(t.Asset));
        RedactionEditor.DropMissingTracks(copy);
        var template = new TemplateFile
        {
            Name = templateName,
            SavedUtc = DateTime.UtcNow,
            Project = copy,
            PinToEnd = copy.Layers.Where(l => pinned.Contains(l.Id)).Select(l => l.Id).ToList(),
        };
        library.Save(LibraryKind.Template, templateName, overwrite, folder =>
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            copy.Assets = project.Assets.Where(a => assetIds.Contains(a.Id)).Select(a => Library.CopyAsset(projectDirectory, a, folder, used)).ToList();
            foreach (var track in copy.Tracks)
            {
                var source = TrackFiles.PathOf(projectDirectory, track);
                if (!File.Exists(source))
                {
                    throw new ZeyoCutException($"track '{track.Id}': its file {track.File} is missing; run track_object again");
                }

                var destination = TrackFiles.PathOf(folder, track);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            AnalysisFiles.Save(Path.Combine(folder, "template.json"), template);
        });
        return template;
    }

    // Builds the new project and copies the template's assets into projectDirectory\assets.
    // Layers that ran to the template's end run to the new end; pinned layers keep their distance
    // from it; everything else keeps its times.
    public static Project Create(Library library, string? name, string projectDirectory, string projectName, double? duration, List<string> warnings)
    {
        var template = library.Load<TemplateFile>(LibraryKind.Template, name);
        var folder = library.PathOf(LibraryKind.Template, Library.CheckName(name, LibraryKind.Template));
        if (ProjectStore.IsProjectDirectory(projectDirectory))
        {
            throw new ZeyoCutException($"a project already exists in '{projectDirectory}'; choose another name");
        }

        var project = ProjectJson.Clone(template.Project);
        project.Name = projectName;
        var old = project.Duration;
        var length = duration ?? old;
        if (!double.IsFinite(length) || length <= 0 || length > 7200)
        {
            throw new ZeyoCutException($"duration must be greater than 0 and at most 7200 seconds (got {Num(length)})");
        }

        project.Duration = length;
        var delta = length - old;
        var pinned = template.PinToEnd.ToHashSet(StringComparer.Ordinal);
        foreach (var layer in project.Layers)
        {
            if (pinned.Contains(layer.Id))
            {
                if (layer.Start + delta < -1e-9)
                {
                    throw new ZeyoCutException($"'{layer.Id}' is pinned to the end and starts {Num(old - layer.Start)}s before it; use a duration of at least {Num(old - layer.Start)}s");
                }

                Shift(layer, delta);
            }
            else if (layer.End is { } end && end >= old - 1e-6 && layer is not VideoLayer)
            {
                layer.End = null;
            }
            else if (layer.Start >= length)
            {
                warnings.Add($"'{layer.Id}' starts at {Num(layer.Start)}s, after the project's end");
            }
        }

        foreach (var asset in project.Assets)
        {
            var destination = ProjectStore.AssetPath(projectDirectory, asset);
            var source = ProjectStore.AssetPath(folder, asset);
            if (!File.Exists(source))
            {
                throw new ZeyoCutException($"template '{template.Name}' is missing its file {source}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        foreach (var track in project.Tracks)
        {
            var source = TrackFiles.PathOf(folder, track);
            if (!File.Exists(source))
            {
                throw new ZeyoCutException($"template '{template.Name}' is missing its file {source}");
            }

            var destination = TrackFiles.PathOf(projectDirectory, track);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        return project;
    }

    // Adds a template's layers to an open project as one block from At: each keeps its time from the
    // template's start, and a layer that ran to the template's end ends where the template does.
    // Pictures, text and footage go on draw levels above the project's, as add_rig puts a rig. Ids the
    // project has already get a number; the template's files are copied into assets/, or the project's
    // own copy is used. A template with tracked footage only makes new projects.
    public static TemplatePlacement Add(Project project, string projectDirectory, Library library, string? name, double at)
    {
        var template = library.Load<TemplateFile>(LibraryKind.Template, name);
        var folder = library.PathOf(LibraryKind.Template, Library.CheckName(name, LibraryKind.Template));
        if (!double.IsFinite(at) || at < 0)
        {
            throw new ZeyoCutException($"at must be 0 or later (got {Num(at)})");
        }

        var source = ProjectJson.Clone(template.Project);
        if (source.Tracks.Count > 0 || source.Layers.Any(l => l is RedactionLayer))
        {
            throw new ZeyoCutException($"template '{template.Name}' has tracked footage; make a new project from it with new_project_from_template");
        }

        if (source.Layers.Count == 0)
        {
            throw new ZeyoCutException($"template '{template.Name}' has no layers");
        }

        var warnings = new List<string>();
        if (source.Width != project.Width || source.Height != project.Height)
        {
            warnings.Add($"the template is {source.Width}x{source.Height} and the project {project.Width}x{project.Height}; its layers keep their pixel positions");
        }

        var assetMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new List<string>();
        var reused = new List<string>();
        foreach (var asset in source.Assets)
        {
            var before = project.Assets.Count;
            var adopted = AssetImporter.Adopt(project, projectDirectory, asset, ProjectStore.AssetPath(folder, asset), asset.Id);
            assetMap[asset.Id] = adopted.Id;
            (project.Assets.Count > before ? added : reused).Add(adopted.Id);
        }

        var taken = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in source.Layers)
        {
            ids[layer.Id] = Ids.Unique(layer.Id, taken.Contains);
            taken.Add(ids[layer.Id]);
        }

        static bool Drawn(Layer layer) => layer is VisualLayer or CutoutLayer;
        var top = project.Layers.Where(Drawn).Select(l => l.ZIndex).DefaultIfEmpty(-1).Max() + 1;
        var lift = source.Layers.Any(Drawn) ? top - source.Layers.Where(Drawn).Min(l => l.ZIndex) : 0;
        foreach (var layer in source.Layers)
        {
            var old = layer.Id;
            layer.Id = ids[old];
            layer.Name = layer.Name == old ? layer.Id : layer.Name;
            layer.ParentId = layer.ParentId is { } parent ? ids[parent] : null;
            layer.ZIndex += Drawn(layer) ? lift : 0;
            if (layer is IColorAdjustable { Grade.Look: { } look })
            {
                look.Asset = assetMap[look.Asset];
            }

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
                case AudioLayer audio:
                    audio.Asset = assetMap[audio.Asset];
                    break;
                case VideoLayer video:
                    video.Asset = assetMap[video.Asset];
                    break;
                case CutoutLayer cutout:
                    cutout.Sources = cutout.Sources.Select(s => ids.GetValueOrDefault(s, s)).ToList();
                    break;
            }

            if (layer is VisualLayer { Mask: { Asset: { } maskAsset } mask })
            {
                mask.Asset = assetMap[maskAsset];
            }

            if (layer is CutoutLayer)
            {
                continue;
            }

            if (layer.End is null && layer is not VideoLayer)
            {
                layer.End = source.Duration;
            }

            Shift(layer, at);
            if (layer is AudioLayer sound)
            {
                KeyframeWindow.Shift(sound.Volume, at);
            }
        }

        project.Layers.AddRange(source.Layers);
        var end = at + source.Duration;
        if (end > project.Duration + 1e-6)
        {
            warnings.Add($"the template runs to {Num(end)}s, past the project's end ({Num(project.Duration)}s)");
        }

        return new TemplatePlacement(template.Name, at, source.Layers.Select(l => l.Id).ToList(), added, reused, warnings);
    }

    public static string Describe(TemplatePlacement placed) =>
        $"added template '{placed.Name}' at {Num(placed.At)}s: {placed.Layers.Count} layer(s): {Ids.List(placed.Layers)}"
        + (placed.AddedAssets.Count > 0 ? $"; imported {placed.AddedAssets.Count} file(s)" : "")
        + (placed.ReusedAssets.Count > 0 ? $"; reused {placed.ReusedAssets.Count} asset(s) already in the project" : "");

    public static string Describe(TemplateFile template)
    {
        var project = template.Project;
        return $"{project.Width}x{project.Height} @ {project.Fps} fps, {Num(project.Duration)}s, {project.Layers.Count} layer(s), {project.Assets.Count} asset(s)"
            + (template.PinToEnd.Count > 0 ? $"; pinned to the end: {string.Join(", ", template.PinToEnd)}" : "");
    }

    private static void Shift(Layer layer, double delta)
    {
        layer.Start = KeyframeWindow.Round(layer.Start + delta);
        layer.End = layer.End is { } end ? KeyframeWindow.Round(end + delta) : null;
        if (layer is VisualLayer visual)
        {
            foreach (var (_, value) in AnimatableProperties.Numeric(visual))
            {
                KeyframeWindow.Shift(value, delta);
            }
        }

        if (layer is ImageLayer image)
        {
            KeyframeWindow.Shift(image.Drawing, delta);
        }
    }

    private static IEnumerable<string> AssetIds(Layer layer)
    {
        IEnumerable<string> ids = layer switch
        {
            ImageLayer image => image.Drawings.Values,
            TextLayer { FontAsset: { } font } => [font],
            AudioLayer audio => [audio.Asset],
            VideoLayer video => [video.Asset],
            _ => [],
        };
        ids = ColorEditor.LookAsset(layer) is { } look ? ids.Append(look) : ids;
        return layer is VisualLayer { Mask.Asset: { } mask } ? ids.Append(mask) : ids;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
