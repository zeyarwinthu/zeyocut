// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ZeyoCut.Mcp;

// One line of the log file. Fields are the named values of the message.
public sealed record LogEntry(DateTimeOffset Time, string Level, string Category, string Message, Dictionary<string, object?>? Fields = null, string? Exception = null);

// The daily log files, zeyocut-yyyyMMdd.jsonl: one JSON object per line.
public static class LogFiles
{
    public const string Pattern = "zeyocut-*.jsonl";

    public static readonly string[] Levels = ["trace", "debug", "information", "warning", "error", "critical"];

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FileName(DateTime day) => $"zeyocut-{day:yyyyMMdd}.jsonl";

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };

    // The last 'count' entries at 'minimum' (an index into Levels) or above that contain 'text'
    // (any case) in their message, category, fields or exception, oldest first. Files are read
    // newest first until there are enough; lines that aren't log entries are skipped.
    public static List<LogEntry> Last(string directory, int count, int minimum, string? text)
    {
        var picked = new List<LogEntry>();
        if (!Directory.Exists(directory))
        {
            return picked;
        }

        foreach (var file in Directory.GetFiles(directory, Pattern).OrderDescending(StringComparer.OrdinalIgnoreCase))
        {
            string[] lines;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = lines.Length - 1; i >= 0 && picked.Count < count; i--)
            {
                if (Parse(lines[i]) is { } entry && Array.IndexOf(Levels, entry.Level) >= minimum && Contains(entry, text))
                {
                    picked.Add(entry);
                }
            }

            if (picked.Count >= count)
            {
                break;
            }
        }

        picked.Reverse();
        return picked;
    }

    private static LogEntry? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LogEntry>(line, JsonOptions) is { Level: not null, Message: not null, Category: not null } entry ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Contains(LogEntry entry, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        return entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
            || entry.Fields?.Values.Any(v => v?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) == true;
    }
}

// Logs every tool call: its arguments, how long it took, and the error when it failed.
public static class ToolCallLog
{
    public const string Category = "ZeyoCut.Tools";

    private const int ArgumentsLength = 2000;
    private const int ErrorLength = 500;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (request, ct) =>
    {
        var tool = request.Params?.Name ?? "(no name)";
        var arguments = Shorten(request.Params?.Arguments is { } values ? JsonSerializer.Serialize(values, LogFiles.JsonOptions) : "{}", ArgumentsLength);
        var logger = request.Services?.GetService<ILoggerFactory>()?.CreateLogger(Category);
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await next(request, ct);
            if (result.IsError == true)
            {
                var error = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                logger?.LogWarning("{Tool} failed after {Milliseconds} ms: {Error}; arguments {Arguments}", tool, clock.ElapsedMilliseconds, Shorten(error, ErrorLength), arguments);
            }
            else
            {
                logger?.LogInformation("{Tool} took {Milliseconds} ms; arguments {Arguments}", tool, clock.ElapsedMilliseconds, arguments);
            }

            return result;
        }
        catch (McpException ex) when (ex is not McpProtocolException)
        {
            // A tool's own error, such as a misspelled id: the reply the SDK gives for it, without the
            // SDK also logging it as an unhandled exception with its stack.
            logger?.LogWarning("{Tool} failed after {Milliseconds} ms: {Error}; arguments {Arguments}", tool, clock.ElapsedMilliseconds, Shorten(ex.Message, ErrorLength), arguments);
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"An error occurred invoking '{request.Params?.Name}': {ex.Message}" }] };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex is McpException ? null : ex, "{Tool} failed after {Milliseconds} ms: {Error}; arguments {Arguments}", tool, clock.ElapsedMilliseconds, Shorten(ex.Message, ErrorLength), arguments);
            throw;
        }
    };

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
