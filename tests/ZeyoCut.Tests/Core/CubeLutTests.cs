// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// .cube files: read as 3D LUTs, mixed between their points, refused when they aren't one, and
// imported as LUT assets for a grade's look.
public class CubeLutTests
{
    // A 3-point LUT that squares red, keeps green and turns blue around, with red changing fastest.
    internal static string Curves()
    {
        var lines = new List<string> { "# made by hand", "TITLE \"Curves\"", "", "LUT_3D_SIZE 3" };
        foreach (var b in new[] { 0, 0.5, 1 })
        {
            foreach (var g in new[] { 0, 0.5, 1 })
            {
                foreach (var r in new[] { 0, 0.5, 1 })
                {
                    lines.Add(FormattableString.Invariant($"{r * r} {g} {1 - b}"));
                }
            }
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    [Fact]
    public void ACubeFileIsReadAndMixedBetweenItsPoints()
    {
        var lut = CubeLut.Parse(Curves(), "curves.cube");
        Assert.Equal(("Curves", 3, 81), (lut.Title, lut.Size, lut.Data.Length));
        Assert.Equal(new[] { 0f, 0f, 0f }, lut.DomainMin);
        Assert.Equal(new[] { 1f, 1f, 1f }, lut.DomainMax);

        // On a point, its color; between points, the mix of the points around it.
        Assert.Equal((0.25, 0.5, 0.5), lut.Sample(0.5, 0.5, 0.5));
        var (red, green, blue) = lut.Sample(0.25, 0.75, 0.1);
        Assert.Equal((0.125, 0.75, 0.9), (red, green, blue), Tolerance);
        Assert.Equal((1.0, 1.0, 0.0), lut.Sample(1.5, 2, 3));

        // A domain of 0 to 2: the points cover twice the range.
        var wide = CubeLut.Parse("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 2 2 2\nLUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n", "wide.cube");
        Assert.Equal((0.5, 0.25, 1.0), wide.Sample(1, 0.5, 2), Tolerance);
    }

    [Theory]
    [InlineData("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n", "'bad.cube' is a 1D LUT (LUT_1D_SIZE); ZeyoCut takes 3D LUTs, with LUT_3D_SIZE")]
    [InlineData("TITLE \"x\"\n", "'bad.cube' isn't a 3D LUT: a .cube file needs a LUT_3D_SIZE line")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n1 1 1\n", "'bad.cube' has 2 rows of red, green and blue; a LUT of 2 points a side has 8")]
    [InlineData("LUT_3D_SIZE 200\n", "'bad.cube' line 1: LUT_3D_SIZE is 200; ZeyoCut takes 2 to 129 points a side")]
    [InlineData("LUT_3D_SIZE 2\n0 0 zero\n", "'bad.cube' line 2: expected three numbers: red, green and blue")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\nTITLE \"late\"\n", "'bad.cube' line 3: 'TITLE' comes after the table; a .cube file's keywords come first")]
    [InlineData("DOMAIN_MAX 0 1 1\nLUT_3D_SIZE 2\n", "'bad.cube': DOMAIN_MAX must be above DOMAIN_MIN for each of red, green and blue")]
    public void FilesThatArentA3DLutAreRefusedWithTheReason(string text, string message) =>
        Assert.Equal(message, Assert.Throws<ZeyoCutException>(() => CubeLut.Parse(text, "bad.cube")).Message);

    // import_asset takes a .cube file as a LUT asset, which a grade's look uses.
    [Fact]
    public void ACubeFileImportsAsALutAsset()
    {
        var directory = TestPaths.NewDirectory("cube-import");
        var source = Path.Combine(TestPaths.NewDirectory("cube-import-source"), "Warm sunset.cube");
        File.WriteAllText(source, Curves());
        var project = TestProjects.Footage();

        var asset = AssetImporter.Import(project, directory, source, null, new AssetProbe());
        Assert.Equal((AssetType.Lut, "lut"), (asset.Type, EnumNames.ToName(asset.Type)));
        Assert.True(File.Exists(ProjectStore.AssetPath(directory, asset)));
        Assert.EndsWith(".cube", asset.File, StringComparison.Ordinal);
        ColorEditor.SetColor(project, "clip_1", new ColorChange(new Dictionary<string, double>(), Look: asset.Id));
        Assert.Empty(ProjectValidator.Validate(project));

        File.WriteAllText(source, "LUT_1D_SIZE 2\n0 0 0\n1 1 1\n");
        Assert.Equal("'Warm sunset.cube' is a 1D LUT (LUT_1D_SIZE); ZeyoCut takes 3D LUTs, with LUT_3D_SIZE",
            Assert.Throws<ZeyoCutException>(() => AssetImporter.Import(project, directory, source, "flat", new AssetProbe())).Message);
    }

    private static readonly IEqualityComparer<(double, double, double)> Tolerance = new Near();

    private sealed class Near : IEqualityComparer<(double, double, double)>
    {
        public bool Equals((double, double, double) x, (double, double, double) y) =>
            Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6 && Math.Abs(x.Item3 - y.Item3) < 1e-6;

        public int GetHashCode((double, double, double) obj) => 0;
    }
}
