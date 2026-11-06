// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Serialization;

public sealed record WordLine(int First, int Last, double Start, double End, string Text);

// analyze_video writes <name>.analysis.json, <name>.transcript.json and <name>.srt next to the asset;
// detect_beats writes <name>.beats.json.
public static class AnalysisFiles
{
    public static string AnalysisPath(string projectDirectory, Asset asset) => Beside(projectDirectory, asset, ".analysis.json");

    public static string TranscriptPath(string projectDirectory, Asset asset) => Beside(projectDirectory, asset, ".transcript.json");

    public static string SubtitlePath(string projectDirectory, Asset asset) => Beside(projectDirectory, asset, ".srt");

    public static string BeatsPath(string projectDirectory, Asset asset) => Beside(projectDirectory, asset, ".beats.json");

    public static MediaAnalysis? LoadAnalysis(string projectDirectory, Asset asset) => Load<MediaAnalysis>(AnalysisPath(projectDirectory, asset));

    public static Transcript? LoadTranscript(string projectDirectory, Asset asset) => Load<Transcript>(TranscriptPath(projectDirectory, asset));

    public static Transcript RequireTranscript(string projectDirectory, Asset asset) =>
        LoadTranscript(projectDirectory, asset)
        ?? throw new ZeyoCutException(LoadAnalysis(projectDirectory, asset) is { } analysis
            ? $"asset '{asset.Id}' has no transcript ({string.Join("; ", analysis.Notes.DefaultIfEmpty("analyze_video did not produce one"))})"
            : $"asset '{asset.Id}' has no transcript yet; run analyze_video first");

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, ProjectJson.Options), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ProjectJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ZeyoCutException($"{Path.GetFileName(path)} is not valid: {ex.Message}; run analyze_video again");
        }
    }

    // Groups words into lines: a new line after a sentence end, before a pause, or when the line is full.
    public static List<WordLine> Lines(IReadOnlyList<TranscriptWord> words, int maxWords = 7, int maxChars = 42, double pause = 0.5)
    {
        var lines = new List<WordLine>();
        var first = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var count = i - first + 1;
            var text = Join(words, first, i);
            var last = i == words.Count - 1;
            var breakAfter = last
                || count >= maxWords
                || EndsSentence(words[i].Text)
                || words[i + 1].Start - words[i].End >= pause
                || (Join(words, first, i + 1).Length > maxChars && count > 1);
            if (breakAfter)
            {
                lines.Add(new WordLine(first, i, words[first].Start, words[i].End, text));
                first = i + 1;
            }
        }

        return lines;
    }

    public static string Srt(IReadOnlyList<WordLine> lines)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var end = Math.Max(lines[i].End, lines[i].Start + 0.2);
            if (i + 1 < lines.Count)
            {
                end = Math.Min(end, lines[i + 1].Start);
            }

            builder.Append(i + 1).Append("\r\n")
                .Append(SrtTime(lines[i].Start)).Append(" --> ").Append(SrtTime(end)).Append("\r\n")
                .Append(lines[i].Text).Append("\r\n\r\n");
        }

        return builder.ToString();
    }

    public static string SrtTime(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    public static bool EndsSentence(string word) => word.TrimEnd('"', '\'', ')', '”', '’').EndsWith('.') || word.EndsWith('?') || word.EndsWith('!');

    private static string Join(IReadOnlyList<TranscriptWord> words, int first, int last) =>
        string.Join(" ", Enumerable.Range(first, last - first + 1).Select(i => words[i].Text));

    private static string Beside(string projectDirectory, Asset asset, string suffix)
    {
        var file = ProjectStore.AssetPath(projectDirectory, asset);
        return Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + suffix);
    }
}
