// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

// A clip's grade (set_color), drawn before its saturation, contrast and brightness: exposure in
// stops; temperature (blue to orange) and tint (green to magenta), the white balance; highlights
// and shadows darken or brighten the light and dark parts. Each is 0 for the picture as it was
// shot; all but exposure run from -1 to 1.
public sealed class ColorGrade
{
    public AnimatedValue Exposure { get; set; } = new(0);
    public AnimatedValue Highlights { get; set; } = new(0);
    public AnimatedValue Shadows { get; set; } = new(0);
    public AnimatedValue Temperature { get; set; } = new(0);
    public AnimatedValue Tint { get; set; } = new(0);
}
