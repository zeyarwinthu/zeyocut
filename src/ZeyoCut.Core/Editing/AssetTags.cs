// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// Tags on assets, which group them the way bins do: tag_assets and the editor's Media browser, which
// lists each tag as a bin, add and take them away. Tags that differ only in case are the same tag.
public static class AssetTags
{
    public const int MaxLength = 40;

    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    // The tag as it's kept: without spaces around it, and with each run of spaces inside as one.
    public static string Clean(string? tag)
    {
        var clean = string.Join(' ', (tag ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
        {
            throw new ZeyoCutException("a tag can't be empty");
        }

        if (clean.Length > MaxLength)
        {
            throw new ZeyoCutException($"tag '{clean}' is {clean.Length} characters; a tag has at most {MaxLength}");
        }

        if (clean.Any(char.IsControl))
        {
            throw new ZeyoCutException($"tag '{clean}' has a control character");
        }

        return clean;
    }

    // What's wrong with an asset's tags as saved, or null.
    public static string? Problem(Asset asset)
    {
        if (asset.Tags is not { } tags)
        {
            return null;
        }

        foreach (var tag in tags)
        {
            try
            {
                if (Clean(tag) != tag)
                {
                    return $"asset '{asset.Id}': tag '{tag}' has spaces around it or a run of spaces";
                }
            }
            catch (ZeyoCutException ex)
            {
                return $"asset '{asset.Id}': {ex.Message}";
            }
        }

        return tags.GroupBy(t => t, Comparer).FirstOrDefault(g => g.Count() > 1) is { } twice
            ? $"asset '{asset.Id}' has tag '{twice.Key}' twice"
            : null;
    }

    // Every tag in the project and how many assets have it, by name.
    public static IReadOnlyList<(string Tag, int Count)> All(Project project) =>
        project.Assets.SelectMany(a => a.Tags ?? [])
            .GroupBy(t => t, Comparer)
            .Select(g => (g.First(), g.Count()))
            .OrderBy(t => t.Item1, Comparer)
            .ToList();

    public static bool Has(Asset asset, string tag) => asset.Tags?.Contains(tag, Comparer) == true;

    // Adds tags to assets and takes others away; returns what each asset's tags became, as
    // "'boing' tags: none → SFX". A tag an asset already has keeps its spelling.
    public static string Tag(Project project, IReadOnlyList<string> assetIds, IReadOnlyList<string>? add, IReadOnlyList<string>? remove)
    {
        if (assetIds.Count == 0)
        {
            throw new ZeyoCutException("give at least one asset id");
        }

        var adding = (add ?? []).Select(Clean).Distinct(Comparer).ToList();
        var removing = (remove ?? []).Select(Clean).Distinct(Comparer).ToList();
        if (adding.Count == 0 && removing.Count == 0)
        {
            throw new ZeyoCutException("nothing to change; pass tags to add, to remove or both");
        }

        if (adding.FirstOrDefault(t => removing.Contains(t, Comparer)) is { } both)
        {
            throw new ZeyoCutException($"tag '{both}' is both added and removed");
        }

        var assets = assetIds.Distinct(StringComparer.Ordinal)
            .Select(id => project.FindAsset(id) ?? throw new ZeyoCutException($"asset '{id}' not found; assets: {Ids.List(project.Assets.Select(a => a.Id))}"))
            .ToList();
        var changes = new List<string>();
        foreach (var asset in assets)
        {
            var before = asset.Tags ?? [];
            var tags = before.Where(t => !removing.Contains(t, Comparer)).ToList();
            tags.AddRange(adding.Where(t => !tags.Contains(t, Comparer)));
            if (tags.SequenceEqual(before, StringComparer.Ordinal))
            {
                continue;
            }

            asset.Tags = tags.Count == 0 ? null : tags;
            changes.Add($"'{asset.Id}' tags: {Text(before)} → {Text(tags)}");
        }

        if (changes.Count == 0)
        {
            throw new ZeyoCutException(assets.Count == 1
                ? $"nothing changed; '{assets[0].Id}' has tags: {Text(assets[0].Tags ?? [])}"
                : "nothing changed; the assets have those tags already, and not the ones to remove");
        }

        return string.Join("; ", changes);
    }

    public static string Text(IReadOnlyList<string> tags) => tags.Count == 0 ? "none" : string.Join(", ", tags);
}
