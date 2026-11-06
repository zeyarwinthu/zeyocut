// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public enum EffectKind
{
    Shadow,
    Outline,
    Blur,
    Tint
}

public sealed record EffectValues(string? Color, double? X, double? Y, double? Blur, double? Opacity, double? Width, double? Amount);

public sealed record MaskValues(MaskType? Type, string? Asset, double? X, double? Y, double? Width, double? Height, double? Feather, bool? Invert);

public sealed record PathPointSpec(double Time, double X, double Y, double? InX, double? InY, double? OutX, double? OutY, string? Easing);

public sealed record EffectChange(string Summary, IReadOnlyList<string> Warnings);

// set_effect, set_mask and set_motion_path.
public static class EffectEditor
{
    public const double MaxBlur = 500;
    public const double MaxOutline = 100;
    public const double MaxFeather = 1000;
    public const int MaxPathPoints = 1000;

    private static readonly Dictionary<EffectKind, string[]> Fields = new()
    {
        [EffectKind.Shadow] = ["color", "x", "y", "blur", "opacity"],
        [EffectKind.Outline] = ["color", "width"],
        [EffectKind.Blur] = ["x", "y"],
        [EffectKind.Tint] = ["color", "amount"],
    };

    public static EffectChange SetEffect(Project project, string layerId, EffectKind kind, EffectValues values, bool remove)
    {
        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        var name = EnumNames.ToName(kind);
        var given = new (string Field, object? Value)[]
            {
                ("color", values.Color), ("x", values.X), ("y", values.Y), ("blur", values.Blur),
                ("opacity", values.Opacity), ("width", values.Width), ("amount", values.Amount),
            }
            .Where(f => f.Value is not null)
            .Select(f => f.Field)
            .ToList();
        if (remove)
        {
            if (given.Count > 0)
            {
                throw new ZeyoCutException($"remove=true takes no values (got {string.Join(", ", given)})");
            }

            var keys = AnimatableProperties.Numeric(layer).Where(p => p.Name.StartsWith(name + ".", StringComparison.Ordinal)).Sum(p => p.Value.Keyframes.Count);
            var had = kind switch
            {
                EffectKind.Shadow => layer.Shadow is not null,
                EffectKind.Outline => layer.Outline is not null,
                EffectKind.Blur => layer.Blur is not null,
                _ => layer.Tint is not null,
            };
            if (!had)
            {
                var effects = Effects(layer);
                throw new ZeyoCutException($"'{layer.Id}' has no {name} effect; its effects: {(effects.Count == 0 ? "(none)" : string.Join(", ", effects))}");
            }

            switch (kind)
            {
                case EffectKind.Shadow:
                    layer.Shadow = null;
                    break;
                case EffectKind.Outline:
                    layer.Outline = null;
                    break;
                case EffectKind.Blur:
                    layer.Blur = null;
                    break;
                default:
                    layer.Tint = null;
                    break;
            }

            return new EffectChange($"removed the {name} effect from '{layer.Id}'" + (keys > 0 ? $" with its {keys} keyframe(s)" : ""), []);
        }

        var wrong = given.Where(f => !Fields[kind].Contains(f)).ToList();
        if (wrong.Count > 0)
        {
            throw new ZeyoCutException($"{name} takes {string.Join(", ", Fields[kind])}; {string.Join(", ", wrong)} {(wrong.Count == 1 ? "is" : "are")} not for {name}");
        }

        var warnings = new List<string>();
        bool created;
        switch (kind)
        {
            case EffectKind.Shadow:
                created = layer.Shadow is null;
                var shadow = layer.Shadow ??= new DropShadow();
                shadow.Color = values.Color?.Trim() ?? shadow.Color;
                Set(shadow.X, values.X, "shadow.x", warnings);
                Set(shadow.Y, values.Y, "shadow.y", warnings);
                Set(shadow.Blur, values.Blur, "shadow.blur", warnings);
                Set(shadow.Opacity, values.Opacity, "shadow.opacity", warnings);
                break;
            case EffectKind.Outline:
                created = layer.Outline is null;
                var outline = layer.Outline ??= new Outline();
                outline.Color = values.Color?.Trim() ?? outline.Color;
                Set(outline.Width, values.Width, "outline.width", warnings);
                break;
            case EffectKind.Blur:
                created = layer.Blur is null;
                var blur = layer.Blur ??= new BlurEffect();
                Set(blur.X, values.X, "blur.x", warnings);
                Set(blur.Y, values.Y, "blur.y", warnings);
                break;
            default:
                created = layer.Tint is null;
                if (created && values.Color is null)
                {
                    throw new ZeyoCutException("a new tint needs a color, e.g. #FF0000");
                }

                var tint = layer.Tint ??= new Tint();
                tint.Color = values.Color?.Trim() ?? tint.Color;
                Set(tint.Amount, values.Amount, "tint.amount", warnings);
                break;
        }

        return new EffectChange($"{(created ? "added" : "updated")} {name} on '{layer.Id}': {Describe(layer, kind)}", warnings);
    }

    public static EffectChange SetMask(Project project, string layerId, MaskValues values, bool remove, ITextMeasurer? measurer)
    {
        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        var given = values.Type is not null || values.Asset is not null || values.X is not null || values.Y is not null
            || values.Width is not null || values.Height is not null || values.Feather is not null || values.Invert is not null;
        if (remove)
        {
            if (given)
            {
                throw new ZeyoCutException("remove=true takes no other values");
            }

            if (layer.Mask is not { } old)
            {
                throw new ZeyoCutException($"'{layer.Id}' has no mask");
            }

            var keys = AnimatableProperties.Numeric(layer).Where(p => p.Name.StartsWith("mask.", StringComparison.Ordinal)).Sum(p => p.Value.Keyframes.Count);
            layer.Mask = null;
            return new EffectChange($"removed the {EnumNames.ToName(old.Type)} mask from '{layer.Id}'" + (keys > 0 ? $" with its {keys} keyframe(s)" : ""), []);
        }

        if (!given)
        {
            throw new ZeyoCutException("pass type, asset, x, y, width, height, feather or invert (or remove=true)");
        }

        var warnings = new List<string>();
        var mask = layer.Mask;
        var created = mask is null;
        if (mask is null)
        {
            if (values.Type is not { } type)
            {
                throw new ZeyoCutException($"a new mask needs a type: {string.Join(", ", EnumNames.All<MaskType>())}");
            }

            mask = new Mask { Type = type };
            if (ProjectChecker.ContentBox(project, layer, layer.Start, measurer) is { } box)
            {
                (mask.X.Value, mask.Y.Value, mask.Width.Value, mask.Height.Value) = (box.Left, box.Top, box.Width, box.Height);
            }
            else if (type == MaskType.Image && values.Asset is { } id && project.FindAsset(id) is { Width: { } w, Height: { } h })
            {
                (mask.Width.Value, mask.Height.Value) = (w, h);
            }
            else if (values.Width is null || values.Height is null)
            {
                throw new ZeyoCutException($"the size of '{layer.Id}' isn't known, so the mask can't fit it; pass width and height");
            }

            layer.Mask = mask;
        }

        if (values.Type is { } newType)
        {
            mask.Type = newType;
            if (newType != MaskType.Image)
            {
                mask.Asset = null;
            }
        }

        if (values.Asset is { } assetId)
        {
            if (mask.Type != MaskType.Image)
            {
                throw new ZeyoCutException($"asset is only for image masks; this one is a {EnumNames.ToName(mask.Type)} (pass type=image)");
            }

            mask.Asset = ProjectEditor.GetAsset(project, assetId, AssetType.Image).Id;
        }

        if (mask.Type == MaskType.Image && mask.Asset is null)
        {
            throw new ZeyoCutException("an image mask needs asset: the id of an image asset (its alpha is the mask)");
        }

        Set(mask.X, values.X, "mask.x", warnings);
        Set(mask.Y, values.Y, "mask.y", warnings);
        Set(mask.Width, values.Width, "mask.width", warnings);
        Set(mask.Height, values.Height, "mask.height", warnings);
        Set(mask.Feather, values.Feather, "mask.feather", warnings);
        mask.Invert = values.Invert ?? mask.Invert;
        return new EffectChange($"{(created ? "added" : "updated")} the mask on '{layer.Id}': {DescribeMask(mask)}", warnings);
    }

    public static EffectChange SetMotionPath(Project project, string layerId, IReadOnlyList<PathPointSpec>? points, bool autoOrient, bool remove)
    {
        var layer = ProjectEditor.GetVisualLayer(project, layerId);
        if (remove)
        {
            if (points is { Count: > 0 })
            {
                throw new ZeyoCutException("remove=true takes no points");
            }

            if (layer.MotionPath is null)
            {
                throw new ZeyoCutException($"'{layer.Id}' has no motion path");
            }

            layer.MotionPath = null;
            return new EffectChange($"removed the motion path from '{layer.Id}'; it stands at x={Num(layer.X.Value)} y={Num(layer.Y.Value)} again", []);
        }

        if (points is null || points.Count < 2)
        {
            throw new ZeyoCutException("a motion path needs at least 2 points: [{time, x, y}, ...]");
        }

        if (points.Count > MaxPathPoints)
        {
            throw new ZeyoCutException($"a motion path takes at most {MaxPathPoints} points (got {points.Count})");
        }

        var keys = new List<Keyframe>();
        var path = new MotionPath { AutoOrient = autoOrient };
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i] ?? throw new ZeyoCutException($"point {i} is empty");
            if (!double.IsFinite(point.Time) || point.Time < 0)
            {
                throw new ZeyoCutException($"point {i}: time must be >= 0 (got {Num(point.Time)})");
            }

            if (i > 0 && point.Time <= points[i - 1].Time + KeyframeEditor.TimeTolerance)
            {
                throw new ZeyoCutException($"point {i}: time {Num(point.Time)}s must come after point {i - 1} ({Num(points[i - 1].Time)}s)");
            }

            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ZeyoCutException($"point {i}: x and y must be finite numbers");
            }

            if (point.InX is null != point.InY is null || point.OutX is null != point.OutY is null)
            {
                throw new ZeyoCutException($"point {i}: give a handle's x and y together (inX with inY, outX with outY)");
            }

            if (new[] { point.InX, point.InY, point.OutX, point.OutY }.Any(h => h is { } v && !double.IsFinite(v)))
            {
                throw new ZeyoCutException($"point {i}: handles must be finite numbers");
            }

            var easing = string.IsNullOrWhiteSpace(point.Easing) ? Easing.Linear : EnumNames.Parse<Easing>(point.Easing, "easing");
            path.Points.Add(new PathPoint { X = point.X, Y = point.Y, InX = point.InX, InY = point.InY, OutX = point.OutX, OutY = point.OutY });
            keys.Add(new Keyframe { Time = KeyframeWindow.Round(point.Time), Value = i, Easing = easing });
        }

        path.Progress = new AnimatedValue(0) { Keyframes = keys };
        var warnings = new List<string>();
        var cleared = layer.X.Keyframes.Count + layer.Y.Keyframes.Count;
        if (cleared > 0)
        {
            warnings.Add($"cleared {cleared} x/y keyframe(s) from '{layer.Id}'; the path places it now");
        }

        layer.X.Keyframes.Clear();
        layer.Y.Keyframes.Clear();
        layer.X.Value = points[0].X;
        layer.Y.Value = points[0].Y;
        var replaced = layer.MotionPath is not null;
        layer.MotionPath = path;
        if (points[^1].Time > project.Duration)
        {
            warnings.Add($"the path runs until {Num(points[^1].Time)}s, past the project's end ({Num(project.Duration)}s)");
        }

        return new EffectChange(
            $"{(replaced ? "replaced the" : "added a")} motion path on '{layer.Id}': {DescribePath(path)}",
            warnings);
    }

    public static string PathRemovedWarning(VisualLayer layer) =>
        $"'{layer.Id}' had a motion path, which overrides x and y; it was removed so x and y apply";

    // Short names of the layer's effects, mask and path, for listings.
    public static List<string> Effects(VisualLayer layer)
    {
        var effects = new List<string>();
        if (layer.Shadow is not null)
        {
            effects.Add("shadow");
        }

        if (layer.Outline is not null)
        {
            effects.Add("outline");
        }

        if (layer.Blur is not null)
        {
            effects.Add("blur");
        }

        if (layer.Tint is not null)
        {
            effects.Add("tint");
        }

        if (layer.Mask is { } mask)
        {
            effects.Add($"mask({EnumNames.ToName(mask.Type)}{(mask.Invert ? ", inverted" : "")})");
        }

        if (layer.MotionPath is { } path)
        {
            effects.Add($"path({path.Points.Count} points{(path.AutoOrient ? ", autoOrient" : "")})");
        }

        if (layer is IMattable { Matte: { } matte })
        {
            effects.Add(MatteEditor.Short(matte));
        }

        return effects;
    }

    public static string Describe(VisualLayer layer, EffectKind kind) => kind switch
    {
        EffectKind.Shadow when layer.Shadow is { } s => $"color {s.Color}, x {Value(s.X)}, y {Value(s.Y)}, blur {Value(s.Blur)}, opacity {Value(s.Opacity)}",
        EffectKind.Outline when layer.Outline is { } o => $"color {o.Color}, width {Value(o.Width)}",
        EffectKind.Blur when layer.Blur is { } b => $"x {Value(b.X)}, y {Value(b.Y)}",
        EffectKind.Tint when layer.Tint is { } t => $"color {t.Color}, amount {Value(t.Amount)}",
        _ => "(none)",
    };

    public static string DescribeMask(Mask mask) =>
        $"{EnumNames.ToName(mask.Type)}{(mask.Asset is { } asset ? $" from '{asset}'" : "")} at x {Value(mask.X)}, y {Value(mask.Y)}, "
        + $"size {Value(mask.Width)} x {Value(mask.Height)}, feather {Value(mask.Feather)}{(mask.Invert ? ", inverted (shows the layer outside the shape)" : "")}";

    public static string DescribePath(MotionPath path)
    {
        var keys = path.Progress.Keyframes;
        var times = keys.Count > 0 ? $"{Num(keys[0].Time)}–{Num(keys[^1].Time)}s" : "not timed";
        return $"{path.Points.Count} points, {times}, {Num(Math.Round(MotionPaths.Length(path)))} px long{(path.AutoOrient ? ", turning with the path" : "")}";
    }

    private static void Set(AnimatedValue value, double? number, string name, List<string> warnings)
    {
        if (number is not { } n)
        {
            return;
        }

        if (!double.IsFinite(n))
        {
            throw new ZeyoCutException($"{name} must be a finite number");
        }

        value.Value = n;
        if (value.Keyframes.Count > 0)
        {
            warnings.Add($"{name} has {value.Keyframes.Count} keyframe(s), so the value you set is only used after clear_keyframes");
        }
    }

    private static string Value(AnimatedValue value) =>
        value.Keyframes.Count > 0 ? $"animated ({value.Keyframes.Count} keyframes)" : Num(value.Value);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
