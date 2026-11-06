// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record ProjectOptions
{
    public string? Name { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Fps { get; init; }
    public double? Duration { get; init; }
    public string? BackgroundColor { get; init; }
    public string? Preset { get; init; }

    // LUFS; 0 turns loudness normalization off.
    public double? LoudnessTarget { get; init; }
    public bool? Ducking { get; init; }
}

public record VisualLayerOptions
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? ParentId { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? AnchorX { get; init; }
    public double? AnchorY { get; init; }
    public double? Rotation { get; init; }
    public double? ScaleX { get; init; }
    public double? ScaleY { get; init; }
    public double? Opacity { get; init; }
    public int? ZIndex { get; init; }
    public double? Start { get; init; }
    public double? End { get; init; }
    public bool? Visible { get; init; }
    public string? Language { get; init; }
}

public sealed record ImageLayerOptions : VisualLayerOptions
{
    public IReadOnlyDictionary<string, string>? Drawings { get; init; }
    public string? Drawing { get; init; }
    public bool? FlipX { get; init; }
}

public sealed record TextLayerOptions : VisualLayerOptions
{
    public string Text { get; init; } = "";
    public string? FontFamily { get; init; }
    public string? FontAsset { get; init; }
    public bool? Bold { get; init; }
    public double? FontSize { get; init; }
    public string? Color { get; init; }
    public string? StrokeColor { get; init; }
    public double? StrokeWidth { get; init; }
    public string? Align { get; init; }
    public double? MaxWidth { get; init; }
    public double? LineHeight { get; init; }
    public TextBackground? Box { get; init; }
}

public sealed record AudioLayerOptions
{
    public required string Asset { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public double? Start { get; init; }
    public double? End { get; init; }
    public double? TrimIn { get; init; }
    public double? TrimOut { get; init; }
    public double? Volume { get; init; }
    public string? Role { get; init; }
    public double? FadeIn { get; init; }
    public double? FadeOut { get; init; }
    public string? Language { get; init; }
}

public sealed record VideoLayerOptions : VisualLayerOptions
{
    public required string Asset { get; init; }
    public double? TrimIn { get; init; }
    public double? TrimOut { get; init; }
    public double? Speed { get; init; }
    public double? Volume { get; init; }
    public bool? Muted { get; init; }
    public string? Role { get; init; }
    public double? FadeIn { get; init; }
    public double? FadeOut { get; init; }
    public double? CropWidth { get; init; }
    public double? CropHeight { get; init; }
}

public sealed record DuplicateOptions
{
    public required string LayerId { get; init; }
    public bool IncludeChildren { get; init; } = true;
    public string? NewParentId { get; init; }
    public string? ReplaceFrom { get; init; }
    public string? ReplaceTo { get; init; }
    public int ZIndexOffset { get; init; }
}

public interface ITextMeasurer
{
    (double Width, double Height) Measure(TextLayer layer);

    // The characters of the text its font has no glyphs for (after shaping); "" when it has them all.
    string MissingGlyphs(TextLayer layer) => "";
}
