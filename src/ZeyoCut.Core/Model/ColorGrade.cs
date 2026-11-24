// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

// A clip's grade (set_color), drawn before its saturation, contrast and brightness: exposure in
// stops; temperature (blue to orange) and tint (green to magenta), the white balance; highlights
// and shadows darken or brighten the light and dark parts. Each is 0 for the picture as it was
// shot; all but exposure run from -1 to 1. Then the color wheels, each only once it's set: lift
// moves the dark parts of each of red, green and blue, gamma the middle and gain the light parts.
// A look (a 3D LUT) is drawn last, after the saturation, contrast and brightness.
public sealed class ColorGrade
{
    public AnimatedValue Exposure { get; set; } = new(0);
    public AnimatedValue Highlights { get; set; } = new(0);
    public AnimatedValue Shadows { get; set; } = new(0);
    public AnimatedValue Temperature { get; set; } = new(0);
    public AnimatedValue Tint { get; set; } = new(0);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ColorWheel? Lift { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ColorWheel? Gamma { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ColorWheel? Gain { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ColorLook? Look { get; set; }
}

// A color wheel: how much it moves red, green and blue, each -1 to 1 (0 as shot).
public sealed class ColorWheel
{
    public AnimatedValue Red { get; set; } = new(0);
    public AnimatedValue Green { get; set; } = new(0);
    public AnimatedValue Blue { get; set; } = new(0);
}

// A look: a LUT asset (a .cube file) laid over the clip, by an amount from 0 (none) to 1.
public sealed class ColorLook
{
    public string Asset { get; set; } = "";
    public AnimatedValue Amount { get; set; } = new(1);
}
