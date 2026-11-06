// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp;
using ZeyoCut.Mcp.Demo;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Demo;

public class DemoTests
{
    private readonly ITestOutputHelper _output;

    public DemoTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DemoRigHasJumpMechanics()
    {
        var directory = TestPaths.NewDirectory("demo-rig");
        var project = DemoBuilder.Build(directory);

        Assert.Empty(ProjectValidator.Validate(project));
        Assert.Equal(3.0, project.Duration);
        Assert.Equal("tora_body", project.FindLayer("tora_head")!.ParentId);
        Assert.Equal("tora_body", project.FindLayer("tora_tail")!.ParentId);
        Assert.IsType<TextLayer>(project.FindLayer("title"));
        Assert.Equal(2, project.Layers.OfType<AudioLayer>().Count());

        var root = (VisualLayer)project.FindLayer("tora")!;
        var body = (VisualLayer)project.FindLayer("tora_body")!;
        var head = (ImageLayer)project.FindLayer("tora_head")!;
        var tail = (VisualLayer)project.FindLayer("tora_tail")!;

        Assert.True(Interpolator.Evaluate(body.ScaleY, DemoBuilder.TakeoffTime) < 0.75, "squash before takeoff");
        Assert.True(Interpolator.Evaluate(body.ScaleX, DemoBuilder.TakeoffTime) > 1.2, "squash widens");
        Assert.True(Interpolator.Evaluate(body.ScaleY, 1.0) > 1.15, "stretch in the air");
        Assert.Equal(DemoBuilder.ApexY, Interpolator.Evaluate(root.Y, DemoBuilder.ApexTime), 6);
        Assert.Equal(DemoBuilder.GroundY, Interpolator.Evaluate(root.Y, DemoBuilder.LandingTime), 6);

        var landing = Enumerable.Range(0, 48).Select(i => Interpolator.Evaluate(body.ScaleY, 1.83 + i * 0.01)).ToList();
        Assert.True(landing.Max() > 1.02, $"easeOutBack landing overshoots (max scaleY {landing.Max():0.000})");
        Assert.Equal(1.0, Interpolator.Evaluate(body.ScaleY, 2.5), 6);

        Assert.True(Interpolator.Evaluate(head.Y, 1.0) > head.Y.Keyframes[0].Value, "head lags behind the body at takeoff");
        Assert.True(Interpolator.Evaluate(head.Y, 1.83) > Interpolator.Evaluate(head.Y, 1.75), "head keeps moving down on landing");
        Assert.Equal("happy", Interpolator.Evaluate(head.Drawing, 1.2));
        Assert.Equal("normal", Interpolator.Evaluate(head.Drawing, 2.5));

        var wag = Enumerable.Range(0, 91).Select(i => Interpolator.Evaluate(tail.Rotation, i / 30.0)).ToList();
        var signChanges = wag.Zip(wag.Skip(1)).Count(pair => Math.Sign(pair.First) * Math.Sign(pair.Second) < 0);
        Assert.True(signChanges >= 6, $"tail wags back and forth ({signChanges} direction changes)");

        var apex = SceneEvaluator.Evaluate(project, DemoBuilder.ApexTime).ToDictionary(e => e.Layer.Id);
        var neck = apex["tora_head"].World.Apply(head.AnchorX, head.AnchorY);
        var feet = apex["tora_body"].World.Apply(body.AnchorX, body.AnchorY);
        Assert.True(neck.Y < feet.Y - 150, "head sits on top of the body");
        Assert.True(feet.Y < DemoBuilder.GroundY - 200, "Tora is in the air at the apex");
    }

    [Fact]
    public void DemoCliExportsVideoVerifiedByFfprobe()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("demo-export");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = Cli.Run(["demo", directory], output, error);
        _output.WriteLine(output.ToString());
        Assert.True(exit == 0, error.ToString());

        var video = Path.Combine(directory, ProjectStore.ExportsFolder, DemoBuilder.ExportFileName);
        var media = Ffprobe.Probe(ffmpeg.Ffprobe, video);
        _output.WriteLine($"ffprobe: {media}");
        Assert.InRange(media.Duration, 3.0 - 1 / 30.0, 3.0 + 1 / 30.0);
        Assert.Equal(1280, media.Width);
        Assert.Equal(720, media.Height);
        Assert.Equal(30, media.Fps!.Value, 3);
        Assert.Equal(90, media.VideoFrames);
        Assert.Equal("h264", media.VideoCodec);
        Assert.Equal("yuv420p", media.PixelFormat);
        Assert.True(media.HasAudio);
        Assert.Equal("aac", media.AudioCodec);
        Assert.True(File.Exists(Path.Combine(directory, ProjectStore.RendersFolder, "sheet_00000-00089_12.png")));
    }
}
