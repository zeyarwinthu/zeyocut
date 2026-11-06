// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZeyoCut.Core;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

public sealed record WhisperPaths(string Cli, string Model);

public static class WhisperLocator
{
    public const string PathVariable = "WHISPER_PATH";
    public const string ModelVariable = "WHISPER_MODEL";

    // The path may be whisper-cli.exe or a folder holding it (also bin\Release inside a whisper.cpp
    // release). Without a model setting, ggml-small.bin (or any ggml-*.bin) next to it or in a 'models'
    // folder up to two levels above is used.
    public static bool TryLocate(string? configuredPath, string? configuredModel, out WhisperPaths? paths, out string? error)
    {
        paths = null;
        error = null;
        var path = Clean(configuredPath) ?? Clean(Environment.GetEnvironmentVariable(PathVariable));
        string? cli;
        if (path is not null)
        {
            cli = ResolveCli(path);
            if (cli is null)
            {
                error = $"whisper path '{path}' has no whisper-cli.exe";
                return false;
            }
        }
        else
        {
            cli = SearchPath("whisper-cli");
            if (cli is null)
            {
                error = $"whisper-cli was not found; set {PathVariable} to whisper-cli.exe or its folder (and {ModelVariable} to a ggml model file)";
                return false;
            }
        }

        var model = Clean(configuredModel) ?? Clean(Environment.GetEnvironmentVariable(ModelVariable));
        if (model is not null)
        {
            if (!File.Exists(model))
            {
                error = $"whisper model '{model}' does not exist";
                return false;
            }
        }
        else
        {
            model = FindModel(cli);
            if (model is null)
            {
                error = $"found {cli} but no ggml model; set {ModelVariable} to a file such as ggml-small.bin";
                return false;
            }
        }

        paths = new WhisperPaths(cli, Path.GetFullPath(model));
        return true;
    }

    private static string? ResolveCli(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        foreach (var candidate in new[] { "whisper-cli.exe", Path.Combine("bin", "Release", "whisper-cli.exe"), Path.Combine("Release", "whisper-cli.exe"), Path.Combine("bin", "whisper-cli.exe") })
        {
            var full = Path.Combine(path, candidate);
            if (File.Exists(full))
            {
                return Path.GetFullPath(full);
            }
        }

        return null;
    }

    private static string? FindModel(string cli)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(cli)!);
        for (var level = 0; level <= 3 && directory is not null; level++, directory = directory.Parent)
        {
            foreach (var folder in new[] { directory.FullName, Path.Combine(directory.FullName, "models") })
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                var small = Path.Combine(folder, "ggml-small.bin");
                if (File.Exists(small))
                {
                    return small;
                }

                var any = Directory.GetFiles(folder, "ggml-*.bin").Where(f => !Path.GetFileName(f).Contains("silero", StringComparison.OrdinalIgnoreCase)).Order().FirstOrDefault();
                if (any is not null)
                {
                    return any;
                }
            }
        }

        return null;
    }

    private static string? SearchPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name + ".exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
}

// Runs whisper-cli with one word per segment and reads word timestamps from its full JSON output.
public static partial class Transcriber
{
    [GeneratedRegex(@"progress\s*=\s*(\d+)%")]
    private static partial Regex Progress();

    public static List<string> Arguments(WhisperPaths whisper, string wav, string outputPrefix, string? language)
    {
        var threads = Math.Clamp(Environment.ProcessorCount, 1, 8);
        return
        [
            "-m", whisper.Model, "-f", wav, "-l", string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim(),
            "-t", threads.ToString(CultureInfo.InvariantCulture),
            "-ml", "1", "-sow", "-ojf", "-of", outputPrefix, "-np", "-pp",
        ];
    }

    public static Transcript Transcribe(WhisperPaths whisper, string wav, string assetId, string? language, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var prefix = Path.Combine(Path.GetDirectoryName(wav)!, Path.GetFileNameWithoutExtension(wav) + ".whisper");
        var json = prefix + ".json";
        try
        {
            var result = ProcessRunner.RunLines(whisper.Cli, Arguments(whisper, wav, prefix, language), null, line =>
            {
                if (Progress().Match(line) is { Success: true } match)
                {
                    progress?.Invoke(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0);
                }
            }, cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(json))
            {
                throw new ZeyoCutException($"whisper-cli failed (exit code {result.ExitCode}): {result.StandardError.Trim()}");
            }

            var transcript = Parse(File.ReadAllText(json));
            transcript.Asset = assetId;
            transcript.Model = Path.GetFileName(whisper.Model);
            return transcript;
        }
        finally
        {
            try
            {
                File.Delete(json);
            }
            catch (IOException)
            {
            }
        }
    }

    // Builds words from tokens: a token starting with a space starts a word, other tokens (word
    // pieces, punctuation) join the current one. Punctuation does not stretch a word's end time.
    // Special tokens ([_BEG_], [_TT_n]) and bracketed notes ([BLANK_AUDIO], (music)) are dropped.
    public static Transcript Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var language = root.TryGetProperty("result", out var result) && result.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "";
        var words = new List<TranscriptWord>();
        var probabilities = new List<double>();
        TranscriptWord? current = null;

        void Finish()
        {
            if (current is not null && !IsNote(current.Text) && current.Text.Length > 0)
            {
                current.Probability = probabilities.Count > 0 ? Math.Round(probabilities.Average(), 3) : 0;
                words.Add(current);
            }

            current = null;
            probabilities.Clear();
        }

        if (root.TryGetProperty("transcription", out var segments))
        {
            foreach (var segment in segments.EnumerateArray())
            {
                if (!segment.TryGetProperty("tokens", out var tokens))
                {
                    continue;
                }

                foreach (var token in tokens.EnumerateArray())
                {
                    var text = token.GetProperty("text").GetString() ?? "";
                    if (text.StartsWith("[_", StringComparison.Ordinal) || text.Trim().Length == 0)
                    {
                        continue;
                    }

                    var offsets = token.GetProperty("offsets");
                    var from = offsets.GetProperty("from").GetDouble() / 1000;
                    var to = offsets.GetProperty("to").GetDouble() / 1000;
                    var p = token.TryGetProperty("p", out var prob) ? prob.GetDouble() : 0;
                    var punctuation = text.Trim().All(c => char.IsPunctuation(c) || char.IsSymbol(c));
                    var opensNote = text.TrimStart().StartsWith('[') || text.TrimStart().StartsWith('(');
                    if (current is null || (text.StartsWith(' ') && (!punctuation || opensNote)))
                    {
                        Finish();
                        current = new TranscriptWord { Text = text.Trim(), Start = from, End = Math.Max(from, to) };
                    }
                    else
                    {
                        current.Text += text.Trim();
                        if (!punctuation)
                        {
                            current.End = Math.Max(current.End, to);
                        }
                    }

                    if (!punctuation)
                    {
                        probabilities.Add(p);
                    }
                }
            }
        }

        Finish();
        return new Transcript { Language = language, Words = words };
    }

    private static bool IsNote(string text) =>
        (text.StartsWith('[') && text.EndsWith(']')) || (text.StartsWith('(') && text.EndsWith(')')) || (text.StartsWith('*') && text.EndsWith('*'));
}
