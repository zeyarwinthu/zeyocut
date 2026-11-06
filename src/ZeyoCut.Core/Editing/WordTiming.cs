// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// whisper spreads a segment's timing error over its words, so words next to a long pause can land in
// it. This fits the words to the speech between the measured silences: a word that is mostly in a
// pause joins the speech before it, or the speech after it when the previous word ends a clause or
// the word reaches into that speech. Words that stick out of their stretch of speech are moved in,
// keeping their length and pushing their neighbours along until a gap takes up the move; when the
// speech has no room for that, the words are squeezed into it instead.
public static class WordTiming
{
    private const double MinSpeech = 0.05;

    // Returns how many words got new times.
    public static int FitToSpeech(IReadOnlyList<TranscriptWord> words, IReadOnlyList<TimeRange> silences, double duration)
    {
        if (words.Count == 0 || silences.Count == 0)
        {
            return 0;
        }

        var speech = Speech(silences, Math.Max(duration, words.Max(w => w.End)));
        if (speech.Count == 0)
        {
            return 0;
        }

        var original = words.Select(w => (w.Start, w.End)).ToList();
        var groups = speech.Select(_ => new List<int>()).ToList();
        var region = 0;
        for (var i = 0; i < words.Count; i++)
        {
            region = Math.Max(region, Region(words, i, speech, region));
            groups[region].Add(i);
        }

        for (var r = 0; r < speech.Count; r++)
        {
            var group = groups[r].Select(i => words[i]).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            if (!Push(group, speech[r]))
            {
                Squeeze(group, groups[r].Select(i => original[i]).ToList(), speech[r]);
            }

            foreach (var word in group)
            {
                var start = Math.Clamp(word.Start, speech[r].Start, speech[r].End);
                word.End = Math.Round(Math.Clamp(word.End, start, speech[r].End), 3);
                word.Start = Math.Round(start, 3);
            }
        }

        return words.Where((w, i) => Math.Abs(w.Start - original[i].Start) > 0.0005 || Math.Abs(w.End - original[i].End) > 0.0005).Count();
    }

    private static List<TimeRange> Speech(IReadOnlyList<TimeRange> silences, double duration)
    {
        var speech = new List<TimeRange>();
        var cursor = 0.0;
        foreach (var silence in silences.OrderBy(s => s.Start))
        {
            if (silence.Start - cursor >= MinSpeech)
            {
                speech.Add(new TimeRange(cursor, silence.Start));
            }

            cursor = Math.Max(cursor, silence.End);
        }

        if (duration - cursor >= MinSpeech)
        {
            speech.Add(new TimeRange(cursor, duration));
        }

        return speech;
    }

    private static int Region(IReadOnlyList<TranscriptWord> words, int index, List<TimeRange> speech, int previous)
    {
        var word = words[index];
        var length = word.End - word.Start;
        if (length <= 0)
        {
            var containing = speech.FindIndex(s => s.Start <= word.Start && word.Start <= s.End);
            if (containing >= 0)
            {
                return containing;
            }
        }
        else
        {
            var best = -1;
            var bestOverlap = 0.0;
            var spoken = 0.0;
            for (var r = 0; r < speech.Count; r++)
            {
                var overlap = Math.Min(word.End, speech[r].End) - Math.Max(word.Start, speech[r].Start);
                if (overlap > 0)
                {
                    spoken += overlap;
                    if (overlap > bestOverlap)
                    {
                        (best, bestOverlap) = (r, overlap);
                    }
                }
            }

            if (spoken * 2 >= length)
            {
                return best;
            }
        }

        var middle = (word.Start + word.End) / 2;
        var after = speech.FindIndex(s => s.Start >= middle);
        var before = after < 0 ? speech.Count - 1 : after - 1;
        if (after < 0 || before < 0)
        {
            return Math.Max(after, before);
        }

        var joinsBefore = index > 0 && previous == before && !EndsClause(words[index - 1].Text) && word.End <= speech[after].Start;
        return joinsBefore ? before : after;
    }

    private static bool Push(List<TranscriptWord> group, TimeRange speech)
    {
        var cursor = speech.Start;
        for (var i = 0; i < group.Count && group[i].Start < cursor; i++)
        {
            var length = group[i].End - group[i].Start;
            (group[i].Start, group[i].End) = (cursor, cursor + length);
            cursor += length;
        }

        cursor = speech.End;
        for (var i = group.Count - 1; i >= 0 && group[i].End > cursor; i--)
        {
            var length = group[i].End - group[i].Start;
            (group[i].Start, group[i].End) = (cursor - length, cursor);
            cursor -= length;
        }

        return group[0].Start >= speech.Start - 1e-9 && group[^1].End <= speech.End + 1e-9;
    }

    // Maps the words' original span onto the part of it that is inside the speech.
    private static void Squeeze(List<TranscriptWord> group, List<(double Start, double End)> original, TimeRange speech)
    {
        var (a, b) = (original[0].Start, original[^1].End);
        var (to0, to1) = (Math.Max(a, speech.Start), Math.Min(b, speech.End));
        double At(double t) => b - a > 1e-9 ? to0 + (t - a) * (to1 - to0) / (b - a) : to0;
        for (var i = 0; i < group.Count; i++)
        {
            (group[i].Start, group[i].End) = (At(original[i].Start), At(original[i].End));
        }
    }

    private static bool EndsClause(string word)
    {
        var text = word.TrimEnd('"', '\'', ')', '”', '’');
        return AnalysisFiles.EndsSentence(word) || (text.Length > 0 && ",;:…—–-".Contains(text[^1]));
    }
}
