// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// save_template / new_project_from_template.
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
        return layer is VisualLayer { Mask.Asset: { } mask } ? ids.Append(mask) : ids;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
