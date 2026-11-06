// SPDX-License-Identifier: MIT

using ZeyoCut.Core;

namespace ZeyoCut.Mcp;

// The folders tools may use. A project lives inside the projects folder; a file to import or read
// (media, .srt, a script) comes from the projects folder, the library or an import folder
// (ImportFolders). Anything else, also when a symbolic link or junction leads there, is refused with
// an error that names the setting to change. Outputs never need this: they go into a project.
public static class AllowedFolders
{
    // The default import folders: the user's Desktop, Documents, Downloads, Music, Pictures and Videos.
    public static IReadOnlyList<string> DefaultImportFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        }.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The full path of a file or folder to read, after checking it is in a folder ZeyoCut may read.
    public static string Readable(Settings settings, string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var folders = new[] { settings.ProjectsRoot, settings.LibraryPath ?? Settings.DefaultLibraryPath }.Concat(settings.ImportFolderList).ToList();
        if (Check(full, folders) is not { } real)
        {
            return full;
        }

        var through = real.Equals(full, StringComparison.OrdinalIgnoreCase) ? "" : $" (it leads to {real})";
        throw new ZeyoCutException($"{full}{through} is outside the folders ZeyoCut may read: the projects folder, the library and the import folders ({string.Join("; ", folders)}). "
            + $"To import from there, add its folder to ImportFolders in appsettings.json or to {Settings.ImportFoldersVariable} (folders separated by ;)");
    }

    // The full path of a project folder, after checking it is inside the projects folder.
    public static string Project(Settings settings, string directory)
    {
        var full = Path.GetFullPath(directory.Trim().Trim('"'));
        var real = Check(full, [settings.ProjectsRoot]);
        if (real is null && !SameFolder(Real(full), Real(settings.ProjectsRoot)))
        {
            return full;
        }

        real ??= Real(full);
        var through = real.Equals(full, StringComparison.OrdinalIgnoreCase) ? "" : $" (it leads to {real})";
        throw new ZeyoCutException($"{full}{through} is not inside the projects folder {settings.ProjectsRoot}; projects live there. "
            + $"To keep them somewhere else, set ProjectsRoot in appsettings.json or {Settings.ProjectsRootVariable}");
    }

    // Null when full is in one of the folders, otherwise where it really is. The path is first compared
    // as text, without touching the disk, so a path such as \\server\share\x is refused without
    // Windows connecting to that server (and sending it the user's sign-in). Only a path that is in a
    // folder by its text is then followed through its links.
    private static string? Check(string full, IReadOnlyList<string> folders)
    {
        var real = folders.Select(Real).ToList();
        if (!folders.Concat(real).Any(folder => IsInside(full, Path.GetFullPath(folder))))
        {
            return full;
        }

        var resolved = Real(full);
        return real.Any(folder => IsInside(resolved, folder)) ? null : resolved;
    }

    private static bool SameFolder(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // The path with each symbolic link or junction on it replaced by where it leads, so a link inside
    // an allowed folder can't reach outside it. Parts that don't exist (yet) stay as they are.
    public static string Real(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }

        return current;
    }

    public static bool IsInside(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
