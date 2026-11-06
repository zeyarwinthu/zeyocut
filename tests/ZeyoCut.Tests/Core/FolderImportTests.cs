// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class FolderImportTests
{
    private static string Touch(string folder, string name, DateTime? changed = null)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, name);
        if (changed is { } time)
        {
            File.SetLastWriteTimeUtc(path, time);
        }

        return path;
    }

    [Fact]
    public void NamesSortWithTheirNumbersInOrder()
    {
        string[] names = ["panel_10.png", "panel_2.png", "Panel_1.png", "panel_02b.png", "cover.png", "panel_2.jpg", "page009.png", "page10.png"];
        Assert.Equal(["cover.png", "page009.png", "page10.png", "Panel_1.png", "panel_2.jpg", "panel_2.png", "panel_02b.png", "panel_10.png"], names.Order(NaturalComparer.Instance));
        Assert.True(NaturalComparer.Instance.Compare("a01", "a1") != 0);
        Assert.Equal(0, NaturalComparer.Instance.Compare("same", "same"));
    }

    [Fact]
    public void ListTakesTheMatchingFilesOfTheFolderInOrder()
    {
        var folder = TestPaths.NewDirectory("folder-list");
        var day = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        Touch(folder, "panel_10.png", day);
        Touch(folder, "panel_2.png", day.AddHours(2));
        Touch(folder, "panel_1.PNG", day.AddHours(1));
        Touch(folder, "voice.wav", day.AddHours(3));
        Touch(folder, "notes.txt");
        Touch(folder, "Thumbs.db");
        File.SetAttributes(Touch(folder, "hidden.png"), FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(folder, "more"));
        Touch(Path.Combine(folder, "more"), "panel_3.png");

        var byName = FolderImport.List(folder, null, null);
        Assert.Equal(folder, byName.Folder);
        Assert.Equal("*", byName.Pattern);
        Assert.Equal(FolderSort.Name, byName.Sort);
        Assert.Equal(["panel_1.PNG", "panel_2.png", "panel_10.png", "voice.wav"], byName.Files.Select(Path.GetFileName));
        Assert.Equal(["notes.txt", "Thumbs.db"], byName.Skipped);

        var byDate = FolderImport.List(folder + "\\", "PANEL_*", "Date");
        Assert.Equal(["panel_10.png", "panel_1.PNG", "panel_2.png"], byDate.Files.Select(Path.GetFileName));
        Assert.Empty(byDate.Skipped);
        Assert.Equal(FolderSort.Date, byDate.Sort);
    }

    [Fact]
    public void ListSaysWhyThereIsNothingToImport()
    {
        var folder = TestPaths.NewDirectory("folder-errors");
        var notes = Touch(folder, "notes.txt");
        Touch(folder, "panel_1.png");

        void Refused(string message, string path, string? pattern = null, string? sortBy = null) =>
            Assert.Equal(message, Assert.Throws<ZeyoCutException>(() => FolderImport.List(path, pattern, sortBy)).Message);

        Refused("path is required", " ");
        Refused($"folder not found: {Path.Combine(folder, "missing")}", Path.Combine(folder, "missing"));
        Refused($"{notes} is a file; import_folder takes a folder (import_asset or import_video take one file)", notes);
        Refused("pattern 'more\\*.png' must be a file name pattern such as *.png or panel_*, without a folder", folder, "more\\*.png");
        Refused("unknown sortBy 'size'; valid values: name, date", folder, null, "size");
        Refused($"no file in {folder} matches '*.jpg'", folder, "*.jpg");
        Refused($"no image, audio, font or video file in {folder} matches 'notes*' (found only: notes.txt)", folder, "notes*");
    }
}
