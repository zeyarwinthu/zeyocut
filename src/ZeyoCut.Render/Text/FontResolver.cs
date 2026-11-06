// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Text;

public sealed record ResolvedFont(string Key, SKTypeface Typeface, bool Embolden, string? Warning);

public sealed class FontResolver
{
    private readonly ConcurrentDictionary<string, SKTypeface> _typefaces = new(StringComparer.OrdinalIgnoreCase);

    public ResolvedFont Resolve(Project project, string projectDirectory, TextLayer layer)
    {
        SKTypeface typeface;
        string key;
        string? warning = null;

        if (layer.FontAsset is { } assetId)
        {
            var asset = project.FindAsset(assetId)
                ?? throw new ZeyoCutException($"layer '{layer.Id}': font asset '{assetId}' not found");
            var path = ProjectStore.AssetPath(projectDirectory, asset);
            key = "file:" + path;
            typeface = _typefaces.GetOrAdd(key, _ =>
                (File.Exists(path) ? SKTypeface.FromFile(path) : null)
                ?? throw new ZeyoCutException($"font file could not be loaded: {path}"));
        }
        else
        {
            var family = layer.FontFamily;
            key = $"family:{family}:{(layer.Bold ? "bold" : "normal")}";
            typeface = _typefaces.GetOrAdd(key, _ =>
                SKFontManager.Default.MatchFamily(family, layer.Bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                ?? SKTypeface.Default);
            if (!string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                warning = $"font family '{family}' is not installed; using '{typeface.FamilyName}' instead";
            }
        }

        return new ResolvedFont(key, typeface, layer.Bold && !typeface.IsBold, warning);
    }
}
