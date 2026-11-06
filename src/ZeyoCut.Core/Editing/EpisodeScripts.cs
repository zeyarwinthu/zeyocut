// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZeyoCut.Core.Editing;

public enum ScriptFormat
{
    Json,
    Markdown
}

// build_from_script's episode script. Times inside a scene are seconds from the scene's start.
// Unknown holds fields the JSON has that aren't part of the script; Where says where an item came
// from (a Markdown line, or its place in the JSON) for the reply.
public sealed class EpisodeScript
{
    public string? Title { get; set; }
    public string? Template { get; set; }
    public string? Preset { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Fps { get; set; }
    public double? Duration { get; set; }
    public string? Background { get; set; }
    public string? CaptionStyle { get; set; }
    public List<ScriptScene> Scenes { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

public sealed class ScriptScene
{
    public string? Name { get; set; }
    public double? Start { get; set; }
    public double? Duration { get; set; }
    public List<ScriptRig> Rigs { get; set; } = [];
    public List<ScriptAction> Actions { get; set; } = [];
    public List<ScriptSound> Sfx { get; set; } = [];
    public List<ScriptCaption> Captions { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore]
    public string Where { get; set; } = "";
}

public sealed class ScriptRig
{
    public string? Rig { get; set; }
    public string? As { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Scale { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore]
    public string Where { get; set; } = "";
}

public sealed class ScriptAction
{
    public string? Action { get; set; }
    public string? On { get; set; }
    public double? At { get; set; }
    public double? Speed { get; set; }
    public int? Loops { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore]
    public string Where { get; set; } = "";
}

public sealed class ScriptSound
{
    public string? Sound { get; set; }
    public double? At { get; set; }
    public double? Volume { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore]
    public string Where { get; set; } = "";
}

public sealed class ScriptCaption
{
    public string? Text { get; set; }
    public double? At { get; set; }
    public double? End { get; set; }
    public double? Duration { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore]
    public string Where { get; set; } = "";
}

// Reads episode scripts. Markdown scripts use fixed line patterns:
//   # Title
//   template: kids-show        (one-word settings before the first scene: template, preset, width, height,
//                               fps, duration, background, captionStyle)
//   ## The ledge (8s)          (a scene, with its duration)
//   - rig tora as tora at 600,800 scale 1
//   - action walk on tora at 1s speed 1 loops 2
//   - sfx boing at 3.5s volume 0.8
//   - caption 2s-4s: Here goes!
// Names with spaces go in double quotes. Other lines are notes and are left alone.
public static partial class EpisodeScripts
{
    private const string TimePattern = @"(?:\d+:\d{1,2}(?:\.\d+)?|\d+(?:\.\d+)?s?)";
    private const string NumberPattern = @"-?\d+(?:\.\d+)?";

    public static readonly string[] Settings = ["template", "preset", "width", "height", "fps", "duration", "background", "captionStyle"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex(@"^(?<key>[A-Za-z]+)\s*:\s*(?<value>.+)$")]
    private static partial Regex Setting();

    [GeneratedRegex(@"^(?<name>.*?)\s*\((?<duration>[^()]*)\)$")]
    private static partial Regex SceneDuration();

    [GeneratedRegex(@"^(?<kind>rig|action|sfx|sound|caption)\b\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Item();

    [GeneratedRegex(@"^(?:""(?<quoted>[^""]+)""|(?<word>\S+))(?<rest>.*)$")]
    private static partial Regex Name();

    [GeneratedRegex(@"\G\s+(?:as\s+(?<as>\S+)|at\s+(?<x>" + NumberPattern + @")\s*,\s*(?<y>" + NumberPattern + @")|scale\s+(?<scale>" + NumberPattern + "))", RegexOptions.IgnoreCase)]
    private static partial Regex RigOption();

    [GeneratedRegex(@"\G\s+(?:on\s+(?<on>\S+)|at\s+(?<at>" + TimePattern + @")|speed\s+(?<speed>" + NumberPattern + @")|loops?\s+(?<loops>\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex ActionOption();

    [GeneratedRegex(@"\G\s+(?:at\s+(?<at>" + TimePattern + @")|volume\s+(?<volume>" + NumberPattern + "))", RegexOptions.IgnoreCase)]
    private static partial Regex SoundOption();

    [GeneratedRegex(@"^(?<start>" + TimePattern + @")\s*(?:-|–|to)\s*(?<end>" + TimePattern + @")\s*:\s*(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CaptionLine();

    [GeneratedRegex(@"^(?:(?<m>\d+):(?<s>\d{1,2}(?:\.\d+)?)|(?<sec>\d+(?:\.\d+)?)\s*s?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeValue();

    // JSON for a .json file or a script that starts with '{'; Markdown otherwise.
    public static ScriptFormat Detect(string? fileName, string text)
    {
        var extension = fileName is null ? "" : Path.GetExtension(fileName);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptFormat.Json;
        }

        if (extension.Length > 0)
        {
            return ScriptFormat.Markdown;
        }

        return text.TrimStart().StartsWith('{') ? ScriptFormat.Json : ScriptFormat.Markdown;
    }

    public static EpisodeScript Parse(string text, ScriptFormat format, List<string> problems) =>
        format == ScriptFormat.Json ? ParseJson(text, problems) : ParseMarkdown(text, problems);

    public static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        var match = TimeValue().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        seconds = match.Groups["sec"].Success
            ? double.Parse(match.Groups["sec"].Value, CultureInfo.InvariantCulture)
            : int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60 + double.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static EpisodeScript ParseJson(string text, List<string> problems)
    {
        EpisodeScript script;
        try
        {
            script = JsonSerializer.Deserialize<EpisodeScript>(text, Json) ?? throw new ZeyoCutException("the script is empty");
        }
        catch (JsonException ex)
        {
            throw new ZeyoCutException($"the script isn't valid JSON: {ex.Message}");
        }

        Unknown("the script", script.Unknown, problems);
        script.Scenes ??= [];
        script.Scenes.RemoveAll(s => s is null);
        for (var i = 0; i < script.Scenes.Count; i++)
        {
            var scene = script.Scenes[i];
            scene.Where = $"scene {i + 1}{(string.IsNullOrWhiteSpace(scene.Name) ? "" : $" '{scene.Name.Trim()}'")}";
            Unknown(scene.Where, scene.Unknown, problems);
            scene.Rigs = Number(scene.Rigs, scene.Where, "rig", (r, w) => r.Where = w, r => r.Unknown, problems);
            scene.Actions = Number(scene.Actions, scene.Where, "action", (a, w) => a.Where = w, a => a.Unknown, problems);
            scene.Sfx = Number(scene.Sfx, scene.Where, "sfx", (s, w) => s.Where = w, s => s.Unknown, problems);
            scene.Captions = Number(scene.Captions, scene.Where, "caption", (c, w) => c.Where = w, c => c.Unknown, problems);
        }

        return script;
    }

    private static List<T> Number<T>(List<T>? items, string scene, string kind, Action<T, string> where, Func<T, Dictionary<string, JsonElement>?> unknown, List<string> problems)
    {
        var list = (items ?? []).Where(i => i is not null).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var place = $"{scene}, {kind} {i + 1}";
            where(list[i], place);
            Unknown(place, unknown(list[i]), problems);
        }

        return list;
    }

    private static void Unknown(string where, Dictionary<string, JsonElement>? fields, List<string> problems)
    {
        foreach (var key in fields?.Keys ?? Enumerable.Empty<string>())
        {
            problems.Add($"{where}: unknown field '{key}'");
        }
    }

    private static EpisodeScript ParseMarkdown(string text, List<string> problems)
    {
        var script = new EpisodeScript();
        ScriptScene? scene = null;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var n = 1; n <= lines.Length; n++)
        {
            var line = lines[n - 1].Trim();
            var where = $"line {n}";
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                scene = Scene(line[3..].Trim(), where, problems);
                script.Scenes.Add(scene);
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                if (script.Title is null && scene is null)
                {
                    script.Title = line[2..].Trim();
                }
                else
                {
                    problems.Add($"{where}: only the first line starting with '# ' is the title; scenes start with '## '");
                }

                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                if (scene is null)
                {
                    problems.Add($"{where}: '{line}' comes before the first scene ('## Scene name (8s)')");
                    continue;
                }

                AddItem(scene, line[2..].Trim(), string.IsNullOrEmpty(scene.Name) ? where : $"{where} (scene '{scene.Name}')", problems);
                continue;
            }

            if (scene is null && Setting().Match(line) is { Success: true } setting)
            {
                Set(script, setting.Groups["key"].Value, setting.Groups["value"].Value.Trim(), where, problems);
            }
        }

        return script;
    }

    private static ScriptScene Scene(string heading, string where, List<string> problems)
    {
        var scene = new ScriptScene { Name = heading, Where = where };
        if (SceneDuration().Match(heading) is { Success: true } match)
        {
            scene.Name = match.Groups["name"].Value.Trim();
            if (TryTime(match.Groups["duration"].Value, out var duration))
            {
                scene.Duration = duration;
            }
            else
            {
                problems.Add($"{where}: the scene's duration '({match.Groups["duration"].Value})' isn't a time like 8s or 1:30; it runs until its last item ends");
            }
        }

        if (scene.Name.Length > 0)
        {
            scene.Where = $"{where} (scene '{scene.Name}')";
        }

        return scene;
    }

    private static void Set(EpisodeScript script, string key, string value, string where, List<string> problems)
    {
        var name = key.Replace(" ", "").ToLowerInvariant();
        int? Whole()
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            problems.Add($"{where}: {key} must be a whole number (got '{value}')");
            return null;
        }

        switch (name)
        {
            case "title":
                script.Title = value;
                break;
            case "template":
                script.Template = value;
                break;
            case "preset":
                script.Preset = value;
                break;
            case "width":
                script.Width = Whole();
                break;
            case "height":
                script.Height = Whole();
                break;
            case "fps":
                script.Fps = Whole();
                break;
            case "duration":
                if (TryTime(value, out var duration))
                {
                    script.Duration = duration;
                }
                else
                {
                    problems.Add($"{where}: duration must be a time like 90s or 1:30 (got '{value}')");
                }

                break;
            case "background":
                script.Background = value;
                break;
            case "captionstyle":
                script.CaptionStyle = value;
                break;
            default:
                problems.Add($"{where}: unknown setting '{key}'; settings: {string.Join(", ", Settings)}");
                break;
        }
    }

    private static void AddItem(ScriptScene scene, string text, string where, List<string> problems)
    {
        var item = Item().Match(text);
        if (!item.Success)
        {
            problems.Add($"{where}: '{text}' isn't a rig, action, sfx or caption line");
            return;
        }

        var kind = item.Groups["kind"].Value.ToLowerInvariant();
        var rest = item.Groups["rest"].Value;
        if (kind == "caption")
        {
            var caption = CaptionLine().Match(rest.Trim());
            if (!caption.Success)
            {
                problems.Add($"{where}: caption lines look like '- caption 2s-4s: text' (got '{text}')");
                return;
            }

            TryTime(caption.Groups["start"].Value, out var start);
            TryTime(caption.Groups["end"].Value, out var end);
            scene.Captions.Add(new ScriptCaption { Text = caption.Groups["text"].Value.Trim(), At = start, End = end, Where = where });
            return;
        }

        var name = Name().Match(rest.Trim());
        if (!name.Success)
        {
            problems.Add($"{where}: {kind} needs a name ('- {kind} <name> ...')");
            return;
        }

        var value = name.Groups["quoted"].Success ? name.Groups["quoted"].Value : name.Groups["word"].Value;
        var options = name.Groups["rest"].Value;
        double Num(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);
        double At(Group group) => TryTime(group.Value, out var seconds) ? seconds : 0;
        switch (kind)
        {
            case "rig":
                var rig = new ScriptRig { Rig = value, Where = where };
                Options(RigOption(), options, where, problems, m =>
                {
                    if (m.Groups["as"].Success)
                    {
                        rig.As = m.Groups["as"].Value;
                    }

                    if (m.Groups["x"].Success)
                    {
                        (rig.X, rig.Y) = (Num(m.Groups["x"]), Num(m.Groups["y"]));
                    }

                    if (m.Groups["scale"].Success)
                    {
                        rig.Scale = Num(m.Groups["scale"]);
                    }
                });
                scene.Rigs.Add(rig);
                break;
            case "action":
                var action = new ScriptAction { Action = value, Where = where };
                Options(ActionOption(), options, where, problems, m =>
                {
                    if (m.Groups["on"].Success)
                    {
                        action.On = m.Groups["on"].Value;
                    }

                    if (m.Groups["at"].Success)
                    {
                        action.At = At(m.Groups["at"]);
                    }

                    if (m.Groups["speed"].Success)
                    {
                        action.Speed = Num(m.Groups["speed"]);
                    }

                    if (m.Groups["loops"].Success)
                    {
                        action.Loops = int.Parse(m.Groups["loops"].Value, CultureInfo.InvariantCulture);
                    }
                });
                scene.Actions.Add(action);
                break;
            default:
                var sound = new ScriptSound { Sound = value, Where = where };
                Options(SoundOption(), options, where, problems, m =>
                {
                    if (m.Groups["at"].Success)
                    {
                        sound.At = At(m.Groups["at"]);
                    }

                    if (m.Groups["volume"].Success)
                    {
                        sound.Volume = Num(m.Groups["volume"]);
                    }
                });
                scene.Sfx.Add(sound);
                break;
        }
    }

    // Reads 'name value' options one after another; whatever doesn't match is reported.
    private static void Options(Regex option, string text, string where, List<string> problems, Action<Match> read)
    {
        var position = 0;
        while (option.Match(text, position) is { Success: true } match && match.Index == position)
        {
            read(match);
            position = match.Index + match.Length;
        }

        var left = text[position..].Trim();
        if (left.Length > 0)
        {
            problems.Add($"{where}: didn't understand '{left}'");
        }
    }
}
