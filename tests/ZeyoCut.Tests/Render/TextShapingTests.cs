// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Text;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class TextShapingTests
{
    private const string Burmese = "မင်္ဂလာပါ";
    private const string Japanese = "こんにちは";

    private readonly ITestOutputHelper _output;

    public TextShapingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static SKTypeface Typeface(string family)
    {
        var typeface = SKFontManager.Default.MatchFamily(family);
        Assert.NotNull(typeface);
        Assert.Equal(family, typeface.FamilyName);
        return typeface;
    }

    private (SKImage Image, TextLayout Layout, string Path) RenderText(string name, TextLayerOptions options, int width = 900, int height = 260)
    {
        var directory = TestPaths.NewDirectory($"text-{name}");
        var project = TestProjects.Empty(width: width, height: height, duration: 1);
        var session = ProjectSession.Create(directory, project);
        using var renderer = new FrameRenderer(directory);
        var layer = session.Mutate("add", p => ProjectEditor.AddTextLayer(p, options with { Id = "text" }, new TextMeasurer(renderer, p)));
        var snapshot = session.Snapshot();
        var image = renderer.RenderImage(snapshot, 0);
        var layout = renderer.Text.Layout(snapshot, directory, layer);

        var path = System.IO.Path.Combine(TestPaths.Root, "text-shaping", $"{name}.png");
        Png.Save(image, path);
        _output.WriteLine($"saved {path}");
        return (image, layout, path);
    }

    [Fact]
    public void BurmeseIsShapedByHarfBuzz()
    {
        Fonts.Require("Myanmar Text");
        var typeface = Typeface("Myanmar Text");
        using var font = TextLayoutEngine.CreateFont(typeface, 64, embolden: false);
        var engine = new TextLayoutEngine(new FontResolver());

        var shaped = engine.Shape(typeface, font, Burmese);
        var naive = font.GetGlyphs(Burmese);

        Assert.All(shaped.Glyphs, g => Assert.NotEqual(0, g));
        Assert.All(naive, g => Assert.NotEqual(0, g));
        Assert.False(shaped.Glyphs.SequenceEqual(naive), "HarfBuzz output should differ from a one-glyph-per-character mapping (kinzi/medial reordering)");
        _output.WriteLine($"codepoints={Burmese.Length} naive=[{string.Join(",", naive)}] shaped=[{string.Join(",", shaped.Glyphs)}]");
    }

    [Fact]
    public void JapaneseIsShaped()
    {
        Fonts.Require("Yu Gothic");
        var typeface = Typeface("Yu Gothic");
        using var font = TextLayoutEngine.CreateFont(typeface, 64, embolden: false);
        var shaped = new TextLayoutEngine(new FontResolver()).Shape(typeface, font, Japanese);
        Assert.Equal(5, shaped.Glyphs.Length);
        Assert.All(shaped.Glyphs, g => Assert.NotEqual(0, g));
        Assert.True(shaped.Width > 5 * 50);
    }

    [Fact]
    public void RendersBurmeseAndJapanesePngs()
    {
        Fonts.Require("Myanmar Text", "Yu Gothic");
        var (burmese, burmeseLayout, _) = RenderText("burmese_myanmar_text", new TextLayerOptions
        {
            Text = Burmese,
            FontFamily = "Myanmar Text",
            FontSize = 96,
            X = 450,
            Y = 130,
        });
        Assert.Empty(burmeseLayout.Warnings);
        Assert.True(TestImages.CountDarkPixels(burmese) > 2000);

        var (japanese, japaneseLayout, _) = RenderText("japanese_yu_gothic", new TextLayerOptions
        {
            Text = Japanese,
            FontFamily = "Yu Gothic",
            FontSize = 96,
            X = 450,
            Y = 130,
        });
        Assert.Empty(japaneseLayout.Warnings);
        Assert.True(TestImages.CountDarkPixels(japanese) > 2000);

        var (burmeseSubtitle, burmeseSubtitleLayout, _) = RenderText("subtitle_burmese_wrapped", new TextLayerOptions
        {
            Text = "မင်္ဂလာပါ ခင်ဗျာ၊ ဒီနေ့ ရာသီဥတု သာယာလို့ ပန်းခြံထဲမှာ လမ်းလျှောက်ကြရအောင်။",
            FontFamily = "Myanmar Text",
            FontSize = 44,
            Color = "#FFFFFF",
            StrokeColor = "#000000",
            StrokeWidth = 3,
            Align = "center",
            MaxWidth = 640,
            Box = new TextBackground { Color = "#1E3A8ACC", Padding = 14, CornerRadius = 12 },
            X = 450,
            Y = 150,
        }, height: 300);
        Assert.Empty(burmeseSubtitleLayout.Warnings);
        Assert.True(burmeseSubtitleLayout.Lines.Count >= 2);
        Assert.All(burmeseSubtitleLayout.Lines, l => Assert.True(l.Width <= 640.5f));
        Assert.Equal(
            "မင်္ဂလာပါ ခင်ဗျာ၊ ဒီနေ့ ရာသီဥတု သာယာလို့ ပန်းခြံထဲမှာ လမ်းလျှောက်ကြရအောင်။",
            string.Join(" ", burmeseSubtitleLayout.Lines.Select(l => l.Text)));

        var (japaneseSubtitle, japaneseSubtitleLayout, _) = RenderText("subtitle_japanese_wrapped", new TextLayerOptions
        {
            Text = "こんにちは、元気ですか。今日はいい天気なので、公園を散歩しましょう。",
            FontFamily = "Yu Gothic",
            Bold = true,
            FontSize = 44,
            Color = "#FFFFFF",
            StrokeColor = "#000000",
            StrokeWidth = 3,
            Align = "center",
            MaxWidth = 640,
            Box = new TextBackground { Color = "#7F1D1DCC", Padding = 14, CornerRadius = 12 },
            X = 450,
            Y = 150,
        }, height: 300);
        Assert.Empty(japaneseSubtitleLayout.Warnings);
        Assert.True(japaneseSubtitleLayout.Lines.Count >= 2);
        Assert.All(japaneseSubtitleLayout.Lines, l => Assert.False(l.Text.StartsWith('。') || l.Text.StartsWith('、')));

        burmese.Dispose();
        japanese.Dispose();
        burmeseSubtitle.Dispose();
        japaneseSubtitle.Dispose();
    }

    [Fact]
    public void MissingGlyphsAndFontsProduceWarnings()
    {
        Fonts.Require("Arial");
        var (image, layout, _) = RenderText("burmese_in_arial", new TextLayerOptions { Text = Burmese, FontFamily = "Arial", FontSize = 48 });
        Assert.Contains(layout.Warnings, w => w.Contains("has no glyphs") && w.Contains("Myanmar Text"));
        image.Dispose();

        var (image2, layout2, _) = RenderText("unknown_font", new TextLayerOptions { Text = "Hi", FontFamily = "No Such Font 123", FontSize = 48 });
        Assert.Contains(layout2.Warnings, w => w.Contains("not installed"));
        image2.Dispose();
    }

    [Fact]
    public void TheShapingCheckNamesTheCharactersAFontHasNoGlyphsFor()
    {
        Fonts.Require("Myanmar Text", "Yu Gothic", "Segoe UI");
        var directory = TestPaths.NewDirectory("text-missing");
        var project = TestProjects.Empty();
        var engine = new TextLayoutEngine(new FontResolver());
        TextLayout Layout(string text, string family)
        {
            var layer = new TextLayer { Id = $"t{project.Layers.Count}", Text = text, FontFamily = family, FontSize = 40 };
            project.Layers.Add(layer);
            return engine.Layout(project, directory, layer);
        }

        Assert.Equal("", Layout(Burmese, "Myanmar Text").Missing);
        Assert.Equal("", Layout(Japanese, "Yu Gothic").Missing);
        Assert.Equal("", Layout("Cafe\u0301 déjà vu", "Segoe UI").Missing);

        var mixed = Layout($"Hi {Burmese}\n{Japanese}", "Segoe UI");
        Assert.Equal("မင်္ဂလာပါこんに", mixed.Missing);
        Assert.Contains("font 'Segoe UI' has no glyphs for \"မင်္ဂလာပါこんに\"; pick a font that covers this script (Burmese: \"Myanmar Text\", Japanese: \"Yu Gothic\")", mixed.Warnings);
    }

    [Fact]
    public void MyanmarBreaksAtSyllables()
    {
        Assert.Equal(["မင်္ဂ", "လာ", "ပါ"], LineBreaker.SplitAtBreaks(Burmese));
        Assert.Equal(["ကျွန်", "တော်"], LineBreaker.SplitAtBreaks("ကျွန်တော်"));
    }

    [Fact]
    public void JapaneseBreaksBetweenCharactersButNotBeforeClosingPunctuation()
    {
        Assert.Equal(["こ", "ん", "に", "ち", "は。", "「元", "気」"], LineBreaker.SplitAtBreaks("こんにちは。「元気」"));
    }

    [Fact]
    public void WrapsAtSpacesGreedily()
    {
        Assert.Equal(["hello ", "big ", "world"], LineBreaker.SplitAtBreaks("hello big world"));
        Assert.Equal(["hello big", "world"], LineBreaker.Wrap("hello big world", 9, s => s.Length));
        Assert.Equal(["abcd", "efgh", "ij"], LineBreaker.Wrap("abcdefghij", 4, s => s.Length));
        Assert.Equal(["ab", "こんに", "ちは"], LineBreaker.Wrap("ab こんにちは", 3, s => s.Length));
    }

    [Fact]
    public void LayoutAlignsLinesInsideMaxWidth()
    {
        Fonts.Require("Segoe UI");
        var directory = TestPaths.NewDirectory("text-align");
        var project = TestProjects.Empty();
        var layer = new TextLayer { Id = "t", Text = "Hi\nHello there", FontFamily = "Segoe UI", FontSize = 40, Align = TextAlign.Center, MaxWidth = 600 };
        project.Layers.Add(layer);
        var layout = new TextLayoutEngine(new FontResolver()).Layout(project, directory, layer);

        Assert.Equal(600, layout.BoxWidth);
        Assert.InRange(layout.BoxHeight, 2 * 40 * 1.1f, 2 * 40 * 1.6f);

        layer.LineHeight = 2;
        var doubled = new TextLayoutEngine(new FontResolver()).Layout(project, directory, layer);
        Assert.Equal(layout.BoxHeight * 2, doubled.BoxHeight, 3);
        foreach (var line in layout.Lines)
        {
            Assert.Equal((600 - line.Width) / 2, line.X, 3);
        }
    }
}
