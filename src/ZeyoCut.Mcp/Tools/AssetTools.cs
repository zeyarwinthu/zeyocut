// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class AssetTools
{
    [McpServerTool(Name = "import_asset")]
    [Description("Copy an image (png/jpg/webp/bmp), audio (wav/mp3/m4a/aac/ogg/opus/flac) or font (ttf/otf/ttc) file into the project's assets folder and register it. Returns the asset id to use in layers. Video files go through import_video.")]
    public static string ImportAsset(
        EditorState state,
        [Description("Absolute path of the file to import, in an import folder, the library or the projects folder.")] string path,
        [Description("Optional asset id (letters, digits, _ or -). Default: derived from the file name.")] string? id = null) => Guard(() =>
    {
        var session = state.Session;
        var probe = state.AssetProbe;
        var source = AllowedFolders.Readable(state.Settings, path);
        var asset = session.Mutate($"import_asset {Path.GetFileName(source)}", p => AssetImporter.Import(p, session.Directory, source, id, probe));
        return $"imported {Describe(asset)}";
    });

    [McpServerTool(Name = "import_folder")]
    [Description("Import every image, audio, font and video file in a folder (not its subfolders) whose name matches pattern, in order: by name with numbers in order (panel_2 before panel_10), or by date, oldest first. Images, audio, fonts and videos that need no normalizing are imported at once, as one undo step. Videos that do (variable frame rate, rotated, HDR, ...) are normalized one after another in one background job (import_N) and added as each finishes; poll get_job_status. Asset ids come from the file names. The reply lists the assets in order, the files of other types it skipped and any file it couldn't read. At most 500 files.")]
    public static string ImportFolder(
        EditorState state,
        [Description("Absolute path of the folder, in an import folder, the library or the projects folder.")] string path,
        [Description("File name pattern with * and ?, in any case, such as *.png or panel_*. Default * (every file).")] string? pattern = null,
        [Description("name (default): by file name, numbers in order; date: by when each file last changed, oldest first.")] string? sortBy = null) => Guard(() =>
    {
        var session = state.Session;
        var listing = FolderImport.List(AllowedFolders.Readable(state.Settings, path), pattern, sortBy);
        var failed = new List<string>();
        var infos = new Dictionary<string, AssetInfo>(StringComparer.OrdinalIgnoreCase);
        var queued = new List<(string Source, VideoProbeResult Probe, IReadOnlyList<string> Reasons)>();
        var ffmpeg = listing.Files.Any(f => AssetImporter.DetectType(f) == AssetType.Video) ? state.RequireFfmpeg() : null;
        var probe = state.AssetProbe;
        foreach (var file in listing.Files)
        {
            var type = AssetImporter.DetectType(file)!.Value;
            try
            {
                AllowedFolders.Readable(state.Settings, file);
                if (type != AssetType.Video)
                {
                    infos[file] = probe.Probe(file, type);
                    continue;
                }

                var video = VideoProbe.Probe(ffmpeg!.Ffprobe, file);
                var reasons = video.NormalizeReasons();
                if (reasons.Count == 0)
                {
                    infos[file] = video.ToAssetInfo();
                }
                else
                {
                    queued.Add((file, video, reasons));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (infos.Count == 0 && queued.Count == 0)
        {
            throw new ZeyoCutException($"nothing was imported from {listing.Folder}; no file could be read:\n- {string.Join("\n- ", failed)}");
        }

        var known = new KnownProbe(infos);
        var now = listing.Files.Where(infos.ContainsKey).ToList();
        var imported = now.Count == 0 ? [] : session.Mutate($"import_folder {Path.GetFileName(listing.Folder)} ({now.Count} file{(now.Count == 1 ? "" : "s")})", p =>
        {
            var added = new List<Asset>();
            foreach (var file in now)
            {
                try
                {
                    added.Add(AssetImporter.Import(p, session.Directory, file, null, known, AssetImporter.DetectType(file) == AssetType.Video ? AssetType.Video : null));
                }
                catch (Exception ex) when (ex is ZeyoCutException or IOException)
                {
                    failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            return added;
        });

        var text = new StringBuilder();
        var how = $"{(listing.Pattern == "*" ? "" : $"'{listing.Pattern}', ")}by {EnumNames.ToName(listing.Sort)}";
        if (imported.Count > 0)
        {
            text.Append($"imported {imported.Count} file{(imported.Count == 1 ? "" : "s")} from {listing.Folder} ({how}), in this order:\n");
            text.Append(string.Join("\n", imported.Select(a => "- " + Describe(a)))).Append('\n');
            if (state.StartProxies(session, imported.Select(a => a.Id).ToList()) is { Length: > 0 } proxies)
            {
                text.Append(proxies.TrimStart('\n')).Append('\n');
            }
        }

        if (listing.Skipped.Count > 0)
        {
            text.Append($"skipped {listing.Skipped.Count} file{(listing.Skipped.Count == 1 ? "" : "s")} of other types: {string.Join(", ", listing.Skipped.Take(10))}{(listing.Skipped.Count > 10 ? ", …" : "")}\n");
        }

        if (failed.Count > 0)
        {
            text.Append($"couldn't import {failed.Count} file{(failed.Count == 1 ? "" : "s")}:\n- {string.Join("\n- ", failed)}\n");
        }

        if (queued.Count > 0)
        {
            var job = StartNormalizing(state, session, ffmpeg!, queued, listing.Folder);
            text.Append($"{queued.Count} video{(queued.Count == 1 ? " needs" : "s need")} normalizing, one after another in the background as {job.Id}: ")
                .Append(string.Join("; ", queued.Select(q => $"{Path.GetFileName(q.Source)} ({string.Join(", ", q.Reasons)})")))
                .Append($". Each is added as an asset when it is done; poll get_job_status with jobId \"{job.Id}\"\n");
        }

        return text.ToString().TrimEnd('\n');
    });

    // import_folder's job: normalizes the videos in order and adds each as an asset when it is done.
    private static BackgroundJob StartNormalizing(EditorState state, ProjectSession session, FfmpegPaths ffmpeg, List<(string Source, VideoProbeResult Probe, IReadOnlyList<string> Reasons)> videos, string folder)
    {
        var directory = session.Directory;
        return state.Jobs.Start("import", $"import_folder: normalize {videos.Count} video{(videos.Count == 1 ? "" : "s")} from {Path.GetFileName(folder)}", (job, ct) =>
        {
            var done = new List<string>();
            var added = new List<string>();
            var problems = new List<string>();
            for (var i = 0; i < videos.Count; i++)
            {
                var (source, probe, reasons) = videos[i];
                var index = i;
                job.Report((double)i / videos.Count, $"normalizing {Path.GetFileName(source)} ({i + 1} of {videos.Count})");
                var output = AssetImporter.NewAssetPath(directory, source, ".mp4");
                try
                {
                    var info = VideoNormalizer.Normalize(ffmpeg, source, probe, output, p => job.Report((index + p * 0.98) / videos.Count), ct).ToAssetInfo();
                    var asset = session.Mutate($"import_folder {Path.GetFileName(source)}", p =>
                    {
                        var added = AssetImporter.Import(p, directory, output, null, new AssetProbe(video: _ => info), AssetType.Video);
                        added.OriginalFile = source;
                        added.Normalized = string.Join("; ", reasons);
                        return added;
                    });
                    done.Add("- " + Describe(asset));
                    added.Add(asset.Id);
                }
                catch (Exception ex) when (ex is ZeyoCutException or IOException)
                {
                    if (File.Exists(output))
                    {
                        File.Delete(output);
                    }

                    problems.Add($"- {Path.GetFileName(source)}: {ex.Message}");
                }
            }

            if (done.Count == 0)
            {
                throw new ZeyoCutException($"no video could be normalized:\n{string.Join("\n", problems)}");
            }

            return $"imported {done.Count} normalized video{(done.Count == 1 ? "" : "s")}:\n{string.Join("\n", done)}"
                + (problems.Count > 0 ? $"\ncouldn't normalize {problems.Count}:\n{string.Join("\n", problems)}" : "")
                + state.StartProxies(session, added);
        });
    }

    // The file information import_folder read before importing.
    private sealed class KnownProbe(IReadOnlyDictionary<string, AssetInfo> infos) : IAssetProbe
    {
        public AssetInfo Probe(string path, AssetType type) => infos[path];
    }

    [McpServerTool(Name = "list_assets")]
    [Description("List the project's assets with their ids, types, sizes or durations, and which layers use them.")]
    public static string ListAssets(EditorState state) => Guard(() =>
    {
        var project = state.Session.Snapshot();
        if (project.Assets.Count == 0)
        {
            return "(no assets; use import_asset)";
        }

        var builder = new StringBuilder();
        foreach (var asset in project.Assets)
        {
            var users = project.Layers.Where(l => Uses(l, asset.Id)).Select(l => l.Id).ToList();
            builder.AppendLine($"- {Describe(asset)}; used by: {Ids.List(users)}");
        }

        return builder.ToString().TrimEnd();
    });

    public static string Describe(Asset asset)
    {
        var details = asset.Type switch
        {
            AssetType.Image => $"{asset.Width}x{asset.Height} px",
            AssetType.Audio => $"{Num(asset.Duration ?? 0)}s",
            AssetType.Video => $"{asset.Width}x{asset.Height} @ {asset.FrameRate} fps, {Num(asset.Duration ?? 0)}s, {asset.FrameCount} frames, {asset.Codec}, {(asset.HasAudio == true ? "with sound" : "no sound")}",
            _ => $"family \"{asset.FontFamily}\"",
        };
        var normalized = asset.Normalized is null ? "" : $"; normalized ({asset.Normalized})";
        return $"{asset.Id} [{EnumNames.ToName(asset.Type)}] {asset.File} ({details}{normalized})";
    }

    private static bool Uses(Layer layer, string assetId) => layer switch
    {
        ImageLayer image => image.Drawings.ContainsValue(assetId),
        TextLayer text => text.FontAsset == assetId,
        AudioLayer audio => audio.Asset == assetId,
        VideoLayer video => video.Asset == assetId,
        _ => false,
    } || layer is VisualLayer { Mask.Asset: { } mask } && mask == assetId;
}
