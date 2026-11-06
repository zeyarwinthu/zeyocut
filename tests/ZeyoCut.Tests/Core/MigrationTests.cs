// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class MigrationTests
{
    [Fact]
    public void EveryEarlierFormatHasAStep()
    {
        Assert.Equal(6, Project.CurrentFormatVersion);
        Assert.Equal(Enumerable.Range(2, Project.CurrentFormatVersion - 1), ProjectMigrations.Steps.Select(s => s.To));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ProjectsSavedByEachEarlierFormatMigrateWithoutLosingAnything(int version)
    {
        var original = File.ReadAllText(TestFixtures.OldProject(version));
        Assert.Equal(version, JsonNode.Parse(original)!["formatVersion"]!.GetValue<int>());

        var migrated = ProjectMigrations.Migrate(original, out var savedVersion);
        Assert.Equal(version, savedVersion);
        Assert.Equal(Project.CurrentFormatVersion, JsonNode.Parse(migrated)!["formatVersion"]!.GetValue<int>());

        var project = ProjectJson.Deserialize(migrated);
        Assert.Equal(Project.CurrentFormatVersion, project.FormatVersion);
        Assert.Empty(ProjectValidator.Validate(project));

        var saved = ProjectJson.Serialize(project);
        var now = Leaves(saved);
        var before = Leaves(original);
        Assert.True(before.Count > 100, $"{before.Count} values");
        var lost = before
            .Where(leaf => leaf.Key != "formatVersion")
            .Where(leaf => now.TryGetValue(leaf.Key, out var value) ? value != leaf.Value : leaf.Value != "null")
            .Select(leaf => $"{leaf.Key}: {leaf.Value} -> {(now.TryGetValue(leaf.Key, out var value) ? value : "missing")}")
            .ToList();
        Assert.Empty(lost);
        Assert.Equal(saved, ProjectJson.Serialize(ProjectJson.Deserialize(ProjectMigrations.Migrate(saved, out var again))));
        Assert.Equal(Project.CurrentFormatVersion, again);
    }

    [Fact]
    public void OpeningAnOlderProjectUpdatesItAndKeepsTheOriginal()
    {
        var directory = TestPaths.NewDirectory("migrate-open");
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        File.Copy(TestFixtures.OldProject(3), file);
        var original = File.ReadAllBytes(file);

        var session = ProjectSession.Open(directory);
        Assert.Equal(3, session.MigratedFrom);
        var copy = Assert.Single(Directory.GetFiles(Path.Combine(directory, ProjectStore.BackupsFolder)));
        Assert.Equal(copy, session.OriginalCopy);
        Assert.Matches(@"^project-v3-\d{8}-\d{6}\.json$", Path.GetFileName(copy));
        Assert.Equal(original, File.ReadAllBytes(copy));
        Assert.Equal(6, JsonNode.Parse(File.ReadAllText(file))!["formatVersion"]!.GetValue<int>());
        Assert.Equal(0, session.UndoCount);
        Assert.Equal("fx", session.Read(p => p.Name));

        var reopened = ProjectSession.Open(directory);
        Assert.Null(reopened.MigratedFrom);
        Assert.Null(reopened.OriginalCopy);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, ProjectStore.BackupsFolder)));
        Assert.Equal(ProjectJson.Serialize(session.Snapshot()), ProjectJson.Serialize(reopened.Snapshot()));

        // A second copy of the same version in the same second gets its own name.
        File.Copy(TestFixtures.OldProject(3), file, overwrite: true);
        var first = ProjectBackups.KeepOriginal(directory, 3);
        var second = ProjectBackups.KeepOriginal(directory, 3);
        Assert.NotEqual(first, second);
        Assert.Equal(original, File.ReadAllBytes(second));
    }

    [Fact]
    public void CurrentNewerAndBrokenFilesAreLeftAsTheyAre()
    {
        var current = ProjectJson.Serialize(TestProjects.Empty());
        Assert.Same(current, ProjectMigrations.Migrate(current, out var savedVersion));
        Assert.Equal(Project.CurrentFormatVersion, savedVersion);

        const string unversioned = """{ "name": "no version", "width": 320, "height": 240 }""";
        Assert.Same(unversioned, ProjectMigrations.Migrate(unversioned, out savedVersion));
        Assert.Equal(Project.CurrentFormatVersion, savedVersion);

        const string newer = """{ "formatVersion": 7, "name": "from the future", "width": 320, "height": 240 }""";
        Assert.Same(newer, ProjectMigrations.Migrate(newer, out savedVersion));
        Assert.Equal(7, savedVersion);
        Assert.Contains("formatVersion 7 is newer than this version of ZeyoCut supports (6)", ProjectValidator.Validate(ProjectJson.Deserialize(newer)));

        const string broken = """{ "formatVersion": 2, "name": """;
        Assert.Same(broken, ProjectMigrations.Migrate(broken, out savedVersion));
        Assert.StartsWith("invalid project JSON", Assert.Throws<ZeyoCutException>(() => ProjectJson.Deserialize(broken)).Message);

        const string commented = """
        {
          // written by hand
          "FormatVersion": 1, "name": "old", "width": 320, "height": 240, "fps": 24, "duration": 2,
        }
        """;
        var migrated = ProjectMigrations.Migrate(commented, out savedVersion);
        Assert.Equal(1, savedVersion);
        Assert.Equal(Project.CurrentFormatVersion, ProjectJson.Deserialize(migrated).FormatVersion);
        Assert.Single(JsonNode.Parse(migrated)!.AsObject(), p => p.Key.Equals("formatVersion", StringComparison.OrdinalIgnoreCase));

        var directory = TestPaths.NewDirectory("migrate-newer");
        File.WriteAllText(Path.Combine(directory, ProjectStore.ProjectFileName), newer);
        var session = ProjectSession.Open(directory);
        Assert.Null(session.MigratedFrom);
        Assert.False(Directory.Exists(Path.Combine(directory, ProjectStore.BackupsFolder)));
        Assert.Equal(newer, File.ReadAllText(Path.Combine(directory, ProjectStore.ProjectFileName)));
    }

    // Every value in a JSON document by its path, numbers compared as numbers.
    private static Dictionary<string, string> Leaves(string json)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(JsonNode.Parse(json), "", leaves);
        return leaves;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> leaves)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    Walk(value, path.Length == 0 ? key : $"{path}.{key}", leaves);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", leaves);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                leaves[path] = value.GetValue<double>().ToString("R", CultureInfo.InvariantCulture);
                break;
            case null:
                leaves[path] = "null";
                break;
            default:
                leaves[path] = node.ToJsonString();
                break;
        }
    }
}
