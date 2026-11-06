// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class ScriptTests
{
    private sealed class FakeProbe : IAssetProbe
    {
        public AssetInfo Probe(string path, AssetType type) => type == AssetType.Audio ? new AssetInfo(Duration: 0.5) : new AssetInfo(Width: 200, Height: 100);
    }

    private const string Markdown = """
        # Tora's Big Jump
        preset: youtube_1080p
        captionStyle: minimal
        Notes for the animator: keep it bouncy.
        speed: fast

        ## The ledge (8s)
        Tora looks down.
        - rig tora as hero at 600,300 scale 0.5
        - action hop on hero at 1s loops 2
        - action flip on hero at 2s
        - sfx impacts/boing at 3.5s volume 0.8
        - sfx zap at 4s
        - caption 0-2s: Here goes!
        - caption 9s-10s: Too late
        - dance wildly
        - action hop on hero at 1:00 speed 2 sideways

        ## The fall
        - rig "tora"
        - action hop at 0.5
        - caption 1s to 0:03: Wheee!
        """;

    // A library with the test rig 'tora' (tora_body with tora_head and tora_tail), a one-second
    // action 'hop' and a sound effect 'impacts/boing'.
    internal static Library MakeLibrary(string folder)
    {
        var library = new Library(folder);
        var directory = TestPaths.NewDirectory(Path.GetFileName(folder) + "-rig");
        var project = TestProjects.Rig();
        project.Duration = 4;
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        foreach (var asset in project.Assets)
        {
            TestImages.Solid(Path.Combine(directory, "assets", asset.Id + ".png"), 200, 100, SKColors.Orange);
        }

        var body = (ImageLayer)project.FindLayer("tora_body")!;
        body.Y.Keyframes = [new Keyframe { Time = 0, Value = 200 }, new Keyframe { Time = 0.5, Value = 150 }, new Keyframe { Time = 1, Value = 200 }];
        Rigs.Save(project, directory, library, "tora_body", "tora", time: null, overwrite: false);
        RigActions.Save(project, library, "tora_body", 0, 1, "hop", overwrite: false);
        Directory.CreateDirectory(Path.Combine(library.SfxFolder, "impacts"));
        TestAudio.WriteWav(Path.Combine(library.SfxFolder, "impacts", "boing.wav"), TestAudio.Tone(0.5, 48000, _ => true), 48000);
        return library;
    }

    [Fact]
    public void TimesAreSecondsOrMinutesAndSeconds()
    {
        foreach (var (text, seconds) in new[] { ("8", 8.0), ("8s", 8), ("2.5s", 2.5), ("1:30", 90), ("0:05.5", 5.5), (" 12 s ", 12) })
        {
            Assert.True(EpisodeScripts.TryTime(text, out var parsed), text);
            Assert.Equal(seconds, parsed, 9);
        }

        Assert.False(EpisodeScripts.TryTime("soon", out _));
        Assert.False(EpisodeScripts.TryTime("-2s", out _));
        Assert.False(EpisodeScripts.TryTime("1:5:0", out _));
    }

    [Fact]
    public void MarkdownScriptsAreReadLineByLine()
    {
        var problems = new List<string>();
        var script = EpisodeScripts.Parse(Markdown, ScriptFormat.Markdown, problems);

        Assert.Equal("Tora's Big Jump", script.Title);
        Assert.Equal("youtube_1080p", script.Preset);
        Assert.Equal("minimal", script.CaptionStyle);
        Assert.Equal(2, script.Scenes.Count);
        var ledge = script.Scenes[0];
        Assert.Equal("The ledge", ledge.Name);
        Assert.Equal(8, ledge.Duration);
        Assert.Equal("line 7 (scene 'The ledge')", ledge.Where);
        var rig = Assert.Single(ledge.Rigs);
        Assert.Equal(("tora", "hero", 600.0, 300.0, 0.5), (rig.Rig, rig.As, rig.X!.Value, rig.Y!.Value, rig.Scale!.Value));
        Assert.Equal(["hop", "flip", "hop"], ledge.Actions.Select(a => a.Action));
        Assert.Equal((1.0, 2), (ledge.Actions[0].At!.Value, ledge.Actions[0].Loops!.Value));
        Assert.Equal((60.0, 2.0), (ledge.Actions[2].At!.Value, ledge.Actions[2].Speed!.Value));
        Assert.Equal(["impacts/boing", "zap"], ledge.Sfx.Select(s => s.Sound));
        Assert.Equal(0.8, ledge.Sfx[0].Volume);
        Assert.Equal([("Here goes!", 0.0, 2.0), ("Too late", 9, 10)], ledge.Captions.Select(c => (c.Text!, c.At!.Value, c.End!.Value)));
        var fall = script.Scenes[1];
        Assert.Null(fall.Duration);
        Assert.Equal("tora", Assert.Single(fall.Rigs).Rig);
        Assert.Null(fall.Actions[0].On);
        Assert.Equal([("Wheee!", 1.0, 3.0)], fall.Captions.Select(c => (c.Text!, c.At!.Value, c.End!.Value)));
        Assert.Equal([
            "line 5: unknown setting 'speed'; settings: template, preset, width, height, fps, duration, background, captionStyle",
            "line 16 (scene 'The ledge'): 'dance wildly' isn't a rig, action, sfx or caption line",
            "line 17 (scene 'The ledge'): didn't understand 'sideways'",
        ], problems);
    }

    [Fact]
    public void JsonScriptsReportFieldsTheyDontKnow()
    {
        var problems = new List<string>();
        var script = EpisodeScripts.Parse("""
            {
              "title": "Tora's Big Jump", "colour": "red",
              // scenes follow each other
              "scenes": [
                { "name": "The ledge", "duration": 8, "rigs": [{ "rig": "tora", "as": "hero", "size": 2 }],
                  "actions": [{ "action": "hop", "on": "hero", "at": 1 }], "sfx": null, "captions": [{ "text": "Here goes!", "at": 0, "end": 2 }] },
                { "duration": 4, "camera": "pan" },
              ],
            }
            """, ScriptFormat.Json, problems);
        Assert.Equal(2, script.Scenes.Count);
        Assert.Equal("scene 1 'The ledge'", script.Scenes[0].Where);
        Assert.Equal("scene 1 'The ledge', action 1", script.Scenes[0].Actions[0].Where);
        Assert.Empty(script.Scenes[0].Sfx);
        Assert.Equal(["the script: unknown field 'colour'", "scene 1 'The ledge', rig 1: unknown field 'size'", "scene 2: unknown field 'camera'"], problems);

        Assert.StartsWith("the script isn't valid JSON: ", Assert.Throws<ZeyoCutException>(() => EpisodeScripts.Parse("{ \"scenes\": [ }", ScriptFormat.Json, [])).Message);
        Assert.Equal(ScriptFormat.Json, EpisodeScripts.Detect("episode.JSON", "# not json"));
        Assert.Equal(ScriptFormat.Markdown, EpisodeScripts.Detect("episode.md", "{"));
        Assert.Equal(ScriptFormat.Json, EpisodeScripts.Detect(null, "  {\"scenes\": []}"));
        Assert.Equal(ScriptFormat.Markdown, EpisodeScripts.Detect(null, "# Title"));
    }

    [Fact]
    public void AScriptIsPlannedThenBuiltAndWhatCantBeMappedIsListed()
    {
        var library = MakeLibrary(TestPaths.NewDirectory("script-library"));
        var probe = new FakeProbe();
        var problems = new List<string>();
        var plan = ScriptBuilder.Plan(EpisodeScripts.Parse(Markdown, ScriptFormat.Markdown, problems), library, probe, problems);

        Assert.Equal(11, plan.Duration);
        Assert.Equal(Presets.Youtube1080p, plan.Preset);
        Assert.Equal([(0.0, 8.0), (8, 11)], plan.Scenes.Select(s => (s.Start, s.End)));
        Assert.Equal([(1.0, 3.0)], plan.Scenes[0].Actions.Select(a => (a.Start, a.End)));
        Assert.Equal((8.5, 9.5, "tora"), (plan.Scenes[1].Actions[0].Start, plan.Scenes[1].Actions[0].End, plan.Scenes[1].Actions[0].On));
        Assert.Equal([(3.5, 4.0)], plan.Scenes[0].Sounds.Select(s => (s.Start, s.End)));
        Assert.Equal(7, problems.Count);
        Assert.Equal("line 11 (scene 'The ledge'): no action named 'flip' in the library; actions: hop", problems[3]);
        Assert.Equal("line 13 (scene 'The ledge'): no sound effect named 'zap'; sound effects: impacts/boing", problems[4]);
        Assert.Equal("line 17 (scene 'The ledge'): action 'hop' starts at or after the end of its scene (8s)", problems[5]);
        Assert.Equal("line 15 (scene 'The ledge'): the caption \"Too late\" starts at or after the end of its scene (8s)", problems[6]);

        var directory = TestPaths.NewDirectory("script-project");
        var project = new Project { Name = "jump", Width = 640, Height = 360, Fps = 30, Duration = plan.Duration };
        var result = ScriptBuilder.Build(project, directory, library, plan, probe, null);

        Assert.Empty(result.Problems);
        Assert.Equal("scene 1 \"The ledge\" 0–8s: rig tora as hero_body; hop on hero_body 1–3s (2 loops); impacts/boing at 3.5s as boing; 1 caption", result.Scenes[0]);
        Assert.Equal("scene 2 \"The fall\" 8–11s: rig tora as tora_body; hop on tora_body 8.5–9.5s; 1 caption", result.Scenes[1]);
        Assert.All(project.Layers.Where(l => l.Id.StartsWith("hero_", StringComparison.Ordinal)), l => Assert.Equal((0.0, 8.0), (l.Start, l.End!.Value)));
        Assert.All(project.Layers.Where(l => l.Id.StartsWith("tora_", StringComparison.Ordinal)), l => Assert.Equal((8.0, (double?)null), (l.Start, l.End)));
        Assert.Equal(0.5, ((ImageLayer)project.FindLayer("hero_body")!).ScaleX.Value);
        Assert.Equal(new[] { 1.0, 1.5, 2, 2.5, 3 }, ((ImageLayer)project.FindLayer("hero_body")!).Y.Keyframes.Select(k => k.Time));
        Assert.Equal(3.5, project.FindLayer("boing")!.Start);
        Assert.Equal(["the_ledge@0", "the_fall@8"], project.Markers.Select(m => $"{m.Id}@{m.Time}"));
        Assert.All(project.Markers, m => Assert.Equal(MarkerKind.Scene, m.Kind));
        Assert.Equal("captions", result.CaptionGroup);
        Assert.Equal([("Here goes!", 0.0, 2.0), ("Wheee!", 9, 11)], project.Layers.OfType<TextLayer>().Select(t => (t.Text, t.Start, t.End!.Value)));
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void ScenesWithoutTimeAndScriptsWithoutScenesAreRefused()
    {
        var library = MakeLibrary(TestPaths.NewDirectory("script-refused"));
        var problems = new List<string>();
        Assert.Equal("the script has no scenes; a Markdown scene starts with '## Scene name (8s)', a JSON one is an entry of \"scenes\"",
            Assert.Throws<ZeyoCutException>(() => ScriptBuilder.Plan(EpisodeScripts.Parse("# Empty\nNothing yet.", ScriptFormat.Markdown, problems), library, new FakeProbe(), problems)).Message);

        problems.Clear();
        var plan = ScriptBuilder.Plan(EpisodeScripts.Parse("# Gaps\nduration: 20\n## Empty\n## Short (2s)\n- rig tora\n- action hop at -1\n- action hop at 1 speed 0\n- caption 1-1: Same time\n## Missing (abc)\n- rig nobody\n- action hop on nobody at 0", ScriptFormat.Markdown, problems), library, new FakeProbe(), problems);
        Assert.Equal(20, plan.Duration);
        Assert.Equal([("Short", 0.0, 2.0)], plan.Scenes.Select(s => (s.Name, s.Start, s.End)));
        Assert.Equal([
            "line 6 (scene 'Short'): didn't understand 'at -1'",
            "line 9: the scene's duration '(abc)' isn't a time like 8s or 1:30; it runs until its last item ends",
            "line 3 (scene 'Empty'): the scene has no duration and nothing timed in it, so it was left out",
            "line 6 (scene 'Short'): action 'hop' needs a time ('at')",
            "line 7 (scene 'Short'): speed must be more than 0 (got 0)",
            "line 8 (scene 'Short'): the caption \"Same time\" ends at 1s, not after it starts (1s)",
            "line 10 (scene 'Missing'): no rig 'nobody' in the library; rigs: tora",
            "line 11 (scene 'Missing'): action 'hop' is on rig 'nobody', which couldn't be placed",
            "line 9 (scene 'Missing'): the scene has no duration and nothing timed in it, so it was left out",
        ], problems);
    }
}
