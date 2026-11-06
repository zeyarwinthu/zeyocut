// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Server;
using ZeyoCut.Core.Editing;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class CameraTools
{
    [McpServerTool(Name = "set_camera")]
    [Description("Set the project camera's fixed values (one undo step). The camera moves the whole scene: x, y is the scene point shown at the center of the frame (default: the canvas center), zoom 2 shows half as much (twice as close), rotation turns the camera clockwise so the picture turns the other way. Layers with ignoreCamera (captions get it) stay put. To move the camera over time use set_keyframes with layerId \"camera\" (properties x, y, zoom, rotation). reset=true removes the camera with all its keyframes and shake.")]
    public static string SetCamera(
        EditorState state,
        [Description("Scene x shown at the frame's center, pixels.")] double? x = null,
        [Description("Scene y shown at the frame's center, pixels.")] double? y = null,
        [Description("0.01-100; 1 shows the whole canvas, 2 is twice as close.")] double? zoom = null,
        [Description("Degrees, clockwise camera roll.")] double? rotation = null,
        [Description("Remove the camera (and its keyframes) first.")] bool reset = false) => Guard(() =>
    {
        var session = state.Session;
        var change = session.Mutate("set_camera", p => CameraEditor.Set(p, x, y, zoom, rotation, reset));
        var described = session.Read(CameraEditor.Describe);
        return WithWarnings($"camera: {string.Join("; ", change.Changes)}\n{described}", change.Warnings);
    });

    [McpServerTool(Name = "camera_shake")]
    [Description("Shake the camera (one undo step): writes seeded keyframes on the camera's shakeX, shakeY and shakeRotation channels, on top of its own movement, fading out to still at the end. The same seed always gives the same shake; change the seed for a different one. Replaces earlier shake keys inside the time range. Remove a shake with clear_keyframes layerId \"camera\" property shakeX/shakeY/shakeRotation, or undo.")]
    public static string CameraShake(
        EditorState state,
        [Description("When the shake starts, seconds.")] double startTime,
        [Description("How long it lasts, seconds (max 60).")] double duration,
        [Description("Strongest offset in screen pixels at the start (it fades out), max 1000. Rotation shakes 0.05° per pixel. Default 20.")] double intensity = 20,
        [Description("Direction changes per second, 0.5-60. Default 12.")] double frequency = 12,
        [Description("Random seed. Default 1.")] int seed = 1) => Guard(() =>
    {
        var result = state.Session.Mutate($"camera_shake {Num(startTime)}s", p => CameraEditor.Shake(p, startTime, duration, intensity, frequency, seed));
        return WithWarnings(
            $"camera shake {Num(result.Start)}–{Num(result.End)}s: {Num(intensity)} px, {Num(frequency)} direction changes per second, seed {seed}; {result.Keyframes} keyframe(s) on shakeX, shakeY, shakeRotation",
            result.Warnings);
    });
}
