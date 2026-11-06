// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class CaptionTests
{
    private static readonly string[] Spoken =
        "So today we look at the editor. It cuts silences for you, and it writes captions that follow every cut. Pretty neat.".Split(' ');

    // Words 0.3s apart (0.25s long), a 1s pause after each sentence.
    private static Transcript Talk()
    {
        var words = new List<TranscriptWord>();
        var t = 0.5;
        foreach (var text in Spoken)
        {
            words.Add(new TranscriptWord { Text = text, Start = Math.Round(t, 3), End = Math.Round(t + 0.25, 3), Probability = 0.9 });
            t += text.EndsWith('.') ? 1.3 : 0.3;
        }

        return new Transcript { Asset = "talk", Language = "en", Words = words };
    }

    private static Project WithTalk(int width = 1920, int height = 1080)
    {
        var project = TestProjects.Empty(width: width, height: height, duration: 12);
        project.Assets.Add(new Asset { Id = "talk", Type = AssetType.Video, File = "assets/talk.mp4", Width = 1920, Height = 1080, FrameRate = "30", FrameCount = 360, Duration = 12, HasAudio = true });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "talk", Id = "talk" });
        return project;
    }

    private static List<TextLayer> Captions(Project project, string group = "captions") =>
        ProjectEditor.Subtree(project, group).OfType<TextLayer>().OrderBy(t => t.Start).ToList();

    [Fact]
    public void CaptionsAlignWithTheTranscript()
    {
        var project = WithTalk();
        var transcript = Talk();

        var result = CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk" }, null);

        var captions = Captions(project);
        Assert.True(result.CreatedGroup);
        Assert.Equal(result.Cues.Count, captions.Count);
        Assert.Equal(Spoken, captions.SelectMany(c => c.Text.Split([' ', '\n'])));
        Assert.All(captions, c => Assert.Equal("captions", c.ParentId));
        for (var i = 0; i < captions.Count; i++)
        {
            var cue = result.Cues[i];
            Assert.Equal(transcript.Words[cue.FirstWord].Start, captions[i].Start, 6);
            Assert.True(captions[i].End >= transcript.Words[cue.LastWord].End - 1e-6, $"cue {i} ends before its last word");
            if (i + 1 < captions.Count)
            {
                Assert.True(captions[i].End <= captions[i + 1].Start + 1e-9, $"cue {i} overlaps the next");
            }

            foreach (var word in transcript.Words.Skip(cue.FirstWord).Take(cue.LastWord - cue.FirstWord + 1))
            {
                Assert.True(word.Start >= captions[i].Start - 1e-6 && word.End <= captions[i].End + 1e-6);
                var shown = SceneEvaluator.Evaluate(project, (word.Start + word.End) / 2).Where(e => e.Shown && e.Layer is TextLayer).ToList();
                Assert.Equal(captions[i].Id, Assert.Single(shown).Layer.Id);
            }
        }

        Assert.Equal(["So today we look", "at the editor."], result.Cues[0].Lines);
        Assert.Equal(0, result.Cues[0].FirstWord);
        Assert.Empty(ProjectValidator.Validate(project));
        Assert.Equal(2.85, captions[0].End!.Value, 6);
        Assert.DoesNotContain(SceneEvaluator.Evaluate(project, 3.2), e => e.Shown && e.Layer is TextLayer);
    }

    [Fact]
    public void CaptionsFollowCutsAndRegeneratingReplacesThem()
    {
        var project = WithTalk();
        var transcript = Talk();
        CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk", MaxWordsPerLine = 4 }, null);
        var first = Captions(project).Count;

        var cut = SilenceCutter.WordRanges(transcript, [(7, 10)]);
        var plan = SilenceCutter.Plan(project, project.FindLayer("talk")!, cut, 0);
        SilenceCutter.Apply(project, plan);
        var removed = plan.Removed;

        var result = CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk", MaxWordsPerLine = 4 }, null);

        Assert.False(result.CreatedGroup);
        Assert.Equal(first, result.Replaced);
        var texts = Captions(project).SelectMany(c => c.Text.Split([' ', '\n'])).ToList();
        Assert.Equal(Spoken.Take(7).Concat(Spoken.Skip(11)), texts);
        var after = transcript.Words[11];
        var cue = result.Cues.Single(c => c.FirstWord == 11);
        Assert.Equal(after.Start - removed, cue.Start, 6);
        Assert.Equal(2, project.Layers.Count(l => l is VideoLayer));
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void WordsWithoutDurationAreCaptioned()
    {
        var project = WithTalk();
        var transcript = Talk();
        foreach (var index in new[] { 1, 11 })
        {
            transcript.Words[index].End = transcript.Words[index].Start;
        }

        var plan = SilenceCutter.Plan(project, project.FindLayer("talk")!, SilenceCutter.WordRanges(transcript, [(7, 10)]), 0);
        SilenceCutter.Apply(project, plan);
        Assert.Equal(transcript.Words[11].Start, ((VideoLayer)project.FindLayer("talk_2")!).TrimIn, 9);

        CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk", MaxWordsPerLine = 4 }, null);

        var texts = Captions(project).SelectMany(c => c.Text.Split([' ', '\n'])).ToList();
        Assert.Equal(Spoken.Take(7).Concat(Spoken.Skip(11)), texts);
    }

    [Fact]
    public void LinesAreBalancedAndCuesBreakAtPauses()
    {
        var words = Enumerable.Range(0, 7).Select(i => new TimedWord(i, $"w{i}", i * 0.3, i * 0.3 + 0.25))
            .Append(new TimedWord(7, "late", 4, 4.3))
            .ToList();

        var cues = CaptionBuilder.Cues(words, maxWordsPerLine: 6, maxLines: 2, projectDuration: 10);

        Assert.Equal(2, cues.Count);
        Assert.Equal(["w0 w1 w2 w3", "w4 w5 w6"], cues[0].Lines);
        Assert.Equal(2.05 + CaptionBuilder.Hold, cues[0].End, 6);
        Assert.Equal(["late"], cues[1].Lines);
        Assert.Equal(4.6, cues[1].End, 6);

        var three = CaptionBuilder.Cues(words.Take(7).ToList(), maxWordsPerLine: 3, maxLines: 1, projectDuration: 10);
        Assert.Equal(["w0 w1 w2", "w3 w4 w5", "w6"], three.Select(c => c.Text));
        Assert.True(three[0].End <= three[1].Start);

        Assert.Contains("maxWordsPerLine must be between 1 and 20",
            Assert.Throws<ZeyoCutException>(() => CaptionBuilder.Cues(words, 0, 2, 10)).Message);
    }

    [Fact]
    public void StylesPlaceTheCaptions()
    {
        var vertical = WithTalk(width: 1080, height: 1920);
        CaptionBuilder.Generate(vertical, Talk(), new CaptionOptions { AssetId = "talk", Style = "bold_center", MaxWordsPerLine = 3 }, null);
        var bold = Captions(vertical)[0];
        Assert.True(bold.Bold);
        Assert.Equal("#000000", bold.StrokeColor);
        Assert.Equal(76, bold.FontSize);
        Assert.Equal(1920 * 0.62, bold.Y.Value, 6);
        Assert.Equal(540, bold.X.Value);
        Assert.Equal(TextAlign.Center, bold.Align);

        var landscape = WithTalk();
        CaptionBuilder.Generate(landscape, Talk(), new CaptionOptions { LayerId = "talk", Style = "subtitle", FontSize = 50 }, null);
        var subtitle = Captions(landscape)[0];
        Assert.NotNull(subtitle.Box);
        Assert.Equal(50, subtitle.FontSize);
        Assert.Equal(1080 * 0.93, subtitle.Y.Value, 6);
        Assert.Equal(2 * 50 * 1.2 * 1.1, subtitle.AnchorY, 6);
        Assert.Equal(1920 * 0.86 / 2, subtitle.AnchorX, 6);
        var group = (ImageLayer)landscape.FindLayer("captions")!;
        Assert.Empty(group.Drawings);
        Assert.Equal((0.0, 0.0), (group.X.Value, group.Y.Value));
        Assert.True(group.ZIndex > ((VideoLayer)landscape.FindLayer("talk")!).ZIndex);

        Assert.Contains("unknown caption style 'karaoke'",
            Assert.Throws<ZeyoCutException>(() => CaptionBuilder.Generate(landscape, Talk(), new CaptionOptions { AssetId = "talk", Style = "karaoke" }, null)).Message);
        Assert.Contains("give either assetId",
            Assert.Throws<ZeyoCutException>(() => CaptionBuilder.Generate(landscape, Talk(), new CaptionOptions { AssetId = "talk", LayerId = "talk" }, null)).Message);
        ProjectEditor.AddTextLayer(landscape, new TextLayerOptions { Id = "title", Text = "Hi" });
        Assert.Contains("is not a caption group",
            Assert.Throws<ZeyoCutException>(() => CaptionBuilder.Generate(landscape, Talk(), new CaptionOptions { AssetId = "talk", GroupId = "title" }, null)).Message);
    }

    [Fact]
    public void FastClipsGetFasterCaptionsAndSrtUsesTimelineTimes()
    {
        var project = TestProjects.Empty(duration: 12);
        project.Assets.Add(new Asset { Id = "talk", Type = AssetType.Video, File = "assets/talk.mp4", Width = 640, Height = 360, FrameRate = "30", FrameCount = 360, Duration = 12, HasAudio = true });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "talk", Id = "talk", Start = 1, Speed = 2 });
        var transcript = Talk();

        var result = CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk" }, null);

        Assert.Equal(1 + transcript.Words[0].Start / 2, result.Cues[0].Start, 6);
        var cues = CaptionBuilder.GroupCues(project, "captions");
        Assert.Equal(result.Cues.Count, cues.Count);
        var srt = CaptionBuilder.Srt(cues);
        Assert.StartsWith("1\r\n00:00:01,250 --> ", srt);
        Assert.Contains("So today we look\r\nat the editor.\r\n\r\n2\r\n", srt);
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "empty" });
        Assert.Contains("'empty' has no caption text layers; run generate_captions first",
            Assert.Throws<ZeyoCutException>(() => CaptionBuilder.GroupCues(project, "empty")).Message);
    }

    [Fact]
    public void CaptionsGoBehindTheSubjectWithACutoutOfTheirOwn()
    {
        var project = WithTalk();
        var transcript = Talk();
        var on = new CaptionOptions { AssetId = "talk", BehindSubject = BehindSubject.On };
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Title" });
        SubjectEditor.Apply(project, "talk", ["title"], false, MatteEditor.Build((IMattable)project.FindLayer("talk")!, MatteMethod.Chroma, new MatteValues("#00FF00", null, null, null)));

        var result = CaptionBuilder.Generate(project, transcript, on, null);

        var cutout = Assert.IsType<CutoutLayer>(project.FindLayer("captions_subject"));
        Assert.Equal("captions", cutout.CaptionGroup);
        Assert.Equal(["talk"], cutout.Sources);
        Assert.Equal(MatteMethod.Chroma, cutout.Matte!.Method);
        Assert.Equal("#00FF00", cutout.Matte.Color);
        Assert.Equal(["captions_subject"], result.Subject!.Cover);
        Assert.Equal(["talk"], result.Subject.Clips);
        Assert.Equal(["talk", "title", "talk_subject", "captions", .. result.LayerIds, "captions_subject"], SubjectEditor.DrawOrder(project).Select(l => l.Id));
        var change = result.Subject.Change;
        Assert.Equal($"put the captions behind the subject of 'talk': cutout 'captions_subject' draws the subject again above them, cut out with chroma (key #00FF00, tolerance 0.4, softness 0.2, spill 1) (zIndex {cutout.ZIndex})", change.Summary);
        Assert.Equal(
            [
                "the cutout follows the clip that plays 'talk' ('talk'): its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity",
                "'title' is behind the subject too",
                "it uses the matte of 'talk_subject', the cutout of the same clip from text_behind_subject",
            ],
            change.Notes);
        Assert.Contains("\n" + change.Summary + "\n- the cutout follows the clip", CaptionBuilder.Describe(result));
        Assert.Contains($"captions_subject [cutout]  z={cutout.ZIndex}  follows=talk  for=captions  effects: matte(chroma)", LayerTree.Format(project));
        Assert.Empty(ProjectValidator.Validate(project));

        // text_behind_subject keeps to the clip's own cutout; the captions' cutout only changes through generate_captions.
        Assert.Equal("talk_subject", SubjectEditor.Apply(project, "talk", ["title"], false, null).Cutout!.Id);
        Assert.Contains("'captions_subject' is the subject cutout of the captions under 'captions'; generate_captions gives it its method",
            Assert.Throws<ZeyoCutException>(() => MatteEditor.GetMattable(project, "captions_subject")).Message);
        Assert.Equal("generate_captions", new PendingMatte("captions_subject", "talk", MatteMethod.AiVideo, "m.mkv", true, true).Tool);

        // Generating again reuses it above the new captions, without the fades of before.
        ((CutoutLayer)project.FindLayer("captions_subject")!).Opacity.Keyframes.Add(new Keyframe { Time = 1, Value = 0 });
        var again = CaptionBuilder.Generate(project, transcript, on with { MaxWordsPerLine = 3 }, null);
        Assert.Single(project.Layers.OfType<CutoutLayer>(), c => c.CaptionGroup == "captions");
        Assert.Equal("captions_subject", SubjectEditor.DrawOrder(project)[^1].Id);
        Assert.Empty(((CutoutLayer)project.FindLayer("captions_subject")!).Opacity.Keyframes);
        Assert.StartsWith($"put the captions behind the subject of 'talk' with their cutout 'captions_subject' (chroma (key #00FF00", again.Subject!.Change.Summary);

        var off = CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk" }, null);
        Assert.Equal("removed 'captions_subject', the captions' subject cutout, since behindSubject is off", off.Removed);
        Assert.Null(project.FindLayer("captions_subject"));
        Assert.EndsWith("\n" + off.Removed, CaptionBuilder.Describe(off));
        Assert.Null(CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk" }, null).Removed);

        // Removing the caption group takes its cutout along.
        CaptionBuilder.Generate(project, transcript, on, null);
        Assert.Contains("captions_subject", ProjectEditor.RemoveLayer(project, "captions", withChildren: true));
        Assert.Null(project.FindLayer("captions_subject"));
        Assert.Equal("talk_subject", Assert.Single(project.Layers.OfType<CutoutLayer>()).Id);
        Assert.Empty(ProjectValidator.Validate(project));

        // Without a cutout to borrow from, the matte is aiVideo.
        var fresh = WithTalk();
        CaptionBuilder.Generate(fresh, transcript, on, null);
        Assert.Equal(MatteMethod.AiVideo, ((CutoutLayer)fresh.FindLayer("captions_subject")!).Matte!.Method);
        Assert.Empty(ProjectValidator.Validate(fresh));
    }

    [Fact]
    public void CaptionsGoDirectlyBelowAClipThatIsItsOwnSubject()
    {
        var project = WithTalk();
        var transcript = Talk();
        var talk = (IMattable)project.FindLayer("talk")!;
        talk.Matte = MatteEditor.Build(talk, MatteMethod.Chroma, new MatteValues("#00FF00", null, null, null));

        var result = CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "talk", BehindSubject = BehindSubject.Auto }, null);

        Assert.Null(result.Subject!.Change.Cutout);
        Assert.Equal(["talk"], result.Subject.Cover);
        Assert.Equal("put the captions behind 'talk'", result.Subject.Change.Summary);
        Assert.Equal("'talk' already has its background removed (chroma), so it is its own subject and needs no cutout: the captions now draw directly below 'talk' (zIndex 0)", result.Subject.Change.Notes[0]);
        Assert.Equal([.. result.LayerIds, "talk", "captions"], SubjectEditor.DrawOrder(project).Select(l => l.Id));

        // auto: a caption the clip would cover too much draws in front of it; the others stay behind.
        var second = result.LayerIds[1];
        var frame = (int)Math.Ceiling(project.FindLayer(second)!.Start * project.Fps);
        var lines = CaptionBuilder.Auto(project, result.Subject, [new CaptionCover(result.LayerIds[0], 20, 0.2), new CaptionCover(second, frame, 0.5), new CaptionCover(second, frame + 1, 0.4)], 35);

        Assert.Equal([$"auto: the subject covers up to 50.0% of a caption ('{second}' at {(frame / 30.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}s; 3 frames measured); '{second}' (50.0%) would be covered more than 35%, so it draws in front of 'talk' (a caption can't fade there, since the subject is its own layer); the other captions stay behind it"], lines);
        Assert.Equal([.. result.LayerIds.Where(id => id != second), "talk", second, "captions"], SubjectEditor.DrawOrder(project).Select(l => l.Id));
        Assert.Empty(ProjectValidator.Validate(project));

        project.Assets.Add(new Asset { Id = "speech", Type = AssetType.Audio, File = "assets/speech.wav", Duration = 12 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "speech", Id = "voice" });
        Assert.Equal("behindSubject needs a video clip to cut the subject from, and 'voice' is an audio layer; generate the captions with behindSubject=off, then put them behind a clip with text_behind_subject (textLayerIds [\"captions\"])",
            Assert.Throws<ZeyoCutException>(() => CaptionBuilder.Generate(project, transcript, new CaptionOptions { LayerId = "voice", BehindSubject = BehindSubject.On }, null)).Message);
        Assert.StartsWith("behindSubject needs a video clip to cut the subject from, and 'speech' only plays on audio layers;",
            Assert.Throws<ZeyoCutException>(() => CaptionBuilder.Generate(project, transcript, new CaptionOptions { AssetId = "speech", BehindSubject = BehindSubject.Auto }, null)).Message);
    }

    // Spans of covered frames (10 fps) join when less than 0.5 s apart; the cutout fades out over
    // 0.25 s, finished when a span starts, and back in after; at the very start and end it doesn't fade.
    [Fact]
    public void AutoFadesTheCaptionsCutoutAroundWhereTheyWouldBeCovered()
    {
        static IEnumerable<CaptionCover> Frames(string id, int first, int last, double covered) =>
            Enumerable.Range(first, last - first + 1).Select(f => new CaptionCover(id, f, covered));
        var over = Frames("a", 0, 1, 0.4).Concat(Frames("b", 10, 20, 0.5)).Concat(Frames("c", 25, 30, 0.6)).Concat(Frames("d", 60, 70, 0.45)).Concat(Frames("e", 115, 119, 0.9)).ToList();

        var spans = CaptionBuilder.FrontSpans(over, 10, 12);

        Assert.Equal([(0.0, 0.2), (1.0, 3.1), (6.0, 7.1), (11.5, 12.0)], spans.Select(s => (s.Start, s.End)));
        Assert.Equal(["b", "c"], spans[1].LayerIds);
        Assert.Equal(0.6, spans[1].Covered);

        var cutout = new CutoutLayer { Id = "captions_subject", Opacity = new AnimatedValue(0.8) };
        CaptionBuilder.Fade(cutout, spans, 12);

        Assert.Equal(
            [(0, 0), (0.2, 0), (0.45, 0.8), (0.75, 0.8), (1, 0), (3.1, 0), (3.35, 0.8), (5.75, 0.8), (6, 0), (7.1, 0), (7.35, 0.8), (11.25, 0.8), (11.5, 0)],
            cutout.Opacity.Keyframes.Select(k => (k.Time, k.Value)));
        Assert.Equal(0, Interpolator.Evaluate(cutout.Opacity, 0.1));
        Assert.Equal(0.8, Interpolator.Evaluate(cutout.Opacity, 0.6), 9);
        Assert.Equal(0.4, Interpolator.Evaluate(cutout.Opacity, 0.875), 9);
        Assert.Equal(0, Interpolator.Evaluate(cutout.Opacity, 2.3));
        Assert.Equal(0, Interpolator.Evaluate(cutout.Opacity, 11.9));

        // In a project: the lines name the spans.
        var project = WithTalk();
        var result = CaptionBuilder.Generate(project, Talk(), new CaptionOptions { AssetId = "talk", BehindSubject = BehindSubject.Auto }, null);
        var first = result.LayerIds[0];
        var lines = CaptionBuilder.Auto(project, result.Subject!, [.. Frames(first, 30, 35, 0.52), .. Frames(first, 36, 40, 0.1)], 35);

        Assert.Equal(
            [
                $"auto: the subject covers up to 52.0% of a caption ('{first}' at 1s; 11 frames measured); where it covers more than 35%, the captions show in front: 'captions_subject' fades out over 0.25s before each of these spans and back in after",
                $"in front 1–1.2s ('{first}', up to 52.0%)",
            ],
            lines);
        Assert.Equal([(0.75, 1.0), (1, 0), (1.2, 0), (1.45, 1)], ((CutoutLayer)project.FindLayer("captions_subject")!).Opacity.Keyframes.Select(k => (k.Time, k.Value)));
        Assert.Equal(["auto: the subject covers up to 10.0% of a caption ('" + first + "' at 1.2s; 5 frames measured), not more than 35%, so the captions stay behind it the whole time"],
            CaptionBuilder.Auto(project, result.Subject!, [.. Frames(first, 36, 40, 0.1)], 35));
        Assert.Empty(((CutoutLayer)project.FindLayer("captions_subject")!).Opacity.Keyframes);
        Assert.Equal(["auto: the captions aren't shown in any frame, so there was nothing to measure"], CaptionBuilder.Auto(project, result.Subject!, [], 35));
        Assert.Empty(ProjectValidator.Validate(project));
    }
}
