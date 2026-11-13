// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Tests.TestSupport;

public sealed class FakeAssetProbe : IAssetProbe
{
    public AssetInfo Probe(string path, AssetType type) => type switch
    {
        AssetType.Image => new AssetInfo(Width: 200, Height: 100),
        AssetType.Audio => new AssetInfo(Duration: 1.5),
        _ => new AssetInfo(FontFamily: "Fake Font"),
    };
}

public static class TestProjects
{
    public static Project Empty(int width = 640, int height = 360, int fps = 30, double duration = 3) => new()
    {
        Name = "test",
        Width = width,
        Height = height,
        Fps = fps,
        Duration = duration,
        BackgroundColor = "#FFFFFF",
    };

    public static Project WithImageAssets(params string[] ids)
    {
        var project = Empty();
        foreach (var id in ids)
        {
            project.Assets.Add(new Asset { Id = id, Type = AssetType.Image, File = $"assets/{id}.png", Width = 200, Height = 100 });
        }

        return project;
    }

    public static Project Rig()
    {
        var project = WithImageAssets("body", "head", "head_shocked", "tail");
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "tora_body",
            Drawings = new Dictionary<string, string> { ["normal"] = "body" },
            ZIndex = 10,
        });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "tora_head",
            ParentId = "tora_body",
            Drawings = new Dictionary<string, string> { ["normal"] = "head", ["shocked"] = "head_shocked" },
            ZIndex = 11,
        });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "tora_tail",
            ParentId = "tora_body",
            Drawings = new Dictionary<string, string> { ["normal"] = "tail" },
            ZIndex = 9,
        });
        return project;
    }

    // Two clips of one 10 s video back to back: clip_1 shows source 0–4 s at 0–4 s (fade in 0.5 s)
    // and clip_2 source 4–8 s at 4–8 s (fade out 0.5 s), with the text 'label' under clip_2. Music
    // (20 s long, volume 0.8, fade out 1 s) plays 0–8 s and a title shows 0–3 s. 8 s long.
    public static Project Footage()
    {
        var project = Empty(duration: 8);
        project.Assets.Add(new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 640, Height = 360, FrameRate = "30", FrameCount = 300, Duration = 10, HasAudio = true, Codec = "h264" });
        project.Assets.Add(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.wav", Duration = 20 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip_1", Start = 0, TrimIn = 0, TrimOut = 4, FadeIn = 0.5 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip_2", Start = 4, TrimIn = 4, TrimOut = 8, FadeOut = 0.5 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "label", Text = "Second", ParentId = "clip_2", Start = 4, End = 8, ZIndex = 5 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "music", End = 8, Volume = 0.8, FadeOut = 1 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 0, End = 3, ZIndex = 10 });
        return project;
    }

    // An English caption track of two captions, 0–2 s and 2–4 s, as add_caption_track adds it.
    public static CaptionTrack AddCaptions(Project project) =>
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 2, "Hello"), new CueEntry(2, 4, "World")], project.Duration).Cues, new CaptionOptions(), [], null);

    // project.json's text with its revision left out, to compare content saved as different revisions.
    public static string WithoutRevision(string json) => Regex.Replace(json, @"""revision"": \d+,\r?\n\s*", "");
}
