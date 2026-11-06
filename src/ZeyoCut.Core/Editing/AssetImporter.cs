// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public sealed record AssetInfo(
    int? Width = null,
    int? Height = null,
    double? Duration = null,
    string? FontFamily = null,
    string? FrameRate = null,
    int? FrameCount = null,
    bool? HasAudio = null,
    string? Codec = null);

public interface IAssetProbe
{
    AssetInfo Probe(string path, AssetType type);
}

public static class AssetImporter
{
    private static readonly Dictionary<string, AssetType> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = AssetType.Image,
        [".jpg"] = AssetType.Image,
        [".jpeg"] = AssetType.Image,
        [".webp"] = AssetType.Image,
        [".bmp"] = AssetType.Image,
        [".wav"] = AssetType.Audio,
        [".mp3"] = AssetType.Audio,
        [".m4a"] = AssetType.Audio,
        [".aac"] = AssetType.Audio,
        [".ogg"] = AssetType.Audio,
        [".opus"] = AssetType.Audio,
        [".flac"] = AssetType.Audio,
        [".ttf"] = AssetType.Font,
        [".otf"] = AssetType.Font,
        [".ttc"] = AssetType.Font,
        [".mp4"] = AssetType.Video,
        [".mov"] = AssetType.Video,
        [".m4v"] = AssetType.Video,
        [".mkv"] = AssetType.Video,
        [".webm"] = AssetType.Video,
        [".avi"] = AssetType.Video,
    };

    public static string SupportedExtensions => string.Join(", ", Extensions.Where(e => e.Value != AssetType.Video).Select(e => e.Key));

    public static string VideoExtensions => string.Join(", ", Extensions.Where(e => e.Value == AssetType.Video).Select(e => e.Key));

    public static string CheckSource(string sourcePath, AssetType? expected)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ZeyoCutException("path is required");
        }

        var source = Path.GetFullPath(sourcePath.Trim().Trim('"'));
        if (!File.Exists(source))
        {
            throw new ZeyoCutException($"file not found: {source}");
        }

        var type = DetectType(source);
        if (expected == AssetType.Video && type != AssetType.Video)
        {
            throw new ZeyoCutException($"'{Path.GetFileName(source)}' is not a video file; video types: {VideoExtensions}. Use import_asset for images, audio and fonts");
        }

        if (expected != AssetType.Video && type == AssetType.Video)
        {
            throw new ZeyoCutException($"'{Path.GetFileName(source)}' is a video; use import_video, which also fixes variable frame rate and rotation");
        }

        if (type is null)
        {
            throw new ZeyoCutException($"unsupported file type '{Path.GetExtension(source)}'; supported: {SupportedExtensions}, and video ({VideoExtensions}) with import_video");
        }

        return source;
    }

    public static AssetType? DetectType(string path) =>
        Extensions.TryGetValue(Path.GetExtension(path), out var type) ? type : null;

    public static void CheckNewId(Project project, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            Ids.EnsureValid(id, "asset");
            if (project.FindAsset(id) is not null)
            {
                throw new ZeyoCutException($"asset id '{id}' already exists; existing assets: {Ids.List(project.Assets.Select(a => a.Id))}");
            }
        }
    }

    public static Asset Import(Project project, string projectDirectory, string sourcePath, string? id, IAssetProbe probe, AssetType? expected = null)
    {
        var source = CheckSource(sourcePath, expected);
        var type = DetectType(source)!.Value;
        CheckNewId(project, id);

        AssetInfo info;
        try
        {
            info = probe.Probe(source, type);
        }
        catch (ZeyoCutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ZeyoCutException($"could not read {EnumNames.ToName(type)} file '{source}': {ex.Message}", ex);
        }

        var assetsDir = Path.GetFullPath(Path.Combine(projectDirectory, ProjectStore.AssetsFolder));
        Directory.CreateDirectory(assetsDir);
        var destination = ChooseDestination(assetsDir, source);
        if (!string.Equals(destination, source, StringComparison.OrdinalIgnoreCase) && !File.Exists(destination))
        {
            File.Copy(source, destination);
        }

        var assetId = !string.IsNullOrWhiteSpace(id)
            ? id
            : Ids.Unique(Ids.Slug(Path.GetFileNameWithoutExtension(source), EnumNames.ToName(type)), c => project.FindAsset(c) is not null);

        var asset = new Asset
        {
            Id = assetId,
            Type = type,
            File = $"{ProjectStore.AssetsFolder}/{Path.GetFileName(destination)}",
            Width = info.Width,
            Height = info.Height,
            Duration = info.Duration,
            FontFamily = info.FontFamily,
            FrameRate = info.FrameRate,
            FrameCount = info.FrameCount,
            HasAudio = info.HasAudio,
            Codec = info.Codec,
        };
        project.Assets.Add(asset);
        return asset;
    }

    // Brings a library file into the project. An asset of the same type whose file has the same
    // content is reused; otherwise the file is copied into assets/ under an id based on preferredId.
    public static Asset Adopt(Project project, string projectDirectory, Asset metadata, string sourceFile, string preferredId)
    {
        if (!File.Exists(sourceFile))
        {
            throw new ZeyoCutException($"library file is missing: {sourceFile}");
        }

        foreach (var existing in project.Assets.Where(a => a.Type == metadata.Type))
        {
            var path = ProjectStore.AssetPath(projectDirectory, existing);
            if (File.Exists(path) && SameContent(path, sourceFile))
            {
                return existing;
            }
        }

        var assetsDir = Path.GetFullPath(Path.Combine(projectDirectory, ProjectStore.AssetsFolder));
        Directory.CreateDirectory(assetsDir);
        var destination = ChooseDestination(assetsDir, sourceFile);
        if (!File.Exists(destination))
        {
            File.Copy(sourceFile, destination);
        }

        var asset = ProjectJson.Copy(metadata);
        asset.Id = Ids.Unique(Ids.Slug(preferredId, EnumNames.ToName(metadata.Type)), c => project.FindAsset(c) is not null);
        asset.File = $"{ProjectStore.AssetsFolder}/{Path.GetFileName(destination)}";
        asset.OriginalFile = null;
        project.Assets.Add(asset);
        return asset;
    }

    // A free path in the assets folder for a file the editor creates from 'source' (e.g. a transcode).
    public static string NewAssetPath(string projectDirectory, string source, string extension)
    {
        var assetsDir = Path.GetFullPath(Path.Combine(projectDirectory, ProjectStore.AssetsFolder));
        Directory.CreateDirectory(assetsDir);
        var stem = Path.GetFileNameWithoutExtension(SafeFileName(Path.GetFileName(source)));
        var candidate = Path.Combine(assetsDir, stem + extension);
        bool Taken(string path) => File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".partial" + extension));
        for (var n = 2; Taken(candidate); n++)
        {
            candidate = Path.Combine(assetsDir, $"{stem}_{n}{extension}");
        }

        return candidate;
    }

    private static string ChooseDestination(string assetsDir, string source)
    {
        if (string.Equals(Path.GetDirectoryName(source), assetsDir, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var name = SafeFileName(Path.GetFileName(source));
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = Path.Combine(assetsDir, name);
        for (var n = 2; File.Exists(candidate) && !SameContent(candidate, source); n++)
        {
            candidate = Path.Combine(assetsDir, $"{stem}_{n}{extension}");
        }

        return candidate;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "asset" : cleaned;
    }

    private static bool SameContent(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
        {
            return false;
        }

        using var streamA = infoA.OpenRead();
        using var streamB = infoB.OpenRead();
        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            var readA = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            var readB = streamB.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
            if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
            {
                return false;
            }

            if (readA == 0)
            {
                return true;
            }
        }
    }
}
