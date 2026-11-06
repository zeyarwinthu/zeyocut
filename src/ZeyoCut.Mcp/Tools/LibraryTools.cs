// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Text;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class LibraryTools
{
    [McpServerTool(Name = "save_rig")]
    [Description("Save a character rig to the shared library (used by every project): the root layer and everything parented under it, with anchors, drawings, parenting and draw order, posed as it is at 'time'. Each part gets a role that save_action/apply_action match by: its rigRole if set (update_layer {\"rigRole\": \"arm_l\"}), otherwise its id without the prefix the part ids share (tora_head → head); the root is always 'root'. Saving doesn't change the project, so it isn't an undo step.")]
    public static string SaveRig(
        EditorState state,
        [Description("The rig's root layer (often a null layer).")] string rootLayerId,
        [Description("Library name: letters, digits, spaces, '_' or '-' (not case-sensitive).")] string name,
        [Description("Pose to save, seconds. Default: the root's start.")] double? time = null,
        [Description("Replace a rig of the same name.")] bool overwrite = false) => Guard(() =>
    {
        var session = state.Session;
        var rig = session.Read(p => Rigs.Save(p, session.Directory, state.Library, rootLayerId, name, time, overwrite));
        return $"saved rig '{rig.Name}' to {state.Library.PathOf(LibraryKind.Rig, rig.Name)}: {Rigs.Describe(rig)}, {rig.Assets.Count} asset file(s)\n"
            + $"roles: {string.Join(", ", rig.Parts.Select(p => p.RigRole ?? Rigs.RootRole))}\n"
            + "next: add_rig places it in any project; save_action stores its animation";
    });

    [McpServerTool(Name = "add_rig")]
    [Description("Place a rig from the library in the open project (one undo step). Layer ids are <instanceName>_<part> (tora_head, tora_2_head for a second copy); the drawings are copied into assets/ once and shared by later copies. The rig is drawn on top of the existing layers, keeping its own part order.")]
    public static string AddRig(
        EditorState state,
        [Description("Rig name from list_library.")] string name,
        [Description("Where the root's anchor lands, canvas pixels. Default: canvas center.")] double? x = null,
        [Description("See x.")] double? y = null,
        [Description("When the rig appears, seconds. Default 0.")] double startTime = 0,
        [Description("Multiplies the root's scale (0..100]. Default 1.")] double scale = 1,
        [Description("Id prefix for the parts. Default: the rig name, numbered when taken.")] string? instanceName = null) => Guard(() =>
    {
        var session = state.Session;
        var placed = session.Mutate($"add_rig {name}", p => Rigs.Add(p, session.Directory, state.Library, name, x, y, startTime, scale, instanceName));
        var roles = session.Read(p => Rigs.Roles(p, placed.RootId));
        var assets = new List<string>();
        if (placed.NewAssets.Count > 0)
        {
            assets.Add($"imported {placed.NewAssets.Count} drawing file(s)");
        }

        if (placed.ReusedAssets.Count > 0)
        {
            assets.Add($"reused {placed.ReusedAssets.Count} asset(s) already in the project");
        }

        return $"added rig '{name}' as '{placed.RootId}' with {placed.LayerIds.Count} layer(s): {string.Join(", ", placed.LayerIds)}\n"
            + $"roles: {string.Join(", ", roles.Select(r => $"{r.Key}={r.Value.Id}"))}\n"
            + (assets.Count > 0 ? string.Join("; ", assets) + "\n" : "")
            + $"next: apply_action with rigRootId '{placed.RootId}', or set_keyframes on its parts";
    });

    [McpServerTool(Name = "save_action")]
    [Description("Save a rig's animation between two times to the library as a reusable action (walk cycle, jump, wing flap, double-take). Keyframes are stored by part role and relative to startTime; the root's x, y and rotation as offsets and its scale as a factor, so the action plays wherever the rig stands. Both window edges are sampled, so the action starts and ends exactly as the rig looks there. Not an undo step.")]
    public static string SaveAction(
        EditorState state,
        [Description("The rig's root layer.")] string rigRootId,
        [Description("Window start, seconds.")] double startTime,
        [Description("Window end, seconds. For a cycle, end on the pose the cycle starts with.")] double endTime,
        [Description("Library name: letters, digits, spaces, '_' or '-'.")] string name,
        [Description("Replace an action of the same name.")] bool overwrite = false) => Guard(() =>
    {
        var action = state.Session.Read(p => RigActions.Save(p, state.Library, rigRootId, startTime, endTime, name, overwrite));
        return $"saved action '{action.Name}': {RigActions.Describe(action)}\nnext: apply_action on any rig with these part roles";
    });

    [McpServerTool(Name = "apply_action")]
    [Description("Play a saved action on a rig in the open project (one undo step). Parts are matched by role, so it works on any copy of the rig and on other rigs with the same part names. Replaces the keyframes of the moved properties inside the time the action covers. With loops > 1 the root's offsets carry on from loop to loop (a walk keeps walking).")]
    public static string ApplyAction(
        EditorState state,
        [Description("Action name from list_library.")] string name,
        [Description("The rig's root layer.")] string rigRootId,
        [Description("When the action starts, seconds.")] double startTime,
        [Description("Playback speed, 0.05-20. 2 = twice as fast. Default 1.")] double speed = 1.0,
        [Description("Times to play it back to back, 1-1000. Default 1.")] int loops = 1) => Guard(() =>
    {
        var action = state.Library.Load<ActionFile>(LibraryKind.Action, name);
        var result = state.Session.Mutate($"apply_action {action.Name} on {rigRootId}", p => RigActions.Apply(p, action, rigRootId, startTime, speed, loops));
        return WithWarnings(
            $"applied action '{action.Name}' to '{rigRootId}' from {Num(result.Start)}s to {Num(result.End)}s{(loops > 1 ? $" ({loops} loops)" : "")}: {result.Keyframes} keyframe(s) on {result.Tracks} track(s)",
            result.Warnings);
    });

    [McpServerTool(Name = "save_style")]
    [Description("Save a text layer's look (font, bold, size, color, outline, alignment, line height, background box) as a named style in the library. Sizes are stored relative to the canvas, so the style looks the same on other canvas sizes. Use it with apply_style, add_text_layer style or generate_captions style. Not an undo step.")]
    public static string SaveStyle(
        EditorState state,
        [Description("A text layer.")] string layerId,
        [Description("Library name: letters, digits, spaces, '_' or '-'.")] string name,
        [Description("Where generate_captions puts captions in this style: bottom or center. Default bottom.")] string captionPosition = "bottom",
        [Description("Replace a style of the same name.")] bool overwrite = false) => Guard(() =>
    {
        var position = EnumNames.Parse<CaptionPosition>(captionPosition, "captionPosition");
        var session = state.Session;
        var style = session.Read(p => TextStyles.Save(p, session.Directory, state.Library, layerId, name, position, overwrite));
        return $"saved style '{style.Name}': {TextStyles.Describe(style)}";
    });

    [McpServerTool(Name = "apply_style")]
    [Description("Restyle a text layer, or every text layer under a group (e.g. 'captions'), with a library style (one undo step). Text, position and timing stay; anchors keep their place in the text box.")]
    public static string ApplyStyle(
        EditorState state,
        [Description("Style name from list_library.")] string name,
        [Description("A text layer or a group of text layers.")] string layerId) => Guard(() =>
    {
        var style = state.Library.Load<TextStyle>(LibraryKind.Style, name);
        var session = state.Session;
        var renderer = state.Renderer;
        var (styled, warnings) = session.Mutate($"apply_style {style.Name} to {layerId}", p =>
        {
            var ids = TextStyles.Apply(p, session.Directory, state.Library, style, layerId, new TextMeasurer(renderer, p));
            ProjectValidator.ThrowIfInvalid(p);
            var problems = ids.SelectMany(id => renderer.Text.Layout(p, session.Directory, (TextLayer)p.FindLayer(id)!).Warnings).Distinct().ToList();
            return (ids, problems);
        });
        return WithWarnings($"applied style '{style.Name}' to {styled.Count} text layer(s): {Ids.List(styled)}", warnings);
    });

    [McpServerTool(Name = "save_template")]
    [Description("Save layers of the open project (intro, outro, end screen, watermark, fixed layers) with the canvas, frame rate and audio settings as a template in the library, with their asset files. Layers that run to the project's end stretch to a new project's length; pinToEnd layers (an end screen) keep their distance from the end; others keep their times. Not an undo step.")]
    public static string SaveTemplate(
        EditorState state,
        [Description("Library name: letters, digits, spaces, '_' or '-'.")] string name,
        [Description("Layers to keep (each with its children). Default: all layers.")] string[]? layerIds = null,
        [Description("Layers (with their children) that move with the end of the video.")] string[]? pinToEnd = null,
        [Description("Replace a template of the same name.")] bool overwrite = false) => Guard(() =>
    {
        var session = state.Session;
        var template = session.Read(p => Templates.Save(p, session.Directory, state.Library, name, layerIds, pinToEnd, overwrite));
        return $"saved template '{template.Name}': {Templates.Describe(template)}\nlayers: {Ids.List(template.Project.Layers.Select(l => l.Id))}\nnext: new_project_from_template";
    });

    [McpServerTool(Name = "new_project_from_template")]
    [Description("Create a new project from a library template and open it. The folder is <projectsRoot>/<name> unless 'directory' is given. With a new duration, layers that ran to the template's end stretch, pinned layers move with the end, and the rest keep their times.")]
    public static string NewProjectFromTemplate(
        EditorState state,
        [Description("Template name from list_library.")] string template,
        [Description("Project name, also the folder name.")] string name,
        [Description("Duration in seconds. Default: the template's.")] double? duration = null,
        [Description("Optional absolute folder for the project inside the projects root, instead of <projectsRoot>/<name>.")] string? directory = null) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ZeyoCutException("name is required");
        }

        var folder = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(state.Settings.ProjectsRoot, ProjectTools.SafeFolderName(name))
            : AllowedFolders.Project(state.Settings, directory);
        var warnings = new List<string>();
        var project = Templates.Create(state.Library, template, folder, name.Trim(), duration, warnings);
        var session = ProjectSession.Create(folder, project);
        state.Open(session);
        return WithWarnings($"created and opened project '{project.Name}' from template '{template.Trim()}' at {session.Directory}\n"
            + $"{project.Width}x{project.Height} @ {project.Fps} fps, {Num(project.Duration)}s\n{LayerTree.Format(session.Snapshot()).TrimEnd()}", warnings);
    });

    [McpServerTool(Name = "list_library")]
    [Description("List what is in the shared library: rigs, actions, styles and templates, with a short description of each.")]
    public static string ListLibrary(
        EditorState state,
        [Description("rigs, actions, styles, templates or all. Default all.")] string type = "all") => Guard(() =>
    {
        var library = state.Library;
        var kinds = Kinds(type);
        var builder = new StringBuilder($"library: {library.Root}\n");
        foreach (var kind in kinds)
        {
            var names = library.Names(kind);
            builder.Append($"{Library.Plural(kind)} ({names.Count}){(names.Count == 0 ? $": none yet ({SaveTool(kind)})" : ":")}\n");
            foreach (var name in names)
            {
                string summary;
                try
                {
                    summary = kind switch
                    {
                        LibraryKind.Rig => Rigs.Describe(library.Load<RigFile>(kind, name)),
                        LibraryKind.Action => RigActions.Describe(library.Load<ActionFile>(kind, name)),
                        LibraryKind.Style => TextStyles.Describe(library.Load<TextStyle>(kind, name)),
                        _ => Templates.Describe(library.Load<TemplateFile>(kind, name)),
                    };
                }
                catch (Exception ex) when (ex is ZeyoCutException or IOException or System.Text.Json.JsonException)
                {
                    summary = $"unreadable: {ex.Message}";
                }

                builder.Append($"- {name}: {summary}\n");
            }
        }

        return builder.ToString().TrimEnd();
    });

    private static List<LibraryKind> Kinds(string? type)
    {
        var text = (type ?? "").Trim().ToLowerInvariant();
        if (text is "" or "all")
        {
            return [.. Enum.GetValues<LibraryKind>()];
        }

        foreach (var kind in Enum.GetValues<LibraryKind>())
        {
            if (text == Library.Plural(kind) || text == EnumNames.ToName(kind).ToLowerInvariant())
            {
                return [kind];
            }
        }

        throw new ZeyoCutException($"unknown library type '{type}'; use rigs, actions, styles, templates or all");
    }

    private static string SaveTool(LibraryKind kind) => kind switch
    {
        LibraryKind.Rig => "save_rig",
        LibraryKind.Action => "save_action",
        LibraryKind.Style => "save_style",
        _ => "save_template",
    };
}
