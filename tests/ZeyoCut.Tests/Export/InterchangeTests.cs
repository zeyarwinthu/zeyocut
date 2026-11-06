// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using System.Xml.Linq;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class InterchangeTests
{
    // 320x240 at 10 fps: clip 'a' at 0–3 s (source 0–3), 'b' at 3–5 s (source 4–6), 'pip' at half
    // size over them at 1–3 s (source 0–4 at 2x), music at 50% all along, a title at 0–2 s and two
    // markers. The folder's name has a space, as file URLs must encode.
    private static (Project Project, string Directory) Timeline(string name)
    {
        var directory = TestPaths.NewDirectory(name + " edit");
        var project = TestProjects.Empty(width: 320, height: 240, fps: 10, duration: 1);
        project.Name = "edit";
        project.Assets.Add(new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 320, Height = 240, FrameRate = "10", FrameCount = 60, Duration = 6, HasAudio = true, Codec = "h264" });
        project.Assets.Add(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.wav", Duration = 10 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "a", Start = 0, TrimIn = 0, TrimOut = 3 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "b", Start = 3, TrimIn = 4, TrimOut = 6 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "pip", Start = 1, TrimIn = 0, TrimOut = 4, Speed = 2, ScaleX = 0.5, ScaleY = 0.5 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "music", Volume = 0.5 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", FontFamily = "Segoe UI", FontSize = 30, Color = "#FFFFFF", Start = 0, End = 2 });
        MarkerEditor.AddMarkers(project, [new MarkerEntry(null, 0, "Intro", null), new MarkerEntry(null, 2.5, "Park", "scene")]);
        return (project, directory);
    }

    private static string[] Clips(PlannedTrack track) =>
        track.Clips.Select(c => $"{c.LayerId} {c.Start:0.###}+{c.Length:0.###} from {c.From:0.###} x{c.Speed:0.###}").ToArray();

    [Fact]
    public void AnEditPlanPutsClipsOnTracksInDrawOrderAndNamesWhatItCantHold()
    {
        var (project, directory) = Timeline("plan");
        var plan = EditPlan.Make(project, directory, Path.Combine(directory, "exports", "edit_prerender"));

        Assert.Equal(["V1", "V2", "A1", "A2", "A3"], plan.Tracks.Select(t => t.Name));
        Assert.Equal(["a 0+3 from 0 x1", "b 3+2 from 4 x1"], Clips(plan.Tracks[0]));
        Assert.Equal(["pip 1+2 from 0 x2"], Clips(plan.Tracks[1]));
        Assert.Equal(["a 0+3 from 0 x1", "b 3+2 from 4 x1"], Clips(plan.Tracks[2]));
        Assert.Equal(["music 0+5 from 0 x1"], Clips(plan.Tracks[3]));
        Assert.Equal(["pip 1+2 from 0 x2"], Clips(plan.Tracks[4]));
        Assert.All(plan.Tracks, t => Assert.False(t.Prerendered));
        Assert.Equal([false, false, true, true, true], plan.Tracks.Select(t => t.Audio));
        Assert.Equal(Path.Combine(directory, "assets", "clip.mp4"), plan.Tracks[0].Clips[0].Media);
        Assert.Equal(6, plan.Tracks[0].Clips[0].MediaLength);
        Assert.Equal(["intro", "park"], plan.Markers.Select(m => m.Id));
        Assert.Empty(plan.Stale);
        Assert.Equal(
            [
                "the place and size of 'pip' (another editor fits it to the frame)",
                "layers only ZeyoCut draws: text 'title'; prerender_layers draws them into video with alpha for the timeline",
                "the volume of 'music' at 50%",
                "the music ducking under the voice",
                "the loudness normalization to -14 LUFS",
            ],
            plan.Missing);

        ProjectEditor.UpdateLayer(project, "pip", new JsonObject { ["scaleX"] = 1, ["scaleY"] = 1 });
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("pip", "opacity", 1, System.Text.Json.JsonSerializer.SerializeToElement(0)), new KeyframeEntry("pip", "opacity", 1.5, System.Text.Json.JsonSerializer.SerializeToElement(1))]);
        EffectEditor.SetEffect(project, "b", EffectKind.Shadow, new EffectValues(null, null, null, null, null, null, null), false);
        project.Audio.Ducking = false;
        project.Audio.LoudnessTarget = null;
        plan = EditPlan.Make(project, directory, Path.Combine(directory, "exports", "edit_prerender"));
        Assert.Equal(
            [
                "the shadow of 'b'",
                "keyframes on 'pip' (opacity)",
                "layers only ZeyoCut draws: text 'title'; prerender_layers draws them into video with alpha for the timeline",
                "the volume of 'music' at 50%",
            ],
            plan.Missing);
    }

    [Fact]
    public void OtioHoldsTheTracksClipsSpeedAndMarkers()
    {
        var (project, directory) = Timeline("otio");
        var otio = OtioExport.Json(EditPlan.Make(project, directory, Path.Combine(directory, "exports", "edit_prerender")));

        Assert.Equal("Timeline.1", otio["OTIO_SCHEMA"]!.GetValue<string>());
        Assert.Equal("edit", otio["name"]!.GetValue<string>());
        var stack = otio["tracks"]!;
        Assert.Equal("Stack.1", stack["OTIO_SCHEMA"]!.GetValue<string>());
        var tracks = stack["children"]!.AsArray();
        Assert.Equal(["V1:Video", "V2:Video", "A1:Audio", "A2:Audio", "A3:Audio"], tracks.Select(t => $"{t!["name"]}:{t["kind"]}"));
        Assert.All(tracks, t => Assert.Equal("Track.1", t!["OTIO_SCHEMA"]!.GetValue<string>()));

        static double Value(JsonNode range, string part) => range[part]!["value"]!.GetValue<double>();

        var v1 = tracks[0]!["children"]!.AsArray();
        Assert.Equal(["Clip.2", "Clip.2"], v1.Select(c => c!["OTIO_SCHEMA"]!.GetValue<string>()));
        Assert.Equal(40, Value(v1[1]!["source_range"]!, "start_time"));
        Assert.Equal(20, Value(v1[1]!["source_range"]!, "duration"));
        Assert.Equal(10, v1[1]!["source_range"]!["duration"]!["rate"]!.GetValue<double>());

        var v2 = tracks[1]!["children"]!.AsArray();
        Assert.Equal(["Gap.1", "Clip.2"], v2.Select(c => c!["OTIO_SCHEMA"]!.GetValue<string>()));
        Assert.Equal(10, Value(v2[0]!["source_range"]!, "duration"));
        var pip = v2[1]!;
        Assert.Equal("pip", pip["name"]!.GetValue<string>());
        Assert.Equal(0, Value(pip["source_range"]!, "start_time"));
        Assert.Equal(20, Value(pip["source_range"]!, "duration"));
        Assert.Equal("LinearTimeWarp.1", pip["effects"]![0]!["OTIO_SCHEMA"]!.GetValue<string>());
        Assert.Equal(2, pip["effects"]![0]!["time_scalar"]!.GetValue<double>());
        Assert.Equal("DEFAULT_MEDIA", pip["active_media_reference_key"]!.GetValue<string>());
        var media = pip["media_references"]!["DEFAULT_MEDIA"]!;
        Assert.Equal("ExternalReference.1", media["OTIO_SCHEMA"]!.GetValue<string>());
        var url = media["target_url"]!.GetValue<string>();
        Assert.StartsWith("file:///", url);
        Assert.Contains("%20edit/assets/clip.mp4", url);
        Assert.Equal(Path.Combine(directory, "assets", "clip.mp4"), new Uri(url).LocalPath);
        Assert.Equal(60, Value(media["available_range"]!, "duration"));
        Assert.Empty(v1[0]!["effects"]!.AsArray());

        var markers = stack["markers"]!.AsArray();
        Assert.Equal(["Intro GREEN 0", "Park BLUE 25"], markers.Select(m => $"{m!["name"]} {m["color"]} {Value(m["marked_range"]!, "start_time")}"));
        Assert.Equal("scene", markers[1]!["metadata"]!["zeyocut"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void FcpxmlHasTheClipsOnTheSpineAndConnectedToItWithTheMarkers()
    {
        var (project, directory) = Timeline("fcpxml");
        var document = FcpxmlExport.Document(EditPlan.Make(project, directory, Path.Combine(directory, "exports", "edit_prerender")));
        var root = document.Root!;
        Assert.Equal("fcpxml", document.DocumentType!.Name);
        Assert.Equal("1.9", (string?)root.Attribute("version"));

        static string Attributes(XElement element, params string[] names) => string.Join(" ", names.Select(n => $"{n}={(string?)element.Attribute(n)}"));

        var resources = root.Element("resources")!.Elements().ToList();
        Assert.Equal(["format id=r1 width=320 height=240 frameDuration=1/10s", "asset id=r2 name=clip hasVideo=1 hasAudio=1 format=r1 duration=60/10s", "asset id=r3 name=music hasVideo=0 hasAudio=1 format= duration=100/10s"],
            resources.Select(e => e.Name.LocalName + " " + (e.Name == "format" ? Attributes(e, "id", "width", "height", "frameDuration") : Attributes(e, "id", "name", "hasVideo", "hasAudio", "format", "duration"))));
        Assert.Equal(new Uri(Path.Combine(directory, "assets", "clip.mp4")).AbsoluteUri, (string?)resources[1].Element("media-rep")!.Attribute("src"));

        var sequence = root.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!;
        Assert.Equal("format=r1 duration=50/10s", Attributes(sequence, "format", "duration"));
        var spine = sequence.Element("spine")!.Elements().ToList();
        Assert.Equal(["a offset=0s start=0s duration=30/10s srcEnable=video", "b offset=30/10s start=40/10s duration=20/10s srcEnable=video"],
            spine.Select(e => $"{(string?)e.Attribute("name")} {Attributes(e, "offset", "start", "duration", "srcEnable")}"));

        var a = spine[0].Elements().ToList();
        Assert.Equal(["asset-clip", "asset-clip", "asset-clip", "asset-clip", "chapter-marker", "marker"], a.Select(e => e.Name.LocalName));
        Assert.Equal(
            [
                "pip lane=1 ref=r2 offset=10/10s start=0s duration=20/10s srcEnable=video",
                "a lane=-1 ref=r2 offset=0s start=0s duration=30/10s srcEnable=audio",
                "music lane=-2 ref=r3 offset=0s start=0s duration=50/10s srcEnable=",
                "pip lane=-3 ref=r2 offset=10/10s start=0s duration=20/10s srcEnable=audio",
            ],
            a.Where(e => e.Name == "asset-clip").Select(e => $"{(string?)e.Attribute("name")} {Attributes(e, "lane", "ref", "offset", "start", "duration", "srcEnable")}"));
        Assert.Equal(["time=0s value=0s interp=linear", "time=30/10s value=60/10s interp=linear"], a[0].Element("timeMap")!.Elements("timept").Select(p => Attributes(p, "time", "value", "interp")));
        Assert.Equal("start=0s duration=1/10s value=Intro", Attributes(a[4], "start", "duration", "value"));
        Assert.Equal("start=25/10s duration=1/10s value=Park", Attributes(a[5], "start", "duration", "value"));
        Assert.Equal(["b lane=-1 offset=40/10s start=40/10s"], spine[1].Elements("asset-clip").Select(e => $"{(string?)e.Attribute("name")} {Attributes(e, "lane", "offset", "start")}"));
    }

    [Fact]
    public void FcpxmlFillsTheSpineWithGapsAroundAndWithoutClips()
    {
        var (project, directory) = Timeline("fcpxml-gaps");
        ProjectEditor.UpdateLayer(project, "a", new JsonObject { ["start"] = 1 });
        ProjectEditor.UpdateLayer(project, "b", new JsonObject { ["start"] = 5 });
        project.Duration = 8;
        var spine = FcpxmlExport.Document(EditPlan.Make(project, directory, Path.Combine(directory, "exports", "edit_prerender")))
            .Root!.Descendants("spine").Single().Elements().ToList();
        Assert.Equal(["gap 0s+10/10s", "asset-clip 10/10s+30/10s", "gap 40/10s+10/10s", "asset-clip 50/10s+20/10s", "gap 70/10s+10/10s"],
            spine.Select(e => $"{e.Name.LocalName} {(string?)e.Attribute("offset")}+{(string?)e.Attribute("duration")}"));
        Assert.Equal("40/10s", (string?)spine[2].Attribute("start"));
        Assert.Contains(spine[0].Elements(), e => e.Name == "chapter-marker" && (string?)e.Attribute("start") == "0s");

        project.Layers.RemoveAll(l => l is VideoLayer);
        spine = FcpxmlExport.Document(EditPlan.Make(project, directory, Path.Combine(directory, "exports", "edit_prerender")))
            .Root!.Descendants("spine").Single().Elements().ToList();
        Assert.Equal(["gap 0s+80/10s"], spine.Select(e => $"{e.Name.LocalName} {(string?)e.Attribute("offset")}+{(string?)e.Attribute("duration")}"));
        Assert.Equal(["music lane=-1"], spine[0].Elements("asset-clip").Select(e => $"{(string?)e.Attribute("name")} lane={(string?)e.Attribute("lane")}"));
    }

    [Fact]
    public void PrerenderedLayersGoAboveTheClipsWhileTheyAreCurrent()
    {
        var ffmpeg = Ffmpeg.Require();
        var (project, directory) = Timeline("prerendered");
        var folder = Path.Combine(directory, "exports", "edit_prerender");
        Prerender.Make(project, directory, folder, "title", ffmpeg, cancellationToken: TestContext.Current.CancellationToken);

        var plan = EditPlan.Make(project, directory, folder);
        Assert.Equal(["V1", "V2", "V3", "A1", "A2", "A3"], plan.Tracks.Select(t => t.Name));
        var overlay = plan.Tracks[2];
        Assert.True(overlay.Prerendered);
        Assert.Equal(["title 0+2 from 0 x1"], Clips(overlay));
        Assert.Equal(Path.Combine(folder, "title.mov"), overlay.Clips[0].Media);
        Assert.Equal(2, overlay.Clips[0].MediaLength);
        Assert.DoesNotContain(plan.Missing, m => m.Contains("'title'"));
        Assert.Equal("title (prerendered)", OtioExport.Json(plan)["tracks"]!["children"]![2]!["children"]![0]!["name"]!.GetValue<string>());

        ProjectEditor.UpdateLayer(project, "title", new JsonObject { ["text"] = "Hello" });
        plan = EditPlan.Make(project, directory, folder);
        Assert.Equal(["title"], plan.Stale);
        Assert.DoesNotContain(plan.Tracks, t => t.Prerendered);
        Assert.Contains("layers only ZeyoCut draws: text 'title'; prerender_layers draws them into video with alpha for the timeline", plan.Missing);
    }
}
