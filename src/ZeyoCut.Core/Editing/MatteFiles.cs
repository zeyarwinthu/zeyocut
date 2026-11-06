// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// An AI matte a layer (or a subject cutout) uses whose file isn't computed yet.
public sealed record PendingMatte(string LayerId, string AssetId, MatteMethod Method, string Path, bool Cutout = false, bool Captions = false)
{
    // The tool that starts its matte job again.
    public string Tool => Captions ? "generate_captions" : Cutout ? "text_behind_subject" : "remove_background";
}

// Where computed mattes live: next to the asset as <name>.matte-<method>-<hash>.<png|mkv> (gray:
// white keeps, black removes). The hash covers the method's settings, the model for the AI methods
// and the asset file's size and time, so a changed setting, model or file gets a new matte.
public static class MatteFiles
{
    public const string ImageModel = "birefnet_lite";
    public const string VideoModel = "rvm_mobilenetv3";

    // Video mattes are computed at most this size on the long side.
    public const int MaxVideoMatteSide = 1920;

    public static bool IsAi(MatteMethod method) => method is MatteMethod.AiImage or MatteMethod.AiVideo;

    public static string? ModelId(MatteMethod method) => method switch
    {
        MatteMethod.AiImage => ImageModel,
        MatteMethod.AiVideo => VideoModel,
        _ => null,
    };

    public static string CachePath(string projectDirectory, Asset asset, Matte matte)
    {
        var file = ProjectStore.AssetPath(projectDirectory, asset);
        var info = new FileInfo(file);
        var stamp = info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "missing";
        var parts = new List<string> { EnumNames.ToName(matte.Method), matte.Color ?? "auto", (matte.Tolerance ?? 0).ToString("R", CultureInfo.InvariantCulture), stamp };
        if (ModelId(matte.Method) is { } model)
        {
            parts.Add(model);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))))[..8];
        var extension = matte.Method == MatteMethod.AiVideo ? ".mkv" : ".png";
        var name = $"{System.IO.Path.GetFileNameWithoutExtension(file)}.matte-{EnumNames.ToName(matte.Method).ToLowerInvariant()}-{hash}{extension}";
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file)!, name);
    }

    // Project-relative form of a cache path, for replies.
    public static string Relative(string projectDirectory, string path) =>
        System.IO.Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');

    // The size a video matte is computed at: the clip's size, scaled down to fit MaxVideoMatteSide.
    public static (int Width, int Height) VideoMatteSize(Asset asset)
    {
        var width = asset.Width ?? 0;
        var height = asset.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            throw new ZeyoCutException($"video asset '{asset.Id}' has no frame size");
        }

        var scale = Math.Min(1, MaxVideoMatteSide / (double)Math.Max(width, height));
        if (scale >= 1)
        {
            return (width, height);
        }

        return (Math.Max(2, (int)Math.Round(width * scale / 2) * 2), Math.Max(2, (int)Math.Round(height * scale / 2) * 2));
    }

    // The assets whose matte a layer's AI background removal needs: each drawing, or the clip; for a
    // subject cutout, those of its sources.
    public static IEnumerable<Asset> Assets(Project project, IMattable layer) => layer switch
    {
        ImageLayer image => image.Drawings.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(project.FindAsset).OfType<Asset>(),
        VideoLayer video => project.FindAsset(video.Asset) is { } asset ? [asset] : [],
        CutoutLayer cutout => cutout.Sources.Select(project.FindLayer).OfType<IMattable>().Where(l => l is not CutoutLayer)
            .SelectMany(l => Assets(project, l)).DistinctBy(a => a.Id).OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
        _ => [],
    };

    // AI mattes whose file isn't there (still computing, or the job failed). Assets whose own file
    // is missing are left out: that is reported as a missing file.
    public static List<PendingMatte> Pending(Project project, string projectDirectory)
    {
        var pending = new List<PendingMatte>();
        foreach (var layer in project.Layers.OfType<IMattable>())
        {
            if (layer.Matte is not { } matte || !IsAi(matte.Method))
            {
                continue;
            }

            foreach (var asset in Assets(project, layer))
            {
                if (!File.Exists(ProjectStore.AssetPath(projectDirectory, asset)))
                {
                    continue;
                }

                var path = CachePath(projectDirectory, asset, matte);
                if (!File.Exists(path))
                {
                    pending.Add(new PendingMatte(layer.Id, asset.Id, matte.Method, path, layer is CutoutLayer, layer is CutoutLayer { CaptionGroup: not null }));
                }
            }
        }

        return pending;
    }

    public static string Describe(PendingMatte pending) =>
        $"{(pending.Cutout ? "cutout" : "layer")} '{pending.LayerId}' ({EnumNames.ToName(pending.Method)} matte of '{pending.AssetId}')";

    // Exports draw every frame as delivered, so a background removal that isn't computed stops them.
    public static void ThrowIfPending(Project project, string projectDirectory)
    {
        var pending = Pending(project, projectDirectory);
        if (pending.Count == 0)
        {
            return;
        }

        var shown = pending.Take(5).Select(Describe).ToList();
        if (pending.Count > shown.Count)
        {
            shown.Add($"{pending.Count - shown.Count} more");
        }

        var tools = string.Join(" or ", pending.Select(p => p.Tool).Distinct());
        throw new ZeyoCutException(
            $"background removal isn't finished: {string.Join(", ", shown)}; wait for its matte job (get_job_status) or run {tools} again, then export");
    }
}
