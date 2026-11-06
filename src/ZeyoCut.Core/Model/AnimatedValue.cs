// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

public sealed class AnimatedValue
{
    public AnimatedValue()
    {
    }

    public AnimatedValue(double value)
    {
        Value = value;
    }

    public double Value { get; set; }
    public List<Keyframe> Keyframes { get; set; } = [];
}

public sealed class DrawingTrack
{
    public string Value { get; set; } = "";
    public List<DrawingKeyframe> Keyframes { get; set; } = [];
}
