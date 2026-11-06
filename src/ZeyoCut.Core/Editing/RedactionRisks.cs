// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// What redaction_report asks to look at: frames a track spans that no box covers, boxes the tracker or
// model wasn't sure of, long stretches held between detections, fast motion, small boxes, and footage
// that plays before a track starts or after its box is let go while the box is away from the frame's
// edge, where the object may still show.
public enum RiskKind
{
    Uncovered,
    LowConfidence,
    LongHold,
    FastMotion,
    SmallBox,
    BeforeStart,
    AfterEnd
}

// Frames First-Last of the footage. Value: Uncovered and LongHold the frames, LowConfidence the lowest
// confidence, FastMotion the most box sizes moved a frame, SmallBox the smallest box's long side, and
// BeforeStart and AfterEnd how far the first or last box is from the frame's nearest edge, in pixels.
public sealed record RiskSpan(RiskKind Kind, int First, int Last, double Value);

// A track's redaction: Layer is the redaction that hides it (null: none), Keyed when its box comes from
// tracks_to_keyframes. The counts are frames of the footage the box covers, with a detection, held
// between two detections and held before the first or after the last; and frames the track spans that
// no box covers. Found: the frames the tracker or model found the object on.
public sealed record TrackReport(
    Track Track,
    Asset Asset,
    RedactionLayer? Layer,
    bool Keyed,
    List<TrackedFrame> Found,
    int CoverFirst,
    int CoverLast,
    int Detected,
    int HeldBetween,
    int HeldAtEnds,
    int Uncovered,
    List<RiskSpan> Risks)
{
    public bool Hidden => Layer is { Visible: true };
}

public static class RedactionRisks
{
    public const double LowConfidence = TrackEditor.DriftBelow;
    public const double LongHoldSeconds = 0.5;
    public const double FastSizes = 0.25;
    public const double SmallBox = 12;

    // A box farther than this share of the frame's larger side from every edge is in view: a face or
    // plate leaving the picture is last found at its edge.
    public const double EdgeShare = 0.02;

    // Flagged frames this many apart or less are one span.
    public const int Join = TrackEditor.DriftJoin;

    // cuts: the frames where the footage's shots start (RedactionCover.Cuts).
    public static TrackReport Of(Project project, Track track, TrackData data, IReadOnlyList<int>? cuts = null)
    {
        var asset = project.FindAsset(track.Asset)
            ?? throw new ZeyoCutException($"track '{track.Id}': asset '{track.Asset}' not found");
        var frameCount = asset.FrameCount ?? 0;
        var width = asset.Width ?? data.Width;
        var height = asset.Height ?? data.Height;
        var found = data.Frames
            .Where(f => !f.Lost && f.Frame >= 0 && f.Frame < frameCount && f.Width > 0 && f.Height > 0 && double.IsFinite(f.X + f.Y + f.Width + f.Height))
            .OrderBy(f => f.Frame)
            .ToList();
        var layer = RedactionEditor.Of(project, track.Id);
        var keyed = layer?.Boxes is { } boxes && boxes.ContainsKey(track.Id);
        var covered = Covered(layer, track, data, asset, cuts);
        var detected = found.Select(f => f.Frame).ToHashSet();
        int counted = 0, between = 0, ends = 0;
        foreach (var frame in covered)
        {
            if (detected.Contains(frame))
            {
                counted++;
            }
            else if (found.Count > 0 && frame > found[0].Frame && frame < found[^1].Frame)
            {
                between++;
            }
            else
            {
                ends++;
            }
        }

        var risks = new List<RiskSpan>();
        var uncovered = layer is null ? [] : data.Frames.Select(f => f.Frame).Where(f => !covered.Contains(f)).ToList();
        risks.AddRange(Spans(uncovered, 0).Select(s => new RiskSpan(RiskKind.Uncovered, s.First, s.Last, s.Last - s.First + 1)));
        risks.AddRange(Spans(found.Where(f => f.Confidence < LowConfidence).ToList(), f => f.Confidence, Math.Min)
            .Select(s => new RiskSpan(RiskKind.LowConfidence, s.First, s.Last, s.Value)));

        var hold = Math.Max(2, (int)Math.Round(VideoTiming.Fps(asset) * LongHoldSeconds));
        var fast = new List<(int Frame, double Value)>();
        for (var i = 1; i < found.Count; i++)
        {
            var (a, b) = (found[i - 1], found[i]);
            var missed = b.Frame - a.Frame - 1;
            if (missed >= hold)
            {
                risks.Add(new RiskSpan(RiskKind.LongHold, a.Frame + 1, b.Frame - 1, missed));
            }

            var moved = Math.Sqrt(Math.Pow(b.X + b.Width / 2 - (a.X + a.Width / 2), 2) + Math.Pow(b.Y + b.Height / 2 - (a.Y + a.Height / 2), 2));
            var sizes = moved / (b.Frame - a.Frame) / Math.Max(Side(a), Side(b));
            if (sizes >= FastSizes)
            {
                fast.Add((a.Frame, sizes));
                fast.Add((b.Frame, sizes));
            }
        }

        risks.AddRange(Spans(fast, f => f.Frame, f => f.Value, Math.Max).Select(s => new RiskSpan(RiskKind.FastMotion, s.First, s.Last, s.Value)));
        risks.AddRange(Spans(found.Where(f => Side(f) < SmallBox).ToList(), Side, Math.Min)
            .Select(s => new RiskSpan(RiskKind.SmallBox, s.First, s.Last, s.Value)));

        var start = covered.Count > 0 ? covered.Min : found.Count > 0 ? found[0].Frame : 0;
        var end = covered.Count > 0 ? covered.Max : found.Count > 0 ? found[^1].Frame : -1;
        if (found.Count > 0)
        {
            var shown = Shown(project, asset);
            var before = shown.Where(s => s.First < start).Select(s => s.First).DefaultIfEmpty(start).Min();
            var after = shown.Where(s => s.Last > end).Select(s => s.Last).DefaultIfEmpty(end).Max();
            var margin = EdgeShare * Math.Max(width, height);
            if (before < start && Edge(found[0], width, height) is var first && first > margin)
            {
                risks.Add(new RiskSpan(RiskKind.BeforeStart, before, start - 1, first));
            }

            if (after > end && Edge(found[^1], width, height) is var last && last > margin)
            {
                risks.Add(new RiskSpan(RiskKind.AfterEnd, end + 1, after, last));
            }
        }

        var plays = Shown(project, asset);
        var played = risks.Where(r => plays.Any(s => s.First <= r.Last && s.Last >= r.First)).OrderBy(r => r.First).ThenBy(r => r.Kind).ToList();
        return new TrackReport(track, asset, layer, keyed, found, covered.Count > 0 ? covered.Min : -1, covered.Count > 0 ? covered.Max : -1,
            counted, between, ends, uncovered.Count, played);
    }

    // The frames of the footage each clip that plays it shows.
    public static List<(int First, int Last)> Shown(Project project, Asset asset) =>
        RedactionEditor.Clips(project, asset.Id).Select(c => TrackEditor.Frames(c, asset)).Where(s => s.First <= s.Last).ToList();

    // The long side of a box.
    public static double Side(TrackedFrame frame) => Math.Max(frame.Width, frame.Height);

    // The frames a redaction's box covers: the fail-safe box's frames, or the keyframed box's.
    private static SortedSet<int> Covered(RedactionLayer? layer, Track track, TrackData data, Asset asset, IReadOnlyList<int>? cuts)
    {
        var covered = new SortedSet<int>();
        if (layer is null)
        {
            return covered;
        }

        if (layer.Boxes is not null && layer.Boxes.TryGetValue(track.Id, out var keyed))
        {
            for (var frame = 0; frame < (asset.FrameCount ?? 0); frame++)
            {
                if (RedactionEditor.KeyedBox(keyed, asset, frame) is not null)
                {
                    covered.Add(frame);
                }
            }

            return covered;
        }

        var cover = RedactionCover.Of(data.Frames, asset.FrameCount ?? 0, layer.PaddingPercent, layer.HoldFrames, cuts);
        foreach (var box in cover.Boxes)
        {
            covered.Add(box.Frame);
        }

        return covered;
    }

    // How far a box is from the nearest edge of the frame, in pixels.
    private static double Edge(TrackedFrame frame, int width, int height) =>
        Math.Max(0, Math.Min(Math.Min(frame.X, frame.Y), Math.Min(width - frame.X - frame.Width, height - frame.Y - frame.Height)));

    private static List<(int First, int Last)> Spans(List<int> frames, int join) =>
        Spans(frames.Select(f => (Frame: f, Value: 0.0)).ToList(), f => f.Frame, f => f.Value, Math.Max, join).Select(s => (s.First, s.Last)).ToList();

    private static List<(int First, int Last, double Value)> Spans(List<TrackedFrame> frames, Func<TrackedFrame, double> value, Func<double, double, double> keep) =>
        Spans(frames, f => f.Frame, value, keep);

    // Runs of frames at most 'join' frames apart, each with the value 'keep' picks from its frames.
    private static List<(int First, int Last, double Value)> Spans<T>(List<T> items, Func<T, int> frame, Func<T, double> value, Func<double, double, double> keep, int join = Join)
    {
        var spans = new List<(int First, int Last, double Value)>();
        foreach (var item in items.OrderBy(frame))
        {
            var at = frame(item);
            if (spans.Count > 0 && at - spans[^1].Last <= join + 1)
            {
                spans[^1] = (spans[^1].First, Math.Max(spans[^1].Last, at), keep(spans[^1].Value, value(item)));
            }
            else
            {
                spans.Add((at, at, value(item)));
            }
        }

        return spans;
    }
}
