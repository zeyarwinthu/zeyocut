// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ZeyoCut.Core.Editing;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Mcp;

// Files in the repository that must follow the code: THIRD_PARTY_NOTICES.md names every package the
// server ships with, docs/TOOLS.md is the server's own tool list, and the example projects open.
public class RepositoryFilesTests
{
    private const string UpdateDocsVariable = "ZEYOCUT_UPDATE_DOCS";

    [Fact]
    public void TheExamplesOpenWithoutProblems()
    {
        var examples = Directory.GetDirectories(Path.Combine(TestPaths.RepositoryRoot, "examples"))
            .Where(d => File.Exists(Path.Combine(d, ProjectStore.ProjectFileName)))
            .ToList();
        Assert.Equal(3, examples.Count);
        foreach (var directory in examples)
        {
            var name = Path.GetFileName(directory);
            var project = ProjectStore.Load(directory, out _);
            Assert.True(ProjectValidator.Validate(project) is [], $"{name}: {string.Join("; ", ProjectValidator.Validate(project))}");
            Assert.All(project.Assets, asset => Assert.True(File.Exists(ProjectStore.AssetPath(directory, asset)), $"{name}: {asset.File} is missing"));
        }
    }

    [Fact]
    public void TheNoticesNameEveryPackageInTheServer()
    {
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ZeyoCut.Mcp.deps.json")));
        var packages = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name.Split('/'))
            .ToList();
        Assert.NotEmpty(packages);

        var notices = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "THIRD_PARTY_NOTICES.md"));
        var missing = packages.Where(p => !notices.Contains($"| {p[0]} | {p[1]} |", StringComparison.Ordinal)
            && !notices.Contains($"| {p[0]} (macOS files only; not in the Windows release) | {p[1]} |", StringComparison.Ordinal)).Select(p => $"{p[0]} {p[1]}").ToList();
        Assert.True(missing.Count == 0, $"THIRD_PARTY_NOTICES.md doesn't list: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task TheToolReferenceMatchesTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("tool-reference");
        string reference;
        await using (var client = await McpServerTests.StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct))
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            var prompts = await client.ListPromptsAsync(cancellationToken: ct);
            reference = ToolReference(tools, prompts);
        }

        var path = Path.Combine(TestPaths.RepositoryRoot, "docs", "TOOLS.md");
        if (Environment.GetEnvironmentVariable(UpdateDocsVariable) == "1")
        {
            File.WriteAllText(path, reference);
        }
        var written = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        Assert.True(written == reference, $"docs/TOOLS.md doesn't match the server's tools. Run this test with {UpdateDocsVariable}=1 to write it again.");
    }

    private static string ToolReference(IList<McpClientTool> tools, IList<McpClientPrompt> prompts)
    {
        var text = new StringBuilder();
        text.Append("<!-- Made by RepositoryFilesTests.TheToolReferenceMatchesTheServer from the server's tool list; don't edit it by hand.\n");
        text.Append($"     After changing a tool or a prompt, run that test with {UpdateDocsVariable}=1 to write it again. -->\n\n");
        text.Append("# Tool reference\n\n");
        text.Append($"The `zeyocut` server has {tools.Count} tools and {prompts.Count} [workflow prompts](#workflow-prompts). Each is listed here as the server describes it to the model, with its parameters. ");
        text.Append("The [guide](GUIDE.md) shows how they work together, with an example call and the real response for each. ");
        text.Append("One more tool, `describe_clip`, appears only when a clip describer is configured (see the [guide](GUIDE.md#optional-describe_clip)).\n\n");

        var sorted = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        text.Append("| Tool | What it does |\n|---|---|\n");
        foreach (var tool in sorted)
        {
            text.Append($"| [`{tool.Name}`](#{tool.Name}) | {Cell(FirstSentence(tool.Description))} |\n");
        }

        text.Append("\n## Tools\n");
        foreach (var tool in sorted)
        {
            text.Append($"\n### `{tool.Name}`\n\n{Escape(tool.Description)}\n\n");
            var rows = new StringBuilder();
            Parameters(rows, tool.JsonSchema, "");
            text.Append(rows.Length == 0 ? "No parameters.\n" : $"| Parameter | Type | Description |\n|---|---|---|\n{rows}");
        }

        text.Append("\n## Workflow prompts\n\nA client shows these as ready-made requests, such as slash commands. Each fills in a step-by-step plan for the model from its arguments.\n");
        foreach (var prompt in prompts.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            text.Append($"\n### `{prompt.Name}`\n\n{Escape(prompt.Description ?? "")}\n\n| Argument | Required | Description |\n|---|---|---|\n");
            foreach (var argument in prompt.ProtocolPrompt.Arguments ?? [])
            {
                text.Append($"| `{argument.Name}` | {(argument.Required == true ? "yes" : "no")} | {Cell(argument.Description ?? "")} |\n");
            }
        }
        return text.ToString();
    }

    private static void Parameters(StringBuilder rows, JsonElement schema, string prefix)
    {
        if (!schema.TryGetProperty("properties", out var properties))
        {
            return;
        }
        var required = schema.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(n => n.GetString()).ToHashSet() : [];
        foreach (var property in properties.EnumerateObject())
        {
            var name = prefix + property.Name;
            var value = property.Value;
            var type = TypeOf(value);
            if (required.Contains(property.Name))
            {
                type += ", required";
            }
            else if (value.TryGetProperty("default", out var fallback) && fallback.ValueKind != JsonValueKind.Null)
            {
                type += $", default `{fallback.GetRawText()}`";
            }
            var description = value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            rows.Append($"| `{name}` | {type} | {Cell(description)} |\n");
            if (value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            {
                Parameters(rows, items, name + "[].");
            }
            else
            {
                Parameters(rows, value, name + ".");
            }
        }
    }

    private static string TypeOf(JsonElement schema)
    {
        var type = !schema.TryGetProperty("type", out var t) ? "any"
            : t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(e => e.GetString()).First(e => e != "null")!
            : t.GetString()!;
        if (type == "array" && schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            var item = TypeOf(items);
            return item == "any" ? "array" : $"array of {item}s";
        }
        if (type == "object" && schema.TryGetProperty("additionalProperties", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            return $"object, name → {TypeOf(values)}";
        }
        return type;
    }

    private static string FirstSentence(string description)
    {
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        while (end > 0 && (description[..end].EndsWith("e.g", StringComparison.Ordinal) || description[..end].EndsWith("i.e", StringComparison.Ordinal)))
        {
            end = description.IndexOf(". ", end + 2, StringComparison.Ordinal);
        }
        return end < 0 ? description : description[..(end + 1)];
    }

    private static string Escape(string text) =>
        text.Replace("\r", "").Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;").Replace("*", "\\*");

    private static string Cell(string text) => Escape(text).Replace("|", "\\|");
}
