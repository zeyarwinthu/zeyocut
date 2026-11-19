// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor;

// A kind of file in a project's cache folder that is made again when it's needed, and the color its
// part of the Render queue's cache bar has.
public sealed record CacheKind(string Folder, string Name, string Color);

public sealed record CacheSize(CacheKind Kind, long Bytes);

// A kind's color and size under the Render queue's cache bar.
public sealed record CacheLegendItem(string Color, string Text);

// What Clear did: how much it freed, and the files it left because a program had them open.
public sealed record CacheCleared(long Freed, int Kept);

// The projects' cache folders as the Render queue shows them: the export chunks, the preview's sound,
// the media and project pictures and the waveforms, each made again when it's needed. Clear deletes
// only those; the rest of a project (its files, exports, backups, tracks, proxies) stays.
public static class CacheFolders
{
    public static readonly IReadOnlyList<CacheKind> Kinds =
    [
        new(Exporter.ChunksFolder, "Export chunks", "#5B8CFF"),
        new(PreviewAudio.Folder, "Preview sound", "#A98BFF"),
        new(MediaThumbnails.Folder, "Pictures", "#3DD68C"),
        new(MediaWaveforms.Folder, "Waveforms", "#F0A94B"),
    ];

    // Each kind's size in the projects' cache folders.
    public static IReadOnlyList<CacheSize> Sizes(IEnumerable<string> projects)
    {
        var folders = projects.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Kinds.Select(kind => new CacheSize(kind, folders.Sum(project => Files(project, kind).Sum(Length)))).ToList();
    }

    // Deletes the kinds' files in the projects' cache folders; a file a program has open is left.
    public static CacheCleared Clear(IEnumerable<string> projects)
    {
        long freed = 0;
        var kept = 0;
        foreach (var project in projects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var kind in Kinds)
            {
                foreach (var file in Files(project, kind))
                {
                    var length = Length(file);
                    try
                    {
                        file.Delete();
                        freed += length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        kept++;
                    }
                }
            }
        }

        return new CacheCleared(freed, kept);
    }

    public static string Folder(string project, CacheKind kind) => Path.Combine(project, ProjectStore.CacheFolder, kind.Folder);

    private static IEnumerable<FileInfo> Files(string project, CacheKind kind)
    {
        var folder = new DirectoryInfo(Folder(project, kind));
        if (!folder.Exists)
        {
            return [];
        }

        try
        {
            return folder.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long Length(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
