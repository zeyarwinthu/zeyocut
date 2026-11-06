// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class DeliveryTests
{
    private static Asset Clip(string id = "clip") => new()
    {
        Id = id,
        Type = AssetType.Video,
        File = $"assets/{id}.mp4",
        Width = 1920,
        Height = 1080,
        FrameRate = "30",
        FrameCount = 300,
        Duration = 10,
        HasAudio = true,
    };

    [Fact]
    public void PresetsSetTheCanvasAndLoudness()
    {
        var project = TestProjects.Empty(width: 1920, height: 1080, fps: 25);
        project.Assets.Add(Clip());
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip" });
        project.Audio.LoudnessTarget = null;

        var update = ProjectEditor.UpdateProject(project, new ProjectOptions { Preset = "Shorts_1080p" });

        Assert.Equal((1080, 1920, 30), (project.Width, project.Height, project.Fps));
        Assert.Equal(-14, project.Audio.LoudnessTarget);
        Assert.Contains("loudness off → -14 LUFS", update.Changes);
        Assert.Contains(update.Warnings, w => w.Contains("reframe_to_vertical"));

        var explicitFps = ProjectEditor.UpdateProject(project, new ProjectOptions { Preset = "youtube_1080p", Fps = 60 });
        Assert.Equal((1920, 1080, 60), (project.Width, project.Height, project.Fps));
        Assert.Contains("fps 30 → 60", explicitFps.Changes);

        Assert.Contains("unknown preset 'tiktok'; presets: youtube_1080p, shorts_1080p",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateProject(project, new ProjectOptions { Preset = "tiktok" })).Message);
    }

    [Fact]
    public void LoudnessAndDuckingAreProjectSettings()
    {
        var project = TestProjects.Empty();
        Assert.Equal(-14, project.Audio.LoudnessTarget);
        Assert.True(project.Audio.Ducking);

        var off = ProjectEditor.UpdateProject(project, new ProjectOptions { LoudnessTarget = 0, Ducking = false });
        Assert.Null(project.Audio.LoudnessTarget);
        Assert.False(project.Audio.Ducking);
        Assert.Equal(["loudness -14 LUFS → off", "music ducking off"], off.Changes);

        var restored = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Assert.Null(restored.Audio.LoudnessTarget);
        Assert.False(restored.Audio.Ducking);

        ProjectEditor.UpdateProject(project, new ProjectOptions { LoudnessTarget = -16 });
        Assert.Equal(-16, project.Audio.LoudnessTarget);
        Assert.Contains("loudnessTarget must be between -40 and -5 LUFS",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateProject(project, new ProjectOptions { LoudnessTarget = 3 })).Message);
        Assert.Contains("nothing changed",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateProject(project, new ProjectOptions { LoudnessTarget = -16 })).Message);

        project.Audio.TruePeak = 2;
        Assert.Contains(ProjectValidator.Validate(project), e => e.Contains("audio.truePeak must be between -9 and 0"));
    }

    [Fact]
    public void OlderProjectsGetDefaultAudioSettingsAndRoles()
    {
        const string json = """
        {
          "formatVersion": 1, "name": "old", "width": 320, "height": 240, "fps": 24, "duration": 2,
          "assets": [ { "id": "song", "type": "audio", "file": "assets/song.mp3", "duration": 5 } ],
          "layers": [ { "type": "audio", "id": "music", "asset": "song" } ]
        }
        """;

        var project = ProjectJson.Deserialize(json);

        Assert.Equal(-14, project.Audio.LoudnessTarget);
        Assert.True(project.Audio.Ducking);
        Assert.Equal(AudioRole.Music, ((AudioLayer)project.Layers[0]).Role);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void RolesDefaultByLayerTypeAndCanBeChanged()
    {
        var project = TestProjects.Empty();
        project.Assets.Add(Clip());
        project.Assets.Add(new Asset { Id = "vo", Type = AssetType.Audio, File = "assets/vo.wav", Duration = 2 });

        Assert.Equal(AudioRole.Voice, ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip" }).Role);
        Assert.Equal(AudioRole.Music, ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Role = "music" }).Role);
        Assert.Equal(AudioRole.Music, ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "vo" }).Role);
        Assert.Equal(AudioRole.Voice, ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "vo", Id = "narration", Role = "Voice" }).Role);
        Assert.Contains("role",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "vo", Role = "singer" })).Message);

        ProjectEditor.UpdateLayer(project, "narration", new JsonObject { ["role"] = "effects" });
        Assert.Equal(AudioRole.Effects, ((AudioLayer)project.FindLayer("narration")!).Role);
    }

    [Fact]
    public void ReframeCropsAFullHeightSliceAndKeepsPunchIn()
    {
        var project = TestProjects.Empty(width: 1920, height: 1080);
        project.Assets.Add(Clip());
        var a = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "a", TrimOut = 2 });
        var b = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "b", Start = 2, TrimIn = 2, TrimOut = 4 });
        b.ScaleX.Value *= 1.1;
        b.ScaleY.Value *= 1.1;

        Assert.Contains("switch it first (update_project preset=shorts_1080p)",
            Assert.Throws<ZeyoCutException>(() => Reframe.ToVertical(project, "all", 0.5, out _)).Message);

        ProjectEditor.UpdateProject(project, new ProjectOptions { Preset = "shorts_1080p" });
        var results = Reframe.ToVertical(project, "all", 0.5, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, results.Count);
        Assert.Equal((608.0, 1080.0), (a.CropWidth!.Value, a.CropHeight!.Value));
        Assert.Equal(656, a.CropX.Value);
        Assert.Equal(1920 / 1080.0, a.ScaleX.Value, 9);
        Assert.Equal(1920 / 1080.0 * 1.1, b.ScaleX.Value, 9);
        Assert.Equal((304.0, 540.0), (a.AnchorX, a.AnchorY));
        Assert.Equal((540.0, 960.0), (a.X.Value, a.Y.Value));
        Assert.Empty(ProjectValidator.Validate(project));

        Reframe.ToVertical(project, "a", 0, out _);
        Assert.Equal(0, a.CropX.Value);
        Reframe.ToVertical(project, "a", 1, out _);
        Assert.Equal(1920 - 608, a.CropX.Value);
        Assert.Contains("focusX must be between 0 (left) and 1 (right)",
            Assert.Throws<ZeyoCutException>(() => Reframe.ToVertical(project, "a", 2, out _)).Message);
    }
}
