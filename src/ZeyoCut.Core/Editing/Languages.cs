// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// A preview or export of one language's version: the layers of other languages left out (with the
// cutouts that followed them) and the clips whose sound was muted.
public sealed record LanguageVersion(string? Language, IReadOnlyList<string> Removed, IReadOnlyList<string> Muted, IReadOnlyList<string> Warnings)
{
    public string Note => Language is null
        ? ""
        : $"; {Language} version"
            + (Removed.Count > 0 ? $", {Removed.Count} layer(s) of other languages left out" : "")
            + (Muted.Count > 0 ? $", sound of {string.Join(", ", Muted.Select(m => $"'{m}'"))} muted" : "");
}

// Language tags (en, ja, my, pt-BR). A tagged layer and its children are only in that language's
// version; a tagged video's tag is the language of its sound, and its picture is in every version.
// Untagged layers are in every version.
public static partial class Languages
{
    [GeneratedRegex("^(?<language>[A-Za-z]{2,3})(?:[-_](?<script>[A-Za-z]{4}))?(?:[-_](?<region>[A-Za-z]{2}|[0-9]{3}))?$")]
    private static partial Regex Tag();

    // The font a text in a language gets when it is given none; appsettings.json LanguageFonts adds
    // languages and changes these.
    public static readonly IReadOnlyDictionary<string, string> DefaultFonts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ja"] = "Yu Gothic",
        ["my"] = "Myanmar Text",
    };

    // The font for a language: its own, or the nearest shorter tag's (zh-Hant for zh-Hant-TW, then zh).
    public static string? FontFor(IReadOnlyDictionary<string, string> fonts, string? language)
    {
        for (var tag = language; !string.IsNullOrEmpty(tag); tag = tag.LastIndexOf('-') is > 0 and var dash ? tag[..dash] : null)
        {
            if (fonts.TryGetValue(tag, out var font))
            {
                return font;
            }
        }

        return null;
    }

    public static bool TryNormalize(string? code, out string normalized)
    {
        normalized = "";
        var match = code is null ? null : Tag().Match(code.Trim());
        if (match is not { Success: true })
        {
            return false;
        }

        normalized = match.Groups["language"].Value.ToLowerInvariant();
        if (match.Groups["script"].Success)
        {
            var script = match.Groups["script"].Value;
            normalized += "-" + char.ToUpperInvariant(script[0]) + script[1..].ToLowerInvariant();
        }

        if (match.Groups["region"].Success)
        {
            normalized += "-" + match.Groups["region"].Value.ToUpperInvariant();
        }

        return true;
    }

    // The language at the end of a subtitle file's name, after a dot or an underscore: talk.ja.srt,
    // talk_pt-BR.srt. Only a two-letter lowercase code counts, so talk_new.srt has none.
    public static string? FromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var cut = stem.LastIndexOfAny(['.', '_']);
        var code = cut < 0 ? "" : stem[(cut + 1)..];
        return code.Length >= 2 && char.IsAsciiLetterLower(code[0]) && char.IsAsciiLetterLower(code[1]) && (code.Length == 2 || code[2] == '-') && TryNormalize(code, out var tag)
            ? tag
            : null;
    }

    public static string Normalize(string? code, string what = "language") =>
        TryNormalize(code, out var normalized)
            ? normalized
            : throw new ZeyoCutException($"{what} '{code}' isn't a language code; use one such as en, ja, my or pt-BR");

    // Why a cutout or a redaction has no language of its own; null for the layers that can have one.
    public static string? Untaggable(Layer layer) => layer switch
    {
        CutoutLayer => "a subject cutout is in the versions its sources are in, so it has no language of its own; tag its sources",
        RedactionLayer => "a redaction hides in every language's version, so it has no language",
        _ => null,
    };

    // The languages the project's layers are tagged with, sorted.
    public static List<string> Used(Project project) =>
        project.Layers.Select(l => l.Language).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    // The version a layer is in: its own tag (a video's tag is only its sound's) or the nearest tag
    // of a layer it is part of. Null means every version.
    public static string? VersionOf(Project project, Layer layer)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Layer? current = layer; current is not null && seen.Add(current.Id); current = current.ParentId is { } parent ? project.FindLayer(parent) : null)
        {
            if (current is not VideoLayer && current.Language is { } language)
            {
                return language;
            }
        }

        return null;
    }

    // Turns the project (a copy) into the version in one language.
    public static LanguageVersion Select(Project project, string language)
    {
        var removed = new List<string>();
        var muted = new List<string>();
        foreach (var layer in project.Layers.Where(l => l is not CutoutLayer and not RedactionLayer))
        {
            if (VersionOf(project, layer) is { } version && version != language)
            {
                removed.Add(layer.Id);
            }
            else if (layer is VideoLayer { Language: { } sound, Muted: false } && sound != language)
            {
                muted.Add(layer.Id);
            }
        }

        var gone = removed.ToHashSet(StringComparer.Ordinal);
        project.Layers.RemoveAll(l => gone.Contains(l.Id));
        foreach (var video in project.Layers.OfType<VideoLayer>().Where(v => muted.Contains(v.Id)))
        {
            video.Muted = true;
        }

        removed.AddRange(SubjectEditor.Follow(project));
        return new LanguageVersion(language, removed, muted, []);
    }

    // A preview's or an export's language parameter: the version in that language, or every layer
    // without one. Works in place, on a copy of the project.
    public static LanguageVersion Choose(Project project, string? language, bool export)
    {
        var used = Used(project);
        var list = string.Join(", ", used);
        if (string.IsNullOrWhiteSpace(language))
        {
            List<string> all = used.Count < 2
                ? []
                : [export
                    ? $"this export includes the layers of every language ({list}); give language for one version, or export_language_versions for one file per language"
                    : $"this shows the layers of every language ({list}); give language to see one version"];
            return new LanguageVersion(null, [], [], all);
        }

        var chosen = Normalize(language);
        var version = Select(project, chosen);
        List<string> warnings = used.Count == 0
            ? [$"no layer has a language tag, so language {chosen} changes nothing; update_layer with language tags one"]
            : used.Contains(chosen)
                ? []
                : [$"no layer is tagged {chosen}, so {(export ? "this export has" : "this shows")} only the untagged layers; languages in the project: {list}"];
        return version with { Warnings = warnings };
    }
}
