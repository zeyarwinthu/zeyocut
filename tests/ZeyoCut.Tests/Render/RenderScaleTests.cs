// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

// A smaller frame for the editor's playback: the same picture with fewer pixels, and the clip decoded
// smaller. Exports never set a scale.
public class RenderScaleTests
{
    // How far apart two pictures of the same size are: the mean difference per channel, 0-255.
    private static double MeanDifference(SKImage a, SKImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        var (x, y) = (TestImages.Pixels(a), TestImages.Pixels(b));
        long sum = 0;
        for (var i = 0; i < x.Length; i++)
        {
            sum += Math.Abs(x[i] - y[i]);
        }

        return sum / (double)x.Length;
    }

    [Fact]
    public void AHalfSizeFrameLooksLikeTheFullFrameMadeSmaller()
    {
        var directory = TestPaths.NewDirectory("render-scale");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 400, height: 300, fps: 10, duration: 1));
        var red = session.Mutate("import", p => AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "red.png"), 80, 80, SKColors.Red), null, new AssetProbe()));
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "shadowed", Drawings = new Dictionary<string, string> { ["normal"] = red.Id }, X = 100, Y = 100, Rotation = 20 });
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "blurred", Drawings = new Dictionary<string, string> { ["normal"] = red.Id }, X = 300, Y = 100, ScaleX = 1.5, ScaleY = 1.5 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Scale", X = 200, Y = 220, FontSize = 60, Color = "#FFFFFF", StrokeColor = "#000000", StrokeWidth = 4 });
            EffectEditor.SetEffect(p, "shadowed", EffectKind.Shadow, new EffectValues("#000000", 14, 10, 6, 0.8, null, null), remove: false);
            EffectEditor.SetEffect(p, "blurred", EffectKind.Blur, new EffectValues(null, 5, 5, null, null, null, null), remove: false);
            EffectEditor.SetEffect(p, "title", EffectKind.Outline, new EffectValues("#2040FF", null, null, null, null, 3, null), remove: false);
            EffectEditor.SetMask(p, "blurred", new MaskValues(MaskType.Ellipse, null, 0, 0, 80, 60, 8, false), remove: false, measurer: null);
            return "";
        });
        var project = session.Snapshot();
        using var renderer = new FrameRenderer(directory);

        using var full = renderer.RenderImage(project, 0);
        using var half = renderer.RenderImage(project, 0, new RenderOptions(Scale: 0.5));
        using var expected = Png.Scale(full, 200, 150);
        Assert.Equal((200, 150), (half.Width, half.Height));
        Assert.True(MeanDifference(expected, half) < 2.5, $"mean difference {MeanDifference(expected, half):0.00}");

        using var quarter = renderer.RenderImage(project, 0, new RenderOptions(Scale: 0.25));
        Assert.Equal((100, 75), (quarter.Width, quarter.Height));
        Assert.Equal((133, 100), FrameRenderer.ScaledSize(project, 1 / 3.0));

        // Scale 1 is the frame exports draw.
        Assert.Equal(1, new RenderOptions().Scale);
        using var one = renderer.RenderImage(project, 0, new RenderOptions(Scale: 1));
        Assert.Equal(TestImages.Pixels(full), TestImages.Pixels(one));

        Assert.Contains("must be above 0 and at most 1", Assert.Throws<ZeyoCutException>(() => renderer.RenderImage(project, 0, new RenderOptions(Scale: 2))).Message);
        Assert.Throws<ZeyoCutException>(() => renderer.RenderImage(project, 0, new RenderOptions(Scale: 0)));
    }

    [Fact]
    public void ASmallerFrameDecodesTheClipSmaller()
    {
        Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("render-scale-video");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 10, duration: 2));
        var asset = TestVideos.Import(session, TestVideos.Ramp(Path.Combine(directory, "src_ramp.mp4"), seconds: 2, fps: 10, width: 640, height: 480), "ramp");
        session.Mutate("layer", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "ramp" }));
        var project = session.Snapshot();

        // At most twice the canvas: 640x480 for the full frame, half that for a frame of half the size.
        Assert.Equal((640, 480), VideoDecoder.DecodeSize(asset, project));
        Assert.Equal((320, 240), VideoDecoder.DecodeSize(asset, project, 0.5));

        using var renderer = new FrameRenderer(directory, () => Ffmpeg.Require().Ffmpeg);
        foreach (var frame in new[] { 0, 7, 19 })
        {
            using var half = renderer.RenderImage(project, frame, new RenderOptions(Scale: 0.5));
            Assert.Equal((160, 120), (half.Width, half.Height));
            Assert.Equal(frame, TestVideos.FrameCode(half, 0, 0, 160, 120));
            Assert.Equal((320, 240), (renderer.VideoFrame(project, (VideoLayer)project.FindLayer("clip")!, frame / 10.0, scale: 0.5).Width, 240));
        }
    }
}
