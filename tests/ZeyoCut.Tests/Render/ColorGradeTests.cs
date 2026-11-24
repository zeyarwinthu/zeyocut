// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

// A clip's grade as the renderer draws it, for render_frame, the editor's preview and exports.
// The expected values are worked out by hand from the formulas in ColorAdjust: sRGB 128 is 0.2159
// of full light, so a stop more (0.4317) is sRGB 176, and so on.
public class ColorGradeTests
{
    private readonly ITestOutputHelper _output;

    public ColorGradeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Four swatches side by side, each 20 px wide: gray 128, gray 85, gray 170 and black/white halves.
    private static string Swatches(string path)
    {
        using var surface = SKSurface.Create(new SKImageInfo(80, 20, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        using var paint = new SKPaint();
        foreach (var (x, color) in new[] { (0, new SKColor(128, 128, 128)), (20, new SKColor(85, 85, 85)), (40, new SKColor(170, 170, 170)), (60, SKColors.Black), (70, SKColors.White) })
        {
            paint.Color = color;
            surface.Canvas.DrawRect(new SKRect(x, 0, x + 20, 20), paint);
        }

        using var image = surface.Snapshot();
        Png.Save(image, path);
        return path;
    }

    private static ProjectSession SwatchProject(string name, double duration = 1)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 80, height: 20, fps: 10, duration: duration));
        var file = Swatches(Path.Combine(directory, "src_swatches.png"));
        session.Mutate("import", p => AssetImporter.Import(p, directory, file, "swatches", new AssetProbe()));
        return session;
    }

    private static void Add(ProjectSession session, string id, double start, double end) =>
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = id, Drawings = new Dictionary<string, string> { ["n"] = "swatches" }, Start = start, End = end }));

    private static (byte R, byte G, byte B) Rgb(SKColor color) => (color.Red, color.Green, color.Blue);

    private static void Near((byte R, byte G, byte B) expected, SKColor actual, int tolerance = 1)
    {
        Assert.True(Math.Abs(expected.R - actual.Red) <= tolerance && Math.Abs(expected.G - actual.Green) <= tolerance && Math.Abs(expected.B - actual.Blue) <= tolerance,
            $"expected {expected}, got ({actual.Red}, {actual.Green}, {actual.Blue})");
    }

    [Fact]
    public void EachControlMovesKnownGraysToTheirReferenceValues()
    {
        var session = SwatchProject("color-grade-reference");
        Add(session, "swatch", 0, 1);
        using var renderer = new FrameRenderer(session.Directory);

        // Mid gray, dark gray, light gray, black and white with one control set.
        SKColor[] Render(string control, double value)
        {
            var project = session.Snapshot();
            ColorEditor.SetColor(project, "swatch", new ColorChange(new Dictionary<string, double> { [control] = value }));
            using var image = renderer.RenderImage(project, 5);
            return [.. new[] { 10, 30, 50, 65, 75 }.Select(x => TestImages.Pixel(image, x, 10))];
        }

        var exposed = Render("exposure", 1);
        Near((176, 176, 176), exposed[0]);
        Near((255, 255, 255), exposed[4], 0);
        Near((0, 0, 0), exposed[3], 0);
        Near((92, 92, 92), Render("exposure", -1)[0]);

        // Temperature and tint move red and blue, or green, half a stop each way.
        Near((150, 128, 109), Render("temperature", 1)[0]);
        Near((109, 128, 150), Render("temperature", -1)[0]);
        Near((128, 109, 128), Render("tint", 1)[0]);
        Near((128, 150, 128), Render("tint", -1)[0]);

        // Shadows lift the dark gray most and leave black and white; highlights the light one.
        var lifted = Render("shadows", 1);
        Near((123, 123, 123), lifted[1]);
        Near((0, 0, 0), lifted[3], 0);
        Near((255, 255, 255), lifted[4], 0);
        var recovered = Render("highlights", -1);
        Near((132, 132, 132), recovered[2]);
        Near((0, 0, 0), recovered[3], 0);
        Near((255, 255, 255), recovered[4], 0);

        // A grade comes before saturation: the warmed gray's own luma is what's left of it.
        var project = session.Snapshot();
        ColorEditor.SetColor(project, "swatch", new ColorChange(new Dictionary<string, double> { ["temperature"] = 1, ["saturation"] = 0 }));
        using (var image = renderer.RenderImage(project, 5))
        {
            Near((131, 131, 131), TestImages.Pixel(image, 10, 10));
        }

        // A keyframed grade is drawn at the frame's value.
        project = session.Snapshot();
        ColorEditor.SetColor(project, "swatch", new ColorChange(new Dictionary<string, double> { ["exposure"] = 0.5 }));
        var grade = ((ImageLayer)project.FindLayer("swatch")!).Grade!;
        grade.Exposure.Keyframes.Add(new Keyframe { Time = 0, Value = 0 });
        grade.Exposure.Keyframes.Add(new Keyframe { Time = 1, Value = 2 });
        using (var image = renderer.RenderImage(project, 5))
        {
            Near((176, 176, 176), TestImages.Pixel(image, 10, 10));
        }
    }

    [Fact]
    public void EveryCurveKeepsBlackAndWhiteAndOnlyGoesUp()
    {
        foreach (var highlights in new[] { -1.0, 0, 1 })
        {
            foreach (var shadows in new[] { -1.0, 0, 1 })
            {
                foreach (var table in new[] { ColorAdjust.Tables(0, highlights, shadows, 0, 0).Red, ColorAdjust.Tables(0.5, highlights, shadows, 1, -1).Green })
                {
                    Assert.Equal(0, table[0]);
                    Assert.True(table.Zip(table.Skip(1)).All(p => p.Second >= p.First), $"highlights {highlights}, shadows {shadows} goes down somewhere");
                }

                Assert.Equal(255, ColorAdjust.Tables(0, highlights, shadows, 0, 0).Blue[255]);
            }
        }

        Assert.Null(ColorAdjust.Grade(0, 0, 0, 0, 0));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), ColorAdjust.Tables(0, 0, 0, 0, 0).Red);
    }

    // Each wheel moves its channel in its part of the range: lift the dark values with white kept,
    // gamma the middle with black and white kept, gain the light values with black kept.
    [Fact]
    public void EachWheelMovesItsChannelToTheReferenceValues()
    {
        var session = SwatchProject("color-wheel-reference");
        Add(session, "swatch", 0, 1);
        using var renderer = new FrameRenderer(session.Directory);

        SKColor[] Render(params (string Control, double Value)[] values)
        {
            var project = session.Snapshot();
            ColorEditor.SetColor(project, "swatch", new ColorChange(values.ToDictionary(v => v.Control, v => v.Value)));
            using var image = renderer.RenderImage(project, 5);
            return [.. new[] { 10, 30, 50, 65, 75 }.Select(x => TestImages.Pixel(image, x, 10))];
        }

        Near((141, 128, 128), Render(("lift.red", 0.1))[0]);
        Near((128, 157, 128), Render(("gamma.green", 0.5))[0]);
        Near((128, 128, 64), Render(("gain.blue", -0.5))[0]);
        Near((64, 64, 64), Render(("gamma.red", -1), ("gamma.green", -1), ("gamma.blue", -1))[0]);

        var lifted = Render(("lift.red", -0.2), ("lift.green", -0.2), ("lift.blue", -0.2));
        Near((51, 51, 51), lifted[1]);
        Near((0, 0, 0), lifted[3], 0);
        Near((255, 255, 255), lifted[4], 0);
        var gained = Render(("gain.red", 0.2), ("gain.green", 0.2), ("gain.blue", 0.2));
        Near((204, 204, 204), gained[2]);
        Near((0, 0, 0), gained[3], 0);

        // The wheels' tables only go up too, whatever they're set to.
        var (red, green, blue) = ColorAdjust.Tables(0.5, -0.3, 0.4, 0.2, 0, new Rgb(-1, 0.5, 1), new Rgb(1, -1, 0.3), new Rgb(-0.5, 1, 0));
        foreach (var table in new[] { red, green, blue })
        {
            Assert.True(table.Zip(table.Skip(1)).All(p => p.Second >= p.First), "a wheel's table goes down somewhere");
        }
    }

    // A look is the LUT's color mixed with the clip's by its amount, drawn after the rest of the
    // color: here a LUT that squares red, keeps green and turns blue around.
    [Fact]
    public void ALookIsTheLutsColorsMixedByItsAmount()
    {
        var session = SwatchProject("color-look-reference");
        Add(session, "swatch", 0, 1);
        var cube = Path.Combine(session.Directory, "src_curves.cube");
        File.WriteAllText(cube, ZeyoCut.Tests.Core.CubeLutTests.Curves());
        session.Mutate("import", p => AssetImporter.Import(p, session.Directory, cube, "curves", new AssetProbe()));
        using var renderer = new FrameRenderer(session.Directory);

        SKColor[] Render(double amount, params (string Control, double Value)[] values)
        {
            var project = session.Snapshot();
            ColorEditor.SetColor(project, "swatch", new ColorChange(values.Append(("look.amount", amount)).ToDictionary(v => v.Item1, v => v.Item2), Look: "curves"));
            using var image = renderer.RenderImage(project, 5);
            return [.. new[] { 10, 30, 50, 65, 75 }.Select(x => TestImages.Pixel(image, x, 10))];
        }

        var looked = Render(1);
        Near((64, 128, 127), looked[0]);
        Near((128, 170, 85), looked[2]);
        Near((0, 0, 255), looked[3], 0);
        Near((255, 255, 0), looked[4], 0);
        Near((96, 128, 128), Render(0.5)[0]);
        Near((128, 128, 128), Render(0)[0], 0);

        // After contrast: contrast 0 makes every swatch mid gray, which the look then colors.
        Near((64, 128, 127), Render(1, ("contrast", 0))[3]);

        // A frame looked through a LUT is drawn again once the LUT file changes.
        var keyed = session.Snapshot();
        ColorEditor.SetColor(keyed, "swatch", new ColorChange(new Dictionary<string, double>(), Look: "curves"));
        var lutFile = ProjectStore.AssetPath(session.Directory, keyed.FindAsset("curves")!);
        var key = new FrameKeys(keyed, session.Directory).Of(5);
        File.AppendAllText(lutFile, "# edited\n");
        File.SetLastWriteTimeUtc(lutFile, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(key, new FrameKeys(keyed, session.Directory).Of(5));

        // A LUT file that's gone stops the frame with the reason.
        var project = session.Snapshot();
        ColorEditor.SetColor(project, "swatch", new ColorChange(new Dictionary<string, double>(), Look: "curves"));
        File.Delete(ProjectStore.AssetPath(session.Directory, project.FindAsset("curves")!));
        using var fresh = new FrameRenderer(session.Directory);
        Assert.StartsWith("LUT file is missing: ", Assert.Throws<ZeyoCutException>(() => fresh.RenderImage(project, 5)).Message);
    }

    // The wheels and a look over the swatches and on footage: exported as the preview draws them.
    [Fact]
    public void WheelsAndALookExportAsThePreviewDrawsThem()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = SwatchProject("color-look-export", duration: 3);
        var cube = Path.Combine(session.Directory, "src_curves.cube");
        File.WriteAllText(cube, ZeyoCut.Tests.Core.CubeLutTests.Curves());
        session.Mutate("import", p => AssetImporter.Import(p, session.Directory, cube, "curves", new AssetProbe()));
        var wheels = new Dictionary<string, double> { ["lift.red"] = -0.1, ["lift.blue"] = 0.15, ["gamma.green"] = 0.4, ["gain.red"] = 0.2, ["gain.blue"] = -0.3 };
        Add(session, "wheeled", 0, 1);
        session.Mutate("color", p => ColorEditor.SetColor(p, "wheeled", new ColorChange(wheels)));
        Add(session, "looked", 1, 2);
        session.Mutate("color", p => ColorEditor.SetColor(p, "looked", new ColorChange(new Dictionary<string, double> { ["look.amount"] = 0.7 }, Look: "curves")));
        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(session.Directory, "src_pattern.mp4"), seconds: 1, fps: 10, width: 80, height: 20), "pattern");
        session.Mutate("clip", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Asset = "pattern", Id = "clip", Start = 2, TrimIn = 0, TrimOut = 1 }));
        session.Mutate("color", p => ColorEditor.SetColor(p, "clip", new ColorChange(new Dictionary<string, double>(wheels) { ["exposure"] = 0.3, ["look.amount"] = 0.6 }, Look: "curves")));

        var project = session.Snapshot();
        var output = Path.Combine(session.Directory, "exports", "looked.mp4");
        Exporter.Export(project, session.Directory, output, 18, ffmpeg, cancellationToken: TestContext.Current.CancellationToken);
        using var renderer = new FrameRenderer(session.Directory, () => ffmpeg.Ffmpeg);
        for (var second = 0; second < 3; second++)
        {
            var frame = second * 10 + 5;
            using var image = renderer.RenderImage(project, frame, new RenderOptions(Proxies: true));
            var drawn = TestImages.Pixels(image);
            var decoded = Ffmpeg.ExtractFrameRgba(ffmpeg, output, frame, 80, 20);
            var error = second < 2 ? Ffmpeg.MeanAbsoluteError(drawn, decoded) : Ffmpeg.MeanLumaError(drawn, decoded);
            _output.WriteLine($"{new[] { "wheels", "look", "everything on footage" }[second]}: mean error {error:0.00}");
            Assert.True(error < 2.5, $"second {second}: the export differs from the preview by {error:0.00}/255 on average");
        }
    }

    // Each control, one per second over the swatches, and all of them on a clip of footage: the
    // exported frames are the frames render_frame and the editor's preview draw.
    [Fact]
    public void AGradedExportIsThePreviewsFrames()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = SwatchProject("color-grade-export", duration: 8);
        var controls = new (string Name, double Value)[] { ("exposure", 0.6), ("contrast", 1.4), ("highlights", -0.7), ("shadows", 0.8), ("temperature", 0.9), ("tint", -0.6), ("saturation", 0.3) };
        for (var i = 0; i < controls.Length; i++)
        {
            var id = $"swatch_{i}";
            Add(session, id, i, i + 1);
            session.Mutate("color", p => ColorEditor.SetColor(p, id, new ColorChange(new Dictionary<string, double> { [controls[i].Name] = controls[i].Value })));
        }

        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(session.Directory, "src_pattern.mp4"), seconds: 1, fps: 10, width: 80, height: 20), "pattern");
        session.Mutate("clip", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Asset = "pattern", Id = "clip", Start = 7, TrimIn = 0, TrimOut = 1 }));
        session.Mutate("color", p => ColorEditor.SetColor(p, "clip", new ColorChange(controls.ToDictionary(c => c.Name, c => c.Value))));

        var project = session.Snapshot();
        var output = Path.Combine(session.Directory, "exports", "graded.mp4");
        Exporter.Export(project, session.Directory, output, 18, ffmpeg, cancellationToken: TestContext.Current.CancellationToken);
        using var renderer = new FrameRenderer(session.Directory, () => ffmpeg.Ffmpeg);
        for (var second = 0; second < 8; second++)
        {
            var frame = second * 10 + 5;
            using var image = renderer.RenderImage(project, frame, new RenderOptions(Proxies: true));
            var drawn = TestImages.Pixels(image);
            var decoded = Ffmpeg.ExtractFrameRgba(ffmpeg, output, frame, 80, 20);
            var error = second < 7 ? Ffmpeg.MeanAbsoluteError(drawn, decoded) : Ffmpeg.MeanLumaError(drawn, decoded);
            _output.WriteLine($"{(second < 7 ? controls[second].Name : "every control on footage")}: mean error {error:0.00}");
            Assert.True(error < 2.5, $"second {second}: the export differs from the preview by {error:0.00}/255 on average");
        }
    }
}
