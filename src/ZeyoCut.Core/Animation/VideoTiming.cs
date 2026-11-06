// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

public readonly record struct CropRect(double X, double Y, double Width, double Height);

// Maps timeline time to source time and source frames. Video assets are constant frame rate, so the
// n-th decoded frame is shown from n / fps until (n + 1) / fps in source time.
public static class VideoTiming
{
    public static (long Numerator, long Denominator) ParseRate(string? rate)
    {
        var text = (rate ?? "").Trim();
        var parts = text.Split('/');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0)
        {
            return (numerator, denominator);
        }

        if (parts.Length == 1 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0 && double.IsFinite(fps))
        {
            return Math.Abs(fps - Math.Round(fps)) < 1e-9 ? ((long)Math.Round(fps), 1) : ((long)Math.Round(fps * 1000), 1000);
        }

        throw new ZeyoCutException($"invalid frame rate '{rate}'; expected a number or a fraction like 30000/1001");
    }

    public static bool IsValidRate(string? rate)
    {
        try
        {
            ParseRate(rate);
            return true;
        }
        catch (ZeyoCutException)
        {
            return false;
        }
    }

    public static double Fps(Asset asset)
    {
        var (numerator, denominator) = ParseRate(asset.FrameRate);
        return numerator / (double)denominator;
    }

    public static double SourceTime(VideoLayer layer, double time) => layer.TrimIn + (time - layer.Start) * layer.Speed;

    public static double TimelineTime(VideoLayer layer, double sourceTime) => layer.Start + (sourceTime - layer.TrimIn) / layer.Speed;

    public static int FrameAt(Asset asset, double sourceTime)
    {
        var (numerator, denominator) = ParseRate(asset.FrameRate);
        var frame = (long)Math.Floor(sourceTime * numerator / denominator + 1e-6);
        var last = Math.Max(0, (asset.FrameCount ?? int.MaxValue) - 1);
        return (int)Math.Clamp(frame, 0, last);
    }

    public static double TimeOfFrame(Asset asset, int frame)
    {
        var (numerator, denominator) = ParseRate(asset.FrameRate);
        return frame * (double)denominator / numerator;
    }

    // The part of the source shown by the layer, in source pixels. Without a crop it is the whole frame.
    public static CropRect Crop(VideoLayer layer, Asset asset, double time)
    {
        var sourceWidth = asset.Width ?? 0;
        var sourceHeight = asset.Height ?? 0;
        var width = Math.Min(layer.CropWidth ?? sourceWidth, sourceWidth);
        var height = Math.Min(layer.CropHeight ?? sourceHeight, sourceHeight);
        var x = Math.Clamp(Interpolator.Evaluate(layer.CropX, time), 0, Math.Max(0, sourceWidth - width));
        var y = Math.Clamp(Interpolator.Evaluate(layer.CropY, time), 0, Math.Max(0, sourceHeight - height));
        return new CropRect(x, y, width, height);
    }

    public static (double Width, double Height) CropSize(VideoLayer layer, Asset asset) =>
        (Math.Min(layer.CropWidth ?? asset.Width ?? 0, asset.Width ?? 0), Math.Min(layer.CropHeight ?? asset.Height ?? 0, asset.Height ?? 0));
}
