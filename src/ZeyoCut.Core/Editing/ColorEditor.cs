// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// One of a clip's color controls: its set_color name, the property keyframes give it, the values
// it takes and the one that leaves the picture as it was shot. The graded ones are in the clip's
// grade, which a clip has only once one of them is set.
public sealed record ColorControl(string Name, string Property, double Min, double Max, double Neutral)
{
    public bool Graded => Property.StartsWith("grade.", StringComparison.Ordinal);
}

// What set_color does to a clip, in this order: puts its color back as shot (Reset), copies
// another clip's (From), sets values by control name, and gives every other clip of its footage
// the color it then has (SameFootage).
public sealed record ColorChange(IReadOnlyDictionary<string, double> Values, string? From = null, bool Reset = false, bool SameFootage = false);

public sealed record ColorResult(string Summary, IReadOnlyList<string> Layers, IReadOnlyList<string> Warnings);

// A clip's color: its contrast and saturation and its grade (exposure, highlights, shadows,
// temperature, tint), as set_color and the editor's Color view change them. Brightness stays with
// the inspector and with fades to black, so a grade never copies it.
public static class ColorEditor
{
    public static readonly IReadOnlyList<ColorControl> Controls =
    [
        new("exposure", "grade.exposure", -3, 3, 0),
        new("contrast", "contrast", 0, 4, 1),
        new("highlights", "grade.highlights", -1, 1, 0),
        new("shadows", "grade.shadows", -1, 1, 0),
        new("temperature", "grade.temperature", -1, 1, 0),
        new("tint", "grade.tint", -1, 1, 0),
        new("saturation", "saturation", 0, 4, 1),
    ];

    public static ColorControl Control(string name) =>
        Controls.FirstOrDefault(c => string.Equals(c.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ZeyoCutException($"unknown color control '{name}'; controls: {string.Join(", ", Controls.Select(c => c.Name))}");

    public static AnimatedValue Value(ColorGrade grade, string name) => name switch
    {
        "exposure" => grade.Exposure,
        "highlights" => grade.Highlights,
        "shadows" => grade.Shadows,
        "temperature" => grade.Temperature,
        "tint" => grade.Tint,
        _ => throw new ArgumentException($"'{name}' isn't in a grade", nameof(name)),
    };

    // The control's value on a clip; a graded one has none while the clip has no grade.
    public static AnimatedValue? Value(IColorAdjustable layer, ColorControl control) => control.Name switch
    {
        "contrast" => layer.Contrast,
        "saturation" => layer.Saturation,
        _ => layer.Grade is { } grade ? Value(grade, control.Name) : null,
    };

    // The control's value at time, its neutral value when the clip has none.
    public static double At(IColorAdjustable layer, ColorControl control, double time) =>
        Value(layer, control) is { } value ? Interpolator.Evaluate(value, time) : control.Neutral;

    public static IColorAdjustable GetColorLayer(Project project, string layerId)
    {
        var layer = ProjectEditor.GetLayer(project, layerId);
        return layer as IColorAdjustable
            ?? throw new ZeyoCutException($"'{layer.Id}' is a {layer.TypeName} layer; color is for image and video layers");
    }

    // Whether the clip's color isn't as shot: it has a grade, or its contrast or saturation changed.
    public static bool Graded(IColorAdjustable layer) =>
        layer.Grade is not null || Changed(layer.Contrast, 1) || Changed(layer.Saturation, 1);

    // The footage a clip shows: a video's asset, or a picture's drawings.
    public static string? Footage(IColorAdjustable layer) => layer switch
    {
        VideoLayer video => "video:" + video.Asset,
        ImageLayer { Drawings.Count: > 0 } image => "image:" + string.Join("|", image.Drawings.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
        _ => null,
    };

    // What to call the footage: the asset a video, or a picture with one drawing, shows.
    public static string? FootageName(IColorAdjustable layer) => layer switch
    {
        VideoLayer video => video.Asset,
        ImageLayer image when image.Drawings.Values.Distinct(StringComparer.Ordinal).Count() == 1 => image.Drawings.Values.First(),
        _ => null,
    };

    // The other clips of the same footage, in the project's order.
    public static IReadOnlyList<Layer> SameFootage(Project project, string layerId)
    {
        var layer = GetColorLayer(project, layerId);
        var footage = Footage(layer);
        return footage is null
            ? []
            : project.Layers.Where(l => l.Id != layerId && l is IColorAdjustable other && Footage(other) == footage).ToList();
    }

    public static ColorResult SetColor(Project project, string layerId, ColorChange change)
    {
        var layer = GetColorLayer(project, layerId);
        var values = change.Values ?? new Dictionary<string, double>();
        if (!change.Reset && change.From is null && values.Count == 0 && !change.SameFootage)
        {
            throw new ZeyoCutException($"nothing to change: give values ({string.Join(", ", Controls.Select(c => c.Name))}), from, reset or sameFootage");
        }

        var given = new List<(ColorControl Control, double Value)>();
        foreach (var (name, value) in values)
        {
            var control = Control(name);
            if (!double.IsFinite(value) || value < control.Min || value > control.Max)
            {
                throw new ZeyoCutException($"{control.Name} must be between {Num(control.Min)} and {Num(control.Max)} (got {Num(value)})");
            }

            given.RemoveAll(g => g.Control == control);
            given.Add((control, value));
        }

        IColorAdjustable? source = null;
        if (change.From is { } from)
        {
            source = GetColorLayer(project, from);
            if (ReferenceEquals(source, layer))
            {
                throw new ZeyoCutException($"'{layerId}' can't take its own color; from names another clip");
            }
        }

        var others = change.SameFootage ? SameFootage(project, layerId) : [];
        if (change.SameFootage && others.Count == 0)
        {
            throw new ZeyoCutException(FootageName(layer) is { } footage
                ? $"'{layerId}' is the only clip of '{footage}'"
                : $"'{layerId}' shows no footage another clip shares");
        }

        var warnings = new List<string>();
        if (change.Reset)
        {
            if (KeyNames(layer) is { Count: > 0 } keyed)
            {
                warnings.Add($"the keyframes of '{layerId}''s {string.Join(", ", keyed)} were taken away");
            }

            layer.Contrast = new AnimatedValue(1);
            layer.Saturation = new AnimatedValue(1);
            layer.Grade = null;
        }

        if (source is not null)
        {
            Copy(source, layer, warnings);
        }

        foreach (var (control, value) in given)
        {
            if (control.Graded)
            {
                layer.Grade ??= new ColorGrade();
            }

            var target = Value(layer, control)!;
            if (target.Keyframes.Count > 0)
            {
                warnings.Add($"{control.Property} has {target.Keyframes.Count} keyframe(s), so the value you set is only used after clear_keyframes");
            }

            target.Value = value;
        }

        Tidy(layer);
        foreach (var other in others)
        {
            Copy(layer, (IColorAdjustable)other, warnings);
        }

        var summary = source is not null ? $"copied the color of '{change.From}' to '{layerId}': {Describe(layer)}"
            : change.Reset && given.Count == 0 ? $"put the color of '{layerId}' back as shot"
            : $"color of '{layerId}': {Describe(layer)}";
        if (others.Count > 0)
        {
            summary += $"; the same on {Ids.List(others.Select(o => o.Id))} (every clip of '{FootageName(layer) ?? "its footage"}')";
        }

        return new ColorResult(summary, [layerId, .. others.Select(o => o.Id)], warnings);
    }

    // The controls that aren't neutral, with their values ("exposure 0.3, saturation 1.2"), or
    // "as shot"; a value with keyframes says how many. With grade, only the grade's.
    public static string Describe(IColorAdjustable layer, bool grade = false)
    {
        var parts = new List<string>();
        foreach (var control in Controls.Where(c => !grade || c.Graded))
        {
            if (Value(layer, control) is not { } value)
            {
                continue;
            }

            if (value.Keyframes.Count > 0)
            {
                parts.Add($"{control.Name} animated ({value.Keyframes.Count} keyframes)");
            }
            else if (Math.Abs(value.Value - control.Neutral) > 1e-9)
            {
                parts.Add($"{control.Name} {Num(value.Value)}");
            }
        }

        return parts.Count == 0 ? "as shot" : string.Join(", ", parts);
    }

    // The source's color as fixed values onto the target; a keyframed source can't be copied, and
    // the target's keyframes on those values go.
    private static void Copy(IColorAdjustable source, IColorAdjustable target, List<string> warnings)
    {
        var animated = KeyNames(source);
        if (animated.Count > 0)
        {
            throw new ZeyoCutException($"'{IdOf(source)}' animates its {string.Join(", ", animated)} with keyframes; its color is copied as fixed values, so clear_keyframes first");
        }

        if (KeyNames(target) is { Count: > 0 } dropped)
        {
            warnings.Add($"the keyframes of '{IdOf(target)}''s {string.Join(", ", dropped)} were taken away");
        }

        target.Contrast = new AnimatedValue(source.Contrast.Value);
        target.Saturation = new AnimatedValue(source.Saturation.Value);
        target.Grade = source.Grade is { } grade
            ? new ColorGrade
            {
                Exposure = new AnimatedValue(grade.Exposure.Value),
                Highlights = new AnimatedValue(grade.Highlights.Value),
                Shadows = new AnimatedValue(grade.Shadows.Value),
                Temperature = new AnimatedValue(grade.Temperature.Value),
                Tint = new AnimatedValue(grade.Tint.Value),
            }
            : null;
    }

    // A grade left neutral and without keyframes goes, so a clip as shot has none.
    private static void Tidy(IColorAdjustable layer)
    {
        if (layer.Grade is { } grade && Controls.Where(c => c.Graded).All(c => Value(grade, c.Name) is { Keyframes.Count: 0 } v && Math.Abs(v.Value) < 1e-12))
        {
            layer.Grade = null;
        }
    }

    private static List<string> KeyNames(IColorAdjustable layer) =>
        Controls.Where(c => Value(layer, c) is { Keyframes.Count: > 0 }).Select(c => c.Name).ToList();

    private static bool Changed(AnimatedValue value, double neutral) => value.Keyframes.Count > 0 || Math.Abs(value.Value - neutral) > 1e-9;

    private static string IdOf(IColorAdjustable layer) => ((Layer)layer).Id;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
