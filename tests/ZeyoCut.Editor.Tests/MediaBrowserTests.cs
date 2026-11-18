// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The Media browser's shelves, grid and details, from the project's assets and their tags.
public class MediaBrowserTests
{
    // Sounds() with a picture used twice, footage, a picture nothing uses and a font, imported in
    // that order after the sounds; the sounds are tagged SFX or Music.
    private static Project Library()
    {
        var project = TestFiles.Sounds();
        project.Assets.Add(new Asset { Id = "tora_body", Type = AssetType.Image, File = "assets/tora_body.png", Width = 220, Height = 240 });
        project.Assets.Add(new Asset { Id = "street", Type = AssetType.Video, File = "assets/street.mp4", Width = 1280, Height = 720, Duration = 6, FrameRate = "30", FrameCount = 180, HasAudio = true });
        project.Assets.Add(new Asset { Id = "clouds", Type = AssetType.Image, File = "assets/clouds.png", Width = 1400, Height = 240 });
        project.Assets.Add(new Asset { Id = "inter", Type = AssetType.Font, File = "assets/Inter.ttf", FontFamily = "Inter" });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "tora", Drawings = new Dictionary<string, string> { ["normal"] = "tora_body" } });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "tora_2", Drawings = new Dictionary<string, string> { ["normal"] = "tora_body" } });
        AssetTags.Tag(project, ["boing", "talk"], ["SFX"], []);
        AssetTags.Tag(project, ["song"], ["Music", "sfx"], []);
        return project;
    }

    [Fact]
    public void TheShelvesAreTheKindsAndTheTags()
    {
        var project = Library();
        Assert.Equal([("all", "All media", 7), ("video", "Video", 1), ("image", "Images", 2), ("audio", "Audio", 3), ("font", "Fonts", 1)],
            MediaBrowser.Library(project).Select(s => (s.Key, s.Name, s.Count)));
        Assert.Equal([("Music", 1), ("SFX", 3)], MediaBrowser.Bins(project).Select(s => (s.Name, s.Count)));

        // A bin's key finds it in any case; a bin that's gone, or a key nobody knows, is All media.
        Assert.Equal("SFX", MediaBrowser.Find(project, "tag:sfx").Name);
        AssetTags.Tag(project, ["song"], [], ["Music"]);
        Assert.Equal("All media", MediaBrowser.Find(project, "tag:Music").Name);

        // Without fonts there's no Fonts shelf.
        project.Assets.RemoveAll(a => a.Type == AssetType.Font);
        Assert.DoesNotContain("Fonts", MediaBrowser.Library(project).Select(s => s.Name));
    }

    [Fact]
    public void TheGridShowsTheShelfSearchedAndSorted()
    {
        var project = Library();
        var all = MediaBrowser.Find(project, MediaBrowser.AllKey);
        Assert.Equal(["inter", "clouds", "street", "tora_body", "boing", "song", "talk"], MediaBrowser.Shown(project, all, null, MediaSort.Recent).Select(a => a.Id));
        Assert.Equal(["talk", "song", "boing", "tora_body", "street", "clouds", "inter"], MediaBrowser.Shown(project, all, "", MediaSort.Oldest).Select(a => a.Id));
        Assert.Equal(["boing", "clouds", "inter", "song", "street", "talk", "tora_body"], MediaBrowser.Shown(project, all, " ", MediaSort.Name).Select(a => a.Id));

        Assert.Equal(["clouds", "tora_body"], MediaBrowser.Shown(project, MediaBrowser.Find(project, "image"), null, MediaSort.Recent).Select(a => a.Id));
        Assert.Equal(["boing", "song", "talk"], MediaBrowser.Shown(project, MediaBrowser.Find(project, "tag:SFX"), null, MediaSort.Recent).Select(a => a.Id));

        // A search finds ids, file names and tags, in any case, with every word.
        Assert.Equal(["song"], MediaBrowser.Shown(project, all, "MUSIC", MediaSort.Recent).Select(a => a.Id));
        Assert.Equal(["inter"], MediaBrowser.Shown(project, all, ".ttf", MediaSort.Recent).Select(a => a.Id));
        Assert.Equal(["boing", "song"], MediaBrowser.Shown(project, all, "sfx o", MediaSort.Recent).Select(a => a.Id));
        Assert.Empty(MediaBrowser.Shown(project, all, "tora sfx", MediaSort.Recent));
    }

    [Fact]
    public void ACardSaysItsSizeOrLengthAndHowOftenItIsUsed()
    {
        var project = Library();
        Assert.Equal(["0:06", "220×240", "0:00.5", "Inter"], new[] { "street", "tora_body", "boing", "inter" }.Select(id => MediaBrowser.Meta(project.FindAsset(id)!)));
        Assert.Equal([2, 0, 1], new[] { "tora_body", "clouds", "song" }.Select(id => MediaBrowser.Uses(project, id)));
        Assert.Equal(["used 2×", "not used", "missing"], new[] { MediaBrowser.Use(2, false), MediaBrowser.Use(0, false), MediaBrowser.Use(2, true) });
    }

    [Fact]
    public void TheDetailsSayWhatTheFileIsWhereAndWhoUsesIt()
    {
        var project = Library();
        var folder = TestFiles.NewProject("media-details", project);
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        TestFiles.Png(Path.Combine(folder, "assets", "tora_body.png"), 220, 240);

        var body = MediaBrowser.Details(project, folder, project.FindAsset("tora_body")!);
        Assert.Equal(("tora_body.png", "PNG image · 220×240", "2 layers: tora, tora_2"), (body.Title, body.Summary, body.UsedBy));
        Assert.Equal(MediaBrowser.Bytes(new FileInfo(body.Path!).Length), body.FileSize);
        Assert.Equal(Path.Combine(folder, "assets", "tora_body.png"), body.Path);
        Assert.Equal(ProjectList.Shorten(body.Path!), body.Location);
        Assert.Empty(body.Tags);

        var street = MediaBrowser.Details(project, folder, project.FindAsset("street")!);
        Assert.Equal(("MP4 video · 1280×720 · 30 fps · 0:06", "No layer yet", "Missing"), (street.Summary, street.UsedBy, street.FileSize));
        Assert.Equal(["Music", "sfx"], MediaBrowser.Details(project, folder, project.FindAsset("song")!).Tags);
        Assert.Equal("WAV sound · 0:00.5", MediaBrowser.Summary(project.FindAsset("boing")!));
        Assert.Equal("TTF font · Inter", MediaBrowser.Summary(project.FindAsset("inter")!));

        // Several files: counted by kind, with the tags any of them has once.
        var several = MediaBrowser.Details([project.FindAsset("song")!, project.FindAsset("talk")!, project.FindAsset("street")!]);
        Assert.Equal(("3 files", "1 video · 2 sounds", null, null), (several.Title, several.Summary, several.UsedBy, several.Path));
        Assert.Equal(["Music", "sfx"], several.Tags);
    }

    [Fact]
    public void FileSizesAreInBytesKilobytesOrMegabytes() =>
        Assert.Equal(["800 bytes", "1 KB", "38 KB", "1.5 MB", "12 MB"], new long[] { 800, 1100, 38_900, 1_572_864, 12_582_912 }.Select(MediaBrowser.Bytes));
}
