// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The source viewer: a file on its own, the marks that become a clip's trim in and out, and the lane
// the clip goes on.
public class SourceViewerTests
{
    // An 8 s cut: footage 'intro' (0-3 s) and 'main' (3-7 s) of the street on draw level 0, a title
    // over them on level 1, a voice and music; and footage at 25 fps nothing uses.
    private static Project Cut()
    {
        var project = TestFiles.Project(duration: 8);
        project.Assets.Add(new Asset { Id = "street", Type = AssetType.Video, File = "assets/street.mp4", Width = 1280, Height = 720, Duration = 6, FrameRate = "30", FrameCount = 180, HasAudio = true });
        project.Assets.Add(new Asset { Id = "b_roll", Type = AssetType.Video, File = "assets/b_roll.mp4", Width = 640, Height = 360, Duration = 4, FrameRate = "25", FrameCount = 100, HasAudio = false });
        project.Assets.Add(new Asset { Id = "talk", Type = AssetType.Audio, File = "assets/talk.wav", Duration = 2 });
        project.Assets.Add(new Asset { Id = "song", Type = AssetType.Audio, File = "assets/song.wav", Duration = 4 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "intro", Start = 0, TrimOut = 3, ZIndex = 0 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "main", Start = 3, TrimIn = 2, ZIndex = 0 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 1, End = 3, ZIndex = 1 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "talk", Id = "narration", Start = 1, Role = "voice" });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "song", Id = "music", Start = 0 });
        return project;
    }

    [Fact]
    public void TheSourceIsTheFileAlone()
    {
        var project = Cut();
        var source = SourceViewer.Of(project, project.FindAsset("street")!);
        Assert.Equal((1280, 720, 30, 6.0, 180), (source.Width, source.Height, source.Fps, source.Duration, FrameMath.FrameCount(source)));
        var clip = Assert.IsType<VideoLayer>(Assert.Single(source.Layers));
        Assert.Equal(("source", "street", 0.0, false), (clip.Id, clip.Asset, clip.Start, clip.Muted));
        Assert.Equal("street", Assert.Single(source.Assets).Id);
        Assert.Null(source.Audio.LoudnessTarget);
        Assert.False(source.Audio.Ducking);
        Assert.Empty(ProjectValidator.Validate(source));

        // Footage at its own frame rate; a sound at the project's, on a canvas with nothing on it.
        var broll = SourceViewer.Of(project, project.FindAsset("b_roll")!);
        Assert.Equal((640, 360, 25, 100), (broll.Width, broll.Height, broll.Fps, FrameMath.FrameCount(broll)));
        var song = SourceViewer.Of(project, project.FindAsset("song")!);
        Assert.Equal((30, 4.0, 120), (song.Fps, song.Duration, FrameMath.FrameCount(song)));
        var sound = Assert.IsType<AudioLayer>(Assert.Single(song.Layers));
        Assert.Equal(("source", "song"), (sound.Id, sound.Asset));
        Assert.Empty(ProjectValidator.Validate(song));
    }

    [Fact]
    public void InAndOutBecomeTrimInAndOut()
    {
        var project = Cut();
        var street = project.FindAsset("street")!;
        var source = SourceViewer.Of(project, street);

        // Without marks the clip is the whole file; the out mark is the clip's last frame.
        Assert.Equal(new SourceRange(0, 6, null, null), SourceViewer.Range(source, null, null));
        var range = SourceViewer.Range(source, 30, 119);
        Assert.Equal((1.0, 4.0, 3.0), (range.In, range.Out, range.Length));
        Assert.Equal(6, SourceViewer.Range(source, null, 179).Out);
        Assert.Equal(0.4, SourceViewer.Range(SourceViewer.Of(project, project.FindAsset("b_roll")!), 10, null).In, 9);

        // A mark past the other clears the other.
        Assert.Equal<(int?, int?)>((30, 119), SourceViewer.MarkIn(30, 119));
        Assert.Equal<(int?, int?)>((150, null), SourceViewer.MarkIn(150, 119));
        Assert.Equal<(int?, int?)>((30, 119), SourceViewer.MarkOut(119, 30));
        Assert.Equal<(int?, int?)>((null, 20), SourceViewer.MarkOut(20, 30));

        // The clip is the marked part at the playhead, inserted or overwritten as the add-layer
        // tools' edit does it.
        var video = SourceViewer.Default(project, street, null);
        var insert = SourceViewer.Clip(street, 3, video, range, sound: true, ClipEdit.Insert);
        Assert.Equal(new AddClipOperation(AssetType.Video, "street", 3, 0, null, 1, 4, false, ClipEdit.Insert), insert);
        var inserted = Cut();
        insert.Apply(inserted);
        var clip = inserted.Layers.OfType<VideoLayer>().Single(l => l.Id is not "intro" and not "main");
        Assert.Equal((3.0, 1.0, 4.0, 0, false), (clip.Start, clip.TrimIn, clip.TrimOut, clip.ZIndex, clip.Muted));
        Assert.Equal(6, inserted.FindLayer("main")!.Start);
        Assert.Equal(ClipEdit.Overwrite, SourceViewer.Clip(street, 3, video, range, sound: true, ClipEdit.Overwrite).Edit);

        // Footage without its sound is a muted clip; a sound is never muted, and goes in its role.
        Assert.True(SourceViewer.Clip(street, 3, video, range, sound: false, ClipEdit.Insert).Muted);
        var song = project.FindAsset("song")!;
        var voice = SourceViewer.Targets(project, song).Single(t => t.Title == "Voice");
        var said = SourceViewer.Clip(song, 2, voice, SourceViewer.Range(SourceViewer.Of(project, song), null, 59), sound: false, ClipEdit.Overwrite);
        Assert.Equal(new AddClipOperation(AssetType.Audio, "song", 2, null, "voice", 0, 2, false, ClipEdit.Overwrite), said);

        // On a new track on top, the clip gets a draw level of its own.
        var top = SourceViewer.Targets(project, street)[^1];
        Assert.Null(SourceViewer.Clip(street, 3, top, range, sound: true, ClipEdit.Overwrite).ZIndex);
    }

    [Fact]
    public void TheClipGoesOnTheSelectedLaneOrVideoOne()
    {
        var project = Cut();
        var street = project.FindAsset("street")!;
        var song = project.FindAsset("song")!;
        Assert.Equal([("z:1", "Text", 1), ("z:0", "Video", 0), (SourceViewer.OnTop, "A new track on top", null)],
            SourceViewer.Targets(project, street).Select(t => (t.Key, t.Title, t.ZIndex)));
        Assert.Equal([("audio:voice", "Voice"), ("audio:music", "Music"), ("audio:effects", "Sound effects")],
            SourceViewer.Targets(project, song).Select(t => (t.Key, t.Title)));

        // The selected clip's lane, when the file can go there.
        Assert.Equal("z:0", SourceViewer.Default(project, street, null).Key);
        Assert.Equal("z:1", SourceViewer.Default(project, street, "title").Key);
        Assert.Equal("z:0", SourceViewer.Default(project, street, "narration").Key);
        Assert.Equal("audio:music", SourceViewer.Default(project, song, null).Key);
        Assert.Equal("audio:voice", SourceViewer.Default(project, song, "narration").Key);
        Assert.Equal("audio:music", SourceViewer.Default(project, song, "main").Key);

        // Without footage, the lowest draw level; with nothing on the canvas, a track on top.
        project.Layers.RemoveAll(l => l is VideoLayer);
        Assert.Equal("z:1", SourceViewer.Default(project, street, null).Key);
        project.Layers.RemoveAll(l => l is TextLayer);
        Assert.Equal(SourceViewer.OnTop, SourceViewer.Default(project, street, null).Key);
    }

    [Fact]
    public void TheMarksAreShownInSecondsAndHundredths()
    {
        Assert.Equal(["00:00.00", "00:01.20", "01:15.46", "00:00.00"], new[] { 0, 1.2, 75.456, -1 }.Select(SourceViewer.Stamp));
        var source = SourceViewer.Of(Cut(), Cut().FindAsset("street")!);
        Assert.Equal("In 00:01.00 · Out 00:04.00 · 3.00 s", SourceViewer.Marks(SourceViewer.Range(source, 30, 119)));
        Assert.Equal("In 00:01.00 · 5.00 s", SourceViewer.Marks(SourceViewer.Range(source, 30, null)));
        Assert.Equal("6.00 s", SourceViewer.Marks(SourceViewer.Range(source, null, null)));
        Assert.Equal("Insert pushes everything from the playhead on 3.00 s to the right. Overwrite replaces what's there.", SourceViewer.Note(3));
    }
}
