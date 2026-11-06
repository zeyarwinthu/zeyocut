// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class PreviewTools
{
    [McpServerTool(Name = "render_frame")]
    [Description("Render one frame exactly as it will appear in the export and return it as a PNG. The time snaps to the nearest frame. The full-resolution frame is also saved in renders/. debug=true draws anchors, outlines, parent bones and layer ids. checkerboard=true shows transparent parts as gray squares instead of the background color, and layerId draws only that layer and its children: together they show the edges of a removed background (remove_background) without the layers behind it.")]
    public static IEnumerable<ContentBlock> RenderFrame(
        EditorState state,
        [Description("Time in seconds.")] double time,
        [Description("Maximum width of the returned image. Default 960.")] int maxWidth = 960,
        [Description("Draw anchors, outlines and layer names, and the box of each track on a clip.")] bool debug = false,
        [Description("Show transparency as a gray checkerboard instead of the background color.")] bool checkerboard = false,
        [Description("Draw only this layer and its children (or only a subject cutout).")] string? layerId = null,
        [Description("Language version to show (en, ja, my, pt-BR): layers of other languages are left out. Default: every layer.")] string? language = null) => Guard(() =>
    {
        var project = state.Session.Snapshot();
        var solo = string.IsNullOrWhiteSpace(layerId) ? null : layerId.Trim();
        var soloFound = solo is not null && project.FindLayer(solo) is not null;
        var version = Languages.Choose(project, language, export: false);
        if (soloFound && project.FindLayer(solo!) is null)
        {
            throw new ZeyoCutException($"'{solo}' isn't in the {version.Language} version; leave out language to draw it");
        }

        PreviewImage preview;
        lock (state.RenderGate)
        {
            preview = Preview.RenderFrame(state.Renderer, project, time, maxWidth, debug, checkerboard, solo);
        }

        var frame = preview.Frames[0];
        var frameTime = FrameMath.TimeOf(project, frame);
        var note = Math.Abs(frameTime - time) > 1e-9 ? $" (requested {Num(time)}s)" : "";
        var shown = (solo is null ? "" : $"; only '{solo}'{(project.Layers.Any(l => l.ParentId == solo) ? " and its children" : "")}") + (checkerboard ? "; transparency as a checkerboard" : "");
        var text = $"frame {frame} of {FrameMath.FrameCount(project)} at t={frameTime:0.000}s{note}{shown}{version.Note}{ProxyNote(preview)}; image {preview.Width}x{preview.Height}; saved {preview.SavedPath}";
        return new ContentBlock[]
        {
            new TextContentBlock { Text = WithWarnings(text, version.Warnings.Concat(preview.Warnings)) },
            ImageContentBlock.FromBytes(preview.Png, "image/png"),
        };
    });

    [McpServerTool(Name = "render_contact_sheet")]
    [Description("Render several evenly spaced frames into one labeled grid image to check motion over time in a single call.")]
    public static IEnumerable<ContentBlock> RenderContactSheet(
        EditorState state,
        [Description("First time in seconds. Default 0.")] double startTime = 0,
        [Description("Last time in seconds. Default: the last frame.")] double? endTime = null,
        [Description("Number of frames (1-64). Default 12.")] int count = 12,
        [Description("Grid columns. Default 4.")] int columns = 4,
        [Description("Width of the whole sheet. Default 1600.")] int maxWidth = 1600,
        [Description("Draw anchors, outlines and layer names, and the box of each track on a clip, in every cell.")] bool debug = false,
        [Description("Language version to show (en, ja, my, pt-BR): layers of other languages are left out. Default: every layer.")] string? language = null) => Guard(() =>
    {
        var project = state.Session.Snapshot();
        var version = Languages.Choose(project, language, export: false);
        var end = endTime ?? FrameMath.TimeOf(project, FrameMath.FrameCount(project) - 1);
        PreviewImage sheet;
        lock (state.RenderGate)
        {
            sheet = Preview.RenderContactSheet(state.Renderer, project, startTime, end, count, Math.Min(columns, count), maxWidth, debug);
        }

        var frames = string.Join(", ", sheet.Frames.Select(f => $"f{f}={FrameMath.TimeOf(project, f):0.000}s"));
        var text = $"contact sheet {sheet.Width}x{sheet.Height}, frames (left to right, top to bottom): {frames}; saved {sheet.SavedPath}{version.Note}{ProxyNote(sheet)}";
        return new ContentBlock[]
        {
            new TextContentBlock { Text = WithWarnings(text, version.Warnings.Concat(sheet.Warnings)) },
            ImageContentBlock.FromBytes(sheet.Png, "image/png"),
        };
    });

    [McpServerTool(Name = "preview_server")]
    [Description("Start or stop the preview page, for a browser on this computer: a timeline scrubber, the layers, and playback of low-res frames with the sound mixed as the export mixes it. It shows the open project and follows every edit within a second; it only shows, so edits are made with the tools. It listens on 127.0.0.1 only, at a random port with a secret token in its address, so other sites can't read it. start returns the address.")]
    public static string PreviewServer(
        EditorState state,
        [Description("start (default) or stop.")] string action = "start") => Guard(() =>
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
                if (!state.PreviewPage.Start(out var url))
                {
                    return $"the preview page is already running at {url}";
                }

                var shows = state.HasSession
                    ? state.Session.Read(p =>
                    {
                        var (width, height, step) = Mcp.PreviewServer.Shape(p);
                        return $"It shows '{p.Name}' ({p.Width}x{p.Height} @ {p.Fps} fps) as {width}x{height} frames, {Num(p.Fps / (double)step)} a second while playing,";
                    })
                    : $"No project is open, so it shows none until open_project or create_project opens one; then it shows its frames at up to {Mcp.PreviewServer.FrameSize} px, about {Mcp.PreviewServer.PlaybackFps} a second while playing,";
                return $"preview page at {url}\nopen it in a browser on this computer. {shows} with the sound mixed as the export mixes it (made in the background), and follows every edit within a second. It only shows; edits are made with the tools. Frames are drawn when first shown and kept in memory, so the first play of a part can stutter. preview_server with action stop stops it.";
            case "stop":
                return state.PreviewPage.Stop() is { } stopped
                    ? $"stopped the preview page at {stopped}"
                    : "the preview page isn't running";
            default:
                throw new ZeyoCutException($"action must be start or stop (got '{action}')");
        }
    });

    // Where a preview drew footage from its proxy.
    internal static string ProxyNote(PreviewImage preview) => preview.Proxied is { Count: > 0 } proxied
        ? $"; {string.Join(", ", proxied.Select(id => $"'{id}'"))} drawn from {(proxied.Count == 1 ? "its 1920 px proxy" : "their 1920 px proxies")} (exports read the original)"
        : "";
}
