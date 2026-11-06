// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// Where a frame's redaction box comes from: the track's box on that frame (detected), filled in
// between two detections (gap), or held before the first detection or after the last (held).
public enum CoverKind
{
    Detected,
    Gap,
    Held
}

// A frame's redaction box in the footage's own pixels.
public readonly record struct CoverBox(int Frame, double X, double Y, double Width, double Height, CoverKind Kind);

// The fail-safe: a track's redaction box on every frame from holdFrames before its first detection to
// holdFrames after its last (inside the footage, and not across a scene cut: a hold ends where the
// shot does). Frames the track lost count as missed. Between two detections the box moves straight
// from one to the other and grows with each frame away from the nearer one, so no frame between them
// is uncovered, even across a cut; past the ends it holds the nearest detection and grows the same way. Every box is padded by paddingPercent of its width and height, and its edges
// are smoothed over a few frames into an envelope that always contains the frame's own box.
public sealed class RedactionCover
{
    // Each missed frame away from the nearest detection grows every side by this share of the box's
    // mean side.
    public const double GrowPerFrame = 0.03;

    // The envelope spans this many frames on each side.
    public const int Smoothing = 2;

    // A hold that a scene cut ends still covers this many frames past the cut, in case the cut was
    // found a frame early or late.
    public const int CutMargin = 1;

    public static readonly RedactionCover Empty = new(0, []);

    private readonly CoverBox[] _boxes;

    private RedactionCover(int first, CoverBox[] boxes, int? cutBefore = null, int? cutAfter = null)
    {
        First = first;
        _boxes = boxes;
        CutBefore = cutBefore;
        CutAfter = cutAfter;
    }

    public int First { get; }

    public int Last => First + _boxes.Length - 1;

    // The scene cuts that ended the hold before the first detection and after the last (each the
    // first frame of a shot), or null where the hold ran its full length.
    public int? CutBefore { get; }

    public int? CutAfter { get; }

    public IReadOnlyList<CoverBox> Boxes => _boxes;

    public CoverBox? At(int frame) => frame >= First && frame - First < _boxes.Length ? _boxes[frame - First] : null;

    // cuts: the frames where a shot starts (Cuts); none means every hold runs its full length.
    public static RedactionCover Of(IReadOnlyList<TrackedFrame> frames, int frameCount, double paddingPercent, int holdFrames, IReadOnlyList<int>? cuts = null)
    {
        var found = frames
            .Where(f => !f.Lost && f.Frame >= 0 && f.Frame < frameCount && f.Width > 0 && f.Height > 0 && double.IsFinite(f.X + f.Y + f.Width + f.Height))
            .OrderBy(f => f.Frame)
            .ToList();
        if (found.Count == 0)
        {
            return Empty;
        }

        var first = Math.Max(0, found[0].Frame - holdFrames);
        var last = Math.Min(frameCount - 1, found[^1].Frame + holdFrames);
        int? cutBefore = null, cutAfter = null;
        foreach (var cut in cuts ?? [])
        {
            if (cut <= found[0].Frame && cut - CutMargin > first)
            {
                first = cut - CutMargin;
                cutBefore = cut;
            }
            else if (cut > found[^1].Frame && cut - 1 + CutMargin < last)
            {
                last = cut - 1 + CutMargin;
                cutAfter = cut;
            }
        }

        var pad = paddingPercent / 100;
        var raw = new CoverBox[last - first + 1];
        var next = 0;
        for (var frame = first; frame <= last; frame++)
        {
            while (next < found.Count && found[next].Frame < frame)
            {
                next++;
            }

            if (next < found.Count && found[next].Frame == frame)
            {
                raw[frame - first] = Padded(frame, Box(found[next]), pad, 0, CoverKind.Detected);
            }
            else if (next == 0)
            {
                raw[frame - first] = Padded(frame, Box(found[0]), pad, found[0].Frame - frame, CoverKind.Held);
            }
            else if (next == found.Count)
            {
                raw[frame - first] = Padded(frame, Box(found[^1]), pad, frame - found[^1].Frame, CoverKind.Held);
            }
            else
            {
                var a = found[next - 1];
                var b = found[next];
                var t = (frame - a.Frame) / (double)(b.Frame - a.Frame);
                var between = (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);
                raw[frame - first] = Padded(frame, between, pad, Math.Min(frame - a.Frame, b.Frame - frame), CoverKind.Gap);
            }
        }

        return new RedactionCover(first, Envelope(raw), cutBefore, cutAfter);
    }

    // The frames where a shot starts, from the scene cuts analyze_video found in the footage. None
    // when it hasn't run or its file can't be read, so every hold then runs its full length.
    public static IReadOnlyList<int> Cuts(string projectDirectory, Asset asset)
    {
        MediaAnalysis? analysis;
        try
        {
            analysis = AnalysisFiles.LoadAnalysis(projectDirectory, asset);
        }
        catch (Exception ex) when (ex is ZeyoCutException or IOException)
        {
            return [];
        }

        var fps = VideoTiming.Fps(asset);
        return analysis is null || !(fps > 0)
            ? []
            : analysis.Scenes.Select(s => (int)Math.Round(s.Time * fps)).Where(f => f > 0).Distinct().Order().ToList();
    }

    private static (double X, double Y, double Width, double Height) Box(TrackedFrame frame) => (frame.X, frame.Y, frame.Width, frame.Height);

    private static CoverBox Padded(int frame, (double X, double Y, double Width, double Height) box, double pad, int missed, CoverKind kind)
    {
        var grow = missed * GrowPerFrame * (box.Width + box.Height) / 2;
        var marginX = box.Width * pad + grow;
        var marginY = box.Height * pad + grow;
        return new CoverBox(frame, box.X - marginX, box.Y - marginY, box.Width + 2 * marginX, box.Height + 2 * marginY, kind);
    }

    // Each edge goes out to its farthest over the frames around it, then is averaged over them: every
    // value averaged reaches at least as far as the frame's own edge, so the box still contains it.
    private static CoverBox[] Envelope(CoverBox[] raw)
    {
        var count = raw.Length;
        var left = new double[count];
        var top = new double[count];
        var right = new double[count];
        var bottom = new double[count];
        for (var i = 0; i < count; i++)
        {
            left[i] = double.MaxValue;
            top[i] = double.MaxValue;
            right[i] = double.MinValue;
            bottom[i] = double.MinValue;
            for (var j = Math.Max(0, i - Smoothing); j <= Math.Min(count - 1, i + Smoothing); j++)
            {
                left[i] = Math.Min(left[i], raw[j].X);
                top[i] = Math.Min(top[i], raw[j].Y);
                right[i] = Math.Max(right[i], raw[j].X + raw[j].Width);
                bottom[i] = Math.Max(bottom[i], raw[j].Y + raw[j].Height);
            }
        }

        var boxes = new CoverBox[count];
        for (var i = 0; i < count; i++)
        {
            double l = 0, t = 0, r = 0, b = 0;
            var from = Math.Max(0, i - Smoothing);
            var to = Math.Min(count - 1, i + Smoothing);
            for (var j = from; j <= to; j++)
            {
                l += left[j];
                t += top[j];
                r += right[j];
                b += bottom[j];
            }

            var n = to - from + 1;
            var own = raw[i];
            var x = Math.Min(l / n, own.X);
            var y = Math.Min(t / n, own.Y);
            var x2 = Math.Max(r / n, own.X + own.Width);
            var y2 = Math.Max(b / n, own.Y + own.Height);
            boxes[i] = new CoverBox(own.Frame, x, y, x2 - x, y2 - y, own.Kind);
        }

        return boxes;
    }
}
