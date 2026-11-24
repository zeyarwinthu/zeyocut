// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Editor.Tests;

// The Color view: its sliders' units, the clip it grades, the edits it makes, and its scopes.
public class ColorPanelTests
{
    // Three clips of 'street' (0-3 s, 3-6 s, and 6-10 s one level up), a picture of 'logo' on top of
    // the last, and a title over the first.
    private static Project Edit()
    {
        var project = TestFiles.Project(duration: 10);
        project.Assets.Add(new Asset { Id = "street", Type = AssetType.Video, File = "assets/street.mp4", Width = 320, Height = 180, Duration = 10, FrameRate = "30", FrameCount = 300 });
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 64, Height = 64 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "street", Start = 0, TrimIn = 0, TrimOut = 3, ZIndex = 0 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "street_2", Start = 3, TrimIn = 3, TrimOut = 6, ZIndex = 0 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "street_b", Start = 6, TrimIn = 1, TrimOut = 5, ZIndex = 1 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Start = 6, End = 10, ZIndex = 2 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Street", Start = 0, End = 3, ZIndex = 3 });
        return project;
    }

    private static void Grade(Project project, string id, params (string Name, double Value)[] values) =>
        ColorEditor.SetColor(project, id, new ColorChange(values.ToDictionary(v => v.Name, v => v.Value)));

    [Fact]
    public void TheSlidersShowTheDesignsUnitsAndReadTypedValues()
    {
        Assert.Equal(["Exposure", "Contrast", "Highlights", "Shadows", "Temperature", "Tint", "Saturation"], ColorPanel.Sliders.Select(s => s.Label));
        Assert.Equal(ColorEditor.Controls.Where(c => !c.Name.Contains('.')).Select(c => c.Name), ColorPanel.Sliders.Select(s => s.Name));
        string Readout(string name, double value) => ColorPanel.Readout(ColorPanel.Slider(name), value);
        Assert.Equal(["+0.3", "+12", "−20", "+18", "+35", "0", "+20"],
            new[] { Readout("exposure", 0.3), Readout("contrast", 1.12), Readout("highlights", -0.2), Readout("shadows", 0.18), Readout("temperature", 0.35), Readout("tint", 0), Readout("saturation", 1.2) });
        Assert.Equal(["−1.25", "0", "−100", "+300"], new[] { Readout("exposure", -1.25), Readout("exposure", 0.001), Readout("saturation", 0), Readout("contrast", 4) });
        Assert.Equal(["−3 to +3", "−100 to +300", "−100 to +100"],
            new[] { ColorPanel.Range(ColorPanel.Slider("exposure")), ColorPanel.Range(ColorPanel.Slider("contrast")), ColorPanel.Range(ColorPanel.Slider("tint")) });

        // The readout's units back to set_color's values, with the design's minus sign or a hyphen.
        double? Parse(string name, string text) => ColorPanel.Parse(ColorPanel.Slider(name), text);
        Assert.Equal(1.12, Parse("contrast", "+12")!.Value, 9);
        Assert.Equal(0.8, Parse("saturation", "−20")!.Value, 9);
        Assert.Equal(4, Parse("contrast", " 300 % ")!.Value, 9);
        Assert.Equal(-0.35, Parse("temperature", "-35")!.Value, 9);
        Assert.Equal(-3, Parse("exposure", "-3")!.Value, 9);
        Assert.Null(Parse("contrast", "301"));
        Assert.Null(Parse("exposure", "3.5"));
        Assert.Null(Parse("tint", "warm"));

        // The knob's place: as shot in the middle of each slider.
        var contrast = ColorPanel.Slider("contrast");
        Assert.Equal((0.0, 1.0), (contrast.Shown(1), contrast.ValueOf(0)));
        Assert.Equal(0, (contrast.Min + contrast.Max) / 2);
    }

    [Fact]
    public void TheViewGradesTheSelectedClipOrTheOneInFrontAtThePlayhead()
    {
        var project = Edit();
        Assert.Equal("street", ColorPanel.Target(project, null, 1));
        Assert.Equal("street", ColorPanel.Target(project, "title", 1));
        Assert.Equal("logo", ColorPanel.Target(project, null, 7));
        Assert.Equal("street_2", ColorPanel.Target(project, "street_2", 8));
        project.FindLayer("logo")!.Visible = false;
        Assert.Equal("street_b", ColorPanel.Target(project, "title", 7));
        Assert.Null(ColorPanel.Target(TestFiles.Project(), null, 0));

        Assert.True(ColorPanel.Gradable(project.FindLayer("logo")));
        Assert.False(ColorPanel.Gradable(project.FindLayer("title")));
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "rig" });
        Assert.False(ColorPanel.Gradable(project.FindLayer("rig")));
    }

    [Fact]
    public void TheRowsAreTheClipsValuesAtThePlayheadAndSayWhichHaveKeyframes()
    {
        var project = Edit();
        Assert.All(ColorPanel.Rows(project, "street_b", 7), row => Assert.Equal(("0", false), (row.Readout, row.Keyed)));

        Grade(project, "street_b", ("exposure", 0.3), ("contrast", 1.12), ("highlights", -0.2), ("shadows", 0.18), ("temperature", 0.35), ("saturation", 1.2));
        var grade = ((VideoLayer)project.FindLayer("street_b")!).Grade!;
        grade.Tint.Keyframes.Add(new Keyframe { Time = 6, Value = 0 });
        grade.Tint.Keyframes.Add(new Keyframe { Time = 8, Value = -0.5 });
        var rows = ColorPanel.Rows(project, "street_b", 7);
        Assert.Equal(["+0.3", "+12", "−20", "+18", "+35", "−25", "+20"], rows.Select(r => r.Readout));
        Assert.Equal(["tint"], rows.Where(r => r.Keyed).Select(r => r.Slider.Name));
        Assert.Equal(-0.25, rows.Single(r => r.Slider.Name == "tint").Value, 9);
        Assert.Throws<ZeyoCutException>(() => ColorPanel.Rows(project, "title", 1));
    }

    [Fact]
    public void ASliderSetsTheValueAsSetColorDoesOrKeysItAtThePlayhead()
    {
        var project = Edit();
        var change = ColorPanel.Change(project, "street_b", ColorPanel.Slider("exposure"), 0.3, 7);
        Assert.Equal("set_color street_b", change.Label);
        var expected = ProjectJson.Clone(project);
        Grade(expected, "street_b", ("exposure", 0.3));

        // The preview's copy has the change; the project doesn't until it's applied.
        var trial = ColorPanel.Trial(project, change);
        Assert.Equal(ProjectJson.Serialize(expected), ProjectJson.Serialize(trial));
        Assert.Null(((VideoLayer)project.FindLayer("street_b")!).Grade);
        change.Apply(project);
        Assert.Equal(ProjectJson.Serialize(expected), ProjectJson.Serialize(project));

        // Contrast too goes through set_color, and back to as shot the grade goes.
        ColorPanel.Change(project, "street_b", ColorPanel.Slider("contrast"), 1.4, 7).Apply(project);
        ColorPanel.Change(project, "street_b", ColorPanel.Slider("exposure"), 0, 7).Apply(project);
        var clip = (VideoLayer)project.FindLayer("street_b")!;
        Assert.Equal((1.4, null), (clip.Contrast.Value, clip.Grade));

        // A value with keyframes gets one at the playhead.
        Grade(project, "street_b", ("temperature", 0.2));
        clip.Grade!.Temperature.Keyframes.Add(new Keyframe { Time = 6, Value = 0 });
        clip.Grade.Temperature.Keyframes.Add(new Keyframe { Time = 9, Value = 0.6 });
        ColorPanel.Change(project, "street_b", ColorPanel.Slider("temperature"), -0.4, 7.5).Apply(project);
        Assert.Equal([(6.0, 0.0), (7.5, -0.4), (9.0, 0.6)], clip.Grade.Temperature.Keyframes.Select(k => (k.Time, k.Value)));
    }

    [Fact]
    public void BeforeIsTheClipAsShotAndTheButtonsAreSetColors()
    {
        var project = Edit();
        Assert.Same(project, ColorPanel.Before(project, "street"));
        Grade(project, "street", ("exposure", 0.5), ("saturation", 1.3));
        Grade(project, "street_2", ("tint", 0.2));
        ((VideoLayer)project.FindLayer("street")!).Opacity = new AnimatedValue(0.8);

        // Only the clip goes back as shot, in a copy; its other values stay.
        var before = ColorPanel.Before(project, "street");
        var clip = (VideoLayer)before.FindLayer("street")!;
        Assert.Equal((null, 1.0, 0.8), (clip.Grade, clip.Saturation.Value, clip.Opacity.Value));
        Assert.NotNull(((VideoLayer)before.FindLayer("street_2")!).Grade);
        Assert.NotNull(((VideoLayer)project.FindLayer("street")!).Grade);

        Assert.Equal("Apply to all street clips", ColorPanel.ApplyLabel(project, "street"));
        Assert.Equal(["street_2", "street_b"], ColorPanel.Others(project, "street"));
        Assert.Empty(ColorPanel.Others(project, "logo"));
        Assert.Equal("Apply to all logo clips", ColorPanel.ApplyLabel(project, "logo"));

        // Apply, paste and reset are set_color's sameFootage, from and reset.
        var applied = ProjectJson.Clone(project);
        ColorPanel.ApplyToFootage("street").Apply(applied);
        Assert.All(new[] { "street_2", "street_b" }, id => Assert.Equal(0.5, ((VideoLayer)applied.FindLayer(id)!).Grade!.Exposure.Value));
        ColorPanel.Paste("logo", "street").Apply(applied);
        Assert.Equal(1.3, ((ImageLayer)applied.FindLayer("logo")!).Saturation.Value);
        Assert.Equal("set_color street", ColorPanel.Reset("street").Label);
        ColorPanel.Reset("street").Apply(applied);
        Assert.False(ColorEditor.Graded((VideoLayer)applied.FindLayer("street")!));
    }

    // A frame BGRA, premultiplied, opaque: width by height of pixel(x, y).
    private static byte[] Frame(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        }

        return pixels;
    }

    private static IEnumerable<(int X, int Y)> Lit(ScopePicture scope) =>
        Enumerable.Range(0, scope.Width * scope.Height).Where(cell => scope.Pixels[cell * 4 + 3] > 0).Select(cell => (cell % scope.Width, cell / scope.Width));

    [Fact]
    public void TheWaveformOfAGrayRampIsADiagonalLine()
    {
        // Black to white across 256 px: each scope column's pixels are at their own brightness.
        var ramp = Frame(256, 16, (x, _) => ((byte)x, (byte)x, (byte)x));
        var waveform = Scopes.Waveform(ramp, 256, 16, 64, 64);
        Assert.Equal((64, 64), (waveform.Width, waveform.Height));
        Assert.Equal(Enumerable.Range(0, 64).Select(column => (column, 63 - column)), Lit(waveform).OrderBy(c => c.Item1));

        // Pure colors land at their Rec. 709 luma: red 54, green 182, blue 18.
        var bands = Frame(30, 10, (x, _) => x < 10 ? ((byte)255, (byte)0, (byte)0) : x < 20 ? ((byte)0, (byte)255, (byte)0) : ((byte)0, (byte)0, (byte)255));
        Assert.Equal([(0, 255 - 54), (1, 255 - 182), (2, 255 - 18)], Lit(Scopes.Waveform(bands, 30, 10, 3, 256)).OrderBy(c => c.Item1));

        // A transparent pixel isn't counted.
        var none = new byte[4 * 4 * 4];
        Assert.Empty(Lit(Scopes.Waveform(none, 4, 4, 4, 4)));
    }

    [Fact]
    public void TheVectorscopePutsEachColorAtItsHue()
    {
        // Gray in the middle; red up and a little left, blue right, green down left, as on a
        // vectorscope's graticule. A cell takes its pixels' color.
        var bands = Frame(40, 10, (x, _) => x < 10 ? ((byte)128, (byte)128, (byte)128) : x < 20 ? ((byte)255, (byte)0, (byte)0) : x < 30 ? ((byte)0, (byte)255, (byte)0) : ((byte)0, (byte)0, (byte)255));
        var scope = Scopes.Vectorscope(bands, 40, 10, 101);
        var red = Scopes.Spot(255, 0, 0, 101);
        var green = Scopes.Spot(0, 255, 0, 101);
        var blue = Scopes.Spot(0, 0, 255, 101);
        Assert.Equal((50, 50), Scopes.Spot(128, 128, 128, 101));
        Assert.True(red.X < 50 && red.Y < 10, $"red at {red}");
        Assert.True(blue.X > 90 && Math.Abs(blue.Y - 50) < 6, $"blue at {blue}");
        Assert.True(green.X < 50 && green.Y > 50, $"green at {green}");
        Assert.Equal(new[] { (50, 50), red, green, blue }.Order(), Lit(scope).Order());
        var cell = (red.Y * 101 + red.X) * 4;
        Assert.True(scope.Pixels[cell + 2] > 0 && scope.Pixels[cell + 1] == 0 && scope.Pixels[cell] == 0, "the red spot isn't red");

        // Rec. 709's color difference: red's Cr is 0.5, blue's Cb is 0.5.
        Assert.Equal(0.5, Scopes.Chroma(255, 0, 0).Cr, 3);
        Assert.Equal(0.5, Scopes.Chroma(0, 0, 255).Cb, 3);
        Assert.Equal((0.0, 0.0), Scopes.Chroma(77, 77, 77));
    }
}
