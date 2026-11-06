// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// save_style / apply_style: text looks kept in the library. Sizes scale with the canvas.
public static class TextStyles
{
    public static TextStyle Save(Project project, string projectDirectory, Library library, string? layerId, string? name, CaptionPosition captionPosition, bool overwrite)
    {
        var styleName = Library.CheckName(name, LibraryKind.Style);
        var layer = ProjectEditor.GetLayer(project, layerId);
        if (layer is not TextLayer text)
        {
            throw new ZeyoCutException($"'{layer.Id}' is a {layer.TypeName} layer; save_style takes a text layer");
        }

        var style = new TextStyle
        {
            Name = styleName,
            SavedUtc = DateTime.UtcNow,
            ReferenceSize = Math.Min(project.Width, project.Height),
            FontFamily = text.FontFamily,
            Bold = text.Bold,
            FontSize = text.FontSize,
            Color = text.Color,
            StrokeColor = text.StrokeColor,
            StrokeWidth = text.StrokeWidth,
            Align = text.Align,
            LineHeight = text.LineHeight,
            Box = text.Box is null ? null : ProjectJson.Copy(text.Box),
            CaptionPosition = captionPosition,
        };
        var font = text.FontAsset is { } fontId ? project.FindAsset(fontId) ?? throw new ZeyoCutException($"font asset '{fontId}' is missing from the project") : null;
        library.Save(LibraryKind.Style, styleName, overwrite, path =>
        {
            if (font is not null)
            {
                var source = ProjectStore.AssetPath(projectDirectory, font);
                if (!File.Exists(source))
                {
                    throw new ZeyoCutException($"font asset '{font.Id}' is missing its file: {source}");
                }

                var fileName = styleName + Path.GetExtension(source);
                File.Copy(source, Path.Combine(Path.GetDirectoryName(path)!, fileName), overwrite: true);
                style.Font = ProjectJson.Copy(font);
                style.Font.File = fileName;
                style.Font.OriginalFile = null;
            }

            AnalysisFiles.Save(path, style);
        });
        return style;
    }

    public static double Scale(Project project, TextStyle style) =>
        style.ReferenceSize > 0 ? Math.Min(project.Width, project.Height) / style.ReferenceSize : 1;

    // The style's font as a project asset id (imported once), or null when it uses an installed font.
    public static string? AdoptFont(Project project, string projectDirectory, Library library, TextStyle style) =>
        style.Font is { } font
            ? AssetImporter.Adopt(project, projectDirectory, font, Path.Combine(library.Folder(LibraryKind.Style), ProjectStore.IsPlainFileName(font.File)
                ? font.File
                : throw new ZeyoCutException($"style '{style.Name}': font file '{font.File}' must be a file next to the style in {library.Folder(LibraryKind.Style)}")), Path.GetFileNameWithoutExtension(font.File)).Id
            : null;

    // Options for add_text_layer with the style filling everything the caller left out.
    public static TextLayerOptions Defaults(Project project, string projectDirectory, Library library, TextStyle style, TextLayerOptions options)
    {
        var scale = Scale(project, style);
        var fontAsset = options.FontAsset ?? (options.FontFamily is null ? AdoptFont(project, projectDirectory, library, style) : null);
        return options with
        {
            FontFamily = options.FontFamily ?? style.FontFamily,
            FontAsset = fontAsset,
            Bold = options.Bold ?? style.Bold,
            FontSize = options.FontSize ?? Math.Round(style.FontSize * scale, 1),
            Color = options.Color ?? style.Color,
            StrokeColor = options.StrokeColor ?? style.StrokeColor,
            StrokeWidth = options.StrokeWidth ?? Math.Round(style.StrokeWidth * scale, 1),
            Align = options.Align ?? EnumNames.ToName(style.Align),
            LineHeight = options.LineHeight ?? style.LineHeight,
            Box = options.Box ?? ScaledBox(style, scale),
        };
    }

    // Restyles a text layer, or every text layer under a group. Anchors keep their place in the
    // text box (center stays center) when a measurer is given.
    public static List<string> Apply(Project project, string projectDirectory, Library library, TextStyle style, string? layerId, ITextMeasurer? measurer)
    {
        var target = ProjectEditor.GetLayer(project, layerId);
        var texts = ProjectEditor.Subtree(project, target.Id).OfType<TextLayer>().ToList();
        if (texts.Count == 0)
        {
            throw new ZeyoCutException($"'{target.Id}' is a {target.TypeName} layer with no text layers under it; apply_style takes a text layer or a group of them");
        }

        var scale = Scale(project, style);
        var font = AdoptFont(project, projectDirectory, library, style);
        foreach (var text in texts)
        {
            var before = measurer?.Measure(text);
            text.FontFamily = style.FontFamily;
            text.FontAsset = font;
            text.Bold = style.Bold;
            text.FontSize = Math.Round(style.FontSize * scale, 1);
            text.Color = style.Color;
            text.StrokeColor = style.StrokeColor;
            text.StrokeWidth = Math.Round(style.StrokeWidth * scale, 1);
            text.Align = style.Align;
            text.LineHeight = style.LineHeight;
            text.Box = ScaledBox(style, scale);
            if (measurer is not null && before is { Width: > 0, Height: > 0 } old)
            {
                var (width, height) = measurer.Measure(text);
                text.AnchorX = Math.Round(text.AnchorX * width / old.Width, 3);
                text.AnchorY = Math.Round(text.AnchorY * height / old.Height, 3);
            }
        }

        return texts.Select(t => t.Id).ToList();
    }

    public static string Describe(TextStyle style)
    {
        var parts = new List<string> { $"{(style.Font is null ? style.FontFamily : $"font file {style.Font.File}")}{(style.Bold ? " bold" : "")} {Num(style.FontSize)}px {style.Color}" };
        if (style.StrokeColor is not null && style.StrokeWidth > 0)
        {
            parts.Add($"outline {style.StrokeColor} {Num(style.StrokeWidth)}px");
        }

        if (style.Box is not null)
        {
            parts.Add($"box {style.Box.Color}");
        }

        parts.Add($"align {EnumNames.ToName(style.Align)}, captions at the {EnumNames.ToName(style.CaptionPosition)}, sizes for a {Num(style.ReferenceSize)}px canvas side");
        return string.Join("; ", parts);
    }

    private static TextBackground? ScaledBox(TextStyle style, double scale) =>
        style.Box is { } box
            ? new TextBackground { Color = box.Color, Padding = Math.Round(box.Padding * scale, 1), CornerRadius = Math.Round(box.CornerRadius * scale, 1) }
            : null;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
