// SPDX-License-Identifier: MIT

using OpenCvSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class FaceDetectionTests
{
    // detect_and_track's default minConfidence for faces.
    private const double Confident = 0.6;

    // The faces' centers in the crew photo (960x768), left to right.
    private static readonly (double X, double Y)[] CrewFaces = [(208, 100), (409, 108), (596, 104), (770, 114)];

    private static AiSession YuNet()
    {
        var store = new ModelStore(Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models"));
        Assert.SkipWhen(!store.Has(AiModels.YuNet), $"{AiModels.YuNet.FileName} isn't in {store.Folder}; skipping the face detection test");
        return AiSession.Open(store.PathOf(AiModels.YuNet), AiDevice.Cpu);
    }

    private static string Report(IEnumerable<Detection> found) =>
        string.Join("; ", found.Select(d => $"{d.X:0},{d.Y:0} {d.Width:0}x{d.Height:0} {d.Score:0.00}"));

    // The confident detections, left to right, each centered within 'pixels' of a face.
    private static void AssertFaces(List<Detection> found, (double X, double Y)[] faces, double pixels)
    {
        var confident = found.Where(d => d.Score >= Confident).OrderBy(d => d.X).ToList();
        Assert.True(confident.Count == faces.Length, $"expected {faces.Length} faces: {Report(found)}");
        foreach (var (d, face) in confident.Zip(faces))
        {
            Assert.True(Math.Abs(d.X + d.Width / 2 - face.X) <= pixels && Math.Abs(d.Y + d.Height / 2 - face.Y) <= pixels, $"a face isn't at {face.X},{face.Y}: {Report(found)}");
        }
    }

    [Fact]
    public void FindsTheOneFaceInAPortrait()
    {
        using var ai = YuNet();
        using var portrait = Cv2.ImRead(TestFixtures.Portrait);

        var found = FaceDetector.Detect(ai, portrait, 0.3);

        var face = Assert.Single(found);
        Assert.True(face.Score >= 0.9, Report(found));
        Assert.True(Math.Abs(face.X - 202) <= 8 && Math.Abs(face.Y - 154) <= 8 && Math.Abs(face.Width - 94) <= 12 && Math.Abs(face.Height - 132) <= 12, Report(found));
    }

    [Fact]
    public void FindsEachFaceInAGroup()
    {
        using var ai = YuNet();
        using var group = Cv2.ImRead(TestFixtures.Group);

        AssertFaces(FaceDetector.Detect(ai, group, 0.3), CrewFaces, 10);
    }

    // YuNet itself finds faces of about 10-300 px; the third and eighth size passes find bigger ones.
    [Fact]
    public void FindsFacesFromAboutFourteenPixelsToTheWholePicture()
    {
        using var ai = YuNet();
        using (var group = Cv2.ImRead(TestFixtures.Group))
        using (var small = new Mat())
        {
            Cv2.Resize(group, small, new Size(240, 192), 0, 0, InterpolationFlags.Area);
            AssertFaces(FaceDetector.Detect(ai, small, 0.3), CrewFaces.Select(f => (f.X / 4, f.Y / 4)).ToArray(), 4);
        }

        using var portrait = Cv2.ImRead(TestFixtures.Portrait);
        using var big = new Mat();
        Cv2.Resize(portrait, big, new Size(3000, 3750), 0, 0, InterpolationFlags.Linear);
        var found = FaceDetector.Detect(ai, big, 0.3);
        var face = Assert.Single(found, d => d.Score >= Confident);
        Assert.True(face.Width > 500 && face.Height > 700, Report(found));
        Assert.True(Math.Abs(face.X + face.Width / 2 - 249 * 6) <= 40 && Math.Abs(face.Y + face.Height / 2 - 220 * 6) <= 40, Report(found));
    }

    [Fact]
    public void FindsAndLinksEachFaceThroughAClip()
    {
        var ffmpeg = Ffmpeg.Require();
        using var ai = YuNet();
        var file = TestVideos.Crew(Path.Combine(TestPaths.NewDirectory("faces-crew"), "crew.mp4"));
        var asset = TestVideos.AssetFor(file, "crew");
        var progress = new List<int>();

        using var result = ObjectFinder.Find(ffmpeg.Ffmpeg, file, asset, 0, 47, bgr => FaceDetector.Detect(ai, bgr, 0.3), 0.6, 0.3, (done, _) => progress.Add(done), TestContext.Current.CancellationToken);

        var report = string.Join("\n", result.Tracks.Select(t => $"{t.Frames[0].Frame}-{t.Frames[^1].Frame} at {t.Frames[0].X:0},{t.Frames[0].Y:0} {t.Frames[0].Width:0}x{t.Frames[0].Height:0}, lost {string.Join(",", t.Frames.Where(f => f.Lost).Select(f => f.Frame))}, scores {string.Join(" ", t.Frames.Where(f => !f.Lost).Take(6).Select(f => f.Confidence.ToString("0.00")))}"));
        Assert.Equal(48, result.Frames);
        Assert.Equal(Enumerable.Range(1, 48), progress);
        Assert.True(result.Tracks.Count == 4, report);

        // Through the 800x640 copy, panned by 40 + 20t, 20 + 5t: on frame 0 each face is at 5/6 of its place in the photo, 40 px left and 20 px up.
        foreach (var (track, face) in result.Tracks.Zip(CrewFaces))
        {
            var first = track.Frames[0];
            Assert.Equal(0, first.Frame);
            Assert.Equal(47, track.Frames[^1].Frame);
            Assert.True(Math.Abs(first.X + first.Width / 2 - (face.X * 5 / 6 - 40)) <= 8 && Math.Abs(first.Y + first.Height / 2 - (face.Y * 5 / 6 - 20)) <= 8, report);
            Assert.All(track.Frames.Where(f => f.Lost), f => Assert.InRange(f.Frame, 14, 31));
            Assert.True(track.Frames.Count(f => !f.Lost) >= 40, report);
            Assert.True(track.Picture.Width > 40 && track.Picture.Height > 40, $"{track.Picture.Width}x{track.Picture.Height}");
        }
    }
}
