// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Tests.Core;

public class InterpolationTests
{
    private static AnimatedValue Track(double staticValue, params (double Time, double Value, Easing Easing)[] keys) => new(staticValue)
    {
        Keyframes = keys.Select(k => new Keyframe { Time = k.Time, Value = k.Value, Easing = k.Easing }).ToList(),
    };

    [Fact]
    public void NoKeyframesUsesStaticValue()
    {
        Assert.Equal(42, Interpolator.Evaluate(new AnimatedValue(42), 1.234));
    }

    [Fact]
    public void BeforeFirstKeyframeUsesFirstValue()
    {
        var track = Track(99, (1, 10, Easing.Linear), (2, 20, Easing.Linear));
        Assert.Equal(10, Interpolator.Evaluate(track, 0));
        Assert.Equal(10, Interpolator.Evaluate(track, 0.999));
    }

    [Fact]
    public void AfterLastKeyframeUsesLastValue()
    {
        var track = Track(99, (1, 10, Easing.Linear), (2, 20, Easing.EaseOutBack));
        Assert.Equal(20, Interpolator.Evaluate(track, 2));
        Assert.Equal(20, Interpolator.Evaluate(track, 50));
    }

    [Fact]
    public void SingleKeyframeIsConstant()
    {
        var track = Track(0, (1, 7, Easing.EaseIn));
        Assert.Equal(7, Interpolator.Evaluate(track, 0));
        Assert.Equal(7, Interpolator.Evaluate(track, 1));
        Assert.Equal(7, Interpolator.Evaluate(track, 3));
    }

    [Fact]
    public void LinearSegment()
    {
        var track = Track(0, (1, 10, Easing.Linear), (2, 20, Easing.Linear));
        Assert.Equal(15, Interpolator.Evaluate(track, 1.5), 1e-12);
        Assert.Equal(10, Interpolator.Evaluate(track, 1));
    }

    [Fact]
    public void EasingOfStartKeyframeControlsTheSegment()
    {
        var track = Track(0, (0, 0, Easing.EaseIn), (1, 100, Easing.Linear), (2, 200, Easing.Hold));
        Assert.Equal(12.5, Interpolator.Evaluate(track, 0.5), 1e-9);
        Assert.Equal(150, Interpolator.Evaluate(track, 1.5), 1e-9);
    }

    [Fact]
    public void HoldKeepsValueUntilNextKeyframe()
    {
        var track = Track(0, (0, 10, Easing.Hold), (1, 20, Easing.Linear));
        Assert.Equal(10, Interpolator.Evaluate(track, 0));
        Assert.Equal(10, Interpolator.Evaluate(track, 0.999));
        Assert.Equal(20, Interpolator.Evaluate(track, 1));
    }

    [Fact]
    public void OvershootEasingGoesPastTarget()
    {
        var track = Track(0, (0, 0, Easing.EaseOutBack), (1, 100, Easing.Linear));
        var peak = Enumerable.Range(0, 100).Select(i => Interpolator.Evaluate(track, i / 100.0)).Max();
        Assert.True(peak > 100);
    }

    [Fact]
    public void DrawingTrackAlwaysHolds()
    {
        var track = new DrawingTrack
        {
            Value = "ignored",
            Keyframes =
            [
                new DrawingKeyframe { Time = 1, Value = "normal" },
                new DrawingKeyframe { Time = 2, Value = "angry" },
                new DrawingKeyframe { Time = 3, Value = "shocked" },
            ],
        };

        Assert.Equal("normal", Interpolator.Evaluate(track, 0));
        Assert.Equal("normal", Interpolator.Evaluate(track, 1.99));
        Assert.Equal("angry", Interpolator.Evaluate(track, 2));
        Assert.Equal("shocked", Interpolator.Evaluate(track, 10));
    }

    [Fact]
    public void DrawingTrackWithoutKeyframesUsesStaticValue()
    {
        Assert.Equal("normal", Interpolator.Evaluate(new DrawingTrack { Value = "normal" }, 5));
    }

    [Fact]
    public void FrameMathSnapsToFrames()
    {
        var project = new Project { Fps = 30, Duration = 3 };
        Assert.Equal(90, FrameMath.FrameCount(project));
        Assert.Equal(21, FrameMath.NearestFrame(project, 0.7));
        Assert.Equal(89, FrameMath.NearestFrame(project, 10));
        Assert.Equal(0.7, FrameMath.TimeOf(project, 21));
    }
}
