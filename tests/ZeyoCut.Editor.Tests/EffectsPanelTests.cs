// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The Effects view: the cuts on the lanes, the tiles, and the operations they apply.
public class EffectsPanelTests
{
    // Footage 'a' (0-2 s), 'b' (2-4 s) and, after a gap, 'c' (4.5-6 s) and 'd' a frame later on level
    // 0; a title on level 1; two voice clips back to back; and a caption track.
    private static Project Edit()
    {
        var project = TestFiles.Project(duration: 8);
        project.Assets.Add(new Asset { Id = "street", Type = AssetType.Video, File = "assets/street.mp4", Width = 320, Height = 180, Duration = 10, FrameRate = "30", FrameCount = 300 });
        project.Assets.Add(new Asset { Id = "talk", Type = AssetType.Audio, File = "assets/talk.wav", Duration = 4 });
        foreach (var (id, start, length) in new[] { ("a", 0.0, 2.0), ("b", 2.0, 2.0), ("c", 4.5, 1.5), ("d", 6 + 1 / 30.0, 1.0) })
        {
            ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = id, Start = start, TrimIn = start, TrimOut = start + length, ZIndex = 0 });
        }

        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", Start = 1, End = 3, ZIndex = 1 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "talk", Id = "line_1", Start = 0, TrimOut = 2, Role = "voice" });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "talk", Id = "line_2", Start = 2, TrimIn = 2, Role = "voice" });
        CaptionTracks.AddEntries(project, "en", [new CueEntry(0, 2, "One"), new CueEntry(2, 4, "Two")], new CaptionOptions(), [], null);
        return project;
    }

    [Fact]
    public void ACutIsWhereAClipMeetsTheNextOnItsLane()
    {
        var project = Edit();
        var lanes = TimelineLayout.Lanes(project);
        var video = lanes.Single(l => l.Clips.Any(c => c.LayerId == "a"));
        Assert.Equal([("a", "b", 2.0), ("c", "d", 6 + 1 / 30.0)], EffectsPanel.Cuts(video, project.Fps).Select(c => (c.LayerA, c.LayerB, c.Time)));
        Assert.Equal("a → b", EffectsPanel.At(video, "a", end: true, project.Fps)!.Label);
        Assert.Equal("a → b", EffectsPanel.At(video, "b", end: false, project.Fps)!.Label);
        Assert.Null(EffectsPanel.At(video, "b", end: true, project.Fps));
        Assert.Null(EffectsPanel.At(video, "c", end: false, project.Fps));

        var voice = lanes.Single(l => l.Role == AudioRole.Voice);
        Assert.Equal("line_1 → line_2", Assert.Single(EffectsPanel.Cuts(voice, project.Fps)).Label);
        Assert.Empty(EffectsPanel.Cuts(lanes.Single(l => l.Kind == LaneKind.Captions), project.Fps));
        Assert.Empty(EffectsPanel.Cuts(lanes.Single(l => l.Clips.Any(c => c.LayerId == "title")), project.Fps));

        // A transition overlaps the clips, so the cut isn't one any more; undone, it's back.
        var cut = EffectsPanel.At(video, "a", end: true, project.Fps)!;
        Assert.Equal(cut, EffectsPanel.Current(lanes, cut, project.Fps));
        EffectsPanel.Transition(cut, TransitionType.Crossfade, 0.5, null, null).Apply(project);
        Assert.Null(EffectsPanel.Current(TimelineLayout.Lanes(project), cut, project.Fps));
    }

    [Fact]
    public void TheTilesAndSettingsAreAddTransitionsAndTheInspectorsEffects()
    {
        Assert.Equal(["Crossfade", "Dip to black", "Wipe", "Slide", "Zoom", "Whip pan"], EffectsPanel.Tiles(EffectCategory.Transitions, "").Select(t => t.Name));
        Assert.Equal("0.5 s", EffectsPanel.Tiles(EffectCategory.Transitions, "")[0].Meta);
        Assert.Equal(["Whip pan"], EffectsPanel.Tiles(EffectCategory.Transitions, "PAN").Select(t => t.Name));
        Assert.Equal(["Blur", "Shadow", "Border", "Tint"], EffectsPanel.Tiles(EffectCategory.VideoEffects, "").Select(t => t.Name));
        Assert.Equal(["Fade", "Pop", "Slide up"], EffectsPanel.Tiles(EffectCategory.TextAnimations, "").Select(t => t.Name));
        Assert.Equal((6, 4, 3), (EffectsPanel.Count(EffectCategory.Transitions), EffectsPanel.Count(EffectCategory.VideoEffects), EffectsPanel.Count(EffectCategory.TextAnimations)));

        Assert.Equal(["From right", "From left", "From bottom", "From top"], EffectsPanel.Choice(TransitionType.Slide).Directions.Select(EffectsPanel.DirectionName));
        Assert.Equal(["In", "Out"], EffectsPanel.Choice(TransitionType.Zoom).Directions.Select(EffectsPanel.DirectionName));
        Assert.Empty(EffectsPanel.Choice(TransitionType.FadeToBlack).Directions);
        Assert.Equal((TransitionDirection.Right, TransitionDirection.Left), (EffectsPanel.Choice(TransitionType.Wipe).Default!.Value, EffectsPanel.Choice(TransitionType.WhipPan).Default!.Value));

        var cut = new TimelineCut("z:0", "a", "b", 2);
        Assert.Equal(new AddTransitionOperation("a", "b", TransitionType.Crossfade, 0.4), EffectsPanel.Transition(cut, TransitionType.Crossfade, 0.4, TransitionDirection.Up, null));
        Assert.Equal(new AddTransitionOperation("a", "b", TransitionType.Slide, 0.5, TransitionDirection.Up), EffectsPanel.Transition(cut, TransitionType.Slide, 0.5, TransitionDirection.Up, null));
        var every = Assert.IsType<BatchOperation>(EffectsPanel.Every([cut, new TimelineCut("z:0", "c", "d", 6)], TransitionType.Zoom, 0.3, TransitionDirection.Out, null));
        Assert.Equal("add_transition (2 cuts)", every.Label);
        Assert.Equal(["a", "b", "c", "d"], every.Layers);
        Assert.IsType<AddTransitionOperation>(EffectsPanel.Every([cut], TransitionType.Zoom, 0.3, null, null));

        Assert.Equal((0.25, null, null), (EffectsPanel.Duration(" 0.25 "), EffectsPanel.Duration("0"), EffectsPanel.Duration("soon")));
        Assert.Equal("#FFFFFF", EffectsPanel.Effect("title", EffectKind.Tint, on: true).Values.Color);
        Assert.Null(EffectsPanel.Effect("title", EffectKind.Blur, on: true).Values.Color);
        Assert.True(EffectsPanel.Effect("title", EffectKind.Shadow, on: false).Remove);
    }
}
