// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Editor.Tests;

public class CaptionGridTests
{
    private static Project ComicPanels() =>
        ProjectJson.Deserialize(File.ReadAllText(Path.Combine(TestFiles.RepositoryRoot, "examples", "comic-panels", ProjectStore.ProjectFileName)));

    // Tracks of captions in languages, as add_caption_track makes them.
    private static Project Tracks(params (string Language, CueEntry[] Cues)[] tracks)
    {
        var project = TestFiles.Project(duration: 10);
        foreach (var (language, cues) in tracks)
        {
            CaptionTracks.AddEntries(project, language, cues, new CaptionOptions(), [], null);
        }

        return project;
    }

    [Fact]
    public void ComicPanelsGivesThreeRowsOfItsThreeLanguages()
    {
        var table = CaptionGrid.Build(ComicPanels(), null);

        Assert.Equal(["EN · source", "JA", "MY"], table.Columns.Select(c => c.Chip));
        Assert.Equal(["", "3/3", "3/3"], table.Columns.Select(c => c.Count));
        Assert.Equal(["English", "日本語", "မြန်မာ"], table.Columns.Select(c => c.Heading));
        Assert.Equal(["Segoe UI", "Yu Gothic", "Myanmar Text"], table.Columns.Select(c => c.Font));
        Assert.All(table.Columns, c => Assert.False(c.Pickable));

        Assert.Equal([1, 2, 3], table.Rows.Select(r => r.Number));
        Assert.Equal([("00:00.40", "00:02.70"), ("00:03.40", "00:05.70"), ("00:06.40", "00:08.70")], table.Rows.Select(r => (r.StartText, r.EndText)));
        Assert.Equal(["captions_en_2", "captions_ja_2", "captions_my_2"], table.Rows[1].LayerIds);
        Assert.Equal(["Then he jumps as high as he can!", "そして、思いきりジャンプ！", "ပြီးတော့ အမြင့်ကြီး ခုန်လိုက်တယ်။"], table.Rows[1].Cells.Select(c => c.Text));
    }

    [Fact]
    public void ACaptionGoesInTheRowItOverlapsMostOrInARowOfItsOwn()
    {
        var project = Tracks(
            ("ja", [new(0, 2, "一"), new(2, 4, "二")]),
            ("my", [new(0.5, 2.5, "a"), new(6, 7, "b")]),
            ("ko", [new(6.5, 8, "가"), new(9, 9.5, "나")]));

        // Without English the first track is the source. my's first caption overlaps ja's first
        // most; its second overlaps none and gets a row, which ko's first shares; ko's second has one
        // of its own.
        var table = CaptionGrid.Build(project, null);
        Assert.Equal(["JA · source", "KO", "MY"], table.Columns.Select(c => c.Chip));
        Assert.Equal([false, true, true], table.Columns.Select(c => c.Pickable));
        Assert.Equal([["一", "", "a"], ["二", "", ""], ["", "가", "b"], ["", "나", ""]], table.Rows.Select(r => r.Cells.Select(c => c.Text)));
        Assert.Equal([0, 2, 6, 9], table.Rows.Select(r => r.Start));
        Assert.Equal(["", "2/4", "2/4"], table.Columns.Select(c => c.Count));

        // Picked as the source, my's captions make the rows.
        table = CaptionGrid.Build(project, "captions_my");
        Assert.Equal(["MY · source", "JA", "KO"], table.Columns.Select(c => c.Chip));
        Assert.Equal([["a", "一", ""], ["", "二", ""], ["b", "", "가"], ["", "", "나"]], table.Rows.Select(r => r.Cells.Select(c => c.Text)));

        // English is the source whenever there is one.
        CaptionTracks.AddEntries(project, "en", [new CueEntry(1, 3, "One")], new CaptionOptions(), [], null);
        table = CaptionGrid.Build(project, "captions_my");
        Assert.Equal(["EN · source", "JA", "KO", "MY"], table.Columns.Select(c => c.Chip));
        Assert.All(table.Columns, c => Assert.False(c.Pickable));
    }

    [Fact]
    public void ANewRowStartsAtThePlayheadOrAfterTheCaptionItIsIn()
    {
        var project = ComicPanels();
        var table = CaptionGrid.Build(project, null);
        Assert.Equal((0.0, 0.4), CaptionGrid.Draft(project, table, 0));
        Assert.Equal((2.7, 3.4), CaptionGrid.Draft(project, table, 1));
        Assert.Equal((3.0, 3.4), CaptionGrid.Draft(project, table, 3));
        Assert.Equal((8.7, 9.0), CaptionGrid.Draft(project, table, 7));

        // It's a row of the grid until a caption is typed into it, and no track's count has it.
        var drafted = CaptionGrid.Build(project, null, (2.7, 3.4));
        Assert.Equal([false, true, false, false], drafted.Rows.Select(r => r.Draft));
        Assert.Equal([1, 0, 2, 3], drafted.Rows.Select(r => r.Number));
        Assert.Equal(["", "", ""], drafted.Rows[1].Cells.Select(c => c.Text));
        Assert.Empty(drafted.Rows[1].LayerIds);
        Assert.Equal(["", "3/3", "3/3"], drafted.Columns.Select(c => c.Count));

        // Captions up to the end leave no room after the playhead.
        project.Duration = 8.7;
        Assert.Null(CaptionGrid.Draft(project, CaptionGrid.Build(project, null), 7));
    }

    [Fact]
    public void TimesAreMinutesAndSecondsAndTypedAsThoseOrSeconds()
    {
        Assert.Equal("00:03.40", CaptionTime.Text(3.4));
        Assert.Equal("1:02:03.05", CaptionTime.Text(3723.05));
        Assert.Equal(3.4, CaptionTime.Parse("00:03.40", "the start"));
        Assert.Equal(3.4, CaptionTime.Parse("3,4", "the start"));
        Assert.Equal(63.5, CaptionTime.Parse(" 1:03.5 ", "the start"));
        Assert.Equal(3723.05, CaptionTime.Parse("1:02:03.05", "the start"));
        Assert.Equal("the end must be a time like 00:03.40 or 3.4 (got 'soon')", Assert.Throws<ZeyoCutException>(() => CaptionTime.Parse(" soon", "the end")).Message);
    }

    [Fact]
    public void ThePreviewShowsTheLanguageChosenEnglishOrTheFirst()
    {
        var project = ComicPanels();
        Assert.Equal("en", CaptionGrid.PreviewLanguage(project, null));
        Assert.Equal("ja", CaptionGrid.PreviewLanguage(project, "ja"));
        Assert.Equal("en", CaptionGrid.PreviewLanguage(project, "th"));
        Assert.Equal("ko", CaptionGrid.PreviewLanguage(Tracks(("ko", [new(0, 1, "가")]), ("my", [new(0, 1, "a")])), null));
        Assert.Null(CaptionGrid.PreviewLanguage(TestFiles.Project(), "ja"));
    }
}
