// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// A sound file in the library's sfx folder. Name is its path inside sfx without the extension,
// with '/' (impacts/thud); Category is the folder part ("" at the top).
public sealed record SoundEffect(string Name, string Category, string File);

public sealed record SoundEffectAdded(SoundEffect Effect, AudioLayer Layer, Asset Asset, bool Imported, IReadOnlyList<string> Warnings);

// list_sfx and add_sfx: the user's own sound files, placed with role effects (never ducked).
public static class SoundEffects
{
    public static List<SoundEffect> List(Library library, string? filter = null)
    {
        var root = library.SfxFolder;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var effects = new List<SoundEffect>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (AssetImporter.DetectType(file) != AssetType.Audio)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var name = relative[..^Path.GetExtension(relative).Length];
            var slash = name.LastIndexOf('/');
            effects.Add(new SoundEffect(name, slash < 0 ? "" : name[..slash], file));
        }

        var wanted = filter?.Trim() ?? "";
        return [.. effects
            .Where(e => wanted.Length == 0 || e.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // By full name (impacts/thud) or, when it is unique, by file name alone (thud).
    public static SoundEffect Find(Library library, string? name)
    {
        var all = List(library);
        if (all.Count == 0)
        {
            throw new ZeyoCutException($"there are no sound effects yet; put wav, mp3, ogg or other audio files in {library.SfxFolder} (subfolders become categories)");
        }

        var wanted = (name ?? "").Trim().Replace('\\', '/');
        if (wanted.Length == 0)
        {
            throw new ZeyoCutException($"name is required; list_sfx shows the {all.Count} sound effect(s)");
        }

        var exact = all.Where(e => e.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var byFile = all.Where(e => Path.GetFileName(e.Name).Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFile.Count == 1)
        {
            return byFile[0];
        }

        if (byFile.Count > 1)
        {
            throw new ZeyoCutException($"'{wanted}' matches {byFile.Count} sound effects: {string.Join(", ", byFile.Select(e => e.Name))}; use the full name");
        }

        var close = all.Where(e => e.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).Select(e => e.Name).Take(10).ToList();
        throw new ZeyoCutException(close.Count > 0
            ? $"no sound effect named '{wanted}'; similar: {string.Join(", ", close)}"
            : $"no sound effect named '{wanted}'; {(all.Count <= 20 ? $"sound effects: {string.Join(", ", all.Select(e => e.Name))}" : $"list_sfx shows all {all.Count}")}");
    }

    // Imports the file once (a project asset with the same content is reused) and adds an audio layer
    // with role effects at 'time'.
    public static SoundEffectAdded Add(Project project, string projectDirectory, Library library, string name, double time, double volume, IAssetProbe probe)
    {
        var effect = Find(library, name);
        if (!double.IsFinite(time) || time < 0 || time >= project.Duration)
        {
            throw new ZeyoCutException($"time must be from 0 to before the project end ({Num(project.Duration)}s); got {Num(time)}");
        }

        if (!double.IsFinite(volume) || volume < 0 || volume > 10)
        {
            throw new ZeyoCutException($"volume must be between 0 and 10 (1 = as recorded); got {Num(volume)}");
        }

        var info = probe.Probe(effect.File, AssetType.Audio);
        var count = project.Assets.Count;
        var asset = AssetImporter.Adopt(project, projectDirectory, new Asset { Type = AssetType.Audio, Duration = info.Duration }, effect.File, Path.GetFileName(effect.Name));
        var layer = ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = asset.Id, Start = time, Volume = volume, Role = EnumNames.ToName(AudioRole.Effects) });
        var warnings = new List<string>();
        if (asset.Duration is { } length && time + length > project.Duration + 1e-9)
        {
            warnings.Add($"the sound lasts {Num(length)}s and runs past the project end ({Num(project.Duration)}s), so the export cuts it at the end");
        }

        return new SoundEffectAdded(effect, layer, asset, project.Assets.Count > count, warnings);
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
