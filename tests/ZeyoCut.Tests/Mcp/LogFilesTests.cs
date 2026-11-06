// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZeyoCut.Mcp;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Mcp;

public class LogFilesTests
{
    [Fact]
    public void LoggerWritesOneJsonObjectPerLineWithTheNamedValues()
    {
        var directory = TestPaths.NewDirectory("logfiles-write");
        string path;
        using (var provider = new FileLoggerProvider(directory))
        {
            path = provider.Path!;
            var logger = provider.CreateLogger("ZeyoCut.Test");
            logger.LogInformation("{Tool} took {Milliseconds} ms on {Day}", "render_frame", 42L, new DateOnly(2026, 11, 4));
            logger.LogError(new InvalidOperationException("broken"), "it failed:\nfirst line");
        }

        Assert.Equal(LogFiles.FileName(DateTime.Now), Path.GetFileName(path));
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        var first = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("information", first.GetProperty("level").GetString());
        Assert.Equal("ZeyoCut.Test", first.GetProperty("category").GetString());
        Assert.Equal("render_frame took 42 ms on 11/04/2026", first.GetProperty("message").GetString());
        Assert.Equal("render_frame", first.GetProperty("fields").GetProperty("Tool").GetString());
        Assert.Equal(42, first.GetProperty("fields").GetProperty("Milliseconds").GetInt64());
        Assert.Equal("11/04/2026", first.GetProperty("fields").GetProperty("Day").GetString());
        Assert.False(first.GetProperty("fields").TryGetProperty("{OriginalFormat}", out _));
        Assert.False(first.TryGetProperty("exception", out _));

        var second = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("error", second.GetProperty("level").GetString());
        Assert.Equal("it failed:\nfirst line", second.GetProperty("message").GetString());
        Assert.StartsWith("System.InvalidOperationException: broken", second.GetProperty("exception").GetString());
        Assert.False(second.TryGetProperty("fields", out _));
    }

    [Fact]
    public void LastReadsTheNewestEntriesAcrossDaysAndSkipsWhatIsNotAnEntry()
    {
        var directory = TestPaths.NewDirectory("logfiles-read");
        static string Line(string time, string level, string message) =>
            JsonSerializer.Serialize(new LogEntry(DateTimeOffset.Parse(time), level, "ZeyoCut", message), LogFiles.JsonOptions);

        File.WriteAllLines(Path.Combine(directory, "zeyocut-20261102.jsonl"),
        [
            Line("2026-11-02T10:00:00+09:00", "information", "monday start"),
            Line("2026-11-02T10:05:00+09:00", "warning", "monday warning"),
        ]);
        File.WriteAllLines(Path.Combine(directory, "zeyocut-20261103.jsonl"),
        [
            Line("2026-11-03T09:00:00+09:00", "error", "tuesday error"),
            "2026-11-03 09:01:00.000 [Information] ZeyoCut: an old plain-text line",
            "{ \"time\": \"2026-11-03T09:02:00+09:00\", \"level\": ",
            "{}",
            Line("2026-11-03T09:03:00+09:00", "information", "tuesday start"),
        ]);
        File.WriteAllText(Path.Combine(directory, "zeyocut-20261101.log"), "2026-11-01 08:00:00.000 [Warning] ZeyoCut: older format\n");

        Assert.Equal(["monday warning", "tuesday error", "tuesday start"], LogFiles.Last(directory, 3, 0, null).Select(e => e.Message));
        Assert.Equal(["monday start", "monday warning", "tuesday error", "tuesday start"], LogFiles.Last(directory, 50, 0, null).Select(e => e.Message));
        Assert.Equal(["monday warning", "tuesday error"], LogFiles.Last(directory, 50, Array.IndexOf(LogFiles.Levels, "warning"), null).Select(e => e.Message));
        Assert.Equal(["tuesday start"], LogFiles.Last(directory, 50, 0, "TUESDAY S").Select(e => e.Message));
        Assert.Empty(LogFiles.Last(Path.Combine(directory, "missing"), 50, 0, null));

        using var open = new FileStream(Path.Combine(directory, "zeyocut-20261103.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal("tuesday start", Assert.Single(LogFiles.Last(directory, 1, 0, null)).Message);
    }
}
