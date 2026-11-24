// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Editor;

// One of the Color view's sliders: a set_color control in the units the view shows. Exposure is in
// stops; contrast and saturation are their change from as shot in percent (1.12 shows +12); the
// others are percent of their reach (0.35 shows +35). The slider runs from Min to Max of those
// units with as shot in the middle; a value typed past them (contrast up to +300) shows with the
// knob at the end.
public sealed record ColorSlider(ColorControl Control, string Label, double Min, double Max, string Tip)
{
    public string Name => Control.Name;

    public double Factor => Control.Name == "exposure" ? 1 : 100;

    public double Shown(double value) => (value - Control.Neutral) * Factor;

    public double ValueOf(double shown) => shown / Factor + Control.Neutral;
}

// A slider as the view shows it for a clip at the playhead.
public sealed record ColorRow(ColorSlider Slider, double Value, string Readout, bool Keyed);

// The Color view: which clip it grades, its controls' values, and the edits its sliders and buttons
// make, which are set_color's (SetColorOperation) or, for a value with keyframes, a keyframe at the
// playhead as the inspector sets one (ValueEditor.Set).
public static class ColorPanel
{
    public const char Minus = '−';

    public static readonly IReadOnlyList<ColorSlider> Sliders =
    [
        new(ColorEditor.Control("exposure"), "Exposure", -3, 3, "Stops of light: +1 doubles it, −1 halves it"),
        new(ColorEditor.Control("contrast"), "Contrast", -100, 100, "Around mid gray: −100 is flat gray; type up to +300"),
        new(ColorEditor.Control("highlights"), "Highlights", -100, 100, "Darkens or brightens the light parts; black and white stay where they are"),
        new(ColorEditor.Control("shadows"), "Shadows", -100, 100, "Deepens or lifts the dark parts; black and white stay where they are"),
        new(ColorEditor.Control("temperature"), "Temperature", -100, 100, "Bluer to warmer"),
        new(ColorEditor.Control("tint"), "Tint", -100, 100, "Greener to more magenta"),
        new(ColorEditor.Control("saturation"), "Saturation", -100, 100, "−100 is black and white; type up to +300"),
    ];

    public static ColorSlider Slider(string name) => Sliders.First(s => s.Name == name);

    // "+0.3", "+12", "−20" or "0".
    public static string Readout(ColorSlider slider, double value)
    {
        var shown = slider.Shown(value);
        var text = Math.Abs(shown).ToString(slider.Factor == 1 ? "0.##" : "0", CultureInfo.InvariantCulture);
        return text == "0" ? "0" : (shown < 0 ? Minus : '+') + text;
    }

    // The values a slider takes, as its readout shows them: "−3 to +3", "−100 to +300".
    public static string Range(ColorSlider slider) => $"{Readout(slider, slider.Control.Min)} to {Readout(slider, slider.Control.Max)}";

    // A value typed in a slider's readout, in its units, with or without its sign or a %; null when
    // it isn't a number set_color takes.
    public static double? Parse(ColorSlider slider, string text)
    {
        var cleaned = text.Trim().Replace(Minus, '-').TrimEnd('%').Trim();
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out var shown)
            && !double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out shown))
        {
            return null;
        }

        var value = slider.ValueOf(shown);
        const double slack = 1e-9;
        return double.IsFinite(value) && value >= slider.Control.Min - slack && value <= slider.Control.Max + slack
            ? Math.Clamp(value, slider.Control.Min, slider.Control.Max)
            : null;
    }

    // A video, or a picture with drawings: what the view grades.
    public static bool Gradable(Layer? layer) => layer is VideoLayer or ImageLayer { Drawings.Count: > 0 };

    // The clip the view grades: the selected layer when it's a video or a picture, else the one in
    // front at the playhead; null when there's neither.
    public static string? Target(Project project, string? selected, double time)
    {
        if (selected is not null && Gradable(project.FindLayer(selected)))
        {
            return selected;
        }

        return project.Layers
            .Select((layer, order) => (Layer: layer, Order: order))
            .Where(l => Gradable(l.Layer) && l.Layer.Visible && SceneEvaluator.IsActive(l.Layer, time, project.Duration))
            .OrderByDescending(l => ((VisualLayer)l.Layer).ZIndex)
            .ThenByDescending(l => l.Order)
            .Select(l => l.Layer.Id)
            .FirstOrDefault();
    }

    public static IReadOnlyList<ColorRow> Rows(Project project, string layerId, double time)
    {
        var layer = ColorEditor.GetColorLayer(project, layerId);
        return Sliders.Select(slider =>
        {
            var value = ColorEditor.At(layer, slider.Control, time);
            return new ColorRow(slider, value, Readout(slider, value), ColorEditor.Value(layer, slider.Control) is { Keyframes.Count: > 0 });
        }).ToList();
    }

    // The edit a slider makes: the value as set_color sets it, or a keyframe at the playhead when
    // the value has keyframes.
    public static EditOperation Change(Project project, string layerId, ColorSlider slider, double value, double time)
    {
        var layer = ColorEditor.GetColorLayer(project, layerId);
        return ColorEditor.Value(layer, slider.Control) is { Keyframes.Count: > 0 }
            ? ValueEditor.Set(project, layerId, new Dictionary<string, double> { [slider.Control.Property] = value }, time)
            : new SetColorOperation(layerId, new ColorChange(new Dictionary<string, double> { [slider.Name] = value }));
    }

    public static EditOperation Reset(string layerId) => new SetColorOperation(layerId, new ColorChange(new Dictionary<string, double>(), Reset: true));

    public static EditOperation Paste(string layerId, string from) => new SetColorOperation(layerId, new ColorChange(new Dictionary<string, double>(), From: from));

    public static EditOperation ApplyToFootage(string layerId) => new SetColorOperation(layerId, new ColorChange(new Dictionary<string, double>(), SameFootage: true));

    // A copy of the project with an edit made, for the preview to draw while a slider moves.
    public static Project Trial(Project project, EditOperation operation)
    {
        var copy = ProjectJson.Clone(project);
        operation.Apply(copy);
        return copy;
    }

    // The project with the clip's color as shot, for the viewer's Before; the project itself when
    // the clip is as shot.
    public static Project Before(Project project, string layerId)
    {
        if (!ColorEditor.Graded(ColorEditor.GetColorLayer(project, layerId)))
        {
            return project;
        }

        var copy = ProjectJson.Clone(project);
        ColorEditor.SetColor(copy, layerId, new ColorChange(new Dictionary<string, double>(), Reset: true));
        return copy;
    }

    // "Apply to all street clips": the button that gives the clip's color to every other clip of its
    // footage.
    public static string ApplyLabel(Project project, string layerId) =>
        ColorEditor.FootageName(ColorEditor.GetColorLayer(project, layerId)) is { } footage ? $"Apply to all {footage} clips" : "Apply to all clips of this footage";

    public static IReadOnlyList<string> Others(Project project, string layerId) => [.. ColorEditor.SameFootage(project, layerId).Select(l => l.Id)];
}
