// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ZeyoCut.Render;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;
using static ZeyoCut.Tests.Mcp.McpServerTests;

namespace ZeyoCut.Tests.Mcp;

// The workflow prompts, and the first two recipes run step by step on generated clips, as an assistant would.
public class WorkflowPromptTests
{
    private static async Task<string> Prompt(McpClient client, string name, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await client.GetPromptAsync(name, arguments, cancellationToken: ct);
        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);
        return Assert.IsType<TextContentBlock>(message.Content).Text;
    }

    // The recipe names these in this order.
    private static void AssertSteps(string recipe, params string[] steps)
    {
        var at = 0;
        foreach (var step in steps)
        {
            var next = recipe.IndexOf(step, at, StringComparison.Ordinal);
            Assert.True(next >= 0, $"the recipe doesn't name {step} after position {at}:\n{recipe}");
            at = next + step.Length;
        }
    }

    // Polls the job a reply started until it is done.
    private static async Task<string> Wait(McpClient client, string reply, CancellationToken ct, string tool = "get_job_status")
    {
        var job = reply.Split("jobId \"")[1].Split('"')[0];
        var deadline = DateTime.UtcNow.AddSeconds(300);
        string status;
        while ((status = await CallOk(client, tool, new() { ["jobId"] = job }, ct)).Contains("running") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
        }

        Assert.Contains($"{job}: completed", status);
        return status;
    }

    [Fact]
    public async Task ServerListsTheWorkflowPromptsAndFillsThemIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-prompts");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);

        var prompts = await client.ListPromptsAsync(cancellationToken: ct);
        Assert.Equal(["comic_panels", "new_episode", "privacy_blur", "talking_head_cleanup"], prompts.Select(p => p.Name).Order());
        Assert.All(prompts, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.Equal([("video", true), ("name", false), ("language", false)],
            prompts.Single(p => p.Name == "talking_head_cleanup").ProtocolPrompt.Arguments!.Select(a => (a.Name, a.Required == true)));
        Assert.Equal([("folder", true), ("voiceover", false), ("name", false)],
            prompts.Single(p => p.Name == "comic_panels").ProtocolPrompt.Arguments!.Select(a => (a.Name, a.Required == true)));

        var talk = await Prompt(client, "talking_head_cleanup", new() { ["video"] = @"C:\videos\my talk.mp4", ["language"] = "ja" }, ct);
        Assert.StartsWith(@"Clean up the talking-head video C:\videos\my talk.mp4 with the ZeyoCut tools, step by step.", talk);
        Assert.Contains("1. create_project with name \"my talk\", preset youtube_1080p and duration 1;", talk);
        Assert.Contains("analyze_video on that asset with language \"ja\".", talk);
        Assert.Contains("Stop and ask the user", talk);

        var faces = await Prompt(client, "privacy_blur", new() { ["video"] = @"C:\videos\street.mp4", ["target"] = "Faces", ["name"] = "street blur" }, ct);
        Assert.StartsWith(@"Hide the faces in the video C:\videos\street.mp4", faces);
        Assert.Contains("create_project with name \"street blur\"", faces);
        Assert.Contains("detect_and_track on the clip with target faces.", faces);
        Assert.DoesNotContain("target plates", faces);
        Assert.Equal(2, faces.Split("Stop and ask the user").Length - 1);
        var both = await Prompt(client, "privacy_blur", new() { ["video"] = @"C:\videos\street.mp4" }, ct);
        Assert.Contains("with target faces, then again with target plates", both);
        Assert.Contains("unknown target 'cars'; valid values: faces, plates, both",
            (await Assert.ThrowsAsync<McpProtocolException>(async () => await Prompt(client, "privacy_blur", new() { ["video"] = "a.mp4", ["target"] = "cars" }, ct))).Message);

        var blank = await Prompt(client, "new_episode", new() { ["name"] = "tora-ep3" }, ct);
        Assert.Contains("Stop and ask the user which template to start from", blank);
        Assert.Contains("build_from_script builds the whole episode in one call", blank);
        Assert.Contains("3. (Without a script: stop and ask the user which template", await Prompt(client, "new_episode", new() { ["name"] = "tora-ep5", ["script"] = @"C:\scripts\ep5.md" }, ct));
        var scripted = await Prompt(client, "new_episode", new() { ["name"] = "tora-ep4", ["template"] = "tora show", ["script"] = @"""C:\scripts\ep4.md""" }, ct);
        Assert.Contains(@"2. build_from_script with scriptFile ""C:\scripts\ep4.md"" and name ""tora-ep4""", scripted);
        Assert.Contains("3. (Without a script: new_project_from_template with template \"tora show\", name \"tora-ep4\"", scripted);
        AssertSteps(scripted, "list_library", "build_from_script", "add_markers", "add_rig", "apply_action", "add_sfx", "render_contact_sheet", "check_project", "start_export");

        var comic = await Prompt(client, "comic_panels", new() { ["folder"] = @"C:\comics\episode 3\", ["voiceover"] = @"C:\comics\narration.wav" }, ct);
        Assert.StartsWith(@"Turn the comic panels in C:\comics\episode 3 into a video with the ZeyoCut tools, step by step: the camera moves from panel to panel, timed to the voiceover C:\comics\narration.wav, with captions.", comic);
        Assert.Contains("create_project with name \"episode 3\"", comic);
        AssertSteps(comic, "create_project", "import_folder", "import_asset", "add_audio_layer", "analyze_video", "get_transcript", "add_image_layer", "set_keyframes", "add_markers",
            "generate_captions", "render_contact_sheet", "start_export");
        var silent = await Prompt(client, "comic_panels", new() { ["folder"] = @"C:\comics\episode 3" }, ct);
        Assert.Contains("There is no voiceover: stop and ask the user how long each panel should stay.", silent);
        Assert.DoesNotContain("generate_captions", silent);
    }

    [Fact]
    public async Task TheTalkingHeadRecipeCutsCaptionsAndExportsBothShapes()
    {
        var ffmpeg = Ffmpeg.Require();
        var whisper = Speech.RequireWhisper();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-recipe-talk");
        var video = TestVideos.Talking(Path.Combine(root, "talk.mp4"), 1.5, "Hello and welcome to the show.", "Today we cut the pauses.");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct, new() { ["WHISPER_PATH"] = whisper.Cli, ["WHISPER_MODEL"] = whisper.Model });

        var recipe = await Prompt(client, "talking_head_cleanup", new() { ["video"] = video, ["language"] = "en" }, ct);
        AssertSteps(recipe, "create_project", "import_video", "add_video_layer", "analyze_video", "remove_silences", "dryRun=true", "Stop and ask the user", "remove_silences", "punchIn=true",
            "generate_captions", "render_contact_sheet", "audio_levels", "check_project", "frame_diff", "export_batch");

        Assert.Contains("1920x1080 @ 30 fps, 1s", await CallOk(client, "create_project", new() { ["name"] = "talk", ["preset"] = "youtube_1080p", ["duration"] = 1.0 }, ct));
        Assert.StartsWith("imported talk [video]", await CallOk(client, "import_video", new() { ["path"] = video }, ct));
        Assert.Contains("the project was extended from 1s to ", await CallOk(client, "add_video_layer", new() { ["assetId"] = "talk" }, ct));
        var analysis = await Wait(client, await CallOk(client, "analyze_video", new() { ["assetId"] = "talk", ["language"] = "en" }, ct), ct);
        Assert.Matches(@"transcript: \d+ words \(en\)", analysis);

        Assert.StartsWith("dry run, nothing changed.", await CallOk(client, "remove_silences", new() { ["layerId"] = "talk", ["dryRun"] = true }, ct));
        var cut = await CallOk(client, "remove_silences", new() { ["layerId"] = "talk", ["punchIn"] = true }, ct);
        Assert.Contains("pieces: talk, talk_2", cut);
        Assert.Contains("punched in to 1.1x: talk_2", cut);
        // Each spoken line ends in about 0.76 s of quiet, so the cuts are the 2.26 s between the lines and the
        // 1.06 s at the end, less the 0.1 s of padding kept on each side.
        var times = Regex.Match(cut, @"\n2 cut\(s\), [\d.]+s removed; duration ([\d.]+)s → ([\d.]+)s\n");
        Assert.True(times.Success, cut);
        var duration = double.Parse(times.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.InRange(double.Parse(times.Groups[1].Value, CultureInfo.InvariantCulture) - duration, 2.7, 3.3);

        var captions = await CallOk(client, "generate_captions", new() { ["assetId"] = "talk" }, ct);
        Assert.Contains("added captions under 'captions':", captions);
        Assert.Contains("Hello", captions);
        Assert.Contains("pauses.", captions);
        Assert.StartsWith("contact sheet ", Text(await Call(client, "render_contact_sheet", new() { ["count"] = 4, ["maxWidth"] = 400 }, ct)));
        Assert.Contains("no clipping, no too-quiet sections and no silences in the middle", await CallOk(client, "audio_levels", new(), ct));
        Assert.StartsWith("check_project: no problems in 'talk'", await CallOk(client, "check_project", new(), ct));
        var diff = await CallOk(client, "frame_diff", new(), ct);
        if (diff.Contains("jobId \""))
        {
            diff = await Wait(client, diff, ct);
        }

        Assert.Contains("no glitches and no unexplained spikes", diff);

        var batch = await Wait(client, await CallOk(client, "export_batch", new() { ["presets"] = new[] { "youtube_1080p", "shorts_1080p" } }, ct), ct);
        Assert.Contains("exported 2 file(s):", batch);
        foreach (var (preset, width, height) in new[] { ("youtube_1080p", 1920, 1080), ("shorts_1080p", 1080, 1920) })
        {
            var file = Path.Combine(root, "projects", "talk", "exports", $"talk_{preset}.mp4");
            var probed = VideoProbe.Probe(ffmpeg.Ffprobe, file);
            Assert.Equal((width, height), (probed.Width, probed.Height));
            Assert.True(probed.HasAudio, $"{file} has no sound");
            Assert.InRange(probed.Duration, duration - 0.1, duration + 0.1);
        }
    }

    [Fact]
    public async Task ThePrivacyRecipeFindsFacesAndPlatesAndHidesWhatTheUserPicks()
    {
        var ffmpeg = Ffmpeg.Require();
        var models = Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models");
        var store = new ModelStore(models);
        Assert.SkipWhen(!store.Has(AiModels.YuNet) || !store.Has(AiModels.Plates), $"the face and plate models aren't in {models}; skipping the privacy recipe test");
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-recipe-privacy");
        var video = TestVideos.FaceThenVans(Path.Combine(root, "street.mp4"));
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct, new() { ["ZEYOCUT_MODELS_DIR"] = models });

        var recipe = await Prompt(client, "privacy_blur", new() { ["video"] = video }, ct);
        AssertSteps(recipe, "create_project", "import_video", "add_video_layer", "detect_and_track", "target faces", "target plates", "analyze_video", "Stop and ask the user",
            "apply_redaction", "redaction_report", "render_frame debug=true", "Stop and ask the user", "start_export");

        await CallOk(client, "create_project", new() { ["name"] = "street", ["preset"] = "youtube_1080p", ["duration"] = 1.0 }, ct);
        await CallOk(client, "import_video", new() { ["path"] = video }, ct);
        await CallOk(client, "add_video_layer", new() { ["assetId"] = "street" }, ct);
        var faces = await Wait(client, await CallOk(client, "detect_and_track", new() { ["layerId"] = "street", ["target"] = "faces" }, ct), ct);
        Assert.Contains("- face_1: 0–0.958s on 'street'", faces);
        var plates = await Wait(client, await CallOk(client, "detect_and_track", new() { ["layerId"] = "street", ["target"] = "plates" }, ct), ct);
        Assert.Contains("- plate_1: 1–1.958s on 'street'", plates);
        var analysis = await Wait(client, await CallOk(client, "analyze_video", new() { ["assetId"] = "street" }, ct), ct);
        Assert.Contains("scene cuts: 1 (1s)", analysis);

        // The user picks the face and the van's own plate, the largest one.
        var van = plates.Split('\n').Where(l => l.StartsWith("- plate_", StringComparison.Ordinal))
            .OrderByDescending(l => int.Parse(l.Split("average ")[1].Split('x')[0], CultureInfo.InvariantCulture)).First().Split(':')[0][2..];
        var applied = await CallOk(client, "apply_redaction", new() { ["trackIds"] = new[] { "face_1", van } }, ct);
        Assert.Contains($"hid face_1, {van} with 'redaction_1'", applied);

        // The face's box isn't held into the vans' shot: frame 24 is the cut, kept as a margin.
        Assert.Contains("- face_1: frames 0–24 of 'street' covered", applied);
        Assert.Contains("- face_1: the box isn't held across the scene cut that analyze_video found at 1s on 'street'", applied);
        var report = await CallOk(client, "redaction_report", new(), ct);
        Assert.Contains(" hidden; ", report.Split('\n')[0]);
        Assert.Contains("- face_1, hidden by 'redaction_1'", report);
        Assert.Contains($"- {van}, hidden by 'redaction_1'", report);
        Assert.Contains(", not hidden: ", report);
        foreach (var time in new[] { 0.5, 1.5 })
        {
            Assert.StartsWith("frame ", Text(await Call(client, "render_frame", new() { ["time"] = time, ["debug"] = true, ["maxWidth"] = 320 }, ct)));
        }

        Assert.Contains("updated layer 'redaction_1'", await CallOk(client, "update_layer", new() { ["layerId"] = "redaction_1", ["changes"] = new Dictionary<string, object?> { ["paddingPercent"] = 25 } }, ct));
        var export = await Wait(client, await CallOk(client, "start_export", new() { ["fileName"] = "street.mp4", ["preset"] = "youtube_1080p" }, ct), ct, "get_export_status");
        Assert.Contains("resolution: 1920x1080", export);
        Assert.Equal(1920, VideoProbe.Probe(ffmpeg.Ffprobe, Path.Combine(root, "projects", "street", "exports", "street.mp4")).Width);
    }
}
