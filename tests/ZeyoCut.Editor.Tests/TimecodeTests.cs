// SPDX-License-Identifier: MIT

namespace ZeyoCut.Editor.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData(0, 30, "00:00:00:00")]
    [InlineData(29, 30, "00:00:00:29")]
    [InlineData(89, 30, "00:00:02:29")]
    [InlineData(3661 * 30 + 5, 30, "01:01:01:05")]
    [InlineData(50, 25, "00:00:02:00")]
    [InlineData(119, 60, "00:00:01:59")]
    public void TimecodeCountsFramesWithinTheSecond(int frame, int fps, string expected) => Assert.Equal(expected, Timecode.Of(frame, fps));

    [Theory]
    [InlineData(0, false, "0:00")]
    [InlineData(5.9, false, "0:05")]
    [InlineData(90, false, "1:30")]
    [InlineData(3723, false, "1:02:03")]
    [InlineData(2.5, true, "0:02.5")]
    [InlineData(0.1, true, "0:00.1")]
    [InlineData(59.96, true, "1:00.0")]
    public void ShortTimesReadAsPeopleWriteThem(double seconds, bool tenths, string expected) => Assert.Equal(expected, Timecode.Short(seconds, tenths));

    [Theory]
    [InlineData(0.6, "0:00.6")]
    [InlineData(0.3, "0:00.3")]
    [InlineData(6.2, "0:06")]
    [InlineData(12, "0:12")]
    public void AShortSoundShowsItsTenths(double seconds, string expected) => Assert.Equal(expected, Timecode.Length(seconds));
}
