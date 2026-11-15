// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ZeyoCut.Editor;

public static class Timecode
{
    // HH:MM:SS:FF, FF being the frame within its second. A frame rate under 1 (in a project.json
    // with that problem) counts as 1.
    public static string Of(int frame, int fps)
    {
        fps = Math.Max(1, fps);
        var seconds = frame / fps;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{frame % fps:00}");
    }

    // A time or length as people write it: 0:05, 1:30, 1:02:03, or with tenths (0:02.5).
    public static string Short(double seconds, bool tenths = false)
    {
        var rounded = tenths ? Math.Round(seconds, 1) : Math.Floor(seconds + 1e-6);
        var whole = (int)Math.Floor(rounded + 1e-6);
        var text = whole >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
        return tenths ? text + string.Create(CultureInfo.InvariantCulture, $".{(int)Math.Round((rounded - whole) * 10)}") : text;
    }

    // A file's length in the media list: with tenths under a second, so a short sound isn't 0:00.
    public static string Length(double seconds) => Short(seconds, seconds < 1);
}
