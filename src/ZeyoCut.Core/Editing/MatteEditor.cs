// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record MatteValues(string? Color, double? Tolerance, double? Softness, double? Spill);

public sealed record MatteChange(string Summary, IReadOnlyList<string> Warnings);

// remove_background and refine_matte.
public static class MatteEditor
{
    public const string DefaultKey = "#00FF00";
    public const double DefaultChromaTolerance = 0.4;
    public const double DefaultSoftness = 0.2;
    public const double DefaultSpill = 1;
    public const double DefaultEdgeTolerance = 0.1;
    public const double MaxShrink = 100;
    public const double MaxFeather = 100;

    // A key color needs at least this much chroma (YCbCr, 0..0.6) to tell it from grays.
    public const double MinKeyChroma = 0.08;

    private static readonly Dictionary<MatteMethod, string[]> Fields = new()
    {
        [MatteMethod.Chroma] = ["color", "tolerance", "softness", "spill"],
        [MatteMethod.EdgeFill] = ["color", "tolerance"],
        [MatteMethod.AiImage] = [],
        [MatteMethod.AiVideo] = [],
    };

    public static IMattable GetMattable(Project project, string? layerId)
    {
        var layer = ProjectEditor.GetLayer(project, layerId);
        switch (layer)
        {
            case ImageLayer { Drawings.Count: 0 }:
                throw new ZeyoCutException($"'{layer.Id}' is a null layer (no drawings), so it has no background to remove");
            case CutoutLayer { CaptionGroup: { } group } cutout:
                throw new ZeyoCutException($"'{cutout.Id}' is the subject cutout of the captions under '{group}'; generate_captions gives it its method when it makes it (aiVideo, or that of the clip's cutout from text_behind_subject); refine_matte changes its edges");
            case CutoutLayer cutout:
                throw new ZeyoCutException($"'{cutout.Id}' is a subject cutout; change its method with text_behind_subject on its source ({string.Join(", ", cutout.Sources.Select(s => $"'{s}'"))}), or its edges with refine_matte");
            case IMattable mattable:
                return mattable;
            default:
                throw new ZeyoCutException($"remove_background works on image and video layers; '{layer.Id}' is a {layer.TypeName} layer");
        }
    }

    // The matte remove_background sets, with every default filled in; shrink and feather carry over
    // from the layer's earlier matte.
    public static Matte Build(IMattable layer, MatteMethod method, MatteValues values)
    {
        var name = EnumNames.ToName(method);
        switch (method)
        {
            case MatteMethod.EdgeFill or MatteMethod.AiImage when layer is not ImageLayer:
                throw new ZeyoCutException($"{name} is for image layers; use chroma or aiVideo on video layer '{layer.Id}'");
            case MatteMethod.AiVideo when layer is not VideoLayer:
                throw new ZeyoCutException($"aiVideo is for video layers; use aiImage, edgeFill or chroma on image layer '{layer.Id}'");
        }

        var given = new (string Field, object? Value)[] { ("color", values.Color), ("tolerance", values.Tolerance), ("softness", values.Softness), ("spill", values.Spill) }
            .Where(f => f.Value is not null)
            .Select(f => f.Field)
            .ToList();
        var wrong = given.Where(f => !Fields[method].Contains(f)).ToList();
        if (wrong.Count > 0)
        {
            var takes = Fields[method].Length == 0 ? "takes no settings" : $"takes {string.Join(", ", Fields[method])}";
            throw new ZeyoCutException($"{name} {takes}; {string.Join(", ", wrong)} {(wrong.Count == 1 ? "is" : "are")} not for {name}");
        }

        var matte = new Matte
        {
            Method = method,
            Shrink = layer.Matte?.Shrink ?? 0,
            Feather = layer.Matte?.Feather ?? 0,
        };
        switch (method)
        {
            case MatteMethod.Chroma:
                matte.Color = Color(values.Color ?? DefaultKey);
                matte.Tolerance = Unit(values.Tolerance ?? DefaultChromaTolerance, "tolerance");
                matte.Softness = Unit(values.Softness ?? DefaultSoftness, "softness");
                matte.Spill = Unit(values.Spill ?? DefaultSpill, "spill");
                if (KeyChroma(RgbaColor.Parse(matte.Color)) < MinKeyChroma)
                {
                    throw new ZeyoCutException($"key color {matte.Color} is too close to gray to key on; use the screen's color, e.g. #00FF00 (green) or #0000FF (blue)");
                }

                break;
            case MatteMethod.EdgeFill:
                matte.Color = values.Color is null ? null : Color(values.Color);
                matte.Tolerance = Unit(values.Tolerance ?? DefaultEdgeTolerance, "tolerance");
                break;
        }

        return matte;
    }

    public static string Set(Project project, string layerId, Matte matte)
    {
        var layer = GetMattable(project, layerId);
        var replaced = layer.Matte is { } old ? $" (was {EnumNames.ToName(old.Method)})" : "";
        layer.Matte = matte;
        return $"removed the background of '{layer.Id}' with {Describe(matte)}{replaced}";
    }

    public static string Remove(Project project, string layerId)
    {
        var layer = GetMattable(project, layerId);
        if (layer.Matte is not { } matte)
        {
            throw new ZeyoCutException($"'{layer.Id}' has no background removal to take off");
        }

        layer.Matte = null;
        return $"took the background removal ({EnumNames.ToName(matte.Method)}) off '{layer.Id}'; it shows its whole picture again";
    }

    public static MatteChange Refine(Project project, string layerId, double? shrink, double? feather)
    {
        var layer = ProjectEditor.GetLayer(project, layerId);
        if (layer is not IMattable { Matte: { } matte })
        {
            throw new ZeyoCutException($"'{layerId}' has no matte to refine; remove its background with remove_background first");
        }

        if (shrink is null && feather is null)
        {
            throw new ZeyoCutException("give shrink, feather or both");
        }

        var warnings = new List<string>();
        if (shrink is { } s)
        {
            if (!double.IsFinite(s) || s < -MaxShrink || s > MaxShrink)
            {
                throw new ZeyoCutException($"shrink must be between -{Num(MaxShrink)} and {Num(MaxShrink)} pixels (negative grows the edge); got {Num(s)}");
            }

            if (Math.Abs(s - Math.Round(s)) > 1e-9)
            {
                warnings.Add($"shrink works in whole pixels, so {Num(s)} acts as {Num(Math.Round(s, MidpointRounding.AwayFromZero))}");
            }

            matte.Shrink = s;
        }

        if (feather is { } f)
        {
            if (!double.IsFinite(f) || f < 0 || f > MaxFeather)
            {
                throw new ZeyoCutException($"feather must be between 0 and {Num(MaxFeather)} pixels; got {Num(f)}");
            }

            matte.Feather = f;
        }

        return new MatteChange($"refined the matte of '{layer.Id}': {Edge(matte)}", warnings);
    }

    public static string Describe(Matte matte)
    {
        var name = EnumNames.ToName(matte.Method);
        var settings = matte.Method switch
        {
            MatteMethod.Chroma => $"key {matte.Color}, tolerance {Num(matte.Tolerance ?? 0)}, softness {Num(matte.Softness ?? 0)}, spill {Num(matte.Spill ?? 0)}",
            MatteMethod.EdgeFill => $"background {matte.Color ?? "from the border"}, tolerance {Num(matte.Tolerance ?? 0)}",
            _ => "",
        };
        var edge = matte.Shrink != 0 || matte.Feather != 0 ? $"; {Edge(matte)}" : "";
        return settings.Length == 0 ? name + edge : $"{name} ({settings}){edge}";
    }

    public static string Short(Matte matte) =>
        $"matte({EnumNames.ToName(matte.Method)}{(matte.Shrink != 0 ? $", shrink {Num(matte.Shrink)}" : "")}{(matte.Feather != 0 ? $", feather {Num(matte.Feather)}" : "")})";

    // A cutout's matte is checked against its source (subject): the methods depend on its kind.
    public static void Validate(IMattable layer, string prefix, List<string> errors, Layer? subject = null)
    {
        if (layer.Matte is not { } matte)
        {
            return;
        }

        var name = EnumNames.ToName(matte.Method);
        var kind = subject ?? layer as Layer;
        if (kind is ImageLayer { Drawings.Count: 0 })
        {
            errors.Add($"{prefix}: a null layer (no drawings) can't have a matte");
        }

        if (matte.Method is MatteMethod.EdgeFill or MatteMethod.AiImage && kind is not ImageLayer)
        {
            errors.Add($"{prefix}: matte method {name} is for image layers");
        }

        if (matte.Method == MatteMethod.AiVideo && kind is not VideoLayer)
        {
            errors.Add($"{prefix}: matte method aiVideo is for video layers");
        }

        var allowed = Fields[matte.Method];
        foreach (var (field, value) in new (string, object?)[] { ("color", matte.Color), ("tolerance", matte.Tolerance), ("softness", matte.Softness), ("spill", matte.Spill) })
        {
            if (value is not null && !allowed.Contains(field))
            {
                errors.Add($"{prefix}: matte.{field} is not used by {name}");
            }
        }

        if (matte.Method == MatteMethod.Chroma && (matte.Color is null || matte.Tolerance is null || matte.Softness is null || matte.Spill is null))
        {
            errors.Add($"{prefix}: a chroma matte needs color, tolerance, softness and spill");
        }

        if (matte.Method == MatteMethod.EdgeFill && matte.Tolerance is null)
        {
            errors.Add($"{prefix}: an edgeFill matte needs a tolerance");
        }

        if (matte.Color is { } color)
        {
            if (!RgbaColor.TryParse(color, out var parsed))
            {
                errors.Add($"{prefix}: matte.color '{color}' is not a color; use #RRGGBB");
            }
            else if (matte.Method == MatteMethod.Chroma && KeyChroma(parsed) < MinKeyChroma)
            {
                errors.Add($"{prefix}: key color {color} is too close to gray to key on");
            }
        }

        foreach (var (field, value) in new (string, double?)[] { ("tolerance", matte.Tolerance), ("softness", matte.Softness), ("spill", matte.Spill) })
        {
            if (value is { } v && (!double.IsFinite(v) || v < 0 || v > 1))
            {
                errors.Add($"{prefix}: matte.{field} must be between 0 and 1 (got {v})");
            }
        }

        if (!double.IsFinite(matte.Shrink) || Math.Abs(matte.Shrink) > MaxShrink)
        {
            errors.Add($"{prefix}: matte.shrink must be between -{Num(MaxShrink)} and {Num(MaxShrink)} (got {matte.Shrink})");
        }

        if (!double.IsFinite(matte.Feather) || matte.Feather < 0 || matte.Feather > MaxFeather)
        {
            errors.Add($"{prefix}: matte.feather must be between 0 and {Num(MaxFeather)} (got {matte.Feather})");
        }
    }

    // Length of the color's chroma (Cb, Cr after BT.709), 0 for grays and about 0.6 for pure green.
    public static double KeyChroma(RgbaColor color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var cb = (b - y) / 1.8556;
        var cr = (r - y) / 1.5748;
        return Math.Sqrt(cb * cb + cr * cr);
    }

    private static string Edge(Matte matte) =>
        $"shrink {Num(matte.Shrink)} px{(matte.Shrink < 0 ? " (grows)" : "")}, feather {Num(matte.Feather)} px";

    private static string Color(string text)
    {
        if (!RgbaColor.TryParse(text, out var color))
        {
            throw new ZeyoCutException($"'{text}' is not a color; use #RRGGBB");
        }

        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static double Unit(double value, string name) =>
        double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new ZeyoCutException($"{name} must be between 0 and 1 (got {Num(value)})");

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
