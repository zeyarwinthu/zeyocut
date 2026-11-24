// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// A step in the Welcome's list: what it's for, and whether it's the one shown and done.
public sealed record WelcomeStep(int Number, string Title, string Detail, bool Current, bool Done)
{
    public string Mark => Done ? "✓" : Number.ToString(CultureInfo.InvariantCulture);
}

// What the Welcome view shows the first time the editor starts: the server's entry for Claude
// Desktop's config, the sample project it offers, and whether Claude has connected since it opened
// (the server notes each client in its log folder: ClientConnection).
public static class Welcome
{
    public const string ServerFile = "ZeyoCut.Mcp.exe";

    public const string SampleName = "tora-jump";

    private static readonly JsonSerializerOptions Text = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The server next to the editor, as the release zip and the bundle have it.
    public static string ServerPath(string editorFolder) => Path.Combine(editorFolder, ServerFile);

    // The zeyocut entry for claude_desktop_config.json, as the install guide shows it.
    public static string Config(string serverPath) =>
        $"{{\n  \"mcpServers\": {{\n    \"zeyocut\": {{\n      \"command\": {JsonSerializer.Serialize(serverPath, Text)}\n    }}\n  }}\n}}";

    // The sample next to the editor; null when it isn't there.
    public static string? Sample(string editorFolder)
    {
        var folder = Path.Combine(editorFolder, "examples", SampleName);
        return ProjectStore.IsProjectDirectory(folder) ? folder : null;
    }

    // The sample's copy in the projects folder, made the first time: a project of that name there
    // already is the one to open.
    public static string CopySample(string sample, string projectsRoot)
    {
        var target = Path.Combine(projectsRoot, SampleName);
        if (ProjectStore.IsProjectDirectory(target))
        {
            return target;
        }

        foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }

        return target;
    }

    // Whether a client connected at or after 'since', the Welcome's start.
    public static bool Connected(ClientConnection? last, DateTimeOffset since) => last is { } connection && connection.Time >= since;

    // What the Connect step says: the client that connected, or that it's waiting, and when Claude
    // last connected before.
    public static string Status(ClientConnection? last, DateTimeOffset since) =>
        Connected(last, since) ? $"Connected: {last!.Client}" : "Waiting for Claude…";

    public static string? Earlier(ClientConnection? last, DateTimeOffset since) =>
        last is { } connection && connection.Time < since
            ? $"Claude last connected on {connection.Time.ToLocalTime().ToString("d MMM 'at' HH:mm", CultureInfo.InvariantCulture)} ({connection.Client}). Restart it to connect again."
            : null;
}
