// SPDX-License-Identifier: MIT

namespace ZeyoCut.Render.Export;

// The name a file is written under until it is whole: <name>.<unique>.partial<extension> beside it.
// Two programs making the same file (the server and the editor) each write their own, as the chunks
// of an export do.
internal static class PartialFile
{
    // A new name for path's partial file. Partial files a stopped program left in the folder more than
    // a day ago are deleted first.
    public static string For(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var end = ".partial" + Path.GetExtension(path);
        try
        {
            foreach (var left in new DirectoryInfo(folder).EnumerateFiles("*" + end))
            {
                if (IsUnique(left.Name[..^end.Length]) && left.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                {
                    left.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{end}");
    }

    // Whether the name ends with the 32 hex digits For puts there, so no other file is taken for one.
    private static bool IsUnique(string name) =>
        name.Length > 33 && name[^33] == '.' && name.AsSpan(name.Length - 32).IndexOfAnyExcept("0123456789abcdef") < 0;
}
