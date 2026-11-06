// SPDX-License-Identifier: MIT

using System.Text;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public static class ProjectStore
{
    public const string ProjectFileName = "project.json";
    public const string AssetsFolder = "assets";
    public const string RendersFolder = "renders";
    public const string ExportsFolder = "exports";
    public const string BackupsFolder = "backups";

    private const int SaveAttempts = 5;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool IsProjectDirectory(string directory) =>
        File.Exists(Path.Combine(directory, ProjectFileName));

    public static Project Load(string directory) => Load(directory, out _);

    // savedVersion is the format the file was written in; an older one is migrated as it's read.
    public static Project Load(string directory, out int savedVersion)
    {
        var path = Path.Combine(directory, ProjectFileName);
        if (!File.Exists(path))
        {
            throw new ZeyoCutException($"no {ProjectFileName} in '{directory}'");
        }

        return ProjectJson.Deserialize(ProjectMigrations.Migrate(File.ReadAllText(path, Encoding.UTF8), out savedVersion));
    }

    public static void Save(Project project, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ProjectFileName);
        var temp = path + ".tmp";

        // On disk before the rename, so a crash or power cut leaves the old file or the new one.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Utf8NoBom.GetBytes(ProjectJson.Serialize(project)));
            stream.Flush(flushToDisk: true);
        }

        // A virus scanner or the search indexer can hold project.json for a moment; try again briefly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < SaveAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    public static void EnsureFolders(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, AssetsFolder));
        Directory.CreateDirectory(Path.Combine(directory, RendersFolder));
        Directory.CreateDirectory(Path.Combine(directory, ExportsFolder));
    }

    // An asset's file is always assets/<name> inside its project (or rig or template) folder. A
    // project.json from someone else can't point it anywhere else, to read or write files there.
    public static bool IsAssetFile(string? file) => IsFileIn(AssetsFolder, file);

    // True when file is folder/<name>: one plain file name, with no further folders, no '.' or '..',
    // no drive and no characters a file name can't have.
    public static bool IsFileIn(string folder, string? file)
    {
        var parts = (file ?? "").Replace('\\', '/').Split('/');
        return parts.Length == 2 && parts[0] == folder && IsPlainFileName(parts[1]);
    }

    public static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(['/', '\\', ':']) < 0;

    public static string AssetPath(string directory, Asset asset) =>
        IsAssetFile(asset.File)
            ? Path.GetFullPath(Path.Combine(directory, asset.File.Replace('/', Path.DirectorySeparatorChar)))
            : throw new ZeyoCutException($"asset '{asset.Id}': file '{asset.File}' must be a file in the {AssetsFolder} folder");
}
