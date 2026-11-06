// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

public sealed class Keyframe
{
    public double Time { get; set; }
    public double Value { get; set; }
    public Easing Easing { get; set; } = Easing.Linear;
}

public sealed class DrawingKeyframe
{
    public double Time { get; set; }
    public string Value { get; set; } = "";
}
