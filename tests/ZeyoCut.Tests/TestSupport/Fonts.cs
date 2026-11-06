// SPDX-License-Identifier: MIT

using SkiaSharp;

namespace ZeyoCut.Tests.TestSupport;

// The fonts these tests shape and draw with come with Windows (Myanmar Text, Yu Gothic, Segoe UI,
// Arial) and aren't in the repository; a test that needs one skips on a computer without it.
public static class Fonts
{
    public static void Require(params string[] families)
    {
        var missing = families.Where(family => !Installed(family)).ToList();
        Assert.SkipWhen(missing.Count > 0, $"{string.Join(", ", missing)} {(missing.Count == 1 ? "isn't" : "aren't")} installed; skipping a test that draws with {(missing.Count == 1 ? "it" : "them")}");
    }

    private static bool Installed(string family)
    {
        using var typeface = SKFontManager.Default.MatchFamily(family);
        return typeface is not null && typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase);
    }
}
