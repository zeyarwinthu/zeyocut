// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record ReframeResult(string LayerId, double CropWidth, double CropHeight, double CropX, double CropY, double Scale);

// Fits landscape clips to a vertical canvas: a full-height crop with the canvas' aspect ratio, placed
// at focusX (0 = left edge, 0.5 = center, 1 = right edge), scaled to fill the canvas. cropX stays
// animatable to follow the subject. Zoom differences between pieces (punch-in) are kept.
public static class Reframe
{
    public static List<ReframeResult> ToVertical(Project project, string? layerId, double focusX, out List<string> warnings)
    {
        warnings = [];
        if (project.Height <= project.Width)
        {
            throw new ZeyoCutException($"the canvas is {project.Width}x{project.Height}; reframe_to_vertical fits clips to a vertical canvas, so switch it first (update_project preset=shorts_1080p)");
        }

        if (!double.IsFinite(focusX) || focusX < 0 || focusX > 1)
        {
            throw new ZeyoCutException($"focusX must be between 0 (left) and 1 (right); got {Num(focusX)}");
        }

        var all = string.IsNullOrWhiteSpace(layerId) || string.Equals(layerId.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        List<VideoLayer> targets;
        if (all)
        {
            targets = project.Layers.OfType<VideoLayer>().ToList();
            if (targets.Count == 0)
            {
                throw new ZeyoCutException("the project has no video layers");
            }
        }
        else
        {
            var layer = ProjectEditor.GetLayer(project, layerId!.Trim());
            targets = layer is VideoLayer video
                ? [video]
                : throw new ZeyoCutException($"reframe_to_vertical works on video layers; '{layer.Id}' is a {layer.TypeName} layer");
        }

        var aspect = project.Width / (double)project.Height;
        var baseScale = targets
            .GroupBy(v => (v.Asset, VideoTiming.CropSize(v, Asset(project, v))))
            .ToDictionary(g => g.Key, g => g.Min(v => v.ScaleX.Value));
        var results = new List<ReframeResult>();
        foreach (var video in targets)
        {
            var asset = Asset(project, video);
            var (sourceWidth, sourceHeight) = ((double)(asset.Width ?? 0), (double)(asset.Height ?? 0));
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                throw new ZeyoCutException($"asset '{asset.Id}' has no frame size; import it again with import_video");
            }

            var oldCrop = VideoTiming.CropSize(video, asset);
            var basis = baseScale[(video.Asset, oldCrop)];
            var zoom = basis > 0 ? video.ScaleX.Value / basis : 1;

            double cropWidth, cropHeight;
            if (sourceWidth / sourceHeight > aspect)
            {
                cropHeight = sourceHeight;
                cropWidth = Math.Min(sourceWidth, Math.Round(sourceHeight * aspect / 2) * 2);
            }
            else
            {
                cropWidth = sourceWidth;
                cropHeight = Math.Min(sourceHeight, Math.Round(sourceWidth / aspect / 2) * 2);
            }

            var cropX = Math.Round(Math.Clamp(focusX * sourceWidth - cropWidth / 2, 0, sourceWidth - cropWidth));
            var cropY = Math.Round((sourceHeight - cropHeight) / 2);
            var scale = Math.Max(project.Width / cropWidth, project.Height / cropHeight) * zoom;

            video.CropWidth = cropWidth;
            video.CropHeight = cropHeight;
            video.CropX.Value = cropX;
            video.CropY.Value = cropY;
            video.AnchorX = cropWidth / 2;
            video.AnchorY = cropHeight / 2;
            video.X.Value = project.Width / 2.0;
            video.Y.Value = project.Height / 2.0;
            var factor = video.ScaleX.Value != 0 ? scale / video.ScaleX.Value : 1;
            foreach (var value in new[] { video.ScaleX, video.ScaleY })
            {
                value.Value = value == video.ScaleX ? scale : value.Value * factor;
                foreach (var key in value.Keyframes)
                {
                    key.Value *= factor;
                }
            }

            if (video.CropX.Keyframes.Count > 0 || video.CropY.Keyframes.Count > 0)
            {
                warnings.Add($"'{video.Id}' has cropX/cropY keyframes from before; they still apply (clear_keyframes to use the new crop position)");
            }

            if (video.X.Keyframes.Count > 0 || video.Y.Keyframes.Count > 0)
            {
                warnings.Add($"'{video.Id}' has x/y keyframes, which still move it; the new center is ({Num(project.Width / 2.0)}, {Num(project.Height / 2.0)})");
            }

            results.Add(new ReframeResult(video.Id, cropWidth, cropHeight, cropX, cropY, scale));
        }

        return results;
    }

    private static Asset Asset(Project project, VideoLayer video) =>
        project.FindAsset(video.Asset) ?? throw new ZeyoCutException($"layer '{video.Id}': asset '{video.Asset}' not found");

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
