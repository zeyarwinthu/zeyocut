// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The inspector's fields: what they show, and the edit each one makes.
public class InspectorTests
{
    // A clip with a label attached, music, a title, and an English caption track of two captions.
    private static Project Footage()
    {
        var project = TestFiles.Project(640, 360, duration: 8);
        project.Assets.Add(new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 640, Height = 360, FrameRate = "30", FrameCount = 300, Duration = 10, HasAudio = true, Codec = "h264" });
        project.Assets.Add(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.wav", Duration = 20 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "clip", Start = 0, TrimIn = 4, TrimOut = 8 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "label", Text = "Second", ParentId = "clip", Start = 0, End = 4, ZIndex = 5 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "music", End = 8 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 0, End = 3, ZIndex = 10 });
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 2, "Hello"), new CueEntry(2, 4, "World")], project.Duration).Cues, new CaptionOptions(), [], null);
        return project;
    }

    private static List<InspectorField> Fields(Project project, string id) =>
        InspectorFields.Layer(project, project.FindLayer(id)!, TimelineLayout.Lanes(project).SelectMany(l => l.Clips).FirstOrDefault(c => c.LayerId == id));

    private static InspectorField Field(Project project, string id, string label) => Fields(project, id).Single(f => f.Label == label && f.Kind != FieldKind.Section);

    // Types text into a field and makes the edit it gives, as pressing Enter does.
    private static string Commit(Project project, string id, string label, string text) => Field(project, id, label).Commit!(text).Apply(project);

    private static string Error(Project project, string id, string label, string text) =>
        Assert.Throws<ZeyoCutException>(() => Commit(project, id, label, text)).Message;

    [Fact]
    public void AClipsFieldsAreItsTimingLevelAndSound()
    {
        var project = Footage();
        var fields = Fields(project, "clip");
        Assert.Equal(
            ["Clip", "Id", "Start", "End", "Length", "Draw level", "Shown", "Child layers", "Video", "Asset", "Plays", "Speed", "Sound", "Sound on", "Role", "Volume", "Fade in", "Fade out"],
            fields.Select(f => f.Label));
        Assert.Equal(["0", "4", "4", "0", "true", "label", "4 – 8 s of the file", "1×", "true", "voice", "1"],
            new[] { "Start", "End", "Length", "Draw level", "Shown", "Child layers", "Plays", "Speed", "Sound on", "Role", "Volume" }.Select(l => fields.Single(f => f.Label == l).Value));
        Assert.All(fields.Where(f => f.Kind is FieldKind.Section or FieldKind.Info), f => Assert.False(f.Editable));
        Assert.Equal(["voice", "music", "effects"], Field(project, "clip", "Role").Options.Select(o => o.Name));

        // Start moves the clip with the label; End and Length trim it, as dragging on the timeline does.
        Assert.StartsWith("moved 'clip' with 1 child layer(s) by +1s", Commit(project, "clip", "Start", "1"));
        Assert.Equal((1.0, 1.0), (project.FindLayer("clip")!.Start, project.FindLayer("label")!.Start));
        Assert.StartsWith("trimmed 'clip'", Commit(project, "clip", "Length", "2,5 s"));
        Assert.Equal(3.5, TimelineEditor.ContentEnd(project, project.FindLayer("clip")!), 9);
        Commit(project, "clip", "End", "4.5");
        Assert.Equal((4.5, 7.5), (TimelineEditor.ContentEnd(project, project.FindLayer("clip")!), ((VideoLayer)project.FindLayer("clip")!).TrimOut));

        Commit(project, "clip", "Draw level", "2");
        Assert.Equal(2, ((VisualLayer)project.FindLayer("clip")!).ZIndex);
        Commit(project, "clip", "Sound on", "false");
        Commit(project, "clip", "Volume", "0.5");
        Commit(project, "clip", "Fade out", "1 s");
        Commit(project, "clip", "Role", "music");
        var video = (VideoLayer)project.FindLayer("clip")!;
        Assert.Equal((true, 0.5, 1.0, AudioRole.Music), (video.Muted, video.Volume, video.FadeOut, video.Role));
        Commit(project, "clip", "Shown", "false");
        Assert.False(project.FindLayer("clip")!.Visible);
    }

    [Fact]
    public void WhatCantBeSavedSaysWhy()
    {
        var project = Footage();
        var before = Core.Serialization.ProjectJson.Serialize(project);
        Assert.Equal("Start must be a number, like 1.5 (got 'soon')", Error(project, "clip", "Start", "soon"));
        Assert.Equal("Start can't be less than 0 (got -1)", Error(project, "clip", "Start", "-1"));
        Assert.Equal("Length must be more than 0 (got 0)", Error(project, "clip", "Length", "0"));
        Assert.Equal("Draw level must be a whole number (got 1.5)", Error(project, "clip", "Draw level", "1.5"));
        Assert.Equal("Volume can't be more than 10 (got 11)", Error(project, "clip", "Volume", "11"));
        Assert.Equal("Font can't be empty", Error(project, "title", "Font", "  "));
        Assert.Equal(before, Core.Serialization.ProjectJson.Serialize(project));
    }

    [Fact]
    public void NumbersCanBeTypedWithACommaOrAUnit()
    {
        Assert.Equal(1.5, InspectorFields.Parse("1,5", "Start"));
        Assert.Equal(2, InspectorFields.Parse(" 2 s ", "Start"));
        Assert.Equal(40, InspectorFields.Parse("40px", "Size"));
        Assert.Equal(0.25, InspectorFields.Parse(".25×", "Volume"));
        Assert.Equal(3, InspectorFields.Parse("3x", "Volume"));
        Assert.Equal(1000, InspectorFields.Parse("1e3", "Size"));
        Assert.Throws<ZeyoCutException>(() => InspectorFields.Parse("1.5 min", "Start"));
        Assert.Throws<ZeyoCutException>(() => InspectorFields.Parse("", "Start"));
    }

    [Fact]
    public void SoundClipsAreMutedAndText()
    {
        var project = Footage();
        Assert.Equal(["Clip", "Id", "Start", "End", "Length", "Muted", "Sound", "Asset", "Plays", "Role", "Volume", "Fade in", "Fade out"],
            Fields(project, "music").Select(f => f.Label));
        Assert.Equal("0 – the end of the file", Field(project, "music", "Plays").Value);

        // Muting sound hides its layer, as audio layers have no other switch.
        Commit(project, "music", "Muted", "true");
        Assert.False(project.FindLayer("music")!.Visible);
        Assert.Equal("true", Field(project, "music", "Muted").Value);

        // Text keeps its lines; Shift+Enter in the box types Windows line endings.
        Commit(project, "title", "Text", "Hello\r\nthere");
        Assert.Equal("Hello\nthere", ((TextLayer)project.FindLayer("title")!).Text);
    }

    [Fact]
    public void StyleFieldsSetAndClearTheTextsLook()
    {
        var project = Footage();
        var labels = Fields(project, "title").Select(f => f.Label).ToList();
        Assert.Equal(["Text", "Text", "Style", "Font", "Size", "Bold", "Color", "Outline", "Outline width", "Align", "Max width", "Line height", "Box"], labels[labels.IndexOf("Text")..]);

        Commit(project, "title", "Size", "48");
        Commit(project, "title", "Bold", "true");
        Commit(project, "title", "Color", "#FFCC00");
        Commit(project, "title", "Align", "left");
        Commit(project, "title", "Max width", "300");
        Commit(project, "title", "Box", "#00000080");
        var title = (TextLayer)project.FindLayer("title")!;
        Assert.Equal((48.0, true, "#FFCC00", TextAlign.Left, 300.0, "#00000080"), (title.FontSize, title.Bold, title.Color, title.Align, title.MaxWidth, title.Box!.Color));

        // With a box, its padding and corners can be set too; the rest of the box stays.
        Commit(project, "title", "Box padding", "12");
        Commit(project, "title", "Box corners", "6");
        title = (TextLayer)project.FindLayer("title")!;
        Assert.Equal(("#00000080", 12.0, 6.0), (title.Box!.Color, title.Box.Padding, title.Box.CornerRadius));

        // Empty takes optional style off.
        Commit(project, "title", "Outline", "#000000");
        Assert.Equal("#000000", ((TextLayer)project.FindLayer("title")!).StrokeColor);
        Commit(project, "title", "Outline", "");
        Commit(project, "title", "Max width", "");
        Commit(project, "title", "Box", " ");
        title = (TextLayer)project.FindLayer("title")!;
        Assert.Equal((null, null, null), (title.StrokeColor, title.MaxWidth, title.Box));
        Assert.DoesNotContain(Fields(project, "title"), f => f.Label == "Box padding");

        // A font file is offered once the project has one.
        Assert.DoesNotContain(Fields(project, "title"), f => f.Label == "Font file");
        project.Assets.Add(new Asset { Id = "brand", Type = AssetType.Font, File = "assets/brand.ttf" });
        Assert.Equal([InspectorFields.NoFontFile, "brand"], Field(project, "title", "Font file").Options.Select(o => o.Name));
        Commit(project, "title", "Font file", "brand");
        Assert.Equal("brand", ((TextLayer)project.FindLayer("title")!).FontAsset);
        Commit(project, "title", "Font file", InspectorFields.NoFontFile);
        Assert.Null(((TextLayer)project.FindLayer("title")!).FontAsset);
    }

    [Fact]
    public void ACaptionsTextIsItsOwnAndItsStyleTheWholeTracks()
    {
        var project = Footage();
        var cues = project.Layers.Where(l => l.ParentId == "captions_en").Select(l => l.Id).ToList();
        Assert.Equal(2, cues.Count);
        var fields = Fields(project, cues[0]);
        Assert.Equal(("Caption", "captions_en, 2 caption(s)"), (fields[0].Label, fields.Single(f => f.Label == "Track").Value));
        Assert.Contains(fields, f => f.Label == "Style of all 2 caption(s) in the track");
        Assert.DoesNotContain(fields, f => f.Label is "Draw level" or "Parent");

        Commit(project, cues[0], "Text", "Hi");
        Assert.Equal(["Hi", "World"], cues.Select(id => ((TextLayer)project.FindLayer(id)!).Text));

        // One edit, named as update_layer on the track, sizes every caption.
        var size = Field(project, cues[0], "Size").Commit!("40");
        Assert.Equal("update_layer captions_en", size.Label);
        size.Apply(project);
        Assert.All(cues, id => Assert.Equal(40, ((TextLayer)project.FindLayer(id)!).FontSize));
        Assert.Equal(["Hi", "World"], cues.Select(id => ((TextLayer)project.FindLayer(id)!).Text));
    }
}
