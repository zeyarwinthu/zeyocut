// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

// The layer boxes a preview picks layers by and drags them with.
public class LayerOutlinesTests
{
    private static void Near((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    [Fact]
    public void EachShownLayerHasItsBoxAndTheTopmostIsPicked()
    {
        var directory = TestPaths.NewDirectory("layer-outlines");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 200, height: 200, fps: 30, duration: 2));
        var red = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "red.png"), 20, 20, SKColors.Red), null, new AssetProbe()));
        var bar = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "bar.png"), 40, 20, SKColors.Blue), null, new AssetProbe()));
        ImageLayerOptions Image(string id, Asset asset) => new() { Id = id, Drawings = new Dictionary<string, string> { ["normal"] = asset.Id }, AnchorX = 0, AnchorY = 0 };
        session.Mutate("add", p =>
        {
            ProjectEditor.AddImageLayer(p, Image("back", red) with { X = 50, Y = 60, ZIndex = 0 });
            ProjectEditor.AddImageLayer(p, Image("front", bar) with { X = 100, Y = 100, Rotation = 90, ZIndex = 1 });
            ProjectEditor.AddImageLayer(p, Image("dot", red) with { ParentId = "front", X = 10, Y = 5, ScaleX = 0.5, ScaleY = 0.5 });
            ProjectEditor.AddImageLayer(p, Image("ghost", red) with { X = 50, Y = 60, Opacity = 0, ZIndex = 5 });
            ProjectEditor.AddImageLayer(p, Image("hidden", red) with { X = 50, Y = 60, Visible = false, ZIndex = 6 });
            return ProjectEditor.AddImageLayer(p, Image("flat", red) with { X = 150, Y = 20, ScaleX = 0, ZIndex = 7 });
        });

        using var renderer = new FrameRenderer(directory);
        var outlines = LayerOutlines.At(renderer, session.Snapshot(), 0);
        Assert.Equal(["back", "front", "dot", "flat"], outlines.Select(o => o.LayerId));
        var back = outlines[0];
        Assert.Equal([(50.0, 60.0), (70, 60), (70, 80), (50, 80)], back.Corners);

        // Turned 90° clockwise around its corner, the 40×20 bar hangs down to the left of 100, 100.
        var front = outlines[1];
        Assert.Equal(4, front.Corners.Count);
        foreach (var (expected, actual) in new[] { (100.0, 100.0), (100, 140), (80, 140), (80, 100) }.Zip(front.Corners))
        {
            Near(expected, actual);
        }

        Assert.True(front.Contains(90, 120));
        Assert.False(front.Contains(110, 120));
        Assert.Equal("back", LayerOutlines.Hit(outlines, 60, 70)?.LayerId);
        Assert.Equal("front", LayerOutlines.Hit(outlines, 85, 130)?.LayerId);
        Assert.Null(LayerOutlines.Hit(outlines, 5, 5));

        // x and y are in the parent's pixels: the parent matrix takes them to where the anchor lands.
        Near((50, 60), back.Parent!.Value.Apply(50, 60));
        var dot = outlines[2];
        Near(dot.Anchor, dot.Parent!.Value.Apply(10, 5));
        Near((95, 110), dot.Anchor);

        // Scaled to 0, a layer has no area and no way back from the canvas to its parent's pixels.
        var flat = outlines[3];
        Assert.Null(flat.Parent);
        Assert.False(flat.Contains(150, 20));
    }
}
