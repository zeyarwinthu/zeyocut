// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

public enum Easing
{
    Linear,
    Hold,
    EaseIn,
    EaseOut,
    EaseInOut,
    EaseOutBack,
    EaseOutBounce,
    EaseOutElastic,

    // Its curve is the keyframe's Bezier: CSS's cubic-bezier(x1, y1, x2, y2).
    CubicBezier
}
