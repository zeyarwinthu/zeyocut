// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class AudioAnimationTests
{
    private const int Rate = BeatDetector.SampleRate;

    private readonly ITestOutputHelper _output;

    public AudioAnimationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Log(BeatGrid grid)
    {
        _output.WriteLine($"bpm {grid.Bpm} confidence {grid.Confidence}");
        _output.WriteLine($"beats {string.Join(", ", grid.Beats)}");
        _output.WriteLine($"onsets {string.Join(", ", grid.Onsets.Select(o => $"{o.Time}({o.Strength})"))}");
        grid.Notes.ForEach(_output.WriteLine);
    }

    private static void AssertOnTheClicks(IReadOnlyList<double> clicks, IReadOnlyList<double> beats)
    {
        Assert.Equal(clicks.Count, beats.Count);
        for (var i = 0; i < clicks.Count; i++)
        {
            Assert.InRange(beats[i], clicks[i] - 0.01, clicks[i] + 0.01);
        }
    }

    [Fact]
    public void TheFftMatchesADirectDft()
    {
        var random = new Random(7);
        const int n = 64;
        var input = Enumerable.Range(0, n).Select(_ => random.NextDouble() * 2 - 1).ToArray();
        var re = (double[])input.Clone();
        var im = new double[n];
        BeatDetector.Fft(re, im);
        for (var k = 0; k < n; k++)
        {
            double sumRe = 0;
            double sumIm = 0;
            for (var t = 0; t < n; t++)
            {
                sumRe += input[t] * Math.Cos(-2 * Math.PI * k * t / n);
                sumIm += input[t] * Math.Sin(-2 * Math.PI * k * t / n);
            }

            Assert.Equal(sumRe, re[k], 9);
            Assert.Equal(sumIm, im[k], 9);
        }

        Assert.Throws<ArgumentException>(() => BeatDetector.Fft(new double[48], new double[48]));
    }

    [Fact]
    public void AClickTrackAt120BpmGives120BpmWithBeatsOnTheClicks()
    {
        var clicks = TestAudio.Beats(120, 0.25, 10);
        var grid = BeatDetector.Detect(TestAudio.Clicks(clicks, 10, Rate), TestContext.Current.CancellationToken);
        Log(grid);

        Assert.NotNull(grid.Bpm);
        Assert.InRange(grid.Bpm.Value, 119.8, 120.2);
        Assert.Equal(20, grid.Onsets.Count);
        AssertOnTheClicks(clicks, grid.Onsets.Select(o => o.Time).ToList());
        AssertOnTheClicks(clicks, grid.Beats);
        Assert.Equal(1, grid.Confidence);
        Assert.Empty(grid.Notes);
    }

    [Theory]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(150)]
    public void OtherTemposAreFound(double bpm)
    {
        var clicks = TestAudio.Beats(bpm, 0.4, 12);
        var grid = BeatDetector.Detect(TestAudio.Clicks(clicks, 12, Rate), TestContext.Current.CancellationToken);
        Log(grid);

        Assert.NotNull(grid.Bpm);
        Assert.InRange(grid.Bpm.Value, bpm - 0.3, bpm + 0.3);
        AssertOnTheClicks(clicks, grid.Beats);
    }

    [Fact]
    public void MissingBeatsAndQuietNoiseKeepTheGrid()
    {
        var clicks = TestAudio.Beats(120, 0.3, 10);
        var played = clicks.Where((_, i) => i is not (5 or 12)).ToList();
        var samples = TestAudio.Clicks(played, 10, Rate);
        var random = new Random(3);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] += (float)((random.NextDouble() * 2 - 1) * 0.002);
        }

        var grid = BeatDetector.Detect(samples, TestContext.Current.CancellationToken);
        Log(grid);

        Assert.NotNull(grid.Bpm);
        Assert.InRange(grid.Bpm.Value, 119.8, 120.2);
        AssertOnTheClicks(clicks, grid.Beats);
        Assert.Equal(0.9, grid.Confidence);
    }

    [Fact]
    public void TheGridFollowsTheKicksNotTheBrighterOffbeatHats()
    {
        var kicks = TestAudio.Beats(100, 0.3, 12);
        var grid = BeatDetector.Detect(TestAudio.DrumLoop(kicks, 0.6, 12, Rate), TestContext.Current.CancellationToken);
        Log(grid);

        Assert.NotNull(grid.Bpm);
        Assert.InRange(grid.Bpm.Value, 99.8, 100.2);
        AssertOnTheClicks(kicks, grid.Beats);
        var hats = kicks.Select(k => k + 0.3).Where(t => t < 11.9).ToList();
        foreach (var hit in kicks.Concat(hats))
        {
            Assert.Contains(grid.Onsets, o => Math.Abs(o.Time - hit) < 0.01);
        }

        Assert.Equal(1 + kicks.Count + hats.Count, grid.Onsets.Count);
        Assert.InRange(grid.Onsets[0].Time, 0, 0.01);

        Assert.All(grid.Onsets.Where(o => hats.Any(h => Math.Abs(o.Time - h) < 0.01)), o => Assert.True(o.Strength < 0.5, $"hat at {o.Time}s has strength {o.Strength}"));
    }

    [Fact]
    public void TooFewOnsetsGiveNoTempo()
    {
        var few = BeatDetector.Detect(TestAudio.Clicks([0.5, 1.0, 1.5], 3, Rate), TestContext.Current.CancellationToken);
        Assert.Null(few.Bpm);
        Assert.Equal(3, few.Onsets.Count);
        Assert.Empty(few.Beats);
        Assert.Equal(["only 3 onset(s), too few to find a tempo"], few.Notes);

        var silence = BeatDetector.Detect(new float[Rate * 2], TestContext.Current.CancellationToken);
        Assert.Null(silence.Bpm);
        Assert.Empty(silence.Onsets);
    }

    [Fact]
    public void AnalyzeDecodesTheFileAndSavesTheBeats()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("beats-analyze");
        var clicks = TestAudio.Beats(120, 0.5, 8);
        TestAudio.WriteWav(Path.Combine(directory, "assets", "clicks.wav"), TestAudio.Clicks(clicks, 8, 44100), 44100);
        var asset = new Asset { Id = "clicks", Type = AssetType.Audio, File = "assets/clicks.wav", Duration = 8 };

        var analysis = BeatDetector.Analyze(ffmpeg.Ffmpeg, directory, asset, null, TestContext.Current.CancellationToken);
        var saved = AnalysisFiles.BeatsPath(directory, asset);
        var text = BeatDetector.Describe(analysis, saved);
        _output.WriteLine(text);

        Assert.InRange(analysis.Bpm!.Value, 119.8, 120.2);
        AssertOnTheClicks(clicks, analysis.Beats);
        Assert.Equal(8, analysis.Duration, 2);
        Assert.Equal(Path.Combine(directory, "assets", "clicks.beats.json"), saved);
        var loaded = AnalysisFiles.Load<BeatAnalysis>(saved)!;
        Assert.Equal("clicks", loaded.Asset);
        Assert.Equal(analysis.Beats, loaded.Beats);
        Assert.Equal(analysis.Onsets.Count, loaded.Onsets.Count);
        Assert.StartsWith($"beats of 'clicks' (8s): 120.0 BPM, {clicks.Count} beats every 0.5s from 0.5", text);
        Assert.Contains($"saved {saved}", text);
    }

    [Fact]
    public void AutoLipFlapOnToneSilenceToneOpensClosesAndOpens()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("lipflap-tone");
        TestAudio.WriteWav(Path.Combine(directory, "assets", "voice.wav"), TestAudio.Tone(3, 48000, t => t < 1 || t >= 2), 48000);
        var project = TestProjects.WithImageAssets("m_closed", "m_open");
        project.Fps = 24;
        project.Duration = 3;
        project.Assets.Add(new Asset { Id = "voice", Type = AssetType.Audio, File = "assets/voice.wav", Duration = 3 });
        var mouth = ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "mouth",
            Drawings = new Dictionary<string, string> { ["closed"] = "m_closed", ["open"] = "m_open" },
        });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "voice", Id = "voice", Role = "voice" });

        var plan = LipFlap.Plan(project, "mouth", "voice", "closed", ["open"], LipFlap.DefaultSensitivity);
        var levels = AudioAnalyzer.FrameLevels(ffmpeg.Ffmpeg, directory, plan, TestContext.Current.CancellationToken);
        var result = LipFlap.Apply(project, "mouth", "closed", ["open"], LipFlap.DefaultSensitivity, plan, levels);
        _output.WriteLine(result.Describe());

        string At(int frame) => Interpolator.Evaluate(mouth.Drawing, FrameMath.TimeOf(project, frame));
        var shown = Enumerable.Range(0, 72).Select(At).ToList();
        Assert.All(shown[..24], d => Assert.Equal("open", d));
        Assert.All(shown[24..48], d => Assert.Equal("closed", d));
        Assert.All(shown[48..], d => Assert.Equal("open", d));
        Assert.Equal(3, result.Keyframes);
        Assert.Equal([(0.0, "open"), (1.0, "closed"), (2.0, "open")], mouth.Drawing.Keyframes.Select(k => (Math.Round(k.Time, 9), k.Value)));
    }
}
