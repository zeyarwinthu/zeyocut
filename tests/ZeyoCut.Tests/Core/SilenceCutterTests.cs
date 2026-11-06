// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class SilenceCutterTests
{
    private static Project WithClip(double seconds = 4, double duration = 4)
    {
        var project = TestProjects.Empty(duration: duration);
        project.Assets.Add(new Asset
        {
            Id = "clip",
            Type = AssetType.Video,
            File = "assets/clip.mp4",
            Width = 160,
            Height = 120,
            FrameRate = "30",
            FrameCount = (int)Math.Round(seconds * 30),
            Duration = seconds,
            HasAudio = true,
        });
        return project;
    }

    private static TranscriptWord Word(string text, double start, double end) => new() { Text = text, Start = start, End = end, Probability = 0.9 };

    private static List<TranscriptWord> Sentence() =>
    [
        Word("Hello", 0.15, 0.5),
        Word("world.", 0.5, 0.89),
        Word("This", 1.86, 2.09),
        Word("is", 2.09, 2.21),
        Word("a", 2.22, 2.24),
        Word("test.", 2.28, 2.49),
    ];

    [Fact]
    public void ToneGapToneLosesTheGapMinusPadding()
    {
        var project = WithClip();
        var clip = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip" });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "outro", Text = "Bye", Start = 3.5 });

        var plan = SilenceCutter.Plan(project, clip, [new TimeRange(1, 3)], padding: 0.1);
        Assert.Single(plan.Cuts);
        Assert.Equal(new PlannedCut(1.1, 2.9, 1.1, 2.9), plan.Cuts[0]);
        Assert.Equal(2.2, plan.NewDuration, 9);
        Assert.Contains("1 cut(s), 1.8s removed; duration 4s → 2.2s", SilenceCutter.Describe(plan));

        var result = SilenceCutter.Apply(project, plan, punchIn: 1.1);

        Assert.Equal(2.2, project.Duration, 9);
        Assert.Equal(["clip", "clip_2"], result.Pieces);
        var left = (VideoLayer)project.FindLayer("clip")!;
        var right = (VideoLayer)project.FindLayer("clip_2")!;
        Assert.Equal((0.0, 1.1), (left.TrimIn, left.TrimOut));
        Assert.Equal((1.1, 2.9, 4.0), (right.Start, right.TrimIn, right.TrimOut));
        Assert.Equal(2.2, right.TimelineEnd, 9);
        Assert.Equal(left.ScaleX.Value * 1.1, right.ScaleX.Value, 9);
        Assert.Equal(1.7, project.FindLayer("outro")!.Start, 9);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void ManyCutsKeepChronologicalIdsAndSourceContinuity()
    {
        var project = WithClip(seconds: 10, duration: 5);
        var clip = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "talk", Start = 0.5, TrimIn = 1, TrimOut = 9, Speed = 2 });
        Assert.Equal(4.5, clip.TimelineEnd, 9);

        var silences = new[] { new TimeRange(0, 1.5), new TimeRange(3, 4), new TimeRange(5, 5.5), new TimeRange(8.5, 10) };
        var plan = SilenceCutter.Plan(project, clip, silences, padding: 0.1);

        Assert.Equal(4, plan.Cuts.Count);
        Assert.Equal(new PlannedCut(1, 1.4, 0.5, 0.7), plan.Cuts[0]);
        Assert.Equal(new PlannedCut(3.1, 3.9, 1.55, 1.95), plan.Cuts[1]);
        Assert.Equal(new PlannedCut(5.1, 5.4, 2.55, 2.7), plan.Cuts[2]);
        Assert.Equal(new PlannedCut(8.6, 9, 4.3, 4.5), plan.Cuts[3]);

        var kept = new[] { (1.4, 3.1), (3.9, 5.1), (5.4, 8.6) };
        var result = SilenceCutter.Apply(project, plan);

        Assert.Equal(["talk", "talk_2", "talk_3"], result.Pieces);
        var pieces = result.Pieces.Select(id => (VideoLayer)project.FindLayer(id)!).ToList();
        Assert.Equal(kept, pieces.Select(p => (Math.Round(p.TrimIn, 9), Math.Round(p.TrimOut, 9))));
        Assert.Equal(0.5, pieces[0].Start, 9);
        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1].TimelineEnd, pieces[i].Start, 9);
        }

        Assert.Equal(5 - plan.Removed, project.Duration, 9);
        Assert.Equal(0.95, plan.Removed, 9);
    }

    [Fact]
    public void PlanRejectsCuttingEverything()
    {
        var project = WithClip();
        var clip = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip" });

        Assert.Contains("that would remove all of 'clip'",
            Assert.Throws<ZeyoCutException>(() => SilenceCutter.Plan(project, clip, [new TimeRange(0, 4)], 0.1)).Message);
        Assert.Empty(SilenceCutter.Plan(project, clip, [new TimeRange(1, 1.15)], 0.1).Cuts);
    }

    [Fact]
    public void SilenceAtTheClipEdgesIsCutWithoutPadding()
    {
        var project = WithClip(seconds: 15.933333333333334, duration: 16);
        var clip = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip" });

        var plan = SilenceCutter.Plan(project, clip, [new TimeRange(0.0001, 0.8), new TimeRange(5, 6), new TimeRange(14.7014, 15.9333)], 0.1);

        Assert.Equal([(0.0, 0.7), (5.1, 5.9), (14.8014, 15.933333)], plan.Cuts.Select(c => (Math.Round(c.SourceStart, 6), Math.Round(c.SourceEnd, 6))));
        Assert.Equal(clip.TrimOut, plan.Cuts[^1].SourceEnd);
        var result = SilenceCutter.Apply(project, plan);
        Assert.Equal(["clip", "clip_2"], result.Pieces);
        Assert.Equal(0.7, ((VideoLayer)project.FindLayer("clip")!).TrimIn, 9);
        Assert.Equal(14.8014, ((VideoLayer)project.FindLayer("clip_2")!).TrimOut, 9);
    }

    [Fact]
    public void TranscriptGapsAndWordRanges()
    {
        var words = Sentence();
        var gaps = SilenceCutter.TranscriptGaps(words, new TimeRange(0, 4), 0.7);
        Assert.Equal([new TimeRange(0.89, 1.86), new TimeRange(2.49, 4)], gaps);
        Assert.Equal([new TimeRange(1, 1.86), new TimeRange(2.49, 3)], SilenceCutter.TranscriptGaps(words, new TimeRange(1, 3), 0.5));

        var transcript = new Transcript { Asset = "clip", Words = words };
        Assert.Equal([new TimeRange(0.5, 1.86), new TimeRange(2.28, 2.49)], SilenceCutter.WordRanges(transcript, [(1, 1), (5, 5)]));
        Assert.Contains("word range 3–9 is not valid; the transcript has words 0–5",
            Assert.Throws<ZeyoCutException>(() => SilenceCutter.WordRanges(transcript, [(3, 9)])).Message);

        var project = WithClip();
        var clip = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip" });
        var plan = SilenceCutter.Plan(project, clip, SilenceCutter.WordRanges(transcript, [(1, 1)]), 0);
        SilenceCutter.Apply(project, plan);
        Assert.Equal(4 - 1.36, project.Duration, 9);
        Assert.Equal(1.86, ((VideoLayer)project.FindLayer("clip_2")!).TrimIn, 9);
    }

    [Fact]
    public void WordCutsTakeQuietSliversAtTheClipEdges()
    {
        var transcript = new Transcript { Asset = "clip", Words = Sentence() };
        var piece = new TimeRange(0.05, 2.6);
        var ranges = SilenceCutter.WordRanges(transcript, [(0, 0), (2, 2), (5, 5)]);
        Assert.Equal([new TimeRange(0.15, 0.5), new TimeRange(1.86, 2.09), new TimeRange(2.28, 2.49)], ranges);
        Assert.Equal([new TimeRange(0.05, 0.5), new TimeRange(1.86, 2.09), new TimeRange(2.28, 2.6)], SilenceCutter.SnapToClipEdges(transcript.Words, ranges, piece));

        List<TranscriptWord> late = [Word("Hi", 1, 1.3), Word("there.", 1.4, 1.8)];
        var intro = SilenceCutter.WordRanges(new Transcript { Words = late }, [(0, 0), (1, 1)]);
        Assert.Equal(intro, SilenceCutter.SnapToClipEdges(late, intro, new TimeRange(0, 3)));

        var project = WithClip();
        var clip = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", TrimIn = 0.05, TrimOut = 2.6 });
        var plan = SilenceCutter.Plan(project, clip, SilenceCutter.SnapToClipEdges(transcript.Words, ranges.Take(1), piece), 0);
        Assert.Equal([new PlannedCut(0.05, 0.5, 0, 0.45)], plan.Cuts);
    }

    [Fact]
    public void AudioLayersCanBeCutToo()
    {
        var project = TestProjects.Empty(duration: 6);
        project.Assets.Add(new Asset { Id = "voice", Type = AssetType.Audio, File = "assets/voice.wav", Duration = 5 });
        var voice = ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Id = "voice", Asset = "voice", Start = 1 });
        Assert.Equal(new TimeRange(0, 5), SilenceCutter.SourceRange(project, voice));

        var result = SilenceCutter.Apply(project, SilenceCutter.Plan(project, voice, [new TimeRange(2, 3)], 0.1));

        Assert.Equal(5.2, project.Duration, 9);
        var second = (AudioLayer)project.FindLayer("voice_2")!;
        Assert.Equal((3.1, 2.9), (Math.Round(second.Start, 9), Math.Round(second.TrimIn, 9)));
        Assert.Equal(3.1, project.FindLayer("voice")!.End!.Value, 9);
        Assert.Equal(["voice", "voice_2"], result.Pieces);
    }

    [Fact]
    public void TranscriptLinesAndSubtitles()
    {
        var lines = AnalysisFiles.Lines(Sentence());
        Assert.Equal(["Hello world.", "This is a test."], lines.Select(l => l.Text));
        Assert.Equal((2, 5), (lines[1].First, lines[1].Last));

        var longer = Enumerable.Range(0, 10).Select(i => Word($"w{i}", i * 0.3, i * 0.3 + 0.25)).ToList();
        Assert.Equal([7, 3], AnalysisFiles.Lines(longer, maxWords: 7).Select(l => l.Last - l.First + 1));

        var srt = AnalysisFiles.Srt(lines);
        Assert.Equal("1\r\n00:00:00,150 --> 00:00:00,890\r\nHello world.\r\n\r\n2\r\n00:00:01,860 --> 00:00:02,490\r\nThis is a test.\r\n\r\n", srt);
        Assert.Equal("01:02:03,457", AnalysisFiles.SrtTime(3723.4567));
    }
}
