// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp.Demo;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class DeliveryExportTests
{
    private readonly ITestOutputHelper _output;

    public DeliveryExportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static ProjectSession ToneProject(string name, double duration = 4, int width = 160, int height = 90, string background = "#404040")
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: width, height: height, fps: 30, duration: duration));
        session.Mutate("background", p => p.BackgroundColor = background);
        return session;
    }

    private static void AddTone(ProjectSession session, FfmpegPaths ffmpeg, string id, double frequency, double seconds, double amplitude, double start, string role)
    {
        var file = Path.Combine(session.Directory, $"src_{id}.wav");
        ToneGenerator.WriteTone(file, frequency, seconds, amplitude);
        var probe = new AssetProbe(path => Ffprobe.AudioDuration(ffmpeg.Ffprobe, path));
        session.Mutate("import", p => AssetImporter.Import(p, session.Directory, file, id, probe));
        session.Mutate("audio", p => ProjectEditor.AddAudioLayer(p, new AudioLayerOptions { Asset = id, Id = id, Start = start, Role = role }));
    }

    private ExportResult Export(ProjectSession session, FfmpegPaths ffmpeg, string fileName, Preset? preset = null, int crf = 18)
    {
        var output = Path.Combine(session.Directory, "exports", fileName);
        var result = Exporter.Export(session.Snapshot(), session.Directory, output, crf, ffmpeg, cancellationToken: TestContext.Current.CancellationToken, preset: preset);
        _output.WriteLine(ExportSummary.Describe(result));
        foreach (var note in result.Notes ?? [])
        {
            _output.WriteLine($"note: {note}");
        }

        return result;
    }

    private static double IntegratedLoudness(FfmpegPaths ffmpeg, string file)
    {
        var result = ProcessRunner.Run(ffmpeg.Ffmpeg, ["-hide_banner", "-nostats", "-i", file, "-vn", "-af", "loudnorm=print_format=json", "-f", "null", "-"], TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return FfmpegArgs.ParseLoudness(result.StandardError).InputI;
    }

    // Mean level (dB) of a narrow band around frequency, between start and start + length.
    internal static double BandLevel(FfmpegPaths ffmpeg, string file, double start, double length, int frequency)
    {
        var band = $"bandpass=f={frequency}:width_type=q:w=8";
        var result = ProcessRunner.Run(
            ffmpeg.Ffmpeg,
            ["-hide_banner", "-nostats", "-ss", Num(start), "-t", Num(length), "-i", file, "-vn", "-af", $"{band},{band},volumedetect", "-f", "null", "-"],
            TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, result.StandardError);
        var match = Regex.Match(result.StandardError, @"mean_volume:\s*(-?[\d.]+) dB");
        Assert.True(match.Success, result.StandardError);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void BuildsDuckingAndLoudnessFilters()
    {
        var clips = new List<AudioClip>
        {
            new("talk", @"C:\p\assets\talk.mp4", 0, 0, 3, 1, 0, 0, Role: AudioRole.Voice),
            new("song", @"C:\p\assets\song.mp3", 0, 0, 3, 0.5, 0, 0, Role: AudioRole.Music),
            new("boing", @"C:\p\assets\boing.wav", 1, 0, 0.5, 1, 0, 0, Role: AudioRole.Effects),
        };
        var measured = new LoudnessMeasurement(-23.5, -4.2, 6.3, -34, 0.4);
        var mix = new AudioMix(true, -14, -1, measured);

        var args = FfmpegArgs.Build(new EncodeSettings(1280, 720, 30, 90, 18), clips, "out.mp4", mix);
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[a1]asplit=2[voicemix][voicekey];[voicekey]apad[voicepad];[a2][voicepad]sidechaincompress=threshold=0.03:ratio=6:attack=20:release=400:makeup=1[ducked]", filter);
        Assert.Contains("[voicemix][ducked][a3]amix=inputs=3:duration=longest:dropout_transition=0:normalize=0,asetpts=N/SR/TB,loudnorm=I=-14:TP=-1:LRA=11:measured_I=-23.5:measured_TP=-4.2:measured_LRA=6.3:measured_thresh=-34:offset=0.4:linear=true:print_format=summary,aresample=48000,apad[aout]", filter);

        var wide = FfmpegArgs.Build(new EncodeSettings(1280, 720, 30, 90, 18), clips, "out.mp4", mix with { Measured = measured with { InputLra = 14.2 } });
        Assert.Contains(":LRA=16:measured_I=", wide[wide.IndexOf("-filter_complex") + 1]);

        var measure = FfmpegArgs.MeasureArguments(clips, mix with { Measured = null }, 3);
        var line = string.Join(" ", measure);
        Assert.StartsWith(@"-hide_banner -nostdin -nostats -loglevel info -i C:\p\assets\talk.mp4 -i C:\p\assets\song.mp3 -i C:\p\assets\boing.wav", line);
        Assert.EndsWith("-map [aout] -t 3 -f null -", line);
        var measureFilter = measure[measure.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:a]atrim=start=0:duration=3", measureFilter);
        Assert.Contains("[a0]asplit=2[voicemix][voicekey]", measureFilter);
        Assert.Contains("[voicemix][ducked][a2]amix=inputs=3:duration=longest:dropout_transition=0:normalize=0,asetpts=N/SR/TB,loudnorm=I=-14:TP=-1:LRA=11:print_format=json[aout]", measureFilter);
        Assert.DoesNotContain("apad[aout]", measureFilter);

        List<AudioClip> twoVoices = [clips[0], clips[0] with { LayerId = "talk_2", Start = 3 }, clips[1]];
        var pair = FfmpegArgs.Build(new EncodeSettings(1280, 720, 30, 180, 18), twoVoices, "out.mp4", mix with { LoudnessTarget = null });
        var pairFilter = pair[pair.IndexOf("-filter_complex") + 1];
        Assert.Contains("[a1][a2]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,asetpts=N/SR/TB[voice];", pairFilter);
        Assert.Contains("[voice]asplit=2[voicemix][voicekey]", pairFilter);
        Assert.Contains("[voicemix][ducked]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,asetpts=N/SR/TB,apad[aout]", pairFilter);

        var plain = FfmpegArgs.Build(new EncodeSettings(1280, 720, 30, 90, 18), clips, "out.mp4", new AudioMix(false, null, -1, null));
        var plainFilter = plain[plain.IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("sidechaincompress", plainFilter);
        Assert.DoesNotContain("loudnorm", plainFilter);
        Assert.DoesNotContain("asetpts=N/SR/TB", plainFilter);
        Assert.Contains("[a1][a2][a3]amix=inputs=3:duration=longest:dropout_transition=0:normalize=0,apad[aout]", plainFilter);

        Assert.False(FfmpegArgs.Ducks([clips[1], clips[2]], mix));
        Assert.False(FfmpegArgs.Ducks(clips, mix with { Ducking = false }));
        Assert.True(FfmpegArgs.Ducks(clips, mix));
    }

    [Fact]
    public void ParsesTheLoudnormMeasurement()
    {
        const string stderr = """
            [Parsed_loudnorm_0 @ 0000023a]
            {
            	"input_i" : "-23.54",
            	"input_tp" : "-4.20",
            	"input_lra" : "6.30",
            	"input_thresh" : "-34.01",
            	"output_i" : "-14.02",
            	"output_tp" : "-1.00",
            	"output_lra" : "5.10",
            	"output_thresh" : "-24.40",
            	"normalization_type" : "dynamic",
            	"target_offset" : "0.02"
            }
            """;
        Assert.Equal(new LoudnessMeasurement(-23.54, -4.2, 6.3, -34.01, 0.02), FfmpegArgs.ParseLoudness(stderr));

        var silent = FfmpegArgs.ParseLoudness("""{ "input_i" : "-inf", "input_tp" : "-inf", "input_lra" : "0.00", "input_thresh" : "-inf", "target_offset" : "inf" }""");
        Assert.Equal(double.NegativeInfinity, silent.InputI);
        Assert.Equal(double.PositiveInfinity, silent.TargetOffset);

        Assert.Contains("ffmpeg loudnorm printed no measurement",
            Assert.Throws<ZeyoCutException>(() => FfmpegArgs.ParseLoudness("Error opening input")).Message);
    }

    [Fact]
    public void PresetSettingsScaleTheFramesAndSetTheEncoder()
    {
        var settings = new EncodeSettings(640, 360, 30, 30, 18, "medium", ["-profile:v", "high", "-bf", "2", "-g", "15"], 192, 1920, 1080);
        var args = FfmpegArgs.Build(settings, [], "out.mp4");
        var line = string.Join(" ", args);

        Assert.Contains("-s 640x360", line);
        Assert.Contains("[0:v]scale=1920:1080:flags=lanczos:out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv[v]", line);
        Assert.Contains("-c:v libx264 -preset medium -crf 18", line);
        Assert.Contains("-profile:v high -bf 2 -g 15", line);

        var same = FfmpegArgs.Build(settings with { Width = 1920, Height = 1080 }, [], "out.mp4");
        Assert.Contains("[0:v]scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv[v]", same);
    }

    [Fact]
    public void ExportNormalizesTheMixToTheLoudnessTarget()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = ToneProject("export-loudness");
        AddTone(session, ffmpeg, "quiet", 440, 4, 0.05, 0, "music");

        var normalized = Export(session, ffmpeg, "normalized.mp4");
        var loudness = IntegratedLoudness(ffmpeg, normalized.OutputPath);
        _output.WriteLine($"normalized: {loudness:0.00} LUFS");
        Assert.InRange(loudness, -15, -13);
        Assert.Contains(normalized.Notes!, n => n.StartsWith("loudness: ") && n.Contains("→ -14 LUFS (true peak at most -1 dBTP)"));
        Assert.DoesNotContain(normalized.Notes!, n => n.Contains("ducked"));

        session.Mutate("quieter", p => ProjectEditor.UpdateProject(p, new ProjectOptions { LoudnessTarget = -23 }));
        var broadcast = IntegratedLoudness(ffmpeg, Export(session, ffmpeg, "broadcast.mp4").OutputPath);
        _output.WriteLine($"-23 target: {broadcast:0.00} LUFS");
        Assert.InRange(broadcast, -24, -22);

        session.Mutate("off", p => ProjectEditor.UpdateProject(p, new ProjectOptions { LoudnessTarget = 0 }));
        var raw = Export(session, ffmpeg, "raw.mp4");
        var rawLoudness = IntegratedLoudness(ffmpeg, raw.OutputPath);
        _output.WriteLine($"off: {rawLoudness:0.00} LUFS");
        Assert.InRange(rawLoudness, -30, -24);
        Assert.Contains("loudness normalization off", raw.Notes!);
    }

    [Fact]
    public void MusicIsDuckedUnderVoice()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = ToneProject("export-ducking", duration: 5);
        AddTone(session, ffmpeg, "song", 1000, 5, 0.2, 0, "music");
        AddTone(session, ffmpeg, "voice", 300, 1.5, 0.4, 1, "voice");

        (double Before, double During, double After) MusicLevels(string file) =>
            (BandLevel(ffmpeg, file, 0.2, 0.6, 1000), BandLevel(ffmpeg, file, 1.4, 0.9, 1000), BandLevel(ffmpeg, file, 4.1, 0.5, 1000));

        var ducked = Export(session, ffmpeg, "ducked.mp4");
        Assert.Contains("music ducked under voice", ducked.Notes!);
        var on = MusicLevels(ducked.OutputPath);

        session.Mutate("no ducking", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Ducking = false }));
        var flat = Export(session, ffmpeg, "flat.mp4");
        Assert.DoesNotContain("music ducked under voice", flat.Notes!);
        var off = MusicLevels(flat.OutputPath);

        _output.WriteLine($"ducking on:  music {on.Before:0.0} / {on.During:0.0} / {on.After:0.0} dB (before / during / after the voice)");
        _output.WriteLine($"ducking off: music {off.Before:0.0} / {off.During:0.0} / {off.After:0.0} dB");
        Assert.True(on.During < on.Before - 6, $"music should drop more than 6 dB under the voice (before {on.Before:0.0} dB, during {on.During:0.0} dB)");
        Assert.InRange(on.After - on.Before, -1.5, 1.5);
        Assert.InRange(off.During - off.Before, -1.5, 1.5);
    }

    [Fact]
    public void PresetExportIsFullHdHighProfile()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = ToneProject("export-preset", duration: 1, width: 640, height: 360, background: "#336699");

        Assert.Contains("the project is 640x360 but shorts_1080p is 1080x1920 (vertical); change the canvas with update_project preset=shorts_1080p and fit video layers with reframe_to_vertical first",
            Assert.Throws<ZeyoCutException>(() => Exporter.CheckPreset(session.Snapshot(), Presets.Shorts1080p)).Message);

        var result = Export(session, ffmpeg, "youtube.mp4", Presets.Youtube1080p, crf: 23);

        Assert.Equal((1920, 1080), (result.Media.Width, result.Media.Height));
        Assert.Equal(30, result.Media.VideoFrames);
        Assert.Contains("preset youtube_1080p: 1920x1080, H.264 high profile, CRF 23, AAC 192k", result.Notes!);

        var probe = ProcessRunner.Run(ffmpeg.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=profile,color_range,color_space,color_transfer,color_primaries", "-of", "default=noprint_wrappers=1", result.OutputPath], TimeSpan.FromSeconds(30));
        Assert.Equal(["profile=High", "color_range=tv", "color_space=bt709", "color_transfer=bt709", "color_primaries=bt709"], probe.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var pixels = Ffmpeg.ExtractFrameRgba(ffmpeg, result.OutputPath, 15, 1920, 1080);
        var center = (1080 / 2 * 1920 + 1920 / 2) * 4;
        Assert.InRange(pixels[center], (byte)48, (byte)54);
        Assert.InRange(pixels[center + 1], (byte)99, (byte)105);
        Assert.InRange(pixels[center + 2], (byte)150, (byte)156);
    }
}
