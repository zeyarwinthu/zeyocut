// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor.Tests;

// The Audio view's strips, lanes and scales, from the project as the mix takes it.
public class MixerModelTests
{
    // Sounds() with footage whose sound is the voice too: 'street' from 0 to 1 s.
    private static Project WithFootage()
    {
        var project = TestFiles.Sounds();
        project.Assets.Add(new Asset { Id = "street", Type = AssetType.Video, File = "assets/street.mp4", Width = 320, Height = 180, Duration = 1, FrameRate = "30", HasAudio = true });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "street" });
        return project;
    }

    [Fact]
    public void EachRoleHasAStripWithTheProjectsMix()
    {
        var project = WithFootage();
        ProjectEditor.UpdateProject(project, new ProjectOptions { Role = AudioRole.Music, Gain = -8.5, Pan = 0.2 });
        ProjectEditor.UpdateProject(project, new ProjectOptions { Role = AudioRole.Effects, Muted = true });

        var strips = MixerModel.Strips(project, new HashSet<AudioRole> { AudioRole.Voice });
        Assert.Equal(["Voice", "Music", "Sound effects"], strips.Select(s => s.Name));
        Assert.Equal([2, 1, 1], strips.Select(s => s.Clips));
        var music = strips[1];
        Assert.Equal((-8.5, "-8.5", 0.2, "R20", false), (music.Gain, music.GainText, music.Pan, music.PanText, music.Muted));
        Assert.Equal((true, false, true), (strips[0].Soloed, music.Soloed, strips[2].Muted));

        // The music's slot is ducking as the mix does it: on while voice plays with it.
        var ducking = Assert.Single(music.Slots);
        Assert.Equal(("Ducking -12 dB", true), (ducking.Name, ducking.On));
        Assert.Empty(strips[0].Slots);
        ProjectEditor.UpdateProject(project, new ProjectOptions { Role = AudioRole.Voice, Muted = true });
        Assert.False(MixerModel.Strips(project, new HashSet<AudioRole>())[1].Slots[0].On);

        Assert.Equal([("Loudness -14", true), ("True-peak limit", true)], MixerModel.MasterSlots(project).Select(s => (s.Name, s.On)));
        ProjectEditor.UpdateProject(project, new ProjectOptions { LoudnessTarget = 0 });
        Assert.Equal([("Loudness off", false), ("True-peak limit", false)], MixerModel.MasterSlots(project).Select(s => (s.Name, s.On)));
    }

    [Fact]
    public void TheLanesHoldTheSoundsByRoleAndSayWhichArentHeard()
    {
        var project = WithFootage();
        project.FindLayer("boing")!.Visible = false;
        var lanes = MixerModel.Lanes(project);
        Assert.Equal([["street", "talk"], ["song"], []], lanes.Select(l => l.Clips.Select(c => c.LayerId).ToArray()));
        var talk = lanes[0].Clips[1];
        Assert.Equal((1.0, 3.0, "talk", null), (talk.Start, talk.End, talk.Asset, talk.Off));
        Assert.Null(lanes[0].Clips[0].Asset);

        ((VideoLayer)project.FindLayer("street")!).Muted = true;
        ProjectEditor.UpdateProject(project, new ProjectOptions { Role = AudioRole.Music, Muted = true });
        lanes = MixerModel.Lanes(project);
        Assert.Equal(["sound off", null], lanes[0].Clips.Select(c => c.Off));
        Assert.Equal("muted", lanes[1].Clips[0].Off);
    }

    // The music ducks as the mix ducks it: down by the duck level under talk (1-3 s), from half a
    // second before and back 1.5 s after.
    [Fact]
    public void TheDuckedStretchesAreTheMixes()
    {
        var project = TestFiles.Sounds();
        var duck = MixerModel.Duck(project, MixerModel.Lanes(project));
        Assert.Equal([0.5, 1, 3, 4.5], duck.Select(d => d.Time));
        Assert.Equal(Math.Pow(10, -12 / 20.0), duck[1].Value, 9);

        ProjectEditor.UpdateProject(project, new ProjectOptions { Ducking = false });
        Assert.Empty(MixerModel.Duck(project, MixerModel.Lanes(project)));
        ProjectEditor.UpdateProject(project, new ProjectOptions { Ducking = true, Role = AudioRole.Voice, Muted = true });
        Assert.Empty(MixerModel.Duck(project, MixerModel.Lanes(project)));
    }

    // Solo makes a copy for the preview with the other roles muted; the project keeps its own.
    [Fact]
    public void SoloMutesTheOtherRolesInACopy()
    {
        var project = TestFiles.Sounds();
        var json = ProjectJson.Serialize(project);
        Assert.Same(project, MixerModel.Heard(project, new HashSet<AudioRole>()));

        var heard = MixerModel.Heard(project, new HashSet<AudioRole> { AudioRole.Music });
        Assert.NotSame(project, heard);
        Assert.Equal((true, false, true), (heard.Audio.Voice.Muted, heard.Audio.Music.Muted, heard.Audio.Effects.Muted));
        Assert.Equal(json, ProjectJson.Serialize(project));
        Assert.Equal(["song"], FfmpegArgs.AudioClips(heard, "C:\\p").Select(c => c.LayerId));
    }

    [Fact]
    public void TheScalesGoBothWays()
    {
        // The fader: +6 at the top, 0 a sixth down, -60 at the bottom, to the half dB.
        Assert.Equal((1.0, 5 / 6.0, 0.0), (MixerModel.Position(6), MixerModel.Position(0), MixerModel.Position(-60)));
        Assert.Equal(MixerModel.Position(-9), (MixerModel.Position(-6) + MixerModel.Position(-12)) / 2, 9);
        foreach (var gain in new[] { -60, -47.5, -8.5, 0, 2.5, 6 })
        {
            Assert.Equal(gain, MixerModel.GainAt(MixerModel.Position(gain)));
        }

        Assert.Equal((6.0, -60.0), (MixerModel.GainAt(2), MixerModel.GainAt(-1)));

        // A volume line: the same down to -48 dB, then evenly to silence at the bottom.
        Assert.Equal(5 / 6.0, MixerModel.LevelPosition(1), 9);
        Assert.Equal(1, MixerModel.LevelPosition(10));
        Assert.Equal(0, MixerModel.LevelPosition(0));
        Assert.Equal(MixerModel.Position(-48) / 2, MixerModel.LevelPosition(Math.Pow(10, -48 / 20.0) / 2), 9);
        Assert.Equal((1.0, 0.5012, 0.0), (MixerModel.VolumeAt(5 / 6.0), MixerModel.VolumeAt(MixerModel.LevelPosition(0.5)), MixerModel.VolumeAt(0)));
        Assert.Equal(("0 dB", "-6 dB", "silent"), (MixerModel.VolumeText(1), MixerModel.VolumeText(0.5012), MixerModel.VolumeText(0)));

        // The Master meter, in dBFS.
        Assert.Equal((1.0, 0.0, 0.0), (MixerModel.MeterPosition(0), MixerModel.MeterPosition(-48), MixerModel.MeterPosition(double.NegativeInfinity)));
        Assert.Equal(0.5, MixerModel.MeterPosition(-18), 9);

        Assert.Equal(("C", "L50", "R100"), (MixerModel.PanLabel(0), MixerModel.PanLabel(-0.5), MixerModel.PanLabel(1)));
        Assert.Equal(("0.0", "+3.0", "-60.0"), (MixerModel.GainText(0), MixerModel.GainText(3), MixerModel.GainText(-60)));
    }

    // The meter reads the loudest of the last tenth of a second of each side.
    [Fact]
    public void TheMeterReadsTheLastTenthOfASecond()
    {
        var peaks = new float[2 * 100];
        peaks[2 * 44] = 0.5f;
        peaks[2 * 47 + 1] = 0.25f;
        var (left, right) = MixerModel.MeterLevel(peaks, 50, 0.95);
        Assert.Equal((-6.02, -12.04), (Math.Round(left, 2), Math.Round(right, 2)));
        Assert.Equal(double.NegativeInfinity, MixerModel.MeterLevel(peaks, 50, 1.2).Left);
        Assert.Equal(double.NegativeInfinity, MixerModel.MeterLevel(peaks, 50, 9).Left);
    }

    [Fact]
    public void TheLoudnessCardSaysHowTheExportNormalizesTheMix()
    {
        AudioLevelsReport Report(IReadOnlyList<string> notes, params AudioFlag[] flags) => new(4, 3, notes, -14, -2.5, 1, [], flags, []);
        Assert.Equal("Before normalizing, the mix is -19.6 LUFS: 5.6 dB quieter than the target. Every export is normalized to -14 LUFS.",
            MixerModel.LoudnessNote(Report(["loudness: -19.6 LUFS → -14 LUFS (true peak at most -1 dBTP)"]), -14));
        Assert.Equal("Before normalizing, the mix is -12 LUFS: 2 dB louder than the target. Every export is normalized to -14 LUFS. 1 flagged: 2–3s too quiet",
            MixerModel.LoudnessNote(Report(["loudness: -12 LUFS → -14 LUFS (true peak at most -1 dBTP)"], new AudioFlag("too quiet", 2, 3, "2–3s too quiet")), -14));
        Assert.Equal("Normalization is off: exports keep the mix's own loudness.", MixerModel.LoudnessNote(Report(["loudness normalization off"]), null));
        Assert.Equal(("Target: YouTube -14 LUFS", "Target: -18 LUFS", "Target: off"), (MixerModel.TargetText(-14), MixerModel.TargetText(-18), MixerModel.TargetText(null)));
    }
}
