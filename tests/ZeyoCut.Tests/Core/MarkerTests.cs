// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Tests.Core;

public class MarkerTests
{
    private static Project NewProject(double duration = 60) => new() { Name = "demo", Width = 640, Height = 360, Fps = 30, Duration = duration };

    private static MarkerEntry Entry(double time, string? name, string? kind = null, string? id = null) => new(id, time, name, kind);

    private static Project WithClips(params (string Id, double Start, double Length)[] clips)
    {
        var project = NewProject(clips.Max(c => c.Start + c.Length));
        project.Assets.Add(new Asset
        {
            Id = "clip",
            Type = AssetType.Video,
            File = "assets/clip.mp4",
            Width = 640,
            Height = 360,
            FrameRate = "30",
            FrameCount = 1800,
            Duration = 60,
            HasAudio = true,
            Codec = "h264",
        });
        foreach (var (id, start, length) in clips)
        {
            ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = id, Start = start, TrimIn = 0, TrimOut = length });
        }

        return project;
    }

    private static string[] Times(Project project) => project.Markers.Select(m => $"{m.Id}@{m.Time:0.###}").ToArray();

    [Fact]
    public void AddMarkersMakesIdsFromNamesAndKeepsThemInTimeOrder()
    {
        var project = NewProject();
        var change = MarkerEditor.AddMarkers(project, [Entry(30, "The chase", "scene"), Entry(0, " Intro "), Entry(12.5, "Intro"), Entry(40, "はじめに")]);

        Assert.Equal(["the_chase", "intro", "intro_2", "chapter"], change.Added.Select(m => m.Id));
        Assert.Empty(change.Changed);
        Assert.Empty(change.Warnings);
        Assert.Equal(["intro@0", "intro_2@12.5", "the_chase@30", "chapter@40"], Times(project));
        Assert.Equal("Intro", project.FindMarker("intro")!.Name);
        Assert.Equal(MarkerKind.Scene, project.FindMarker("the_chase")!.Kind);
        Assert.Equal(MarkerKind.Chapter, project.FindMarker("chapter")!.Kind);

        change = MarkerEditor.AddMarkers(project, [Entry(45, "Part 2", id: "intro_2"), Entry(60, "Credits", id: "credits"), Entry(45, "Also part 2")]);
        Assert.Equal(["credits", "also_part_2"], change.Added.Select(m => m.Id));
        Assert.Equal(["intro_2"], change.Changed.Select(m => m.Id));
        Assert.Equal(["intro@0", "the_chase@30", "chapter@40", "intro_2@45", "also_part_2@45", "credits@60"], Times(project));
        Assert.Equal("Part 2", project.FindMarker("intro_2")!.Name);
        Assert.Equal(MarkerKind.Chapter, project.FindMarker("intro_2")!.Kind);
        Assert.Equal(["chapter markers 'intro_2', 'also_part_2' are at the same time (45s)"], change.Warnings);

        MarkerEditor.AddMarkers(project, [Entry(31, "The chase", "chapter", "the_chase")]);
        Assert.Equal(MarkerKind.Chapter, project.FindMarker("the_chase")!.Kind);
        Assert.Equal("the_chase chapter at 31s \"The chase\"", MarkerEditor.Describe(project.FindMarker("the_chase")!));
        Assert.Contains("markers: intro chapter at 0s \"Intro\"; the_chase chapter at 31s \"The chase\"; chapter chapter at 40s \"はじめに\";", LayerTree.Format(project));
    }

    [Fact]
    public void AddMarkersRefusesWhatIsNotAMarker()
    {
        var project = NewProject();
        MarkerEditor.AddMarkers(project, [Entry(5, "Intro")]);

        void Refused(string message, params MarkerEntry[] entries) =>
            Assert.Equal(message, Assert.Throws<ZeyoCutException>(() => MarkerEditor.AddMarkers(project, entries)).Message);

        Refused("give at least one marker: [{time, name, kind}]");
        Refused("marker 1 (\"Late\") is at 61s; markers go from 0 to the project end (60s)", Entry(61, "Late"));
        Refused("marker 'early' (\"Early\") is at -1s; markers go from 0 to the project end (60s)", Entry(-1, "Early", id: "early"));
        Refused("marker 2 (\"Odd\") is at NaNs; markers go from 0 to the project end (60s)", Entry(1, "Fine"), Entry(double.NaN, "Odd"));
        Refused("marker 1 has no name", Entry(1, "  "));
        Refused("marker 1 has no name", Entry(1, null));
        Refused("the name of marker 1 must be one line of text", Entry(1, "Part\nTwo"));
        Refused("unknown marker kind 'act'; valid values: chapter, scene", Entry(1, "Act one", "act"));
        Refused("marker 'a' is given twice", Entry(1, "A", id: "a"), Entry(2, "B", id: "a"));
        Refused("invalid marker id 'bad id': use letters, digits, '_' or '-' (max 64 characters), starting with a letter or digit", Entry(1, "Bad", id: "bad id"));
        Assert.Equal(["intro@5"], Times(project));
    }

    [Fact]
    public void RemoveMarkersByIdAllOrKind()
    {
        var project = NewProject();
        MarkerEditor.AddMarkers(project, [Entry(0, "Intro"), Entry(10, "Street", "scene"), Entry(20, "Park", "scene"), Entry(30, "Outro")]);

        void Refused(string message, string[]? ids, string? kind) =>
            Assert.Equal(message, Assert.Throws<ZeyoCutException>(() => MarkerEditor.RemoveMarkers(project, ids, kind)).Message);

        Refused("give markerIds (or [\"all\"]) or a kind", null, null);
        Refused("give markerIds (or [\"all\"]) or a kind", [], null);
        Refused("marker 'nope' not found; markers: intro, street, park, outro", ["intro", "nope"], null);
        Refused("give markerIds or a kind, not both (a kind goes with markerIds [\"all\"] or none)", ["intro"], "chapter");
        Refused("unknown marker kind 'act'; valid values: chapter, scene", null, "act");

        Assert.Equal(["street", "park"], MarkerEditor.RemoveMarkers(project, null, "scene").Select(m => m.Id));
        Assert.Equal(["intro@0", "outro@30"], Times(project));
        Refused("the project has no scene markers; nothing was changed", ["all"], "scene");
        Assert.Equal(["outro"], MarkerEditor.RemoveMarkers(project, ["outro", "outro"], null).Select(m => m.Id));
        Assert.Equal(["intro"], MarkerEditor.RemoveMarkers(project, ["ALL"], null).Select(m => m.Id));
        Refused("the project has no markers; nothing was changed", ["all"], null);
    }

    [Fact]
    public void RippleEditsMoveMarkersWithTheTimeAroundThem()
    {
        var project = WithClips(("talk", 0, 20));
        MarkerEditor.AddMarkers(project, [Entry(2, "Hello"), Entry(6, "Inside"), Entry(8, "At the end of the cut"), Entry(10, "Later"), Entry(20, "Last")]);

        TimelineEditor.RemoveRange(project, null, 5, 8, ripple: false);
        Assert.Equal(["hello@2", "inside@6", "at_the_end_of_the_cut@8", "later@10", "last@20"], Times(project));

        TimelineEditor.RemoveRange(project, null, 5, 8, ripple: true);
        Assert.Equal(["hello@2", "inside@5", "at_the_end_of_the_cut@5", "later@7", "last@17"], Times(project));
        Assert.Equal(17, project.Duration);

        project = WithClips(("a", 0, 10), ("b", 10, 10));
        MarkerEditor.AddMarkers(project, [Entry(4, "In A"), Entry(10, "B starts"), Entry(15, "In B")]);
        TimelineEditor.SetSpeed(project, "a", 2, ripple: true);
        Assert.Equal(["in_a@4", "b_starts@5", "in_b@10"], Times(project));
        TimelineEditor.SetSpeed(project, "a", 0.5, ripple: true);
        Assert.Equal(["in_a@4", "b_starts@20", "in_b@25"], Times(project));
        TimelineEditor.SetSpeed(project, "a", 1, ripple: false);
        Assert.Equal(["in_a@4", "b_starts@20", "in_b@25"], Times(project));
    }

    [Fact]
    public void MarkersAreSavedOnlyWhenThereAreSomeAndLoadInTimeOrder()
    {
        var project = NewProject();
        Assert.DoesNotContain("\"markers\"", ProjectJson.Serialize(project));

        MarkerEditor.AddMarkers(project, [Entry(30, "Park", "scene"), Entry(0, "Intro")]);
        var json = ProjectJson.Serialize(project);
        Assert.Contains("\"kind\": \"scene\"", json);
        Assert.Contains("\"kind\": \"chapter\"", json);

        var reordered = json.Replace("\"time\": 30,", "\"time\": -2,").Replace("\"time\": 0,", "\"time\": 50,");
        var loaded = ProjectJson.Deserialize(reordered);
        Assert.Equal(["park@-2", "intro@50"], Times(loaded));
        Assert.Equal(["marker 'park': time must be 0 or more seconds"], ProjectValidator.Validate(loaded));

        loaded.Markers[0].Time = 1;
        loaded.Markers[0].Id = "intro";
        loaded.Markers[1].Name = "Two\tlines";
        Assert.Equal(["duplicate marker id 'intro'", "marker 'intro': the name must be one line of text"], ProjectValidator.Validate(loaded));
    }

    [Fact]
    public void YouTubeChaptersListTheChapterMarkersInWholeSeconds()
    {
        var project = NewProject(3730);
        MarkerEditor.AddMarkers(project, [Entry(3725.4, "Late part"), Entry(30, "Park", "scene"), Entry(0, "Intro"), Entry(12.7, "Part 1"), Entry(59.9999995, "Minute")]);

        var list = YouTubeChapters.Make(project);
        Assert.Equal("0:00 Intro\n0:12 Part 1\n1:00 Minute\n1:02:05 Late part\n", list.Text);
        Assert.Equal(["intro", "part_1", "minute", "late_part"], list.Chapters.Select(m => m.Id));
        Assert.Equal(["'late_part' (\"Late part\") lasts 5s from 1:02:05; YouTube needs at least 10 s per chapter"], list.Warnings);

        Assert.Equal("0:59", YouTubeChapters.Stamp(59.99));
        Assert.Equal("1:00:00", YouTubeChapters.Stamp(3600));
        Assert.Equal("10:00:01", YouTubeChapters.Stamp(36001));
    }

    [Fact]
    public void YouTubeChaptersWarnAboutEachRuleTheyBreak()
    {
        var project = NewProject();
        MarkerEditor.AddMarkers(project, [Entry(3, "Intro"), Entry(8.5, "Next")]);

        var list = YouTubeChapters.Make(project);
        Assert.Equal("0:03 Intro\n0:08 Next\n", list.Text);
        Assert.Equal(
            [
                "YouTube needs the first chapter at 0:00, but the first one, 'intro' (\"Intro\"), is at 0:03; add a chapter at 0 or move this one there with add_markers",
                "YouTube needs at least 3 chapters; there are 2",
                "'intro' (\"Intro\") lasts 5s from 0:03; YouTube needs at least 10 s per chapter",
            ],
            list.Warnings);

        MarkerEditor.RemoveMarkers(project, ["next"], null);
        Assert.Contains("YouTube needs at least 3 chapters; there is 1", YouTubeChapters.Make(project).Warnings);

        MarkerEditor.AddMarkers(project, [Entry(0, "Start"), Entry(20, "Middle"), Entry(40, "End")]);
        Assert.Equal(
            ["'start' (\"Start\") lasts 3s from 0:00; YouTube needs at least 10 s per chapter"],
            YouTubeChapters.Make(project).Warnings);

        Assert.Equal("the project has no markers; add chapters with add_markers", Assert.Throws<ZeyoCutException>(() => YouTubeChapters.Make(NewProject())).Message);
        var scenes = NewProject();
        MarkerEditor.AddMarkers(scenes, [Entry(0, "Park", "scene")]);
        Assert.Equal("the project has scene markers but no chapters; add chapters with add_markers (kind chapter is the default)", Assert.Throws<ZeyoCutException>(() => YouTubeChapters.Make(scenes)).Message);
    }

    [Fact]
    public void ShorteningTheProjectWarnsAboutMarkersPastTheNewEnd()
    {
        var project = NewProject();
        MarkerEditor.AddMarkers(project, [Entry(0, "Intro"), Entry(45, "Part 2"), Entry(60, "Credits")]);

        var update = ProjectEditor.UpdateProject(project, new ProjectOptions { Duration = 40 });
        Assert.Contains("markers after the new end (40s) are past the video: part_2 (45s), credits (60s); remove_markers removes them", update.Warnings);
        Assert.Empty(ProjectValidator.Validate(project));
    }
}
