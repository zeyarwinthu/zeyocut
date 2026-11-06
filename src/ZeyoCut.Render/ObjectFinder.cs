// SPDX-License-Identifier: MIT

using System.Diagnostics;
using OpenCvSharp;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Render;

public enum DetectTarget
{
    Faces,
    Plates
}

// A track detect_and_track found, in source frames and pixels, with the picture of its best detection.
public sealed record FoundTrack(List<TrackedFrame> Frames, SKImage Picture);

public sealed class FindResult(List<FoundTrack> tracks, List<int> brief, int frames, TimeSpan elapsed) : IDisposable
{
    public List<FoundTrack> Tracks { get; } = tracks;

    // The first source frames of detections too brief to keep.
    public List<int> Brief { get; } = brief;

    public int Frames { get; } = frames;

    public TimeSpan Elapsed { get; } = elapsed;

    public void Dispose()
    {
        foreach (var track in Tracks)
        {
            track.Picture.Dispose();
        }
    }
}

// detect_and_track: runs a detector on every frame from 'first' to 'last' (at the size tracking uses)
// and links the boxes into tracks (DetectionLinker), keeping each track's best detection as a picture
// for the contact sheet.
public static class ObjectFinder
{
    // A track ends after this long without a detection.
    public const double BufferSeconds = 1;

    // The pictures show this much around the box on each side, at most this size.
    private const double PictureMargin = 0.3;
    private const int PictureSide = 256;

    public static FindResult Find(
        string ffmpeg,
        string file,
        Asset asset,
        int first,
        int last,
        Func<Mat, List<Detection>> detect,
        double confident,
        double weak,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        var (width, height) = ObjectTracker.TrackingSize(asset);
        var scaleX = asset.Width!.Value / (double)width;
        var scaleY = asset.Height!.Value / (double)height;
        var linker = new DetectionLinker(confident, weak, (int)Math.Round(VideoTiming.Fps(asset) * BufferSeconds));
        var best = new Dictionary<int, (double Score, SKImage Picture)>();
        var stopwatch = Stopwatch.StartNew();
        var total = last - first + 1;
        var done = 0;
        try
        {
            using (var reader = new VideoFrameReader(ffmpeg, file, first, VideoDecoder.SeekSeconds(asset, first), width, height, total))
            using (var rgba = new Mat(height, width, MatType.CV_8UC4))
            using (var bgr = new Mat())
            {
                while (reader.Advance())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reader.Current.CopyTo(rgba.AsSpan<byte>());
                    Cv2.CvtColor(rgba, bgr, ColorConversionCodes.RGBA2BGR);
                    var detections = detect(bgr);
                    var keys = linker.Add(reader.CurrentFrame, detections);
                    for (var i = 0; i < detections.Count; i++)
                    {
                        var key = keys[i];
                        if (key >= 0 && (!best.TryGetValue(key, out var kept) || detections[i].Score > kept.Score))
                        {
                            var picture = Picture(bgr, detections[i]);
                            if (best.TryGetValue(key, out var replaced))
                            {
                                replaced.Picture.Dispose();
                            }

                            best[key] = (detections[i].Score, picture);
                        }
                    }

                    done++;
                    progress?.Invoke(done, total);
                }

                reader.ThrowIfFailed();
            }

            if (done == 0)
            {
                throw new ZeyoCutException($"frames {first}–{last} of '{asset.Id}' couldn't be decoded");
            }

            var (linked, brief) = linker.Finish();
            var tracks = new List<FoundTrack>();
            foreach (var track in linked)
            {
                var frames = track.Frames.Select(f => new TrackedFrame
                {
                    Frame = f.Frame,
                    X = f.X * scaleX,
                    Y = f.Y * scaleY,
                    Width = f.Width * scaleX,
                    Height = f.Height * scaleY,
                    Confidence = f.Confidence,
                    Lost = f.Lost,
                }).ToList();
                tracks.Add(new FoundTrack(frames, best[track.Key].Picture));
                best.Remove(track.Key);
            }

            return new FindResult(tracks, brief, done, stopwatch.Elapsed);
        }
        catch (OpenCVException ex)
        {
            throw new ZeyoCutException($"OpenCV failed while searching the frames: {ex.ErrMsg}");
        }
        finally
        {
            foreach (var (_, picture) in best.Values)
            {
                picture.Dispose();
            }
        }
    }

    // The detection with a margin around it, as RGBA, at most PictureSide on its long side.
    private static SKImage Picture(Mat bgr, Detection box)
    {
        var marginX = box.Width * PictureMargin;
        var marginY = box.Height * PictureMargin;
        var left = Math.Clamp((int)Math.Floor(box.X - marginX), 0, bgr.Width - 1);
        var top = Math.Clamp((int)Math.Floor(box.Y - marginY), 0, bgr.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(box.X + box.Width + marginX), left + 1, bgr.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(box.Y + box.Height + marginY), top + 1, bgr.Height);
        using var crop = new Mat(bgr, new Rect(left, top, right - left, bottom - top));
        var scale = Math.Min(1, PictureSide / (double)Math.Max(crop.Width, crop.Height));
        using var sized = new Mat();
        if (scale < 1)
        {
            Cv2.Resize(crop, sized, new Size(Math.Max(1, (int)Math.Round(crop.Width * scale)), Math.Max(1, (int)Math.Round(crop.Height * scale))), 0, 0, InterpolationFlags.Area);
        }
        else
        {
            crop.CopyTo(sized);
        }

        using var rgba = new Mat();
        Cv2.CvtColor(sized, rgba, ColorConversionCodes.BGR2RGBA);
        return Pixels.Rgba(rgba.AsSpan<byte>().ToArray(), rgba.Width, rgba.Height);
    }
}
