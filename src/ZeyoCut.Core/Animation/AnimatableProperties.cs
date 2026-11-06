// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

public static class AnimatableProperties
{
    public const string Drawing = "drawing";

    // A motion path's progress (0 at its first point): set by set_motion_path, never by hand.
    public const string Path = "path";

    public static readonly IReadOnlyList<string> NumericNames =
    [
        "x", "y", "rotation", "scaleX", "scaleY", "opacity", "brightness", "contrast", "saturation", "cropX", "cropY",
        "shadow.x", "shadow.y", "shadow.blur", "shadow.opacity", "outline.width", "blur.x", "blur.y", "tint.amount",
        "mask.x", "mask.y", "mask.width", "mask.height", "mask.feather", Path,
    ];

    public static readonly IReadOnlyList<string> CameraNames = ["x", "y", "zoom", "rotation", "shakeX", "shakeY", "shakeRotation"];

    private static readonly string[] ColorNames = ["brightness", "contrast", "saturation"];
    private static readonly string[] CropNames = ["cropX", "cropY"];

    public static string Describe() =>
        "x, y, rotation, scaleX, scaleY, opacity; brightness, contrast, saturation (image and video layers); cropX, cropY (video layers); drawing (image layers); "
        + "shadow.x, shadow.y, shadow.blur, shadow.opacity, outline.width, blur.x, blur.y, tint.amount (layers with that effect, see set_effect); "
        + "mask.x, mask.y, mask.width, mask.height, mask.feather (layers with a mask, see set_mask)";

    public static IEnumerable<(string Name, AnimatedValue Value)> Numeric(Camera camera)
    {
        yield return ("x", camera.X);
        yield return ("y", camera.Y);
        yield return ("zoom", camera.Zoom);
        yield return ("rotation", camera.Rotation);
        yield return ("shakeX", camera.ShakeX);
        yield return ("shakeY", camera.ShakeY);
        yield return ("shakeRotation", camera.ShakeRotation);
    }

    public static string? CameraCanonical(string? name) =>
        CameraNames.FirstOrDefault(n => string.Equals(n, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static AnimatedValue GetNumeric(Camera camera, string? name) =>
        CameraCanonical(name) is { } canonical
            ? Numeric(camera).First(p => p.Name == canonical).Value
            : throw new ZeyoCutException($"unknown camera property '{name}'; camera properties: {string.Join(", ", CameraNames)}");

    public static IEnumerable<(string Name, AnimatedValue Value)> Numeric(VisualLayer layer)
    {
        yield return ("x", layer.X);
        yield return ("y", layer.Y);
        yield return ("rotation", layer.Rotation);
        yield return ("scaleX", layer.ScaleX);
        yield return ("scaleY", layer.ScaleY);
        yield return ("opacity", layer.Opacity);
        if (layer is IColorAdjustable color)
        {
            yield return ("brightness", color.Brightness);
            yield return ("contrast", color.Contrast);
            yield return ("saturation", color.Saturation);
        }

        if (layer is VideoLayer video)
        {
            yield return ("cropX", video.CropX);
            yield return ("cropY", video.CropY);
        }

        if (layer.Shadow is { } shadow)
        {
            yield return ("shadow.x", shadow.X);
            yield return ("shadow.y", shadow.Y);
            yield return ("shadow.blur", shadow.Blur);
            yield return ("shadow.opacity", shadow.Opacity);
        }

        if (layer.Outline is { } outline)
        {
            yield return ("outline.width", outline.Width);
        }

        if (layer.Blur is { } blur)
        {
            yield return ("blur.x", blur.X);
            yield return ("blur.y", blur.Y);
        }

        if (layer.Tint is { } tint)
        {
            yield return ("tint.amount", tint.Amount);
        }

        if (layer.Mask is { } mask)
        {
            yield return ("mask.x", mask.X);
            yield return ("mask.y", mask.Y);
            yield return ("mask.width", mask.Width);
            yield return ("mask.height", mask.Height);
            yield return ("mask.feather", mask.Feather);
        }

        if (layer.MotionPath is { } path)
        {
            yield return (Path, path.Progress);
        }
    }

    public static string? Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (string.Equals(name, Drawing, StringComparison.OrdinalIgnoreCase))
        {
            return Drawing;
        }

        return NumericNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    public static AnimatedValue GetNumeric(VisualLayer layer, string canonicalName)
    {
        foreach (var (name, value) in Numeric(layer))
        {
            if (name == canonicalName)
            {
                return value;
            }
        }

        if (ColorNames.Contains(canonicalName))
        {
            throw new ZeyoCutException($"'{canonicalName}' is only on image and video layers; '{layer.Id}' is a {layer.TypeName} layer");
        }

        if (CropNames.Contains(canonicalName))
        {
            throw new ZeyoCutException($"'{canonicalName}' is only on video layers; '{layer.Id}' is a {layer.TypeName} layer");
        }

        if (canonicalName == Path)
        {
            throw new ZeyoCutException($"'{layer.Id}' has no motion path; set_motion_path adds one");
        }

        if (canonicalName.Split('.') is [var owner, _])
        {
            throw new ZeyoCutException(owner == "mask"
                ? $"'{layer.Id}' has no mask; add one with set_mask first"
                : $"'{layer.Id}' has no {owner} effect; add one with set_effect first");
        }

        throw new ZeyoCutException($"unknown property '{canonicalName}'; animatable properties: {Describe()}");
    }
}
