// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Tests.TestSupport;

public sealed class FakeAssetProbe : IAssetProbe
{
    public AssetInfo Probe(string path, AssetType type) => type switch
    {
        AssetType.Image => new AssetInfo(Width: 200, Height: 100),
        AssetType.Audio => new AssetInfo(Duration: 1.5),
        _ => new AssetInfo(FontFamily: "Fake Font"),
    };
}

public static class TestProjects
{
    public static Project Empty(int width = 640, int height = 360, int fps = 30, double duration = 3) => new()
    {
        Name = "test",
        Width = width,
        Height = height,
        Fps = fps,
        Duration = duration,
        BackgroundColor = "#FFFFFF",
    };

    public static Project WithImageAssets(params string[] ids)
    {
        var project = Empty();
        foreach (var id in ids)
        {
            project.Assets.Add(new Asset { Id = id, Type = AssetType.Image, File = $"assets/{id}.png", Width = 200, Height = 100 });
        }

        return project;
    }

    public static Project Rig()
    {
        var project = WithImageAssets("body", "head", "head_shocked", "tail");
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "tora_body",
            Drawings = new Dictionary<string, string> { ["normal"] = "body" },
            ZIndex = 10,
        });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "tora_head",
            ParentId = "tora_body",
            Drawings = new Dictionary<string, string> { ["normal"] = "head", ["shocked"] = "head_shocked" },
            ZIndex = 11,
        });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "tora_tail",
            ParentId = "tora_body",
            Drawings = new Dictionary<string, string> { ["normal"] = "tail" },
            ZIndex = 9,
        });
        return project;
    }
}
