// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class OutputTests
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Green = new(0, 255, 0);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor White = new(255, 255, 255);
    private static readonly SKColor Slate = new(0x33, 0x66, 0x99);

    // An image layer with its top-left corner at (x, y).
    private static void AddImage(ProjectSession session, string file, string id, double x, double y)
    {
        var asset = session.Mutate("import", p => AssetImporter.Import(p, session.Directory, file, id, new AssetProbe()));
        session.Mutate("add", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions
        {
            Id = id,
            Drawings = new Dictionary<string, string> { ["normal"] = asset.Id },
            AnchorX = 0,
            AnchorY = 0,
            X = x,
            Y = y,
        }));
    }

    private static void Near(SKColor expected, SKColor actual, int tolerance = 6)
    {
        var close = Math.Abs(expected.Red - actual.Red) <= tolerance
            && Math.Abs(expected.Green - actual.Green) <= tolerance
            && Math.Abs(expected.Blue - actual.Blue) <= tolerance;
        Assert.True(close, $"expected about {expected}, got {actual}");
    }

    [Fact]
    public void APresetOfAnotherShapeFitsTheWholeCanvasOverABlurredCopy()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("export-fit");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 160, height: 90, fps: 30, duration: 1));
        session.Mutate("background", p => p.BackgroundColor = "#336699");
        AddImage(session, TestImages.Solid(Path.Combine(directory, "src_bar.png"), 20, 90, SKColors.White), "bar", 70, 0);

        var output = Path.Combine(directory, "exports", "fit.mp4");
        var result = Exporter.Export(session.Snapshot(), directory, output, 18, ffmpeg, cancellationToken: TestContext.Current.CancellationToken, preset: Presets.Shorts1080p, fitShape: true);

        Assert.Equal((1080, 1920), (result.Media.Width, result.Media.Height));
        Assert.Equal(30, result.Media.VideoFrames);
        Assert.Contains("the 160x90 canvas is fitted whole into 1080x1920 over a blurred, enlarged copy of itself", result.Notes!);

        var pixels = Ffmpeg.ExtractFrameRgba(ffmpeg, output, 15, 1080, 1920);
        SKColor At(int x, int y)
        {
            var i = (y * 1080 + x) * 4;
            return new SKColor(pixels[i], pixels[i + 1], pixels[i + 2]);
        }

        // The whole canvas, 6.75x, in the band y 656–1264: the bar is sharp at x 472–608.
        Near(White, At(540, 960));
        Near(Slate, At(400, 960));
        Near(Slate, At(700, 960));

        // Above and below, the copy enlarged 21.3x covers the frame: the bar spans x 327–753, blurred.
        Assert.True(At(400, 300).Red > 200, $"inside the enlarged bar: {At(400, 300)}");
        Assert.True(At(540, 1700).Red > 200, $"inside the enlarged bar: {At(540, 1700)}");
        Near(Slate, At(100, 300));
        Near(Slate, At(1000, 1700));
        Assert.InRange(At(327, 300).Red, (byte)100, (byte)200);

        var same = Exporter.Export(session.Snapshot(), directory, Path.Combine(directory, "exports", "same.mp4"), 18, ffmpeg, cancellationToken: TestContext.Current.CancellationToken, preset: Presets.Youtube1080p, fitShape: true);
        Assert.Equal((1920, 1080), (same.Media.Width, same.Media.Height));
        Assert.DoesNotContain(same.Notes!, n => n.Contains("fitted"));
    }

    [Fact]
    public void ThumbnailHasTheRequestedSizeAndIsCroppedFromTheCenter()
    {
        var directory = TestPaths.NewDirectory("thumbnail");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 300, height: 100, fps: 10, duration: 1));
        AddImage(session, TestImages.Solid(Path.Combine(directory, "src_red.png"), 100, 100, Red), "red", 0, 0);
        AddImage(session, TestImages.Solid(Path.Combine(directory, "src_green.png"), 100, 100, Green), "green", 100, 0);
        AddImage(session, TestImages.Solid(Path.Combine(directory, "src_blue.png"), 100, 100, Blue), "blue", 200, 0);
        var project = session.Snapshot();
        using var renderer = new FrameRenderer(directory);

        var square = FrameExport.Thumbnail(renderer, project, 0.5, 100, 100, Path.Combine(directory, "exports", "square.png"), 960);
        Assert.Equal((5, 100, 100, true, 1.0), (square.Frame, square.Width, square.Height, square.Cropped, square.Enlarged));
        Assert.Empty(square.Warnings);
        using (var image = SKImage.FromEncodedData(square.Path))
        {
            Assert.Equal((100, 100), (image.Width, image.Height));
            Near(Green, TestImages.Pixel(image, 1, 50), 2);
            Near(Green, TestImages.Pixel(image, 98, 50), 2);
        }

        var wide = FrameExport.Thumbnail(renderer, project, 0, 600, 200, Path.Combine(directory, "exports", "wide.png"), 300);
        Assert.Equal((600, 200, false, 2.0), (wide.Width, wide.Height, wide.Cropped, wide.Enlarged));
        Assert.Equal((300, 100), (wide.PreviewWidth, wide.PreviewHeight));
        Assert.Equal(new FileInfo(wide.Path).Length, wide.Bytes);
        using (var image = SKImage.FromEncodedData(wide.Path))
        {
            Assert.Equal((600, 200), (image.Width, image.Height));
            Near(Red, TestImages.Pixel(image, 5, 100), 2);
            Near(Green, TestImages.Pixel(image, 300, 100), 2);
            Near(Blue, TestImages.Pixel(image, 595, 100), 2);
        }

        using (var preview = SKImage.FromEncodedData(wide.Preview))
        {
            Assert.Equal((300, 100), (preview.Width, preview.Height));
        }

        Assert.Contains("width must be between 16 and 7680 pixels (got 8)",
            Assert.Throws<ZeyoCutException>(() => FrameExport.Thumbnail(renderer, project, 0, 8, 100, Path.Combine(directory, "exports", "x.png"), 960)).Message);
        Assert.Contains("time must be between 0 and the end of the project (1s); got 2",
            Assert.Throws<ZeyoCutException>(() => FrameExport.Thumbnail(renderer, project, 2, 100, 100, Path.Combine(directory, "exports", "x.png"), 960)).Message);
    }

    [Fact]
    public void ThumbnailWarnsAboveYouTubesTwoMegabyteLimit()
    {
        var directory = TestPaths.NewDirectory("thumbnail-noise");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 1280, height: 720, fps: 10, duration: 1));
        var info = new SKImageInfo(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bytes = new byte[info.BytesSize];
        new Random(7).NextBytes(bytes);
        for (var i = 3; i < bytes.Length; i += 4)
        {
            bytes[i] = 255;
        }

        var noise = Path.Combine(directory, "src_noise.png");
        using (var image = SKImage.FromPixelCopy(info, bytes))
        {
            Png.Save(image, noise);
        }

        AddImage(session, noise, "noise", 0, 0);
        using var renderer = new FrameRenderer(directory);
        var result = FrameExport.Thumbnail(renderer, session.Snapshot(), 0, 1280, 720, Path.Combine(directory, "exports", "noisy.png"), 960);

        Assert.True(result.Bytes > FrameExport.ThumbnailLimit, $"{result.Bytes} bytes");
        Assert.Contains(result.Warnings, w => w.Contains("over YouTube's 2 MB thumbnail limit"));
    }

    [Fact]
    public void FrameSequenceHasOneNumberedFilePerFrameOfTheRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = TestPaths.NewDirectory("frame-sequence");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 64, height: 36, fps: 10, duration: 2));
        AddImage(session, TestImages.Solid(Path.Combine(directory, "src_red.png"), 16, 16, Red), "red", 0, 0);
        var project = session.Snapshot();
        using var renderer = new FrameRenderer(directory);
        var folder = Path.Combine(directory, "exports", "seq_frames");

        var (first, last) = FrameExport.Frames(project, 0.5, 1.0);
        Assert.Equal((5, 10), (first, last));
        var progress = new List<double>();
        var pngs = FrameExport.Sequence(renderer, project, first, last, "png", folder, progress.Add, ct);
        Assert.Equal((6, 0, "png", 64, 36), (pngs.Count, pngs.Replaced, pngs.Format, pngs.Width, pngs.Height));
        Assert.Equal(1, progress[^1]);
        Assert.Equal(Enumerable.Range(5, 6).Select(f => $"frame_{f:D5}.png"), Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal(Directory.GetFiles(folder).Sum(f => new FileInfo(f).Length), pngs.Bytes);
        using (var image = SKImage.FromEncodedData(Path.Combine(folder, "frame_00007.png")))
        {
            Assert.Equal((64, 36), (image.Width, image.Height));
            Near(Red, TestImages.Pixel(image, 4, 4), 0);
            Near(White, TestImages.Pixel(image, 40, 20), 0);
        }

        var jpgs = FrameExport.Sequence(renderer, project, 0, 2, "jpeg", folder, null, ct);
        Assert.Equal((3, 6, "jpg"), (jpgs.Count, jpgs.Replaced, jpgs.Format));
        Assert.Equal(["frame_00000.jpg", "frame_00001.jpg", "frame_00002.jpg"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());

        Assert.Equal((15, 19), FrameExport.Frames(project, 1.5, 99));
        Assert.Equal((0, 0), FrameExport.Frames(project, 0, 0));
        Assert.Contains("the range must have 0 <= startTime <= endTime (got 1–0.5)",
            Assert.Throws<ZeyoCutException>(() => FrameExport.Frames(project, 1, 0.5)).Message);
        Assert.Contains("startTime 3s is after the end of the project (2s)",
            Assert.Throws<ZeyoCutException>(() => FrameExport.Frames(project, 3, 4)).Message);
        Assert.Contains("unknown format 'gif'; valid values: png, jpg",
            Assert.Throws<ZeyoCutException>(() => FrameExport.CheckFormat("gif")).Message);
    }
}
