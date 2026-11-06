// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class AnimationTools
{
    [McpServerTool(Name = "set_keyframes")]
    [Description("Add or replace many keyframes in one call (one undo step; all-or-nothing). A keyframe at the same layer/property/time is replaced. Each keyframe's easing shapes the segment from it to the next keyframe. Before the first and after the last keyframe a property holds that keyframe's value; once a property has keyframes its static value is ignored. layerId \"camera\" animates the project camera (x, y, zoom, rotation), which moves every layer except those with ignoreCamera. Effect and mask numbers (shadow.x, blur.x, mask.width, ...) animate after set_effect / set_mask added them. x or y keyframes take a set_motion_path path off the layer. On a redaction layer the properties are its keyframed boxes from tracks_to_keyframes (<track>.x, .y, .width, .height), with time in the footage's own seconds.")]
    public static string SetKeyframes(
        EditorState state,
        [Description("Keyframes: [{layerId, property, time, value, easing}].")] KeyframeArg[] entries) => Guard(() =>
    {
        var list = (entries ?? []).Select(e => new KeyframeEntry(e.LayerId, e.Property, e.Time, e.Value, e.Easing)).ToList();
        var change = state.Session.Mutate($"set_keyframes ({list.Count} entries)", p => KeyframeEditor.SetKeyframes(p, list));
        var targets = list.Select(e => $"{e.LayerId}.{e.Property}").Distinct().ToList();
        return WithWarnings(
            $"set {change.Added + change.Replaced} keyframe(s) ({change.Added} added, {change.Replaced} replaced) on {string.Join(", ", targets)}",
            change.Warnings);
    });

    [McpServerTool(Name = "remove_keyframes")]
    [Description("Remove specific keyframes (all-or-nothing).")]
    public static string RemoveKeyframes(
        EditorState state,
        [Description("Keyframes to remove: [{layerId, property, time}].")] KeyframeRefArg[] entries) => Guard(() =>
    {
        var list = (entries ?? []).Select(e => new KeyframeRef(e.LayerId, e.Property, e.Time)).ToList();
        var removed = state.Session.Mutate($"remove_keyframes ({list.Count} entries)", p => KeyframeEditor.RemoveKeyframes(p, list));
        return $"removed {removed} keyframe(s)";
    });

    [McpServerTool(Name = "clear_keyframes")]
    [Description("Remove all keyframes of one property, or of every property of a layer when property is omitted (that also takes a motion path off). The static values then apply again. layerId \"camera\" clears the camera (property shakeX, shakeY, shakeRotation removes a camera_shake). On a redaction layer, property is a track id: its keyframed box goes, and the box worked out from the track applies again.")]
    public static string ClearKeyframes(
        EditorState state,
        [Description("Layer id, or \"camera\".")] string layerId,
        [Description("x, y, rotation, scaleX, scaleY, opacity, brightness, contrast, saturation, cropX, cropY, drawing, or an effect or mask property such as shadow.x or mask.width; for the camera x, y, zoom, rotation, shakeX, shakeY or shakeRotation; for a redaction a track id. Omit for all.")] string? property = null) => Guard(() =>
    {
        var target = layerId + (property is null ? "" : "." + property);
        var count = state.Session.Mutate($"clear_keyframes {target}", p =>
        {
            var removed = KeyframeEditor.ClearKeyframes(p, layerId, property);
            return removed > 0 ? removed : throw new ZeyoCutException($"{target} has no keyframes; nothing was changed");
        });
        return $"cleared {count} keyframe(s) from {target}";
    });
}
