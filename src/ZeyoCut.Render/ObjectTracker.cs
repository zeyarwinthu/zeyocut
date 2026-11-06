// SPDX-License-Identifier: MIT

using System.Globalization;
using OpenCvSharp;
using OpenCvSharp.Tracking;
using ZeyoCut.Core;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Render;

// track_object with OpenCV (OpenCvSharp): follows a box from an anchor frame to another frame of a
// video asset, one frame at a time. csrt is OpenCV's CSRT tracker (follows changes of shape and
// size); template looks for the anchor's picture near the last box (fixed size); opticalFlow moves the
// box with the median motion of the points inside it (pyramidal Lucas-Kanade, checked backwards) and
// scales it with their spread. For every method, confidence is how well the middle of the anchor's
// picture matches inside the box (normalized correlation, allowing for a few pixels and 10% of size).
public static class ObjectTracker
{
    // Frames are tracked at the source size, but at most this size on the long side.
    public const int MaxSide = 1920;

    // Backward tracking decodes this many frames at a time and walks them in reverse.
    private const int BackwardChunk = 24;

    // The smallest box the trackers work with, in tracked pixels.
    private const double MinSide = 4;

    public static (int Width, int Height) TrackingSize(Asset asset)
    {
        var width = asset.Width ?? 0;
        var height = asset.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            throw new ZeyoCutException($"video asset '{asset.Id}' has no frame size");
        }

        var scale = Math.Min(1, MaxSide / (double)Math.Max(width, height));
        if (scale >= 1)
        {
            return (width, height);
        }

        return (Math.Max(2, (int)Math.Round(width * scale / 2) * 2), Math.Max(2, (int)Math.Round(height * scale / 2) * 2));
    }

    // The frames from anchor.Frame to 'to' (either way), in frame order; the anchor's own frame has
    // its box with confidence 1. Boxes are in source pixels. Ends early if the file has fewer frames.
    public static List<TrackedFrame> Track(string ffmpeg, string file, Asset asset, TrackBox anchor, TrackMethod method, int to, Action<double>? progress, CancellationToken cancellationToken)
    {
        var (width, height) = TrackingSize(asset);
        var scaleX = width / (double)asset.Width!.Value;
        var scaleY = height / (double)asset.Height!.Value;
        var box = new Rect2d(anchor.X * scaleX, anchor.Y * scaleY, anchor.Width * scaleX, anchor.Height * scaleY);
        if (box.Width < MinSide || box.Height < MinSide)
        {
            throw new ZeyoCutException($"the box is too small to track ({Num(anchor.Width)}x{Num(anchor.Height)} source pixels); draw it around the whole object");
        }

        var total = Math.Abs(to - anchor.Frame);
        var frames = new List<TrackedFrame>(total + 1);
        try
        {
            using var follower = Follower(method);
            Similarity? similarity = null;
            try
            {
                foreach (var (index, bgr, gray) in Frames(ffmpeg, file, asset, anchor.Frame, to, width, height, cancellationToken))
                {
                    using (bgr)
                    using (gray)
                    {
                        if (similarity is null)
                        {
                            follower.Start(bgr, gray, box);
                            similarity = new Similarity(gray, box);
                            frames.Add(new TrackedFrame { Frame = index, X = anchor.X, Y = anchor.Y, Width = anchor.Width, Height = anchor.Height, Confidence = 1 });
                            continue;
                        }

                        var (next, found) = follower.Next(bgr, gray);
                        var confidence = found ? similarity.Score(gray, next) : 0;
                        if (found)
                        {
                            box = next;
                        }

                        frames.Add(new TrackedFrame
                        {
                            Frame = index,
                            X = box.X / scaleX,
                            Y = box.Y / scaleY,
                            Width = box.Width / scaleX,
                            Height = box.Height / scaleY,
                            Confidence = confidence,
                            Lost = !found,
                        });
                        progress?.Invoke((frames.Count - 1) / (double)Math.Max(1, total));
                    }
                }
            }
            finally
            {
                similarity?.Dispose();
            }
        }
        catch (OpenCVException ex)
        {
            throw new ZeyoCutException($"OpenCV couldn't track the box: {ex.ErrMsg}");
        }

        if (frames.Count == 0)
        {
            throw new ZeyoCutException($"frame {anchor.Frame} of '{asset.Id}' couldn't be decoded");
        }

        return frames.OrderBy(f => f.Frame).ToList();
    }

    private static IFollower Follower(TrackMethod method) => method switch
    {
        TrackMethod.Csrt => new CsrtFollower(),
        TrackMethod.Template => new TemplateFollower(),
        _ => new FlowFollower(),
    };

    // Each frame as BGR and gray, from 'from' to 'to'; backwards in chunks read forwards.
    private static IEnumerable<(int Frame, Mat Bgr, Mat Gray)> Frames(string ffmpeg, string file, Asset asset, int from, int to, int width, int height, CancellationToken cancellationToken)
    {
        if (to >= from)
        {
            foreach (var frame in Read(ffmpeg, file, asset, from, to, width, height, cancellationToken))
            {
                yield return frame;
            }

            yield break;
        }

        for (var last = from; last >= to; last -= BackwardChunk)
        {
            var first = Math.Max(to, last - BackwardChunk + 1);
            var chunk = Read(ffmpeg, file, asset, first, last, width, height, cancellationToken).ToList();
            var next = chunk.Count - 1;
            try
            {
                for (; next >= 0; next--)
                {
                    var frame = chunk[next];
                    chunk.RemoveAt(next);
                    yield return frame;
                }
            }
            finally
            {
                foreach (var (_, bgr, gray) in chunk)
                {
                    bgr.Dispose();
                    gray.Dispose();
                }
            }
        }
    }

    private static IEnumerable<(int Frame, Mat Bgr, Mat Gray)> Read(string ffmpeg, string file, Asset asset, int first, int last, int width, int height, CancellationToken cancellationToken)
    {
        using var reader = new VideoFrameReader(ffmpeg, file, first, VideoDecoder.SeekSeconds(asset, first), width, height, last - first + 1);
        using var rgba = new Mat(height, width, MatType.CV_8UC4);
        while (reader.Advance())
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader.Current.CopyTo(rgba.AsSpan<byte>());
            var bgr = new Mat();
            var gray = new Mat();
            Cv2.CvtColor(rgba, bgr, ColorConversionCodes.RGBA2BGR);
            Cv2.CvtColor(rgba, gray, ColorConversionCodes.RGBA2GRAY);
            yield return (reader.CurrentFrame, bgr, gray);
        }

        reader.ThrowIfFailed();
    }

    private static Rect Whole(Rect2d box, Mat frame)
    {
        var left = Math.Clamp((int)Math.Round(box.X), 0, frame.Width - 1);
        var top = Math.Clamp((int)Math.Round(box.Y), 0, frame.Height - 1);
        var right = Math.Clamp((int)Math.Round(box.X + box.Width), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Round(box.Y + box.Height), top + 1, frame.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private interface IFollower : IDisposable
    {
        void Start(Mat bgr, Mat gray, Rect2d box);

        (Rect2d Box, bool Found) Next(Mat bgr, Mat gray);
    }

    // TrackerCSRT.Create() with no arguments: a default Params struct is all zeros, which crashes it.
    private sealed class CsrtFollower : IFollower
    {
        private TrackerCSRT? _tracker;

        public void Start(Mat bgr, Mat gray, Rect2d box)
        {
            _tracker = TrackerCSRT.Create();
            _tracker.Init(bgr, Whole(box, bgr));
        }

        public (Rect2d Box, bool Found) Next(Mat bgr, Mat gray)
        {
            var rect = new Rect();
            var found = _tracker!.Update(bgr, ref rect) && rect.Width > 0 && rect.Height > 0;
            return (new Rect2d(rect.X, rect.Y, rect.Width, rect.Height), found);
        }

        public void Dispose() => _tracker?.Dispose();
    }

    // The anchor's picture, searched for within a box size around the last box.
    private sealed class TemplateFollower : IFollower
    {
        private const double MinScore = 0.3;

        private Mat? _template;
        private Rect2d _box;

        public void Start(Mat bgr, Mat gray, Rect2d box)
        {
            _box = box;
            _template = new Mat();
            Cv2.GetRectSubPix(gray, new Size(Math.Max(1, (int)Math.Round(box.Width)), Math.Max(1, (int)Math.Round(box.Height))),
                new Point2f((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2)), _template);
        }

        public (Rect2d Box, bool Found) Next(Mat bgr, Mat gray)
        {
            var template = _template!;
            var margin = Math.Max(16, Math.Max(template.Width, template.Height));
            var left = Math.Max(0, (int)Math.Floor(_box.X - margin));
            var top = Math.Max(0, (int)Math.Floor(_box.Y - margin));
            var right = Math.Min(gray.Width, (int)Math.Ceiling(_box.X + template.Width + margin));
            var bottom = Math.Min(gray.Height, (int)Math.Ceiling(_box.Y + template.Height + margin));
            if (right - left < template.Width || bottom - top < template.Height)
            {
                return (_box, false);
            }

            using var window = new Mat(gray, new Rect(left, top, right - left, bottom - top));
            using var scores = new Mat();
            Cv2.MatchTemplate(window, template, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out var best, out _, out var at);
            if (!(best >= MinScore))
            {
                return (_box, false);
            }

            var dx = at.X > 0 && at.X < scores.Cols - 1 ? Peak(scores.At<float>(at.Y, at.X - 1), (float)best, scores.At<float>(at.Y, at.X + 1)) : 0;
            var dy = at.Y > 0 && at.Y < scores.Rows - 1 ? Peak(scores.At<float>(at.Y - 1, at.X), (float)best, scores.At<float>(at.Y + 1, at.X)) : 0;
            _box = new Rect2d(left + at.X + dx + (template.Width - _box.Width) / 2, top + at.Y + dy + (template.Height - _box.Height) / 2, _box.Width, _box.Height);
            return (_box, true);
        }

        public void Dispose() => _template?.Dispose();

        // Where a parabola through three scores peaks, relative to the middle one.
        private static double Peak(float before, float middle, float after)
        {
            var curve = before - 2 * middle + after;
            return curve < 0 ? Math.Clamp((before - after) / (2.0 * curve), -0.5, 0.5) : 0;
        }
    }

    // Points inside the box followed to the next frame; those that don't track back to where they
    // started are dropped.
    private sealed class FlowFollower : IFollower
    {
        private const int MaxPoints = 100;
        private const int Grid = 8;
        private const double MaxBackError = 1;
        private const int MaxPairs = 400;

        private static readonly Size Window = new(21, 21);

        private Mat? _previous;
        private Rect2d _box;

        public void Start(Mat bgr, Mat gray, Rect2d box)
        {
            _previous = gray.Clone();
            _box = box;
        }

        public (Rect2d Box, bool Found) Next(Mat bgr, Mat gray)
        {
            var previous = _previous!;
            var moved = Move(previous, gray, Seed(previous, _box), _box);
            previous.Dispose();
            _previous = gray.Clone();
            if (moved is not { } box)
            {
                return (_box, false);
            }

            _box = box;
            return (_box, true);
        }

        public void Dispose() => _previous?.Dispose();

        private static Point2f[] Seed(Mat gray, Rect2d box)
        {
            var rect = Whole(box, gray);
            if (rect.Width < 2 || rect.Height < 2)
            {
                return [];
            }

            using var region = new Mat(gray, rect);
            var corners = Cv2.GoodFeaturesToTrack(region, MaxPoints, 0.01, Math.Max(2, Math.Min(rect.Width, rect.Height) / 16.0), null!, 3, false, 0.04);
            var points = corners.Select(p => new Point2f(p.X + rect.X, p.Y + rect.Y)).ToList();
            if (points.Count < 10)
            {
                for (var row = 0; row < Grid; row++)
                {
                    for (var column = 0; column < Grid; column++)
                    {
                        points.Add(new Point2f(
                            (float)(rect.X + rect.Width * (0.1 + 0.8 * (column + 0.5) / Grid)),
                            (float)(rect.Y + rect.Height * (0.1 + 0.8 * (row + 0.5) / Grid))));
                    }
                }
            }

            return points.ToArray();
        }

        private static Rect2d? Move(Mat previous, Mat gray, Point2f[] points, Rect2d box)
        {
            if (points.Length < 4)
            {
                return null;
            }

            var next = Array.Empty<Point2f>();
            Cv2.CalcOpticalFlowPyrLK(previous, gray, points, ref next, out var status, out _, Window, 3);
            var back = Array.Empty<Point2f>();
            Cv2.CalcOpticalFlowPyrLK(gray, previous, next, ref back, out var backStatus, out _, Window, 3);
            var kept = Enumerable.Range(0, points.Length)
                .Where(i => status[i] == 1 && backStatus[i] == 1 && Distance(points[i], back[i]) < MaxBackError)
                .ToList();
            if (kept.Count < 4)
            {
                return null;
            }

            var dx = Median(kept.Select(i => (double)(next[i].X - points[i].X)).ToList());
            var dy = Median(kept.Select(i => (double)(next[i].Y - points[i].Y)).ToList());
            var ratios = new List<double>();
            for (var a = 0; a < kept.Count && ratios.Count < MaxPairs; a++)
            {
                for (var b = a + 1; b < kept.Count && ratios.Count < MaxPairs; b++)
                {
                    var before = Distance(points[kept[a]], points[kept[b]]);
                    if (before > 4)
                    {
                        ratios.Add(Distance(next[kept[a]], next[kept[b]]) / before);
                    }
                }
            }

            var scale = ratios.Count > 0 ? Math.Clamp(Median(ratios), 0.8, 1.25) : 1;
            var width = box.Width * scale;
            var height = box.Height * scale;
            var centerX = box.X + box.Width / 2 + dx;
            var centerY = box.Y + box.Height / 2 + dy;
            return new Rect2d(centerX - width / 2, centerY - height / 2, width, height);
        }

        private static double Distance(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    // The middle 70% of the anchor's picture, looked for in the box at 0.9, 1 and 1.1 times its size;
    // everything scaled so the box is 32 px on its long side.
    private sealed class Similarity : IDisposable
    {
        private const double PatchSide = 32;
        private const double Middle = 0.7;

        private static readonly double[] Scales = [0.9, 1, 1.1];

        private readonly Mat _anchor;
        private readonly Size _size;
        private readonly Rect _middle;
        private readonly bool _flat;

        public Similarity(Mat gray, Rect2d box)
        {
            var scale = PatchSide / Math.Max(box.Width, box.Height);
            _size = new Size(Math.Max(8, (int)Math.Round(box.Width * scale)), Math.Max(8, (int)Math.Round(box.Height * scale)));
            var width = Math.Max(4, (int)Math.Round(_size.Width * Middle));
            var height = Math.Max(4, (int)Math.Round(_size.Height * Middle));
            _middle = new Rect((_size.Width - width) / 2, (_size.Height - height) / 2, width, height);
            using var whole = Patch(gray, box, _size);
            _anchor = new Mat(whole, _middle).Clone();
            Cv2.MeanStdDev(_anchor, out _, out var deviation);
            _flat = deviation.Val0 < 2;
        }

        public double Score(Mat gray, Rect2d box)
        {
            var best = 0.0;
            foreach (var scale in Scales)
            {
                var scaled = new Rect2d(box.X + box.Width * (1 - scale) / 2, box.Y + box.Height * (1 - scale) / 2, box.Width * scale, box.Height * scale);
                using var patch = Patch(gray, scaled, _size);
                double value;
                if (_flat)
                {
                    using var middle = new Mat(patch, _middle);
                    using var difference = new Mat();
                    Cv2.Absdiff(middle, _anchor, difference);
                    value = 1 - Cv2.Mean(difference).Val0 / 32;
                }
                else
                {
                    using var result = new Mat();
                    Cv2.MatchTemplate(patch, _anchor, result, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(result, out _, out value);
                }

                if (double.IsFinite(value))
                {
                    best = Math.Max(best, value);
                }
            }

            return Math.Clamp(best, 0, 1);
        }

        public void Dispose() => _anchor.Dispose();

        private static Mat Patch(Mat gray, Rect2d box, Size size)
        {
            using var crop = new Mat();
            Cv2.GetRectSubPix(gray, new Size(Math.Max(1, (int)Math.Round(box.Width)), Math.Max(1, (int)Math.Round(box.Height))),
                new Point2f((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2)), crop);
            var patch = new Mat();
            Cv2.Resize(crop, patch, size, 0, 0, InterpolationFlags.Area);
            return patch;
        }
    }
}
