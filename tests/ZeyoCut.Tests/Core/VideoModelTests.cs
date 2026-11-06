// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class VideoModelTests
{
    private static KeyframeEntry Key(string layer, string property, double time, object value) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value));

    private static Asset Clip(string id = "clip", string rate = "30", int frames = 300, int width = 1920, int height = 1080) => new()
    {
        Id = id,
        Type = AssetType.Video,
        File = $"assets/{id}.mp4",
        Width = width,
        Height = height,
        FrameRate = rate,
        FrameCount = frames,
        Duration = frames * VideoTiming.TimeOfFrame(new Asset { FrameRate = rate }, 1),
        HasAudio = true,
        Codec = "h264",
    };

    private static Project WithClip(double duration = 3)
    {
        var project = TestProjects.Empty(width: 1280, height: 720, duration: duration);
        project.Assets.Add(Clip());
        return project;
    }

    [Fact]
    public void TimingMapsTimelineTimeToSourceFrames()
    {
        var ntsc = Clip(rate: "30000/1001", frames: 600);
        var layer = new VideoLayer { Asset = "clip", Start = 2, TrimIn = 1, TrimOut = 9, Speed = 2 };

        Assert.Equal(4, layer.Length, 9);
        Assert.Equal(6, layer.TimelineEnd, 9);
        Assert.Equal(1, VideoTiming.SourceTime(layer, 2), 9);
        Assert.Equal(3, VideoTiming.SourceTime(layer, 3), 9);
        Assert.Equal(2.5, VideoTiming.TimelineTime(layer, 2), 9);

        Assert.Equal(29, VideoTiming.FrameAt(ntsc, 1));
        Assert.Equal(89, VideoTiming.FrameAt(ntsc, 3));
        Assert.Equal(30, VideoTiming.FrameAt(ntsc, VideoTiming.TimeOfFrame(ntsc, 30)));
        Assert.Equal(0, VideoTiming.FrameAt(ntsc, -1));
        Assert.Equal(599, VideoTiming.FrameAt(ntsc, 1000));

        var pal = Clip(rate: "25", frames: 250);
        for (var frame = 0; frame < 250; frame++)
        {
            Assert.Equal(frame, VideoTiming.FrameAt(pal, frame / 25.0));
        }

        Assert.Equal((30000L, 1001L), VideoTiming.ParseRate("30000/1001"));
        Assert.Equal((24L, 1L), VideoTiming.ParseRate("24"));
        Assert.Equal((29970L, 1000L), VideoTiming.ParseRate("29.97"));
        Assert.Throws<ZeyoCutException>(() => VideoTiming.ParseRate("0/0"));
    }

    [Fact]
    public void AddVideoLayerFitsTheCanvasAndExtendsTheProject()
    {
        var project = WithClip(duration: 3);
        var layer = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Start = 1, TrimIn = 2, TrimOut = 8, Speed = 2 });

        Assert.Equal("clip", layer.Id);
        Assert.Equal(3, layer.Length, 9);
        Assert.Equal(4, layer.TimelineEnd, 9);
        Assert.Equal(4, project.Duration, 9);
        Assert.Equal(960, layer.AnchorX);
        Assert.Equal(540, layer.AnchorY);
        Assert.Equal(640, layer.X.Value);
        Assert.Equal(360, layer.Y.Value);
        Assert.Equal(1280 / 1920.0, layer.ScaleX.Value, 9);
        Assert.Null(layer.End);
        Assert.Empty(ProjectValidator.Validate(project));

        var whole = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Start = 0, ScaleX = 1, ScaleY = 1 });
        Assert.Equal("clip_2", whole.Id);
        Assert.Equal(10, whole.TrimOut, 9);
        Assert.Equal(10, project.Duration, 9);
        Assert.Equal(1, whole.ScaleX.Value);

        Assert.True(SceneEvaluator.IsActive(layer, 3.99, project.Duration));
        Assert.False(SceneEvaluator.IsActive(layer, 4, project.Duration));
        Assert.False(SceneEvaluator.IsActive(layer, 0.99, project.Duration));
    }

    [Fact]
    public void AddVideoLayerRejectsBadTrimsSpeedAndAssets()
    {
        var project = WithClip();
        project.Assets.Add(new Asset { Id = "pic", Type = AssetType.Image, File = "assets/pic.png", Width = 10, Height = 10 });

        Assert.Contains("trimOut must be after trimIn (0s) and at most the source length (10s); got 12",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", TrimOut = 12 })).Message);
        Assert.Contains("trimIn must be between 0 and the source length",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", TrimIn = 10 })).Message);
        Assert.Contains("speed must be between 0.1 and 10; got 20",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Speed = 20 })).Message);
        Assert.Contains("asset 'pic' is a image asset, not video",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "pic" })).Message);
        Assert.Contains("Import files with import_video first",
            Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "nope" })).Message);
    }

    [Fact]
    public void ValidatorChecksVideoLayers()
    {
        var project = WithClip();
        project.Layers.Add(new VideoLayer { Id = "fast", Asset = "clip", TrimOut = 5, Speed = 20 });
        project.Layers.Add(new VideoLayer { Id = "long", Asset = "clip", TrimOut = 11 });
        project.Layers.Add(new VideoLayer { Id = "half_crop", Asset = "clip", TrimOut = 5, CropWidth = 100 });
        project.Layers.Add(new VideoLayer { Id = "big_crop", Asset = "clip", TrimOut = 5, CropWidth = 4000, CropHeight = 100 });
        project.Layers.Add(new VideoLayer { Id = "ok", Asset = "clip", TrimOut = 5, CropWidth = 608, CropHeight = 1080 });

        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("layer 'fast': speed must be between 0.1 and 10"));
        Assert.Contains(errors, e => e.Contains("layer 'long': trimOut (11) is past the end of 'clip'"));
        Assert.Contains(errors, e => e.Contains("layer 'half_crop': set both cropWidth and cropHeight"));
        Assert.Contains(errors, e => e.Contains("layer 'big_crop': the crop (4000x100) is larger than the source (1920x1080)"));
        Assert.DoesNotContain(errors, e => e.Contains("layer 'ok'"));
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void VideoLayersRoundTripWithoutAnEndField()
    {
        var project = WithClip();
        var layer = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", TrimIn = 1, TrimOut = 4, Speed = 1.5, Volume = 0.5, Muted = true, CropWidth = 608, CropHeight = 1080 });
        KeyframeEditor.SetKeyframes(project, [Key("clip", "cropX", 0, 0), Key("clip", "cropX", 2, 1312), Key("clip", "brightness", 1, -1)]);

        var node = ProjectJson.LayerToNode(layer);
        Assert.False(node.ContainsKey("end"));
        Assert.Equal("video", node["type"]!.GetValue<string>());

        var json = ProjectJson.Serialize(project);
        var restored = ProjectJson.Deserialize(json);
        Assert.Equal(json, ProjectJson.Serialize(restored));
        var video = Assert.IsType<VideoLayer>(restored.FindLayer("clip"));
        Assert.Equal(1.5, video.Speed);
        Assert.True(video.Muted);
        Assert.Equal(608, video.CropWidth);
        Assert.Equal(1312, video.CropX.Keyframes[1].Value);
        Assert.Equal(-1, video.Brightness.Keyframes[0].Value);
        Assert.Equal("30", restored.FindAsset("clip")!.FrameRate);
        Assert.Equal(300, restored.FindAsset("clip")!.FrameCount);

        var crop = VideoTiming.Crop(video, restored.FindAsset("clip")!, 1);
        Assert.Equal(new CropRect(656, 0, 608, 1080), crop);
        Assert.Equal(1312, VideoTiming.Crop(video, restored.FindAsset("clip")!, 5).X);
    }

    [Fact]
    public void FormatVersionOneProjectsStillOpen()
    {
        const string json = """
        {
          "formatVersion": 1,
          "name": "old",
          "width": 320, "height": 240, "fps": 24, "duration": 2,
          "assets": [ { "id": "dot", "type": "image", "file": "assets/dot.png", "width": 10, "height": 10 } ],
          "layers": [ { "type": "image", "id": "dot", "drawings": { "normal": "dot" }, "drawing": { "value": "normal" } } ]
        }
        """;

        var project = ProjectJson.Deserialize(json);
        Assert.Equal(Project.CurrentFormatVersion, project.FormatVersion);
        Assert.Equal(6, Project.CurrentFormatVersion);
        var dot = Assert.IsType<ImageLayer>(project.Layers.Single());
        Assert.Equal(0, dot.Brightness.Value);
        Assert.Equal(1, dot.Contrast.Value);
        Assert.Equal(1, dot.Saturation.Value);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void UpdateLayerHasVideoFieldsButNoEnd()
    {
        var project = WithClip();
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip" });

        var ex = Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateLayer(project, "clip", new JsonObject { ["end"] = 2 }));
        Assert.Contains("unknown field 'end' for video layer 'clip'", ex.Message);
        Assert.Contains("trimOut", ex.Message);
        Assert.Contains("cropWidth", ex.Message);
        Assert.Contains("saturation", ex.Message);

        ProjectEditor.UpdateLayer(project, "clip", new JsonObject { ["trimOut"] = 6, ["speed"] = 3, ["brightness"] = 0.25 });
        var video = (VideoLayer)project.FindLayer("clip")!;
        Assert.Equal(2, video.Length, 9);
        Assert.Equal(0.25, video.Brightness.Value);
    }

    [Fact]
    public void ColorAndCropPropertiesAreOnlyOnTheRightLayers()
    {
        var project = TestProjects.Rig();
        project.Assets.Add(Clip());
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "video" });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi" });

        var change = KeyframeEditor.SetKeyframes(project,
        [
            Key("tora_body", "brightness", 0, 0),
            Key("tora_body", "Saturation", 1, 0),
            Key("video", "contrast", 0, 1.5),
            Key("video", "cropY", 0, 10),
        ]);
        Assert.Equal(4, change.Added);

        Assert.Contains("'brightness' is only on image and video layers; 'title' is a text layer",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [Key("title", "brightness", 0, 1)])).Message);
        Assert.Contains("'cropX' is only on video layers; 'tora_body' is a image layer",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [Key("tora_body", "cropX", 0, 1)])).Message);

        Assert.Equal(0, KeyframeEditor.ClearKeyframes(project, "title", null));
        Assert.Equal(2, KeyframeEditor.ClearKeyframes(project, "video", null));
        Assert.Contains("cropX, cropY (video layers)", AnimatableProperties.Describe());
    }

    [Fact]
    public void ImportAssetPointsVideosToImportVideo()
    {
        var directory = TestPaths.NewDirectory("video-import-routing");
        var clip = Path.Combine(directory, "clip.mp4");
        var picture = Path.Combine(directory, "still.png");
        File.WriteAllBytes(clip, [0]);
        File.WriteAllBytes(picture, [0]);
        var project = TestProjects.Empty();

        Assert.Contains("is a video; use import_video",
            Assert.Throws<ZeyoCutException>(() => AssetImporter.Import(project, directory, clip, null, new FakeAssetProbe())).Message);
        Assert.Contains("is not a video file",
            Assert.Throws<ZeyoCutException>(() => AssetImporter.CheckSource(picture, AssetType.Video)).Message);
        Assert.Equal(Path.GetFullPath(clip), AssetImporter.CheckSource(clip, AssetType.Video));
    }
}
