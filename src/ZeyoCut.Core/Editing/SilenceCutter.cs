// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record PlannedCut(double SourceStart, double SourceEnd, double Start, double End)
{
    public double Length => End - Start;
}

public sealed record CutPlan(string LayerId, IReadOnlyList<PlannedCut> Cuts, double Removed, double OldDuration, double NewDuration);

public sealed record CutResult(CutPlan Plan, IReadOnlyList<string> Pieces, double NewDuration, IReadOnlyList<string> Warnings);

// Turns silences or unwanted words (source seconds of a clip's asset) into ripple cuts on the clip.
public static class SilenceCutter
{
    public const double DefaultPunchIn = 1.1;

    private const double Sliver = 0.3;

    public static (Layer Layer, Asset Asset) MediaLayer(Project project, string layerId)
    {
        var layer = ProjectEditor.GetLayer(project, layerId);
        var assetId = layer switch
        {
            VideoLayer video => video.Asset,
            AudioLayer audio => audio.Asset,
            _ => throw new ZeyoCutException($"'{layerId}' is a {layer.TypeName} layer; this works on video and audio layers"),
        };
        var asset = project.FindAsset(assetId) ?? throw new ZeyoCutException($"layer '{layerId}' uses asset '{assetId}', which is missing");
        return (layer, asset);
    }

    // The part of the asset the layer plays, in source seconds.
    public static TimeRange SourceRange(Project project, Layer layer) => layer switch
    {
        VideoLayer video => new TimeRange(video.TrimIn, video.TrimOut),
        AudioLayer audio => new TimeRange(audio.TrimIn, audio.TrimIn + (TimelineEditor.ContentEnd(project, audio) - audio.Start)),
        _ => throw new ZeyoCutException($"'{layer.Id}' is a {layer.TypeName} layer; this works on video and audio layers"),
    };

    public static double ToTimeline(Layer layer, double source) => layer switch
    {
        VideoLayer video => VideoTiming.TimelineTime(video, source),
        AudioLayer audio => audio.Start + (source - audio.TrimIn),
        _ => layer.Start,
    };

    // Gaps between spoken words inside 'range' that last at least minSilence, including the gap
    // before the first word and after the last one.
    public static List<TimeRange> TranscriptGaps(IReadOnlyList<TranscriptWord> words, TimeRange range, double minSilence)
    {
        var gaps = new List<TimeRange>();
        var cursor = range.Start;
        foreach (var word in words.Where(w => w.End > range.Start && w.Start < range.End).OrderBy(w => w.Start))
        {
            if (word.Start - cursor >= minSilence)
            {
                gaps.Add(new TimeRange(cursor, word.Start));
            }

            cursor = Math.Max(cursor, word.End);
        }

        if (range.End - cursor >= minSilence)
        {
            gaps.Add(new TimeRange(cursor, range.End));
        }

        return gaps;
    }

    // Source ranges for word index ranges (inclusive): from the first word's start to the next
    // word's start, so the pause after the removed words goes too.
    public static List<TimeRange> WordRanges(Transcript transcript, IReadOnlyList<(int From, int To)> ranges)
    {
        var words = transcript.Words;
        if (ranges.Count == 0)
        {
            throw new ZeyoCutException("give at least one word range");
        }

        var result = new List<TimeRange>();
        foreach (var (from, to) in ranges)
        {
            if (from < 0 || to >= words.Count || from > to)
            {
                throw new ZeyoCutException($"word range {from}–{to} is not valid; the transcript has words 0–{words.Count - 1} and 'from' must be <= 'to'");
            }

            var end = to + 1 < words.Count ? words[to + 1].Start : words[to].End;
            result.Add(new TimeRange(words[from].Start, Math.Max(end, words[to].End)));
        }

        return result;
    }

    // A word cut that would leave a sliver without words at the clip's edge (such as the padding
    // remove_silences keeps) takes the sliver too.
    public static List<TimeRange> SnapToClipEdges(IReadOnlyList<TranscriptWord> words, IEnumerable<TimeRange> ranges, TimeRange clip) =>
        ranges.Select(r => new TimeRange(
            r.Start > clip.Start && r.Start - clip.Start < Sliver && !words.Any(w => w.End > clip.Start && w.Start < r.Start) ? clip.Start : r.Start,
            r.End < clip.End && clip.End - r.End < Sliver && !words.Any(w => w.Start < clip.End && w.End > r.End) ? clip.End : r.End)).ToList();

    // Cuts that remove each range minus 'padding' on the sides that touch kept sound.
    public static CutPlan Plan(Project project, Layer layer, IEnumerable<TimeRange> sourceRanges, double padding)
    {
        if (!double.IsFinite(padding) || padding < 0)
        {
            throw new ZeyoCutException($"padding must be >= 0 (got {Num(padding)})");
        }

        var range = SourceRange(project, layer);
        var frame = 1.0 / project.Fps;
        var edge = frame / 2;
        var cuts = new List<PlannedCut>();
        foreach (var silence in Merge(sourceRanges))
        {
            var start = Math.Max(silence.Start, range.Start);
            var end = Math.Min(silence.End, range.End);
            if (end <= start)
            {
                continue;
            }

            var cutStart = start <= range.Start + edge ? range.Start : start + padding;
            var cutEnd = end >= range.End - edge ? range.End : end - padding;
            var t0 = ToTimeline(layer, cutStart);
            var t1 = Math.Min(ToTimeline(layer, cutEnd), project.Duration);
            if (t1 - t0 < frame - 1e-9)
            {
                continue;
            }

            if (cutStart <= range.Start + 1e-6 && cutEnd >= range.End - 1e-6)
            {
                throw new ZeyoCutException($"that would remove all of '{layer.Id}' (source {Num(range.Start)}–{Num(range.End)}s); nothing in it counts as sound");
            }

            cuts.Add(new PlannedCut(cutStart, cutEnd, KeyframeWindow.Round(t0), KeyframeWindow.Round(t1)));
        }

        var removed = cuts.Sum(c => c.Length);
        return new CutPlan(layer.Id, cuts, removed, project.Duration, KeyframeWindow.Round(project.Duration - removed));
    }

    // Applies the cuts left to right with ripple. Pieces keep chronological ids (clip, clip_2, ...);
    // with punchIn, every second piece is zoomed by that factor to hide the jump cuts.
    public static CutResult Apply(Project project, CutPlan plan, double punchIn = 1)
    {
        if (!double.IsFinite(punchIn) || punchIn < 1 || punchIn > 2)
        {
            throw new ZeyoCutException($"punchIn zoom must be between 1 and 2 (got {Num(punchIn)})");
        }

        var pieces = new List<string> { plan.LayerId };
        var current = plan.LayerId;
        var offset = 0.0;
        var warnings = new List<string>();
        foreach (var cut in plan.Cuts.OrderBy(c => c.Start))
        {
            var result = TimelineEditor.RemoveRange(project, current, cut.Start - offset, cut.End - offset, ripple: true);
            offset += result.RemovedSeconds;
            var right = result.Pieces.FirstOrDefault(p => p.Value == current).Key;
            if (right is not null)
            {
                current = right;
                pieces.Add(right);
            }

            warnings = result.Warnings.ToList();
        }

        if (punchIn > 1)
        {
            for (var i = 1; i < pieces.Count; i += 2)
            {
                if (project.FindLayer(pieces[i]) is VisualLayer visual)
                {
                    Zoom(visual.ScaleX, punchIn);
                    Zoom(visual.ScaleY, punchIn);
                }
            }
        }

        return new CutResult(plan, pieces, project.Duration, warnings);
    }

    public static string Describe(CutPlan plan, int maxListed = 30)
    {
        var lines = new List<string>
        {
            $"{plan.Cuts.Count} cut(s), {Num(plan.Removed)}s removed; duration {Num(plan.OldDuration)}s → {Num(plan.NewDuration)}s",
        };
        foreach (var cut in plan.Cuts.Take(maxListed))
        {
            lines.Add($"- timeline {Num(cut.Start)}–{Num(cut.End)}s (source {Num(cut.SourceStart)}–{Num(cut.SourceEnd)}s, {Num(cut.Length)}s)");
        }

        if (plan.Cuts.Count > maxListed)
        {
            lines.Add($"- ... and {plan.Cuts.Count - maxListed} more");
        }

        return string.Join("\n", lines);
    }

    private static void Zoom(AnimatedValue value, double factor)
    {
        value.Value *= factor;
        foreach (var key in value.Keyframes)
        {
            key.Value *= factor;
        }
    }

    private static List<TimeRange> Merge(IEnumerable<TimeRange> ranges)
    {
        var merged = new List<TimeRange>();
        foreach (var range in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + 1e-9)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
