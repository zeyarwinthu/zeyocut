// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render.Text;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class EditTools
{
    [McpServerTool(Name = "split_layer")]
    [Description("Split a layer (and its children) at a timeline time into two layers that play exactly as before. The left part keeps the id; the right part gets the next free id like 'clip_2'. Keyframes are trimmed to each part and children follow the part that is showing when they start.")]
    public static string SplitLayer(
        EditorState state,
        [Description("Layer to split.")] string layerId,
        [Description("Timeline time of the split, seconds; must be inside the layer.")] double time) => Guard(() =>
    {
        var result = state.Session.Mutate($"split_layer {layerId}", p => TimelineEditor.SplitLayer(p, layerId, time));
        var others = result.Pieces.Count > 1 ? $"; children split too: {string.Join(", ", result.Pieces.Where(x => x.Key != result.RightId).Select(x => $"{x.Value} → {x.Key}"))}" : "";
        return $"split '{layerId}' at {Num(time)}s into '{result.LeftId}' and '{result.RightId}'{others}";
    });

    [McpServerTool(Name = "remove_range")]
    [Description("Cut timeline seconds start..end out of one layer (and its children) or out of every layer (layerId \"all\"). With ripple (default) everything after the cut moves left, keyframes and children included, and the project gets shorter by the removed length; without ripple a gap is left.")]
    public static string RemoveRange(
        EditorState state,
        [Description("Layer to cut, or \"all\" for every layer. Default \"all\".")] string? layerId = null,
        [Description("Start of the range, timeline seconds.")] double start = 0,
        [Description("End of the range, timeline seconds.")] double end = 0,
        [Description("Close the gap. Default true.")] bool ripple = true) => Guard(() =>
    {
        var result = state.Session.Mutate($"remove_range {layerId ?? "all"} {Num(start)}–{Num(end)}", p => TimelineEditor.RemoveRange(p, layerId, start, end, ripple));
        var lines = new List<string>
        {
            $"removed {Num(result.RemovedSeconds)}s ({Num(start)}–{Num(start + result.RemovedSeconds)}s){(ripple ? "" : ", leaving a gap")}; duration {Num(result.OldDuration)}s → {Num(result.NewDuration)}s",
        };
        if (result.Cut.Count > 0)
        {
            lines.Add($"cut: {string.Join(", ", result.Cut)}");
        }

        if (result.Pieces.Count > 0)
        {
            lines.Add($"new pieces: {string.Join(", ", result.Pieces.Select(x => $"{x.Key} (from {x.Value})"))}");
        }

        if (result.Removed.Count > 0)
        {
            lines.Add($"removed entirely: {string.Join(", ", result.Removed)}");
        }

        lines.AddRange(result.Warnings.Select(w => $"warning: {w}"));
        return string.Join("\n", lines);
    });

    [McpServerTool(Name = "move_layer")]
    [Description("Move a layer to a new timeline start, together with its children and keyframes. The project grows if the layer now runs past the end.")]
    public static string MoveLayer(
        EditorState state,
        string layerId,
        [Description("New start time, seconds.")] double start) => Guard(() =>
            Describe(state.Session.Mutate($"move_layer {layerId}", p => TimelineEditor.MoveLayer(p, layerId, start))));

    [McpServerTool(Name = "set_speed")]
    [Description("Change a video layer's playback speed (0.1..10) keeping its start; its keyframes and children stretch with it. With ripple (default) the layers after it move to stay back to back and the project duration changes by the same amount.")]
    public static string SetSpeed(
        EditorState state,
        string layerId,
        [Description("New speed, 0.1..10.")] double speed,
        [Description("Move later layers. Default true.")] bool ripple = true) => Guard(() =>
            Describe(state.Session.Mutate($"set_speed {layerId}", p => TimelineEditor.SetSpeed(p, layerId, speed, ripple))));

    [McpServerTool(Name = "add_transition")]
    [Description("Add a transition between layerA and layerB, which must be back to back (or already overlapping). crossfade overlaps them by 'duration' using unused source on each side where there is some and ripples B and later layers left for the rest, then fades B in (or A out if A is drawn on top) and crossfades the sound. fadeToBlack (image/video layers) darkens A to black over the last half of 'duration' and brings B up from black over the first half. wipe, slide, zoom and whipPan (image, text or video layers) overlap the clips the same way as crossfade, then: wipe sweeps an edge across the layer drawn on top with a rectangle mask (it must not have a mask yet); slide pushes A out of the frame and B in by the canvas width or height; zoom scales A up 2x (in) or down to half (out) while B comes from half size (or 2x) to its own size, around their centers, and the top one fades; whipPan is a slide with motion blur that peaks in the middle (blur effects are added). Everything is baked into keyframes you can adjust afterwards.")]
    public static string AddTransition(
        EditorState state,
        string layerA,
        string layerB,
        [Description("crossfade, fadeToBlack, wipe, slide, zoom or whipPan. Default crossfade.")] string type = "crossfade",
        [Description("Transition length, seconds. Default 0.5.")] double duration = 0.5,
        [Description("wipe, slide, whipPan: which way the edge or the pictures move, left, right, up or down (default: wipe right, slide and whipPan left). zoom: in (default) or out.")] string? direction = null) => Guard(() =>
    {
        var kind = EnumNames.Parse<TransitionType>(type, "transition");
        var way = string.IsNullOrWhiteSpace(direction) ? (TransitionDirection?)null : EnumNames.Parse<TransitionDirection>(direction, "direction");
        var renderer = state.Renderer;
        return Describe(state.Session.Mutate($"add_transition {layerA} {layerB}", p => TimelineEditor.AddTransition(p, layerA, layerB, kind, duration, way, new TextMeasurer(renderer, p))));
    });

    private static string Describe(TimelineChange change)
    {
        var lines = new List<string> { change.Summary };
        if (Math.Abs(change.NewDuration - change.OldDuration) > 1e-9)
        {
            lines.Add($"duration {Num(change.OldDuration)}s → {Num(change.NewDuration)}s");
        }

        lines.AddRange(change.Warnings.Select(w => $"warning: {w}"));
        return string.Join("\n", lines);
    }
}
