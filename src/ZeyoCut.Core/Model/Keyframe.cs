// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

public sealed class Keyframe
{
    public double Time { get; set; }
    public double Value { get; set; }
    public Easing Easing { get; set; } = Easing.Linear;

    // x1, y1, x2, y2 of a cubicBezier easing; saved only with that easing, and only read with it.
    public double[]? Bezier { get; set; }
}

public sealed class DrawingKeyframe
{
    public double Time { get; set; }
    public string Value { get; set; } = "";
}
