// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp;
using ZeyoCut.Mcp.Tools;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Mcp;

public class ClipDescriberTests
{
    // Answers with two good moments, one past the range and one without text.
    private sealed class FakeDescriber : IClipDescriber
    {
        public string Provider => "fake";

        public ClipRequest? Request { get; private set; }

        public Task<IReadOnlyList<ClipMoment>> DescribeAsync(ClipRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            IReadOnlyList<ClipMoment> moments =
            [
                new(request.End, "the dog sits down"),
                new(request.Start + 0.5, "a dog runs in from the left\nand barks"),
                new(request.End + 5, "long after the range"),
                new(request.Start + 1, " "),
            ];
            return Task.FromResult(moments);
        }
    }

    private static Settings Config(string root, string? provider = null, string keyVariable = Settings.DefaultDescriberKeyVariable) =>
        new(null, Path.Combine(root, "projects"), Path.Combine(root, "logs"), LibraryPath: Path.Combine(root, "library"), DescriberProvider: provider, DescriberKeyVariable: keyVariable);

    [Fact]
    public void DescribeClipStaysOffUntilAKnownProviderHasItsKey()
    {
        var root = TestPaths.NewDirectory("describer-config");
        var fake = new FakeDescriber();
        var providers = new Dictionary<string, Func<string, Settings, IClipDescriber>>(StringComparer.OrdinalIgnoreCase) { ["fake"] = (_, _) => fake };
        var environment = new Dictionary<string, string?>();
        string? Env(string name) => environment.GetValueOrDefault(name);

        Assert.Null(ClipDescribers.Create(Config(root), Env, providers, out var none));
        Assert.Equal("describe_clip is off: no clip describer is configured (ZeyoCut:ClipDescriber:Provider in appsettings.json, or ZEYOCUT_DESCRIBER_PROVIDER)", none);
        Assert.Null(ClipDescribers.Create(Config(root, "gemini"), Env, providers, out var unknown));
        Assert.Equal("describe_clip is off: there is no clip describer named 'gemini' in this build (available: fake)", unknown);
        Assert.Null(ClipDescribers.Create(Config(root, "Fake", "MY_KEY"), Env, providers, out var keyless));
        Assert.Equal("describe_clip is off: the Fake clip describer needs its API key in the environment variable MY_KEY", keyless);

        environment["MY_KEY"] = "secret-key";
        Assert.Same(fake, ClipDescribers.Create(Config(root, "Fake", "MY_KEY"), Env, providers, out var on));
        Assert.Equal("describe_clip is on: clip describer fake (API key from MY_KEY)", on);

        Assert.Null(ClipDescribers.Create(Config(root, "nope"), out var builtIn));
        Assert.StartsWith("describe_clip is off: there is no clip describer named 'nope' in this build", builtIn);
    }

    [Fact]
    public void DescribeClipIsOnTheServerOnlyWithADescriber()
    {
        var root = TestPaths.NewDirectory("describer-registration");
        var services = new ServiceCollection();
        services.AddSingleton(Config(root));
        services.AddSingleton<EditorState>();
        var mcp = services.AddMcpServer().WithToolsFromAssembly(typeof(ClipTools).Assembly);
        using (var plain = services.BuildServiceProvider())
        {
            var names = plain.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToList();
            Assert.Contains("export_batch", names);
            Assert.DoesNotContain("describe_clip", names);
        }

        mcp.WithClipDescriber(new FakeDescriber());
        using var configured = services.BuildServiceProvider();
        var tool = Assert.Single(configured.GetServices<McpServerTool>(), t => t.ProtocolTool.Name == "describe_clip");
        var schema = tool.ProtocolTool.InputSchema;
        Assert.Equal(["assetId", "endTime", "layerId", "startTime"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["endTime", "startTime"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
    }

    [Fact]
    public async Task DescribeClipMapsTheTimelineToTheSourceAndBack()
    {
        var root = TestPaths.NewDirectory("describer-tool");
        var directory = Path.Combine(root, "projects", "dogs");
        var project = TestProjects.Empty(width: 1280, height: 720, duration: 10);
        project.Assets.Add(new Asset { Id = "dogs", Type = AssetType.Video, File = "assets/dogs.mp4", Width = 1280, Height = 720, FrameRate = "30", FrameCount = 600, Duration = 20, Codec = "h264" });
        var session = ProjectSession.Create(directory, project);
        File.WriteAllBytes(Path.Combine(directory, "assets", "dogs.mp4"), [0]);
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Asset = "dogs", Id = "dogs", Start = 2, TrimIn = 4, TrimOut = 16, Speed = 2 }));
        using var state = new EditorState(Config(root));
        state.Open(session);
        var fake = new FakeDescriber();

        var started = ClipTools.DescribeClip(state, fake, 3, 5, layerId: "dogs");
        Assert.Equal("describing layer 'dogs' 3–5s with fake in the background as describe_1.\nPoll get_job_status with jobId \"describe_1\".", started);
        var job = state.Jobs.Get("describe_1");
        await job.Completion;
        Assert.Equal(new ClipRequest("dogs", "dogs", Path.Combine(directory, "assets", "dogs.mp4"), 6, 10), fake.Request);
        Assert.Equal(
            "layer 'dogs' 3–5s on the timeline (source 6–10s of 'dogs'), described by fake: 2 moment(s)\n"
            + "- 3.25s (source 6.5s): a dog runs in from the left and barks\n"
            + "- 5s (source 10s): the dog sits down\n"
            + "Warnings:\n- 2 moment(s) outside the range or without text were left out",
            job.Result);

        ClipTools.DescribeClip(state, fake, 0, 2, assetId: "dogs");
        var byAsset = state.Jobs.Get("describe_2");
        await byAsset.Completion;
        Assert.Equal(new ClipRequest("dogs", null, Path.Combine(directory, "assets", "dogs.mp4"), 0, 2), fake.Request);
        Assert.StartsWith("asset 'dogs' 0–2s (source seconds), described by fake: 2 moment(s)\n- 0.5s: a dog runs in", byAsset.Result);

        string Error(Func<string> call) => Assert.Throws<McpException>(call).Message;
        Assert.Contains("give either assetId (source times) or layerId (timeline times), not both", Error(() => ClipTools.DescribeClip(state, fake, 0, 2, assetId: "dogs", layerId: "dogs")));
        Assert.Contains("give either assetId", Error(() => ClipTools.DescribeClip(state, fake, 0, 2)));
        Assert.Contains("layer 'dogs' plays 2–8s on the timeline; give times in that range", Error(() => ClipTools.DescribeClip(state, fake, 1, 4, layerId: "dogs")));
        Assert.Contains("asset 'dogs' is 20s long", Error(() => ClipTools.DescribeClip(state, fake, 15, 25, assetId: "dogs")));
        Assert.Contains("describe at most 60s per call", Error(() => ClipTools.DescribeClip(state, fake, 0, 61, assetId: "dogs")));
        Assert.Contains("the range must have 0 <= startTime < endTime", Error(() => ClipTools.DescribeClip(state, fake, 4, 3, assetId: "dogs")));
        Assert.Equal(2, state.Jobs.All.Count);
    }
}
