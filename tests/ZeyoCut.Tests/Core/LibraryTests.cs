// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class LibraryTests
{
    private sealed class FakeMeasurer : ITextMeasurer
    {
        public (double Width, double Height) Measure(TextLayer layer) => (layer.Text.Length * layer.FontSize * 0.5, layer.FontSize * 1.2);
    }

    // The test rig (tora_body with tora_head and tora_tail) with real drawing files.
    private static (Project Project, string Directory) RigProject(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var project = TestProjects.Rig();
        project.Duration = 4;
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        var colors = new[] { SKColors.Orange, SKColors.Gold, SKColors.Red, SKColors.Brown };
        for (var i = 0; i < project.Assets.Count; i++)
        {
            TestImages.Solid(Path.Combine(directory, "assets", project.Assets[i].Id + ".png"), 200, 100, colors[i]);
        }

        return (project, directory);
    }

    private static Keyframe Key(double time, double value, Easing easing = Easing.Linear) => new() { Time = time, Value = value, Easing = easing };

    // A hop over 0–1s: the body moves and bobs, the head tilts and looks shocked, the tail swings.
    private static void Animate(Project project)
    {
        var body = (ImageLayer)project.FindLayer("tora_body")!;
        body.X.Keyframes = [Key(0, 300, Easing.EaseInOut), Key(1, 500)];
        body.Y.Keyframes = [Key(0, 200, Easing.EaseOut), Key(0.5, 150, Easing.EaseIn), Key(1, 200)];
        body.ScaleY.Keyframes = [Key(0, 1), Key(0.5, 1.2), Key(1, 1)];
        var head = (ImageLayer)project.FindLayer("tora_head")!;
        head.Rotation.Keyframes = [Key(0, 0, Easing.EaseOutBack), Key(0.5, 20, Easing.EaseInOut), Key(1, 0)];
        head.Drawing.Keyframes = [new DrawingKeyframe { Time = 0, Value = "normal" }, new DrawingKeyframe { Time = 0.5, Value = "shocked" }, new DrawingKeyframe { Time = 0.9, Value = "normal" }];
        var tail = (ImageLayer)project.FindLayer("tora_tail")!;
        tail.Rotation.Keyframes = [Key(0, 0, Easing.EaseInOut), Key(0.4, -15, Easing.EaseInOut), Key(1, 0)];
    }

    [Fact]
    public void ASavedActionGivesASecondCopyOfTheRigTheSameMotion()
    {
        var (project, directory) = RigProject("library-action");
        var library = new Library(TestPaths.NewDirectory("library-action-lib"));
        Animate(project);

        Rigs.Save(project, directory, library, "tora_body", "tora", time: null, overwrite: false);
        RigActions.Save(project, library, "tora_body", 0, 1, "hop", overwrite: false);
        var placed = Rigs.Add(project, directory, library, "tora", 800, 300, 0, 1, null);
        Assert.Equal("tora_2_body", placed.RootId);
        Assert.Equal(["tora_2_body", "tora_2_head", "tora_2_tail"], placed.LayerIds.Order());
        Assert.Empty(placed.NewAssets);

        var action = library.Load<ActionFile>(LibraryKind.Action, "hop");
        var result = RigActions.Apply(project, action, "tora_2_body", 2, 1, 1);
        Assert.Equal((2.0, 3.0), (result.Start, result.End));
        Assert.Empty(ProjectValidator.Validate(project));

        var moved = Affine2D.Translation(800 - 300, 300 - 200);
        var roles = Rigs.Roles(project, "tora_body");
        var copy = Rigs.Roles(project, "tora_2_body");
        Assert.Equal(roles.Keys.Order(), copy.Keys.Order());
        for (var frame = 0; frame <= 30; frame++)
        {
            var t = frame / 30.0;
            var a = SceneEvaluator.Evaluate(project, t).ToDictionary(e => e.Layer.Id);
            var b = SceneEvaluator.Evaluate(project, t + 2).ToDictionary(e => e.Layer.Id);
            foreach (var (role, layer) in roles)
            {
                var expected = moved * a[layer.Id].World;
                var actual = b[copy[role].Id].World;
                foreach (var (x, y) in new[] { (expected.A, actual.A), (expected.B, actual.B), (expected.C, actual.C), (expected.D, actual.D), (expected.E, actual.E), (expected.F, actual.F) })
                {
                    Assert.Equal(x, y, 6);
                }

                if (layer is ImageLayer image)
                {
                    Assert.Equal(Interpolator.Evaluate(image.Drawing, t), Interpolator.Evaluate(((ImageLayer)copy[role]).Drawing, t + 2));
                }
            }
        }
    }

    [Fact]
    public void ARigKeepsItsPartsDrawingsAndParentingInAnotherProject()
    {
        var (source, sourceDirectory) = RigProject("library-rig-src");
        var library = new Library(TestPaths.NewDirectory("library-rig-lib"));
        Animate(source);

        var rig = Rigs.Save(source, sourceDirectory, library, "tora_body", "Tora Cat", time: 0.5, overwrite: false);
        Assert.Equal("body", rig.Root);
        Assert.Equal(["body", "head", "tail"], rig.Parts.Select(p => p.Id));
        Assert.All(rig.Parts, p => Assert.Equal(0, ((VisualLayer)p).X.Keyframes.Count + ((VisualLayer)p).Rotation.Keyframes.Count));
        Assert.Equal(20, ((ImageLayer)rig.Parts[1]).Rotation.Value, 9);
        Assert.Equal("shocked", ((ImageLayer)rig.Parts[1]).Drawing.Value);
        Assert.Equal(4, rig.Assets.Count);
        Assert.True(File.Exists(Path.Combine(library.PathOf(LibraryKind.Rig, "Tora Cat"), "assets", "head_shocked.png")));
        Assert.Contains("already has a rig named 'Tora Cat'",
            Assert.Throws<ZeyoCutException>(() => Rigs.Save(source, sourceDirectory, library, "tora_body", "Tora Cat", null, false)).Message);

        var directory = TestPaths.NewDirectory("library-rig-dst");
        var project = TestProjects.Empty(width: 1280, height: 720);
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "bg", ZIndex = 5 });
        var first = Rigs.Add(project, directory, library, "tora cat", 400, 500, 1, 0.5, null);
        var second = Rigs.Add(project, directory, library, "Tora Cat", 900, 500, 0, 1, "rival");

        Assert.Equal("tora_cat_body", first.RootId);
        Assert.Equal(4, first.NewAssets.Count);
        Assert.Equal("rival_body", second.RootId);
        Assert.Empty(second.NewAssets);
        Assert.Equal(4, second.ReusedAssets.Count);
        Assert.Empty(ProjectValidator.Validate(project));

        var head = (ImageLayer)project.FindLayer("tora_cat_head")!;
        var body = (ImageLayer)project.FindLayer("tora_cat_body")!;
        Assert.Equal("tora_cat_body", head.ParentId);
        Assert.Equal("head", head.RigRole);
        Assert.Null(body.RigRole);
        Assert.Equal((400.0, 500.0, 0.5), (body.X.Value, body.Y.Value, body.ScaleX.Value));
        Assert.Equal((1.0, 1.0), (body.Start, head.Start));
        Assert.Equal(["normal", "shocked"], head.Drawings.Keys.Order());
        Assert.All(head.Drawings.Values, id => Assert.True(File.Exists(ProjectStore.AssetPath(directory, project.FindAsset(id)!))));
        Assert.True(project.FindLayer("tora_cat_tail")!.ZIndex < body.ZIndex && body.ZIndex < head.ZIndex && head.ZIndex > 5);
        Assert.Equal(["root", "head", "tail"], Rigs.Roles(project, "rival_body").Keys);
        Assert.Contains("no rig named 'dog' in the library; rigs: Tora Cat",
            Assert.Throws<ZeyoCutException>(() => Rigs.Add(project, directory, library, "dog", 0, 0, 0, 1, null)).Message);
    }

    [Fact]
    public void LoopsCarryTheRootForwardAndTheRigMustFit()
    {
        var (project, directory) = RigProject("library-loops");
        var library = new Library(TestPaths.NewDirectory("library-loops-lib"));
        var body = (ImageLayer)project.FindLayer("tora_body")!;
        body.X.Keyframes = [Key(0, 100), Key(1, 150)];
        var tail = (ImageLayer)project.FindLayer("tora_tail")!;
        tail.Rotation.Keyframes = [Key(0, 0), Key(0.5, 10), Key(1, 0)];
        var step = RigActions.Capture(project, "tora_body", 0, 1, "step");
        body.X.Keyframes.Clear();
        body.X.Value = 600;
        tail.Rotation.Keyframes.Clear();

        var result = RigActions.Apply(project, step, "tora_body", 1, 2, 3);

        Assert.Equal(2.5, result.End, 9);
        Assert.Equal([(1.0, 600.0), (1.5, 650.0), (2.0, 700.0), (2.5, 750.0)], body.X.Keyframes.Select(k => (k.Time, k.Value)));
        Assert.Equal([1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5], tail.Rotation.Keyframes.Select(k => k.Time));
        Assert.Equal(ActionMode.Offset, step.Tracks.Single(t => t.Property == "x").Mode);
        Assert.Equal(ActionMode.Absolute, step.Tracks.Single(t => t.Role == "tail").Mode);

        var noTail = RigProject("library-loops-2").Project;
        noTail.Layers.RemoveAll(l => l.Id == "tora_tail");
        Assert.Contains("moves part(s) tail, which rig 'tora_body' doesn't have; its parts: root, head",
            Assert.Throws<ZeyoCutException>(() => RigActions.Apply(noTail, step, "tora_body", 0, 1, 1)).Message);
        Assert.Contains("speed must be between 0.05 and 20",
            Assert.Throws<ZeyoCutException>(() => RigActions.Apply(project, step, "tora_body", 0, 0, 1)).Message);
        Assert.Contains("nothing under 'tora_body' is animated between 3s and 4s",
            Assert.Throws<ZeyoCutException>(() => RigActions.Capture(project, "tora_body", 3, 4, "idle")).Message);

        project.FindLayer("tora_tail")!.RigRole = "head";
        Assert.Contains("both have the role 'head'",
            Assert.Throws<ZeyoCutException>(() => Rigs.Roles(project, "tora_body")).Message);
        _ = directory;
        _ = library;
    }

    [Fact]
    public void StylesScaleWithTheCanvasAndStyleCaptions()
    {
        var directory = TestPaths.NewDirectory("library-style");
        var library = new Library(TestPaths.NewDirectory("library-style-lib"));
        var source = TestProjects.Empty(width: 1920, height: 1080);
        var title = ProjectEditor.AddTextLayer(source, new TextLayerOptions
        {
            Id = "title",
            Text = "Hello",
            FontFamily = "Arial",
            Bold = true,
            FontSize = 90,
            Color = "#FFD700",
            StrokeColor = "#000000",
            StrokeWidth = 6,
            Align = "center",
            Box = new TextBackground { Color = "#00000080", Padding = 30, CornerRadius = 12 },
        });
        TextStyles.Save(source, directory, library, title.Id, "gold title", CaptionPosition.Center, overwrite: false);
        Assert.Contains("is a image layer; save_style takes a text layer",
            Assert.Throws<ZeyoCutException>(() => TextStyles.Save(source, directory, library, ProjectEditor.AddImageLayer(source, new ImageLayerOptions { Id = "n" }).Id, "x", CaptionPosition.Bottom, false)).Message);

        var project = TestProjects.Empty(width: 1280, height: 720);
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "group" });
        var measurer = new FakeMeasurer();
        foreach (var id in new[] { "a", "b" })
        {
            ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = id, Text = "Some words", ParentId = "group", FontSize = 30 }, measurer);
        }

        var style = library.Load<TextStyle>(LibraryKind.Style, "Gold Title");
        var styled = TextStyles.Apply(project, directory, library, style, "group", measurer);

        Assert.Equal(["a", "b"], styled);
        var a = (TextLayer)project.FindLayer("a")!;
        Assert.Equal((60.0, 4.0, "#FFD700", true, "Arial"), (a.FontSize, a.StrokeWidth, a.Color, a.Bold, a.FontFamily));
        Assert.Equal((20.0, 8.0), (a.Box!.Padding, a.Box.CornerRadius));
        Assert.Equal(measurer.Measure(a), (a.AnchorX * 2, a.AnchorY * 2));

        var options = TextStyles.Defaults(project, directory, library, style, new TextLayerOptions { Text = "Hi", Color = "#FFFFFF" });
        Assert.Equal((60.0, "#FFFFFF", "center"), (options.FontSize!.Value, options.Color, options.Align));

        var transcript = new Transcript { Asset = "talk", Words = [new TranscriptWord { Text = "Hello", Start = 0.2, End = 0.6 }, new TranscriptWord { Text = "there.", Start = 0.6, End = 1 }] };
        project.Assets.Add(new Asset { Id = "talk", Type = AssetType.Audio, File = "assets/talk.wav", Duration = 2 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "talk", Role = "voice" });
        CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk", Style = "gold title", LibraryStyle = style }, measurer);
        var caption = ProjectEditor.Subtree(project, "captions").OfType<TextLayer>().Single();
        Assert.Equal((60.0, "#FFD700", "#000000", 360.0), (caption.FontSize, caption.Color, caption.StrokeColor, caption.Y.Value));
        Assert.Equal(measurer.Measure(caption).Height / 2, caption.AnchorY, 6);
    }

    [Fact]
    public void TemplatesStretchFullLengthLayersAndPinTheEnd()
    {
        var (source, sourceDirectory) = RigProject("library-template-src");
        var library = new Library(TestPaths.NewDirectory("library-template-lib"));
        source.Duration = 10;
        source.Audio.LoudnessTarget = -16;
        ProjectEditor.AddTextLayer(source, new TextLayerOptions { Id = "watermark", Text = "zeyo", End = 10 });
        ProjectEditor.AddTextLayer(source, new TextLayerOptions { Id = "intro", Text = "Hi", End = 2 });
        var end = ProjectEditor.AddTextLayer(source, new TextLayerOptions { Id = "endscreen", Text = "Subscribe", Start = 8, End = 10 });
        end.Opacity.Keyframes = [Key(8, 0), Key(9, 1)];
        ProjectEditor.AddTextLayer(source, new TextLayerOptions { Id = "endnote", Text = "thanks", ParentId = "endscreen", Start = 8.5 });

        var template = Templates.Save(source, sourceDirectory, library, "channel", ["watermark", "intro", "endscreen", "tora_body"], ["endscreen"], overwrite: false);
        Assert.Equal(["endscreen", "endnote"], template.PinToEnd);
        Assert.Equal(7, template.Project.Layers.Count);
        Assert.Contains("pinToEnd layer 'tora_head' is not among the layers being saved",
            Assert.Throws<ZeyoCutException>(() => Templates.Save(source, sourceDirectory, library, "x", ["intro"], ["tora_head"], false)).Message);

        var directory = Path.Combine(TestPaths.NewDirectory("library-template-dst"), "episode 1");
        var warnings = new List<string>();
        var project = Templates.Create(library, "channel", directory, "Episode 1", 20, warnings);
        var session = ProjectSession.Create(directory, project);

        var saved = session.Snapshot();
        Assert.Equal(("Episode 1", 20.0, -16.0), (saved.Name, saved.Duration, saved.Audio.LoudnessTarget!.Value));
        Assert.Null(saved.FindLayer("watermark")!.End);
        Assert.Equal((0.0, 2.0), (saved.FindLayer("intro")!.Start, saved.FindLayer("intro")!.End!.Value));
        Assert.Equal((18.0, 20.0), (saved.FindLayer("endscreen")!.Start, saved.FindLayer("endscreen")!.End!.Value));
        Assert.Equal([18.0, 19.0], ((TextLayer)saved.FindLayer("endscreen")!).Opacity.Keyframes.Select(k => k.Time));
        Assert.Equal(18.5, saved.FindLayer("endnote")!.Start);
        Assert.All(saved.Assets, a => Assert.True(File.Exists(ProjectStore.AssetPath(directory, a))));
        Assert.Empty(warnings);

        Assert.Contains("'endscreen' is pinned to the end and starts 2s before it; use a duration of at least 2s",
            Assert.Throws<ZeyoCutException>(() => Templates.Create(library, "channel", directory + "-short", "short", 1, warnings)).Message);
        Assert.Contains("a project already exists in",
            Assert.Throws<ZeyoCutException>(() => Templates.Create(library, "channel", directory, "again", null, warnings)).Message);
    }

    [Fact]
    public void LibraryNamesAreCheckedAndListed()
    {
        var library = new Library(TestPaths.NewDirectory("library-names"));
        Assert.Empty(library.Names(LibraryKind.Rig));
        Assert.Contains("is not a valid action name",
            Assert.Throws<ZeyoCutException>(() => Library.CheckName("../evil", LibraryKind.Action)).Message);
        Assert.Contains("no style named 'none' in the library; there are no styles yet",
            Assert.Throws<ZeyoCutException>(() => library.Load<TextStyle>(LibraryKind.Style, "none")).Message);

        var (project, directory) = RigProject("library-names-rig");
        Animate(project);
        RigActions.Save(project, library, "tora_body", 0, 0.5, "wave b", false);
        RigActions.Save(project, library, "tora_body", 0, 1, "Wave A", false);
        RigActions.Save(project, library, "tora_body", 0, 1, "wave b", true);

        Assert.Equal(["Wave A", "wave b"], library.Names(LibraryKind.Action));
        Assert.Equal(1, library.Load<ActionFile>(LibraryKind.Action, "wave b").Duration);
        _ = directory;
    }

    [Fact]
    public void AssetFilesMustStayInTheAssetsFolder()
    {
        Assert.True(ProjectStore.IsAssetFile("assets/tora_body.png"));
        Assert.True(ProjectStore.IsAssetFile("assets\\tora body.png"));
        foreach (var bad in new[] { "../x.png", "assets/../x.png", "assets/sub/x.png", "C:\\Windows\\win.ini", "C:/x.png", "\\\\server\\share\\x.png", "assets/x.png:stream", "assets/", "assets/..", "x.png", "" })
        {
            Assert.False(ProjectStore.IsAssetFile(bad), bad);
        }

        var project = TestProjects.Rig();
        project.Assets[0].File = "../../outside.png";
        Assert.Contains($"asset '{project.Assets[0].Id}': file '../../outside.png' must be a file in the project's assets folder", ProjectValidator.Validate(project));
        Assert.Contains("must be a file in the assets folder",
            Assert.Throws<ZeyoCutException>(() => ProjectStore.AssetPath(TestPaths.NewDirectory("library-asset-path"), project.Assets[0])).Message);
    }

    // A template or rig from someone else is only JSON plus files; its file names can't make
    // new_project_from_template or add_rig write or read outside the folders they belong in.
    [Fact]
    public void ATemplateOrRigCantReachOutsideItsFolder()
    {
        var (source, sourceDirectory) = RigProject("library-escape-src");
        var library = new Library(TestPaths.NewDirectory("library-escape-lib"));
        Templates.Save(source, sourceDirectory, library, "shared", ["tora_body"], [], overwrite: false);
        var templateFile = Path.Combine(library.PathOf(LibraryKind.Template, "shared"), "template.json");
        File.WriteAllText(templateFile, File.ReadAllText(templateFile).Replace("\"file\": \"assets/", "\"file\": \"../escaped_"));

        var parent = TestPaths.NewDirectory("library-escape-dst");
        var directory = Path.Combine(parent, "episode");
        Assert.Contains("must be a file in the assets folder",
            Assert.Throws<ZeyoCutException>(() => Templates.Create(library, "shared", directory, "Episode", null, [])).Message);
        Assert.Empty(Directory.GetFiles(parent, "escaped_*", SearchOption.AllDirectories));

        Rigs.Save(source, sourceDirectory, library, "tora_body", "tora", time: null, overwrite: false);
        var rigFile = Path.Combine(library.PathOf(LibraryKind.Rig, "tora"), "rig.json");
        File.WriteAllText(rigFile, File.ReadAllText(rigFile).Replace("\"file\": \"assets/", "\"file\": \"../../"));
        var (target, targetDirectory) = RigProject("library-escape-rig");
        var before = target.Assets.Count;
        Assert.Contains("must be a file in the assets folder",
            Assert.Throws<ZeyoCutException>(() => Rigs.Add(target, targetDirectory, library, "tora", 100, 100, 0, 1, null)).Message);
        Assert.Equal(before, target.Assets.Count);
    }
}
