// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp.Demo;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class AudioLevelsTests
{
    private readonly ITestOutputHelper _output;

    public AudioLevelsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static float[] Sine(double seconds, double amplitude, double frequency = 1000)
    {
        var count = (int)Math.Round(seconds * LoudnessMeter.SampleRate);
        var samples = new float[count * 2];
        for (var i = 0; i < count; i++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / LoudnessMeter.SampleRate));
            samples[2 * i] = value;
            samples[2 * i + 1] = value;
        }

        return samples;
    }

    private static IReadOnlyList<AudioHop> Meter(params float[][] parts)
    {
        var meter = new LoudnessMeter();
        foreach (var part in parts)
        {
            meter.Add(part);
        }

        meter.Finish();
        return meter.Hops;
    }

    [Fact]
    public void TheMeterReadsAStereoSineAtItsLevel()
    {
        var sine = Meter(Sine(3, 0.1));
        Assert.Equal(30, sine.Count);
        Assert.InRange(LoudnessMeter.Integrated(sine, 0, sine.Count), -20.1, -19.9);
        Assert.InRange(LoudnessMeter.Decibels(sine.Max(h => h.Peak)), -20.01, -19.99);

        var gated = Meter(Sine(2, 0.1), Sine(2, 0.001));
        Assert.InRange(LoudnessMeter.Integrated(gated, 0, gated.Count), -20.5, -20.1);

        var silence = Meter(new float[LoudnessMeter.SampleRate * 2]);
        Assert.Equal(double.NegativeInfinity, LoudnessMeter.Integrated(silence, 0, silence.Count));

        var shortest = Meter(Sine(0.25, 0.1));
        Assert.Equal(3, shortest.Count);
        Assert.Equal(2400, shortest[^1].Samples);
        Assert.InRange(LoudnessMeter.Integrated(shortest, 0, shortest.Count), -20.3, -19.7);
    }

    [Fact]
    public void TheReportFlagsClippingQuietSectionsAndSilenceInTheMiddle()
    {
        var project = TestProjects.Empty(duration: 16);
        var hops = Meter(Sine(5, 0.3), Sine(5, 0.01), new float[LoudnessMeter.SampleRate * 4], Sine(3, 1.2), new float[LoudnessMeter.SampleRate * 2]);
        var audio = new List<AudioClip>
        {
            new("music", "music.wav", 0, 0, 10, 1, 0, 0),
            new("boom", "boom.wav", 12, 0, 3, 1, 0, 0),
        };

        var report = AudioLevels.Report(project, audio, ["music ducked under voice"], hops, 16, 5);
        var text = report.Describe();
        _output.WriteLine(text);

        Assert.Equal(["too quiet", "clipping", "silence"], report.Flags.Select(f => f.Kind));
        Assert.Equal([0.0, 5, 10, 15], report.Sections.Select(s => s.Start));
        Assert.Equal(16, report.Sections[^1].End);
        Assert.InRange(report.Sections[0].Lufs, -10.6, -10.3);
        Assert.InRange(report.Sections[1].Lufs, -40.1, -39.9);
        Assert.Equal(double.NegativeInfinity, report.Sections[3].Lufs);
        Assert.StartsWith("audio_levels 0–16s, the mix as exported (2 clip(s); music ducked under voice): ", text);
        Assert.Contains("\nsections (5s):\n- 0–5s: -10.5 LUFS, peak -10.5 dBFS\n- 5–10s: -40.0 LUFS, peak -40.0 dBFS\n- 10–15s: ", text);
        Assert.Contains(", 108000 clipped sample(s)\n- 15–16s: silent\nflagged (3):", text);
        Assert.Contains("\n- 5–10s too quiet: -40.0 LUFS is ", text);
        Assert.Contains(" LU below the whole mix; raise the volume of 'music' (update_layer volume)", text);
        Assert.Contains("\n- 10–15s clipping: ", text);
        Assert.Contains(" sample(s) at full scale (peak +1.6 dBFS); lower the volume of 'boom', or turn on loudness normalization (update_project loudnessTarget=-14)", text);
        Assert.Contains("\n- 10–12s silence: 2s with no audio clip playing; fill it with music or a sound effect, or close the gap with remove_range", text);
        Assert.EndsWith("\nnot flagged: silence at the end (15–16s)", text);
    }

    [Fact]
    public void AQuietMixWithoutNormalizationAndASilentMixAreFlagged()
    {
        var project = TestProjects.Empty(duration: 3);
        project.Audio.LoudnessTarget = null;
        var audio = new List<AudioClip> { new("song", "song.wav", 0, 0, 3, 1, 0, 0) };

        var quiet = AudioLevels.Report(project, audio, ["loudness normalization off"], Meter(Sine(3, 0.01)), 3, 5);
        Assert.Equal("the whole mix is -40.0 LUFS with loudness normalization off; most platforms play at about -14 LUFS, so set update_project loudnessTarget=-14", Assert.Single(quiet.Flags).Message);

        var silent = AudioLevels.Report(project, audio, ["loudness normalization off"], Meter(new float[LoudnessMeter.SampleRate * 3]), 3, 5);
        Assert.Equal("0–3s silent: the whole mix is silent although 1 clip(s) play; check their volume and source files", silent.Describe().Split('\n').Single(l => l.StartsWith("- 0–3s silent:"))[2..]);
        Assert.Empty(silent.EdgeSilences);

        var fine = AudioLevels.Report(TestProjects.Empty(duration: 3), audio, ["loudness: -40.0 LUFS → -14 LUFS"], Meter(Sine(3, 0.2)), 3, 5);
        Assert.Contains("\nno clipping, no too-quiet sections and no silences in the middle", fine.Describe());
    }

    [Fact]
    public void AudioLevelsMeasuresTheMixTheExportWouldMake()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("audio-levels");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 160, height: 90, fps: 30, duration: 6));
        Assert.Null(AudioLevels.Measure(session.Snapshot(), directory, ffmpeg, 5, null, TestContext.Current.CancellationToken));

        var probe = new AssetProbe(path => Ffprobe.AudioDuration(ffmpeg.Ffprobe, path));
        foreach (var (id, start) in new[] { ("intro", 0.0), ("outro", 4.0) })
        {
            var file = Path.Combine(directory, $"{id}.wav");
            ToneGenerator.WriteTone(file, 440, 2, 0.05);
            session.Mutate("import", p => AssetImporter.Import(p, directory, file, id, probe));
            session.Mutate("audio", p => ProjectEditor.AddAudioLayer(p, new AudioLayerOptions { Asset = id, Id = id, Start = start }));
        }

        var progress = new List<double>();
        var normalized = AudioLevels.Measure(session.Snapshot(), directory, ffmpeg, 2, progress.Add, TestContext.Current.CancellationToken)!;
        _output.WriteLine(normalized.Describe());
        Assert.InRange(normalized.Lufs, -14.5, -13.5);
        Assert.Equal(3, normalized.Sections.Count);
        Assert.Equal(double.NegativeInfinity, normalized.Sections[1].Lufs);
        Assert.Contains(normalized.Notes, n => n.StartsWith("loudness: ") && n.EndsWith("→ -14 LUFS (true peak at most -1 dBTP)"));
        var gap = Assert.Single(normalized.Flags);
        Assert.Equal("silence", gap.Kind);
        Assert.InRange(gap.Start, 1.9, 2.1);
        Assert.InRange(gap.End, 3.9, 4.1);
        Assert.Contains("with no audio clip playing", gap.Message);
        Assert.Equal(1, progress[^1]);

        session.Mutate("off", p => ProjectEditor.UpdateProject(p, new ProjectOptions { LoudnessTarget = 0 }));
        var raw = AudioLevels.Measure(session.Snapshot(), directory, ffmpeg, 5, null, TestContext.Current.CancellationToken)!;
        _output.WriteLine(raw.Describe());
        var measured = double.Parse(Regex.Match(normalized.Notes.Single(n => n.StartsWith("loudness: ")), @"^loudness: (-[\d.]+) LUFS").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(raw.Lufs, measured - 0.5, measured + 0.5);
        Assert.Contains("loudness normalization off", raw.Notes);
        Assert.Contains(raw.Flags, f => f.Kind == "too quiet" && f.Start == 0 && f.End == 6);
    }
}
