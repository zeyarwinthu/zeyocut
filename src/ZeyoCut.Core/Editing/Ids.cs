// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.RegularExpressions;

namespace ZeyoCut.Core.Editing;

public static partial class Ids
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex ValidId();

    public static bool IsValid(string? id) => id is not null && ValidId().IsMatch(id);

    public static void EnsureValid(string id, string what)
    {
        if (!IsValid(id))
        {
            throw new ZeyoCutException(
                $"invalid {what} id '{id}': use letters, digits, '_' or '-' (max 64 characters), starting with a letter or digit");
        }
    }

    public static string Slug(string? text, string fallback)
    {
        var builder = new StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var slug = builder.ToString().Trim('_');
        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('_');
        }

        return slug.Length == 0 ? fallback : slug;
    }

    public static string Unique(string baseId, Func<string, bool> isTaken)
    {
        if (!isTaken(baseId))
        {
            return baseId;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{baseId}_{n}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }

    public static string List(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }
}
