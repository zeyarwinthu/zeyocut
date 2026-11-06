// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class AnalysisTests
{
    private static (string Directory, Asset Asset) ProjectWith(string name, Func<string, string> create, string id)
    {
        var directory = TestPaths.NewDirectory(name);
        ProjectStore.EnsureFolders(directory);
        var file = create(Path.Combine(directory, ProjectStore.AssetsFolder, id + ".mp4"));
        var asset = TestVideos.AssetFor(file, id);
        asset.File = $"{ProjectStore.AssetsFolder}/{Path.GetFileName(file)}";
        return (directory, asset);
    }

    [Fact]
    public void NoiseFloorSetsTheThresholdAndRemoveSilencesLeavesTheExpectedLength()
    {
        var ffmpeg = Ffmpeg.Require();
        var (directory, asset) = ProjectWith("analysis-tone-gap", TestVideos.ToneGapTone, "talk");
        var file = ProjectStore.AssetPath(directory, asset);

        var levels = AudioAnalyzer.Levels(ffmpeg.Ffmpeg, file, 0, 4, TestContext.Current.CancellationToken);
        Assert.InRange(levels.NoiseFloorDb, -56, -48);
        Assert.Equal(levels.NoiseFloorDb + AudioAnalyzer.NoiseMarginDb, levels.ThresholdDb, 1);
        Assert.InRange(levels.LoudDb, -22, -14);

        var silences = AudioAnalyzer.Silences(ffmpeg.Ffmpeg, file, 0, 4, levels.ThresholdDb, 0.7, TestContext.Current.CancellationToken);
        var silence = Assert.Single(silences);
        Assert.InRange(silence.Start, 0.97, 1.03);
        Assert.InRange(silence.End, 2.97, 3.03);

        var later = AudioAnalyzer.Silences(ffmpeg.Ffmpeg, file, 0.5, 3, levels.ThresholdDb, 0.7, TestContext.Current.CancellationToken);
        Assert.InRange(Assert.Single(later).Start, 0.97, 1.03);

        var project = TestProjects.Empty(width: 160, height: 120, duration: 4);
        project.Assets.Add(asset);
        var clip = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "talk" });
        var plan = SilenceCutter.Plan(project, clip, silences, padding: 0.1);
        SilenceCutter.Apply(project, plan);
        Assert.InRange(project.Duration, 2.17, 2.23);
        Assert.InRange(((VideoLayer)project.FindLayer("talk_2")!).TrimIn, 2.87, 2.93);
    }

    [Fact]
    public void AnalyzeFindsScenesAndSilencesAndSavesThemBesideTheAsset()
    {
        var ffmpeg = Ffmpeg.Require();
        var (directory, asset) = ProjectWith("analysis-files", TestVideos.ToneGapTone, "talk");

        var output = MediaAnalyzer.Analyze(ffmpeg, null, "no whisper in this test", directory, asset, null, cancellationToken: TestContext.Current.CancellationToken);

        var scene = Assert.Single(output.Analysis.Scenes);
        Assert.InRange(scene.Time, 1.95, 2.05);
        var silence = Assert.Single(output.Analysis.Silences);
        Assert.InRange(silence.Length, 1.9, 2.1);
        Assert.Contains("transcript skipped: no whisper in this test", output.Analysis.Notes);
        Assert.Null(output.Transcript);
        Assert.Equal(Path.Combine(directory, "assets", "talk.analysis.json"), output.AnalysisFile);

        var loaded = AnalysisFiles.LoadAnalysis(directory, asset)!;
        Assert.Equal(output.Analysis.Silences, loaded.Silences);
        Assert.Equal(output.Analysis.Scenes, loaded.Scenes);
        Assert.Contains("has no transcript (transcript skipped: no whisper in this test)",
            Assert.Throws<ZeyoCut.Core.ZeyoCutException>(() => AnalysisFiles.RequireTranscript(directory, asset)).Message);

        var description = MediaAnalyzer.Describe(output);
        Assert.Contains("scene cuts: 1 (2s)", description);
        Assert.Contains("silences ≥ 0.3s below", description);
    }

    [Fact]
    public void WhisperTranscriptHasWordTimesThatMatchTheSilences()
    {
        var ffmpeg = Ffmpeg.Require();
        var whisper = Speech.RequireWhisper();
        var directory = TestPaths.NewDirectory("analysis-whisper");
        ProjectStore.EnsureFolders(directory);
        var wav = Speech.Speak("Hello world. This is a test of the editor.", Path.Combine(directory, "assets", "speech.wav"));
        var asset = new Asset { Id = "speech", Type = AssetType.Audio, File = "assets/speech.wav", Duration = Ffprobe.AudioDuration(ffmpeg.Ffprobe, wav) };

        var output = MediaAnalyzer.Analyze(ffmpeg, whisper, null, directory, asset, "en", cancellationToken: TestContext.Current.CancellationToken);

        var transcript = Assert.IsType<Transcript>(output.Transcript);
        var words = transcript.Words.Select(w => new string(w.Text.Where(char.IsLetter).ToArray()).ToLowerInvariant()).ToList();
        Assert.Equal(["hello", "world", "this", "is", "a", "test", "of", "the", "editor"], words);
        Assert.Equal("en", transcript.Language);
        Assert.All(transcript.Words, w => Assert.True(w.End >= w.Start, $"{w.Text}: {w.Start}–{w.End}"));
        Assert.True(transcript.Words.Zip(transcript.Words.Skip(1)).All(p => p.Second.Start >= p.First.Start));

        var pause = new TimeRange(transcript.Words[1].End, transcript.Words[2].Start);
        Assert.True(pause.Length > 0.5, $"the pause after 'world' is {pause.Length}s");
        Assert.Contains(output.Analysis.Silences, s => s.Start < pause.End && s.End > pause.Start && s.Length > 0.5);
        Assert.Equal(9, output.Analysis.WordCount);

        var srt = File.ReadAllText(AnalysisFiles.SubtitlePath(directory, asset));
        Assert.StartsWith("1\r\n00:00:00,", srt);
        Assert.Contains("2\r\n", srt);
        Assert.Equal(transcript.Words.Count, AnalysisFiles.RequireTranscript(directory, asset).Words.Count);
    }

    [Fact]
    public void WhisperJsonBecomesWords()
    {
        const string json = """
        {
          "result": { "language": "en" },
          "transcription": [
            { "tokens": [ { "text": "[_BEG_]", "offsets": { "from": 0, "to": 0 }, "p": 0.9 } ] },
            { "tokens": [ { "text": " Hello", "offsets": { "from": 150, "to": 810 }, "p": 0.8 } ] },
            { "tokens": [ { "text": " world", "offsets": { "from": 830, "to": 890 }, "p": 0.6 }, { "text": ".", "offsets": { "from": 1810, "to": 1860 }, "p": 0.9 } ] },
            { "tokens": [ { "text": " [", "offsets": { "from": 1900, "to": 1950 }, "p": 0.5 }, { "text": "BLANK_AUDIO", "offsets": { "from": 1950, "to": 2500 }, "p": 0.5 }, { "text": "]", "offsets": { "from": 2500, "to": 2600 }, "p": 0.5 } ] },
            { "tokens": [ { "text": " edit", "offsets": { "from": 2820, "to": 3000 }, "p": 0.7 }, { "text": "or", "offsets": { "from": 3000, "to": 3140 }, "p": 0.9 }, { "text": "[_TT_165]", "offsets": { "from": 3300, "to": 3300 }, "p": 0.9 } ] }
          ]
        }
        """;

        var transcript = Transcriber.Parse(json);

        Assert.Equal("en", transcript.Language);
        Assert.Equal(["Hello", "world.", "editor"], transcript.Words.Select(w => w.Text));
        Assert.Equal((0.83, 0.89), (transcript.Words[1].Start, transcript.Words[1].End));
        Assert.Equal((2.82, 3.14), (transcript.Words[2].Start, transcript.Words[2].End));
        Assert.Equal(0.8, transcript.Words[2].Probability, 3);
    }

    [Fact]
    public void SourceSheetShowsTheAssetFramesWithSourceTimes()
    {
        var ffmpeg = Ffmpeg.Require();
        var (directory, asset) = ProjectWith("analysis-source-sheet", TestVideos.ToneGapTone, "talk");
        using var renderer = new FrameRenderer(directory, () => ffmpeg.Ffmpeg);

        var sheet = Preview.RenderSourceSheet(renderer, asset, 0.5, 3.5, 2, 2, 400);

        Assert.Equal([15, 105], sheet.Frames);
        Assert.Equal(6 * 3 + 160 * 2, sheet.Width);
        Assert.True(File.Exists(sheet.SavedPath));
        using var image = SkiaSharp.SKImage.FromEncodedData(sheet.Png);
        var red = TestImages.Pixel(image, 6 + 80, 6 + 60);
        var blue = TestImages.Pixel(image, 6 + 160 + 6 + 80, 6 + 60);
        Assert.True(red.Red > 200 && red.Blue < 60, $"first cell is {red}");
        Assert.True(blue.Blue > 200 && blue.Red < 60, $"second cell is {blue}");
    }
}
