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
public static class ScriptTools
{
    [McpServerTool(Name = "build_from_script")]
    [Description("Build a whole episode from a script and open it as a new project: the canvas or a library template, then each scene's rigs (shown for that scene), actions, sound effects and captions at their times, and a scene marker at each scene's start. The script is JSON or Markdown in fixed line patterns (docs/GUIDE.md has both); times inside a scene are seconds from its start. Anything it can't map (an unknown rig, action or sound, a time past its scene, a line it doesn't understand) is listed in the reply and the rest is built. The build is one undo step on top of the empty project.")]
    public static string BuildFromScript(
        EditorState state,
        [Description("Absolute path of the script: .json, or .md (Markdown). Give this or script.")] string? scriptFile = null,
        [Description("The script's text instead of a file: JSON if it starts with '{', Markdown otherwise.")] string? script = null,
        [Description("Project name, also the folder name. Default: the script's title.")] string? name = null,
        [Description("Optional absolute folder for the project inside the projects root, instead of <projectsRoot>/<name>.")] string? directory = null) => Guard(() =>
    {
        string text;
        string source;
        if (!string.IsNullOrWhiteSpace(scriptFile) && !string.IsNullOrWhiteSpace(script))
        {
            throw new ZeyoCutException("give scriptFile or script, not both");
        }

        if (!string.IsNullOrWhiteSpace(scriptFile))
        {
            source = AllowedFolders.Readable(state.Settings, scriptFile);
            if (!File.Exists(source))
            {
                throw new ZeyoCutException($"script file not found: {source}");
            }

            text = File.ReadAllText(source);
        }
        else if (!string.IsNullOrWhiteSpace(script))
        {
            source = "the script";
            text = script;
        }
        else
        {
            throw new ZeyoCutException("give scriptFile (a .json or .md file) or script (its text)");
        }

        var problems = new List<string>();
        var parsed = EpisodeScripts.Parse(text, EpisodeScripts.Detect(string.IsNullOrWhiteSpace(scriptFile) ? null : source, text), problems);
        var plan = ScriptBuilder.Plan(parsed, state.Library, state.AssetProbe, problems);
        var projectName = (string.IsNullOrWhiteSpace(name) ? plan.Title : name)?.Trim();
        if (string.IsNullOrEmpty(projectName))
        {
            throw new ZeyoCutException("the script has no title ('# Title' in Markdown, \"title\" in JSON); give name");
        }

        var folder = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(state.Settings.ProjectsRoot, ProjectTools.SafeFolderName(projectName))
            : AllowedFolders.Project(state.Settings, directory);
        if (ProjectStore.IsProjectDirectory(folder))
        {
            throw new ZeyoCutException($"a project already exists in '{folder}'; give another name or directory");
        }

        var notes = new List<string>();
        Project project;
        if (plan.Template is { } template)
        {
            project = Templates.Create(state.Library, template, folder, projectName, plan.Duration, notes);
            if (plan.Preset is not null || plan.Width is not null || plan.Height is not null || plan.Fps is not null)
            {
                notes.Add($"template '{template}' sets the canvas and frame rate, so the script's preset, width, height and fps aren't used");
            }
        }
        else
        {
            project = new Project
            {
                Name = projectName,
                Width = plan.Width ?? plan.Preset?.Width ?? 1920,
                Height = plan.Height ?? plan.Preset?.Height ?? 1080,
                Fps = plan.Fps ?? plan.Preset?.Fps ?? 30,
                Duration = plan.Duration,
            };
            if (plan.Preset is not null)
            {
                project.Audio.LoudnessTarget = plan.Preset.Loudness;
            }
        }

        if (plan.Background is { } background)
        {
            project.BackgroundColor = background;
        }

        var session = ProjectSession.Create(folder, project);
        state.Open(session);
        var renderer = state.Renderer;
        var probe = state.AssetProbe;
        var built = session.Mutate($"build_from_script {(source == "the script" ? "" : Path.GetFileName(source))}".TrimEnd(), p =>
        {
            var result = ScriptBuilder.Build(p, session.Directory, state.Library, plan, probe, new TextMeasurer(renderer, p));
            ProjectValidator.ThrowIfInvalid(p);
            return result;
        });

        var reply = new StringBuilder();
        reply.Append($"created and opened project '{project.Name}' at {session.Directory} from {source}{(plan.Template is null ? "" : $" (template '{plan.Template}')")}\n");
        reply.Append($"{project.Width}x{project.Height} @ {project.Fps} fps, {Num(plan.Duration)}s, {built.Scenes.Count} scene{(built.Scenes.Count == 1 ? "" : "s")}:\n");
        reply.Append(string.Join("\n", built.Scenes.Select(s => "- " + s))).Append('\n');
        if (built.CaptionGroup is { } group)
        {
            reply.Append($"captions: {built.Captions} under '{group}' (style {plan.CaptionStyle ?? "subtitle"})\n");
        }

        var all = problems.Concat(built.Problems).OrderBy(Position).ToList();
        if (all.Count > 0)
        {
            reply.Append($"couldn't map {all.Count} thing{(all.Count == 1 ? "" : "s")}; the rest was built:\n- {string.Join("\n- ", all)}\n");
        }

        reply.Append("next: render_contact_sheet to check the scenes; undo takes the build out and leaves the empty project");
        return WithWarnings(reply.ToString(), notes.Concat(built.Notes));
    });

    // Problems in script order: those about the whole script first, then by line (Markdown) or scene (JSON).
    private static int Position(string problem)
    {
        var words = problem.Split(' ', 3);
        return words.Length == 3 && words[0] is "line" or "scene" && int.TryParse(words[1].TrimEnd(':', ','), out var number) ? number : 0;
    }
}
