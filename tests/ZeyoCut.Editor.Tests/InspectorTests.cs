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

    // The fields with the playhead at time.
    private static List<InspectorField> Fields(Project project, string id, double time = 0) =>
        InspectorFields.Layer(project, project.FindLayer(id)!, TimelineLayout.Lanes(project).SelectMany(l => l.Clips).FirstOrDefault(c => c.LayerId == id), time);

    private static InspectorField Field(Project project, string id, string label, double time = 0) =>
        Fields(project, id, time).Single(f => f.Label == label && f.Kind != FieldKind.Section);

    // The labels of a section's rows.
    private static List<string> Section(Project project, string id, string title) =>
        Fields(project, id).SkipWhile(f => f.Label != title).Skip(1).TakeWhile(f => f.Kind != FieldKind.Section).Select(f => f.Label).ToList();

    // Types text into a field and makes the edit it gives, as pressing Enter does.
    private static string Commit(Project project, string id, string label, string text, double time = 0) => Field(project, id, label, time).Commit!(text).Apply(project);

    private static string Error(Project project, string id, string label, string text, double time = 0) =>
        Assert.Throws<ZeyoCutException>(() => Commit(project, id, label, text, time)).Message;

    private static T Layer<T>(Project project, string id) where T : Layer => Assert.IsType<T>(project.FindLayer(id));

    [Fact]
    public void AClipsFieldsAreItsTimingLevelAndSound()
    {
        var project = Footage();
        var fields = Fields(project, "clip");
        Assert.Equal(
            ["Clip", "Id", "Start", "End", "Length", "Draw level", "Shown", "Child layers", "Transform", "X", "Y", "Scale X", "Scale Y", "Rotation", "Opacity",
                "Video", "Asset", "Plays", "Speed", "Crop", "Left", "Top", "Right", "Bottom", "Sound", "Sound on", "Role", "Volume", "Fade in", "Fade out",
                "Effects", "Blur", "Shadow", "Border", "Tint", "Adjustments", "Brightness", "Contrast", "Saturation"],
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
        Assert.Equal(50, InspectorFields.Parse("50 %", "Scale X"));
        Assert.Equal(-15, InspectorFields.Parse("-15°", "Rotation"));
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
        Assert.Equal(["Text", "Text", "Style", "Font", "Size", "Bold", "Color", "Outline", "Outline width", "Align", "Max width", "Line height", "Box",
            "Effects", "Blur", "Shadow", "Border", "Tint"], labels[labels.IndexOf("Text")..]);

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
        Assert.DoesNotContain(fields, f => f.Label is "Draw level" or "Parent" or "Effects");

        Commit(project, cues[0], "Text", "Hi");
        Assert.Equal(["Hi", "World"], cues.Select(id => ((TextLayer)project.FindLayer(id)!).Text));

        // One edit, named as update_layer on the track, sizes every caption.
        var size = Field(project, cues[0], "Size").Commit!("40");
        Assert.Equal("update_layer captions_en", size.Label);
        size.Apply(project);
        Assert.All(cues, id => Assert.Equal(40, ((TextLayer)project.FindLayer(id)!).FontSize));
        Assert.Equal(["Hi", "World"], cues.Select(id => ((TextLayer)project.FindLayer(id)!).Text));
    }

    [Fact]
    public void TransformFieldsSetAValueOrAKeyframeAtThePlayhead()
    {
        var project = Footage();
        Assert.Equal(["X", "Y", "Scale X", "Scale Y", "Rotation", "Opacity"], Section(project, "title", "Transform"));
        Assert.Equal(["320", "180", "100", "100", "0", "100"], new[] { "X", "Y", "Scale X", "Scale Y", "Rotation", "Opacity" }.Select(l => Field(project, "title", l).Value));
        Assert.Equal(["px", "px", "%", "%", "°", "%"], new[] { "X", "Y", "Scale X", "Scale Y", "Rotation", "Opacity" }.Select(l => Field(project, "title", l).Unit));
        Assert.Equal("Where the layer's anchor is on the canvas, in pixels from its top-left corner", Field(project, "title", "X").Tip);
        Assert.Equal("Where the layer's anchor is in its parent 'clip', in the parent's own pixels", Field(project, "label", "X").Tip);

        Assert.Equal("update_layer title", Field(project, "title", "X").Commit!("100").Label);
        Commit(project, "title", "X", "100");
        Commit(project, "title", "Scale X", "50 %");
        Commit(project, "title", "Rotation", "15°");
        Commit(project, "title", "Opacity", "40");
        var title = Layer<TextLayer>(project, "title");
        Assert.Equal((100.0, 0.5, 15.0, 0.4), (title.X.Value, title.ScaleX.Value, title.Rotation.Value, title.Opacity.Value));
        Assert.Equal("Opacity can't be more than 100 (got 150)", Error(project, "title", "Opacity", "150"));

        // Animated, a field shows the value at the playhead, and a change there adds a keyframe; where
        // the layer isn't, it can't.
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("title", "y", 0, Json(0)), new KeyframeEntry("title", "y", 2, Json(200))]);
        var y = Field(project, "title", "Y", time: 1);
        Assert.Equal(("100", true, false), (y.Value, y.Keyed, y.KeyHere));
        Assert.StartsWith(InspectorField.KeyedTip, y.Tip);
        Assert.False(Field(project, "title", "X", time: 1).Keyed);
        Assert.Equal("set_keyframes title", y.Commit!("150").Label);
        Commit(project, "title", "Y", "150", time: 1);
        Assert.Equal([(0.0, 0.0), (1, 150), (2, 200)], Layer<TextLayer>(project, "title").Y.Keyframes.Select(k => (k.Time, k.Value)));
        Assert.Equal("'title' animates y with keyframes, and a change adds one at the playhead; move the playhead into 0–3s, where the layer is",
            Error(project, "title", "Y", "10", time: 5));

        // On a motion path, the path places it.
        Layer<TextLayer>(project, "title").MotionPath = new MotionPath { Points = [new PathPoint { X = 0, Y = 0 }, new PathPoint { X = 10, Y = 10 }] };
        Assert.Equal(["Position", "Scale X", "Scale Y", "Rotation", "Opacity"], Section(project, "title", "Transform"));
        Assert.Equal(FieldKind.Info, Field(project, "title", "Position").Kind);
    }

    [Fact]
    public void CropFieldsCutTheSidesOffTheVideo()
    {
        var project = Footage();
        Assert.Equal(["0", "0", "0", "0"], new[] { "Left", "Top", "Right", "Bottom" }.Select(l => Field(project, "clip", l).Value));
        Assert.Equal("crop_layer clip", Field(project, "clip", "Left").Commit!("100").Label);
        Commit(project, "clip", "Left", "100");
        Commit(project, "clip", "Bottom", "60");
        var clip = Layer<VideoLayer>(project, "clip");
        Assert.Equal((100.0, 0.0, 540.0, 300.0), (clip.CropX.Value, clip.CropY.Value, clip.CropWidth!.Value, clip.CropHeight!.Value));
        Assert.Equal(["100", "0", "0", "60"], new[] { "Left", "Top", "Right", "Bottom" }.Select(l => Field(project, "clip", l).Value));
        Assert.Equal("that leaves 0×300 px of the 640×360 source; at least 1 px each way must stay", Error(project, "clip", "Right", "540"));
        Assert.Equal("Left can't be less than 0 (got -5)", Error(project, "clip", "Left", "-5"));

        // A moving crop is changed with keyframes.
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("clip", "cropX", 1, Json(10))]);
        Assert.Equal(["Crop"], Section(project, "clip", "Crop"));
        Assert.Equal(FieldKind.Info, Field(project, "clip", "Crop").Kind);
    }

    [Fact]
    public void ACaptionsPositionMovesTheWholeTrack()
    {
        var project = Footage();
        var cues = project.Layers.Where(l => l.ParentId == "captions_en").Select(l => l.Id).ToList();
        var title = "Position of all 2 caption(s) in the track";
        Assert.Equal(["X", "Y"], Section(project, cues[0], title));
        Assert.DoesNotContain(Fields(project, cues[0]), f => f.Label == "Transform");
        var before = cues.Select(id => Layer<TextLayer>(project, id).Y.Value).ToList();
        var y = InspectorFields.Parse(Field(project, cues[0], "Y").Value, "Y");

        var move = Field(project, cues[0], "Y").Commit!((y - 40).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("update_layer captions_en", move.Label);
        Assert.Equal("moved 2 layer(s) by 0, -40 px", move.Apply(project));
        Assert.Equal(before.Select(b => b - 40), cues.Select(id => Layer<TextLayer>(project, id).Y.Value));
    }

    [Fact]
    public void EffectFieldsSwitchEffectsOnAndSetThem()
    {
        var project = Footage();
        Assert.Equal(["Blur", "Shadow", "Border", "Tint"], Section(project, "title", "Effects"));
        Assert.All(Section(project, "title", "Effects"), label => Assert.Equal(("false", FieldKind.Toggle), (Field(project, "title", label).Value, Field(project, "title", label).Kind)));

        // On, an effect shows its settings with set_effect's defaults; a new tint is white.
        Assert.Equal("set_effect title shadow", Field(project, "title", "Shadow").Commit!("true").Label);
        Commit(project, "title", "Shadow", "true");
        Commit(project, "title", "Tint", "true");
        Assert.Equal(["Blur", "Shadow", "Shadow color", "Shadow X", "Shadow Y", "Shadow blur", "Shadow opacity", "Border", "Tint", "Tint color", "Tint amount"],
            Section(project, "title", "Effects"));
        Assert.Equal(["#000000", "8", "8", "6", "50", "#FFFFFF", "50"],
            new[] { "Shadow color", "Shadow X", "Shadow Y", "Shadow blur", "Shadow opacity", "Tint color", "Tint amount" }.Select(l => Field(project, "title", l).Value));

        Commit(project, "title", "Shadow opacity", "30");
        Commit(project, "title", "Shadow X", "-4");
        Commit(project, "title", "Shadow color", "#FF0000");
        Commit(project, "title", "Tint amount", "100 %");
        Commit(project, "title", "Border", "true");
        Commit(project, "title", "Border width", "12");
        Commit(project, "title", "Blur", "true");
        Commit(project, "title", "Blur Y", "0");
        var title = Layer<TextLayer>(project, "title");
        Assert.Equal((0.3, -4.0, "#FF0000", 1.0, 12.0, 4.0, 0.0),
            (title.Shadow!.Opacity.Value, title.Shadow.X.Value, title.Shadow.Color, title.Tint!.Amount.Value, title.Outline!.Width.Value, title.Blur!.X.Value, title.Blur.Y.Value));
        Assert.Equal("Blur X can't be more than 500 (got 600)", Error(project, "title", "Blur X", "600"));
        Assert.Equal("Shadow opacity can't be more than 100 (got 101)", Error(project, "title", "Shadow opacity", "101"));
        Assert.Equal("Tint color can't be empty", Error(project, "title", "Tint color", ""));

        // Off takes it away, with its settings.
        Assert.StartsWith("removed the shadow effect from 'title'", Commit(project, "title", "Shadow", "false"));
        Assert.Null(Layer<TextLayer>(project, "title").Shadow);
        Assert.DoesNotContain(Fields(project, "title"), f => f.Label == "Shadow X");
    }

    [Fact]
    public void AdjustmentsChangeAPicturesColor()
    {
        var project = Footage();
        Assert.Equal(["Brightness", "Contrast", "Saturation"], Section(project, "clip", "Adjustments"));
        Assert.Equal(["0", "100", "100"], new[] { "Brightness", "Contrast", "Saturation" }.Select(l => Field(project, "clip", l).Value));
        Assert.DoesNotContain(Fields(project, "title"), f => f.Label == "Adjustments");

        Assert.Equal("update_layer clip", Field(project, "clip", "Brightness").Commit!("20").Label);
        Commit(project, "clip", "Brightness", "20");
        Commit(project, "clip", "Contrast", "150");
        Commit(project, "clip", "Saturation", "0");
        var clip = Layer<VideoLayer>(project, "clip");
        Assert.Equal((0.2, 1.5, 0.0), (clip.Brightness.Value, clip.Contrast.Value, clip.Saturation.Value));
        Assert.Equal("Brightness can't be less than -100 (got -150)", Error(project, "clip", "Brightness", "-150"));
    }

    [Fact]
    public void TheDiamondAddsAndTakesAwayAKeyframeAtThePlayhead()
    {
        var project = Footage();
        var rotation = Field(project, "title", "Rotation", time: 1);
        Assert.Equal((true, false, false, "◇"), (rotation.HasToggle, rotation.Keyed, rotation.KeyHere, rotation.KeyGlyph));
        Assert.False(Field(project, "title", "Start").HasToggle);
        Assert.False(Field(project, "title", "Size").HasToggle);
        var cue = project.Layers.First(l => l.ParentId == "captions_en").Id;
        Assert.False(Field(project, cue, "X").HasToggle);

        // The first keyframe starts animating it; the field then shows it's there.
        Assert.Equal("set 1 keyframe(s) at 1s on title.rotation", rotation.ToggleKey!(1).Apply(project));
        rotation = Field(project, "title", "Rotation", time: 1);
        Assert.Equal((true, true, "◆"), (rotation.Keyed, rotation.KeyHere, rotation.KeyGlyph));
        Assert.Equal("Take away the keyframe at the playhead", rotation.KeyTip);
        Commit(project, "title", "Rotation", "30", time: 2);
        Assert.Equal(("30", false), (Field(project, "title", "Rotation", time: 2).Value, Field(project, "title", "Rotation", time: 1.5).KeyHere));
        Assert.Equal([(1.0, 0.0), (2, 30)], Layer<TextLayer>(project, "title").Rotation.Keyframes.Select(k => (k.Time, k.Value)));

        // Taking both away leaves the value of the last.
        Field(project, "title", "Rotation", time: 1).ToggleKey!(1).Apply(project);
        Assert.StartsWith("removed 1 keyframe(s) at 2s", Field(project, "title", "Rotation", time: 2).ToggleKey!(2).Apply(project));
        var title = Layer<TextLayer>(project, "title");
        Assert.Equal((30.0, 0), (title.Rotation.Value, title.Rotation.Keyframes.Count));
        Assert.False(Field(project, "title", "Rotation", time: 2).Keyed);

        // An effect's number too; where the layer isn't, it says so.
        Commit(project, "title", "Shadow", "true");
        Field(project, "title", "Shadow blur", time: 0.5).ToggleKey!(0.5).Apply(project);
        Assert.Single(Layer<TextLayer>(project, "title").Shadow!.Blur.Keyframes);
        Assert.Equal("A keyframe goes at the playhead, and 'title' isn't there; move the playhead into 0–3s, where the layer is",
            Assert.Throws<ZeyoCutException>(() => Field(project, "title", "Opacity", time: 5).ToggleKey!(5)).Message);
    }

    [Fact]
    public void AClipShowsTheFaceBlursOfItsFootage()
    {
        var project = Footage();
        project.Tracks.Add(new Track { Id = "track_1", Asset = "clip", Layer = "clip", FirstFrame = 0, LastFrame = 59, File = "tracks/track_1-00000000.json", Anchors = [new TrackBox { Frame = 0, Width = 40, Height = 40 }] });
        Assert.DoesNotContain(Fields(project, "clip"), f => f.Label.StartsWith("Redaction", StringComparison.Ordinal));
        var id = RedactionEditor.Apply(project, ["track_1"], new RedactionSettings(RedactionStyle.Pixelate, 50, 15, 4, 15, "#000000")).Layer.Id;

        var title = $"Redaction '{id}'";
        Assert.Equal(["Style", "Strength", "Padding", "Feather", "Hold frames"], Section(project, "clip", title));
        Assert.Equal(["pixelate", "50", "15", "4", "15"], new[] { "Style", "Strength", "Padding", "Feather", "Hold frames" }.Select(l => Field(project, "clip", l).Value));
        Assert.Equal(["pixelate", "blur", "solid"], Field(project, "clip", "Style").Options.Select(o => o.Name));

        // Each is update_layer on the redaction; solid fills with a color instead of a strength.
        Assert.Equal("update_layer " + id, Field(project, "clip", "Strength").Commit!("80").Label);
        Commit(project, "clip", "Strength", "80");
        Commit(project, "clip", "Padding", "25");
        Commit(project, "clip", "Hold frames", "30");
        Commit(project, "clip", "Style", "solid");
        Assert.Equal(["Style", "Fill color", "Padding", "Feather", "Hold frames"], Section(project, "clip", title));
        Commit(project, "clip", "Fill color", "#202020");
        var redaction = Assert.IsType<RedactionLayer>(project.FindLayer(id));
        Assert.Equal((80.0, 25.0, 30, RedactionStyle.Solid, "#202020"), (redaction.Strength, redaction.PaddingPercent, redaction.HoldFrames, redaction.Style, redaction.Color));
        Assert.Equal("Hold frames must be a whole number (got 2.5)", Error(project, "clip", "Hold frames", "2.5"));
        Assert.Equal("Feather can't be more than 100 (got 200)", Error(project, "clip", "Feather", "200"));

        // A second face blur of the same footage has its own section, and its Style buttons a group of their own.
        project.Tracks.Add(new Track { Id = "track_2", Asset = "clip", Layer = "clip", FirstFrame = 0, LastFrame = 59, File = "tracks/track_2-00000000.json", Anchors = [new TrackBox { Frame = 0, Width = 40, Height = 40 }] });
        var second = RedactionEditor.Apply(project, ["track_2"], new RedactionSettings(RedactionStyle.Blur, 50, 15, 4, 15, "#000000")).Layer.Id;
        var styles = Fields(project, "clip").Where(f => f.Label == "Style").ToList();
        Assert.Equal((2, "solid", "blur"), (styles.Count, styles[0].Value, styles[1].Value));
        Assert.NotEqual(styles[0].Options[0].Group, styles[1].Options[0].Group);
        Assert.Equal("update_layer " + second, styles[1].Commit!("pixelate").Label);
    }

    private static System.Text.Json.JsonElement Json(double value) => System.Text.Json.JsonSerializer.SerializeToElement(value);
}
