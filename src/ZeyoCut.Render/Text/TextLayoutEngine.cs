// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Text;

public sealed record TextLine(string Text, SKTextBlob? Blob, float X, float Baseline, float Width);

public sealed record TextLayout(
    IReadOnlyList<TextLine> Lines,
    float BoxWidth,
    float BoxHeight,
    SKRect InkBounds,
    string FamilyName,
    IReadOnlyList<string> Warnings,
    string Missing = "");

public sealed record ShapedText(ushort[] Glyphs, SKPoint[] Positions, float Width);

public sealed class TextLayoutEngine
{
    private readonly FontResolver _fonts;
    private readonly ConcurrentDictionary<string, TextLayout> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SKShaper> _shapers = new(StringComparer.Ordinal);
    private readonly object _shapeLock = new();

    public TextLayoutEngine(FontResolver fonts)
    {
        _fonts = fonts;
    }

    public static SKFont CreateFont(SKTypeface typeface, double size, bool embolden) => new(typeface, (float)size)
    {
        Edging = SKFontEdging.Antialias,
        Subpixel = true,
        Hinting = SKFontHinting.None,
        LinearMetrics = true,
        Embolden = embolden,
    };

    public TextLayout Layout(Project project, string projectDirectory, TextLayer layer)
    {
        var font = _fonts.Resolve(project, projectDirectory, layer);
        var key = string.Join(
            "\u0001",
            font.Key,
            font.Embolden,
            layer.Text,
            layer.FontSize.ToString("R", CultureInfo.InvariantCulture),
            layer.MaxWidth?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
            layer.LineHeight.ToString("R", CultureInfo.InvariantCulture),
            layer.Align);
        return _cache.GetOrAdd(key, _ => Build(layer, font));
    }

    public ShapedText Shape(SKTypeface typeface, SKFont font, string text)
    {
        lock (_shapeLock)
        {
            return ShapeUnlocked(typeface, font, text);
        }
    }

    private TextLayout Build(TextLayer layer, ResolvedFont resolved)
    {
        lock (_shapeLock)
        {
            using var font = CreateFont(resolved.Typeface, layer.FontSize, resolved.Embolden);
            var warnings = new List<string>();
            if (resolved.Warning is not null)
            {
                warnings.Add(resolved.Warning);
            }

            var missing = MissingCharacters(resolved.Typeface, font, layer.Text);
            if (missing.Length > 0)
            {
                warnings.Add(
                    $"font '{resolved.Typeface.FamilyName}' has no glyphs for \"{missing}\"; pick a font that covers this script (Burmese: \"Myanmar Text\", Japanese: \"Yu Gothic\")");
            }

            var lines = new List<string>();
            foreach (var paragraph in layer.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (layer.MaxWidth is { } maxWidth)
                {
                    lines.AddRange(LineBreaker.Wrap(paragraph, maxWidth, s => ShapeUnlocked(resolved.Typeface, font, s).Width));
                }
                else
                {
                    lines.Add(paragraph);
                }
            }

            var shaped = lines.Select(l => ShapeUnlocked(resolved.Typeface, font, l)).ToList();
            var ascent = -font.Metrics.Ascent;
            var descent = font.Metrics.Descent;
            var naturalSpacing = Math.Max(font.Spacing, ascent + descent);
            var lineHeight = (float)(naturalSpacing * layer.LineHeight);
            var boxWidth = (float)(layer.MaxWidth ?? (shaped.Count == 0 ? 0 : shaped.Max(s => s.Width)));
            var boxHeight = lineHeight * lines.Count;

            var result = new List<TextLine>();
            var inkLeft = float.MaxValue;
            var inkRight = float.MinValue;
            for (var i = 0; i < lines.Count; i++)
            {
                var shape = shaped[i];
                var x = layer.Align switch
                {
                    TextAlign.Center => (boxWidth - shape.Width) / 2,
                    TextAlign.Right => boxWidth - shape.Width,
                    _ => 0f,
                };
                var baseline = i * lineHeight + (lineHeight - (ascent + descent)) / 2 + ascent;
                SKTextBlob? blob = null;
                if (shape.Glyphs.Length > 0)
                {
                    using var builder = new SKTextBlobBuilder();
                    builder.AddPositionedRun(shape.Glyphs, font, shape.Positions);
                    blob = builder.Build();
                    inkLeft = Math.Min(inkLeft, x);
                    inkRight = Math.Max(inkRight, x + shape.Width);
                }

                result.Add(new TextLine(lines[i], blob, x, baseline, shape.Width));
            }

            var ink = inkLeft <= inkRight ? new SKRect(inkLeft, 0, inkRight, boxHeight) : SKRect.Empty;
            return new TextLayout(result, boxWidth, boxHeight, ink, resolved.Typeface.FamilyName, warnings, missing);
        }
    }

    private ShapedText ShapeUnlocked(SKTypeface typeface, SKFont font, string text)
    {
        if (text.Length == 0)
        {
            return new ShapedText([], [], 0);
        }

        var shaped = Shaper(typeface).Shape(text, font);
        var glyphs = shaped.Codepoints.Select(c => (ushort)c).ToArray();
        return new ShapedText(glyphs, shaped.Points, shaped.Width);
    }

    private SKShaper Shaper(SKTypeface typeface)
    {
        var shaperKey = typeface.FamilyName + "|" + typeface.FontStyle.Weight + "|" + typeface.FontStyle.Slant + "|" + typeface.Handle;
        if (!_shapers.TryGetValue(shaperKey, out var shaper))
        {
            shaper = new SKShaper(typeface);
            _shapers[shaperKey] = shaper;
        }

        return shaper;
    }

    // Shapes each line with HarfBuzz, as it is drawn, and collects the characters of the clusters that
    // came out as glyph 0 (the font's empty box): within such a cluster the characters the font doesn't
    // map, or the whole cluster when it maps them all.
    private string MissingCharacters(SKTypeface typeface, SKFont font, string text)
    {
        var missing = new StringBuilder();
        var seen = new HashSet<int>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(line);
            buffer.GuessSegmentProperties();
            var shaped = Shaper(typeface).Shape(buffer, font);
            var starts = shaped.Clusters.Select(c => (int)c).Distinct().Order().ToList();
            for (var i = 0; i < shaped.Codepoints.Length; i++)
            {
                if (shaped.Codepoints[i] != 0)
                {
                    continue;
                }

                var start = (int)shaped.Clusters[i];
                var end = starts.FirstOrDefault(s => s > start, line.Length);
                var cluster = line[start..end].EnumerateRunes()
                    .Where(r => !Rune.IsWhiteSpace(r) && !Rune.IsControl(r) && !IsInvisible(r.Value))
                    .ToList();
                var unmapped = cluster.Where(r => !font.ContainsGlyphs(r.ToString())).ToList();
                foreach (var rune in unmapped.Count > 0 ? unmapped : cluster)
                {
                    if (seen.Add(rune.Value))
                    {
                        missing.Append(rune.ToString());
                        if (missing.Length >= 12)
                        {
                            return missing.ToString();
                        }
                    }
                }
            }
        }

        return missing.ToString();
    }

    private static bool IsInvisible(int codepoint) =>
        codepoint is >= 0x200B and <= 0x200F or >= 0xFE00 and <= 0xFE0F or 0x2060 or 0xFEFF;
}
