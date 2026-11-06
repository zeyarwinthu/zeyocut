// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Animation;

public static class FrameMath
{
    public static int FrameCount(Project project) =>
        Math.Max(1, (int)Math.Round(project.Duration * project.Fps, MidpointRounding.AwayFromZero));

    public static double TimeOf(int frame, int fps) => frame / (double)fps;

    public static double TimeOf(Project project, int frame) => TimeOf(frame, project.Fps);

    public static int NearestFrame(Project project, double time)
    {
        var frame = (int)Math.Round(time * project.Fps, MidpointRounding.AwayFromZero);
        return Math.Clamp(frame, 0, FrameCount(project) - 1);
    }

    public static double RenderedDuration(Project project) => FrameCount(project) / (double)project.Fps;
}
