// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;

namespace ZeyoCut.Editor.Tests;

// Importing from the editor: each file is an edit of the editor's, as import_asset and import_video
// would make it.
public class MediaImporterTests
{
    private static (EditorDocument Document, MediaImporter Importer, List<Asset> Added, string Media) Open(string name, Func<FfmpegPaths>? ffmpeg = null)
    {
        var folder = TestFiles.NewProject(name);
        var media = TestFiles.NewFolder(name + "-media");
        var document = EditorDocument.Open(folder);
        var importer = new MediaImporter(document, new JobManager(), ffmpeg ?? TestFiles.Ffmpeg, action => action(),
            operation => Task.FromResult<(bool Saved, string Status)?>((document.Apply(operation), document.Status)));
        var added = new List<Asset>();
        importer.Added += added.Add;
        return (document, importer, added, media);
    }

    [Fact]
    public async Task AnImageIsCopiedInAndAddedAsAnEdit()
    {
        var (document, importer, added, media) = Open("import-image", () => throw new InvalidOperationException("an image needs no FFmpeg"));
        await importer.Import([TestFiles.Png(Path.Combine(media, "Big Logo.png"), 64, 32)]);

        var asset = Assert.Single(added);
        Assert.Equal(("big_logo", "assets/Big_Logo.png", 64, 32), (asset.Id, asset.File, asset.Width, asset.Height));
        Assert.True(File.Exists(Path.Combine(document.Session.Directory, "assets", "Big_Logo.png")));
        Assert.NotNull(ProjectStore.Load(document.Session.Directory).FindAsset("big_logo"));
        Assert.Equal("Saved · revision 2", document.Status);
        Assert.Equal("editor import_asset Big Logo.png", ProjectBackups.List(document.Session.Directory)[0].Before);
        Assert.Empty(importer.Pending);
    }

    [Fact]
    public async Task AFileOfAnotherTypeStaysInTheListWithTheReason()
    {
        var (document, importer, added, media) = Open("import-unsupported", () => throw new InvalidOperationException("not needed"));
        var notes = Path.Combine(media, "notes.txt");
        File.WriteAllText(notes, "hello");
        await importer.Import([notes]);

        Assert.Empty(added);
        var item = Assert.Single(importer.Pending);
        Assert.True(item.Failed);
        Assert.StartsWith("couldn't import: '.txt' files can't be imported; ZeyoCut imports images, audio, fonts and LUTs (.png", item.Stage);
        Assert.Equal(1, document.Revision);

        importer.Dismiss(item);
        Assert.Empty(importer.Pending);
    }

    [Fact]
    public async Task AVideoThatNeedsNoNormalizingIsCopiedAsItIs()
    {
        var (document, importer, added, media) = Open("import-video");
        var clip = TestFiles.Video(Path.Combine(media, "clip.mp4"), "testsrc2=s=160x120:r=30:d=1");
        await importer.Import([clip]);

        var asset = Assert.Single(added);
        Assert.Equal(("clip", "assets/clip.mp4", 160, 120, 30), (asset.Id, asset.File, asset.Width, asset.Height, asset.FrameCount));
        Assert.Null(asset.OriginalFile);
        Assert.Null(asset.Normalized);
        Assert.Equal("editor import_video clip.mp4", ProjectBackups.List(document.Session.Directory)[0].Before);
    }

    [Fact]
    public async Task AVideoWithAVariableFrameRateIsNormalizedFirst()
    {
        var (document, importer, added, media) = Open("import-vfr");
        var phone = TestFiles.Video(Path.Combine(media, "phone.mp4"), "testsrc2=s=160x120:r=30:d=2,select='not(eq(n,10)+eq(n,20)+eq(n,40))'", "-fps_mode", "vfr");
        await importer.Import([phone]);

        var asset = Assert.Single(added);
        Assert.Equal(("phone", "assets/phone.mp4", phone), (asset.Id, asset.File, asset.OriginalFile));
        Assert.Contains("variable frame rate", asset.Normalized);
        Assert.True(File.Exists(Path.Combine(document.Session.Directory, "assets", "phone.mp4")));
        Assert.Empty(Directory.GetFiles(Path.Combine(document.Session.Directory, "assets"), "*.partial*"));
        Assert.Equal("editor import_video phone.mp4", ProjectBackups.List(document.Session.Directory)[0].Before);
        Assert.Empty(importer.Pending);
    }
}
