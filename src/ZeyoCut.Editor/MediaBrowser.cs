// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// A shelf of the Media browser: the whole library, the media of one kind, or a bin, which is an
// asset tag. Key is what the browser remembers it by.
public sealed record MediaShelf(string Key, string Name, int Count, AssetType? Kind = null, string? Tag = null);

// The order of the browser's grid: the newest import first, the oldest first, or by name.
public enum MediaSort
{
    Recent,
    Oldest,
    Name,
}

// What the browser's details column says about the media selected: one file, with its kind and
// size, how big it is, where it is and which of this project's layers use it; or several, counted
// by kind. Tags are the ones the selection has between them.
public sealed record MediaDetails(string Title, string Summary, string? UsedBy, string? FileSize, string? Location, string? Path, IReadOnlyList<string> Tags);

// The Media browser: the shelves, the grid and the details, worked out from the project.
public static class MediaBrowser
{
    public const string AllKey = "all";

    private static readonly AssetType[] KindOrder = [AssetType.Video, AssetType.Image, AssetType.Audio, AssetType.Font, AssetType.Lut];

    public static IReadOnlyList<MediaShelf> Library(Project project) =>
    [
        new(AllKey, "All media", project.Assets.Count),
        Shelf(project, AssetType.Video, "Video"),
        Shelf(project, AssetType.Image, "Images"),
        Shelf(project, AssetType.Audio, "Audio"),
        .. project.Assets.Any(a => a.Type == AssetType.Font) ? [Shelf(project, AssetType.Font, "Fonts")] : Array.Empty<MediaShelf>(),
        .. project.Assets.Any(a => a.Type == AssetType.Lut) ? [Shelf(project, AssetType.Lut, "Looks")] : Array.Empty<MediaShelf>(),
    ];

    // The bins: each tag the assets have, with how many have it.
    public static IReadOnlyList<MediaShelf> Bins(Project project) =>
        AssetTags.All(project).Select(t => new MediaShelf("tag:" + t.Tag, t.Tag, t.Count, Tag: t.Tag)).ToList();

    // The shelf with that key, or All media when there's none (a bin whose last file lost its tag).
    public static MediaShelf Find(Project project, string? key) =>
        Library(project).Concat(Bins(project)).FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Library(project)[0];

    // The shelf's media in the sort's order, those that have every word of the search in their id,
    // their file's name or one of their tags (any case).
    public static IReadOnlyList<Asset> Shown(Project project, MediaShelf shelf, string? search, MediaSort sort)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var assets = project.Assets
            .Where(a => shelf.Kind is null || a.Type == shelf.Kind)
            .Where(a => shelf.Tag is null || AssetTags.Has(a, shelf.Tag))
            .Where(a => words.All(w => Mentions(a, w)));
        return sort switch
        {
            MediaSort.Oldest => assets.ToList(),
            MediaSort.Name => assets.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => assets.Reverse().ToList(),
        };
    }

    public static string SortName(MediaSort sort) => sort switch
    {
        MediaSort.Oldest => "Oldest first",
        MediaSort.Name => "Name",
        _ => "Recently added",
    };

    public static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    // A card's size or length: a picture's pixels, a video's or sound's length, a font's family, or
    // "look" for a LUT.
    public static string Meta(Asset asset) => asset.Type switch
    {
        AssetType.Video or AssetType.Audio => asset.Duration is { } seconds ? Timecode.Length(seconds) : "",
        AssetType.Image => asset.Width is { } w && asset.Height is { } h ? $"{w}×{h}" : "",
        AssetType.Lut => "look",
        _ => asset.FontFamily ?? "",
    };

    // How many of this project's layers show or play the asset.
    public static int Uses(Project project, string assetId) => ProjectChecker.Users(project, assetId).Count();

    public static string Use(int uses, bool missing) => missing ? "missing" : uses == 0 ? "not used" : $"used {uses}×";

    // One file's details. A file missing from the assets folder has no size.
    public static MediaDetails Details(Project project, string directory, Asset asset)
    {
        var users = ProjectChecker.Users(project, asset.Id).ToList();
        var path = ProjectStore.IsAssetFile(asset.File) ? ProjectStore.AssetPath(directory, asset) : null;
        var file = path is null ? null : new FileInfo(path);
        var usedBy = users.Count switch
        {
            0 => "No layer yet",
            <= 3 => $"{Layers(users.Count)}: {string.Join(", ", users)}",
            _ => $"{Layers(users.Count)}: {string.Join(", ", users.Take(3))} and {users.Count - 3} more",
        };
        return new MediaDetails(
            Path.GetFileName(asset.File),
            Summary(asset),
            usedBy,
            file is { Exists: true } ? Bytes(file.Length) : "Missing",
            path is null ? asset.File : ProjectList.Shorten(path),
            path,
            asset.Tags ?? []);
    }

    // Several files: how many of each kind, and the tags any of them has.
    public static MediaDetails Details(IReadOnlyList<Asset> assets)
    {
        var kinds = assets.GroupBy(a => a.Type).OrderBy(g => Array.IndexOf(KindOrder, g.Key)).Select(g => Kinds(g.Key, g.Count()));
        var tags = assets.SelectMany(a => a.Tags ?? []).Distinct(AssetTags.Comparer).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new MediaDetails($"{assets.Count} files", string.Join(" · ", kinds), null, null, null, null, tags);
    }

    // "PNG image · 220×240", "MP4 video · 1280×720 · 30 fps · 0:06", "WAV sound · 0:00.6", "TTF font · Inter",
    // "CUBE look".
    public static string Summary(Asset asset)
    {
        var extension = Path.GetExtension(asset.File).TrimStart('.').ToUpperInvariant();
        var size = asset.Width is { } w && asset.Height is { } h ? $"{w}×{h}" : null;
        var length = asset.Duration is { } d ? Timecode.Length(d) : null;
        string?[] parts = asset.Type switch
        {
            AssetType.Video => [$"{extension} video", size, asset.FrameRate is null ? null : $"{VideoTiming.Fps(asset).ToString("0.##", CultureInfo.InvariantCulture)} fps", length],
            AssetType.Image => [$"{extension} image", size],
            AssetType.Audio => [$"{extension} sound", length],
            AssetType.Lut => [$"{extension} look"],
            _ => [$"{extension} font", asset.FontFamily],
        };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1048576 => $"{Math.Max(1, Math.Round(bytes / 1024.0)).ToString(CultureInfo.InvariantCulture)} KB",
        _ => (bytes / 1048576.0).ToString(bytes < 10 * 1048576L ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB",
    };

    private static MediaShelf Shelf(Project project, AssetType kind, string name) =>
        new(EnumNames.ToName(kind), name, project.Assets.Count(a => a.Type == kind), Kind: kind);

    private static bool Mentions(Asset asset, string word) =>
        asset.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(asset.File).Contains(word, StringComparison.OrdinalIgnoreCase)
        || (asset.Tags ?? []).Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string Layers(int count) => count == 1 ? "1 layer" : $"{count} layers";

    private static string Kinds(AssetType kind, int count) => kind switch
    {
        AssetType.Video => count == 1 ? "1 video" : $"{count} videos",
        AssetType.Image => count == 1 ? "1 image" : $"{count} images",
        AssetType.Audio => count == 1 ? "1 sound" : $"{count} sounds",
        AssetType.Lut => count == 1 ? "1 look" : $"{count} looks",
        _ => count == 1 ? "1 font" : $"{count} fonts",
    };
}
