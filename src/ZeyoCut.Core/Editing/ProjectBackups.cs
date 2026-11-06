// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// A copy of project.json in the project's backups folder. Before is the edit that was saved over
// it; Format is set on the copy kept when a file from an older ZeyoCut was updated.
public sealed record ProjectBackup(string Name, string Path, DateTime Time, string? Before, int? Format);

// Before every save the project.json being replaced is copied to backups\, as
// project-<time> before <edit>.json, and only the newest Kept of those stay. The copies kept when
// an older format is updated (project-v<format>-<time>.json) are never removed.
public static partial class ProjectBackups
{
    public const int Kept = 20;

    private const int CopyAttempts = 5;
    private const int LabelLength = 80;

    [GeneratedRegex(@"^project-(?<time>\d{8}-\d{6}-\d{3})(?:-(?<n>\d+))? before (?<before>.+)$")]
    private static partial Regex RollingName();

    [GeneratedRegex(@"^project-v(?<format>\d+)-(?<time>\d{8}-\d{6})(?:-(?<n>\d+))?$")]
    private static partial Regex OriginalName();

    public static string Folder(string directory) => Path.Combine(directory, ProjectStore.BackupsFolder);

    // Copies project.json as it is now before 'label' is saved over it. Returns null when there's
    // no project.json yet.
    public static string? Add(string directory, string label)
    {
        var source = Path.Combine(directory, ProjectStore.ProjectFileName);
        if (!File.Exists(source))
        {
            return null;
        }

        var folder = Folder(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        // A second save in the same millisecond is -2, and so on, so List keeps them in order.
        var stem = $"project-{stamp}";
        for (var n = 2; System.IO.Directory.Exists(folder) && System.IO.Directory.EnumerateFiles(folder, stem + " before *").Any(); n++)
        {
            stem = $"project-{stamp}-{n}";
        }

        var target = Copy(source, folder, stem, $" before {Clean(label)}.json");
        Prune(folder);
        return target;
    }

    // Copies project.json, as an older ZeyoCut saved it, to backups\project-v<version>-<time>.json.
    public static string KeepOriginal(string directory, int savedVersion)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Copy(Path.Combine(directory, ProjectStore.ProjectFileName), Folder(directory), $"project-v{savedVersion}-{stamp}", ".json");
    }

    // Newest first.
    public static IReadOnlyList<ProjectBackup> List(string directory)
    {
        var folder = Folder(directory);
        if (!System.IO.Directory.Exists(folder))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(folder, "project-*.json")
            .Select(Parse)
            .OfType<(ProjectBackup Backup, int N)>()
            .OrderByDescending(b => b.Backup.Time)
            .ThenByDescending(b => b.N)
            .Select(b => b.Backup)
            .ToList();
    }

    // By its name as List gives it, with or without .json, or by the start of it (its time) when
    // that's enough to tell it apart.
    public static ProjectBackup Find(string directory, string name)
    {
        var backups = List(directory);
        if (backups.Count == 0)
        {
            throw new ZeyoCutException("this project has no backups yet; one is kept before every save");
        }

        var key = name.Trim();
        if (key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^5];
        }

        if (backups.FirstOrDefault(b => string.Equals(b.Name, key, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }

        var matches = key.Length == 0
            ? []
            : backups.Where(b => b.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                || b.Name.StartsWith("project-" + key, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ZeyoCutException($"no backup '{name}'; the newest are: {Ids.List(backups.Take(5).Select(b => b.Name))}"),
            _ => throw new ZeyoCutException($"'{name}' fits {matches.Count} backups ({Ids.List(matches.Take(5).Select(b => b.Name))}); give more of its name"),
        };
    }

    // Reads a backup the way open_project reads project.json, updating an older format.
    public static Project Load(ProjectBackup backup)
    {
        try
        {
            return ProjectJson.Deserialize(ProjectMigrations.Migrate(File.ReadAllText(backup.Path, Encoding.UTF8), out _));
        }
        catch (ZeyoCutException ex)
        {
            throw new ZeyoCutException($"backup {backup.Name} can't be read: {ex.Message}", ex);
        }
    }

    private static (ProjectBackup, int)? Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (RollingName().Match(name) is { Success: true } rolling
            && DateTime.TryParseExact(rolling.Groups["time"].Value, "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return (new ProjectBackup(name, path, time, rolling.Groups["before"].Value, null), Number(rolling));
        }

        if (OriginalName().Match(name) is { Success: true } original
            && DateTime.TryParseExact(original.Groups["time"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
            && int.TryParse(original.Groups["format"].Value, CultureInfo.InvariantCulture, out var format))
        {
            return (new ProjectBackup(name, path, time, null, format), Number(original));
        }

        return null;
    }

    private static int Number(Match match) =>
        match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;

    // Copies to a free name: <stem><tail>, else <stem>-2<tail>, and so on. The copy is written
    // under a .tmp name first, so a half-written one never looks like a backup.
    private static string Copy(string source, string folder, string stem, string tail)
    {
        var target = Path.Combine(folder, stem + tail);
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(folder, $"{stem}-{n}{tail}");
        }

        var temp = target + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, target);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= CopyAttempts)
                {
                    throw new ZeyoCutException($"couldn't keep a backup of {ProjectStore.ProjectFileName} in {folder}, so nothing was saved: {ex.Message}", ex);
                }

                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static void Prune(string folder)
    {
        var rolling = System.IO.Directory.EnumerateFiles(folder, "project-*.json")
            .Select(Parse)
            .OfType<(ProjectBackup Backup, int N)>()
            .Where(b => b.Backup.Format is null)
            .OrderByDescending(b => b.Backup.Time)
            .ThenByDescending(b => b.N)
            .Skip(Kept);
        foreach (var (backup, _) in rolling)
        {
            try
            {
                File.Delete(backup.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next save to remove.
            }
        }
    }

    // The edit's label as a file name: characters Windows doesn't allow become '_'.
    private static string Clean(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(label.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length > LabelLength)
        {
            cleaned = cleaned[..LabelLength];
        }

        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "edit" : cleaned;
    }
}
