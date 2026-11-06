// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public sealed class SubjectTests
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Yellow = new(255, 255, 0);
    private static readonly MatteValues Screen = new("#28BE3C", null, null, null);

    // The spec's test: a clip of a red box moving over green (x = 8 + 4n on frame n, y 16-40), a title
    // over its path, and the title put behind the box with a chroma cutout. The box hides the title
    // exactly where they overlap, and the share text_occlusion reports is the overlap's share of the
    // title's pixels.
    [Fact]
    public void TextBehindAMovingShapeIsHiddenExactlyWhereTheyOverlap()
    {
        var session = Clip("subject-moving");
        AddTitle(session);
        using var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg);

        var before = Occlusion.Run(renderer, session.Snapshot(), "title", 0, 9, null, TestContext.Current.CancellationToken);
        Assert.All(before.Values.Frames, f => Assert.Equal(0, f.Covered));

        var change = session.Mutate("behind", p => SubjectEditor.Apply(p, "clip", ["title"], false, ChromaFor(p)));
        Assert.Equal("put 'title' behind the subject of 'clip': cutout 'clip_subject' draws the subject again above the text, cut out with chroma (key #28BE3C, tolerance 0.4, softness 0.2, spill 1) (zIndex 1)", change.Summary);
        Assert.Equal("the cutout follows 'clip': its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity", change.Notes[0]);
        var project = session.Snapshot();
        Assert.Equal(["clip", "title", "clip_subject"], SubjectEditor.DrawOrder(project).Select(l => l.Id));

        var after = Occlusion.Run(renderer, project, "title", 0, 9, null, TestContext.Current.CancellationToken);
        var covered = 0;
        for (var frame = 0; frame <= 9; frame++)
        {
            var text = TextAlpha(renderer, project, frame);
            var box = new SKRectI(8 + 4 * frame, 16, 32 + 4 * frame, 40);
            double inBox = 0, total = 0;
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    total += text[y * 96 + x];
                    inBox += box.Contains(x, y) ? text[y * 96 + x] : 0;
                }
            }

            var expected = inBox / total;
            var measured = Assert.NotNull(after.Values.Frames[frame].Covered);
            Assert.True(Math.Abs(expected - measured) < 0.001, $"frame {frame}: measured {measured}, overlap {expected}");
            covered += expected > 0 ? 1 : 0;

            using var image = renderer.RenderImage(project, frame);
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    if (text[y * 96 + x] == 255)
                    {
                        Near(box.Contains(x, y) ? Red : Yellow, TestImages.Pixel(image, x, y));
                    }
                }
            }
        }

        Assert.InRange(covered, 3, 8);
        Assert.Equal(0, after.Values.Frames[0].Covered);
        Assert.Contains("text_occlusion 'title' 0–0.9s (f0–f9, 10 frames): covered min 0.0%, max ", after.Describe());
        Assert.Contains("\n- f0–f", after.Describe());
        Assert.EndsWith(Path.Combine("renders", "text_occlusion_title_00000-00009.json"), after.SavedPath);
        Assert.True(File.Exists(after.SavedPath));
    }

    [Fact]
    public void TheCutoutFollowsSplitsRemovalsAndRipples()
    {
        var session = Clip("subject-follow");
        AddTitle(session);
        session.Mutate("behind", p => SubjectEditor.Apply(p, "clip", ["title"], false, ChromaFor(p)));
        session.Mutate("fade", p => KeyframeEditor.SetKeyframes(p, [Key("clip_subject", "opacity", 0.1, 0), Key("clip_subject", "opacity", 0.2, 1), Key("clip_subject", "opacity", 0.6, 1)]));

        session.Mutate("split", p => TimelineEditor.SplitLayer(p, "clip", 0.5));
        Assert.Equal(["clip", "clip_2"], Cutout(session).Sources);
        Assert.Contains("clip_subject [cutout]  z=1  follows=clip/clip_2  effects: matte(chroma)  keys: opacity(3)", LayerTree.Format(session.Snapshot()));

        // The second piece plays the box from x = 28 on; the cutout draws it over the title there too.
        using (var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg))
        {
            var project = session.Snapshot();
            var text = TextAlpha(renderer, project, 9);
            using var image = renderer.RenderImage(project, 9);
            var hidden = Enumerable.Range(0, 96 * 64).Count(i => text[i] == 255 && i % 96 is >= 44 and < 68 && i / 96 is >= 16 and < 40);
            Assert.True(hidden > 0);
            Assert.All(Enumerable.Range(0, 96 * 64).Where(i => text[i] == 255 && i % 96 is >= 44 and < 68 && i / 96 is >= 16 and < 40),
                i => Near(Red, TestImages.Pixel(image, i % 96, i / 96)));
        }

        var range = session.Mutate("cut", p => TimelineEditor.RemoveRange(p, "all", 0.3, 0.4, ripple: true));
        Assert.Equal(["clip", "clip_3", "clip_2"], Cutout(session).Sources);
        Assert.Equal([(0.1, 0.0), (0.2, 1.0), (0.3, 1.0), (0.5, 1.0)], Cutout(session).Opacity.Keyframes.Select(k => (k.Time, k.Value)));
        Assert.DoesNotContain("clip_subject", range.Shifted);

        Assert.Equal(["clip_2"], session.Mutate("remove", p => ProjectEditor.RemoveLayer(p, "clip_2", withChildren: false)));
        Assert.Equal(["clip", "clip_3"], Cutout(session).Sources);
        session.Mutate("remove", p => ProjectEditor.RemoveLayer(p, "clip_3", withChildren: false));
        Assert.Equal(["clip", "clip_subject"], session.Mutate("remove", p => ProjectEditor.RemoveLayer(p, "clip", withChildren: false)));
        Assert.Null(session.Snapshot().FindLayer("clip_subject"));
    }

    [Fact]
    public void ACutoutOnlyTakesItsOwnSettings()
    {
        var session = Clip("subject-edits");
        AddTitle(session);
        session.Mutate("behind", p => SubjectEditor.Apply(p, "clip", ["title"], false, ChromaFor(p)));
        const string follows = "'clip_subject' is the subject cutout of 'clip'; it always follows 'clip', so make this change on 'clip' (its own settings are only name, zIndex, visible and opacity)";
        var project = session.Snapshot();
        void Refused(Action<Project> edit) => Assert.Equal(follows, Assert.Throws<ZeyoCutException>(() => edit(ProjectJson.Clone(project))).Message);

        Refused(p => TimelineEditor.SplitLayer(p, "clip_subject", 0.5));
        Refused(p => TimelineEditor.MoveLayer(p, "clip_subject", 0.5));
        Refused(p => TimelineEditor.SetSpeed(p, "clip_subject", 2, ripple: false));
        Refused(p => TimelineEditor.RemoveRange(p, "clip_subject", 0.2, 0.4, ripple: false));
        Refused(p => TimelineEditor.AddTransition(p, "clip", "clip_subject", TransitionType.Crossfade, 0.2));
        Refused(p => ProjectEditor.DuplicateLayer(p, new DuplicateOptions { LayerId = "clip_subject" }));
        Refused(p => ProjectEditor.UpdateLayer(p, "clip_subject", new JsonObject { ["x"] = 10 }));
        Refused(p => EffectEditor.SetEffect(p, "clip_subject", EffectKind.Blur, new EffectValues(null, 2, 2, null, null, null, null), remove: false));
        Assert.Contains(follows, Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(ProjectJson.Clone(project), [Key("clip_subject", "x", 0, 5)])).Message);
        Assert.Equal(
            "'clip_subject' is a subject cutout; change its method with text_behind_subject on its source ('clip'), or its edges with refine_matte",
            Assert.Throws<ZeyoCutException>(() => MatteEditor.GetMattable(project, "clip_subject")).Message);

        session.Mutate("own", p => ProjectEditor.UpdateLayer(p, "clip_subject", new JsonObject { ["name"] = "presenter", ["opacity"] = 0.5, ["visible"] = false }));
        session.Mutate("refine", p => MatteEditor.Refine(p, "clip_subject", 1, 2));
        var cutout = Cutout(session);
        Assert.Equal(("presenter", 0.5, false, 1.0, 2.0), (cutout.Name, cutout.Opacity.Value, cutout.Visible, cutout.Matte!.Shrink, cutout.Matte.Feather));
        Assert.Equal(1, session.Mutate("clear", p => { KeyframeEditor.SetKeyframes(p, [Key("clip_subject", "opacity", 0.5, 1)]); return KeyframeEditor.ClearKeyframes(p, "clip_subject", null); }));

        var json = ProjectJson.LayerToJson(Cutout(session));
        Assert.Contains("\"type\":\"cutout\"", json.Replace(" ", ""));
        Assert.IsType<CutoutLayer>(ProjectJson.LayerFromNode(JsonNode.Parse(json)!.AsObject()));

        var broken = ProjectJson.Clone(session.Snapshot());
        var bad = (CutoutLayer)broken.FindLayer("clip_subject")!;
        bad.Sources = ["title", "gone"];
        bad.Matte = new Matte { Method = MatteMethod.AiImage };
        bad.ParentId = "clip";
        var errors = ProjectValidator.Validate(broken);
        Assert.Contains("layer 'clip_subject': a cutout has no parent; it follows its sources", errors);
        Assert.Contains("layer 'clip_subject': source 'title' is a text layer; a cutout's sources are video layers or image layers with drawings", errors);
        Assert.Contains(errors, e => e.StartsWith("layer 'clip_subject': source 'gone' not found"));
    }

    [Fact]
    public void ASubjectThatIsItsOwnLayerOnlyMovesTheText()
    {
        var session = Clip("subject-own");
        AddTitle(session);
        session.Mutate("key", p => MatteEditor.Set(p, "clip", ChromaFor(p)));
        session.Mutate("logo", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "group", ZIndex = 5 }));

        var change = session.Mutate("behind", p => SubjectEditor.Apply(p, "clip", ["title"], false, null));
        Assert.Equal("put 'title' behind 'clip'", change.Summary);
        Assert.Equal("'clip' already has its background removed (chroma), so it is its own subject and needs no cutout: the text now draws directly below 'clip' (zIndex 0)", change.Notes[0]);
        Assert.Null(change.Cutout);
        Assert.Equal(["title", "clip", "group"], SubjectEditor.DrawOrder(session.Snapshot()).Select(l => l.Id));
        Assert.DoesNotContain(session.Snapshot().Layers, l => l is CutoutLayer);

        // The key leaves green out, so the title shows there and the box covers it.
        using var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg);
        var covered = Occlusion.Run(renderer, session.Snapshot(), "title", 9, 9, null, TestContext.Current.CancellationToken);
        Assert.True(covered.Values.Frames[0].Covered > 0.5);

        Assert.Equal(
            "'group' is a null layer with no pictures under it, so it has no subject",
            Assert.Throws<ZeyoCutException>(() => SubjectEditor.Apply(session.Snapshot(), "group", ["title"], false, null)).Message);
        Assert.Equal(
            "'clip' is a video layer; textLayerIds takes text layers or a group of them (like captions)",
            Assert.Throws<ZeyoCutException>(() => SubjectEditor.Apply(session.Snapshot(), "clip", ["clip"], false, null)).Message);
    }

    [Fact]
    public void TextsBelowTheClipAreLiftedAndLayersInBetweenAreNamed()
    {
        var session = Clip("subject-lift");
        AddTitle(session);
        var art = session.Mutate("import", p => AssetImporter.Import(p, session.Directory, TestImages.Solid(Path.Combine(session.Directory, "..", "logo.png"), 8, 8, SKColors.White), "logo", new AssetProbe()));
        session.Mutate("logo", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = art.Id }, ZIndex = 1 }));
        session.Mutate("under", p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["zIndex"] = -1 }));
        session.Mutate("second", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "subtitle", Text = "yo", FontSize = 10, ZIndex = 4 }));

        var change = session.Mutate("behind", p => SubjectEditor.Apply(p, "clip", ["subtitle", "title"], false, ChromaFor(p)));
        Assert.Equal(["clip", "title", "logo", "subtitle", "clip_subject"], SubjectEditor.DrawOrder(session.Snapshot()).Select(l => l.Id));
        Assert.Contains("'title' was drawn below 'clip', so it now draws just above it", change.Notes);
        Assert.Contains("'logo' is also drawn between 'clip' and the cutout, so the subject covers it where they overlap", change.Notes);

        // Again with a new method: the same cutout, its edge settings kept.
        session.Mutate("refine", p => MatteEditor.Refine(p, "clip_subject", 2, null));
        var again = session.Mutate("again", p => SubjectEditor.Apply(p, "clip", ["title"], false, MatteEditor.Build((IMattable)p.FindLayer("clip")!, MatteMethod.AiVideo, new MatteValues(null, null, null, null))));
        Assert.Equal("put 'title' behind the subject of 'clip' with its cutout 'clip_subject' (aiVideo; shrink 2 px, feather 0 px, was chroma; zIndex 4)", again.Summary);
        Assert.Equal(
            ["the cutout follows 'clip': its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity",
             "'subtitle' is behind the subject too",
             "'logo' is also drawn between 'clip' and the cutout, so the subject covers it where they overlap"],
            again.Notes);
        Assert.Single(session.Snapshot().Layers.OfType<CutoutLayer>());
        Assert.Equal(2, Cutout(session).Matte!.Shrink);
    }

    [Fact]
    public void OcclusionCountsHalfTransparentLayersHalfAndSkipsFramesWithoutTheText()
    {
        var directory = TestPaths.NewDirectory("subject-half");
        var session = ProjectSession.Create(Path.Combine(directory, "project"), TestProjects.Empty(width: 80, height: 40, fps: 10, duration: 1));
        var white = session.Mutate("import", p => AssetImporter.Import(p, session.Directory, TestImages.Solid(Path.Combine(directory, "white.png"), 80, 40, SKColors.White), "white", new AssetProbe()));
        session.Mutate("text", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions
        {
            Id = "words",
            Text = "Hey",
            FontSize = 14,
            Color = "#000000",
            Box = new TextBackground { Color = "#000000", Padding = 3 },
            X = 40,
            Y = 20,
            Start = 0.3,
        }));
        session.Mutate("veil", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "veil", Drawings = new Dictionary<string, string> { ["normal"] = white.Id }, X = 40, Y = 20, Opacity = 0.5 }));
        using var renderer = new FrameRenderer(session.Directory);
        var project = session.Snapshot();
        Assert.Equal((3, 9), Occlusion.Frames(project, (TextLayer)project.FindLayer("words")!, null, null));
        Assert.Equal((0, 4), Occlusion.Frames(project, (TextLayer)project.FindLayer("words")!, 0, 0.45));

        var result = Occlusion.Run(renderer, project, "words", 0, 4, null, TestContext.Current.CancellationToken);
        Assert.Equal([null, null, null], result.Values.Frames.Take(3).Select(f => f.Covered));
        Assert.All(result.Values.Frames.Skip(3), f => Assert.InRange(f.Covered!.Value, 0.495, 0.505));
        Assert.StartsWith("text_occlusion 'words' 0–0.4s (f0–f4, 5 frames): covered min 50.", result.Describe());
        Assert.Contains("over the 2 frames it shows (3 not shown)\n- f0–f2 (0–0.2s): not shown\n- f3–f4 (0.3–0.4s): ", result.Describe());
    }

    private static void Near(SKColor expected, SKColor actual, int tolerance = 3) =>
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance && expected.Alpha == actual.Alpha,
            $"expected {expected}, got {actual}");

    // behindSubject=auto on the moving box (2 s): three one-word captions at the box's height; the
    // box covers most of the first two as it passes. Wherever it covers more than 35% of a caption,
    // the captions' cutout is faded out (so the caption shows in front), it fades over 0.25 s with no
    // jump, and elsewhere the box is drawn over the captions.
    [Fact]
    public void AutoCaptionsShowInFrontWhereTheSubjectWouldCoverThem()
    {
        var session = Clip("subject-captions", seconds: 2);
        AddTitle(session);
        session.Mutate("behind", p => SubjectEditor.Apply(p, "clip", ["title"], false, ChromaFor(p)));
        using var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg);
        var ct = TestContext.Current.CancellationToken;
        var plan = session.Snapshot();
        var result = CaptionBuilder.Generate(plan, Spoken, AutoCaptions, new ZeyoCut.Render.Text.TextMeasurer(renderer, plan));
        Assert.Equal(["captions_subject"], result.Subject!.Cover);
        Assert.Equal(MatteMethod.Chroma, ((CutoutLayer)plan.FindLayer("captions_subject")!).Matte!.Method);

        var measured = Occlusion.Captions(renderer, plan, result.LayerIds, result.Subject.Cover, null, ct);

        // Measuring only the cutout gives what text_occlusion gives (nothing else is drawn above the captions).
        foreach (var (id, first, last) in Occlusion.TextFrames(plan, result.LayerIds))
        {
            var run = Occlusion.Run(renderer, plan, id, first, last, null, ct);
            Assert.Equal(run.Values.Frames.Where(f => f.Covered is not null).Select(f => (f.Frame, f.Covered!.Value)), measured.Where(m => m.LayerId == id).Select(m => (m.Frame, m.Covered)));
        }

        Assert.Contains(measured, m => m.Covered > 0.6);
        var lines = CaptionBuilder.Auto(plan, result.Subject, measured, 35);
        Assert.StartsWith("auto: the subject covers up to ", lines[0]);
        Assert.Contains("; where it covers more than 35%, the captions show in front: 'captions_subject' fades out over 0.25s before each of these spans and back in after", lines[0]);
        Assert.StartsWith("in front ", Assert.Single(lines.Skip(1)));
        Assert.Empty(ProjectValidator.Validate(plan));

        var cutout = (CutoutLayer)plan.FindLayer("captions_subject")!;
        var hidden = ProjectJson.Clone(plan);
        hidden.FindLayer("captions_subject")!.Visible = false;
        var covered = measured.GroupBy(m => m.Frame).ToDictionary(g => g.Key, g => g.Max(m => m.Covered));
        var behind = 0;
        for (var frame = 0; frame < 20; frame++)
        {
            var opacity = Interpolator.Evaluate(cutout.Opacity, frame / 10.0);
            if (frame > 0)
            {
                Assert.True(Math.Abs(opacity - Interpolator.Evaluate(cutout.Opacity, (frame - 1) / 10.0)) <= 0.4 + 1e-9, $"the cutout jumps at frame {frame}");
            }

            var share = covered.GetValueOrDefault(frame);
            if (share > 0.35)
            {
                Assert.Equal(0, opacity);
            }

            using var shown = renderer.RenderImage(plan, frame);
            using var without = renderer.RenderImage(hidden, frame);
            if (opacity == 0)
            {
                Assert.Equal(Bytes(without), Bytes(shown));
            }
            else if (share > 0.01)
            {
                Assert.NotEqual(Bytes(without), Bytes(shown));
                behind++;
            }
        }

        Assert.True(behind > 0, "no frame draws the box over a caption");
    }

    // A clip that is its own subject (keyed with chroma): the captions go directly below it, and with
    // auto the captions it would cover too much draw in front of it instead.
    [Fact]
    public void AutoCaptionsOfAClipThatIsItsOwnSubjectDrawInFrontWhereCovered()
    {
        var session = Clip("subject-captions-own", seconds: 2);
        session.Mutate("key", p => MatteEditor.Set(p, "clip", ChromaFor(p)));
        using var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg);
        var ct = TestContext.Current.CancellationToken;
        var plan = session.Snapshot();
        var result = CaptionBuilder.Generate(plan, Spoken, AutoCaptions, new ZeyoCut.Render.Text.TextMeasurer(renderer, plan));
        Assert.Equal(["clip"], result.Subject!.Cover);
        Assert.Null(plan.FindLayer("captions_subject"));

        var measured = Occlusion.Captions(renderer, plan, result.LayerIds, result.Subject.Cover, null, ct);
        var front = measured.Where(m => m.Covered > 0.35).Select(m => m.LayerId).Distinct().ToList();
        Assert.Equal(["captions_1", "captions_2"], front);

        var lines = CaptionBuilder.Auto(plan, result.Subject, measured, 35);

        Assert.Contains("'captions_1' (", lines[0]);
        Assert.Contains("would be covered more than 35%, so they draw in front of 'clip' (a caption can't fade there, since the subject is its own layer); the other captions stay behind it", lines[0]);
        Assert.Equal(["captions_3", "clip", "captions_1", "captions_2"], SubjectEditor.DrawOrder(plan).Select(l => l.Id).Where(id => id != "captions"));
        foreach (var (id, first, last) in Occlusion.TextFrames(plan, result.LayerIds))
        {
            var run = Occlusion.Run(renderer, plan, id, first, last, null, ct);
            var before = measured.Where(m => m.LayerId == id).Select(m => m.Covered);
            Assert.Equal(front.Contains(id) ? before.Select(_ => 0.0) : before, run.Values.Frames.Where(f => f.Covered is not null).Select(f => f.Covered!.Value));
        }

        Assert.Empty(ProjectValidator.Validate(plan));
    }

    // One-word captions of the clip at the box's height (bold_center, 14 px), at 0.1, 0.9 and 1.5 s.
    private static readonly Transcript Spoken = new()
    {
        Asset = "screen",
        Language = "en",
        Words =
        [
            new TranscriptWord { Text = "Hi.", Start = 0.1, End = 0.3, Probability = 0.9 },
            new TranscriptWord { Text = "Go.", Start = 0.9, End = 1.1, Probability = 0.9 },
            new TranscriptWord { Text = "Ok.", Start = 1.5, End = 1.7, Probability = 0.9 },
        ],
    };

    private static readonly CaptionOptions AutoCaptions = new() { AssetId = "screen", Style = "bold_center", FontSize = 14, BehindSubject = BehindSubject.Auto };

    private static byte[] Bytes(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.Bytes;
    }

    private static ProjectSession Clip(string name, double seconds = 1)
    {
        Ffmpeg.Require();
        var directory = TestPaths.NewDirectory(name);
        var file = TestVideos.GreenScreen(Path.Combine(directory, "screen.mp4"), seconds);
        var session = ProjectSession.Create(Path.Combine(directory, "project"), TestProjects.Empty(width: 96, height: 64, fps: 10, duration: seconds));
        TestVideos.Import(session, file, "screen");
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "screen" }));
        return session;
    }

    // A yellow title (box and glyphs the same color) right of the box's start, over rows 20-44.
    private static void AddTitle(ProjectSession session) =>
        session.Mutate("title", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions
        {
            Id = "title",
            Text = "Hi",
            FontSize = 14,
            Bold = true,
            Color = "#FFFF00",
            Box = new TextBackground { Color = "#FFFF00", Padding = 4 },
            AnchorX = 0,
            AnchorY = 0,
            X = 44,
            Y = 24,
        }));

    private static Matte ChromaFor(Project project) => MatteEditor.Build((IMattable)project.FindLayer("clip")!, MatteMethod.Chroma, Screen);

    private static CutoutLayer Cutout(ProjectSession session) => (CutoutLayer)session.Snapshot().FindLayer("clip_subject")!;

    private static KeyframeEntry Key(string layer, string property, double time, double value) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value));

    // The title's coverage (alpha) at a frame, drawn alone.
    private static byte[] TextAlpha(FrameRenderer renderer, Project project, int frame)
    {
        using var image = renderer.RenderImage(project, frame, new RenderOptions(Transparent: true, Only: new HashSet<string> { "title" }));
        return Pixels.ReadAlpha(image);
    }
}
