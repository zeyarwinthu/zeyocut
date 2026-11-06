// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Text;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class LayerTools
{
    [McpServerTool(Name = "add_image_layer")]
    [Description("Add an image layer (a cutout part or a still). Give one image with 'asset', or several named drawings with 'drawings' (swap them with keyframes on the 'drawing' property). With neither, the layer is an invisible null that only carries a transform (useful as a rig root). x,y is where the anchor lands in the parent's pixels (canvas for top-level layers).")]
    public static string AddImageLayer(
        EditorState state,
        [Description("Image asset id for a single drawing named 'normal'.")] string? asset = null,
        [Description("Named drawings, e.g. {\"normal\": \"head_png\", \"shocked\": \"head_shocked_png\"}.")] Dictionary<string, string>? drawings = null,
        [Description("Drawing shown initially. Default: the first drawing.")] string? drawing = null,
        [Description("Layer id. Default: derived from name or asset.")] string? id = null,
        [Description("Display name.")] string? name = null,
        [Description("Parent layer id; this layer then moves with the parent.")] string? parentId = null,
        [Description("Position of the anchor in the parent's pixels. Default: canvas center, or the parent's anchor for children.")] double? x = null,
        [Description("See x.")] double? y = null,
        [Description("Pivot in this image's own pixels. Default: image center.")] double? anchorX = null,
        [Description("See anchorX.")] double? anchorY = null,
        [Description("Degrees, clockwise.")] double? rotation = null,
        double? scaleX = null,
        double? scaleY = null,
        [Description("0..1")] double? opacity = null,
        [Description("Draw order across all layers (higher is in front). Default: on top.")] int? zIndex = null,
        [Description("Seconds. Default 0.")] double? start = null,
        [Description("Seconds (exclusive). Default: project end.")] double? end = null,
        [Description("Mirror this layer's pixels around anchorX (children are not flipped).")] bool? flipX = null,
        bool? visible = null,
        [Description("Language tag (en, ja, my, pt-BR): the layer and its children are only in that language's version. Default: every version.")] string? language = null) => Guard(() =>
    {
        if (asset is not null && drawings is not null)
        {
            throw new ZeyoCutException("pass either asset or drawings, not both");
        }

        var options = new ImageLayerOptions
        {
            Id = id,
            Name = name,
            Drawings = asset is not null ? new Dictionary<string, string> { ["normal"] = asset } : drawings,
            Drawing = drawing,
            ParentId = parentId,
            X = x,
            Y = y,
            AnchorX = anchorX,
            AnchorY = anchorY,
            Rotation = rotation,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Opacity = opacity,
            ZIndex = zIndex,
            Start = start,
            End = end,
            FlipX = flipX,
            Visible = visible,
            Language = language,
        };
        var layer = state.Session.Mutate($"add_image_layer {id ?? name ?? asset}", p => ProjectEditor.AddImageLayer(p, options));
        var kind = layer.Drawings.Count == 0 ? "null layer (no drawings)" : "image layer";
        return $"added {kind} '{layer.Id}'{(layer.ParentId is null ? "" : $" under '{layer.ParentId}'")}, anchor ({Num(layer.AnchorX)},{Num(layer.AnchorY)}), z={layer.ZIndex}\n{LayerJson(layer)}";
    });

    [McpServerTool(Name = "add_text_layer")]
    [Description("Add a text layer (titles, subtitles). Complex scripts are shaped with HarfBuzz; use fontFamily \"Myanmar Text\" for Burmese and \"Yu Gothic\" for Japanese. With a language and neither fontFamily nor fontAsset, the text gets that language's font from the settings (my: Myanmar Text, ja: Yu Gothic), even over a style's font. Warns about characters the font has no glyphs for. The text's own pixel space starts at the top-left of its box; the box is maxWidth wide if set, otherwise as wide as the longest line. Returns the measured box size. 'style' fills in everything you leave out from a library style (save_style).")]
    public static string AddTextLayer(
        EditorState state,
        [Description("The text. Use \\n for line breaks.")] string text,
        [Description("Layer id. Default: derived from the text.")] string? id = null,
        string? name = null,
        [Description("Installed font family. Default \"Segoe UI\".")] string? fontFamily = null,
        [Description("Font asset id (an imported ttf/otf); overrides fontFamily.")] string? fontAsset = null,
        bool? bold = null,
        [Description("Pixels. Default 48.")] double? fontSize = null,
        [Description("#RRGGBB or #RRGGBBAA. Default #000000.")] string? color = null,
        [Description("Outline color.")] string? strokeColor = null,
        [Description("Outline thickness in pixels outside the glyphs. Default 4 when strokeColor is set.")] double? strokeWidth = null,
        [Description("left, center or right (within the box).")] string? align = null,
        [Description("Wrap lines to this width in pixels.")] double? maxWidth = null,
        [Description("Multiple of the font's natural line spacing. Default 1.")] double? lineHeight = null,
        [Description("Optional background box behind the text.")] TextBoxArg? box = null,
        string? parentId = null,
        [Description("Where the anchor lands. Default: canvas center.")] double? x = null,
        double? y = null,
        [Description("Pivot in the text box's pixels. Default: box center.")] double? anchorX = null,
        double? anchorY = null,
        double? rotation = null,
        double? scaleX = null,
        double? scaleY = null,
        double? opacity = null,
        int? zIndex = null,
        double? start = null,
        double? end = null,
        [Description("Library style name (list_library); explicit parameters override it.")] string? style = null,
        [Description("Language tag (en, ja, my, pt-BR): the layer and its children are only in that language's version. Default: every version.")] string? language = null) => Guard(() =>
    {
        var libraryStyle = string.IsNullOrWhiteSpace(style) ? null : state.Library.Load<TextStyle>(LibraryKind.Style, style);
        var tag = string.IsNullOrWhiteSpace(language) ? null : Languages.Normalize(language);
        var options = new TextLayerOptions
        {
            Text = text,
            Id = id,
            Name = name,
            FontFamily = fontFamily ?? (fontAsset is null ? Languages.FontFor(state.Settings.Fonts, tag) : null),
            FontAsset = fontAsset,
            Bold = bold,
            FontSize = fontSize,
            Color = color,
            StrokeColor = strokeColor,
            StrokeWidth = strokeWidth,
            Align = align,
            MaxWidth = maxWidth,
            LineHeight = lineHeight,
            Box = box is null ? null : ToBackground(box),
            ParentId = parentId,
            X = x,
            Y = y,
            AnchorX = anchorX,
            AnchorY = anchorY,
            Rotation = rotation,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Opacity = opacity,
            ZIndex = zIndex,
            Start = start,
            End = end,
            Language = tag,
        };
        var session = state.Session;
        var renderer = state.Renderer;
        var (layer, size, warnings) = session.Mutate($"add_text_layer {id ?? name}", p =>
        {
            var styled = libraryStyle is null ? options : TextStyles.Defaults(p, session.Directory, state.Library, libraryStyle, options);
            var added = ProjectEditor.AddTextLayer(p, styled, new TextMeasurer(renderer, p));
            var layout = renderer.Text.Layout(p, session.Directory, added);
            return (added, $"{Num(layout.BoxWidth)}x{Num(layout.BoxHeight)}", layout.Warnings);
        });
        return WithWarnings($"added text layer '{layer.Id}', box {size} px, anchor ({Num(layer.AnchorX)},{Num(layer.AnchorY)}), z={layer.ZIndex}\n{LayerJson(layer)}", warnings);
    });

    [McpServerTool(Name = "add_audio_layer")]
    [Description("Add an audio layer from an imported audio asset. Audio is only heard in the export (mixed by FFmpeg).")]
    public static string AddAudioLayer(
        EditorState state,
        [Description("Audio asset id.")] string asset,
        string? id = null,
        string? name = null,
        [Description("When the sound starts on the timeline, seconds. Default 0.")] double? start = null,
        [Description("Cut the sound at this timeline time, seconds.")] double? end = null,
        [Description("Start point inside the source file, seconds. Default 0.")] double? trimIn = null,
        [Description("End point inside the source file, seconds. Default: end of file.")] double? trimOut = null,
        [Description("Linear gain, 1 = original. Default 1.")] double? volume = null,
        [Description("Fade-in length, seconds.")] double? fadeIn = null,
        [Description("Fade-out length, seconds.")] double? fadeOut = null,
        [Description("What the sound is in the mix: music (default; ducked under voice), voice (voiceover, dialogue) or effects.")] string? role = null,
        [Description("Language tag (en, ja, my, pt-BR): the sound is only in that language's version, e.g. a dubbed voice. Default: every version.")] string? language = null) => Guard(() =>
    {
        var layer = state.Session.Mutate($"add_audio_layer {id ?? asset}", p => ProjectEditor.AddAudioLayer(p, new AudioLayerOptions
        {
            Asset = asset,
            Id = id,
            Name = name,
            Start = start,
            End = end,
            TrimIn = trimIn,
            TrimOut = trimOut,
            Volume = volume,
            Role = role,
            FadeIn = fadeIn,
            FadeOut = fadeOut,
            Language = language,
        }));
        return $"added audio layer '{layer.Id}' at {Num(layer.Start)}s\n{LayerJson(layer)}";
    });

    [McpServerTool(Name = "update_layer")]
    [Description("Change static fields of a layer. 'changes' is an object of field: value, using the field names from get_project, e.g. {\"x\": 640, \"parentId\": \"tora_body\", \"anchorX\": 120, \"end\": 2.5, \"text\": \"Hi\", \"box\": {\"padding\": 20}}. For animatable properties a number sets the static value (used only when the property has no keyframes). Use parentId \"\" to unparent. language (en, ja, my, pt-BR) puts the layer and its children in that language's version only; on a video it is the language of its sound; \"\" puts it back in every version. Nested objects are merged; null removes a nested key. A redaction's own fields are name, visible, style, strength, paddingPercent, feather, holdFrames and color.")]
    public static string UpdateLayer(
        EditorState state,
        [Description("Layer id.")] string layerId,
        [Description("Fields to change.")] JsonElement changes) => Guard(() =>
    {
        if (changes.ValueKind != JsonValueKind.Object)
        {
            throw new ZeyoCutException("changes must be a JSON object, e.g. {\"x\": 100}");
        }

        var patch = JsonObject.Create(changes)!;
        var session = state.Session;
        var renderer = state.Renderer;
        var (layer, warnings) = session.Mutate($"update_layer {layerId}", p =>
        {
            var list = ProjectEditor.UpdateLayer(p, layerId, patch).ToList();
            var updated = p.FindLayer(layerId)!;
            if (updated is TextLayer text)
            {
                ProjectValidator.ThrowIfInvalid(p);
                var layout = renderer.Text.Layout(p, session.Directory, text);
                list.Add($"text box is now {Num(layout.BoxWidth)}x{Num(layout.BoxHeight)} px");
                list.AddRange(layout.Warnings);
            }

            return (updated, list);
        });
        return WithWarnings($"updated layer '{layer.Id}'\n{LayerJson(layer)}", warnings);
    });

    [McpServerTool(Name = "remove_layer")]
    [Description("Remove a layer. A layer with children is only removed together with its children when withChildren is true.")]
    public static string RemoveLayer(
        EditorState state,
        string layerId,
        [Description("Also remove all descendants.")] bool withChildren = false) => Guard(() =>
    {
        var removed = state.Session.Mutate($"remove_layer {layerId}", p => ProjectEditor.RemoveLayer(p, layerId, withChildren));
        return $"removed: {string.Join(", ", removed)}";
    });

    [McpServerTool(Name = "duplicate_layer")]
    [Description("Copy a layer, by default with all its children (a whole rig), including keyframes. New ids replace 'replaceFrom' with 'replaceTo' (e.g. tora -> mimi), or get a numeric suffix.")]
    public static string DuplicateLayer(
        EditorState state,
        string layerId,
        [Description("Copy descendants too. Default true.")] bool includeChildren = true,
        [Description("Parent for the copy. Omit to keep the original's parent; \"\" for none.")] string? newParentId = null,
        [Description("Substring of ids/names to replace in the copies.")] string? replaceFrom = null,
        [Description("Replacement for replaceFrom.")] string? replaceTo = null,
        [Description("Added to every copied layer's zIndex.")] int zIndexOffset = 0) => Guard(() =>
    {
        var map = state.Session.Mutate($"duplicate_layer {layerId}", p => ProjectEditor.DuplicateLayer(p, new DuplicateOptions
        {
            LayerId = layerId,
            IncludeChildren = includeChildren,
            NewParentId = newParentId,
            ReplaceFrom = replaceFrom,
            ReplaceTo = replaceTo,
            ZIndexOffset = zIndexOffset,
        }));
        return "copied:\n" + string.Join("\n", map.Select(kv => $"- {kv.Key} -> {kv.Value}"));
    });

    [McpServerTool(Name = "list_layers")]
    [Description("Show the layers as a parent/child tree with type, zIndex, time range, language, anchor and keyframed properties.")]
    public static string ListLayers(EditorState state) => Guard(() => LayerTree.Format(state.Session.Snapshot()));

    private static TextBackground ToBackground(TextBoxArg box)
    {
        var background = new TextBackground();
        background.Color = box.Color ?? background.Color;
        background.Padding = box.Padding ?? background.Padding;
        background.CornerRadius = box.CornerRadius ?? background.CornerRadius;
        return background;
    }
}
