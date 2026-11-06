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
