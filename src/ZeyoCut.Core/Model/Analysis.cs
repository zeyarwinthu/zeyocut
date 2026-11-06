// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public sealed record TimeRange(double Start, double End)
{
    [JsonIgnore]
    public double Length => End - Start;
}

public sealed record SceneCut(double Time, double Score);

public sealed class TranscriptWord
{
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public double Probability { get; set; }
}

// Words with source-time timestamps (seconds into the asset), from whisper.
public sealed class Transcript
{
    public string Asset { get; set; } = "";
    public string Language { get; set; } = "";
    public string Model { get; set; } = "";
    public List<TranscriptWord> Words { get; set; } = [];
}

// What analyze_video found, saved next to the asset as <name>.analysis.json. All times are source seconds.
public sealed class MediaAnalysis
{
    public string Asset { get; set; } = "";
    public string File { get; set; } = "";
    public double Duration { get; set; }
    public DateTime AnalyzedUtc { get; set; }
    public List<SceneCut> Scenes { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? NoiseFloorDb { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SilenceThresholdDb { get; set; }

    public double MinSilence { get; set; }
    public List<TimeRange> Silences { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranscriptFile { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubtitleFile { get; set; }

    public int WordCount { get; set; }
    public int RetimedWords { get; set; }
    public List<string> Notes { get; set; } = [];
}

// Strength is relative: 1 is a strong onset for this file.
public sealed record Onset(double Time, double Strength);

// What detect_beats found, saved next to the asset as <name>.beats.json. All times are source seconds.
public sealed class BeatAnalysis
{
    public string Asset { get; set; } = "";
    public string File { get; set; } = "";
    public double Duration { get; set; }
    public DateTime AnalyzedUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Bpm { get; set; }

    // The share of grid beats that have an onset on them, 0-1.
    public double Confidence { get; set; }
    public List<double> Beats { get; set; } = [];
    public List<Onset> Onsets { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}
