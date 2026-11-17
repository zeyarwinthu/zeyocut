// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The media's thumbnails, shared by the media panel and the timeline: made once in the background,
// kept in the project's cache folder, and made again when the file changes.
public class ThumbnailTests
{
    private static BitmapSource Wait(MediaThumbnails thumbnails, BlockingCollection<string> ready, Asset asset)
    {
        Assert.Null(thumbnails.Get(asset));
        Assert.True(ready.TryTake(out var id, TimeSpan.FromSeconds(30)), $"no thumbnail of '{asset.Id}'");
        Assert.Equal(asset.Id, id);
        return thumbnails.Get(asset)!;
    }

    private static string[] Kept(string directory) =>
        Directory.GetFiles(Path.Combine(directory, "cache", MediaThumbnails.Folder)).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public void APictureIsMadeSmallerOnceAndKeptForNextTime()
    {
        var directory = TestFiles.NewProject("thumbnail-picture");
        var logo = new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 400, Height = 100 };
        var path = TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 400, 100);

        var ready = new BlockingCollection<string>();
        using (var thumbnails = new MediaThumbnails(directory, () => "", ready.Add))
        {
            var made = Wait(thumbnails, ready, logo);
            Assert.Equal((160, 40), (made.PixelWidth, made.PixelHeight));
            Assert.Same(made, thumbnails.Get(logo));
        }

        // Opened again, it's read from the cache folder, not made again.
        var kept = Kept(directory);
        Assert.Single(kept);
        var file = Path.Combine(directory, "cache", MediaThumbnails.Folder, kept[0]);
        var written = File.GetLastWriteTimeUtc(file);
        using (var again = new MediaThumbnails(directory, () => "", ready.Add))
        {
            var read = Wait(again, ready, logo);
            Assert.Equal((160, 40), (read.PixelWidth, read.PixelHeight));
        }

        Assert.Equal(written, File.GetLastWriteTimeUtc(file));

        // The file replaced under the same name gets a thumbnail of its own.
        TestFiles.Png(path, 50, 100);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        using (var changed = new MediaThumbnails(directory, () => "", ready.Add))
        {
            var made = Wait(changed, ready, logo);
            Assert.Equal((50, 100), (made.PixelWidth, made.PixelHeight));
        }

        Assert.Equal(2, Kept(directory).Length);
    }

    [Fact]
    public void AVideoShowsItsFirstFrameAndSoundAndMissingFilesHaveNone()
    {
        var directory = TestFiles.NewProject("thumbnail-video");
        TestFiles.Video(Path.Combine(directory, ProjectStore.AssetsFolder, "clip.mp4"), "testsrc2=s=320x240:r=30:d=1");
        var clip = new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 320, Height = 240, FrameRate = "30", FrameCount = 30, Duration = 1 };
        var ready = new BlockingCollection<string>();
        using var thumbnails = new MediaThumbnails(directory, () => TestFiles.Ffmpeg().Ffmpeg, ready.Add);

        var made = Wait(thumbnails, ready, clip);
        Assert.Equal((160, 120), (made.PixelWidth, made.PixelHeight));

        // testsrc2's first frame isn't one color.
        var pixels = new byte[160 * 120 * 4];
        new FormatConvertedBitmap(made, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 160 * 4, 0);
        Assert.True(pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count() > 4);

        // Sound, fonts, and files that aren't there have no thumbnail and aren't waited for.
        Assert.Null(thumbnails.Get(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.wav", Duration = 2 }));
        Assert.Null(thumbnails.Get(new Asset { Id = "font", Type = AssetType.Font, File = "assets/font.ttf" }));
        Assert.Null(thumbnails.Get(new Asset { Id = "gone", Type = AssetType.Image, File = "assets/gone.png", Width = 8, Height = 8 }));
        Assert.Null(thumbnails.Get(new Asset { Id = "outside", Type = AssetType.Image, File = "../outside.png", Width = 8, Height = 8 }));
        Assert.False(ready.TryTake(out _, TimeSpan.FromMilliseconds(300)));
    }
}
