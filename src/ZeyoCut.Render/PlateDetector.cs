// SPDX-License-Identifier: MIT

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Render;

// The license plate model of open-image-models (YOLOv9-t at 640 px, with its NMS in the graph) on a BGR
// picture. The model sees the picture shrunk to 640 px, so each corner of it (2x2 tiles that overlap)
// is searched too, for smaller plates, and the boxes of the five passes are merged. Each pass is
// letterboxed as open-image-models does: scaled to fit 640x640, centered on gray 114, RGB from 0 to 1.
public static class PlateDetector
{
    public const int Side = 640;

    // Each tile covers this share of the picture's width and height, so the tiles overlap by a fifth of it.
    private const double TileShare = 0.6;

    // A box from a tile this close to one of its inner edges is a plate the edge cuts; the whole
    // picture or the tile beside it sees that plate whole.
    private const double EdgeMargin = 2;

    private const float Gray = 114 / 255f;

    public static List<Detection> Detect(AiSession session, Mat bgr, double minScore)
    {
        if (bgr.Type() != MatType.CV_8UC3)
        {
            throw new ZeyoCutException("plate detection needs an 8-bit BGR picture");
        }

        var width = bgr.Width;
        var height = bgr.Height;
        var found = Run(session, bgr, minScore);
        var tileWidth = (int)Math.Round(width * TileShare);
        var tileHeight = (int)Math.Round(height * TileShare);
        foreach (var top in new[] { 0, height - tileHeight })
        {
            foreach (var left in new[] { 0, width - tileWidth })
            {
                using var tile = new Mat(bgr, new Rect(left, top, tileWidth, tileHeight));
                foreach (var d in Run(session, tile, minScore))
                {
                    var cut = (left > 0 && d.X <= EdgeMargin)
                        || (top > 0 && d.Y <= EdgeMargin)
                        || (left + tileWidth < width && d.X + d.Width >= tileWidth - EdgeMargin)
                        || (top + tileHeight < height && d.Y + d.Height >= tileHeight - EdgeMargin);
                    if (!cut)
                    {
                        found.Add(d with { X = d.X + left, Y = d.Y + top });
                    }
                }
            }
        }

        return FaceDetector.Merge(found, width, height);
    }

    private static List<Detection> Run(AiSession session, Mat bgr, double minScore)
    {
        var width = bgr.Width;
        var height = bgr.Height;
        var ratio = Math.Min(Side / (double)height, Side / (double)width);
        var scaledWidth = (int)Math.Round(width * ratio);
        var scaledHeight = (int)Math.Round(height * ratio);
        var padX = (Side - scaledWidth) / 2.0;
        var padY = (Side - scaledHeight) / 2.0;
        var left = (int)Math.Round(padX - 0.1);
        var top = (int)Math.Round(padY - 0.1);

        const int plane = Side * Side;
        var input = new DenseTensor<float>([1, 3, Side, Side]);
        var values = input.Buffer.Span;
        values.Fill(Gray);
        using (var scaled = scaledWidth == width && scaledHeight == height ? null : new Mat())
        {
            if (scaled is not null)
            {
                Cv2.Resize(bgr, scaled, new Size(scaledWidth, scaledHeight), 0, 0, InterpolationFlags.Linear);
            }

            var source = scaled ?? bgr;
            using var continuous = source.IsContinuous() ? null : source.Clone();
            var pixels = (continuous ?? source).AsSpan<byte>();
            for (var y = 0; y < scaledHeight; y++)
            {
                var row = y * scaledWidth * 3;
                var to = (y + top) * Side + left;
                for (var x = 0; x < scaledWidth; x++)
                {
                    var from = row + x * 3;
                    values[to + x] = pixels[from + 2] / 255f;
                    values[plane + to + x] = pixels[from + 1] / 255f;
                    values[2 * plane + to + x] = pixels[from] / 255f;
                }
            }
        }

        var name = session.Session.InputMetadata.Keys.First();
        using var results = session.Session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        var output = results.First().AsTensor<float>();
        if (output.Rank != 2 || (output.Length > 0 && output.Dimensions[1] != 7))
        {
            throw new ZeyoCutException($"the plate model's output has the shape [{string.Join(", ", output.Dimensions.ToArray())}] instead of [n, 7]; delete the model so it downloads again");
        }

        var detections = new List<Detection>();
        for (var i = 0; i < output.Dimensions[0]; i++)
        {
            double score = output[i, 6];
            if (score < minScore)
            {
                continue;
            }

            var x1 = (output[i, 1] - padX) / ratio;
            var y1 = (output[i, 2] - padY) / ratio;
            var x2 = (output[i, 3] - padX) / ratio;
            var y2 = (output[i, 4] - padY) / ratio;
            detections.Add(new Detection(x1, y1, x2 - x1, y2 - y1, score));
        }

        return detections;
    }
}
