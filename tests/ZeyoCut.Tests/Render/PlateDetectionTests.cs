// SPDX-License-Identifier: MIT

using OpenCvSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class PlateDetectionTests
{
    // detect_and_track's default minConfidence for plates.
    private const double Confident = 0.3;

    // The plates in the van photo (960x639), left to right: on the van behind on the left (the edge cuts it),
    // the van's own, and on the van on the right (the edge cuts it too).
    private static readonly (double X, double Y, double Width, double Height)[] VanPlates = [(0, 307, 33, 11), (557, 427, 103, 41), (943, 346, 17, 13)];

    // The van's own plate in the photo at 0.55 of its size, 528x351.
    private const double Shrunk = 0.55;
    private static readonly (double X, double Y, double Width, double Height) ShrunkPlate = (557 * Shrunk, 427 * Shrunk, 103 * Shrunk, 41 * Shrunk);

    private static AiSession Model()
    {
        var store = new ModelStore(Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models"));
        Assert.SkipWhen(!store.Has(AiModels.Plates), $"{AiModels.Plates.FileName} isn't in {store.Folder}; skipping the plate detection test");
        return AiSession.Open(store.PathOf(AiModels.Plates), AiDevice.Cpu);
    }

    private static string Report(IEnumerable<Detection> found) =>
        string.Join("; ", found.Select(d => $"{d.X:0},{d.Y:0} {d.Width:0}x{d.Height:0} {d.Score:0.00}"));

    // One confident detection per plate, each within 'pixels' of it on every side.
    private static void AssertPlates(List<Detection> found, (double X, double Y, double Width, double Height)[] plates, double pixels)
    {
        Assert.True(found.Count(d => d.Score >= Confident) == plates.Length, $"expected {plates.Length} plates: {Report(found)}");
        foreach (var plate in plates)
        {
            Assert.True(found.Any(d => d.Score >= Confident && Math.Abs(d.X - plate.X) <= pixels && Math.Abs(d.Y - plate.Y) <= pixels
                && Math.Abs(d.X + d.Width - plate.X - plate.Width) <= pixels && Math.Abs(d.Y + d.Height - plate.Y - plate.Height) <= pixels),
                $"a plate isn't at {plate.X:0},{plate.Y:0} {plate.Width:0}x{plate.Height:0}: {Report(found)}");
        }
    }

    // A gray 1920x1080 frame with the shrunk photo at each of these places.
    private static Mat Frame(params (int X, int Y)[] places)
    {
        var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(128, 128, 128));
        using var photo = Cv2.ImRead(TestFixtures.Plates);
        using var small = new Mat();
        Cv2.Resize(photo, small, new Size(528, 351), 0, 0, InterpolationFlags.Area);
        foreach (var (x, y) in places)
        {
            using var target = new Mat(frame, new Rect(x, y, small.Width, small.Height));
            small.CopyTo(target);
        }

        return frame;
    }

    private static (double X, double Y, double Width, double Height) PlateAt((int X, int Y) place) =>
        (place.X + ShrunkPlate.X, place.Y + ShrunkPlate.Y, ShrunkPlate.Width, ShrunkPlate.Height);

    [Fact]
    public void FindsEachPlateInAPhoto()
    {
        using var ai = Model();
        using var photo = Cv2.ImRead(TestFixtures.Plates);

        var found = PlateDetector.Detect(ai, photo, Confident / 2);

        AssertPlates(found, VanPlates, 4);
        Assert.All(found.Where(d => d.Score >= Confident), d => Assert.True(d.Score >= 0.75, Report(found)));
    }

    // The whole 1080p frame is shrunk to 640 px, where a 57 px plate is 19 px across and scores under 0.25;
    // each corner's tile shows it at 32 px.
    [Fact]
    public void FindsSmallPlatesInEachCornerOf1080p()
    {
        using var ai = Model();
        (int X, int Y)[] corners = [(40, 40), (1352, 40), (40, 689), (1352, 689)];
        using var frame = Frame(corners);

        var found = PlateDetector.Detect(ai, frame, Confident / 2);

        AssertPlates(found, corners.Select(PlateAt).ToArray(), 8);
    }

    // The tiles overlap from 768 to 1152 px across and from 432 to 648 px down. A plate across one tile's
    // edge is whole in the tile beside it, and the cut part isn't kept as well.
    [Fact]
    public void APlateAcrossATileEdgeIsFoundWholeOnce()
    {
        using var ai = Model();
        (int X, int Y)[] places = [(433, 40), (1352, 401), (817, 709)];
        using var frame = Frame(places);
        var plates = places.Select(PlateAt).ToArray();
        Assert.InRange(768, plates[0].X, plates[0].X + plates[0].Width);
        Assert.InRange(648, plates[1].Y, plates[1].Y + plates[1].Height);
        Assert.InRange(1152, plates[2].X, plates[2].X + plates[2].Width);

        var found = PlateDetector.Detect(ai, frame, Confident / 2);

        AssertPlates(found, plates, 8);
    }

    [Fact]
    public void FindsAndLinksEachPlateThroughAClip()
    {
        var ffmpeg = Ffmpeg.Require();
        using var ai = Model();
        var file = TestVideos.Vans(Path.Combine(TestPaths.NewDirectory("plates-vans"), "vans.mp4"));
        var asset = TestVideos.AssetFor(file, "vans");

        using var result = ObjectFinder.Find(ffmpeg.Ffmpeg, file, asset, 0, 47, bgr => PlateDetector.Detect(ai, bgr, Confident / 2), Confident, Confident / 2, null, TestContext.Current.CancellationToken);

        var report = string.Join("\n", result.Tracks.Select(t => $"{t.Frames[0].Frame}-{t.Frames[^1].Frame} at {t.Frames[0].X:0},{t.Frames[0].Y:0} {t.Frames[0].Width:0}x{t.Frames[0].Height:0}, lost {string.Join(",", t.Frames.Where(f => f.Lost).Select(f => f.Frame))}, scores {string.Join(" ", t.Frames.Where(f => !f.Lost).Take(6).Select(f => f.Confidence.ToString("0.00")))}"));
        // At the default 0.3, the RENAULT badge above the van's plate passes for a plate on a few frames:
        // a short, weak track the contact sheet shows, to leave out.
        var plates = result.Tracks.Where(t => t.Frames.Where(f => !f.Lost).Average(f => f.Confidence) >= 0.5).OrderBy(t => t.Frames[0].X).ToList();
        Assert.True(plates.Count == 3, report);
        Assert.All(result.Tracks.Except(plates), t => Assert.True(t.Frames.Count < 24, report));

        // In the 640x426 copy each plate is at 2/3 of its place in the photo; the frame starts at its top.
        foreach (var (track, plate) in plates.Zip(VanPlates))
        {
            var first = track.Frames[0];
            Assert.Equal(0, first.Frame);
            Assert.Equal(47, track.Frames[^1].Frame);
            Assert.True(Math.Abs(first.X + first.Width / 2 - (plate.X + plate.Width / 2) * 2 / 3) <= 5 && Math.Abs(first.Y + first.Height / 2 - (plate.Y + plate.Height / 2) * 2 / 3) <= 5, report);
            Assert.All(track.Frames.Where(f => f.Lost), f => Assert.InRange(f.Frame, 14, 31));
            Assert.True(track.Frames.Count(f => !f.Lost) >= 40, report);
        }

        // 30 px a second up: the van's plate is 60 px higher on the last frame.
        var van = plates[1].Frames;
        Assert.InRange(van[0].Y - van[^1].Y, 54, 66);
    }
}
