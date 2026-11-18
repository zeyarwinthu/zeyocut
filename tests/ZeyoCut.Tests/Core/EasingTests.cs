// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Tests.Core;

public class EasingTests
{
    public static TheoryData<Easing> AllEasings => new(Enum.GetValues<Easing>());

    [Theory]
    [MemberData(nameof(AllEasings))]
    public void StartsAtZeroAndEndsAtOne(Easing easing)
    {
        Assert.Equal(0, EasingFunctions.Apply(easing, 0));
        Assert.Equal(1, EasingFunctions.Apply(easing, 1));
    }

    [Theory]
    [InlineData(Easing.Linear, 0.5, 0.5)]
    [InlineData(Easing.Linear, 0.25, 0.25)]
    [InlineData(Easing.Hold, 0.5, 0.0)]
    [InlineData(Easing.Hold, 0.999, 0.0)]
    [InlineData(Easing.EaseIn, 0.5, 0.125)]
    [InlineData(Easing.EaseOut, 0.5, 0.875)]
    [InlineData(Easing.EaseInOut, 0.25, 0.0625)]
    [InlineData(Easing.EaseInOut, 0.5, 0.5)]
    [InlineData(Easing.EaseInOut, 0.75, 0.9375)]
    [InlineData(Easing.EaseOutBack, 0.5, 1.0876975)]
    [InlineData(Easing.EaseOutBounce, 0.5, 0.765625)]
    [InlineData(Easing.EaseOutElastic, 0.5, 1.015625)]
    public void KnownValues(Easing easing, double t, double expected)
    {
        Assert.Equal(expected, EasingFunctions.Apply(easing, t), 1e-9);
    }

    // Worked out separately by halving the interval at 50 digits. 0.25, 0.1, 0.25, 1 is CSS's ease.
    [Theory]
    [InlineData(0.25, 0.1, 0.25, 1, 0.1, 0.094796305716043)]
    [InlineData(0.25, 0.1, 0.25, 1, 0.25, 0.408510591355396)]
    [InlineData(0.25, 0.1, 0.25, 1, 0.5, 0.802403387584857)]
    [InlineData(0.25, 0.1, 0.25, 1, 0.9, 0.994316477484556)]
    [InlineData(0, 0, 0.4, 1, 0.1, 0.211069922789738)]
    [InlineData(0, 0, 0.4, 1, 0.5, 0.765988682530208)]
    [InlineData(0, 0, 0.4, 1, 0.75, 0.941991889400904)]
    [InlineData(0.34, 1.56, 0.64, 1, 0.25, 0.816289198692472)]
    [InlineData(0.34, 1.56, 0.64, 1, 0.5, 1.087400670218698)]
    [InlineData(0.34, 1.56, 0.64, 1, 0.75, 1.059646859964033)]
    [InlineData(0.42, 0, 0.58, 1, 0.1, 0.019722453548311)]
    [InlineData(0.42, 0, 0.58, 1, 0.5, 0.5)]
    [InlineData(0.42, 0, 0.58, 1, 0.9, 0.980277546451689)]
    [InlineData(1, 0, 0, 1, 0.5, 0.5)]
    public void CubicBezierMatchesReferencePoints(double x1, double y1, double x2, double y2, double t, double expected)
    {
        Assert.Equal(expected, EasingFunctions.Apply(Easing.CubicBezier, t, [x1, y1, x2, y2]), 1e-9);
    }

    [Fact]
    public void ACubicBezierAlongTheDiagonalIsLinearAndTheNumbersCountOnlyForIt()
    {
        for (var i = 0; i <= 100; i++)
        {
            Assert.Equal(i / 100.0, EasingFunctions.Apply(Easing.CubicBezier, i / 100.0, [0.3, 0.3, 0.7, 0.7]), 1e-9);
        }

        Assert.Equal(0.125, EasingFunctions.Apply(Easing.EaseIn, 0.5, [0, 0, 0.4, 1]));
        Assert.Null(EasingFunctions.BezierProblem([0, 0, 0.4, 1]));
        Assert.Null(EasingFunctions.BezierProblem([0.2, -1.5, 0.8, 2.5]));
        Assert.Equal("bezier must be 4 numbers, [x1, y1, x2, y2] (got none)", EasingFunctions.BezierProblem(null));
        Assert.Equal("bezier must be 4 numbers, [x1, y1, x2, y2] (got [0, 1])", EasingFunctions.BezierProblem([0, 1]));
        Assert.Equal("bezier's x1 and x2 must be from 0 to 1, so the curve has one value at each time (got [1.2, 0, 0.4, 1])", EasingFunctions.BezierProblem([1.2, 0, 0.4, 1]));
        Assert.Equal("bezier's numbers must be finite (got [0, NaN, 0.4, 1])", EasingFunctions.BezierProblem([0, double.NaN, 0.4, 1]));
    }

    [Fact]
    public void EaseOutBackOvershootsByAboutTenPercent()
    {
        var peak = Enumerable.Range(0, 1001).Select(i => EasingFunctions.Apply(Easing.EaseOutBack, i / 1000.0)).Max();
        Assert.InRange(peak, 1.09, 1.11);
    }

    [Fact]
    public void EaseOutElasticOvershoots()
    {
        var peak = Enumerable.Range(0, 1001).Select(i => EasingFunctions.Apply(Easing.EaseOutElastic, i / 1000.0)).Max();
        Assert.True(peak > 1.0);
    }

    [Theory]
    [InlineData(Easing.Linear)]
    [InlineData(Easing.EaseIn)]
    [InlineData(Easing.EaseOut)]
    [InlineData(Easing.EaseInOut)]
    public void BasicEasingsAreMonotonic(Easing easing)
    {
        var previous = 0.0;
        for (var i = 1; i <= 1000; i++)
        {
            var value = EasingFunctions.Apply(easing, i / 1000.0);
            Assert.True(value >= previous);
            previous = value;
        }
    }
}
