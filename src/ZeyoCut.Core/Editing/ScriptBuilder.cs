// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record PlannedRig(string Where, string Rig, string Name, double? X, double? Y, double Scale);

public sealed record PlannedAction(string Where, ActionFile Action, string On, double Start, double End, double Speed, int Loops);

public sealed record PlannedSound(string Where, string Sound, double Start, double End, double Volume);

public sealed record PlannedCaption(string Where, string Text, double Start, double End);

public sealed record PlannedScene(int Number, string Name, double Start, double End, List<PlannedRig> Rigs, List<PlannedAction> Actions, List<PlannedSound> Sounds, List<PlannedCaption> Captions);

// What build_from_script will build: the project settings and every scene on the project's timeline.
public sealed record ScriptPlan(string? Title, string? Template, Preset? Preset, int? Width, int? Height, int? Fps, string? Background, string? CaptionStyle, double Duration, IReadOnlyList<PlannedScene> Scenes);

// Scenes: one line per scene; Problems: what couldn't be built; Notes: warnings about what was.
public sealed record ScriptResult(IReadOnlyList<string> Scenes, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes, string? CaptionGroup, int Captions);

// build_from_script: plans a script against the library (every name looked up, every time worked
// out), then builds it into a project. Whatever can't be mapped is reported and the rest is built.
public static class ScriptBuilder
{
    public const double CaptionSeconds = 3;
    private const double Tolerance = 1e-6;

    public static ScriptPlan Plan(EpisodeScript script, Library library, IAssetProbe probe, List<string> problems)
    {
        if (script.Scenes.Count == 0)
        {
            throw new ZeyoCutException("the script has no scenes; a Markdown scene starts with '## Scene name (8s)', a JSON one is an entry of \"scenes\"");
        }

        var preset = string.IsNullOrWhiteSpace(script.Preset) ? null : Presets.Get(script.Preset);
        var rigNames = library.Names(LibraryKind.Rig);
        var scenes = new List<PlannedScene>();
        var time = 0.0;
        for (var i = 0; i < script.Scenes.Count; i++)
        {
            var scene = script.Scenes[i];
            var where = scene.Where.Length > 0 ? scene.Where : $"scene {i + 1}";
            var start = time;
            if (scene.Start is { } given)
            {
                if (double.IsFinite(given) && given >= 0)
                {
                    start = given;
                }
                else
                {
                    problems.Add($"{where}: start must be 0 or more seconds (got {Num(given)}); it starts at {Num(time)}s, after the scene before");
                }
            }

            double? length = scene.Duration;
            if (length is { } d && (!double.IsFinite(d) || d <= 0))
            {
                problems.Add($"{where}: duration must be more than 0 seconds (got {Num(d)}); it runs until its last item ends");
                length = null;
            }

            var rigs = new List<PlannedRig>();
            foreach (var rig in scene.Rigs)
            {
                var name = rig.Rig?.Trim() ?? "";
                var local = (rig.As ?? name).Trim();
                if (name.Length == 0)
                {
                    problems.Add($"{rig.Where}: the rig has no name");
                }
                else if (!rigNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{rig.Where}: no rig '{name}' in the library; {(rigNames.Count == 0 ? "there are no rigs yet (save_rig saves one)" : $"rigs: {string.Join(", ", rigNames)}")}");
                }
                else if (rigs.Any(r => r.Name.Equals(local, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{rig.Where}: the scene already has a rig called '{local}'; give this one another name with 'as'");
                }
                else
                {
                    rigs.Add(new PlannedRig(rig.Where, name, local, rig.X, rig.Y, rig.Scale ?? 1));
                }
            }

            var unplaced = scene.Rigs.Select(r => (r.As ?? r.Rig ?? "").Trim()).Where(n => n.Length > 0 && !rigs.Any(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actions = new List<PlannedAction>();
            foreach (var action in scene.Actions)
            {
                var name = action.Action?.Trim() ?? "";
                if (!Timed(name, action.At, action.Where, "the action has no name", $"action '{name}'", problems, out var at))
                {
                    continue;
                }

                ActionFile file;
                try
                {
                    file = library.Load<ActionFile>(LibraryKind.Action, name);
                }
                catch (ZeyoCutException ex)
                {
                    problems.Add($"{action.Where}: {ex.Message}");
                    continue;
                }

                string? on = action.On?.Trim();
                if (string.IsNullOrEmpty(on))
                {
                    if (rigs.Count != 1)
                    {
                        problems.Add($"{action.Where}: action '{name}' needs 'on' to say which rig plays it (the scene has {rigs.Count} rigs)");
                        continue;
                    }

                    on = rigs[0].Name;
                }
                else if (rigs.Find(r => r.Name.Equals(on, StringComparison.OrdinalIgnoreCase)) is { } target)
                {
                    on = target.Name;
                }
                else
                {
                    problems.Add(unplaced.Contains(on)
                        ? $"{action.Where}: action '{name}' is on rig '{on}', which couldn't be placed"
                        : $"{action.Where}: action '{name}' is on '{on}', which isn't a rig in this scene ({(rigs.Count == 0 ? "it has none" : $"rigs: {string.Join(", ", rigs.Select(r => r.Name))}")})");
                    continue;
                }

                var speed = action.Speed ?? 1;
                var loops = action.Loops ?? 1;
                if (!double.IsFinite(speed) || speed <= 0)
                {
                    problems.Add($"{action.Where}: speed must be more than 0 (got {Num(speed)})");
                    continue;
                }

                actions.Add(new PlannedAction(action.Where, file, on, start + at, start + at + file.Duration * Math.Max(loops, 1) / speed, speed, loops));
            }

            var sounds = new List<PlannedSound>();
            foreach (var sound in scene.Sfx)
            {
                var name = sound.Sound?.Trim() ?? "";
                if (!Timed(name, sound.At, sound.Where, "the sound has no name", $"sound '{name}'", problems, out var at))
                {
                    continue;
                }

                try
                {
                    var effect = SoundEffects.Find(library, name);
                    var seconds = probe.Probe(effect.File, AssetType.Audio).Duration ?? 0;
                    sounds.Add(new PlannedSound(sound.Where, effect.Name, start + at, start + at + seconds, sound.Volume ?? 1));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"{sound.Where}: {ex.Message}");
                }
            }

            var captions = new List<PlannedCaption>();
            foreach (var caption in scene.Captions)
            {
                var text = caption.Text?.Trim() ?? "";
                if (!Timed(text, caption.At, caption.Where, "the caption has no text", $"the caption \"{Short(text)}\"", problems, out var at))
                {
                    continue;
                }

                var end = caption.End ?? at + (caption.Duration ?? CaptionSeconds);
                if (!double.IsFinite(end) || end <= at)
                {
                    problems.Add($"{caption.Where}: the caption \"{Short(text)}\" ends at {Num(end)}s, not after it starts ({Num(at)}s)");
                    continue;
                }

                captions.Add(new PlannedCaption(caption.Where, text, start + at, start + end));
            }

            var ends = actions.Select(a => a.End).Concat(sounds.Select(s => s.End)).Concat(captions.Select(c => c.End)).ToList();
            var sceneName = string.IsNullOrWhiteSpace(scene.Name) ? $"Scene {i + 1}" : string.Concat(scene.Name.Trim().Select(c => char.IsControl(c) ? ' ' : c));
            if (length is null && ends.Count == 0)
            {
                problems.Add($"{where}: the scene has no duration and nothing timed in it, so it was left out");
                continue;
            }

            var sceneEnd = length is { } fixedLength ? start + fixedLength : Math.Max(ends.Max(), start + 1);
            Late(actions, a => a.Start, a => a.Where, $"action '{{0}}'", a => a.Action.Name, sceneEnd, problems);
            Late(sounds, s => s.Start, s => s.Where, "sound '{0}'", s => s.Sound, sceneEnd, problems);
            Late(captions, c => c.Start, c => c.Where, "the caption \"{0}\"", c => Short(c.Text), sceneEnd, problems);
            scenes.Add(new PlannedScene(i + 1, sceneName, start, sceneEnd, rigs, actions, sounds, captions));
            time = sceneEnd;
        }

        if (scenes.Count == 0)
        {
            throw new ZeyoCutException("none of the script's scenes could be built:\n- " + string.Join("\n- ", problems));
        }

        var scenesEnd = scenes.Max(s => s.End);
        var duration = Math.Max(script.Duration ?? 0, scenesEnd);
        if (script.Duration is { } wanted && wanted < scenesEnd - Tolerance)
        {
            problems.Add($"the script's duration ({Num(wanted)}s) is shorter than its scenes, which run to {Num(scenesEnd)}s; the project is {Num(scenesEnd)}s long");
        }

        return new ScriptPlan(script.Title?.Trim(), string.IsNullOrWhiteSpace(script.Template) ? null : script.Template.Trim(), preset, script.Width, script.Height, script.Fps,
            string.IsNullOrWhiteSpace(script.Background) ? null : script.Background.Trim(), string.IsNullOrWhiteSpace(script.CaptionStyle) ? null : script.CaptionStyle.Trim(),
            Math.Round(duration, 3), scenes);
    }

    // Builds the plan into the project: the rigs of each scene shown for that scene, their actions,
    // the sounds, a scene marker at each scene's start and the captions under one caption group.
    public static ScriptResult Build(Project project, string projectDirectory, Library library, ScriptPlan plan, IAssetProbe probe, ITextMeasurer? measurer)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var lines = new List<string>();
        foreach (var scene in plan.Scenes)
        {
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();
            foreach (var rig in scene.Rigs)
            {
                try
                {
                    var instance = Ids.Unique(Ids.Slug(rig.Name, "rig"), c => project.Layers.Any(l => l.Id.StartsWith(c + "_", StringComparison.Ordinal)));
                    var placed = Rigs.Add(project, projectDirectory, library, rig.Rig, rig.X, rig.Y, scene.Start, rig.Scale, instance);
                    if (scene.End < project.Duration - Tolerance)
                    {
                        foreach (var id in placed.LayerIds)
                        {
                            var layer = project.FindLayer(id)!;
                            layer.End = Math.Round(layer.End is { } end ? Math.Min(end, scene.End) : scene.End, 9);
                        }
                    }

                    roots[rig.Name] = placed.RootId;
                    parts.Add($"rig {rig.Rig} as {placed.RootId}");
                }
                catch (ZeyoCutException ex)
                {
                    problems.Add($"{rig.Where}: {ex.Message}");
                }
            }

            foreach (var action in scene.Actions)
            {
                if (!roots.TryGetValue(action.On, out var root))
                {
                    problems.Add($"{action.Where}: action '{action.Action.Name}' is on rig '{action.On}', which couldn't be placed");
                    continue;
                }

                try
                {
                    var applied = RigActions.Apply(project, action.Action, root, action.Start, action.Speed, action.Loops);
                    parts.Add($"{action.Action.Name} on {root} {Num(applied.Start)}–{Num(applied.End)}s{(action.Loops > 1 ? $" ({action.Loops} loops)" : "")}");
                    notes.AddRange(applied.Warnings.Select(w => $"{action.Where}: {w}"));
                }
                catch (ZeyoCutException ex)
                {
                    problems.Add($"{action.Where}: {ex.Message}");
                }
            }

            foreach (var sound in scene.Sounds)
            {
                try
                {
                    var added = SoundEffects.Add(project, projectDirectory, library, sound.Sound, sound.Start, sound.Volume, probe);
                    parts.Add($"{added.Effect.Name} at {Num(sound.Start)}s as {added.Layer.Id}");
                    notes.AddRange(added.Warnings.Select(w => $"{sound.Where}: {w}"));
                }
                catch (ZeyoCutException ex)
                {
                    problems.Add($"{sound.Where}: {ex.Message}");
                }
            }

            if (scene.Captions.Count > 0)
            {
                parts.Add($"{scene.Captions.Count} caption{(scene.Captions.Count == 1 ? "" : "s")}");
            }

            lines.Add($"scene {scene.Number} \"{scene.Name}\" {Num(scene.Start)}–{Num(scene.End)}s: {(parts.Count == 0 ? "nothing placed" : string.Join("; ", parts))}");
        }

        try
        {
            MarkerEditor.AddMarkers(project, plan.Scenes.Select(s => new MarkerEntry(null, s.Start, s.Name, "scene")).ToList());
        }
        catch (ZeyoCutException ex)
        {
            problems.Add($"scene markers: {ex.Message}");
        }

        var captions = plan.Scenes.SelectMany(s => s.Captions).ToList();
        string? group = null;
        if (captions.Count > 0)
        {
            try
            {
                var options = CaptionStyle(project, projectDirectory, library, plan.CaptionStyle, problems);
                var cues = captions.Select(c => new CaptionCue(c.Start, Math.Min(c.End, project.Duration), c.Text.Split('\n'), 0, 0)).ToList();
                group = CaptionBuilder.Place(project, cues, options, measurer).GroupId;
            }
            catch (ZeyoCutException ex)
            {
                problems.Add($"captions: {ex.Message}");
            }
        }

        return new ScriptResult(lines, problems, notes, group, group is null ? 0 : captions.Count);
    }

    private static CaptionOptions CaptionStyle(Project project, string projectDirectory, Library library, string? style, List<string> problems)
    {
        if (style is null || CaptionBuilder.Styles.Contains(style.ToLowerInvariant().Replace('-', '_')))
        {
            return new CaptionOptions { Style = style ?? "subtitle" };
        }

        var names = library.Names(LibraryKind.Style);
        if (!names.Contains(style, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"captionStyle: unknown caption style '{style}', so the captions use subtitle; use {string.Join(", ", CaptionBuilder.Styles)}{(names.Count == 0 ? "" : $" or a library style: {string.Join(", ", names)}")}");
            return new CaptionOptions();
        }

        var loaded = library.Load<TextStyle>(LibraryKind.Style, style);
        return new CaptionOptions { Style = style, LibraryStyle = loaded, FontAsset = TextStyles.AdoptFont(project, projectDirectory, library, loaded) };
    }

    // Takes out what starts at or after its scene's end, which the scene would never show.
    private static void Late<T>(List<T> items, Func<T, double> start, Func<T, string> where, string what, Func<T, string> name, double sceneEnd, List<string> problems)
    {
        foreach (var item in items.Where(i => start(i) >= sceneEnd - Tolerance).ToList())
        {
            problems.Add($"{where(item)}: {string.Format(CultureInfo.InvariantCulture, what, name(item))} starts at or after the end of its scene ({Num(sceneEnd)}s)");
            items.Remove(item);
        }
    }

    // An item needs a name (or text) and a time of 0 or more seconds from its scene's start.
    private static bool Timed(string name, double? time, string where, string unnamed, string what, List<string> problems, out double at)
    {
        at = time ?? 0;
        if (name.Length == 0)
        {
            problems.Add($"{where}: {unnamed}");
            return false;
        }

        if (time is null)
        {
            problems.Add($"{where}: {what} needs a time ('at')");
            return false;
        }

        if (!double.IsFinite(at) || at < 0)
        {
            problems.Add($"{where}: {what} is at {Num(at)}s; times are 0 or more seconds from the scene's start");
            return false;
        }

        return true;
    }

    private static string Short(string text)
    {
        var line = text.Replace('\n', ' ');
        return line.Length > 30 ? line[..30] + "…" : line;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
