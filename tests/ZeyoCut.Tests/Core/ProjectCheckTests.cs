// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class ProjectCheckTests
{
    private sealed class FakeMeasurer : ITextMeasurer
    {
        public (double Width, double Height) Measure(TextLayer layer) => (layer.Text.Length * layer.FontSize * 0.5, layer.FontSize * 1.2);
    }

    private static Keyframe Key(double time, double value) => new() { Time = time, Value = value };

    private static Dictionary<string, string> Drawing(string asset) => new() { ["normal"] = asset };

    [Fact]
    public void CheckProjectReportsEachSeededProblem()
    {
        var directory = TestPaths.NewDirectory("check-seeded");
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        var project = TestProjects.Empty(width: 640, height: 360, fps: 10, duration: 3);
        foreach (var id in new[] { "box", "ghost", "spare" })
        {
            project.Assets.Add(new Asset { Id = id, Type = AssetType.Image, File = $"assets/{id}.png", Width = 100, Height = 100 });
            if (id != "ghost")
            {
                TestImages.Solid(Path.Combine(directory, "assets", $"{id}.png"), 100, 100, SKColors.Red);
            }
        }

        var measurer = new FakeMeasurer();
        var box = ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "box", Drawings = Drawing("box"), X = 320, Y = 180, End = 2 });
        box.X.Keyframes = [Key(0, 300), Key(1, 340), Key(2.5, 400), Key(2.8, 500)];
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "ghosty", Drawings = Drawing("ghost"), X = 100, Y = 100, End = 2 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "far", Drawings = Drawing("box"), X = 2000, Y = 180, End = 2 });
        var late = ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "late", Drawings = Drawing("box"), X = 500, Y = 200, Start = 1, End = 2 });
        late.Opacity.Keyframes = [Key(0.2, 0), Key(0.5, 0), Key(1, 0), Key(1.5, 1)];
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", FontSize = 40, X = 320, Y = 20, End = 2 }, measurer);

        var report = ProjectChecker.Check(project, directory, measurer);
        var text = report.Describe();

        Assert.Equal(
            [CheckKind.MissingFile, CheckKind.OutsideCanvas, CheckKind.OutsideSafeArea, CheckKind.EmptyFrames, CheckKind.DeadKeyframes, CheckKind.DeadKeyframes, CheckKind.UnusedAsset],
            report.Issues.Select(i => i.Kind));
        Assert.StartsWith("check_project: 7 problem(s) in 'test' (30 frames checked)", text);
        Assert.Contains("missing files (1):\n- asset 'ghost' (assets/ghost.png) has no file; used by ghosty, which won't render or export", text);
        Assert.Contains("off the canvas (1):\n- 'far' is never on the canvas while it's shown (0–2s); check its x/y, its parent and the camera", text);
        Assert.Contains("text outside the safe area (1):\n- text 'title' reaches into the title-safe margin (5%, top) at 0–2s", text);
        Assert.Contains("empty frames (1):\n- nothing but the background shows at 2–3s (10 frames)", text);
        Assert.Contains("- box.x: 1 keyframe(s) at 2.8s come after its time (0–2s) and never play", text);
        Assert.Contains("- late.opacity: 2 keyframe(s) at 0.2s, 0.5s come before its time (1–2s) and never play", text);
        Assert.Contains("unused assets (1):\n- image asset 'spare' isn't used by any layer", text);
    }

    [Fact]
    public void VerticalCanvasesKeepTextOutOfTheShortsPlayerUi()
    {
        var directory = TestPaths.NewDirectory("check-shorts");
        var project = TestProjects.Empty(width: 1080, height: 1920, fps: 10, duration: 1);
        var measurer = new FakeMeasurer();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "middle", Text = "Fine here", FontSize = 60, X = 500, Y = 900 }, measurer);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "caption", Text = "Too low", FontSize = 60, X = 500, Y = 1700, Start = 0.5 }, measurer);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "side", Text = "Edge", FontSize = 60, X = 1000, Y = 800 }, measurer);

        var text = ProjectChecker.Check(project, directory, measurer).Describe();

        Assert.Contains("text outside the safe area (2):", text);
        Assert.Contains("- text 'caption' reaches into the Shorts UI zone (bottom 25%) at 0.5–1s", text);
        Assert.Contains("- text 'side' reaches into the Shorts UI zone (right 12%) at 0–1s", text);
        Assert.DoesNotContain("'middle'", text);
    }

    [Fact]
    public void AProjectWithoutProblemsSaysSo()
    {
        var directory = TestPaths.NewDirectory("check-clean");
        var project = TestProjects.Empty(width: 320, height: 240, fps: 10, duration: 1);
        var measurer = new FakeMeasurer();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "hi", Text = "Hi", FontSize = 40 }, measurer);
        var hi = (TextLayer)project.FindLayer("hi")!;
        hi.X.Keyframes = [Key(0, 150), Key(0.5, 170), Key(1.5, 200)];

        var report = ProjectChecker.Check(project, directory, measurer);

        Assert.Empty(report.Issues);
        Assert.Equal("check_project: no problems in 'test' (10 frames checked for missing files, layers off the canvas, text outside the safe area, glyphs a font lacks, empty frames, keyframes that never play and unused assets)", report.Describe());
    }

    // Has every glyph in Myanmar Text and none for Burmese in any other font.
    private sealed class BurmeseMeasurer : ITextMeasurer
    {
        public (double Width, double Height) Measure(TextLayer layer) => (100, 40);

        public string MissingGlyphs(TextLayer layer) =>
            layer.FontFamily == "Myanmar Text" ? "" : string.Concat(layer.Text.Where(c => c is >= '\u1000' and <= '\u109F').Distinct());
    }

    [Fact]
    public void TextsWhoseFontLacksGlyphsAreNamedWithTheirLanguagesFont()
    {
        var directory = TestPaths.NewDirectory("check-glyphs");
        var project = TestProjects.Empty(width: 640, height: 360, fps: 10, duration: 1);
        var measurer = new BurmeseMeasurer();
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "captions_my", X = 0, Y = 0, Language = "my" });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "cap_my", Text = "မင်", ParentId = "captions_my", X = 320, Y = 180 }, measurer);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title_my", Text = "မင်္ဂလာပါ", FontFamily = "Myanmar Text", Language = "my" }, measurer);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "loose", Text = "Hi မင်" }, measurer);

        var text = ProjectChecker.Check(project, directory, measurer).Describe();
        Assert.StartsWith("check_project: 2 problem(s) in 'test' (10 frames checked)\nglyphs a font lacks (2):", text);
        Assert.Contains("\n- text 'cap_my' (my): font 'Segoe UI' has no glyphs for \"မင်\", which show as empty boxes; the my font is \"Myanmar Text\" (update_layer fontFamily)", text);
        Assert.Contains("\n- text 'loose': font 'Segoe UI' has no glyphs for \"မင်\", which show as empty boxes; give it a font that has them (update_layer fontFamily or fontAsset)", text);

        var configured = ProjectChecker.Check(project, directory, measurer, new Dictionary<string, string> { ["my"] = "Padauk" }).Describe();
        Assert.Contains("- text 'cap_my' (my): font 'Segoe UI' has no glyphs for \"မင်\", which show as empty boxes; the my font is \"Padauk\" (update_layer fontFamily)", configured);
    }
}
