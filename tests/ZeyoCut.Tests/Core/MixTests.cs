// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class MixTests
{
    private static KeyframeEntry Key(string layer, double time, double value, string property = "volume", string? easing = null) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value), easing);

    private static string Refused(Project project, ProjectOptions options) =>
        Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateProject(project, options)).Message;

    private static T Layer<T>(Project project, string id) where T : Layer => Assert.IsType<T>(project.FindLayer(id));

    [Fact]
    public void EachRoleHasAGainAPanAndAMuteInTheProject()
    {
        var project = TestProjects.Footage();
        Assert.Equal((0.0, 0.0, false), (project.Audio.Music.Gain, project.Audio.Music.Pan, project.Audio.Music.Muted));

        var update = ProjectEditor.UpdateProject(project, new ProjectOptions { Role = AudioRole.Music, Gain = -6, Pan = -0.5, Muted = true });
        Assert.Equal(["music gain 0 dB → -6 dB", "music pan center → 50% left", "music muted"], update.Changes);
        Assert.Same(project.Audio.Music, project.Audio.For(AudioRole.Music));
        Assert.Equal((-6.0, -0.5, true), (project.Audio.Music.Gain, project.Audio.Music.Pan, project.Audio.Music.Muted));
        Assert.Equal((0.0, 0.0, false), (project.Audio.Voice.Gain, project.Audio.Voice.Pan, project.Audio.Voice.Muted));

        var again = ProjectEditor.UpdateProject(project, new ProjectOptions { Role = AudioRole.Music, Gain = 3, Pan = 1, Muted = false });
        Assert.Equal(["music gain -6 dB → +3 dB", "music pan 50% left → 100% right", "music unmuted"], again.Changes);

        var json = JsonNode.Parse(ProjectJson.Serialize(project))!["audio"]!;
        Assert.Equal(3, json["music"]!["gain"]!.GetValue<double>());
        Assert.Equal(1, json["music"]!["pan"]!.GetValue<double>());
        var restored = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Assert.Equal((3.0, 1.0, false), (restored.Audio.Music.Gain, restored.Audio.Music.Pan, restored.Audio.Music.Muted));

        // A project without them (format 8) gets every role as it is.
        var node = JsonNode.Parse(ProjectJson.Serialize(project))!;
        node["audio"]!.AsObject().Remove("music");
        var older = ProjectJson.Deserialize(node.ToJsonString());
        Assert.Equal((0.0, 0.0, false), (older.Audio.Music.Gain, older.Audio.Music.Pan, older.Audio.Music.Muted));

        Assert.Equal("gain must be between -60 and 6 dB (got 7)", Refused(project, new ProjectOptions { Role = AudioRole.Voice, Gain = 7 }));
        Assert.Equal("pan must be between -1 (left) and 1 (right) (got 1.5)", Refused(project, new ProjectOptions { Role = AudioRole.Voice, Pan = 1.5 }));
        Assert.Equal("gain, pan and muted set a role's mix; pass role too (voice, music or effects)", Refused(project, new ProjectOptions { Gain = -3 }));
        Assert.Equal("role picks whose gain, pan and muted change; pass at least one of them", Refused(project, new ProjectOptions { Role = AudioRole.Voice }));

        project.Audio.Effects.Pan = 2;
        project.Audio.Voice.Gain = -70;
        var errors = ProjectValidator.Validate(project);
        Assert.Contains("audio.effects.pan must be between -1 and 1 (got 2)", errors);
        Assert.Contains("audio.voice.gain must be between -60 and 6 dB (got -70)", errors);
    }

    [Fact]
    public void DuckingHasALevelAndFades()
    {
        var project = TestProjects.Footage();
        Assert.Equal((-12.0, 0.5, 1.5), (project.Audio.DuckLevel, project.Audio.DuckFadeIn, project.Audio.DuckFadeOut));

        var update = ProjectEditor.UpdateProject(project, new ProjectOptions { DuckLevel = -18, DuckFadeIn = 0.25, DuckFadeOut = 2 });
        Assert.Equal(["duck level -12 dB → -18 dB", "duck fade in 0.5s → 0.25s", "duck fade out 1.5s → 2s"], update.Changes);
        var restored = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Assert.Equal((-18.0, 0.25, 2.0), (restored.Audio.DuckLevel, restored.Audio.DuckFadeIn, restored.Audio.DuckFadeOut));

        Assert.Equal("duckLevel must be between -40 and 0 dB (got -50)", Refused(project, new ProjectOptions { DuckLevel = -50 }));
        Assert.Equal("duckLevel must be between -40 and 0 dB (got 3)", Refused(project, new ProjectOptions { DuckLevel = 3 }));
        Assert.Equal("duckFadeIn must be between 0 and 5 seconds (got 6)", Refused(project, new ProjectOptions { DuckFadeIn = 6 }));
        Assert.Equal("duckFadeOut must be between 0 and 5 seconds (got -1)", Refused(project, new ProjectOptions { DuckFadeOut = -1 }));

        project.Audio.DuckLevel = -41;
        project.Audio.DuckFadeOut = 9;
        var errors = ProjectValidator.Validate(project);
        Assert.Contains("audio.duckLevel must be between -40 and 0 dB (got -41)", errors);
        Assert.Contains("audio.duckFadeOut must be between 0 and 5 seconds (got 9)", errors);
    }

    [Fact]
    public void VolumeIsKeyframedOnSoundAndOnFootage()
    {
        var project = TestProjects.Footage();
        var change = KeyframeEditor.SetKeyframes(project, [Key("music", 0, 0.8), Key("music", 2, 0.2, easing: "easeInOut"), Key("clip_2", 5, 1), Key("clip_2", 6, 0)]);
        Assert.Equal((4, 0), (change.Added, change.Replaced));

        var music = Layer<AudioLayer>(project, "music");
        Assert.Equal(0.8, music.Volume.Value);
        Assert.Equal(0.5, Interpolator.Evaluate(music.Volume, 1), 6);
        Assert.Equal(0.2, Interpolator.Evaluate(music.Volume, 5), 6);
        Assert.Equal(0.5, Interpolator.Evaluate(Layer<VideoLayer>(project, "clip_2").Volume, 5.5), 6);
        Assert.Contains(AnimatableProperties.Numeric(Layer<VideoLayer>(project, "clip_2")), p => p.Name == "volume");
        Assert.Equal(["volume"], AnimatableProperties.Keyed(music).Select(p => p.Name));
        Assert.Contains("keys: volume(2)", LayerTree.Format(project));
        Assert.Contains("volume (audio layers and video layers' own sound", AnimatableProperties.Describe());

        // As JSON, a volume is a value with keyframes, like every animated value.
        var json = JsonNode.Parse(ProjectJson.LayerToJson(music))!;
        Assert.Equal(0.8, json["volume"]!["value"]!.GetValue<double>());
        Assert.Equal(2, json["volume"]!["keyframes"]!.AsArray().Count);

        // update_layer sets the value under the keyframes.
        Assert.Equal(["volume has 2 keyframe(s), so the static value you set is only used after clear_keyframes"],
            ProjectEditor.UpdateLayer(project, "music", new JsonObject { ["volume"] = 0.5 }));
        Assert.Equal(0.5, Layer<AudioLayer>(project, "music").Volume.Value);

        // Moving a sound moves its keyframes; splitting it gives each piece its own.
        TimelineEditor.MoveLayer(project, "music", 1);
        Assert.Equal([1.0, 3.0], Layer<AudioLayer>(project, "music").Volume.Keyframes.Select(k => k.Time));
        var heard = Interpolator.Evaluate(Layer<AudioLayer>(project, "music").Volume, 2.5);
        var split = TimelineEditor.SplitLayer(project, "music", 2);
        var second = Layer<AudioLayer>(project, split.RightId);
        Assert.Equal(2, second.Start);
        Assert.Equal(heard, Interpolator.Evaluate(second.Volume, 2.5), 6);
        Assert.Equal([1.0, 2.0], Layer<AudioLayer>(project, "music").Volume.Keyframes.Select(k => k.Time));

        // Retiming, easing, removing and clearing work on a volume as on any value.
        Assert.Equal("moved music.volume's keyframe from 1s to 1.5s, value 0.6", KeyframeEditor.MoveKeyframes(project, [new KeyframeMove("music", "volume", 1, 1.5, 0.6)]));
        Assert.Equal(1, KeyframeEditor.SetEasing(project, [new KeyframeRef("music", "volume", 1.5)], Easing.Hold));
        Assert.Equal(1, KeyframeEditor.RemoveKeyframes(project, [new KeyframeRef("clip_2", "volume", 6)]));
        Assert.Equal(1, KeyframeEditor.ClearKeyframes(project, "clip_2", "volume"));

        Assert.Contains("'music' is an audio layer, whose only animatable property is volume (got 'x')",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [Key("music", 1, 5, property: "x")])).Message);
        Assert.Contains("'volume' is only on audio and video layers; 'title' is a text layer",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [Key("title", 1, 1)])).Message);

        KeyframeEditor.SetKeyframes(project, [Key("music", 4, 11), Key("clip_1", 1, -1)]);
        var errors = ProjectValidator.Validate(project);
        Assert.Contains("layer 'music'.volume: keyframe values must be between 0 and 10 (1 = original level)", errors);
        Assert.Contains("layer 'clip_1'.volume: keyframe values must be between 0 and 10 (1 = original level)", errors);
    }

    [Fact]
    public void AVolumeWrittenAsANumberReadsAsAValueWithoutKeyframes()
    {
        var json = ProjectJson.Serialize(TestProjects.Footage());
        var old = Regex.Replace(json, @"""volume"": \{\s*""value"": ([0-9.]+)\s*\}", @"""volume"": $1");
        Assert.NotEqual(json, old);
        Assert.Contains(@"""volume"": 0.8,", old);

        var project = ProjectJson.Deserialize(old);
        Assert.Equal(0.8, Layer<AudioLayer>(project, "music").Volume.Value);
        Assert.Empty(Layer<AudioLayer>(project, "music").Volume.Keyframes);
        Assert.Equal(json, ProjectJson.Serialize(project));
    }

    [Fact]
    public void TheEditorsProjectSettingsAreUpdateProjects()
    {
        var options = new ProjectOptions { Role = AudioRole.Effects, Gain = -4.5, Pan = 0.25 };
        var byEditor = TestProjects.Footage();
        var operation = new UpdateProjectOperation(options);
        Assert.Equal("update_project", operation.Label);
        Assert.Empty(operation.Layers);
        Assert.Equal("effects gain 0 dB → -4.5 dB; effects pan center → 25% right", operation.Apply(byEditor));

        var byTool = TestProjects.Footage();
        ProjectEditor.UpdateProject(byTool, options);
        Assert.Equal(ProjectJson.Serialize(byTool), ProjectJson.Serialize(byEditor));
    }
}
