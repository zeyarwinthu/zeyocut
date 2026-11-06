// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class JsonRoundTripTests
{
    private static Project FullProject()
    {
        var project = TestProjects.Rig();
        project.Assets.Add(new Asset { Id = "boing", Type = AssetType.Audio, File = "assets/boing.wav", Duration = 0.5 });
        project.Assets.Add(new Asset { Id = "myfont", Type = AssetType.Font, File = "assets/my.ttf", FontFamily = "My Font" });

        var head = (ImageLayer)project.FindLayer("tora_head")!;
        head.Rotation.Keyframes.Add(new Keyframe { Time = 0.5, Value = -10, Easing = Easing.EaseOutBack });
        head.Rotation.Keyframes.Add(new Keyframe { Time = 1.25, Value = 5.125, Easing = Easing.Hold });
        head.Drawing.Keyframes.Add(new DrawingKeyframe { Time = 0, Value = "normal" });
        head.Drawing.Keyframes.Add(new DrawingKeyframe { Time = 1.75, Value = "shocked" });
        head.FlipX = true;

        project.Layers.Add(new TextLayer
        {
            Id = "subtitle",
            Name = "Subtitle",
            Text = "မင်္ဂလာပါ\nこんにちは",
            FontFamily = "Myanmar Text",
            FontAsset = "myfont",
            FontSize = 64,
            Color = "#FFFFFF",
            StrokeColor = "#000000",
            StrokeWidth = 6,
            Align = TextAlign.Center,
            MaxWidth = 1200,
            Box = new TextBackground { Color = "#00000080", Padding = 20, CornerRadius = 12 },
            Start = 0.5,
            End = 2.5,
            ZIndex = 50,
        });
        project.Layers.Add(new AudioLayer
        {
            Id = "boing_1",
            Name = "Boing",
            Asset = "boing",
            Start = 0.7,
            TrimIn = 0.1,
            TrimOut = 0.4,
            Volume = 0.8,
            FadeIn = 0.05,
            FadeOut = 0.1,
        });
        return project;
    }

    [Fact]
    public void RoundTripIsLossless()
    {
        var json = ProjectJson.Serialize(FullProject());
        var restored = ProjectJson.Deserialize(json);
        Assert.Equal(json, ProjectJson.Serialize(restored));

        Assert.IsType<ImageLayer>(restored.FindLayer("tora_head"));
        Assert.IsType<TextLayer>(restored.FindLayer("subtitle"));
        Assert.IsType<AudioLayer>(restored.FindLayer("boing_1"));

        var head = (ImageLayer)restored.FindLayer("tora_head")!;
        Assert.Equal("tora_body", head.ParentId);
        Assert.Equal(Easing.EaseOutBack, head.Rotation.Keyframes[0].Easing);
        Assert.Equal(5.125, head.Rotation.Keyframes[1].Value);
        Assert.Equal("shocked", head.Drawing.Keyframes[1].Value);
        Assert.True(head.FlipX);

        var text = (TextLayer)restored.FindLayer("subtitle")!;
        Assert.Equal("မင်္ဂလာပါ\nこんにちは", text.Text);
        Assert.Equal(TextAlign.Center, text.Align);
        Assert.Equal(12, text.Box!.CornerRadius);

        var audio = (AudioLayer)restored.FindLayer("boing_1")!;
        Assert.Equal(0.4, audio.TrimOut);
        Assert.Equal(AssetType.Font, restored.FindAsset("myfont")!.Type);
    }

    [Fact]
    public void WritesReadableJson()
    {
        var json = ProjectJson.Serialize(FullProject());
        Assert.Contains("\"type\": \"image\"", json);
        Assert.Contains("\"easing\": \"easeOutBack\"", json);
        Assert.Contains("မင်္ဂလာပါ", json);
        Assert.Contains("こんにちは", json);
        Assert.DoesNotContain("typeName", json);

        var tail = ProjectJson.LayerToJson(FullProject().FindLayer("tora_tail")!);
        Assert.DoesNotContain("keyframes", tail);
    }

    [Fact]
    public void ReadsTypeDiscriminatorAnywhereAndSortsKeyframes()
    {
        const string json = """
        {
          "name": "hand written",
          "width": 320, "height": 240, "fps": 24, "duration": 2,
          "layers": [
            {
              "id": "dot",
              "x": { "value": 0, "keyframes": [
                { "time": 1, "value": 100 },
                { "time": 0, "value": 0, "easing": "EaseOut" }
              ] },
              "type": "image",
            }
          ]
        }
        """;
        var project = ProjectJson.Deserialize(json);
        var dot = Assert.IsType<ImageLayer>(project.Layers.Single());
        Assert.Equal([0.0, 1.0], dot.X.Keyframes.Select(k => k.Time));
        Assert.Equal(Easing.EaseOut, dot.X.Keyframes[0].Easing);
        Assert.Equal(1, dot.ScaleX.Value);
    }

    [Fact]
    public void UnknownLayerTypeGivesClearError()
    {
        var ex = Assert.Throws<ZeyoCutException>(() => ProjectJson.Deserialize("""{ "layers": [ { "type": "sticker", "id": "v" } ] }"""));
        Assert.Contains("invalid project JSON", ex.Message);
    }
}
