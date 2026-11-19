// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Text;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class EffectTools
{
    [McpServerTool(Name = "set_effect")]
    [Description("Add, change or remove one effect on a visual layer (one undo step). The effect is on that layer only, not its children. shadow (color, x, y, blur, opacity; default #000000, 8, 8, 6, 0.5): a soft copy behind the layer, offset right/down. outline (color, width; default #FFFFFF, 4): a border around the layer's shape. blur (x, y; default 4, 4): softens the layer, x and y separately (x alone gives a horizontal motion blur). tint (color, amount; color required): mixes the layer toward a color, amount 1 paints it solid in it. Sizes are in pixels at the layer's scale and grow with it and the camera; blur sizes are the Gaussian's standard deviation. Numbers are animatable with set_keyframes as shadow.x, shadow.y, shadow.blur, shadow.opacity, outline.width, blur.x, blur.y, tint.amount; colors are fixed. Fields you leave out keep their value. remove=true removes the effect with its keyframes.")]
    public static string SetEffect(
        EditorState state,
        [Description("Layer id.")] string layerId,
        [Description("shadow, outline, blur or tint.")] string effect,
        [Description("#RRGGBB or #RRGGBBAA (shadow, outline, tint).")] string? color = null,
        [Description("shadow: offset right in pixels; blur: horizontal blur.")] double? x = null,
        [Description("shadow: offset down in pixels; blur: vertical blur.")] double? y = null,
        [Description("shadow: softness, 0-500 (0 = hard edge).")] double? blur = null,
        [Description("shadow: 0-1.")] double? opacity = null,
        [Description("outline: thickness in pixels, 0-100.")] double? width = null,
        [Description("tint: 0-1 (1 = solid color).")] double? amount = null,
        [Description("Remove the effect.")] bool remove = false) => Guard(() =>
    {
        var kind = EnumNames.Parse<EffectKind>(effect, "effect");
        var values = new EffectValues(color, x, y, blur, opacity, width, amount);
        var change = state.Session.Mutate($"set_effect {layerId} {EnumNames.ToName(kind)}", p => EffectEditor.SetEffect(p, layerId, kind, values, remove));
        return WithWarnings(change.Summary, change.Warnings);
    });

    [McpServerTool(Name = "set_color")]
    [Description("Grade an image or video clip (one undo step): exposure in stops (-3 to 3; 1 doubles the light), contrast and saturation (0 to 4, 1 as shot), highlights and shadows (-1 to 1: darken or brighten the light or the dark parts; black and white stay), temperature (-1 bluer to 1 warmer) and tint (-1 greener to 1 more magenta); 0 leaves each of those as shot. Values you leave out keep theirs. from copies another clip's color as fixed values (a clip whose color has keyframes can't be copied); reset=true puts the clip back as shot first; sameFootage=true then gives every other clip of the same video (or picture) this clip's color, and takes their color keyframes away. Once a clip has a grade, set_keyframes animates grade.exposure, grade.highlights, grade.shadows, grade.temperature and grade.tint; contrast and saturation are animatable as ever. Brightness isn't part of the color here: update_layer sets it.")]
    public static string SetColor(
        EditorState state,
        [Description("Image or video layer id.")] string layerId,
        [Description("Stops of light, -3 to 3.")] double? exposure = null,
        [Description("0 to 4, 1 as shot; 0 is flat gray.")] double? contrast = null,
        [Description("-1 to 1: darkens (below 0) or brightens the light parts.")] double? highlights = null,
        [Description("-1 to 1: deepens (below 0) or lifts the dark parts.")] double? shadows = null,
        [Description("-1 (bluer) to 1 (warmer).")] double? temperature = null,
        [Description("-1 (greener) to 1 (more magenta).")] double? tint = null,
        [Description("0 to 4, 1 as shot; 0 is black and white.")] double? saturation = null,
        [Description("Another clip to copy the color of.")] string? from = null,
        [Description("Put the clip's color back as shot first: contrast and saturation 1, no grade.")] bool reset = false,
        [Description("Then give every other clip of the same footage this clip's color.")] bool sameFootage = false) => Guard(() =>
    {
        var values = new Dictionary<string, double>();
        foreach (var (name, value) in new[] { ("exposure", exposure), ("contrast", contrast), ("highlights", highlights), ("shadows", shadows), ("temperature", temperature), ("tint", tint), ("saturation", saturation) })
        {
            if (value is { } given)
            {
                values[name] = given;
            }
        }

        var change = new ColorChange(values, string.IsNullOrWhiteSpace(from) ? null : from.Trim(), reset, sameFootage);
        var result = state.Session.Mutate($"set_color {layerId}", p => ColorEditor.SetColor(p, layerId, change));
        return WithWarnings(result.Summary, result.Warnings);
    });

    [McpServerTool(Name = "set_mask")]
    [Description("Add, change or remove the layer's mask (one undo step): the layer shows only inside a rectangle, an ellipse or an image's opaque pixels, or only outside with invert=true. The box (x, y = top-left corner, width, height) is in the layer's own pixels, like its anchor, so it moves, turns and scales with the layer; a new mask fits the layer's content unless you give it. An image mask stretches the image asset's alpha over the box. feather softens the edge over about that many pixels. x, y, width, height and feather are animatable with set_keyframes as mask.x, mask.y, mask.width, mask.height, mask.feather (a growing width is a wipe). Fields you leave out keep their value. remove=true removes the mask.")]
    public static string SetMask(
        EditorState state,
        [Description("Layer id.")] string layerId,
        [Description("rectangle, ellipse or image. Required for a new mask.")] string? type = null,
        [Description("Image asset id for type image.")] string? asset = null,
        [Description("Left edge of the box, layer pixels.")] double? x = null,
        [Description("Top edge of the box, layer pixels.")] double? y = null,
        [Description("Box width, layer pixels.")] double? width = null,
        [Description("Box height, layer pixels.")] double? height = null,
        [Description("Edge softness in pixels, 0-1000. Default 0.")] double? feather = null,
        [Description("Show the layer outside the shape instead.")] bool? invert = null,
        [Description("Remove the mask.")] bool remove = false) => Guard(() =>
    {
        var shape = string.IsNullOrWhiteSpace(type) ? (MaskType?)null : EnumNames.Parse<MaskType>(type, "mask type");
        var values = new MaskValues(shape, string.IsNullOrWhiteSpace(asset) ? null : asset.Trim(), x, y, width, height, feather, invert);
        var renderer = state.Renderer;
        var change = state.Session.Mutate($"set_mask {layerId}", p => EffectEditor.SetMask(p, layerId, values, remove, new TextMeasurer(renderer, p)));
        return WithWarnings(change.Summary, change.Warnings);
    });

    [McpServerTool(Name = "set_motion_path")]
    [Description("Move a layer along a curve through timed points (one undo step; replaces an earlier path). The layer's anchor reaches each point at its time, in the same pixel space as its x, y (the canvas for top-level layers). Each segment is a cubic bezier: inX/inY and outX/outY are a point's handles relative to the point; handles you leave out are set for a smooth curve (one handle alone is mirrored). Inside a segment the layer moves at an even speed along the curve, shaped by the easing of the point the segment starts at. autoOrient=true turns the layer with the path on top of its rotation (draw it facing right). The path replaces x and y: their keyframes are cleared, and setting x or y keyframes (or x/y with update_layer) later removes the path. Ripple edits, splits and speed changes retime it like other keyframes. remove=true takes the path off.")]
    public static string SetMotionPath(
        EditorState state,
        [Description("Layer id.")] string layerId,
        [Description("At least 2 points, in time order: [{time, x, y, inX, inY, outX, outY, easing}].")] PathPointArg[]? points = null,
        [Description("Turn the layer along the path's direction. Default false.")] bool autoOrient = false,
        [Description("Remove the path.")] bool remove = false) => Guard(() =>
    {
        var list = (points ?? []).Select(p => p is null ? null! : new PathPointSpec(p.Time, p.X, p.Y, p.InX, p.InY, p.OutX, p.OutY, p.Easing)).ToList();
        var change = state.Session.Mutate($"set_motion_path {layerId}", p => EffectEditor.SetMotionPath(p, layerId, list, autoOrient, remove));
        return WithWarnings(change.Summary, change.Warnings);
    });
}
