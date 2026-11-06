// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// A detector's box on one frame, in the picture's pixels, with its score (0-1).
public sealed record Detection(double X, double Y, double Width, double Height, double Score);

// A linked track: the key Add gave its detections, and its frames from the first detection to the
// last; frames between them that no detection matched are lost and hold the box before them.
public sealed record LinkedTrack(int Key, List<TrackedFrame> Frames);

// detect_and_track's linking, in the manner of ByteTrack: each frame's confident detections are matched
// to the tracks first, by overlap with where each track is heading, then weaker ones only keep going
// the tracks found on the frame before. A track that was missed may also take a confident detection
// near where it was last seen, at a similar size: a face coming out from behind something is often
// found in part first, off the way it was heading. Going behind it, the object is found in part too,
// in ever smaller boxes, so the detection may also be near its largest box of the last 'buffer'
// frames (a plate, wide and low, shrinks to a sliver). A confident detection no track takes starts a
// tentative track, which goes on only if a confident detection matches it on the next frame, and a
// track ends after 'buffer' frames without a match. Only boxes are compared, so nobody is recognized:
// someone who is gone for longer than that gets a new track.
public sealed class DetectionLinker
{
    // A confident detection matches a track when it overlaps where the track is heading by at least
    // this (intersection over union); a weak one needs this much more.
    public const double MatchOverlap = 0.2;
    public const double WeakOverlap = 0.5;

    // Near: the centers at most this many of the track's box sides apart, the sizes at most this ratio.
    public const double NearSides = 1;
    public const double NearSize = 2;

    // Tracks found on fewer frames are left out as brief detections.
    public const int MinDetections = 3;

    // How much of a new motion goes into a track's speed.
    private const double Smoothing = 0.5;

    private readonly double _confident;
    private readonly double _weak;
    private readonly int _buffer;
    private readonly List<Live> _live = [];
    private readonly List<Live> _ended = [];
    private readonly List<int> _brief = [];
    private int _nextKey;
    private int? _lastFrame;

    public DetectionLinker(double confident, double weak, int buffer)
    {
        _confident = confident;
        _weak = Math.Min(weak, confident);
        _buffer = Math.Max(1, buffer);
    }

    // Links one frame's detections (frames in increasing order) and returns each one's track key, or -1
    // for a weak detection no track took.
    public int[] Add(int frame, IReadOnlyList<Detection> detections)
    {
        if (_lastFrame is { } last && frame <= last)
        {
            throw new ZeyoCutException($"frames must be linked in order (frame {frame} after {last})");
        }

        _lastFrame = frame;
        foreach (var gone in _live.Where(t => frame - t.LastFrame > _buffer).ToList())
        {
            _live.Remove(gone);
            _ended.Add(gone);
        }

        var keys = Enumerable.Repeat(-1, detections.Count).ToArray();
        var confident = Enumerable.Range(0, detections.Count).Where(i => detections[i].Score >= _confident).ToList();
        var weak = Enumerable.Range(0, detections.Count).Where(i => detections[i].Score >= _weak && detections[i].Score < _confident).ToList();
        var started = _live.Where(t => t.Confirmed).ToList();
        Match(started, confident, detections, frame, MatchOverlap, _buffer, keys);
        Match(started.Where(t => t.LastFrame == frame - 1).ToList(), weak, detections, frame, WeakOverlap, _buffer, keys);
        var tentative = _live.Where(t => !t.Confirmed).ToList();
        Match(tentative, confident, detections, frame, MatchOverlap, _buffer, keys);
        foreach (var track in tentative)
        {
            if (track.LastFrame == frame)
            {
                track.Confirmed = true;
            }
            else
            {
                _live.Remove(track);
                _brief.Add(track.Hits[0].Frame);
            }
        }

        foreach (var index in confident.Where(i => keys[i] < 0))
        {
            var track = new Live(_nextKey++);
            track.Hit(frame, detections[index]);
            _live.Add(track);
            keys[index] = track.Key;
        }

        return keys;
    }

    // The tracks found on at least MinDetections frames, by first frame (then from the left), and the
    // first frames of the brief detections left out (tentative ones included).
    public (List<LinkedTrack> Tracks, List<int> Brief) Finish()
    {
        var all = _ended.Concat(_live).OrderBy(t => t.Hits[0].Frame).ThenBy(t => t.Hits[0].Box.X).ToList();
        var kept = all.Where(t => t.Confirmed && t.Hits.Count >= MinDetections).ToList();
        var tracks = kept.Select(t => new LinkedTrack(t.Key, Frames(t))).ToList();
        var brief = all.Except(kept).Select(t => t.Hits[0].Frame).Concat(_brief).Order().ToList();
        return (tracks, brief);
    }

    public static double Overlap(double x1, double y1, double w1, double h1, double x2, double y2, double w2, double h2)
    {
        var width = Math.Min(x1 + w1, x2 + w2) - Math.Max(x1, x2);
        var height = Math.Min(y1 + h1, y2 + h2) - Math.Max(y1, y2);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var shared = width * height;
        return shared / (w1 * h1 + w2 * h2 - shared);
    }

    // Matches the closest pairs first, overlapping ones before near ones: each track and each detection
    // at most once.
    private static void Match(List<Live> tracks, List<int> candidates, IReadOnlyList<Detection> detections, int frame, double overlap, int recent, int[] keys)
    {
        var pairs = new List<(double Rank, Live Track, int Index)>();
        foreach (var track in tracks)
        {
            var (x, y, width, height) = track.Heading(frame);
            var missed = frame - track.LastFrame > 1 && overlap == MatchOverlap;
            foreach (var index in candidates.Where(i => keys[i] < 0))
            {
                var d = detections[index];
                var shared = Overlap(x, y, width, height, d.X, d.Y, d.Width, d.Height);
                if (shared >= overlap)
                {
                    pairs.Add((shared, track, index));
                }
                else if (missed && new[] { Near(track.Hits[^1].Box, d), Near(track.Largest(recent), d) }.Min() is { } apart)
                {
                    pairs.Add((-apart, track, index));
                }
            }
        }

        foreach (var (_, track, index) in pairs.OrderByDescending(p => p.Rank))
        {
            if (keys[index] >= 0 || track.LastFrame == frame)
            {
                continue;
            }

            track.Hit(frame, detections[index]);
            keys[index] = track.Key;
        }
    }

    // How many box sides apart the centers are, when the detection is near the box at a similar size.
    private static double? Near(Detection box, Detection d)
    {
        var side = Math.Max(box.Width, box.Height);
        var ratio = Math.Max(d.Width, d.Height) / side;
        var apart = Math.Sqrt(Math.Pow(d.X + d.Width / 2 - (box.X + box.Width / 2), 2) + Math.Pow(d.Y + d.Height / 2 - (box.Y + box.Height / 2), 2)) / side;
        return apart <= NearSides && ratio <= NearSize && ratio >= 1 / NearSize ? apart : null;
    }

    private static List<TrackedFrame> Frames(Live track)
    {
        var frames = new List<TrackedFrame>();
        var hits = track.Hits;
        for (var i = 0; i < hits.Count; i++)
        {
            var (frame, box) = hits[i];
            if (i > 0)
            {
                var held = hits[i - 1].Box;
                for (var lost = hits[i - 1].Frame + 1; lost < frame; lost++)
                {
                    frames.Add(new TrackedFrame { Frame = lost, X = held.X, Y = held.Y, Width = held.Width, Height = held.Height, Confidence = 0, Lost = true });
                }
            }

            frames.Add(new TrackedFrame { Frame = frame, X = box.X, Y = box.Y, Width = box.Width, Height = box.Height, Confidence = box.Score });
        }

        return frames;
    }

    private sealed class Live(int key)
    {
        private double _speedX;
        private double _speedY;

        public int Key { get; } = key;

        public bool Confirmed { get; set; }

        public List<(int Frame, Detection Box)> Hits { get; } = [];

        public int LastFrame => Hits[^1].Frame;

        // The largest box of the hits from the last 'frames' frames up to the last hit.
        public Detection Largest(int frames)
        {
            var largest = Hits[^1].Box;
            for (var i = Hits.Count - 2; i >= 0 && Hits[i].Frame >= LastFrame - frames; i--)
            {
                var box = Hits[i].Box;
                if (box.Width * box.Height > largest.Width * largest.Height)
                {
                    largest = box;
                }
            }

            return largest;
        }

        // Where the box should be on 'frame': moved on at the track's speed, at its last size.
        public (double X, double Y, double Width, double Height) Heading(int frame)
        {
            var last = Hits[^1].Box;
            var frames = frame - LastFrame;
            return (last.X + _speedX * frames, last.Y + _speedY * frames, last.Width, last.Height);
        }

        public void Hit(int frame, Detection box)
        {
            if (Hits.Count > 0)
            {
                var last = Hits[^1].Box;
                var frames = frame - LastFrame;
                var speedX = (box.X + box.Width / 2 - (last.X + last.Width / 2)) / frames;
                var speedY = (box.Y + box.Height / 2 - (last.Y + last.Height / 2)) / frames;
                (_speedX, _speedY) = Hits.Count == 1
                    ? (speedX, speedY)
                    : (Smoothing * speedX + (1 - Smoothing) * _speedX, Smoothing * speedY + (1 - Smoothing) * _speedY);
            }

            Hits.Add((frame, box));
        }
    }
}
