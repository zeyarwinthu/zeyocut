// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ZeyoCut.Mcp;

// Writes the day's log file in the log directory as JSON lines (see LogFiles).
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    public FileLoggerProvider(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, LogFiles.FileName(DateTime.Now));
            var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warning: file logging disabled: {ex.Message}");
        }
    }

    public string? Path { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }

    private void Write(string line)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var entry = new LogEntry(DateTimeOffset.Now, LogFiles.LevelName(logLevel), category, formatter(state, exception), Fields(state), exception?.ToString());
            provider.Write(JsonSerializer.Serialize(entry, LogFiles.JsonOptions));
        }

        // The message's named values: strings, numbers and booleans as they are, anything else as text.
        private static Dictionary<string, object?>? Fields<TState>(TState state)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                return null;
            }

            Dictionary<string, object?>? fields = null;
            foreach (var (key, value) in pairs)
            {
                if (key == "{OriginalFormat}")
                {
                    continue;
                }

                (fields ??= new Dictionary<string, object?>(StringComparer.Ordinal))[key] = value switch
                {
                    null or string or bool or int or long or double or float or decimal => value,
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture),
                };
            }

            return fields;
        }
    }
}
