// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Serialization;

// Brings a project.json written by an earlier ZeyoCut up to Project.CurrentFormatVersion, one
// version at a time. Every format so far only added fields, and a field an older file lacks gets
// its default when the project is read, so no step has to rewrite anything yet. A change that
// renames, moves or reinterprets a field goes in the step that reaches the version it ships in.
public static class ProjectMigrations
{
    public sealed record Step(int To, Action<JsonObject> Apply);

    public static IReadOnlyList<Step> Steps { get; } =
    [
        // 2: video clips, sound roles and loudness settings.
        new(2, _ => { }),
        // 3: the camera, effects, masks and motion paths.
        new(3, _ => { }),
        // 4: subject cutouts.
        new(4, _ => { }),
        // 5: tracks and redactions.
        new(5, _ => { }),
        // 6: markers and language tags.
        new(6, _ => { }),
    ];

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Returns the json at the current version and the version it was saved at. A file without a
    // formatVersion counts as current, as it always has. A newer file, or one that isn't valid
    // JSON, comes back unchanged for the reader and the validator to report.
    public static string Migrate(string json, out int savedVersion)
    {
        savedVersion = Project.CurrentFormatVersion;
        JsonObject root;
        int version;
        try
        {
            if (JsonNode.Parse(json, NodeOptions, DocumentOptions) is not JsonObject parsed
                || !parsed.TryGetPropertyValue("formatVersion", out var node)
                || node is not JsonValue value
                || !value.TryGetValue(out version))
            {
                return json;
            }

            if (version >= Project.CurrentFormatVersion)
            {
                savedVersion = version;
                return json;
            }

            root = parsed;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return json;
        }

        var from = Math.Max(version, 1);
        savedVersion = from;
        foreach (var step in Steps.Where(s => s.To > from))
        {
            step.Apply(root);
            root["formatVersion"] = step.To;
        }

        return root.ToJsonString();
    }
}
