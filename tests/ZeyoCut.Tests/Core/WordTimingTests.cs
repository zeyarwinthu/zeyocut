// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Tests.Core;

public class WordTimingTests
{
    private readonly ITestOutputHelper _output;

    public WordTimingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static TranscriptWord Word(string text, double start, double end) => new() { Text = text, Start = start, End = end, Probability = 0.9 };

    private static void AssertOrdered(IReadOnlyList<TranscriptWord> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            Assert.True(words[i].Start <= words[i].End, $"word {i} ends before it starts");
            if (i > 0)
            {
                Assert.True(words[i - 1].End <= words[i].Start + 1e-9, $"word {i} starts before word {i - 1} ends");
            }
        }
    }

    // whisper-cli (ggml-small) on the smoke test's talk.mp4: three synthesized lines with 1.6s of
    // silence after the first two, and the silences analyze_video measured on it.
    private static List<TranscriptWord> SmokeWords() =>
    [
        Word("Welcome", 0.12, 0.51), Word("to", 0.51, 0.65), Word("Zia-Cut,", 0.65, 1.14), Word("the", 1.3, 1.3),
        Word("video", 1.7, 1.86), Word("editor", 1.86, 2.3), Word("you", 2.3, 2.51), Word("drive", 2.52, 2.88),
        Word("from", 2.88, 3.16), Word("a", 3.17, 3.24), Word("chat.", 3.24, 3.59),
        Word("Um,", 6, 6.18), Word("so,", 6.48, 6.78), Word("let", 7.53, 7.53), Word("me", 7.58, 7.83),
        Word("show", 7.83, 8.43), Word("you", 8.43, 8.87), Word("how", 8.96, 9.33), Word("cutting", 9.33, 9.5),
        Word("works.", 10.39, 11.14),
        Word("Long", 11.76, 11.9), Word("pauses", 11.9, 12.29), Word("and", 12.29, 12.48), Word("filler", 12.48, 12.81),
        Word("words", 13.2, 13.2), Word("come", 13.34, 13.45), Word("out", 13.47, 13.65), Word("automatically.", 13.65, 14.56),
    ];

    private static readonly TimeRange[] SmokeSilences = [new(3.708, 6.16), new(6.549, 6.875), new(9.458, 11.904), new(14.701, 15.933)];

    // Where the speech synthesizer said each word starts (its positions count 16 kHz samples in a
    // 22.05 kHz file), plus each line's place in the clip.
    private static double[] SmokeOnsets()
    {
        double[][] lines =
        [
            [0.1370625, 0.71575, 0.9155, 2.3141875, 2.4519375, 2.9686875, 3.444, 3.6713125, 4.1535625, 4.4359375, 4.5255],
            [0.1370625, 1.1499375, 2.1559375, 2.459, 2.65875, 3.0376875, 3.258125, 3.49925, 3.9815],
            [0.1370625, 0.522875, 1.1635625, 1.4115625, 1.86625, 2.293375, 2.6171875, 2.885875],
        ];
        double[] offsets = [0, 4.45873 + 1.6, 4.45873 + 1.6 + 4.143946 + 1.6];
        return lines.SelectMany((line, i) => line.Select(p => offsets[i] + p * 16000 / 22050 + 0.012)).ToArray();
    }

    [Fact]
    public void WhisperWordsInPausesMoveBackOntoTheSpeech()
    {
        var words = SmokeWords();
        var onsets = SmokeOnsets();
        var before = words.Select((w, i) => Math.Abs(w.Start - onsets[i])).ToArray();

        var retimed = WordTiming.FitToSpeech(words, SmokeSilences, 15.933);

        var after = words.Select((w, i) => Math.Abs(w.Start - onsets[i])).ToArray();
        for (var i = 0; i < words.Count; i++)
        {
            _output.WriteLine($"{i,2} {words[i].Text,-15} {words[i].Start,7:0.000}–{words[i].End,7:0.000}  true {onsets[i],7:0.000}  error {before[i]:0.000} → {after[i]:0.000}");
        }

        Assert.Equal(13, retimed);
        Assert.Equal(SmokeWords().Take(11).Select(w => (w.Start, w.End)), words.Take(11).Select(w => (w.Start, w.End)));
        Assert.Equal(6.16, words[11].Start);
        Assert.Equal(6.875, words[12].Start);
        Assert.Equal(9.458, words[19].End);
        Assert.Equal(11.904, words[20].Start);
        Assert.Equal((13.65, 14.56), (words[27].Start, words[27].End));
        AssertOrdered(words);
        foreach (var word in words.Where(w => w.End > w.Start))
        {
            var quiet = SmokeSilences.Sum(s => Math.Max(0, Math.Min(word.End, s.End) - Math.Max(word.Start, s.Start)));
            Assert.True(quiet == 0, $"'{word.Text}' still overlaps a silence by {quiet:0.000}s");
        }

        var moved = Enumerable.Range(11, 17).ToArray();
        Assert.True(moved.Average(i => after[i]) < moved.Average(i => before[i]) * 0.6, "the moved words should be much closer to the real speech");
        Assert.True(before[19] > 1.4 && after[19] < 0.1, "'works.' should come back from the middle of the pause");
        Assert.True(moved.Max(i => after[i]) < 0.4);
    }

    [Fact]
    public void ACommaBeforeAPauseSendsTheNextWordAfterIt()
    {
        List<TranscriptWord> words = [Word("Well,", 1, 1.4), Word("maybe", 1.5, 1.9), Word("not.", 3.2, 3.6)];

        WordTiming.FitToSpeech(words, [new TimeRange(1.6, 3)], 5);

        Assert.Equal([(1.0, 1.4), (3.0, 3.4), (3.4, 3.8)], words.Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void AWordInAPauseMidSentenceJoinsTheSpeechBeforeUnlessItReachesTheNext()
    {
        List<TranscriptWord> trailing = [Word("I", 1, 1.2), Word("think", 1.3, 1.8), Word("so.", 2.2, 2.6), Word("Yes.", 3.2, 3.5)];
        WordTiming.FitToSpeech(trailing, [new TimeRange(2, 3)], 5);
        Assert.Equal([(0.9, 1.1), (1.1, 1.6), (1.6, 2.0), (3.2, 3.5)], trailing.Select(w => (w.Start, w.End)));

        List<TranscriptWord> leading = [Word("I", 1, 1.2), Word("think", 1.3, 1.8), Word("that", 2.85, 3.1), Word("works.", 3.1, 3.5)];
        WordTiming.FitToSpeech(leading, [new TimeRange(2, 3)], 5);
        Assert.Equal([(1.0, 1.2), (1.3, 1.8), (3.0, 3.25), (3.25, 3.65)], leading.Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void AnOverrunPushesWordsBackOnlyUntilAGapTakesItUp()
    {
        List<TranscriptWord> words =
        [
            Word("One", 1, 1.3), Word("two.", 1.3, 1.6),
            Word("Three", 3, 3.4), Word("four", 3.4, 3.8), Word("five.", 4.2, 5.6),
            Word("Six.", 8.2, 8.5),
        ];

        var retimed = WordTiming.FitToSpeech(words, [new TimeRange(5, 8)], 10);

        Assert.Equal(3, retimed);
        Assert.Equal([(1.0, 1.3), (1.3, 1.6), (2.8, 3.2), (3.2, 3.6), (3.6, 5.0), (8.2, 8.5)], words.Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void PausesAtTheClipEdgesSendWordsToTheOnlySpeechBesideThem()
    {
        List<TranscriptWord> words = [Word("Hi", 0.5, 0.9), Word("there", 1.2, 1.6), Word("friend", 4.3, 4.8)];

        WordTiming.FitToSpeech(words, [new TimeRange(0, 1), new TimeRange(4, 6)], 6);

        Assert.Equal(1.0, words[0].Start);
        Assert.Equal(4.0, words[2].End);
        Assert.All(words, w => Assert.InRange(w.Start, 1.0, 4.0));
        AssertOrdered(words);
    }

    [Fact]
    public void AWordPushedToTheEndOfSpeechBetweenMillisecondsKeepsItsOrder()
    {
        // The speech ends at 2.1318 s, which rounds up to 2.132; the empty last word lands there.
        List<TranscriptWord> words = [Word("Hello", 1, 1.5), Word("there.", 2.2, 2.2)];

        WordTiming.FitToSpeech(words, [new TimeRange(2.1318, 5)], 5);

        Assert.Equal([(1.0, 1.5), (2.132, 2.132)], words.Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void WordsAlreadyOnTheSpeechAreLeftAlone()
    {
        List<TranscriptWord> words = [Word("Hello", 0.15, 0.5), Word("world.", 0.5, 0.89), Word("Again.", 1.86, 2.4)];

        Assert.Equal(0, WordTiming.FitToSpeech(words, [new TimeRange(0.95, 1.8)], 3));
        Assert.Equal(0, WordTiming.FitToSpeech(words, [], 3));
        Assert.Equal([(0.15, 0.5), (0.5, 0.89), (1.86, 2.4)], words.Select(w => (w.Start, w.End)));
    }
}
