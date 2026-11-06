// SPDX-License-Identifier: MIT

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Render;

// YuNet (OpenCV Zoo) on a BGR picture. It finds faces of about 10-300 px, so the picture is also searched
// at a third and an eighth of its size for bigger ones, and the boxes of all three are merged. Its
// outputs are decoded as OpenCV's FaceDetectorYN does: per stride and cell, a score sqrt(cls * obj) and a
// box from the cell's offsets. The picture is padded with black to multiples of 32.
public static class FaceDetector
{
    // A box that overlaps a better one by more than this (intersection over union), or where one of the
    // two lies mostly inside the other (a face, and the face with its hair and neck), is the same face.
    public const double SameFace = 0.3;
    private const double Inside = 0.7;

    // A reduced picture smaller than this on its short side isn't searched.
    private const int MinSide = 32;

    private const int Padding = 32;

    private static readonly double[] Scales = [1, 1 / 3.0, 1 / 8.0];
    private static readonly int[] Strides = [8, 16, 32];

    public static List<Detection> Detect(AiSession session, Mat bgr, double minScore)
    {
        if (bgr.Type() != MatType.CV_8UC3)
        {
            throw new ZeyoCutException("face detection needs an 8-bit BGR picture");
        }

        var found = new List<Detection>();
        foreach (var scale in Scales)
        {
            if (scale == 1)
            {
                found.AddRange(Run(session, bgr, 1, 1, minScore));
                continue;
            }

            var width = (int)Math.Round(bgr.Width * scale);
            var height = (int)Math.Round(bgr.Height * scale);
            if (Math.Min(width, height) < MinSide)
            {
                continue;
            }

            using var small = new Mat();
            Cv2.Resize(bgr, small, new Size(width, height), 0, 0, InterpolationFlags.Area);
            found.AddRange(Run(session, small, bgr.Width / (double)width, bgr.Height / (double)height, minScore));
        }

        return Merge(found, bgr.Width, bgr.Height);
    }

    private static List<Detection> Run(AiSession session, Mat bgr, double scaleX, double scaleY, double minScore)
    {
        var width = bgr.Width;
        var height = bgr.Height;
        var paddedWidth = (width + Padding - 1) / Padding * Padding;
        var paddedHeight = (height + Padding - 1) / Padding * Padding;
        var plane = paddedWidth * paddedHeight;
        var input = new DenseTensor<float>([1, 3, paddedHeight, paddedWidth]);
        var values = input.Buffer.Span;
        using (var continuous = bgr.IsContinuous() ? null : bgr.Clone())
        {
            var pixels = (continuous ?? bgr).AsSpan<byte>();
            for (var y = 0; y < height; y++)
            {
                var row = y * width * 3;
                var to = y * paddedWidth;
                for (var x = 0; x < width; x++)
                {
                    var from = row + x * 3;
                    values[to + x] = pixels[from];
                    values[plane + to + x] = pixels[from + 1];
                    values[2 * plane + to + x] = pixels[from + 2];
                }
            }
        }

        var detections = new List<Detection>();
        var name = session.Session.InputMetadata.Keys.First();
        using var results = session.Session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>(), StringComparer.Ordinal);
        foreach (var stride in Strides)
        {
            var columns = paddedWidth / stride;
            var rows = paddedHeight / stride;
            var classes = Output(outputs, $"cls_{stride}", rows * columns);
            var objects = Output(outputs, $"obj_{stride}", rows * columns);
            var boxes = Output(outputs, $"bbox_{stride}", rows * columns * 4);
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var i = r * columns + c;
                    var score = Math.Sqrt(Math.Clamp(classes[i], 0f, 1f) * Math.Clamp(objects[i], 0f, 1f));
                    if (score < minScore)
                    {
                        continue;
                    }

                    var centerX = (c + boxes[i * 4]) * stride;
                    var centerY = (r + boxes[i * 4 + 1]) * stride;
                    var boxWidth = Math.Exp(boxes[i * 4 + 2]) * stride;
                    var boxHeight = Math.Exp(boxes[i * 4 + 3]) * stride;
                    detections.Add(new Detection((centerX - boxWidth / 2) * scaleX, (centerY - boxHeight / 2) * scaleY, boxWidth * scaleX, boxHeight * scaleY, score));
                }
            }
        }

        return detections;
    }

    private static float[] Output(Dictionary<string, Tensor<float>> outputs, string name, int length)
    {
        if (!outputs.TryGetValue(name, out var tensor) || tensor.Length != length)
        {
            throw new ZeyoCutException($"the face model's output {name} is missing or has {(tensor?.Length ?? 0)} values instead of {length}; delete the model so it downloads again");
        }

        return tensor.ToArray();
    }

    // Boxes cut to the picture, best first, without the ones that are the same face as a better one.
    // PlateDetector merges its passes with it too.
    internal static List<Detection> Merge(List<Detection> found, int width, int height)
    {
        var kept = new List<Detection>();
        foreach (var d in found.OrderByDescending(d => d.Score))
        {
            var left = Math.Max(0, d.X);
            var top = Math.Max(0, d.Y);
            var right = Math.Min(width, d.X + d.Width);
            var bottom = Math.Min(height, d.Y + d.Height);
            if (right - left < 2 || bottom - top < 2)
            {
                continue;
            }

            var box = d with { X = left, Y = top, Width = right - left, Height = bottom - top };
            if (!kept.Any(k => DetectionLinker.Overlap(k.X, k.Y, k.Width, k.Height, box.X, box.Y, box.Width, box.Height) > SameFace || Nested(box, k)))
            {
                kept.Add(box);
            }
        }

        return kept;
    }

    private static bool Nested(Detection box, Detection other)
    {
        var width = Math.Min(box.X + box.Width, other.X + other.Width) - Math.Max(box.X, other.X);
        var height = Math.Min(box.Y + box.Height, other.Y + other.Height) - Math.Max(box.Y, other.Y);
        return width > 0 && height > 0 && width * height > Inside * Math.Min(box.Width * box.Height, other.Width * other.Height);
    }
}
