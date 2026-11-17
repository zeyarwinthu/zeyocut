// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace ZeyoCut.Core;

// The last MCP client a ZeyoCut server saw. The server writes it to connected.json in its log folder
// when a client lists the tools, as clients do once they connect; the editor's Welcome watches for a
// newer one to tell that Claude is connected.
public sealed record ClientConnection(string Client, string? Version, DateTimeOffset Time)
{
    public const string FileName = "connected.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string PathIn(string logDirectory) => Path.Combine(logDirectory, FileName);

    // Replaces the file whole; false when the log folder can't be written.
    public static bool Write(string logDirectory, ClientConnection connection)
    {
        var path = PathIn(logDirectory);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(temp, JsonSerializer.Serialize(connection, Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }

    // Null when no client has connected yet, or the file can't be read.
    public static ClientConnection? Read(string logDirectory)
    {
        try
        {
            return JsonSerializer.Deserialize<ClientConnection>(File.ReadAllText(PathIn(logDirectory)), Options) is { Client.Length: > 0 } connection ? connection : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
