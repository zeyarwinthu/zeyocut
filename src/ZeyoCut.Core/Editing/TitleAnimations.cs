// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// How a title (or any picture layer) comes in at its start or goes out at its end.
public enum TitleAnimation
{
    Fade,
    Pop,
    SlideUp
}

public enum TitleSide
{
    In,
    Out
}

// The Fade, Pop and Slide up presets, baked into keyframes on the layer's opacity, scale and y, as
// add_text_layer's animateIn and animateOut add them. In plays from the layer's start, out up to its
// end, each for half a second or a third of the layer's length when that's shorter, so the two never
// meet. A preset takes the place of the keyframes those four values have in its time; the values it
// comes in to and goes out from are the layer's own there.
public static class TitleAnimations
{
    // The longest a preset plays.
    public const double Window = 0.5;

    // How far Slide up moves, as a part of the canvas height.
    public const double SlideDistance = 0.05;

    private const double Tolerance = 1e-6;

    private static readonly string[] Properties = ["opacity", "scaleX", "scaleY", "y"];

    // Puts a preset on one side of a layer, or with null takes the side's keyframes away (the values
    // keep what they are just inside it).
    public static string Set(Project project, string? layerId, TitleSide side, TitleAnimation? animation)
    {
        var layer = ProjectEditor.GetLayer(project, layerId);
        if (layer is not VisualLayer visual)
        {
            throw new ZeyoCutException($"'{layer.Id}' is not a text, picture or video layer; only those come in and go out");
        }

        var (t0, t1) = Times(project, visual, side);
        if (t1 - t0 <= Tolerance)
        {
            throw new ZeyoCutException($"'{layer.Id}' is too short to animate");
        }

        var inside = side == TitleSide.In ? t1 : t0;
        var values = Properties.ToDictionary(p => p, p => AnimatableProperties.GetNumeric(visual, p), StringComparer.Ordinal);
        var steady = values.ToDictionary(p => p.Key, p => Interpolator.Evaluate(p.Value, inside), StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            value.Keyframes.RemoveAll(k => k.Time >= t0 - Tolerance && k.Time <= t1 + Tolerance);
            if (value.Keyframes.Count == 0)
            {
                value.Value = steady[name];
            }
        }

        var coming = side == TitleSide.In;
        var enter = coming ? Easing.EaseOut : Easing.EaseIn;
        switch (animation)
        {
            case TitleAnimation.Fade:
                Ramp(values["opacity"], t0, t1, coming ? (0, steady["opacity"]) : (steady["opacity"], 0), enter);
                break;
            case TitleAnimation.Pop:
                foreach (var scale in new[] { "scaleX", "scaleY" })
                {
                    Ramp(values[scale], t0, t1, coming ? (0, steady[scale]) : (steady[scale], 0), coming ? Easing.EaseOutBack : Easing.EaseIn);
                }

                break;
            case TitleAnimation.SlideUp:
                var distance = Math.Round(project.Height * SlideDistance, 1);
                var y = steady["y"];
                Ramp(values["y"], t0, t1, coming ? (y + distance, y) : (y, y - distance), enter);
                Ramp(values["opacity"], t0, t1, coming ? (0, steady["opacity"]) : (steady["opacity"], 0), enter);
                break;
        }

        var what = animation is { } chosen ? Name(chosen) : "no animation";
        return $"'{layer.Id}' {(coming ? "comes in" : "goes out")} with {what} over {Num(t0)}–{Num(t1)}s";
    }

    // The preset a side of the layer has: the one that, put on again, changes nothing. Null when the
    // side has keyframes of its own, or none (Keyed says which).
    public static TitleAnimation? Detect(Project project, VisualLayer layer, TitleSide side)
    {
        foreach (var animation in Enum.GetValues<TitleAnimation>())
        {
            var copy = ProjectJson.Clone(project);
            try
            {
                Set(copy, layer.Id, side, animation);
            }
            catch (ZeyoCutException)
            {
                return null;
            }

            var again = (VisualLayer)copy.FindLayer(layer.Id)!;
            if (Properties.All(p => Same(AnimatableProperties.GetNumeric(layer, p), AnimatableProperties.GetNumeric(again, p))))
            {
                return animation;
            }
        }

        return null;
    }

    // Whether the side's time has keyframes on the values a preset moves.
    public static bool Keyed(Project project, VisualLayer layer, TitleSide side)
    {
        var (t0, t1) = Times(project, layer, side);
        return Properties.Any(p => AnimatableProperties.GetNumeric(layer, p).Keyframes.Any(k => k.Time >= t0 - Tolerance && k.Time <= t1 + Tolerance));
    }

    // add_text_layer's text: the style, when there is one, filling in what the options leave out,
    // then the presets it comes in and goes out with.
    public static TextLayer AddText(Project project, string projectDirectory, Library? library, TextStyle? style, TextLayerOptions options, TitleAnimation? animateIn, TitleAnimation? animateOut, ITextMeasurer? measurer)
    {
        var styled = style is null ? options
            : TextStyles.Defaults(project, projectDirectory, library ?? throw new ZeyoCutException("a style needs the library it's from"), style, options);
        var text = ProjectEditor.AddTextLayer(project, styled, measurer);
        if (animateIn is not null)
        {
            Set(project, text.Id, TitleSide.In, animateIn);
        }

        if (animateOut is not null)
        {
            Set(project, text.Id, TitleSide.Out, animateOut);
        }

        return text;
    }

    public static string Name(TitleAnimation animation) => animation switch
    {
        TitleAnimation.Fade => "Fade",
        TitleAnimation.Pop => "Pop",
        _ => "Slide up",
    };

    // When a side plays: from the layer's start, or up to its end (the project's end when it has none).
    public static (double From, double To) Times(Project project, VisualLayer layer, TitleSide side)
    {
        var (from, to) = (layer.Start, layer is VideoLayer video ? video.TimelineEnd : layer.End ?? project.Duration);
        var length = Math.Min(Window, (to - from) / 3);
        return side == TitleSide.In ? (from, from + length) : (to - length, to);
    }

    private static void Ramp(AnimatedValue value, double t0, double t1, (double From, double To) values, Easing easing)
    {
        value.Keyframes.Add(new Keyframe { Time = KeyframeWindow.Round(t0), Value = values.From, Easing = easing });
        value.Keyframes.Add(new Keyframe { Time = KeyframeWindow.Round(t1), Value = values.To, Easing = Easing.Linear });
        value.Keyframes = value.Keyframes.OrderBy(k => k.Time).ToList();
    }

    private static bool Same(AnimatedValue a, AnimatedValue b) =>
        Math.Abs(a.Value - b.Value) < Tolerance
        && a.Keyframes.Count == b.Keyframes.Count
        && a.Keyframes.Zip(b.Keyframes).All(k => Math.Abs(k.First.Time - k.Second.Time) < Tolerance && Math.Abs(k.First.Value - k.Second.Value) < Tolerance && k.First.Easing == k.Second.Easing);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
