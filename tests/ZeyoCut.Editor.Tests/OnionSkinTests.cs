// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

public class OnionSkinTests
{
    // An 8 px orange box going right 20 px a frame, and another layer that stays put.
    private static (Project Project, string Folder) Moving()
    {
        var folder = TestFiles.NewFolder("onion");
        var project = TestFiles.Project();
        Directory.CreateDirectory(Path.Combine(folder, ProjectStore.AssetsFolder));
        TestFiles.Png(Path.Combine(folder, ProjectStore.AssetsFolder, "box.png"), 8, 8);
        project.Assets.Add(new Asset { Id = "box", Type = AssetType.Image, File = "assets/box.png", Width = 8, Height = 8 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "box", Drawings = new Dictionary<string, string> { ["normal"] = "box" }, X = 0, Y = 90, AnchorX = 4, AnchorY = 4 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "other", Drawings = new Dictionary<string, string> { ["normal"] = "box" }, X = 40, Y = 30, AnchorX = 4, AnchorY = 4 });
        var box = (ImageLayer)project.FindLayer("box")!;
        box.X.Keyframes = [new Keyframe { Time = 0, Value = 0 }, new Keyframe { Time = 1, Value = 600 }];
        return (project, folder);
    }

    [Fact]
    public void TheGhostsAreTheLayerAloneAtTheFramesAroundFadedAndTheOnesAfterGreen()
    {
        var (project, folder) = Moving();
        using var skin = new OnionSkin(folder, () => throw new InvalidOperationException("no footage here"), _ => { });
        var picture = skin.Draw(new OnionRequest(project, new HashSet<string> { "box" }, 10, 2, 1, 1));
        Assert.Equal((320, 180), (picture.Width, picture.Height));
        byte[] At(int x, int y) => picture.Pixels.AsSpan((y * picture.Width + x) * 4, 4).ToArray();

        // Frames 8 and 9 in the box's own orange, the nearer stronger, BGRA premultiplied.
        Assert.Equal([0, 27, 42, 42], At(160, 90));
        Assert.Equal([0, 55, 85, 85], At(180, 90));

        // Frame 10 is the frame itself; frame 11 is after, in green.
        Assert.Equal([0, 0, 0, 0], At(200, 90));
        Assert.Equal([28, 47, 11, 64], At(220, 90));

        // Only the layer asked for: the other one isn't there, nor anything else.
        Assert.Equal([0, 0, 0, 0], At(40, 30));
        Assert.Equal(3 * 8 * 8, picture.Pixels.Chunk(4).Count(p => p[3] != 0));

        // At the start there's nothing before.
        var start = skin.Draw(new OnionRequest(project, new HashSet<string> { "box" }, 0, 2, 1, 0.5));
        Assert.Equal((160, 90), (start.Width, start.Height));
        Assert.Equal(4 * 4, start.Pixels.Chunk(4).Count(p => p[3] != 0));
    }
}
