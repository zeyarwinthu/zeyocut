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
