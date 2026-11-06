// SPDX-License-Identifier: MIT

namespace ZeyoCut.Tests.TestSupport;

// The public-domain photos and the old project files in Fixtures (see Fixtures\README.md).
public static class TestFixtures
{
    // One face: Scott Horowitz's official NASA portrait, 500x625.
    public static string Portrait => Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait.jpg");

    // Four faces side by side: the STS-135 crew photo, 960x768.
    public static string Group => Path.Combine(AppContext.BaseDirectory, "Fixtures", "group.jpg");

    // Three plates: a police van's, 103x41, a small one on a van behind it and one cut by the right edge. 960x639.
    public static string Plates => Path.Combine(AppContext.BaseDirectory, "Fixtures", "plates.jpg");

    // A project.json as ZeyoCut saved it in format 'version' (1 to 5).
    public static string OldProject(int version) => Path.Combine(AppContext.BaseDirectory, "Fixtures", $"project-v{version}.json");
}
