// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

public sealed record AnalysisOutput(MediaAnalysis Analysis, Transcript? Transcript, string AnalysisFile);

// analyze_video: scene cuts (video), noise floor and silences, and a word-level transcript (whisper),
// saved next to the asset.
public static class MediaAnalyzer
{
    public const double MinSilence = 0.3;

    public static AnalysisOutput Analyze(
        FfmpegPaths ffmpeg,
        WhisperPaths? whisper,
        string? whisperProblem,
        string projectDirectory,
        Asset asset,
        string? language,
        Action<double, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (asset.Type is not (AssetType.Video or AssetType.Audio))
        {
            throw new ZeyoCutException($"asset '{asset.Id}' is a {asset.Type.ToString().ToLowerInvariant()} asset; analyze_video works on video and audio assets");
        }

        var file = ProjectStore.AssetPath(projectDirectory, asset);
        if (!File.Exists(file))
        {
            throw new ZeyoCutException($"asset file is missing: {file}");
        }

        var duration = asset.Duration ?? 0;
        var hasSound = asset.Type == AssetType.Audio || asset.HasAudio != false;
        var analysis = new MediaAnalysis
        {
            Asset = asset.Id,
            File = asset.File,
            Duration = duration,
            AnalyzedUtc = DateTime.UtcNow,
            MinSilence = MinSilence,
        };
        Transcript? transcript = null;

        if (asset.Type == AssetType.Video)
        {
            progress?.Invoke(0, "finding scene cuts");
            analysis.Scenes = AudioAnalyzer.Scenes(ffmpeg.Ffmpeg, file, duration, p => progress?.Invoke(p * 0.3, "finding scene cuts"), cancellationToken);
        }

        if (!hasSound)
        {
            analysis.Notes.Add("the clip has no sound, so there are no silences or transcript");
        }
        else
        {
            progress?.Invoke(0.3, "measuring the noise floor");
            var levels = AudioAnalyzer.Levels(ffmpeg.Ffmpeg, file, 0, duration, cancellationToken);
            analysis.NoiseFloorDb = levels.NoiseFloorDb;
            analysis.SilenceThresholdDb = levels.ThresholdDb;
            progress?.Invoke(0.35, "finding silences");
            analysis.Silences = AudioAnalyzer.Silences(ffmpeg.Ffmpeg, file, 0, duration, levels.ThresholdDb, MinSilence, cancellationToken);

            if (whisper is null)
            {
                analysis.Notes.Add($"transcript skipped: {whisperProblem ?? "whisper-cli is not configured"}");
            }
            else
            {
                var wav = Path.Combine(Path.GetTempPath(), $"zeyocut_{Guid.NewGuid():N}.wav");
                try
                {
                    progress?.Invoke(0.4, "extracting speech audio");
                    AudioAnalyzer.ExtractSpeechWav(ffmpeg.Ffmpeg, file, wav, cancellationToken);
                    progress?.Invoke(0.45, "transcribing");
                    transcript = Transcriber.Transcribe(whisper, wav, asset.Id, language, p => progress?.Invoke(0.45 + p * 0.53, "transcribing"), cancellationToken);
                }
                finally
                {
                    try
                    {
                        File.Delete(wav);
                    }
                    catch (IOException)
                    {
                    }
                }

                analysis.RetimedWords = WordTiming.FitToSpeech(transcript.Words, analysis.Silences, duration);
                var transcriptFile = AnalysisFiles.TranscriptPath(projectDirectory, asset);
                var subtitleFile = AnalysisFiles.SubtitlePath(projectDirectory, asset);
                AnalysisFiles.Save(transcriptFile, transcript);
                File.WriteAllText(subtitleFile, AnalysisFiles.Srt(AnalysisFiles.Lines(transcript.Words)), new UTF8Encoding(false));
                analysis.TranscriptFile = Relative(projectDirectory, transcriptFile);
                analysis.SubtitleFile = Relative(projectDirectory, subtitleFile);
                analysis.WordCount = transcript.Words.Count;
                if (transcript.Words.Count == 0)
                {
                    analysis.Notes.Add("whisper heard no words");
                }
            }
        }

        var analysisFile = AnalysisFiles.AnalysisPath(projectDirectory, asset);
        AnalysisFiles.Save(analysisFile, analysis);
        progress?.Invoke(1, "done");
        return new AnalysisOutput(analysis, transcript, analysisFile);
    }

    public static string Describe(AnalysisOutput output)
    {
        var analysis = output.Analysis;
        var lines = new List<string> { $"analyzed '{analysis.Asset}' ({Num(analysis.Duration)}s) → {output.AnalysisFile}" };
        if (analysis.Scenes.Count > 0 || analysis.Notes.Count == 0)
        {
            var times = string.Join(", ", analysis.Scenes.Take(20).Select(s => $"{Num(s.Time)}s"));
            lines.Add($"scene cuts: {analysis.Scenes.Count}{(analysis.Scenes.Count > 0 ? $" ({times}{(analysis.Scenes.Count > 20 ? ", ..." : "")})" : "")}");
        }

        if (analysis.SilenceThresholdDb is { } threshold)
        {
            var total = analysis.Silences.Sum(s => s.Length);
            lines.Add($"silences ≥ {Num(analysis.MinSilence)}s below {threshold.ToString("0.0", CultureInfo.InvariantCulture)} dB (noise floor {analysis.NoiseFloorDb?.ToString("0.0", CultureInfo.InvariantCulture)} dB): {analysis.Silences.Count}, {Num(total)}s in total");
        }

        if (output.Transcript is { } transcript)
        {
            lines.Add($"transcript: {transcript.Words.Count} words ({transcript.Language}) → {analysis.TranscriptFile}, subtitles → {analysis.SubtitleFile}");
            if (analysis.RetimedWords > 0)
            {
                lines.Add($"word times: {analysis.RetimedWords} adjusted to the speech between the measured silences");
            }

            lines.Add("read it with get_transcript; cut words with cut_by_transcript");
        }

        lines.AddRange(analysis.Notes.Select(n => $"note: {n}"));
        return string.Join("\n", lines);
    }

    private static string Relative(string projectDirectory, string path) => Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
