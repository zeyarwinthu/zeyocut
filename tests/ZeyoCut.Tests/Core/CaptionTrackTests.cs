// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Tests.Core;

public class CaptionTrackTests
{
    private static Project NewProject() => new() { Name = "demo", Width = 640, Height = 360, Fps = 10, Duration = 10 };

    private static CaptionOptions Options(string? font = null) => new() { FontFamily = font };

    // A text box as wide as the text's width limit, and a line tall for each line.
    private sealed class LineMeasurer : ITextMeasurer
    {
        public (double Width, double Height) Measure(TextLayer layer) =>
            (layer.MaxWidth ?? layer.Text.Length * layer.FontSize * 0.5, layer.Text.Split('\n').Length * layer.FontSize * 1.25 * layer.LineHeight);
    }

    private static TextLayer Cue(Project project, string id) => (TextLayer)project.FindLayer(id)!;

    private static Project Tracks(ITextMeasurer measurer)
    {
        var project = NewProject();
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(1, 2, "One"), new CueEntry(4, 5, "Two")], project.Duration).Cues, Options(), [], measurer);
        CaptionTracks.Add(project, "ja", CaptionTracks.Check([new CueEntry(1, 2, "イチ")], project.Duration).Cues, Options("Yu Gothic"), [], measurer);
        return project;
    }

    [Fact]
    public void SrtBlocksBecomeCuesAndBrokenBlocksAreNamed()
    {
        var srt = "﻿1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i> there\r\n\r\n"
            + "2\r\n00:00:03.000 --> 00:00:04,000 X1:10\r\n{\\an8}Top line\r\n<font color=\"#ff0\">second line</font>\r\n\r\n"
            + "00:00:05,000 --> 00:01:06,000\r\nNo number\r\n\r\n"
            + "4\r\n00:00:0x,000 --> 00:00:07,000\r\nBad\r\n\r\n"
            + "5\r\nJust text\r\n";

        var (cues, problems) = CaptionTracks.ParseSrt(srt);

        Assert.Equal(
            [new CueEntry(1, 2.5, "Hello there"), new CueEntry(3, 4, "Top line\nsecond line"), new CueEntry(5, 66, "No number")],
            cues);
        Assert.Equal(
            [
                "line 14: '00:00:0x,000 --> 00:00:07,000' isn't a time line like 00:00:01,500 --> 00:00:03,000, so the block was left out",
                "line 17: this block has no 'start --> end' line, so it was left out",
            ],
            problems);
    }

    [Fact]
    public void CuesAreCheckedAgainstTheProjectAndSortedByTime()
    {
        var (cues, problems) = CaptionTracks.Check(
            [
                new CueEntry(0, 1.5, "Hi"),
                new CueEntry(1, 2, "Overlaps\nthe first"),
                new CueEntry(2, 1, "Backwards"),
                new CueEntry(3, 4, "  "),
                new CueEntry(9, 12, "Long"),
                new CueEntry(10, 11, "Late"),
                new CueEntry(5.0004, 6, "Rounded"),
            ],
            10);

        Assert.Equal(["0-1.5 Hi", "1-2 Overlaps\nthe first", "5-6 Rounded", "9-10 Long"], cues.Select(c => $"{c.Start}-{c.End} {c.Text}"));
        Assert.Equal(["Overlaps", "the first"], cues[1].Lines);
        Assert.Equal(
            [
                "cue 3 (\"Backwards\") must start at 0 s or later and end after it starts (got 2–1s), so it was left out",
                "cue 4 has no text, so it was left out",
                "cue 5 (\"Long\") runs past the project end (10s), so it ends there",
                "cue 6 (\"Late\") starts at 10s, at or after the project end (10s), so it was left out",
                "cues 1 and 2 overlap at 1–1.5s, so both show at once, one over the other",
            ],
            problems);
    }

    [Fact]
    public void ACaptionAddedToATrackLooksLikeTheOneBeforeIt()
    {
        var measurer = new LineMeasurer();
        var project = Tracks(measurer);
        var two = Cue(project, "captions_en_2");
        two.Color = "#FFFF00";
        two.Y.Value = 300;

        var added = CaptionTracks.AddCue(project, "captions_en", 6, 7.5, "  Three\n\n and more ", Options(), measurer);
        Assert.Equal("captions_en_3", added.Id);
        Assert.Equal(("Three\nand more", "Three and more", "captions_en"), (added.Text, added.Name, added.ParentId));
        Assert.Equal((6.0, (double?)7.5), (added.Start, added.End));
        Assert.Equal(("#FFFF00", 300.0, two.FontSize, two.FontFamily, two.ZIndex), (added.Color, added.Y.Value, added.FontSize, added.FontFamily, added.ZIndex));
        Assert.Equal((two.Box!.Color, two.Box.Padding), (added.Box!.Color, added.Box.Padding));
        Assert.Equal(project.Layers.IndexOf(two) + 1, project.Layers.IndexOf(added));

        // Its anchor is at the same place in its box, two lines tall: it stands on the same bottom edge.
        Assert.Equal(two.AnchorX, added.AnchorX);
        Assert.Equal(two.AnchorY * 2, added.AnchorY, 3);

        // Before the first caption it looks like the first, and keyframes move with it.
        var one = Cue(project, "captions_en_1");
        one.Opacity.Keyframes = [new Keyframe { Time = 1, Value = 0 }, new Keyframe { Time = 1.5, Value = 1 }];
        var first = CaptionTracks.AddCue(project, "captions_en", 0.2, 0.9, "Zero", Options(), measurer);
        Assert.Equal("captions_en_4", first.Id);
        Assert.Equal([0.2, 0.7], first.Opacity.Keyframes.Select(k => Math.Round(k.Time, 6)));
        Assert.Equal([1.0, 1.5], one.Opacity.Keyframes.Select(k => k.Time));
        Assert.Equal(10, CaptionTracks.AddCue(project, "captions_en", 9, 12, "Late", Options(), measurer).End);
        Assert.Equal(["Zero", "One", "Two", "Three\nand more", "Late"], CaptionTracks.Cues(project, "captions_en").Select(c => c.Text));
        Assert.Empty(ProjectValidator.Validate(project));

        Assert.Equal("a caption needs some text", Assert.Throws<ZeyoCutException>(() => CaptionTracks.AddCue(project, "captions_en", 3, 3.5, " \n ", Options(), measurer)).Message);
        Assert.Equal("a caption must start at 0 s or later and before the project end (10s), and end after it starts (got 3–2s)",
            Assert.Throws<ZeyoCutException>(() => CaptionTracks.AddCue(project, "captions_en", 3, 2, "Back", Options(), measurer)).Message);
        Assert.Equal("'captions_en_1' isn't a caption track (a null layer with the captions under it)",
            Assert.Throws<ZeyoCutException>(() => CaptionTracks.AddCue(project, "captions_en_1", 3, 3.5, "Hi", Options(), measurer)).Message);

        // A track without captions gets the look the options give a new track.
        project.Layers.RemoveAll(l => l.ParentId == "captions_ja");
        var fresh = CaptionTracks.AddCue(project, "captions_ja", 2, 3, "ニ", Options("Yu Gothic"), measurer);
        Assert.Equal(("captions_ja_1", "Yu Gothic", CaptionTracks.BoxColor), (fresh.Id, fresh.FontFamily, fresh.Box?.Color));
        Assert.Equal(Cue(project, "captions_en_1").Y.Value, fresh.Y.Value);

        var operation = new AddCaptionOperation("captions_en", 7.6, 8.5, "Four", Measurer: _ => measurer);
        Assert.Equal(("add_caption captions_en", "captions_en"), (operation.Label, Assert.Single(operation.Layers)));
        Assert.Equal("added caption 'captions_en_6' to 'captions_en' at 7.6–8.5s", operation.Apply(project));
    }

    [Fact]
    public void ACaptionGivenASecondLineStandsOnTheSameBottomEdge()
    {
        var measurer = new LineMeasurer();
        var project = Tracks(measurer);
        var one = Cue(project, "captions_en_1");
        var bottom = one.AnchorY;

        CaptionTracks.SetText(project, one.Id, "One\n  and two ", measurer);
        Assert.Equal(("One\nand two", "One and two"), (one.Text, one.Name));
        Assert.Equal(bottom * 2, one.AnchorY, 3);

        // A name of its own stays.
        one.Name = "opening";
        Assert.Equal("updated the text of caption 'captions_en_1'", new CaptionTextOperation(one.Id, "Just one", _ => measurer).Apply(project));
        Assert.Equal(("Just one", "opening"), (one.Text, one.Name));
        Assert.Equal(bottom, one.AnchorY, 3);

        Assert.Equal("a caption needs some text", Assert.Throws<ZeyoCutException>(() => CaptionTracks.SetText(project, one.Id, "  ", measurer)).Message);
        Assert.Equal("'captions_en' isn't a text layer", Assert.Throws<ZeyoCutException>(() => CaptionTracks.SetText(project, "captions_en", "Hi", measurer)).Message);
    }

    [Fact]
    public void RetimingACaptionTrimsItInEveryLanguage()
    {
        var project = Tracks(new LineMeasurer());
        string[] row = ["captions_en_1", "captions_ja_1"];
        Cue(project, "captions_ja_1").End = 2.2;

        var operation = new RetimeCaptionsOperation(row, 0.5, null);
        Assert.Equal(("trim_layer captions_en_1, captions_ja_1", row), (operation.Label, operation.Layers));
        Assert.Equal("trimmed 'captions_en_1': now 0.5–2s (was 1–2s); trimmed 'captions_ja_1': now 0.5–2.2s (was 1–2.2s)", operation.Apply(project));

        // Only those not there already change.
        Assert.Equal(["trimmed 'captions_en_1': now 0.5–2.2s (was 0.5–2s)"], CaptionTracks.Retime(project, row, null, 2.2));
        Assert.Equal("the captions run to 2.2s already", Assert.Throws<ZeyoCutException>(() => CaptionTracks.Retime(project, row, null, 2.2)).Message);
        Assert.Equal("the end (0.4s) must be after the start (0.5s)", Assert.Throws<ZeyoCutException>(() => CaptionTracks.Retime(project, row, null, 0.4)).Message);
    }

    [Fact]
    public void RestylingSetsTheSizeLookWidthAndPlaceOfEveryTrack()
    {
        var measurer = new LineMeasurer();
        var project = Tracks(measurer);
        string[] groups = ["captions_en", "captions_ja"];
        var cues = groups.SelectMany(g => CaptionTracks.Cues(project, g)).ToList();
        var one = cues[0];
        Assert.Equal((CaptionLook.Box, CaptionPlace.Bottom), (CaptionTracks.LookOf(one), CaptionTracks.PlaceOf(project, one)));
        Assert.Equal((16.0, 5.0, 3.0), (one.FontSize, one.Box!.Padding, one.Box.CornerRadius));

        // The size: the box grows with it, and the anchor stays at the bottom of the text.
        Assert.Equal(3, CaptionTracks.Restyle(project, groups, new CaptionRestyle { FontSize = 32 }, measurer));
        Assert.All(cues, c => Assert.Equal(32, c.FontSize));
        Assert.Equal((10.0, 6.0), (one.Box.Padding, one.Box.CornerRadius));
        Assert.Equal(measurer.Measure(one).Height, one.AnchorY, 3);

        // What is behind the letters.
        CaptionTracks.Restyle(project, groups, new CaptionRestyle { Look = CaptionLook.Outline }, measurer);
        Assert.All(cues, c => Assert.Equal((CaptionLook.Outline, (TextBackground?)null, "#000000", 3.2), (CaptionTracks.LookOf(c), c.Box, c.StrokeColor, c.StrokeWidth)));
        CaptionTracks.Restyle(project, groups, new CaptionRestyle { Look = CaptionLook.None }, measurer);
        Assert.All(cues, c => Assert.Equal((CaptionLook.None, (string?)null, 0.0), (CaptionTracks.LookOf(c), c.StrokeColor, c.StrokeWidth)));
        CaptionTracks.Restyle(project, groups, new CaptionRestyle { Look = CaptionLook.Box }, measurer);
        Assert.All(cues, c => Assert.Equal((CaptionTracks.BoxColor, 10.0, 6.0), (c.Box!.Color, c.Box.Padding, c.Box.CornerRadius)));

        // The place, inside the title-safe margin, the box's padding in from it.
        var safe = ProjectChecker.SafeArea(project);
        Assert.Equal((18.0, 342.0), (safe.Top, safe.Bottom));
        CaptionTracks.Restyle(project, groups, new CaptionRestyle { Place = CaptionPlace.Top }, measurer);
        Assert.All(cues, c => Assert.Equal((0.0, 28.0, CaptionPlace.Top), (c.AnchorY, c.Y.Value, CaptionTracks.PlaceOf(project, c))));
        CaptionTracks.Restyle(project, groups, new CaptionRestyle { Place = CaptionPlace.Middle }, measurer);
        Assert.All(cues, c => Assert.Equal((Math.Round(measurer.Measure(c).Height / 2, 3), 180.0, CaptionPlace.Middle), (c.AnchorY, c.Y.Value, CaptionTracks.PlaceOf(project, c))));
        CaptionTracks.Restyle(project, groups, new CaptionRestyle { Place = CaptionPlace.Bottom, MaxWidth = 300 }, measurer);
        Assert.All(cues, c => Assert.Equal((Math.Round(measurer.Measure(c).Height, 3), 332.0, 300.0, 150.0), (c.AnchorY, c.Y.Value, c.MaxWidth!.Value, c.AnchorX)));

        // A font for one track; a font file it had is let go.
        project.Assets.Add(new Asset { Id = "brand", Type = AssetType.Font, File = "assets/brand.ttf" });
        cues[2].FontAsset = "brand";
        Assert.Equal("restyled 1 caption(s) in 'captions_ja': font Noto Sans JP",
            new RestyleCaptionsOperation(["captions_ja"], new CaptionRestyle { FontFamily = " Noto Sans JP " }, _ => measurer).Apply(project));
        Assert.Equal(("Segoe UI", "Noto Sans JP", (string?)null), (one.FontFamily, cues[2].FontFamily, cues[2].FontAsset));
        Assert.Empty(ProjectValidator.Validate(project));

        Assert.Equal("font Arial, size 40px, an outline, lines up to 500px, at the top",
            CaptionTracks.Describe(new CaptionRestyle { FontFamily = "Arial", FontSize = 40, Look = CaptionLook.Outline, MaxWidth = 500, Place = CaptionPlace.Top }));
        Assert.Equal("the size must be more than 0 px (got 0)", Assert.Throws<ZeyoCutException>(() => CaptionTracks.Restyle(project, groups, new CaptionRestyle { FontSize = 0 }, measurer)).Message);
        Assert.Equal("give the font's name, such as Segoe UI", Assert.Throws<ZeyoCutException>(() => CaptionTracks.Restyle(project, groups, new CaptionRestyle { FontFamily = " " }, measurer)).Message);
        one.Y.Keyframes = [new Keyframe { Time = 1, Value = 300 }];
        Assert.Equal("caption 'captions_en_1' moves up or down (its Y has keyframes), so it can't be put at the top; remove its Y keyframes first",
            Assert.Throws<ZeyoCutException>(() => CaptionTracks.Restyle(project, groups, new CaptionRestyle { Place = CaptionPlace.Top }, measurer)).Message);
    }

    [Fact]
    public void ALanguagesTrackIsATaggedCaptionGroupThatAddingAgainReplaces()
    {
        var project = NewProject();
        var (cues, _) = CaptionTracks.Check([new CueEntry(0, 1.5, "こんにちは"), new CueEntry(2, 3, "元気\nですか")], project.Duration);

        var track = CaptionTracks.Add(project, "ja", cues, Options("Yu Gothic"), [], null);

        Assert.True(track.Created);
        Assert.Equal("captions_ja", track.GroupId);
        var group = project.FindLayer("captions_ja")!;
        Assert.Equal("ja", group.Language);
        Assert.Equal("captions (ja)", group.Name);
        Assert.True(((ImageLayer)group).IgnoreCamera);
        Assert.Equal(["captions_ja_1", "captions_ja_2"], track.LayerIds);
        Assert.All(track.LayerIds, id => Assert.Equal("Yu Gothic", ((TextLayer)project.FindLayer(id)!).FontFamily));
        Assert.Equal("元気\nですか", ((TextLayer)project.FindLayer("captions_ja_2")!).Text);
        Assert.Empty(ProjectValidator.Validate(project));
        Assert.Equal(
            "added the ja caption track 'captions_ja': 2 caption(s), font Yu Gothic\n- 0–1.5s \"こんにちは\"\n- 2–3s \"元気 / ですか\"\n"
            + "'captions_ja' is only in the ja version: give language ja to render_frame or start_export to see it; export_srt with groupId captions_ja writes it as subtitles",
            CaptionTracks.Describe(track, "Yu Gothic"));

        var english = CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 2, "Hello")], project.Duration).Cues, Options(), [], null);
        Assert.Equal("captions_en", english.GroupId);
        var en = ProjectJson.Clone(project);
        Assert.Equal(["captions_ja", "captions_ja_1", "captions_ja_2"], Languages.Select(en, "en").Removed);

        var again = CaptionTracks.Add(project, "ja", CaptionTracks.Check([new CueEntry(4, 5, "さようなら")], project.Duration).Cues, Options("Yu Gothic"), [], null);
        Assert.False(again.Created);
        Assert.Equal(2, again.Replaced);
        Assert.Equal(["captions_ja_1"], ProjectEditor.Subtree(project, "captions_ja").Skip(1).Select(l => l.Id));
        Assert.StartsWith("replaced the ja caption track 'captions_ja': 1 caption(s) (removed 2 old caption layer(s)), font Yu Gothic", CaptionTracks.Describe(again, "Yu Gothic"));

        var none = Assert.Throws<ZeyoCutException>(() => CaptionTracks.Add(project, "my", [], Options(), ["cue 1 has no text, so it was left out"], null));
        Assert.Equal("none of the cues can be used:\n- cue 1 has no text, so it was left out", none.Message);

        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "captions_fr", Text = "Bonjour" });
        var taken = Assert.Throws<ZeyoCutException>(() => CaptionTracks.Add(project, "fr", english.Cues, Options(), [], null));
        Assert.Equal("layer 'captions_fr' exists and isn't a caption group (a null layer); add_caption_track keeps the fr captions under that id, so remove that layer first", taken.Message);
    }
}
