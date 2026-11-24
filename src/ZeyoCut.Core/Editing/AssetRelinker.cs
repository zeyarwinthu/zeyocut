// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// What relinking changed: the asset, and how its size, length or font family differs from before.
public sealed record AssetRelinked(Asset Asset, IReadOnlyList<string> Warnings)
{
    public string Summary => $"relinked '{Asset.Id}' to {Asset.File}" + string.Concat(Warnings.Select(w => $"; {w}"));
}

// A file that is likely an asset's file, and why.
public sealed record RelinkMatch(string Path, string Reason);

// Points an asset at another file of the same kind, such as its file found elsewhere after it was
// moved or deleted from assets\: relink_asset and the editor's Relink. The asset keeps its id, so
// every layer that uses it uses the new file.
public static class AssetRelinker
{
    // How many folders down Matches looks below each folder it's given.
    public const int SearchDepth = 4;

    // The asset, when the file is of its kind; checked before the file is copied in.
    public static Asset Check(Project project, string assetId, string sourcePath)
    {
        var asset = project.FindAsset(assetId)
            ?? throw new ZeyoCutException($"asset '{assetId}' not found; assets: {Ids.List(project.Assets.Select(a => a.Id))}");
        var type = AssetImporter.DetectType(sourcePath);
        if (type != asset.Type)
        {
            var name = Path.GetFileName(sourcePath);
            throw new ZeyoCutException(type is { } kind
                ? $"'{name}' is {Kind(kind)}, and asset '{asset.Id}' needs {Kind(asset.Type)}"
                : $"'{name}' isn't a file ZeyoCut imports, and asset '{asset.Id}' needs {Kind(asset.Type)}");
        }

        return asset;
    }

    // Points the asset at a file Prepare copied into assets\, with that file's size, length and font
    // family. A normalized video's original and reasons go: the new file is used as it is.
    public static AssetRelinked Relink(Project project, string assetId, PreparedAsset prepared)
    {
        var asset = Check(project, assetId, prepared.Path);
        var before = Measure(asset);
        var info = prepared.Info;
        var changed = before is not null
            && (info.Width != asset.Width || info.Height != asset.Height
                || (asset.Duration is { } duration && (info.Duration is not { } length || Math.Abs(length - duration) > Frame(project)))
                || !string.Equals(info.FontFamily, asset.FontFamily, StringComparison.OrdinalIgnoreCase));
        asset.File = $"{ProjectStore.AssetsFolder}/{Path.GetFileName(prepared.Path)}";
        asset.Width = info.Width;
        asset.Height = info.Height;
        asset.Duration = info.Duration;
        asset.FontFamily = info.FontFamily;
        asset.FrameRate = info.FrameRate;
        asset.FrameCount = info.FrameCount;
        asset.HasAudio = info.HasAudio;
        asset.Codec = info.Codec;
        asset.OriginalFile = null;
        asset.Normalized = null;
        return new AssetRelinked(asset, changed ? [$"'{asset.Id}' was {before} and is now {Measure(asset)}"] : []);
    }

    // Files in the folders, or up to SearchDepth folders below them, that are likely the asset's
    // file: the same name and kind, and the same pixel size, length (within a frame) or font family
    // as the asset has on record. A file that can't be read is passed over.
    public static IReadOnlyList<RelinkMatch> Matches(Project project, Asset asset, IEnumerable<string> folders, IAssetProbe probe, CancellationToken cancellationToken = default)
    {
        var found = new List<RelinkMatch>();
        var name = Path.GetFileName(asset.File.Replace('/', Path.DirectorySeparatorChar));
        if (!ProjectStore.IsPlainFileName(name))
        {
            return found;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = SearchDepth,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            MatchType = MatchType.Simple,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> files;
            try
            {
                files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, name, options).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(file);
                if (seen.Add(path) && AssetImporter.DetectType(path) == asset.Type && Reason(project, asset, path, probe) is { } reason)
                {
                    found.Add(new RelinkMatch(path, reason));
                }
            }
        }

        return found;
    }

    private static string? Reason(Project project, Asset asset, string path, IAssetProbe probe)
    {
        AssetInfo info;
        try
        {
            info = probe.Probe(path, asset.Type);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        var frame = Frame(project);
        bool? size = asset.Width is { } width && asset.Height is { } height ? info.Width == width && info.Height == height : null;
        bool? length = asset.Duration is { } duration ? info.Duration is { } other && Math.Abs(other - duration) <= frame : null;
        bool? family = asset.FontFamily is { } font ? string.Equals(info.FontFamily, font, StringComparison.OrdinalIgnoreCase) : null;
        var (same, said) = asset.Type switch
        {
            AssetType.Image => (size, $"size {Size(asset)}"),
            AssetType.Audio => (length, $"length {Length(asset)}"),
            AssetType.Video => (size is null || length is null ? size ?? length : size & length, $"size {Size(asset)} and length {Length(asset)}"),
            AssetType.Lut => ((bool?)null, ""),
            _ => (family, $"font family \"{asset.FontFamily}\""),
        };
        return same switch
        {
            true => $"same {said}",
            false => null,
            null => "same name",
        };
    }

    // How far apart two lengths can be and still be the same: a frame.
    private static double Frame(Project project) => 1.0 / project.Fps + 1e-9;

    // The asset's size, length or font family, as relinking reports a change in it.
    private static string? Measure(Asset asset) => asset.Type switch
    {
        AssetType.Image => asset.Width is null || asset.Height is null ? null : Size(asset),
        AssetType.Audio => asset.Duration is null ? null : Length(asset),
        AssetType.Video => asset.Width is null || asset.Height is null || asset.Duration is null ? null : $"{asset.Width}x{asset.Height}, {Length(asset)}",
        AssetType.Lut => null,
        _ => asset.FontFamily is null ? null : $"font family \"{asset.FontFamily}\"",
    };

    private static string Size(Asset asset) => $"{asset.Width}x{asset.Height}{(asset.Type == AssetType.Image ? " px" : "")}";

    private static string Length(Asset asset) => $"{(asset.Duration ?? 0).ToString("0.###", CultureInfo.InvariantCulture)}s";

    private static string Kind(AssetType type) => type switch
    {
        AssetType.Image => "an image",
        AssetType.Audio => "an audio file",
        AssetType.Video => "a video",
        AssetType.Lut => "a LUT",
        _ => "a font",
    };
}
