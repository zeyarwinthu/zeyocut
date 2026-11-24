// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp.Demo;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

// The mixer settings, volume keyframes and the duck level, as audio_levels measures the mix.
public class MixExportTests
{
    private readonly ITestOutputHelper _output;

    public MixExportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // A project with loudness normalization off, so levels are what the settings make them.
    private static ProjectSession Session(string name, double duration)
    {
        var session = ProjectSession.Create(TestPaths.NewDirectory(name), TestProjects.Empty(width: 160, height: 90, fps: 30, duration: duration));
        session.Mutate("raw", p => ProjectEditor.UpdateProject(p, new ProjectOptions { LoudnessTarget = 0 }));
        return session;
    }

    private static void AddTone(ProjectSession session, FfmpegPaths ffmpeg, string id, double seconds, double amplitude, double start, string role, double frequency = 1000)
    {
        var file = Path.Combine(session.Directory, $"src_{id}.wav");
        ToneGenerator.WriteTone(file, frequency, seconds, amplitude);
        var probe = new AssetProbe(path => Ffprobe.AudioDuration(ffmpeg.Ffprobe, path));
        session.Mutate("import", p => AssetImporter.Import(p, session.Directory, file, id, probe));
        session.Mutate("audio", p => ProjectEditor.AddAudioLayer(p, new AudioLayerOptions { Asset = id, Id = id, Start = start, Role = role }));
    }

    private AudioLevelsReport Measure(ProjectSession session, FfmpegPaths ffmpeg, double sectionSeconds = 1)
    {
        var report = AudioLevels.Measure(session.Snapshot(), session.Directory, ffmpeg, sectionSeconds, null, TestContext.Current.CancellationToken)!;
        _output.WriteLine(report.Describe());
        return report;
    }

    // Each channel's mean level in dB, from an export.
    private static (double Left, double Right) ChannelLevels(FfmpegPaths ffmpeg, string file)
    {
        double Level(int channel)
        {
            var result = ProcessRunner.Run(ffmpeg.Ffmpeg, ["-hide_banner", "-nostats", "-i", file, "-vn", "-af", $"pan=mono|c0=c{channel},volumedetect", "-f", "null", "-"], TimeSpan.FromSeconds(60));
            Assert.True(result.ExitCode == 0, result.StandardError);
            return double.Parse(Regex.Match(result.StandardError, @"mean_volume:\s*(-?[\d.]+|-inf) dB").Groups[1].Value.Replace("-inf", "-200"), CultureInfo.InvariantCulture);
        }

        return (Level(0), Level(1));
    }

    [Fact]
    public void ARolesGainLowersItAndAMutedRoleIsNotHeard()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = Session("mix-gain", 3);
        AddTone(session, ffmpeg, "song", 3, 0.2, 0, "music");

        var flat = Measure(session, ffmpeg).Lufs;
        session.Mutate("gain", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Role = AudioRole.Music, Gain = -6 }));
        var lower = Measure(session, ffmpeg);
        Assert.InRange(lower.Lufs - flat, -6.2, -5.8);
        Assert.Contains("mix: music -6 dB", lower.Notes);

        // The voice role's strip leaves the music alone, and isn't noted while no voice plays.
        session.Mutate("voice", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Role = AudioRole.Voice, Gain = -20 }));
        var voiceDown = Measure(session, ffmpeg);
        Assert.InRange(voiceDown.Lufs - lower.Lufs, -0.1, 0.1);
        Assert.Contains("mix: music -6 dB", voiceDown.Notes);

        // A muted role with sound is noted while another plays.
        AddTone(session, ffmpeg, "boing", 1, 0.2, 1, "effects");
        session.Mutate("mute fx", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Role = AudioRole.Effects, Muted = true }));
        var withoutEffects = Measure(session, ffmpeg);
        Assert.Contains("mix: music -6 dB; effects muted", withoutEffects.Notes);
        Assert.InRange(withoutEffects.Lufs - lower.Lufs, -0.1, 0.1);

        session.Mutate("mute", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Role = AudioRole.Music, Muted = true }));
        Assert.Empty(FfmpegArgs.AudioClips(session.Snapshot(), session.Directory));
        Assert.Null(AudioLevels.Measure(session.Snapshot(), session.Directory, ffmpeg, 1, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void APanSendsARoleToOneSide()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = Session("mix-pan", 2);
        AddTone(session, ffmpeg, "song", 2, 0.2, 0, "music");
        session.Mutate("left", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Role = AudioRole.Music, Pan = -1 }));

        var clip = Assert.Single(FfmpegArgs.AudioClips(session.Snapshot(), session.Directory));
        Assert.Equal(-1, clip.Pan);
        var filter = FfmpegArgs.Build(new EncodeSettings(160, 90, 30, 60, 18), [clip], "out.mp4", new AudioMix(false, null, -1, null));
        Assert.Contains("pan=stereo|c0=1*c0|c1=0*c1", filter[filter.IndexOf("-filter_complex") + 1]);

        var output = Path.Combine(session.Directory, "exports", "left.mp4");
        Exporter.Export(session.Snapshot(), session.Directory, output, 23, ffmpeg, cancellationToken: TestContext.Current.CancellationToken);
        var (left, right) = ChannelLevels(ffmpeg, output);
        _output.WriteLine($"left {Num(left)} dB, right {Num(right)} dB");
        Assert.InRange(left, -20, -10);
        Assert.True(right < left - 40, $"right {Num(right)} dB should be silent next to left {Num(left)} dB");

        session.Mutate("half", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Role = AudioRole.Music, Pan = 0.5 }));
        var half = FfmpegArgs.Build(new EncodeSettings(160, 90, 30, 60, 18), FfmpegArgs.AudioClips(session.Snapshot(), session.Directory), "out.mp4", new AudioMix(false, null, -1, null));
        Assert.Contains("pan=stereo|c0=0.5*c0|c1=1*c1", half[half.IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void VolumeKeyframesRideTheLevel()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = Session("mix-ramp", 4);
        AddTone(session, ffmpeg, "song", 4, 0.3, 0, "music");
        session.Mutate("ramp", p => KeyframeEditor.SetKeyframes(p,
        [
            new KeyframeEntry("song", "volume", 0, JsonSerializer.SerializeToElement(1)),
            new KeyframeEntry("song", "volume", 2, JsonSerializer.SerializeToElement(0.1), "hold"),
            new KeyframeEntry("song", "volume", 3, JsonSerializer.SerializeToElement(1)),
        ]));

        var clip = Assert.Single(FfmpegArgs.AudioClips(session.Snapshot(), session.Directory));
        Assert.Equal([0.0, 2.0, 3.0], clip.VolumeKeys!.Select(k => k.Time));
        var points = FfmpegArgs.VolumePoints(clip);
        Assert.Equal((0.0, 1.0), points[0]);
        Assert.Contains((2.998, 0.1), points.Select(p => (Math.Round(p.Time, 3), Math.Round(p.Value, 3))));
        Assert.Equal((4.0, 1.0), points[^1]);

        var sections = Measure(session, ffmpeg).Sections;
        var levels = sections.Select(s => s.Lufs).ToList();
        _output.WriteLine(string.Join(", ", levels.Select(Num)));

        // Down from full to 0.1 over two seconds, held there, then back at full.
        Assert.True(levels[0] > levels[1], $"{Num(levels[0])} then {Num(levels[1])}");
        Assert.InRange(levels[2] - levels[3], -20.5, -19.5);
        Assert.InRange(levels[0] - levels[3], -3, -1.5);
    }

    [Fact]
    public void MusicGoesDownByTheDuckLevelWhereVoiceClipsPlay()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = Session("mix-duck", 7);
        AddTone(session, ffmpeg, "song", 7, 0.2, 0, "music");
        AddTone(session, ffmpeg, "voice", 2, 0, 3, "voice");
        session.Mutate("fades", p => ProjectEditor.UpdateProject(p, new ProjectOptions { DuckFadeIn = 0.5, DuckFadeOut = 0.5 }));

        var points = FfmpegArgs.DuckPoints(FfmpegArgs.AudioClips(session.Snapshot(), session.Directory), AudioMix.Of(session.Snapshot().Audio));
        var level = Math.Pow(10, -12 / 20.0);
        Assert.Equal([2.5, 3.0, 5.0, 5.5], points.Select(p => p.Time));
        Assert.Equal([1.0, level, level, 1.0], points.Select(p => p.Value), (a, b) => Math.Abs(a - b) < 1e-9);

        double Drop(AudioLevelsReport report) => report.Sections[3].Lufs - report.Sections[0].Lufs;
        var ducked = Measure(session, ffmpeg);
        Assert.Contains("music ducked -12 dB under voice", ducked.Notes);
        Assert.InRange(Drop(ducked), -12.3, -11.7);
        Assert.InRange(ducked.Sections[6].Lufs - ducked.Sections[0].Lufs, -0.3, 0.3);

        session.Mutate("deeper", p => ProjectEditor.UpdateProject(p, new ProjectOptions { DuckLevel = -20 }));
        Assert.InRange(Drop(Measure(session, ffmpeg)), -20.3, -19.7);

        session.Mutate("off", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Ducking = false }));
        Assert.InRange(Drop(Measure(session, ffmpeg)), -0.3, 0.3);

        // Two voice clips close together: the music comes back up only as far as the fades reach.
        var close = new List<AudioClip>
        {
            new("a", "a.wav", 1, 0, 1, 1, 0, 0, Role: AudioRole.Voice),
            new("b", "b.wav", 2.5, 0, 1, 1, 0, 0, Role: AudioRole.Voice),
            new("m", "m.wav", 0, 0, 5, 1, 0, 0, Role: AudioRole.Music),
        };
        var between = FfmpegArgs.DuckPoints(close, new AudioMix(true, null, -1, null, -12, 1, 1));
        var middle = between.Single(p => Math.Abs(p.Time - 2.25) < 1e-9);
        Assert.Equal(1 - 0.75 * (1 - level), middle.Value, 6);
    }

    [Fact]
    public void AnEnvelopeIsAShallowFfmpegExpression()
    {
        Assert.Equal("1", FfmpegArgs.Expression([]));
        Assert.Equal("0.5", FfmpegArgs.Expression([(0, 0.5)]));
        Assert.Equal("if(lt(t,2),1-0.25*(t-0),0.5)", FfmpegArgs.Expression([(0, 1), (2, 0.5)]));
        Assert.Equal("if(lt(t,1),0.5,if(lt(t,3),0.5+0.25*(t-1),1))", FfmpegArgs.Expression([(1, 0.5), (3, 1)]));
        Assert.Equal("if(lt(t,1),1,if(lt(t,2),1-0.5*(t-1),0.5))", FfmpegArgs.Expression([(1, 1), (2, 0.5)]));

        // A clip's envelope runs in its own seconds, after its speed change: here it starts at 1s and plays twice as fast.
        var fast = new AudioClip("clip", "clip.mp4", 1, 0, 2, 1, 0, 0, Speed: 2, Role: AudioRole.Voice,
            VolumeKeys: [new Keyframe { Time = 2, Value = 1 }, new Keyframe { Time = 3, Value = 0 }], Gain: 0.5);
        Assert.Equal([(0.0, 1.0), (1.0, 1.0), (2.0, 0.0)], FfmpegArgs.VolumePoints(fast));
        Assert.Contains("atempo=2,asetnsamples=n=480:p=0,volume=volume='0.5*(if(lt(t,1),1,if(lt(t,2),1-1*(t-1),0)))':eval=frame",
            string.Join(" ", FfmpegArgs.Build(new EncodeSettings(160, 90, 30, 90, 18), [fast], "out.mp4", null)));

        var many = Enumerable.Range(0, 200).Select(i => (i * 0.1, i % 2 == 0 ? 1.0 : 0.5)).ToList();
        var expression = FfmpegArgs.Expression(many);
        var depth = 0;
        var deepest = 0;
        foreach (var c in expression)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            deepest = Math.Max(deepest, depth);
        }

        Assert.InRange(deepest, 1, 30);
    }
}
