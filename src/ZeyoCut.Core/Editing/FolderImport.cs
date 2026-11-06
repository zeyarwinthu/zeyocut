// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Editing;

public enum FolderSort
{
    Name,
    Date
}

// The files import_folder takes, in order, and the names of the matching files it can't import.
public sealed record FolderListing(string Folder, string Pattern, FolderSort Sort, IReadOnlyList<string> Files, IReadOnlyList<string> Skipped);

public static class FolderImport
{
    public const int MaxFiles = 500;

    // The files directly in 'folder' whose names match 'pattern' (* and ?, any case), sorted by
    // name with numbers in order, or by date (oldest first, then by name). Hidden and system files
    // are left out.
    public static FolderListing List(string folder, string? pattern, string? sortBy)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new ZeyoCutException("path is required");
        }

        var full = Path.GetFullPath(folder.Trim().Trim('"'));
        if (!Directory.Exists(full))
        {
            throw new ZeyoCutException(File.Exists(full) ? $"{full} is a file; import_folder takes a folder (import_asset or import_video take one file)" : $"folder not found: {full}");
        }

        var filter = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        if (filter.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ZeyoCutException($"pattern '{filter}' must be a file name pattern such as *.png or panel_*, without a folder");
        }

        var sort = sortBy is null ? FolderSort.Name : EnumNames.Parse<FolderSort>(sortBy, "sortBy");
        var options = new EnumerationOptions
        {
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var matching = Directory.EnumerateFiles(full, filter, options).ToList();
        var files = matching.Where(f => AssetImporter.DetectType(f) is not null).ToList();
        var skipped = matching.Where(f => AssetImporter.DetectType(f) is null).Select(Path.GetFileName).OrderBy(n => n, NaturalComparer.Instance).ToList();
        if (files.Count == 0)
        {
            var what = matching.Count == 0 ? $"no file in {full} matches '{filter}'" : $"no image, audio, font or video file in {full} matches '{filter}' (found only: {string.Join(", ", skipped.Take(10))}{(skipped.Count > 10 ? ", …" : "")})";
            throw new ZeyoCutException(what);
        }

        if (files.Count > MaxFiles)
        {
            throw new ZeyoCutException($"{files.Count} files in {full} match '{filter}', more than {MaxFiles}; narrow the pattern");
        }

        var ordered = sort == FolderSort.Date
            ? files.OrderBy(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName, NaturalComparer.Instance).ToList()
            : files.OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
        return new FolderListing(full, filter, sort, ordered, skipped!);
    }
}

// Orders names the way people number them: runs of digits by their value (panel_2 before
// panel_10), everything else by character, ignoring case.
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (startA, startB) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                var numberA = a[startA..i].TrimStart('0');
                var numberB = b[startB..j].TrimStart('0');
                var byValue = numberA.Length != numberB.Length ? numberA.Length.CompareTo(numberB.Length) : string.CompareOrdinal(numberA, numberB);
                if (byValue != 0)
                {
                    return byValue;
                }

                continue;
            }

            var byChar = string.Compare(a, i, b, j, 1, StringComparison.OrdinalIgnoreCase);
            if (byChar != 0)
            {
                return byChar;
            }

            i++;
            j++;
        }

        var byLength = (a.Length - i).CompareTo(b.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
    }
}
