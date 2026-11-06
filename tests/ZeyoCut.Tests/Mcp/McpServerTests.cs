// SPDX-License-Identifier: MIT

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using ZeyoCut.Mcp;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Mcp;

public class McpServerTests
{
    private static readonly string[] ExpectedTools =
    [
        "create_project", "open_project", "update_project", "get_project", "list_projects", "undo", "redo", "restore_backup",
        "import_asset", "import_folder", "list_assets",
        "add_image_layer", "add_text_layer", "add_audio_layer", "update_layer", "remove_layer", "duplicate_layer", "list_layers",
        "set_keyframes", "remove_keyframes", "clear_keyframes",
        "render_frame", "render_contact_sheet", "preview_server",
        "start_export", "get_export_status", "report_render_stats", "export_language_versions",
        "import_video", "add_video_layer", "get_job_status", "reframe_to_vertical",
        "split_layer", "remove_range", "move_layer", "set_speed", "add_transition",
        "add_markers", "remove_markers", "export_markers", "build_from_script",
        "analyze_video", "get_transcript", "view_source", "remove_silences", "cut_by_transcript",
        "generate_captions", "add_caption_track", "export_srt", "import_srt",
        "save_rig", "add_rig", "save_action", "apply_action", "save_style", "apply_style", "save_template", "new_project_from_template", "list_library",
        "set_camera", "camera_shake",
        "frame_diff", "audio_levels", "check_project",
        "list_sfx", "add_sfx", "detect_beats", "auto_lip_flap",
        "set_effect", "set_mask", "set_motion_path",
        "export_batch", "export_thumbnail", "export_frames",
        "remove_background", "refine_matte", "text_behind_subject", "text_occlusion",
        "system_check", "get_logs",
        "track_object", "reanchor_track", "attach_to_track",
        "apply_redaction", "tracks_to_keyframes", "redaction_report",
        "detect_and_track",
        "prerender_layers", "export_otio", "export_fcpxml",
    ];

    [Fact]
    public async Task ServerRunsEditingWorkflowOverStdio()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-server");
        var projects = Path.Combine(root, "projects");
        var logs = Path.Combine(root, "logs");
        var box = TestImages.Solid(Path.Combine(root, "red box.png"), 40, 30, SKColors.Red);

        await using (var client = await StartServer(root, projects, logs, ct))
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            Assert.Equal(ExpectedTools.Order(), tools.Select(t => t.Name).Order());

            var notOpen = await Call(client, "list_layers", new(), ct);
            Assert.True(notOpen.IsError);
            Assert.Contains("no project is open", Text(notOpen));

            var created = await CallOk(client, "create_project", new()
            {
                ["name"] = "smoke",
                ["width"] = 320,
                ["height"] = 240,
                ["fps"] = 10,
                ["duration"] = 1.0,
                ["backgroundColor"] = "#202020",
            }, ct);
            Assert.Contains("created and opened project 'smoke'", created);
            Assert.True(File.Exists(Path.Combine(projects, "smoke", "project.json")));

            var imported = await CallOk(client, "import_asset", new() { ["path"] = box }, ct);
            Assert.Contains("red_box [image]", imported);
            Assert.Contains("40x30", imported);

            var added = await CallOk(client, "add_image_layer", new() { ["asset"] = "red_box", ["id"] = "box", ["x"] = 160, ["y"] = 120 }, ct);
            Assert.Contains("added image layer 'box'", added);
            Assert.Contains("anchor (20,15)", added);

            var keys = await CallOk(client, "set_keyframes", new()
            {
                ["entries"] = new object[]
                {
                    new Dictionary<string, object?> { ["layerId"] = "box", ["property"] = "x", ["time"] = 0, ["value"] = 40, ["easing"] = "easeInOut" },
                    new Dictionary<string, object?> { ["layerId"] = "box", ["property"] = "x", ["time"] = 0.9, ["value"] = 280 },
                    new Dictionary<string, object?> { ["layerId"] = "box", ["property"] = "rotation", ["time"] = 0, ["value"] = 0 },
                    new Dictionary<string, object?> { ["layerId"] = "box", ["property"] = "rotation", ["time"] = 0.9, ["value"] = 90 },
                },
            }, ct);
            Assert.Contains("set 4 keyframe(s) (4 added, 0 replaced)", keys);

            var badKeys = await Call(client, "set_keyframes", new()
            {
                ["entries"] = new object[]
                {
                    new Dictionary<string, object?> { ["layerId"] = "nope", ["property"] = "x", ["time"] = 0, ["value"] = 1 },
                },
            }, ct);
            Assert.True(badKeys.IsError);
            Assert.Contains("layer 'nope' not found; existing layers: box", Text(badKeys));

            var badField = await Call(client, "update_layer", new() { ["layerId"] = "box", ["changes"] = new Dictionary<string, object?> { ["colour"] = "#fff" } }, ct);
            Assert.True(badField.IsError);
            Assert.Contains("unknown field 'colour'", Text(badField));

            var frame = await Call(client, "render_frame", new() { ["time"] = 0.5, ["maxWidth"] = 160 }, ct);
            Assert.False(frame.IsError == true, Text(frame));
            Assert.Contains("frame 5 of 10 at t=0.500s", Text(frame));
            using (var preview = DecodeImage(frame))
            {
                Assert.Equal(160, preview.Width);
                Assert.Equal(120, preview.Height);
            }

            Assert.True(File.Exists(Path.Combine(projects, "smoke", "renders", "frame_00005.png")));

            var sheet = await Call(client, "render_contact_sheet", new() { ["count"] = 4, ["columns"] = 2, ["maxWidth"] = 400, ["debug"] = true }, ct);
            Assert.False(sheet.IsError == true, Text(sheet));
            Assert.Contains("f0=0.000s, f3=0.300s, f6=0.600s, f9=0.900s", Text(sheet));
            using (var grid = DecodeImage(sheet))
            {
                Assert.Equal(400, grid.Width);
            }

            var tree = await CallOk(client, "list_layers", new(), ct);
            Assert.Contains("box [image]", tree);
            Assert.Contains("x(2)", tree);

            var nothing = await Call(client, "clear_keyframes", new() { ["layerId"] = "box", ["property"] = "opacity" }, ct);
            Assert.True(nothing.IsError);
            Assert.Contains("box.opacity has no keyframes", Text(nothing));

            var undone = await CallOk(client, "undo", new(), ct);
            Assert.Contains("undid: set_keyframes (4 entries)", undone);
            var json = await CallOk(client, "get_project", new(), ct);
            using (var document = JsonDocument.Parse(json))
            {
                var layer = document.RootElement.GetProperty("layers")[0];
                Assert.False(layer.GetProperty("x").TryGetProperty("keyframes", out _));
            }

            Assert.Contains("redid: set_keyframes (4 entries)", await CallOk(client, "redo", new(), ct));

            var listed = await CallOk(client, "list_projects", new(), ct);
            Assert.Contains("- smoke: \"smoke\" 320x240 @ 10 fps, 1s, 1 layer(s)", listed);
            Assert.Contains("[open]", listed);

            var updated = await CallOk(client, "update_project", new() { ["duration"] = 2.5, ["fps"] = 12 }, ct);
            Assert.Contains("updated project 'smoke': fps 10 → 12; duration 1s → 2.5s", updated);
            Assert.Contains("320x240 @ 12 fps, 2.5s (30 frames), background #202020", updated);

            var oddWidth = await Call(client, "update_project", new() { ["width"] = 321 }, ct);
            Assert.True(oddWidth.IsError);
            Assert.Contains("width must be an even number", Text(oddWidth));

            var noArguments = await Call(client, "update_project", new(), ct);
            Assert.True(noArguments.IsError);
            Assert.Contains("nothing to change", Text(noArguments));
            Assert.Contains("undid: update_project", await CallOk(client, "undo", new(), ct));
        }

        var logFile = Assert.Single(Directory.GetFiles(logs, "zeyocut-*.jsonl"));
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        Assert.Contains("ZeyoCut MCP server", reader.ReadToEnd());
    }

    [Fact]
    public async Task ServerLogsEveryToolCallAsJsonLinesAndGetLogsReadsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-logs");
        var logs = Path.Combine(root, "logs");

        await using (var client = await StartServer(root, Path.Combine(root, "projects"), logs, ct))
        {
            Assert.Equal($"no log entries containing \"create_project\" in {logs}", await CallOk(client, "get_logs", new() { ["contains"] = "create_project" }, ct));
            await CallOk(client, "create_project", new() { ["name"] = "logged", ["width"] = 320, ["height"] = 240, ["fps"] = 10, ["duration"] = 1.0 }, ct);
            var typo = await Call(client, "update_layer", new() { ["layerId"] = "ghost", ["changes"] = new Dictionary<string, object?> { ["x"] = 5 } }, ct);
            Assert.True(typo.IsError);
            Assert.StartsWith("An error occurred invoking 'update_layer': layer 'ghost' not found", Text(typo));

            var all = await CallOk(client, "get_logs", new(), ct);
            Assert.StartsWith("all ", all);
            Assert.Contains($" log entries in {logs}, oldest first:\n", all);
            Assert.Matches(@"\n\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[information\] ZeyoCut: ZeyoCut MCP server .* starting", all);
            Assert.Matches(@"\[information\] ZeyoCut\.Tools: create_project took \d+ ms; arguments \{""name"":""logged"",""width"":320,""height"":240,""fps"":10,""duration"":1(\.0)?\}", all);
            Assert.Matches(@"\[warning\] ZeyoCut\.Tools: update_layer failed after \d+ ms: .*'ghost'.*; arguments \{""layerId"":""ghost"",""changes"":\{""x"":5\}\}", all);
            Assert.True(all.IndexOf("create_project took", StringComparison.Ordinal) < all.IndexOf("update_layer failed", StringComparison.Ordinal));

            // A tool's error is only a warning: the SDK doesn't log it again as an error. An error from an
            // earlier day shows the first lines of its exception.
            var earlier = Path.Combine(logs, LogFiles.FileName(new DateTime(2000, 1, 1)));
            var exception = "System.InvalidOperationException: boom\n   at A()\n   at B()\n   at C()\n   at D()\n   at E()\n   at F()\n   at G()";
            File.WriteAllText(earlier, JsonSerializer.Serialize(new LogEntry(new DateTimeOffset(2000, 1, 1, 9, 0, 0, TimeSpan.Zero), "error", "ZeyoCut.Tools", "render_frame failed", Exception: exception), LogFiles.JsonOptions) + "\n");
            var warnings = await CallOk(client, "get_logs", new() { ["level"] = "Warning" }, ct);
            File.Delete(earlier);
            Assert.StartsWith($"all 2 log entries at warning or above in {logs}, oldest first:\n2000-01-01 09:00:00.000 [error] ZeyoCut.Tools: render_frame failed\n  System.InvalidOperationException: boom\n     at A()", warnings);
            Assert.Contains("\n     at E()\n  … 2 more lines\n", warnings);
            Assert.Contains("[warning] ZeyoCut.Tools: update_layer failed", warnings);
            Assert.DoesNotContain("threw an unhandled exception", warnings);
            Assert.DoesNotContain("create_project took", warnings);

            var last = await CallOk(client, "get_logs", new() { ["lastN"] = 1, ["contains"] = "CREATE_PROJECT" }, ct);
            Assert.StartsWith($"the last log entry containing \"CREATE_PROJECT\" in {logs}, oldest first:\n", last);
            var lastLines = last.Split('\n');
            Assert.Equal(2, lastLines.Length);
            Assert.Contains("create_project", lastLines[1], StringComparison.Ordinal);

            Assert.EndsWith("unknown level 'loud'; use trace, debug, information, warning, error, critical", Text(await Call(client, "get_logs", new() { ["level"] = "loud" }, ct)));
            Assert.EndsWith("lastN must be between 1 and 1000 (got 0)", Text(await Call(client, "get_logs", new() { ["lastN"] = 0 }, ct)));
        }

        var logFile = Assert.Single(Directory.GetFiles(logs, "zeyocut-*.jsonl"));
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var entries = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement).ToList();
        Assert.All(entries, e =>
        {
            Assert.True(DateTimeOffset.TryParse(e.GetProperty("time").GetString(), out _));
            Assert.Contains(e.GetProperty("level").GetString(), new[] { "trace", "debug", "information", "warning", "error", "critical" });
            Assert.False(string.IsNullOrEmpty(e.GetProperty("category").GetString()));
            Assert.False(string.IsNullOrEmpty(e.GetProperty("message").GetString()));
        });

        static bool ToolCall(JsonElement e, string tool) => e.TryGetProperty("fields", out var f) && f.TryGetProperty("Tool", out var t) && t.GetString() == tool;
        var created = Assert.Single(entries, e => ToolCall(e, "create_project"));
        Assert.Equal("ZeyoCut.Tools", created.GetProperty("category").GetString());
        Assert.Equal("information", created.GetProperty("level").GetString());
        var fields = created.GetProperty("fields");
        Assert.Equal(JsonValueKind.Number, fields.GetProperty("Milliseconds").ValueKind);
        Assert.Equal("logged", JsonDocument.Parse(fields.GetProperty("Arguments").GetString()!).RootElement.GetProperty("name").GetString());

        var failed = Assert.Single(entries, e => ToolCall(e, "update_layer"));
        Assert.Equal("warning", failed.GetProperty("level").GetString());
        Assert.Contains("ghost", failed.GetProperty("fields").GetProperty("Error").GetString());
        Assert.False(failed.TryGetProperty("exception", out _));
        Assert.DoesNotContain(entries, e => e.GetProperty("level").GetString() == "error");
    }

    [Fact]
    public async Task ServerExportsInBackground()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-export");
        var projects = Path.Combine(root, "projects");
        var box = TestImages.Solid(Path.Combine(root, "box.png"), 40, 40, SKColors.Blue);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "export", ["width"] = 160, ["height"] = 120, ["fps"] = 10, ["duration"] = 1.0 }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = box }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "box" }, ct);

        var badName = await Call(client, "start_export", new() { ["fileName"] = "..\\out.mp4" }, ct);
        Assert.True(badName.IsError);

        var started = await CallOk(client, "start_export", new() { ["fileName"] = "smoke" }, ct);
        Assert.Contains("started export_1", started);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        do
        {
            await Task.Delay(200, ct);
            status = await CallOk(client, "get_export_status", new() { ["jobId"] = "export_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("export_1: completed", status);
        Assert.Contains("resolution: 160x120", status);
        Assert.Contains("video: h264 yuv420p, 10 frames", status);
        var output = Path.Combine(projects, "export", "exports", "smoke.mp4");
        Assert.Contains($"output: {output}", status);
        Assert.True(File.Exists(output));

        var missing = await Call(client, "get_export_status", new() { ["jobId"] = "export_9" }, ct);
        Assert.True(missing.IsError);
        Assert.Contains("export_1", Text(missing));
    }

    [Fact]
    public async Task ServerImportsAndRendersVideo()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-video");
        var projects = Path.Combine(root, "projects");
        var clip = TestVideos.Pattern(Path.Combine(root, "street clip.mp4"), seconds: 2, width: 160, height: 120, withAudio: true);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "video", ["width"] = 320, ["height"] = 240, ["fps"] = 30, ["duration"] = 1.0 }, ct);

        var wrongTool = await Call(client, "import_asset", new() { ["path"] = clip }, ct);
        Assert.True(wrongTool.IsError);
        Assert.Contains("use import_video", Text(wrongTool));

        var imported = await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
        Assert.Contains("street_clip [video]", imported);
        Assert.Contains("160x120 @ 30 fps, 2s, 60 frames, h264, with sound", imported);

        var added = await CallOk(client, "add_video_layer", new() { ["assetId"] = "street_clip", ["trimIn"] = 0.5, ["speed"] = 1.5 }, ct);
        Assert.Contains("source 0.5–2s at 1.5x → timeline 0–1s, scale 2", added);
        Assert.DoesNotContain("extended", added);

        var longer = await CallOk(client, "add_video_layer", new() { ["assetId"] = "street_clip", ["id"] = "second", ["timelineStart"] = 1, ["scaleX"] = 0.5, ["scaleY"] = 0.5 }, ct);
        Assert.Contains("the project was extended from 1s to 3s", longer);

        var badSpeed = await Call(client, "add_video_layer", new() { ["assetId"] = "street_clip", ["speed"] = 20 }, ct);
        Assert.True(badSpeed.IsError);
        Assert.Contains("speed must be between 0.1 and 10", Text(badSpeed));

        var tree = await CallOk(client, "list_layers", new(), ct);
        Assert.Contains("street_clip [video]", tree);
        Assert.Contains("t=1–3", tree);

        var frame = await Call(client, "render_frame", new() { ["time"] = 0.5, ["maxWidth"] = 320 }, ct);
        Assert.False(frame.IsError == true, Text(frame));
        using (var image = DecodeImage(frame))
        {
            Assert.Equal(320, image.Width);
            Assert.NotEqual(SKColors.White, TestImages.Pixel(image, 160, 120));
        }

        var noEnd = await Call(client, "update_layer", new() { ["layerId"] = "second", ["changes"] = new Dictionary<string, object?> { ["end"] = 2 } }, ct);
        Assert.True(noEnd.IsError);
        Assert.Contains("unknown field 'end'", Text(noEnd));

        var split = await CallOk(client, "split_layer", new() { ["layerId"] = "second", ["time"] = 2 }, ct);
        Assert.Contains("split 'second' at 2s into 'second' and 'second_2'", split);

        var cut = await CallOk(client, "remove_range", new() { ["layerId"] = "all", ["start"] = 1.5, ["end"] = 2 }, ct);
        Assert.Contains("removed 0.5s (1.5–2s); duration 3s → 2.5s", cut);

        var crossfade = await CallOk(client, "add_transition", new() { ["layerA"] = "second", ["layerB"] = "second_2", ["duration"] = 0.2 }, ct);
        Assert.Contains("crossfade 'second' → 'second_2'", crossfade);

        var badType = await Call(client, "add_transition", new() { ["layerA"] = "second", ["layerB"] = "second_2", ["type"] = "spin" }, ct);
        Assert.True(badType.IsError);
        Assert.Contains("unknown transition 'spin'; valid values: crossfade, fadeToBlack, wipe, slide, zoom, whipPan", Text(badType));

        var faster = await CallOk(client, "set_speed", new() { ["layerId"] = "second_2", ["speed"] = 2 }, ct);
        Assert.Contains("'second_2' now plays at 2x (was 1x)", faster);

        var moved = await CallOk(client, "move_layer", new() { ["layerId"] = "street_clip", ["start"] = 0.25 }, ct);
        Assert.Contains("moved 'street_clip' by +0.25s: now 0.25–1.25s", moved);

        await CallOk(client, "undo", new(), ct);
        await CallOk(client, "undo", new(), ct);
        await CallOk(client, "undo", new(), ct);
        await CallOk(client, "undo", new(), ct);
        Assert.Contains("\"duration\": 3,", await CallOk(client, "get_project", new(), ct));
    }

    [Fact]
    public async Task ServerAnalyzesAndRemovesSilences()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-silences");
        var projects = Path.Combine(root, "projects");
        var clip = TestVideos.ToneGapTone(Path.Combine(root, "talk.mp4"));
        var noWhisper = Path.Combine(root, "no-whisper");

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct, new() { ["WHISPER_PATH"] = noWhisper });
        await CallOk(client, "create_project", new() { ["name"] = "silences", ["width"] = 160, ["height"] = 120, ["fps"] = 30, ["duration"] = 4.0 }, ct);
        await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
        await CallOk(client, "add_video_layer", new() { ["assetId"] = "talk" }, ct);

        var started = await CallOk(client, "analyze_video", new() { ["assetId"] = "talk" }, ct);
        Assert.Contains("analyzing 'talk' in the background as analyze_1: scene cuts, noise floor and silences, no transcript (whisper path", started);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            await Task.Delay(200, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "analyze_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("analyze_1: completed", status);
        Assert.Contains("scene cuts: 1 (2s)", status);
        Assert.Contains("silences ≥ 0.3s below", status);
        Assert.Contains("note: transcript skipped", status);

        var noTranscript = await Call(client, "get_transcript", new() { ["assetId"] = "talk" }, ct);
        Assert.True(noTranscript.IsError);
        Assert.Contains("asset 'talk' has no transcript (transcript skipped", Text(noTranscript));

        var dry = await CallOk(client, "remove_silences", new() { ["layerId"] = "talk", ["dryRun"] = true }, ct);
        Assert.Contains("dry run, nothing changed. noise floor", dry);
        Assert.Contains("1 cut(s), 1.8s removed; duration 4s → 2.2s", dry);
        Assert.Contains("\"duration\": 4,", await CallOk(client, "get_project", new(), ct));

        var byWords = await Call(client, "remove_silences", new() { ["layerId"] = "talk", ["mode"] = "transcript" }, ct);
        Assert.True(byWords.IsError);
        Assert.Contains("has no transcript", Text(byWords));

        var cut = await CallOk(client, "remove_silences", new() { ["layerId"] = "talk", ["punchIn"] = true }, ct);
        Assert.Contains("1 cut(s), 1.8s removed; duration 4s → 2.2s", cut);
        Assert.Contains("pieces: talk, talk_2", cut);
        Assert.Contains("punched in to 1.1x: talk_2", cut);
        Assert.Contains("talk_2 [video]", await CallOk(client, "list_layers", new(), ct));

        var sheet = await Call(client, "view_source", new() { ["assetId"] = "talk", ["count"] = 4, ["maxWidth"] = 800 }, ct);
        Assert.False(sheet.IsError == true, Text(sheet));
        Assert.Contains("source frames of 'talk'", Text(sheet));
        Assert.Contains("f0=0.000s", Text(sheet));
        using (var image = DecodeImage(sheet))
        {
            Assert.True(image.Width > 0);
        }

        await CallOk(client, "undo", new(), ct);
        Assert.Contains("\"duration\": 4,", await CallOk(client, "get_project", new(), ct));
    }

    [Fact]
    public async Task ServerTranscribesAndCutsWords()
    {
        Ffmpeg.Require();
        var whisper = Speech.RequireWhisper();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-transcript");
        var projects = Path.Combine(root, "projects");
        var speech = Speech.Speak("Hello world. This is a test of the editor.", Path.Combine(root, "speech.wav"));

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct, new() { ["WHISPER_PATH"] = whisper.Cli, ["WHISPER_MODEL"] = whisper.Model });
        await CallOk(client, "create_project", new() { ["name"] = "words", ["width"] = 160, ["height"] = 120, ["fps"] = 30, ["duration"] = 5.0 }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = speech }, ct);
        await CallOk(client, "add_audio_layer", new() { ["asset"] = "speech", ["id"] = "voice" }, ct);

        var started = await CallOk(client, "analyze_video", new() { ["assetId"] = "speech", ["language"] = "en" }, ct);
        Assert.Contains("transcript with whisper (ggml-", started);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        do
        {
            await Task.Delay(250, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "analyze_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("analyze_1: completed", status);
        Assert.Contains("transcript: 9 words (en)", status);

        var transcript = await CallOk(client, "get_transcript", new() { ["assetId"] = "speech" }, ct);
        Assert.Contains("transcript of 'speech' (en, 9 words)", transcript);
        Assert.Contains("[0] Hello", transcript);
        Assert.Contains("[8] editor.", transcript);
        Assert.Contains("(pause", transcript);

        var later = await CallOk(client, "get_transcript", new() { ["assetId"] = "speech", ["startTime"] = 1.5 }, ct);
        Assert.DoesNotContain("[0] Hello", later);
        Assert.Contains("[2] This", later);

        var bad = await Call(client, "cut_by_transcript", new() { ["layerId"] = "voice", ["wordRanges"] = new[] { new Dictionary<string, object?> { ["from"] = 7, ["to"] = 20 } } }, ct);
        Assert.True(bad.IsError);
        Assert.Contains("word range 7–20 is not valid; the transcript has words 0–8", Text(bad));

        var cut = await CallOk(client, "cut_by_transcript", new() { ["layerId"] = "voice", ["wordRanges"] = new[] { new Dictionary<string, object?> { ["from"] = 1, ["to"] = 1 } } }, ct);
        Assert.Contains("removed: world.", cut);
        Assert.Contains("1 cut(s)", cut);
        Assert.Contains("duration 5s →", cut);
        Assert.Contains("pieces: voice, voice_2", cut);

        var captions = await CallOk(client, "generate_captions", new() { ["assetId"] = "speech", ["style"] = "bold_center" }, ct);
        Assert.Contains("added captions under 'captions':", captions);
        Assert.Contains("\"Hello", captions);
        Assert.DoesNotContain("world", captions);
        Assert.Contains("captions_1 [text]", await CallOk(client, "list_layers", new(), ct));

        var frame = await Call(client, "render_frame", new() { ["time"] = 0.4, ["maxWidth"] = 160 }, ct);
        Assert.False(frame.IsError == true, Text(frame));

        var srt = await CallOk(client, "export_srt", new() { ["fileName"] = "speech" }, ct);
        Assert.Contains("subtitle(s) to ", srt);
        var srtFile = Path.Combine(projects, "words", "exports", "speech.srt");
        Assert.StartsWith("1\r\n00:00:00,", File.ReadAllText(srtFile));
        Assert.Contains("Hello", File.ReadAllText(srtFile));

        var wrongExtension = await Call(client, "export_srt", new() { ["fileName"] = "speech.txt" }, ct);
        Assert.True(wrongExtension.IsError);
        Assert.Contains("export_srt writes .srt files", Text(wrongExtension));

        var again = await CallOk(client, "generate_captions", new() { ["layerId"] = "voice_2", ["style"] = "minimal" }, ct);
        Assert.Contains("replaced captions under 'captions':", again);
        Assert.DoesNotContain("Hello", again);
    }

    [Fact]
    public async Task ServerNormalizesRotatedVideoInBackground()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-normalize");
        var projects = Path.Combine(root, "projects");
        var rotated = TestVideos.Rotated(TestVideos.Halves(Path.Combine(root, "halves.mp4")), Path.Combine(root, "phone.mp4"), 90);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "normalize", ["width"] = 48, ["height"] = 64, ["fps"] = 30, ["duration"] = 1.0 }, ct);
        Assert.Equal("(no jobs yet)", await CallOk(client, "get_job_status", new(), ct));

        var started = await CallOk(client, "import_video", new() { ["path"] = rotated }, ct);
        Assert.Contains("'phone.mp4' needs normalizing: rotated", started);
        Assert.Contains("Transcoding in the background as import_1", started);
        Assert.Contains("constant 30 fps", started);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        do
        {
            await Task.Delay(200, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "import_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("import_1: completed", status);
        Assert.Contains("imported phone [video] assets/phone.mp4 (48x64 @ 30 fps, 1s, 30 frames, h264, no sound; normalized (rotated", status);
        Assert.Contains($"normalized from {rotated}", status);
        Assert.Contains("- import_1: completed", await CallOk(client, "get_job_status", new(), ct));
        Assert.Contains("phone [video]", await CallOk(client, "list_assets", new(), ct));

        var added = await CallOk(client, "add_video_layer", new() { ["assetId"] = "phone" }, ct);
        Assert.Contains("scale 1", added);

        var missing = await Call(client, "get_job_status", new() { ["jobId"] = "import_9" }, ct);
        Assert.True(missing.IsError);
        Assert.Contains("job 'import_9' not found; jobs: import_1", Text(missing));

        Assert.Contains("undid: add_video_layer", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: import_video phone.mp4", await CallOk(client, "undo", new(), ct));
        Assert.Contains("(no assets", await CallOk(client, "list_assets", new(), ct));
    }

    [Fact]
    public async Task ServerAppliesDeliveryPresets()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-delivery");
        var projects = Path.Combine(root, "projects");
        var clip = TestVideos.Pattern(Path.Combine(root, "wide.mp4"), seconds: 1, width: 320, height: 180, withAudio: true);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        var badPreset = await Call(client, "create_project", new() { ["name"] = "nope", ["preset"] = "tiktok_4k" }, ct);
        Assert.True(badPreset.IsError);
        Assert.Contains("unknown preset 'tiktok_4k'; presets: youtube_1080p, shorts_1080p", Text(badPreset));

        var created = await CallOk(client, "create_project", new() { ["name"] = "delivery", ["preset"] = "youtube_1080p", ["duration"] = 1.0 }, ct);
        Assert.Contains("(preset youtube_1080p: YouTube 1920x1080 landscape, 30 fps)", created);
        Assert.Contains("1920x1080 @ 30 fps, 1s (30 frames)", created);
        Assert.Contains("audio: loudness -14 LUFS, music ducking on", created);

        await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
        await CallOk(client, "add_video_layer", new() { ["assetId"] = "wide", ["role"] = "voice" }, ct);
        var badRole = await Call(client, "add_video_layer", new() { ["assetId"] = "wide", ["role"] = "singer" }, ct);
        Assert.True(badRole.IsError);

        var tooEarly = await Call(client, "reframe_to_vertical", new(), ct);
        Assert.True(tooEarly.IsError);
        Assert.Contains("switch it first (update_project preset=shorts_1080p)", Text(tooEarly));

        var vertical = await CallOk(client, "update_project", new() { ["preset"] = "shorts_1080p", ["loudnessTarget"] = -16, ["ducking"] = false }, ct);
        Assert.Contains("updated project 'delivery': width 1920 → 1080; height 1080 → 1920; loudness -14 LUFS → -16 LUFS; music ducking off", vertical);
        Assert.Contains("reframe_to_vertical crops and fits video layers", vertical);

        var wrongShape = await Call(client, "start_export", new() { ["fileName"] = "wrong.mp4", ["preset"] = "youtube_1080p" }, ct);
        Assert.True(wrongShape.IsError);
        Assert.Contains("the project is 1080x1920 but youtube_1080p is 1920x1080 (landscape)", Text(wrongShape));

        var reframed = await CallOk(client, "reframe_to_vertical", new() { ["focusX"] = 0.5 }, ct);
        Assert.Contains("reframed 1 video layer(s):\n- wide: crop 102x180 at x=109 y=0, scale 10.667", reframed);

        var started = await CallOk(client, "start_export", new() { ["fileName"] = "short.mp4", ["preset"] = "shorts_1080p" }, ct);
        Assert.Contains("(1080x1920, preset shorts_1080p @ 30 fps)", started);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        do
        {
            await Task.Delay(250, ct);
            status = await CallOk(client, "get_export_status", new() { ["jobId"] = "export_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("export_1: completed", status);
        Assert.Contains("resolution: 1080x1920", status);
        Assert.Contains("processing: preset shorts_1080p: 1080x1920, H.264 high profile, CRF 18, AAC 192k", status);
        Assert.Contains("processing: loudness: ", status);
        Assert.Contains("→ -16 LUFS", status);
    }

    [Fact]
    public async Task ServerSharesRigsActionsStylesAndTemplatesThroughTheLibrary()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-library");
        var projects = Path.Combine(root, "projects");
        var body = TestImages.Solid(Path.Combine(root, "body.png"), 60, 40, SKColors.Orange);
        var head = TestImages.Solid(Path.Combine(root, "head.png"), 30, 30, SKColors.Gold);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        Assert.Contains("rigs (0): none yet (save_rig)", await CallOk(client, "list_library", new(), ct));
        await CallOk(client, "create_project", new() { ["name"] = "studio", ["width"] = 320, ["height"] = 240, ["fps"] = 10, ["duration"] = 2.0 }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = body }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = head }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "body", ["id"] = "cat_body", ["x"] = 100, ["y"] = 120 }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "head", ["id"] = "cat_head", ["parentId"] = "cat_body", ["x"] = 30, ["y"] = 0 }, ct);
        var badRole = await Call(client, "update_layer", new() { ["layerId"] = "cat_head", ["changes"] = new Dictionary<string, object?> { ["rigRole"] = "root" } }, ct);
        Assert.True(badRole.IsError);
        Assert.Contains("rigRole 'root' is invalid", Text(badRole));
        await CallOk(client, "update_layer", new() { ["layerId"] = "cat_head", ["changes"] = new Dictionary<string, object?> { ["rigRole"] = "noggin" } }, ct);
        await CallOk(client, "set_keyframes", new()
        {
            ["entries"] = new object[]
            {
                new Dictionary<string, object?> { ["layerId"] = "cat_body", ["property"] = "x", ["time"] = 0, ["value"] = 100 },
                new Dictionary<string, object?> { ["layerId"] = "cat_body", ["property"] = "x", ["time"] = 1, ["value"] = 160 },
                new Dictionary<string, object?> { ["layerId"] = "cat_head", ["property"] = "rotation", ["time"] = 0, ["value"] = 0 },
                new Dictionary<string, object?> { ["layerId"] = "cat_head", ["property"] = "rotation", ["time"] = 0.5, ["value"] = 30 },
                new Dictionary<string, object?> { ["layerId"] = "cat_head", ["property"] = "rotation", ["time"] = 1, ["value"] = 0 },
            },
        }, ct);

        var rig = await CallOk(client, "save_rig", new() { ["rootLayerId"] = "cat_body", ["name"] = "Cat" }, ct);
        Assert.Contains("saved rig 'Cat' to " + Path.Combine(root, "library", "rigs", "Cat"), rig);
        Assert.Contains("2 part(s): body, noggin; 2 drawing(s), 2 asset file(s)\nroles: root, noggin", rig);
        Assert.Contains("already has a rig named 'cat'", Text(await Call(client, "save_rig", new() { ["rootLayerId"] = "cat_body", ["name"] = "cat" }, ct)));
        var action = await CallOk(client, "save_action", new() { ["rigRootId"] = "cat_body", ["startTime"] = 0, ["endTime"] = 1, ["name"] = "scoot" }, ct);
        Assert.Contains("saved action 'scoot': 1s, parts root, noggin (2 track(s): root.x, noggin.rotation)", action);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Title", ["id"] = "title", ["fontSize"] = 24, ["color"] = "#FFD700", ["bold"] = true, ["end"] = 2.0 }, ct);
        Assert.Contains("saved style 'Gold': Segoe UI bold 24px #FFD700", await CallOk(client, "save_style", new() { ["layerId"] = "title", ["name"] = "Gold" }, ct));
        var template = await CallOk(client, "save_template", new() { ["name"] = "show", ["layerIds"] = new[] { "title" } }, ct);
        Assert.Contains("saved template 'show': 320x240 @ 10 fps, 2s, 1 layer(s), 0 asset(s)", template);

        var created = await CallOk(client, "new_project_from_template", new() { ["template"] = "show", ["name"] = "episode", ["duration"] = 3.0 }, ct);
        Assert.Contains("created and opened project 'episode' from template 'show'", created);
        Assert.Contains("320x240 @ 10 fps, 3s", created);
        Assert.True(File.Exists(Path.Combine(projects, "episode", "project.json")));
        var placed = await CallOk(client, "add_rig", new() { ["name"] = "cat", ["x"] = 200, ["y"] = 150 }, ct);
        Assert.Contains("added rig 'cat' as 'cat_body' with 2 layer(s): cat_body, cat_noggin\nroles: root=cat_body, noggin=cat_noggin\nimported 2 drawing file(s)", placed);
        var second = await CallOk(client, "add_rig", new() { ["name"] = "Cat", ["scale"] = 0.5 }, ct);
        Assert.Contains("as 'cat_2_body'", second);
        Assert.Contains("reused 2 asset(s) already in the project", second);

        var applied = await CallOk(client, "apply_action", new() { ["name"] = "scoot", ["rigRootId"] = "cat_body", ["startTime"] = 1.0, ["loops"] = 3 }, ct);
        Assert.Contains("applied action 'scoot' to 'cat_body' from 1s to 4s (3 loops): 11 keyframe(s) on 2 track(s)", applied);
        Assert.Contains("past the project's end (3s)", applied);
        using (var document = JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)))
        {
            var catBody = document.RootElement.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("id").GetString() == "cat_body");
            Assert.Equal([200.0, 260.0, 320.0, 380.0], catBody.GetProperty("x").GetProperty("keyframes").EnumerateArray().Select(k => k.GetProperty("value").GetDouble()));
        }

        Assert.Contains("undid: apply_action scoot on cat_body", await CallOk(client, "undo", new(), ct));
        var missing = await Call(client, "apply_action", new() { ["name"] = "skip", ["rigRootId"] = "cat_body", ["startTime"] = 0 }, ct);
        Assert.Contains("no action named 'skip' in the library; actions: scoot", Text(missing));

        var styled = await CallOk(client, "add_text_layer", new() { ["text"] = "Hey", ["id"] = "hey", ["style"] = "gold", ["color"] = "#FFFFFF" }, ct);
        Assert.Contains("\"fontSize\":24,", styled);
        Assert.Contains("\"bold\":true", styled);
        Assert.Contains("\"color\":\"#FFFFFF\"", styled);
        Assert.Contains("applied style 'Gold' to 1 text layer(s): title", await CallOk(client, "apply_style", new() { ["name"] = "Gold", ["layerId"] = "title" }, ct));
        var notText = await Call(client, "apply_style", new() { ["name"] = "Gold", ["layerId"] = "cat_noggin" }, ct);
        Assert.Contains("no text layers under it", Text(notText));

        var listed = await CallOk(client, "list_library", new(), ct);
        Assert.Contains("library: " + Path.Combine(root, "library"), listed);
        Assert.Contains("rigs (1):\n- Cat: 2 part(s): body, noggin; 2 drawing(s)", listed);
        Assert.Contains("actions (1):\n- scoot: 1s", listed);
        Assert.Contains("styles (1):\n- Gold: Segoe UI bold 24px #FFD700", listed);
        Assert.Contains("templates (1):\n- show: 320x240", listed);
        Assert.DoesNotContain("rigs", await CallOk(client, "list_library", new() { ["type"] = "styles" }, ct));
        Assert.Contains("unknown library type 'fonts'", Text(await Call(client, "list_library", new() { ["type"] = "fonts" }, ct)));
    }

    [Fact]
    public async Task ServerMovesAndShakesTheCamera()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-camera");
        var box = TestImages.Solid(Path.Combine(root, "box.png"), 40, 40, SKColors.Red);

        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "cam", ["width"] = 320, ["height"] = 240, ["fps"] = 10, ["duration"] = 2.0 }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = box }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "box", ["id"] = "box" }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Title", ["id"] = "title", ["y"] = 30 }, ct);
        await CallOk(client, "update_layer", new() { ["layerId"] = "title", ["changes"] = new Dictionary<string, object?> { ["ignoreCamera"] = true } }, ct);

        var set = await CallOk(client, "set_camera", new() { ["zoom"] = 2 }, ct);
        Assert.Contains("camera: zoom 1 → 2\ncamera: x=160 y=120 zoom=2 rotation=0", set);
        await CallOk(client, "set_keyframes", new()
        {
            ["entries"] = new object[]
            {
                new Dictionary<string, object?> { ["layerId"] = "camera", ["property"] = "x", ["time"] = 0, ["value"] = 100, ["easing"] = "easeInOut" },
                new Dictionary<string, object?> { ["layerId"] = "camera", ["property"] = "x", ["time"] = 1, ["value"] = 220 },
            },
        }, ct);
        var warned = await CallOk(client, "set_camera", new() { ["x"] = 50 }, ct);
        Assert.Contains("camera x has 2 keyframe(s), so the value you set is only used after clear_keyframes", warned);
        var badProperty = await Call(client, "set_keyframes", new()
        {
            ["entries"] = new object[] { new Dictionary<string, object?> { ["layerId"] = "camera", ["property"] = "opacity", ["time"] = 0, ["value"] = 1 } },
        }, ct);
        Assert.Contains("unknown camera property 'opacity'", Text(badProperty));

        var shake = await CallOk(client, "camera_shake", new() { ["startTime"] = 0.5, ["duration"] = 1.0, ["intensity"] = 8 }, ct);
        Assert.Contains("camera shake 0.5–1.5s: 8 px, 12 direction changes per second, seed 1; 39 keyframe(s) on shakeX, shakeY, shakeRotation", shake);
        Assert.Contains("more than half the frame rate (10 fps)", shake);
        var tree = await CallOk(client, "list_layers", new(), ct);
        Assert.Contains("camera: x=50 y=120 zoom=2 rotation=0  keys: x(2) shakeX(13) shakeY(13) shakeRotation(13)", tree);
        Assert.Contains("ignoreCamera", tree);

        using (var frame = DecodeImage(await Call(client, "render_frame", new() { ["time"] = 0.0 }, ct)))
        {
            Assert.Equal(SKColors.White, TestImages.Pixel(frame, 160, 120));
            Assert.Equal(new SKColor(255, 0, 0), TestImages.Pixel(frame, 280, 120));
        }

        Assert.Contains("cleared 13 keyframe(s) from camera.shakeX", await CallOk(client, "clear_keyframes", new() { ["layerId"] = "camera", ["property"] = "shakeX" }, ct));
        Assert.Contains("undid: clear_keyframes camera.shakeX", await CallOk(client, "undo", new(), ct));
        Assert.Contains("removed the camera", await CallOk(client, "set_camera", new() { ["reset"] = true }, ct));
        Assert.DoesNotContain("camera:", await CallOk(client, "list_layers", new(), ct));
        var badLayer = await Call(client, "add_image_layer", new() { ["id"] = "camera" }, ct);
        Assert.Contains("layer id 'camera' is reserved for the project camera", Text(badLayer));
    }

    [Fact]
    public async Task ServerChecksFramesSoundAndTheProject()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-checks");
        var box = TestImages.Solid(Path.Combine(root, "box.png"), 40, 40, SKColors.Red);
        var black = TestImages.Solid(Path.Combine(root, "black.png"), 320, 180, SKColors.Black);
        var spare = TestImages.Solid(Path.Combine(root, "spare.png"), 10, 10, SKColors.Blue);
        var tone = Path.Combine(root, "tone.wav");
        ZeyoCut.Mcp.Demo.ToneGenerator.WriteTone(tone, 440, 1, 0.1);

        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "checks", ["width"] = 320, ["height"] = 180, ["fps"] = 10, ["duration"] = 3.0 }, ct);
        foreach (var file in new[] { box, black, spare })
        {
            await CallOk(client, "import_asset", new() { ["path"] = file }, ct);
        }

        await CallOk(client, "add_image_layer", new() { ["asset"] = "box", ["id"] = "box" }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "black", ["id"] = "flash", ["start"] = 1.0, ["end"] = 1.1 }, ct);

        var diff = await CallOk(client, "frame_diff", new(), ct);
        Assert.StartsWith("frame_diff 0–2.9s (f0–f29, 30 frames compared at 160x90)", diff);
        Assert.Contains("- f10 1s glitch: differs from both neighbors", diff);
        Assert.Contains("'flash' starts here", diff);
        Assert.True(File.Exists(Path.Combine(root, "projects", "checks", "renders", "frame_diff_00000-00029.json")));
        var tooShort = await Call(client, "frame_diff", new() { ["startTime"] = 1.0, ["endTime"] = 1.1 }, ct);
        Assert.Contains("compare at least 3 frames", Text(tooShort));

        var check = await CallOk(client, "check_project", new(), ct);
        Assert.StartsWith("check_project: 1 problem(s) in 'checks'", check);
        Assert.Contains("image asset 'spare' isn't used by any layer", check);

        Assert.Equal("audio_levels: the project has no sound (no audio layers and no video layers with sound), so the export will be silent", await CallOk(client, "audio_levels", new(), ct));
        await CallOk(client, "import_asset", new() { ["path"] = tone }, ct);
        await CallOk(client, "add_audio_layer", new() { ["asset"] = "tone" }, ct);
        var levels = await CallOk(client, "audio_levels", new() { ["sectionSeconds"] = 1.0 }, ct);
        Assert.StartsWith("audio_levels 0–3s, the mix as exported (1 clip(s); loudness: ", levels);
        Assert.Contains("not flagged: silence at the end (1–3s)", levels);
        var badSection = await Call(client, "audio_levels", new() { ["sectionSeconds"] = 0.5 }, ct);
        Assert.Contains("sectionSeconds must be between 1 and 60 (got 0.5)", Text(badSection));

        await CallOk(client, "update_project", new() { ["duration"] = 20.0 }, ct);
        var started = await CallOk(client, "frame_diff", new(), ct);
        Assert.Contains("comparing 200 frames (f0–f199) in the background as framediff_1", started);
        string status;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            await Task.Delay(200, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "framediff_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("framediff_1: completed", status);
        Assert.Contains("- f10 1s glitch: ", status);
    }

    [Fact]
    public async Task ServerAddsSoundEffectsFindsBeatsAndFlapsAMouth()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-audio");
        var projects = Path.Combine(root, "projects");
        var sfx = Path.Combine(root, "library", "sfx");
        var closed = TestImages.Solid(Path.Combine(root, "mouth_closed.png"), 40, 40, SKColors.Blue);
        var open = TestImages.Solid(Path.Combine(root, "mouth_open.png"), 40, 40, SKColors.Red);
        var voice = TestAudio.WriteWav(Path.Combine(root, "voice.wav"), TestAudio.Tone(3, 48000, t => t < 1 || t >= 2), 48000);
        var clicks = TestAudio.WriteWav(Path.Combine(root, "clicks.wav"), TestAudio.Clicks(TestAudio.Beats(120, 0.5, 6), 6, 44100), 44100);
        var longClicks = TestAudio.WriteWav(Path.Combine(root, "long.wav"), TestAudio.Clicks(TestAudio.Beats(120, 0.5, 121), 121, 22050), 22050);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "sound", ["width"] = 320, ["height"] = 180, ["fps"] = 10, ["duration"] = 4.0 }, ct);
        Assert.StartsWith("no sound effects yet; put wav, mp3, ogg or other audio files in ", await CallOk(client, "list_sfx", new(), ct));
        Assert.Contains("there are no sound effects yet", Text(await Call(client, "add_sfx", new() { ["name"] = "boom", ["time"] = 1.0 }, ct)));

        ZeyoCut.Mcp.Demo.ToneGenerator.WriteBoing(Path.Combine(sfx, "impacts", "boom.wav"), 0.6);
        ZeyoCut.Mcp.Demo.ToneGenerator.WriteTone(Path.Combine(sfx, "whoosh.wav"), 880, 0.3);
        var listed = await CallOk(client, "list_sfx", new(), ct);
        Assert.Equal($"sound effects in {sfx} (2):\n(top level)\n- whoosh (0.3s)\nimpacts/\n- impacts/boom (0.6s)", listed);
        Assert.Equal($"sound effects in {sfx} (1 matching 'boo'):\nimpacts/\n- impacts/boom (0.6s)", await CallOk(client, "list_sfx", new() { ["filter"] = "boo" }, ct));
        Assert.Equal("no sound effect name contains 'zap'", await CallOk(client, "list_sfx", new() { ["filter"] = "zap" }, ct));

        var added = await CallOk(client, "add_sfx", new() { ["name"] = "boom", ["time"] = 1.0 }, ct);
        Assert.StartsWith("added sound effect 'impacts/boom' as audio layer 'boom' at 1s (volume 1, role effects, 0.6s long); imported 'impacts/boom' as asset 'boom'", added);
        Assert.Contains("\"role\":\"effects\"", added);
        var again = await CallOk(client, "add_sfx", new() { ["name"] = "impacts/boom", ["time"] = 2.5, ["volume"] = 0.5 }, ct);
        Assert.StartsWith("added sound effect 'impacts/boom' as audio layer 'boom_2' at 2.5s (volume 0.5, role effects, 0.6s long); reused asset 'boom'", again);
        Assert.Contains("undid: add_sfx impacts/boom", await CallOk(client, "undo", new(), ct));
        Assert.Contains("time must be from 0 to before the project end (4s); got 5", Text(await Call(client, "add_sfx", new() { ["name"] = "boom", ["time"] = 5.0 }, ct)));

        foreach (var file in new[] { closed, open, voice, clicks, longClicks })
        {
            await CallOk(client, "import_asset", new() { ["path"] = file }, ct);
        }

        var beats = await CallOk(client, "detect_beats", new() { ["assetId"] = "clicks" }, ct);
        Assert.StartsWith("beats of 'clicks' (6s): 120.0 BPM, 11 beats every 0.5s from 0.5s to 5.5s; 100% of them land on one of the 11 onsets", beats);
        Assert.True(File.Exists(Path.Combine(projects, "sound", "assets", "clicks.beats.json")));
        Assert.Contains("'mouth_open' is not an audio or video asset (image)", Text(await Call(client, "detect_beats", new() { ["assetId"] = "mouth_open" }, ct)));
        var started = await CallOk(client, "detect_beats", new() { ["assetId"] = "long" }, ct);
        Assert.Contains("finding the beats of 'long' (121s) in the background as beats_1", started);
        string status;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            await Task.Delay(200, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "beats_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("beats_1: completed", status);
        Assert.Contains("120.0 BPM, 241 beats every 0.5s", status);

        await CallOk(client, "add_image_layer", new()
        {
            ["id"] = "mouth",
            ["drawings"] = new Dictionary<string, object?> { ["closed"] = "mouth_closed", ["open"] = "mouth_open" },
        }, ct);
        await CallOk(client, "add_audio_layer", new() { ["asset"] = "voice", ["id"] = "voice", ["start"] = 0.5, ["role"] = "voice" }, ct);
        var flapped = await CallOk(client, "auto_lip_flap", new()
        {
            ["mouthLayerId"] = "mouth",
            ["audioLayerId"] = "voice",
            ["closedDrawing"] = "closed",
            ["openDrawings"] = new[] { "open" },
        }, ct);
        Assert.StartsWith("lip flap on 'mouth' from 'voice' at 0.5–3.5s (30 frames): open 20 frames, closed 10; 5 keyframe(s) on mouth.drawing (the mouth opens above ", flapped);
        foreach (var (time, color) in new[] { (0.2, SKColors.Blue), (1.0, SKColors.Red), (2.0, SKColors.Blue), (3.0, SKColors.Red), (3.7, SKColors.Blue) })
        {
            using var frame = DecodeImage(await Call(client, "render_frame", new() { ["time"] = time }, ct));
            Assert.Equal(color, TestImages.Pixel(frame, 160, 90));
        }

        Assert.Contains("'closed' is the closed drawing", Text(await Call(client, "auto_lip_flap", new()
        {
            ["mouthLayerId"] = "mouth",
            ["audioLayerId"] = "voice",
            ["closedDrawing"] = "closed",
            ["openDrawings"] = new[] { "closed" },
        }, ct)));
        Assert.Contains("undid: auto_lip_flap mouth", await CallOk(client, "undo", new(), ct));
        Assert.DoesNotContain("drawing(", await CallOk(client, "list_layers", new(), ct));
    }

    [Fact]
    public async Task ServerAddsEffectsMasksMotionPathsAndMovingTransitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-effects");
        var red = TestImages.Solid(Path.Combine(root, "red.png"), 200, 100, SKColors.Red);
        var blue = TestImages.Solid(Path.Combine(root, "blue.png"), 200, 100, SKColors.Blue);
        var box = TestImages.Solid(Path.Combine(root, "box.png"), 40, 30, SKColors.Lime);

        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "effects", ["width"] = 200, ["height"] = 100, ["fps"] = 10, ["duration"] = 2.0 }, ct);
        foreach (var file in new[] { red, blue, box })
        {
            await CallOk(client, "import_asset", new() { ["path"] = file }, ct);
        }

        await CallOk(client, "add_image_layer", new() { ["id"] = "one", ["drawings"] = new Dictionary<string, object?> { ["normal"] = "red" }, ["end"] = 1.0 }, ct);
        await CallOk(client, "add_image_layer", new() { ["id"] = "two", ["drawings"] = new Dictionary<string, object?> { ["normal"] = "blue" }, ["start"] = 1.0 }, ct);
        await CallOk(client, "add_image_layer", new() { ["id"] = "box", ["drawings"] = new Dictionary<string, object?> { ["normal"] = "box" }, ["zIndex"] = 5 }, ct);

        Assert.Equal("added shadow on 'box': color #000000, x 8, y 8, blur 6, opacity 0.5", await CallOk(client, "set_effect", new() { ["layerId"] = "box", ["effect"] = "shadow" }, ct));
        Assert.Contains("outline takes color, width; x is not for outline", Text(await Call(client, "set_effect", new() { ["layerId"] = "box", ["effect"] = "outline", ["x"] = 3.0 }, ct)));
        Assert.Contains("unknown effect 'glow'; valid values: shadow, outline, blur, tint", Text(await Call(client, "set_effect", new() { ["layerId"] = "box", ["effect"] = "glow" }, ct)));
        Assert.Equal("added tint on 'box': color #FFFF00, amount 0.25", await CallOk(client, "set_effect", new() { ["layerId"] = "box", ["effect"] = "tint", ["color"] = "#FFFF00", ["amount"] = 0.25 }, ct));
        await CallOk(client, "set_keyframes", new()
        {
            ["entries"] = new object[]
            {
                new Dictionary<string, object?> { ["layerId"] = "box", ["property"] = "shadow.x", ["time"] = 0.0, ["value"] = 0 },
                new Dictionary<string, object?> { ["layerId"] = "box", ["property"] = "shadow.x", ["time"] = 1.0, ["value"] = 20 },
            },
        }, ct);
        var warned = await CallOk(client, "set_effect", new() { ["layerId"] = "box", ["effect"] = "shadow", ["x"] = 4.0 }, ct);
        Assert.Contains("shadow.x has 2 keyframe(s), so the value you set is only used after clear_keyframes", warned);
        Assert.Equal("removed the shadow effect from 'box' with its 2 keyframe(s)", await CallOk(client, "set_effect", new() { ["layerId"] = "box", ["effect"] = "shadow", ["remove"] = true }, ct));

        Assert.Equal("added the mask on 'box': ellipse at x 0, y 0, size 40 x 30, feather 0", await CallOk(client, "set_mask", new() { ["layerId"] = "box", ["type"] = "ellipse" }, ct));
        Assert.Contains("asset is only for image masks", Text(await Call(client, "set_mask", new() { ["layerId"] = "box", ["asset"] = "red" }, ct)));
        var path = await CallOk(client, "set_motion_path", new()
        {
            ["layerId"] = "box",
            ["points"] = new object[]
            {
                new Dictionary<string, object?> { ["time"] = 0.0, ["x"] = 20, ["y"] = 50, ["easing"] = "easeInOut" },
                new Dictionary<string, object?> { ["time"] = 1.0, ["x"] = 120, ["y"] = 50 },
            },
            ["autoOrient"] = true,
        }, ct);
        Assert.Equal("added a motion path on 'box': 2 points, 0–1s, 100 px long, turning with the path", path);
        Assert.Contains("'path' keyframes come from set_motion_path", Text(await Call(client, "set_keyframes", new()
        {
            ["entries"] = new object[] { new Dictionary<string, object?> { ["layerId"] = "box", ["property"] = "path", ["time"] = 0.0, ["value"] = 1 } },
        }, ct)));
        var layers = await CallOk(client, "list_layers", new(), ct);
        Assert.Contains("effects: tint mask(ellipse) path(2 points, autoOrient)", layers);
        Assert.Contains("keys: path(2)", layers);
        using (var frame = DecodeImage(await Call(client, "render_frame", new() { ["time"] = 0.5 }, ct)))
        {
            Assert.Equal(200, frame.Width);
        }

        Assert.Contains("zoom goes in or out (got left)", Text(await Call(client, "add_transition", new() { ["layerA"] = "one", ["layerB"] = "two", ["type"] = "zoom", ["direction"] = "left" }, ct)));
        var wipe = await CallOk(client, "add_transition", new() { ["layerA"] = "one", ["layerB"] = "two", ["type"] = "wipe", ["direction"] = "up", ["duration"] = 0.4 }, ct);
        Assert.StartsWith("wipe up 'one' → 'two' over 0.8–1.2s: ", wipe);
        Assert.Contains("'two' is revealed by a mask wiping up", wipe);
        Assert.Contains("undid: add_transition one two", await CallOk(client, "undo", new(), ct));
        var whip = await CallOk(client, "add_transition", new() { ["layerA"] = "one", ["layerB"] = "two", ["type"] = "whip-pan", ["duration"] = 0.4 }, ct);
        Assert.Contains("whipPan left 'one' → 'two' over 0.8–1.2s", whip);
        Assert.Contains("motion blur up to 4 px at 1s", whip);
        Assert.Contains("effects: blur", await CallOk(client, "list_layers", new(), ct));
    }

    [Fact]
    public async Task ServerRemovesBackgroundsAndShowsTransparency()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-mattes");
        var screen = Path.Combine(root, "screen.png");
        using (var surface = SKSurface.Create(new SKImageInfo(100, 100))!)
        {
            surface.Canvas.Clear(new SKColor(40, 190, 60));
            using var paint = new SKPaint();
            paint.Color = SKColors.Red;
            surface.Canvas.DrawRect(new SKRect(30, 30, 70, 70), paint);
            using var image = surface.Snapshot();
            ZeyoCut.Render.Png.Save(image, screen);
        }

        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "mattes", ["width"] = 100, ["height"] = 100, ["fps"] = 10, ["duration"] = 1.0, ["backgroundColor"] = "#0000FF" }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = screen }, ct);
        await CallOk(client, "add_image_layer", new() { ["id"] = "art", ["drawings"] = new Dictionary<string, object?> { ["normal"] = "screen" }, ["x"] = 50, ["y"] = 50 }, ct);

        var keyed = await CallOk(client, "remove_background", new() { ["layerId"] = "art", ["method"] = "chroma" }, ct);
        Assert.Contains("removed the background of 'art' with chroma (key #28BE3C, tolerance 0.4, softness 0.2, spill 1)", keyed);
        Assert.Contains("- key color #28BE3C was picked from the edges of the picture; pass color to choose another", keyed);
        Assert.Contains("- drawing 'normal' (screen): 84% of the pixels keyed out", keyed);
        Assert.Contains("edgeFill takes color, tolerance; softness is not for edgeFill", Text(await Call(client, "remove_background", new() { ["layerId"] = "art", ["method"] = "edgeFill", ["softness"] = 0.5 }, ct)));
        Assert.Contains("method is required", Text(await Call(client, "remove_background", new() { ["layerId"] = "art" }, ct)));

        Assert.Equal("refined the matte of 'art': shrink 2 px, feather 0 px", await CallOk(client, "refine_matte", new() { ["layerId"] = "art", ["shrink"] = 2.0 }, ct));
        Assert.Contains("matte(chroma, shrink 2)", await CallOk(client, "list_layers", new(), ct));

        var frame = await Call(client, "render_frame", new() { ["time"] = 0.0, ["checkerboard"] = true, ["layerId"] = "art" }, ct);
        Assert.False(frame.IsError == true, Text(frame));
        Assert.Contains("frame 0 of 10 at t=0.000s; only 'art'; transparency as a checkerboard; image 100x100", Text(frame));
        using (var image = DecodeImage(frame))
        {
            Assert.Equal(new SKColor(0x70, 0x70, 0x70), TestImages.Pixel(image, 5, 5));
            Assert.Equal(SKColors.Red, TestImages.Pixel(image, 50, 50));
            Assert.Equal(new SKColor(0xA0, 0xA0, 0xA0), TestImages.Pixel(image, 31, 50));
        }

        Assert.Equal("took the background removal (chroma) off 'art'; it shows its whole picture again", await CallOk(client, "remove_background", new() { ["layerId"] = "art", ["remove"] = true }, ct));
        Assert.Contains("undid: remove_background art remove", await CallOk(client, "undo", new(), ct));
        Assert.Contains("matte(chroma, shrink 2)", await CallOk(client, "list_layers", new(), ct));
    }

    [Fact]
    public async Task ServerPutsTextBehindASubjectAndMeasuresIt()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-subject");
        var clip = TestVideos.GreenScreen(Path.Combine(root, "screen.mp4"));
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "behind", ["width"] = 96, ["height"] = 64, ["fps"] = 10, ["duration"] = 1.0 }, ct);
        var imported = await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
        if (imported.Contains("import_1"))
        {
            string status;
            do
            {
                await Task.Delay(200, ct);
                status = await CallOk(client, "get_job_status", new() { ["jobId"] = "import_1" }, ct);
            }
            while (status.Contains("running"));
            Assert.Contains("completed", status);
        }

        await CallOk(client, "add_video_layer", new() { ["id"] = "clip", ["assetId"] = "screen" }, ct);
        await CallOk(client, "add_text_layer", new()
        {
            ["id"] = "title",
            ["text"] = "Hi",
            ["fontSize"] = 14,
            ["color"] = "#FFFF00",
            ["box"] = new Dictionary<string, object?> { ["color"] = "#FFFF00", ["padding"] = 4 },
            ["anchorX"] = 0,
            ["anchorY"] = 0,
            ["x"] = 44,
            ["y"] = 24,
        }, ct);

        var before = await CallOk(client, "text_occlusion", new() { ["textLayerId"] = "title" }, ct);
        Assert.StartsWith("text_occlusion 'title' 0–0.9s (f0–f9, 10 frames): covered min 0.0%, max 0.0%, average 0.0%\n- f0–f9 (0–0.9s): 0.0%\nper frame: ", before);
        Assert.EndsWith(Path.Combine("renders", "text_occlusion_title_00000-00009.json"), before);
        using var front = DecodeImage(await Call(client, "render_frame", new() { ["time"] = 0.9 }, ct));
        Assert.Equal(new SKColor(255, 255, 0), TestImages.Pixel(front, 50, 30));

        var behind = await CallOk(client, "text_behind_subject", new() { ["videoLayerId"] = "clip", ["textLayerIds"] = new[] { "title" }, ["method"] = "chroma" }, ct);
        Assert.StartsWith("put 'title' behind the subject of 'clip': cutout 'clip_subject' draws the subject again above the text, cut out with chroma (key #", behind);
        Assert.Contains("\n- the cutout follows 'clip': its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity\n", behind);
        Assert.Contains("was picked from the edges of the picture", behind);
        Assert.Contains("of the picture is keyed out", behind);
        Assert.Contains("\n- check it with render_frame (layerId=\"clip_subject\" checkerboard=true shows the subject alone", behind);
        Assert.Contains("clip_subject [cutout]  z=1  follows=clip  effects: matte(chroma)", await CallOk(client, "list_layers", new(), ct));

        var after = await CallOk(client, "text_occlusion", new() { ["textLayerId"] = "title" }, ct);
        Assert.Matches(@"^text_occlusion 'title' 0–0\.9s \(f0–f9, 10 frames\): covered min 0\.0%, max [1-9]\d\.\d%", after);
        var shown = await Call(client, "render_frame", new() { ["time"] = 0.9 }, ct);
        Assert.False(shown.IsError == true, Text(shown));
        using (var image = DecodeImage(shown))
        {
            var pixel = TestImages.Pixel(image, 50, 30);
            Assert.True(pixel.Red > 250 && pixel.Green < 5 && pixel.Blue < 5, pixel.ToString());
        }

        var alone = await Call(client, "render_frame", new() { ["time"] = 0.9, ["checkerboard"] = true, ["layerId"] = "clip_subject" }, ct);
        Assert.Contains("; only 'clip_subject'; transparency as a checkerboard", Text(alone));
        using (var image = DecodeImage(alone))
        {
            var pixel = TestImages.Pixel(image, 50, 30);
            Assert.True(pixel.Red > 250 && pixel.Green < 5 && pixel.Blue < 5, pixel.ToString());
            Assert.Contains(TestImages.Pixel(image, 80, 30), new[] { ZeyoCut.Render.Preview.CheckerLight, ZeyoCut.Render.Preview.CheckerDark });
        }

        Assert.Equal(
            "'clip_subject' is the subject cutout of 'clip'; it always follows 'clip', so make this change on 'clip' (its own settings are only name, zIndex, visible and opacity)",
            Text(await Call(client, "split_layer", new() { ["layerId"] = "clip_subject", ["time"] = 0.5 }, ct)).Replace("An error occurred invoking 'split_layer': ", ""));
        Assert.Contains("'title' is a text layer; videoLayerId takes the clip (or picture, or group) whose subject goes in front of the text",
            Text(await Call(client, "text_behind_subject", new() { ["videoLayerId"] = "title", ["textLayerIds"] = new[] { "title" } }, ct)));
        Assert.Contains("'clip' is a video layer; text_occlusion measures text layers",
            Text(await Call(client, "text_occlusion", new() { ["textLayerId"] = "clip" }, ct)));

        // Captions behind the subject: their own cutout, with the chroma matte of 'clip_subject'.
        WriteTranscript(Path.Combine(root, "projects", "behind"), "screen", ("Hi.", 0.1, 0.3), ("Go.", 0.6, 0.8));
        var captions = new Dictionary<string, object?> { ["assetId"] = "screen", ["style"] = "bold_center", ["fontSize"] = 14, ["behindSubject"] = "auto" };
        var auto = await CallOk(client, "generate_captions", captions, ct);
        Assert.StartsWith("added captions under 'captions': 2 cue(s) from 2 words\n", auto);
        Assert.Contains("\nput the captions behind the subject of 'clip': cutout 'captions_subject' draws the subject again above them, cut out with chroma (key #", auto);
        Assert.Contains("\n- the cutout follows the clip that plays 'screen' ('clip'): its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity\n- 'title' is behind the subject too\n- it uses the matte of 'clip_subject', the cutout of the same clip from text_behind_subject\n- auto: the subject covers up to ", auto);
        Assert.Contains("\n- check it with render_frame (layerId=\"captions_subject\" checkerboard=true shows the subject alone", auto);
        Assert.DoesNotContain("get_job_status", auto);
        Assert.Matches(@"captions_subject \[cutout\]  z=\d+  follows=clip  for=captions  effects: matte\(chroma\)", await CallOk(client, "list_layers", new(), ct));
        Assert.Contains("occlusionThreshold must be between 0 and 100 percent (got 150)",
            Text(await Call(client, "generate_captions", new() { ["assetId"] = "screen", ["behindSubject"] = "auto", ["occlusionThreshold"] = 150 }, ct)));
        Assert.Contains("unknown behindSubject 'always'; valid values: off, on, auto",
            Text(await Call(client, "generate_captions", new() { ["assetId"] = "screen", ["behindSubject"] = "always" }, ct)));

        var on = await CallOk(client, "generate_captions", new(captions) { ["behindSubject"] = "on", ["occlusionThreshold"] = 50 }, ct);
        Assert.Contains("\nput the captions behind the subject of 'clip' with their cutout 'captions_subject' (chroma (key #", on);
        Assert.DoesNotContain("auto:", on);
        Assert.EndsWith("\n- occlusionThreshold is only used with behindSubject=auto", on);
        var off = await CallOk(client, "generate_captions", new() { ["assetId"] = "screen" }, ct);
        Assert.EndsWith("\nremoved 'captions_subject', the captions' subject cutout, since behindSubject is off", off);
        Assert.DoesNotContain("captions_subject", await CallOk(client, "list_layers", new(), ct));
        foreach (var step in new[] { "generate_captions screen", "generate_captions screen", "generate_captions screen" })
        {
            Assert.StartsWith($"undid: {step} (", await CallOk(client, "undo", new(), ct));
        }

        await CallOk(client, "undo", new(), ct);
        Assert.DoesNotContain("clip_subject", await CallOk(client, "list_layers", new(), ct));
    }

    [Fact]
    public async Task ServerChecksTheSystem()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-system-check");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);

        var check = await CallOk(client, "system_check", new(), ct);
        Assert.Matches(@"^system check\n- FFmpeg: .+ffmpeg(\.exe)?\n- whisper.+\n- folders: projects in .+[\\/]projects; library .+[\\/]library; files are imported from there and from .+TestOutput \(the ImportFolders setting\)\n- CPU: ", check);
        Assert.Matches(@"\n- RAM: \d+\.\d GB, \d+\.\d GB free\n- GPU: .+\n- ONNX Runtime 1\.\d+\.\d+ \(.*CPUExecutionProvider\)\n", check);
        Assert.Contains("\n- AiDevice=auto: a model runs on DirectML only where the benchmark found it works and takes at most 80% of the CPU's time\n", check);
        Assert.Contains("\n- aiVideo matte: Robust Video Matting (MobileNetV3) (rvm_mobilenetv3_fp32.onnx, 15 MB, GPL-3.0): not downloaded; it downloads the first time it is used\n", check);
        Assert.Contains("\n- aiImage matte: BiRefNet lite (birefnet_lite.onnx, 224 MB, MIT): not downloaded; it downloads the first time it is used\n", check);
        Assert.EndsWith("\n- benchmarking 1080p decoding (no AI model is downloaded yet) in the background as syscheck_1 (DirectML in a separate process, stopped after 60 s per model); poll get_job_status with jobId \"syscheck_1\"", check);
        Assert.DoesNotContain("last benchmark", check);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        do
        {
            await Task.Delay(250, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "syscheck_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        var saved = Path.Combine(root, "library", AiBenchmark.FileName);
        Assert.StartsWith("syscheck_1: completed in ", status);
        Assert.Contains(" — system_check benchmark of 1080p decoding (no AI model is downloaded yet)\nbenchmark at 1920x1080, 30 fps:\n", status);
        Assert.Matches(@"\n- decoding 1080p video: \d+(\.\d)? ms a frame\n", status);
        Assert.Contains($"\n- saved to {saved}; AI jobs estimate their time from it, and AiDevice=auto picks each model's device with it\n", status);
        Assert.EndsWith("\n- no AI model is downloaded yet; each downloads the first time it is used, so run system_check again after that to measure it", status);
        Assert.True(File.Exists(saved));
        Assert.Contains("last benchmark: ", await CallOk(client, "system_check", new(), ct));
    }

    [Fact]
    public async Task ServerComputesAiMattesInTheBackground()
    {
        Ffmpeg.Require();
        var models = Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models");
        Assert.SkipWhen(
            !File.Exists(Path.Combine(models, "birefnet_lite.onnx")) || !File.Exists(Path.Combine(models, "rvm_mobilenetv3_fp32.onnx")),
            $"the AI models aren't in {models}; skipping the AI matte server test");
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-ai-mattes");
        var clip = TestVideos.GreenScreen(Path.Combine(root, "screen.mp4"));
        var art = Path.Combine(root, "art.png");
        using (var surface = SKSurface.Create(new SKImageInfo(120, 90))!)
        {
            surface.Canvas.Clear(new SKColor(235, 235, 235));
            using var paint = new SKPaint();
            paint.IsAntialias = true;
            paint.Color = new SKColor(220, 90, 30);
            surface.Canvas.DrawCircle(60, 45, 28, paint);
            using var picture = surface.Snapshot();
            ZeyoCut.Render.Png.Save(picture, art);
        }

        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct, new() { ["ZEYOCUT_MODELS_DIR"] = models });

        async Task<string> Finished(string jobId)
        {
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(180);
            do
            {
                await Task.Delay(250, ct);
                status = await CallOk(client, "get_job_status", new() { ["jobId"] = jobId }, ct);
            }
            while (status.Contains("running") && DateTime.UtcNow < deadline);

            return status;
        }

        await CallOk(client, "create_project", new() { ["name"] = "aimattes", ["width"] = 96, ["height"] = 64, ["fps"] = 10, ["duration"] = 1.0 }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = art }, ct);
        var imported = await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
        if (imported.Contains("import_1"))
        {
            Assert.Contains("completed", await Finished("import_1"));
        }

        await CallOk(client, "add_image_layer", new() { ["id"] = "art", ["drawings"] = new Dictionary<string, object?> { ["normal"] = "art" } }, ct);
        await CallOk(client, "add_video_layer", new() { ["id"] = "clip", ["assetId"] = "screen" }, ct);

        Assert.Contains("aiVideo takes no settings; tolerance is not for aiVideo", Text(await Call(client, "remove_background", new() { ["layerId"] = "clip", ["method"] = "aiVideo", ["tolerance"] = 0.2 }, ct)));
        Assert.Contains("aiImage is for image layers; use chroma or aiVideo on video layer 'clip'", Text(await Call(client, "remove_background", new() { ["layerId"] = "clip", ["method"] = "aiImage" }, ct)));

        // A benchmark saying the model is very slow here: a matte that would take more than 5 minutes
        // isn't started (and nothing changes) until confirmLongJob=true.
        var benchmark = Path.Combine(root, "library", AiBenchmark.FileName);
        void Slow(AiFeature feature) => AiBenchmark.Save(benchmark, new BenchmarkResults
        {
            CheckedUtc = DateTime.UtcNow,
            Machine = SystemInfo.Hardware().Machine,
            DecodeMs = 5,
            Features = [new FeatureTiming { Feature = feature.Id, Model = AiBenchmark.Identity(feature.Model), CpuMs = 1e8, CpuLoadMs = 100, GpuProblem = "DirectML can't start: test" }],
        });
        Slow(AiFeatures.AiVideo);
        var slow = await CallOk(client, "remove_background", new() { ["layerId"] = "clip", ["method"] = "aiVideo" }, ct);
        Assert.Equal("not started, and nothing was changed: the aiVideo matte (10 frames of clip 'screen' with Robust Video Matting (MobileNetV3) on the CPU) would take about 277 h 47 min (measured by system_check), more than 5 minutes. Tell the user how long it takes first; to go ahead, call remove_background again with the same arguments and confirmLongJob=true.", slow);
        Assert.DoesNotContain("matte(", await CallOk(client, "list_layers", new(), ct));
        File.Delete(benchmark);

        var video = await CallOk(client, "remove_background", new() { ["layerId"] = "clip", ["method"] = "aiVideo" }, ct);
        Assert.StartsWith("removed the background of 'clip' with aiVideo\n", video);
        Assert.Contains("- the matte is computed in the background as matte_1: 10 frames of clip 'screen' with Robust Video Matting (MobileNetV3) on the CPU, about 2 s (a rough guess; system_check measures this computer)\n", video);
        Assert.Contains("- until it is done, previews draw 'clip' with its background and exports refuse; poll get_job_status with jobId \"matte_1\"", video);
        var computed = await Finished("matte_1");
        Assert.Contains("matte_1: completed in", computed);
        Assert.Contains("aiVideo matte of 'clip' (screen, 10 frames)", computed);
        Assert.Contains("computed the aiVideo matte of 'clip' with Robust Video Matting (MobileNetV3) on the CPU: 10 frames at 96x64 in", computed);
        Assert.Matches(@"- matte assets/screen\.matte-aivideo-[0-9a-f]{8}\.mkv", computed);

        var again = await CallOk(client, "remove_background", new() { ["layerId"] = "clip", ["method"] = "aiVideo" }, ct);
        Assert.Contains("removed the background of 'clip' with aiVideo (was aiVideo)", again);
        Assert.Matches(@"- clip 'screen': the matte is already computed: assets/screen\.matte-aivideo-[0-9a-f]{8}\.mkv", again);
        Assert.DoesNotContain("get_job_status", again);
        Assert.Contains("matte(aiVideo)", await CallOk(client, "list_layers", new(), ct));

        // A cutout of the same clip uses the matte computed for 'clip'.
        await CallOk(client, "add_video_layer", new() { ["id"] = "plate", ["assetId"] = "screen" }, ct);
        await CallOk(client, "add_text_layer", new() { ["id"] = "label", ["text"] = "A", ["fontSize"] = 12 }, ct);
        var cutout = await CallOk(client, "text_behind_subject", new() { ["videoLayerId"] = "plate", ["textLayerIds"] = new[] { "label" } }, ct);
        Assert.StartsWith("put 'label' behind the subject of 'plate': cutout 'plate_subject' draws the subject again above the text, cut out with aiVideo", cutout);
        Assert.Matches(@"\n- clip 'screen': the matte is already computed: assets/screen\.matte-aivideo-[0-9a-f]{8}\.mkv\n", cutout);
        Assert.DoesNotContain("get_job_status", cutout);

        Slow(AiFeatures.AiImage);
        Assert.StartsWith("not started, and nothing was changed: the aiImage matte (drawing 'normal' (art) with BiRefNet lite on the CPU) would take about 27 h 47 min (measured by system_check), more than 5 minutes.",
            await CallOk(client, "remove_background", new() { ["layerId"] = "art", ["method"] = "aiImage" }, ct));
        var image = await CallOk(client, "remove_background", new() { ["layerId"] = "art", ["method"] = "aiImage", ["confirmLongJob"] = true }, ct);
        Assert.Contains("- the matte is computed in the background as matte_2: drawing 'normal' (art) with BiRefNet lite on the CPU, about 27 h 47 min (measured by system_check)\n", image);
        File.Delete(benchmark);
        Assert.Contains("layer 'art': the aiImage matte of 'art' isn't computed yet", await CallOk(client, "check_project", new(), ct));
        var refused = await Call(client, "start_export", new() { ["fileName"] = "early.mp4" }, ct);
        Assert.True(refused.IsError);
        Assert.Contains("background removal isn't finished: layer 'art' (aiImage matte of 'art'); wait for its matte job (get_job_status) or run remove_background again, then export", Text(refused));

        var still = await Finished("matte_2");
        Assert.Contains("computed the aiImage matte of 'art' with BiRefNet lite on the CPU in", still);
        Assert.Matches(@"- drawing 'normal' \(art\): about \d+(\.\d)?% of the picture is background; matte assets/art\.matte-aiimage-[0-9a-f]{8}\.png", still);
        Assert.DoesNotContain("isn't computed yet", await CallOk(client, "check_project", new(), ct));

        var frame = await Call(client, "render_frame", new() { ["time"] = 0.0, ["checkerboard"] = true, ["layerId"] = "art" }, ct);
        Assert.False(frame.IsError == true, Text(frame));
        Assert.DoesNotContain("Warnings", Text(frame));
        using (var shown = DecodeImage(frame))
        {
            Assert.Equal(new SKColor(220, 90, 30), TestImages.Pixel(shown, 48, 32));
        }

        Assert.Contains("matte_1", await CallOk(client, "get_job_status", new(), ct));

        // Captions behind a clip whose matte isn't computed yet: they are added at once, and the fades
        // follow from a background job once the matte is done, as their own undo step.
        foreach (var (name, box) in new[] { ("screen2", 20), ("screen3", 16) })
        {
            var file = TestVideos.GreenScreen(Path.Combine(root, name + ".mp4"), box: box);
            var added = await CallOk(client, "import_video", new() { ["path"] = file }, ct);
            if (added.Contains("in the background as import_"))
            {
                Assert.Contains("completed", await Finished(added.Split("in the background as ")[1].Split(' ', '.', '\n')[0]));
            }

            await CallOk(client, "add_video_layer", new() { ["id"] = name == "screen2" ? "talk" : "talk3", ["assetId"] = name }, ct);
            WriteTranscript(Path.Combine(root, "projects", "aimattes"), name, ("Hi.", 0.1, 0.3), ("Go.", 0.6, 0.8));
        }

        Slow(AiFeatures.AiVideo);
        Assert.StartsWith("not started, and nothing was changed: the aiVideo matte (10 frames of clip 'screen3' with Robust Video Matting (MobileNetV3) on the CPU) would take about 277 h 47 min",
            await CallOk(client, "text_behind_subject", new() { ["videoLayerId"] = "talk3", ["textLayerIds"] = new[] { "label" } }, ct));
        Assert.StartsWith("not started, and nothing was changed: the aiVideo matte (10 frames of clip 'screen2' with Robust Video Matting (MobileNetV3) on the CPU) would take about 277 h 47 min",
            await CallOk(client, "generate_captions", new() { ["assetId"] = "screen2", ["behindSubject"] = "auto" }, ct));
        File.Delete(benchmark);

        var auto = await CallOk(client, "generate_captions", new() { ["assetId"] = "screen2", ["behindSubject"] = "auto" }, ct);
        Assert.Contains("\nput the captions behind the subject of 'talk': cutout 'captions_subject' draws the subject again above them, cut out with aiVideo (zIndex ", auto);
        Assert.Contains("\n- the matte is computed in the background as matte_3: 10 frames of clip 'screen2' with Robust Video Matting (MobileNetV3) on the CPU, about 2 s (a rough guess; system_check measures this computer)\n", auto);
        Assert.Matches(@"\n- auto: \d+ caption frames are measured in the background as captions_1 once matte_3 is done, and the fades are added as their own undo step \(an edit made meanwhile makes it refuse, so run generate_captions again then\); until then the captions are behind the subject the whole time; poll get_job_status with jobId ""captions_1""\n", auto);
        var fades = await Finished("captions_1");
        Assert.Contains("captions_1: completed", fades);
        Assert.Contains("auto: the subject covers up to ", fades);
        Assert.StartsWith("undid: generate_captions auto fades (", await CallOk(client, "undo", new(), ct));
        await CallOk(client, "redo", new(), ct);

        var meanwhile = await CallOk(client, "generate_captions", new() { ["assetId"] = "screen3", ["behindSubject"] = "auto", ["groupId"] = "subs" }, ct);
        Assert.Contains("cutout 'subs_subject'", meanwhile);
        Assert.Contains("as captions_2 once matte_4 is done", meanwhile);
        await CallOk(client, "update_layer", new() { ["layerId"] = "label", ["changes"] = new Dictionary<string, object?> { ["x"] = 10 } }, ct);
        var refusedFades = await Finished("captions_2");
        Assert.Contains("failed", refusedFades);
        Assert.Contains("so the fades weren't added (the captions stay behind the subject the whole time); run generate_captions again", refusedFades);
        Assert.StartsWith("undid: update_layer label (", await CallOk(client, "undo", new(), ct));
        var direct = await CallOk(client, "generate_captions", new() { ["assetId"] = "screen3", ["behindSubject"] = "auto", ["groupId"] = "subs" }, ct);
        Assert.Matches(@"\n- clip 'screen3': the matte is already computed: assets/screen3\.matte-aivideo-[0-9a-f]{8}\.mkv\n- auto: the subject covers up to ", direct);
        Assert.DoesNotContain("get_job_status", direct);
    }

    private static void WriteTranscript(string projectDirectory, string assetId, params (string Text, double Start, double End)[] words)
    {
        using var project = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "project.json")));
        var file = project.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("id").GetString() == assetId).GetProperty("file").GetString()!;
        var asset = new ZeyoCut.Core.Model.Asset { Id = assetId, File = file };
        ZeyoCut.Core.Serialization.AnalysisFiles.Save(ZeyoCut.Core.Serialization.AnalysisFiles.TranscriptPath(projectDirectory, asset), new ZeyoCut.Core.Model.Transcript
        {
            Asset = assetId,
            Language = "en",
            Words = words.Select(w => new ZeyoCut.Core.Model.TranscriptWord { Text = w.Text, Start = w.Start, End = w.End, Probability = 0.9 }).ToList(),
        });
    }

    [Fact]
    public async Task ServerExportsBatchesThumbnailsAndFrames()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-output");
        var projects = Path.Combine(root, "projects");
        var box = TestImages.Solid(Path.Combine(root, "box.png"), 40, 40, SKColors.Orange);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "launch", ["width"] = 320, ["height"] = 180, ["fps"] = 10, ["duration"] = 1.0 }, ct);
        await CallOk(client, "import_asset", new() { ["path"] = box }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "box" }, ct);
        var exports = Path.Combine(projects, "launch", "exports");

        async Task<string> Finished(string jobId)
        {
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(180);
            do
            {
                await Task.Delay(250, ct);
                status = await CallOk(client, "get_job_status", new() { ["jobId"] = jobId }, ct);
            }
            while (status.Contains("running") && DateTime.UtcNow < deadline);

            return status;
        }

        var none = await Call(client, "export_batch", new() { ["presets"] = Array.Empty<string>() }, ct);
        Assert.Contains("presets must list at least one preset: youtube_1080p, shorts_1080p", Text(none));
        var twice = await Call(client, "export_batch", new() { ["presets"] = new[] { "youtube_1080p", "YouTube_1080p" } }, ct);
        Assert.Contains("preset 'youtube_1080p' is listed twice", Text(twice));
        var unknown = await Call(client, "export_batch", new() { ["presets"] = new[] { "tiktok_4k" } }, ct);
        Assert.Contains("unknown preset 'tiktok_4k'; presets: youtube_1080p, shorts_1080p", Text(unknown));
        var folder = await Call(client, "export_batch", new() { ["presets"] = new[] { "youtube_1080p" }, ["fileName"] = "a\\b" }, ct);
        Assert.Contains("fileName 'a\\b' must be a plain file name", Text(folder));

        var started = await CallOk(client, "export_batch", new() { ["presets"] = new[] { "youtube_1080p", "shorts_1080p" } }, ct);
        var youtube = Path.Combine(exports, "launch_youtube_1080p.mp4");
        var shorts = Path.Combine(exports, "launch_shorts_1080p.mp4");
        Assert.StartsWith("started batch_1: 2 export(s) of 10 frames @ 10 fps, one after another", started);
        Assert.Contains($"- youtube_1080p: 1920x1080 -> {youtube}", started);
        Assert.Contains($"- shorts_1080p: 1080x1920, the 320x180 canvas fitted over a blurred copy -> {shorts}", started);

        var batch = await Finished("batch_1");
        Assert.Contains("batch_1: completed", batch);
        Assert.Contains("exported 2 file(s):", batch);
        Assert.Contains($"- youtube_1080p: {youtube} — 1.000s, 1920x1080 @ 10 fps, h264 yuv420p", batch);
        Assert.Contains($"- shorts_1080p: {shorts} — 1.000s, 1080x1920 @ 10 fps, h264 yuv420p", batch);
        Assert.Contains("processing: preset shorts_1080p: 1080x1920, H.264 high profile, CRF 18, AAC 192k", batch);
        Assert.Contains("processing: the 320x180 canvas is fitted whole into 1080x1920 over a blurred, enlarged copy of itself", batch);
        Assert.Equal(2, batch.Split("fitted whole").Length);
        Assert.True(File.Exists(youtube));
        Assert.True(File.Exists(shorts));

        var thumbnail = await Call(client, "export_thumbnail", new() { ["time"] = 0.5 }, ct);
        Assert.False(thumbnail.IsError == true, Text(thumbnail));
        var thumbnailPath = Path.Combine(exports, "thumbnail_00005.png");
        Assert.StartsWith($"thumbnail 1280x720 of frame 5 (t=0.500s), from the 320x180 canvas, enlarged 4x; saved {thumbnailPath} (", Text(thumbnail));
        using (var preview = DecodeImage(thumbnail))
        {
            Assert.Equal((960, 540), (preview.Width, preview.Height));
        }

        using (var saved = SKImage.FromEncodedData(thumbnailPath))
        {
            Assert.Equal((1280, 720), (saved.Width, saved.Height));
        }

        var square = await CallOk(client, "export_thumbnail", new() { ["time"] = 0.0, ["width"] = 720, ["height"] = 720, ["fileName"] = "square" }, ct);
        Assert.Contains($"thumbnail 720x720 of frame 0 (t=0.000s), cropped from the center of the 320x180 canvas, enlarged 4x; saved {Path.Combine(exports, "square.png")}", square);
        var jpeg = await Call(client, "export_thumbnail", new() { ["time"] = 0.0, ["fileName"] = "thumb.jpg" }, ct);
        Assert.Contains("thumbnails are PNG files (got '.jpg')", Text(jpeg));
        var late = await Call(client, "export_thumbnail", new() { ["time"] = 5.0 }, ct);
        Assert.Contains("time must be between 0 and the end of the project (1s); got 5", Text(late));

        var frames = await CallOk(client, "export_frames", new() { ["startTime"] = 0.2, ["endTime"] = 0.5 }, ct);
        var framesFolder = Path.Combine(exports, "launch_frames");
        Assert.Contains($"writing 4 frame(s) f2–f5 (0.2–0.5s, 320x180 png) to {framesFolder} in the background as frames_1", frames);
        var written = await Finished("frames_1");
        Assert.Contains($"wrote 4 png frame(s) f2–f5 (0.2–0.5s) at 320x180 to {framesFolder}: frame_00002.png … frame_00005.png", written);
        Assert.Equal(["frame_00002.png", "frame_00003.png", "frame_00004.png", "frame_00005.png"], Directory.GetFiles(framesFolder).Select(Path.GetFileName).Order());
        var gif = await Call(client, "export_frames", new() { ["startTime"] = 0.0, ["endTime"] = 0.5, ["format"] = "gif" }, ct);
        Assert.Contains("unknown format 'gif'; valid values: png, jpg", Text(gif));

        var jobs = await CallOk(client, "get_job_status", new(), ct);
        Assert.Contains("- batch_1: completed", jobs);
        Assert.Contains("- frames_1: completed", jobs);
    }

    [Fact]
    public async Task ServerTracksAnObjectAndCorrectsIt()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-tracking");
        var square = TestVideos.MovingSquare(Path.Combine(root, "square.mp4"), cover: (30, 35, 100, 75));
        var pattern = TestVideos.Pattern(Path.Combine(root, "pattern.mp4"), seconds: 6);
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);

        async Task<string> Finished(string jobId)
        {
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(180);
            do
            {
                await Task.Delay(250, ct);
                status = await CallOk(client, "get_job_status", new() { ["jobId"] = jobId }, ct);
            }
            while (status.Contains("running") && DateTime.UtcNow < deadline);

            return status;
        }

        async Task Import(string path, string id)
        {
            var imported = await CallOk(client, "import_video", new() { ["path"] = path, ["id"] = id }, ct);
            if (imported.Contains("in the background as "))
            {
                var job = imported.Split("in the background as ")[1].Split([' ', ';', ',', '\n'])[0];
                Assert.Contains("completed", await Finished(job));
            }
        }

        Dictionary<string, object?> Box(double x, double y, double width, double height) => new() { ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height };

        await CallOk(client, "create_project", new() { ["name"] = "tracking", ["width"] = 640, ["height"] = 360, ["fps"] = 30, ["duration"] = 2.0 }, ct);
        await Import(square, "square");
        await CallOk(client, "add_video_layer", new() { ["id"] = "clip", ["assetId"] = "square" }, ct);
        var projectFile = Path.Combine(root, "projects", "tracking", "project.json");

        // The square is at 20,60 40x40 in frame 0 of the clip, which is fit to the canvas: 40,120 80x80.
        var tracked = await CallOk(client, "track_object", new() { ["layerId"] = "clip", ["time"] = 0.0, ["box"] = Box(40, 120, 80, 80) }, ct);
        Assert.StartsWith("tracked the box on 'clip' as track_1 with csrt, forward\n- frames 0–59 of 'square' (60 frames): 0–1.967s on 'clip'\n- the box in source pixels: 20,60 40x40 at frame 0, ", tracked);
        Assert.Matches(@"\n- possible drift \(confidence under 0\.5\): 1(\.033)?–1\.\d+s on 'clip' \(frames 3[01]–\d\d, lowest 0\.\d\d(, lost on \d+)?\)", tracked);
        Assert.EndsWith("\n- look at those frames with render_frame debug=true (it draws the box); where it is off the object, correct it with reanchor_track trackId=\"track_1\"", tracked);
        Assert.Contains("tracks=track_1", await CallOk(client, "list_layers", new(), ct));

        // debug draws the box: yellow, dashed from its top-left corner.
        using (var debug = DecodeImage(await Call(client, "render_frame", new() { ["time"] = 0.0, ["maxWidth"] = 640, ["debug"] = true }, ct)))
        {
            var edge = TestImages.Pixel(debug, 43, 120);
            Assert.True(edge.Red > 200 && edge.Green > 170 && edge.Blue < 90, $"the track's box edge is {edge}");
        }

        var first = JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("tracks")[0];
        Assert.Equal(6, JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("formatVersion").GetInt32());
        var firstFile = first.GetProperty("file").GetString()!;
        Assert.True(File.Exists(Path.Combine(root, "projects", "tracking", firstFile)));

        // After the cover the square is at 140,100 (frame 40): 280,200 on the canvas.
        var corrected = await CallOk(client, "reanchor_track", new() { ["trackId"] = "track_1", ["time"] = 4 / 3.0, ["box"] = Box(280, 200, 80, 80) }, ct);
        Assert.StartsWith("reanchored track_1 at frame 40 (1.333s on 'clip') and tracked frames 40–59 again with csrt; its other frames stayed as they were\n- frames 0–59 of 'square' (60 frames): 0–1.967s on 'clip'\n- the box in source pixels: 20,60 40x40 at frame 0, ", corrected);
        var reanchored = JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("tracks")[0];
        Assert.Equal([0, 40], reanchored.GetProperty("anchors").EnumerateArray().Select(a => a.GetProperty("frame").GetInt32()));
        Assert.NotEqual(firstFile, reanchored.GetProperty("file").GetString());

        Assert.Contains("undid: reanchor_track track_1", await CallOk(client, "undo", new(), ct));
        Assert.Equal(firstFile, JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("tracks")[0].GetProperty("file").GetString());
        Assert.Contains("undid: track_object track_1", await CallOk(client, "undo", new(), ct));
        Assert.False(JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.TryGetProperty("tracks", out _));
        Assert.DoesNotContain("tracks=", await CallOk(client, "list_layers", new(), ct));
        await CallOk(client, "redo", new(), ct);
        await CallOk(client, "redo", new(), ct);
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("tracks")[0].GetProperty("anchors").GetArrayLength());

        var outside = await Call(client, "track_object", new() { ["layerId"] = "clip", ["time"] = 0.0, ["box"] = Box(700, 10, 50, 50) }, ct);
        Assert.True(outside.IsError);
        Assert.Contains("the box 700,10 50x50 is mostly outside clip 'clip''s picture at 0s", Text(outside));
        var missing = await Call(client, "reanchor_track", new() { ["trackId"] = "track_9", ["time"] = 0.0, ["box"] = Box(40, 120, 80, 80) }, ct);
        Assert.EndsWith(": track 'track_9' not found; tracks: track_1", Text(missing));

        // 180 frames: a tracking_N job that adds the track as its own undo step.
        await Import(pattern, "pattern");
        await CallOk(client, "add_video_layer", new() { ["id"] = "long", ["assetId"] = "pattern" }, ct);
        var started = await CallOk(client, "track_object", new() { ["layerId"] = "long", ["time"] = 0.0, ["box"] = Box(280, 140, 80, 80), ["method"] = "template" }, ct);
        Assert.Equal("tracking the box on 'long' as track_2 with template, forward, in the background as tracking_1: 179 frames of 'pattern' (frames 0–179)\n- drawn at 0s: frame 0, 133.333,93.333 53.333x53.333 in the source pixels of 'pattern'\n- poll get_job_status with jobId \"tracking_1\"; the track is added as its own undo step when it is done", started);
        var status = await Finished("tracking_1");
        Assert.StartsWith("tracking_1: completed in ", status);
        Assert.Contains(" — track_object track_2 (template, 179 frames of 'pattern')\ntracked the box on 'long' as track_2 with template, forward\n- frames 0–179 of 'pattern' (180 frames): 0–5.967s on 'long'\n", status);
        Assert.Contains("tracks=track_2", await CallOk(client, "list_layers", new(), ct));
        Assert.Contains("undid: track_object track_2", await CallOk(client, "undo", new(), ct));
        Assert.Equal(["track_1"], JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task ServerPrerendersLayersWithAlpha()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-prerender");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);

        async Task<string> Finished(string jobId)
        {
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(120);
            do
            {
                await Task.Delay(200, ct);
                status = await CallOk(client, "get_job_status", new() { ["jobId"] = jobId }, ct);
            }
            while (status.Contains("running") && DateTime.UtcNow < deadline);

            return status;
        }

        await CallOk(client, "create_project", new() { ["name"] = "overlays", ["width"] = 160, ["height"] = 120, ["fps"] = 10, ["duration"] = 3.0 }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Hi", ["id"] = "title", ["start"] = 0.5, ["end"] = 2.0 }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "!", ["id"] = "mark", ["parentId"] = "title" }, ct);
        var folder = Path.Combine(root, "projects", "overlays", "exports", "overlays_prerender");

        Assert.Equal(
            $"prerendering 2 layer(s) as ProRes 4444 with alpha, 160x120 @ 10 fps, to {folder} in the background as prerender_1:\n- 'title': frames 5–19 (0.5–2s) -> title.mov\n- 'mark': frames 5–19 (0.5–2s) -> mark.mov\nPoll get_job_status with jobId \"prerender_1\".\nWarnings:\n- 'mark' is a child of 'title', so it is in that prerender too",
            await CallOk(client, "prerender_layers", new() { ["layerIds"] = new[] { "title", "mark" } }, ct));
        var done = await Finished("prerender_1");
        Assert.Contains($"prerender_1: completed", done);
        Assert.Contains($"prerendered 2 layer(s) as ProRes 4444 with alpha to {folder}; export_otio and export_fcpxml put them on tracks above the clips:\n- 'title': frames 5–19 (0.5–2s), ", done);
        Assert.Contains("s: title.mov\n- 'mark': frames 5–19 (0.5–2s), ", done);
        Assert.True(File.Exists(Path.Combine(folder, "title.mov")));
        Assert.True(File.Exists(Path.Combine(folder, "prerender.json")));

        await CallOk(client, "prerender_layers", new() { ["layerIds"] = new[] { "title" } }, ct);
        var again = await Finished("prerender_2");
        Assert.Contains($"prerendered 1 layer(s) as ProRes 4444 with alpha to {folder} (1 already current); ", again);
        Assert.Contains("\n- 'title': already current, frames 5–19 (0.5–2s), ", again);

        var otio = Path.Combine(root, "projects", "overlays", "exports", "overlays.otio");
        Assert.Equal(
            $"wrote {otio} (OpenTimelineIO): 160x120 @ 10 fps, 3s; 0 video track(s), 1 of prerendered layers, 0 audio track(s), 0 marker(s)\n- V1, prerendered: 'title' 0.5–2s",
            await CallOk(client, "export_otio", new(), ct));
        using (var document = JsonDocument.Parse(File.ReadAllText(otio)))
        {
            var clip = document.RootElement.GetProperty("tracks").GetProperty("children")[0].GetProperty("children")[1];
            Assert.Equal("title (prerendered)", clip.GetProperty("name").GetString());
            Assert.Equal(Path.Combine(folder, "title.mov"), new Uri(clip.GetProperty("media_references").GetProperty("DEFAULT_MEDIA").GetProperty("target_url").GetString()!).LocalPath);
        }

        await CallOk(client, "update_layer", new() { ["layerId"] = "title", ["changes"] = new Dictionary<string, object?> { ["text"] = "Hey" } }, ct);
        Assert.EndsWith(
            "0 video track(s), 1 of prerendered layers, 0 audio track(s), 0 marker(s)\n- V1, prerendered: 'mark' 0.5–2s\nprerendered, but changed since, so left out: 'title'; prerender_layers draws them again\nnot in the timeline:\n- layers only ZeyoCut draws: text 'title'; prerender_layers draws them into video with alpha for the timeline",
            await CallOk(client, "export_otio", new() { ["fileName"] = "changed" }, ct));
        Assert.EndsWith(": export_otio writes .otio files (got '.json')", Text(await Call(client, "export_otio", new() { ["fileName"] = "edit.json" }, ct)));

        var fcpxml = Path.Combine(root, "projects", "overlays", "exports", "overlays.fcpxml");
        Assert.StartsWith($"wrote {fcpxml} (FCPXML 1.9): 160x120 @ 10 fps, 3s; 0 video track(s), 1 of prerendered layers, 0 audio track(s), 0 marker(s)\n- V1, prerendered: 'mark' 0.5–2s\n",
            await CallOk(client, "export_fcpxml", new(), ct));
        var spine = System.Xml.Linq.XDocument.Load(fcpxml).Root!.Descendants("spine").Single().Elements().ToList();
        Assert.Equal(["gap 0s+30/10s"], spine.Select(e => $"{e.Name.LocalName} {(string?)e.Attribute("offset")}+{(string?)e.Attribute("duration")}"));
        Assert.Equal(["mark (prerendered) lane=1 offset=5/10s"], spine[0].Elements("asset-clip").Select(e => $"{(string?)e.Attribute("name")} lane={(string?)e.Attribute("lane")} offset={(string?)e.Attribute("offset")}"));
        Assert.EndsWith(": export_fcpxml writes .fcpxml files (got '.xml')", Text(await Call(client, "export_fcpxml", new() { ["fileName"] = "edit.xml" }, ct)));

        Assert.EndsWith(": layerIds is empty; give the ids of the layers to prerender", Text(await Call(client, "prerender_layers", new() { ["layerIds"] = Array.Empty<string>() }, ct)));
        Assert.Contains("'nope'", Text(await Call(client, "prerender_layers", new() { ["layerIds"] = new[] { "nope" } }, ct)));
    }

    [Fact]
    public async Task ServerMakesProxiesOfBigFootageForPreviewsAndTracking()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-proxies");
        var square = TestVideos.MovingSquare(Path.Combine(root, "square.mp4"));
        var big = Path.Combine(root, "big.mp4");
        var scaled = ZeyoCut.Render.Export.ProcessRunner.Run(ffmpeg.Ffmpeg, ["-v", "error", "-y", "-i", square, "-vf", "scale=2880:1620:flags=neighbor", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", big], TimeSpan.FromSeconds(120));
        Assert.True(scaled.ExitCode == 0, scaled.StandardError);
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);

        async Task<string> Finished(string jobId)
        {
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(180);
            do
            {
                await Task.Delay(250, ct);
                status = await CallOk(client, "get_job_status", new() { ["jobId"] = jobId }, ct);
            }
            while (status.Contains("running") && DateTime.UtcNow < deadline);

            return status;
        }

        await CallOk(client, "create_project", new() { ["name"] = "proxies", ["width"] = 640, ["height"] = 360, ["fps"] = 30, ["duration"] = 2.0 }, ct);
        var imported = await CallOk(client, "import_video", new() { ["path"] = big, ["id"] = "big" }, ct);
        Assert.EndsWith("\nmaking a 1920 px proxy of 'big' in the background as proxy_1: previews and object tracking read it once it is done, exports always read the original", imported);
        var made = await Finished("proxy_1");
        Assert.Contains("completed", made);
        Assert.Matches(@"made 1 proxy for previews and object tracking \(exports read the original\):\n- 'big': 1920x1080, \d+\.\d MB, in \d+\.\ds: proxies\\big\.[0-9a-f]{12}\.mp4", made);

        await CallOk(client, "add_video_layer", new() { ["id"] = "clip", ["assetId"] = "big" }, ct);
        Assert.Contains("; 'big' drawn from its 1920 px proxy (exports read the original); image 640x360", Text(await Call(client, "render_frame", new() { ["time"] = 0.5, ["maxWidth"] = 640 }, ct)));
        Assert.Contains("'big' drawn from its 1920 px proxy", Text(await Call(client, "view_source", new() { ["assetId"] = "big", ["count"] = 2, ["columns"] = 2, ["maxWidth"] = 600 }, ct)));

        // Tracking reads the proxy, and the box is still in the clip's own pixels: the square is at
        // 20,60 40x40 in the small clip, so 180,540 360x360 here, and 40,120 80x80 on the canvas.
        var box = new Dictionary<string, object?> { ["x"] = 40, ["y"] = 120, ["width"] = 80, ["height"] = 80 };
        var tracked = await CallOk(client, "track_object", new() { ["layerId"] = "clip", ["time"] = 0.0, ["box"] = box }, ct);
        Assert.StartsWith("tracked the box on 'clip' as track_1 with csrt, forward\n- frames 0–59 of 'big' (60 frames): 0–1.967s on 'clip'\n- the box in source pixels: 180,540 360x360 at frame 0, ", tracked);
        var track = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "projects", "proxies", "project.json"))).RootElement.GetProperty("tracks")[0].GetProperty("file").GetString()!;
        var last = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "projects", "proxies", track))).RootElement.GetProperty("frames").EnumerateArray().Last();
        var expected = TestVideos.SquareAt(last.GetProperty("frame").GetInt32());
        Assert.InRange(last.GetProperty("x").GetDouble(), expected.X * 9 - 27, expected.X * 9 + 27);
        Assert.InRange(last.GetProperty("y").GetDouble(), expected.Y * 9 - 27, expected.Y * 9 + 27);

        // open_project makes a proxy that is missing.
        Directory.Delete(Path.Combine(root, "projects", "proxies", "proxies"), recursive: true);
        var opened = await CallOk(client, "open_project", new() { ["nameOrPath"] = "proxies" }, ct);
        Assert.Contains("\nmaking a 1920 px proxy of 'big' in the background as proxy_2: ", opened);
        Assert.Contains("completed", await Finished("proxy_2"));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "projects", "proxies", "proxies")));
        Assert.DoesNotContain("making", await CallOk(client, "open_project", new() { ["nameOrPath"] = "proxies" }, ct));
    }

    [Fact]
    public async Task ServerRedactsATrackAndTurnsItIntoKeyframes()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-redaction");
        var square = TestVideos.MovingSquare(Path.Combine(root, "square.mp4"));
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "redaction", ["width"] = 320, ["height"] = 180, ["fps"] = 30, ["duration"] = 2.0 }, ct);
        var imported = await CallOk(client, "import_video", new() { ["path"] = square, ["id"] = "square" }, ct);
        if (imported.Contains("in the background as "))
        {
            var job = imported.Split("in the background as ")[1].Split([' ', ';', ',', '\n'])[0];
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while ((await CallOk(client, "get_job_status", new() { ["jobId"] = job }, ct)).Contains("running") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct);
            }
        }
        await CallOk(client, "add_video_layer", new() { ["id"] = "clip", ["assetId"] = "square" }, ct);
        await CallOk(client, "track_object", new() { ["layerId"] = "clip", ["time"] = 0.0, ["box"] = new Dictionary<string, object?> { ["x"] = 20, ["y"] = 60, ["width"] = 40, ["height"] = 40 }, ["method"] = "template" }, ct);
        var projectFile = Path.Combine(root, "projects", "redaction", "project.json");

        async Task<SKColor[]> Square(double time)
        {
            using var image = DecodeImage(await Call(client, "render_frame", new() { ["time"] = time, ["maxWidth"] = 320 }, ct));
            var (x, y, size) = TestVideos.SquareAt((int)Math.Round(time * 30));
            return Enumerable.Range(0, (int)size * (int)size).Select(i => TestImages.Pixel(image, (int)x + i % (int)size, (int)y + i / (int)size)).ToArray();
        }

        var original = await Square(1);
        var applied = await CallOk(client, "apply_redaction", new() { ["trackIds"] = new[] { "track_1" } }, ct);
        Assert.Equal(
            "hid track_1 with 'redaction_1': pixelate, strength 50 (12 blocks across each box), padding 15%, feather 4 px, held 15 frames before and after"
            + "\n- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('square' plays in 'clip'), so moves, effects, the camera and subject cutouts all show it hidden"
            + "\n- track_1: frames 0–59 of 'square' covered (0–1.967s on 'clip'): 60 detected, 0 filled between detections, 0 held"
            + "\n- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand",
            applied);
        var pixelated = await Square(1);
        Assert.True(pixelated.Zip(original).Count(p => p.First != p.Second) > pixelated.Length / 2, "the square isn't pixelated");
        Assert.Contains("\"type\": \"redaction\"", File.ReadAllText(projectFile));

        // Solid, then across a split: the second piece is hidden too.
        await CallOk(client, "update_layer", new() { ["layerId"] = "redaction_1", ["changes"] = new Dictionary<string, object?> { ["style"] = "solid" } }, ct);
        Assert.All(await Square(1), c => Assert.True(c.Red < 12 && c.Green < 12 && c.Blue < 12, $"{c}"));
        await CallOk(client, "split_layer", new() { ["layerId"] = "clip", ["time"] = 0.5 }, ct);
        Assert.All(await Square(1.5), c => Assert.True(c.Red < 12 && c.Green < 12 && c.Blue < 12, $"{c}"));
        Assert.Contains("redaction_1 [redaction]  hides=track_1  solid #000000 padding=15% feather=4 hold=15", await CallOk(client, "list_layers", new(), ct));

        var keyed = await CallOk(client, "tracks_to_keyframes", new() { ["trackId"] = "track_1" }, ct);
        Assert.Matches(@"^made track_1's box on 'redaction_1' keyframes: x \d+, y \d+, width \d+, height \d+ over frames 0–59 of 'square' \(source 0–1\.967s; 0–0\.467s on 'clip', 0\.5–1\.967s on 'clip_2'\)\n- the times are the footage's own seconds", keyed);
        Assert.EndsWith("clear_keyframes property \"track_1\" goes back to the box worked out from the track", keyed);
        Assert.Contains("keys: track_1.x(", await CallOk(client, "list_layers", new(), ct));
        Assert.All(await Square(1), c => Assert.True(c.Red < 12 && c.Green < 12 && c.Blue < 12, $"{c}"));

        // Moved by hand at 1 s (the footage's own second 1), the box leaves the square there.
        await CallOk(client, "set_keyframes", new() { ["entries"] = new[] { new Dictionary<string, object?> { ["layerId"] = "redaction_1", ["property"] = "track_1.x", ["time"] = 1.0, ["value"] = 250 } } }, ct);
        Assert.Contains(await Square(1), c => c.Red > 30 || c.Green > 30 || c.Blue > 30);
        var cleared = await CallOk(client, "clear_keyframes", new() { ["layerId"] = "redaction_1", ["property"] = "track_1" }, ct);
        Assert.Matches(@"^cleared \d+ keyframe\(s\) from redaction_1\.track_1$", cleared);
        Assert.All(await Square(1), c => Assert.True(c.Red < 12 && c.Green < 12 && c.Blue < 12, $"{c}"));

        Assert.EndsWith(": track 'nope' not found; tracks: track_1", Text(await Call(client, "apply_redaction", new() { ["trackIds"] = new[] { "nope" } }, ct)));
        Assert.Contains("strength must be between 0 and 100 (got 101)", Text(await Call(client, "apply_redaction", new() { ["trackIds"] = new[] { "all" }, ["strength"] = 101 }, ct)));
        Assert.Contains("'redaction_1' is a redaction of track_1: it isn't drawn or placed on the timeline itself", Text(await Call(client, "update_layer", new() { ["layerId"] = "redaction_1", ["changes"] = new Dictionary<string, object?> { ["x"] = 5 } }, ct)));
        Assert.Contains("'redaction_1' is a redaction of track_1", Text(await Call(client, "split_layer", new() { ["layerId"] = "redaction_1", ["time"] = 1.0 }, ct)));

        Assert.Contains("undid: clear_keyframes redaction_1.track_1", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: set_keyframes (1 entries)", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: tracks_to_keyframes track_1", await CallOk(client, "undo", new(), ct));
        Assert.DoesNotContain("keys: track_1", await CallOk(client, "list_layers", new(), ct));
        Assert.Contains("undid: split_layer clip", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: update_layer redaction_1", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: apply_redaction track_1", await CallOk(client, "undo", new(), ct));
        Assert.DoesNotContain("redaction_1", await CallOk(client, "list_layers", new(), ct));
        Assert.Equal(original, await Square(1));
    }

    [Fact]
    public async Task ServerAttachesATextToATrack()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-attach");
        var square = TestVideos.MovingSquare(Path.Combine(root, "square.mp4"));
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "attach", ["width"] = 320, ["height"] = 180, ["fps"] = 30, ["duration"] = 2.0 }, ct);
        var imported = await CallOk(client, "import_video", new() { ["path"] = square, ["id"] = "square" }, ct);
        if (imported.Contains("in the background as "))
        {
            var job = imported.Split("in the background as ")[1].Split([' ', ';', ',', '\n'])[0];
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while ((await CallOk(client, "get_job_status", new() { ["jobId"] = job }, ct)).Contains("running") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct);
            }
        }
        await CallOk(client, "add_video_layer", new() { ["id"] = "clip", ["assetId"] = "square" }, ct);
        await CallOk(client, "track_object", new() { ["layerId"] = "clip", ["time"] = 0.0, ["box"] = new Dictionary<string, object?> { ["x"] = 20, ["y"] = 60, ["width"] = 40, ["height"] = 40 }, ["method"] = "template" }, ct);
        await CallOk(client, "add_text_layer", new() { ["id"] = "label", ["text"] = "Hi", ["fontSize"] = 16 }, ct);
        var projectFile = Path.Combine(root, "projects", "attach", "project.json");

        var attached = await CallOk(client, "attach_to_track", new() { ["layerId"] = "label", ["trackId"] = "track_1", ["offset"] = new Dictionary<string, object?> { ["x"] = 0, ["y"] = -30 } }, ct);
        Assert.Matches(
            @"^attached 'label' to track_1: it follows the box 0–1\.967s on 'clip' and scales with it\n"
            + @"- its anchor sits 0,-30 canvas px from the box's center at 0s; the offset scales with the box\n"
            + @"- keyframes: x \d+, y \d+, scaleX \d+, scaleY \d+, kept to stay within 0\.5 px and 0\.2% of the size of every frame; outside it, its own keyframes and values apply\n"
            + @"- its size goes from 1\.00 to 1\.00 times its size at 0s\n",
            attached);
        Assert.EndsWith("\n- check it with render_frame; debug=true draws the track's box", attached);
        Assert.Contains("label [text]", await CallOk(client, "list_layers", new(), ct));

        // The text's x and y keyframes put it on the square's center, 30 px above, at every second.
        var label = JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("layers").EnumerateArray().First(l => l.GetProperty("id").GetString() == "label");
        double At(string property, double time)
        {
            var keys = label.GetProperty(property).GetProperty("keyframes").EnumerateArray().Select(k => (Time: k.GetProperty("time").GetDouble(), Value: k.GetProperty("value").GetDouble())).ToList();
            var after = keys.FindIndex(k => k.Time >= time - 1e-9);
            if (after <= 0)
            {
                return after == 0 ? keys[0].Value : keys[^1].Value;
            }

            var (a, b) = (keys[after - 1], keys[after]);
            return a.Value + (b.Value - a.Value) * (time - a.Time) / (b.Time - a.Time);
        }

        foreach (var frame in new[] { 0, 15, 30, 45, 59 })
        {
            var (x, y, size) = TestVideos.SquareAt(frame);
            Assert.InRange(At("x", frame / 30.0), x + size / 2 - 3, x + size / 2 + 3);
            Assert.InRange(At("y", frame / 30.0), y + size / 2 - 33, y + size / 2 - 27);
        }

        Assert.EndsWith(": attach_to_track moves text and image layers; 'clip' is a video layer", Text(await Call(client, "attach_to_track", new() { ["layerId"] = "clip", ["trackId"] = "track_1" }, ct)));
        Assert.EndsWith(": track 'track_9' not found; tracks: track_1", Text(await Call(client, "attach_to_track", new() { ["layerId"] = "label", ["trackId"] = "track_9" }, ct)));
        Assert.Contains("undid: attach_to_track label track_1", await CallOk(client, "undo", new(), ct));
        Assert.DoesNotContain("keyframes", JsonDocument.Parse(File.ReadAllText(projectFile)).RootElement.GetProperty("layers").EnumerateArray().First(l => l.GetProperty("id").GetString() == "label").GetProperty("x").GetRawText());
    }

    [Fact]
    public async Task ServerFindsAFaceAndHidesIt()
    {
        Ffmpeg.Require();
        var models = Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models");
        Assert.SkipWhen(!new ModelStore(models).Has(AiModels.YuNet), $"{AiModels.YuNet.FileName} isn't in {models}; skipping the face detection server test");
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-faces");

        // The portrait at 320x400, its face (about 60x85) centered near 159,81 of the 320x180 frame.
        var portrait = TestVideos.FromPhoto(TestFixtures.Portrait, Path.Combine(root, "portrait.mp4"), 1, "[0:v]scale=320:-2,crop=320:180:0:60,format=yuv420p[out]");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct, new() { ["ZEYOCUT_MODELS_DIR"] = models });
        await CallOk(client, "create_project", new() { ["name"] = "faces", ["width"] = 320, ["height"] = 180, ["fps"] = 24, ["duration"] = 1.0 }, ct);
        var imported = await CallOk(client, "import_video", new() { ["path"] = portrait, ["id"] = "portrait" }, ct);
        if (imported.Contains("in the background as "))
        {
            var importing = imported.Split("in the background as ")[1].Split([' ', ';', ',', '\n'])[0];
            while ((await CallOk(client, "get_job_status", new() { ["jobId"] = importing }, ct)).Contains("running"))
            {
                await Task.Delay(250, ct);
            }
        }

        await CallOk(client, "add_video_layer", new() { ["id"] = "guest", ["assetId"] = "portrait" }, ct);
        var started = await CallOk(client, "detect_and_track", new() { ["layerId"] = "guest", ["target"] = "faces" }, ct);
        Assert.Matches(@"^finding faces on 'guest' in the background as detect_1: finding faces on 24 frames of 'portrait' at 320x180 with YuNet on the CPU, about \d+ s \(a rough guess; system_check measures this computer\)\n- frames 0–23 of 'portrait' \(0–0\.958s on 'guest'\)\n- poll get_job_status", started);

        CallToolResult status;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (Text(status = await Call(client, "get_job_status", new() { ["jobId"] = "detect_1" }, ct)).Contains("running") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
        }

        var found = Text(status);
        Assert.Matches(@"^detect_1: completed in [\d.]+s — detect_and_track faces on 'guest' \(24 frames of 'portrait'\)\nfound 1 face in 'guest' with YuNet on the CPU: frames 0–23 of 'portrait' in [\d.]+ s \([\d.]+ frames/s\)\n- face_1: 0–0\.958s on 'guest'; found on 24 of 24 frames, average \d+x\d+ px, confidence 0\.\d\d\n- the contact sheet shows each face with its id; saved ", found);
        Assert.Contains("hide them with apply_redaction trackIds=[\"face_1\"] or [\"all\"]", found);
        using (var sheet = DecodeImage(status))
        {
            Assert.Equal((172, 212), (sheet.Width, sheet.Height));
        }

        Assert.Contains("tracks=face_1", await CallOk(client, "list_layers", new(), ct));
        Assert.Contains("\"method\": \"faces\"", File.ReadAllText(Path.Combine(root, "projects", "faces", "project.json")));

        async Task<SKColor[]> Face()
        {
            using var image = DecodeImage(await Call(client, "render_frame", new() { ["time"] = 0.5, ["maxWidth"] = 320 }, ct));
            return Enumerable.Range(0, 40 * 50).Select(i => TestImages.Pixel(image, 139 + i % 40, 56 + i / 40)).ToArray();
        }

        var face = await Face();
        Assert.Contains("hid face_1 with 'redaction_1'", await CallOk(client, "apply_redaction", new() { ["trackIds"] = new[] { "face_1" } }, ct));
        Assert.Matches(@"^redaction report: 1 track, 1 hidden; nothing to check\n- 'portrait' plays in 'guest': a redaction hides its tracks in every clip that plays it\n- face_1, hidden by 'redaction_1': frames 0–23 of 'portrait' covered \(0–0\.958s on 'guest'\): 24 detected, 0 held between detections, 0 held before or after, 0 uncovered; confidence 0\.\d+[^;]*; boxe?s? [\d–]+ px\n- nothing to check: no frame left uncovered, ",
            await CallOk(client, "redaction_report", new(), ct));
        var hidden = await Face();
        Assert.True(hidden.Zip(face).Count(p => p.First != p.Second) > hidden.Length / 2, "the face isn't pixelated");

        Assert.EndsWith(": unknown target 'cars'; valid values: faces, plates", Text(await Call(client, "detect_and_track", new() { ["layerId"] = "guest", ["target"] = "cars" }, ct)));
        Assert.EndsWith(": minConfidence must be above 0 and at most 1 (got 0)", Text(await Call(client, "detect_and_track", new() { ["layerId"] = "guest", ["target"] = "faces", ["minConfidence"] = 0.0 }, ct)));
        Assert.Contains("track 'face_1' was found on every frame by detect_and_track, not tracked from a box, so reanchor_track can't correct it",
            Text(await Call(client, "reanchor_track", new() { ["trackId"] = "face_1", ["time"] = 0.5, ["box"] = new Dictionary<string, object?> { ["x"] = 100, ["y"] = 30, ["width"] = 60, ["height"] = 80 } }, ct)));

        Assert.Contains("undid: apply_redaction face_1", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: detect_and_track faces guest", await CallOk(client, "undo", new(), ct));
        Assert.DoesNotContain("face_1", await CallOk(client, "list_layers", new(), ct));
        Assert.Equal("no tracks to report on: track_object follows an object from a box, and detect_and_track finds faces or plates", await CallOk(client, "redaction_report", new(), ct));
    }

    [Fact]
    public async Task ServerFindsPlatesAndHidesThem()
    {
        Ffmpeg.Require();
        var models = Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models");
        Assert.SkipWhen(!new ModelStore(models).Has(AiModels.Plates), $"{AiModels.Plates.FileName} isn't in {models}; skipping the plate detection server test");
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-plates");

        // The van photo at 640x426, its top 360 rows: the van's plate (69x27) at 371,285, a small one at the left edge
        // and one the right edge cuts.
        var vans = TestVideos.FromPhoto(TestFixtures.Plates, Path.Combine(root, "vans.mp4"), 1, "[0:v]scale=640:-2,crop=640:360:0:0,format=yuv420p[out]");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct, new() { ["ZEYOCUT_MODELS_DIR"] = models });
        await CallOk(client, "create_project", new() { ["name"] = "plates", ["width"] = 640, ["height"] = 360, ["fps"] = 24, ["duration"] = 1.0 }, ct);
        var imported = await CallOk(client, "import_video", new() { ["path"] = vans, ["id"] = "vans" }, ct);
        if (imported.Contains("in the background as "))
        {
            var importing = imported.Split("in the background as ")[1].Split([' ', ';', ',', '\n'])[0];
            while ((await CallOk(client, "get_job_status", new() { ["jobId"] = importing }, ct)).Contains("running"))
            {
                await Task.Delay(250, ct);
            }
        }

        await CallOk(client, "add_video_layer", new() { ["id"] = "van", ["assetId"] = "vans" }, ct);
        var started = await CallOk(client, "detect_and_track", new() { ["layerId"] = "van", ["target"] = "plates" }, ct);
        Assert.Matches(@"^finding plates on 'van' in the background as detect_1: finding plates on 24 frames of 'vans' at 640x360 with open-image-models YOLOv9-t on the CPU, about \d+ s \(a rough guess; system_check measures this computer\)\n- frames 0–23 of 'vans' \(0–0\.958s on 'van'\)\n- poll get_job_status with jobId ""detect_1""; when it is done, it adds a track for each plate as one undo step", started);

        CallToolResult status;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (Text(status = await Call(client, "get_job_status", new() { ["jobId"] = "detect_1" }, ct)).Contains("running") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
        }

        var found = Text(status);
        Assert.Matches(@"^detect_1: completed in [\d.]+s — detect_and_track plates on 'van' \(24 frames of 'vans'\)\nfound 3 plates in 'van' with open-image-models YOLOv9-t on the CPU: frames 0–23 of 'vans' in [\d.]+ s \([\d.]+ frames/s\)\n- plate_1: 0–0\.958s on 'van'; found on 24 of 24 frames, average \d+x\d+ px, confidence 0\.\d\d\n- plate_2: .*\n- plate_3: .*\n- the contact sheet shows each plate with its id; saved .*plates_van_000000-000023\.png\n", found);
        Assert.EndsWith("\n- plates are linked only by where their boxes go, not read: a car gone for more than 1 s comes back as a new track", found);
        using (var sheet = DecodeImage(status))
        {
            Assert.Equal((504, 212), (sheet.Width, sheet.Height));
        }

        Assert.Contains("tracks=plate_1/plate_2/plate_3", await CallOk(client, "list_layers", new(), ct));
        Assert.Contains("\"method\": \"plates\"", File.ReadAllText(Path.Combine(root, "projects", "plates", "project.json")));

        async Task<SKColor[]> Plate()
        {
            using var image = DecodeImage(await Call(client, "render_frame", new() { ["time"] = 0.5, ["maxWidth"] = 640 }, ct));
            return Enumerable.Range(0, 50 * 16).Select(i => TestImages.Pixel(image, 380 + i % 50, 290 + i / 50)).ToArray();
        }

        var plate = await Plate();
        Assert.Contains("hid plate_2 with 'redaction_1'", await CallOk(client, "apply_redaction", new() { ["trackIds"] = new[] { "plate_2" } }, ct));
        // The plates left unhidden are listed, and the small one is worth a look.
        var report = await CallOk(client, "redaction_report", new(), ct);
        Assert.StartsWith("redaction report: 3 tracks, 1 hidden; 1 span to check\n- 'vans' plays in 'van': a redaction hides its tracks in every clip that plays it\n", report);
        Assert.Contains("\n- plate_1, not hidden: found on frames 0–23 of 'vans' (0–0.958s on 'van'); ", report);
        Assert.Contains("; apply_redaction trackIds=[\"plate_3\"] hides it\n", report);
        Assert.Matches(@"\nto check \(render_frame with debug=true draws each track's box\):\n- plate_3, 0–0\.958s on 'van': the box is only 1\d px across, so padding 15% adds about [\d.]+ px a side and a box a few pixels off leaves an edge showing; raise paddingPercent$", report);
        Assert.StartsWith("redaction report: 1 track, 1 hidden; nothing to check\n", await CallOk(client, "redaction_report", new() { ["trackIds"] = new[] { "plate_2" } }, ct));
        Assert.EndsWith(": track 'face_9' not found; tracks: plate_1, plate_2, plate_3", Text(await Call(client, "redaction_report", new() { ["trackIds"] = new[] { "face_9" } }, ct)));
        var hidden = await Plate();
        Assert.True(hidden.Zip(plate).Count(p => p.First != p.Second) > hidden.Length / 2, "the plate isn't pixelated");

        Assert.Contains("track 'plate_1' was found on every frame by detect_and_track, not tracked from a box, so reanchor_track can't correct it; where it missed the face or plate",
            Text(await Call(client, "reanchor_track", new() { ["trackId"] = "plate_1", ["time"] = 0.5, ["box"] = new Dictionary<string, object?> { ["x"] = 0, ["y"] = 200, ["width"] = 30, ["height"] = 12 } }, ct)));
        Assert.EndsWith(": unknown method 'plates'; valid values: csrt, template, opticalFlow (detect_and_track finds faces and plates)",
            Text(await Call(client, "track_object", new() { ["layerId"] = "van", ["time"] = 0.5, ["box"] = new Dictionary<string, object?> { ["x"] = 371, ["y"] = 285, ["width"] = 69, ["height"] = 27 }, ["method"] = "plates" }, ct)));

        Assert.Contains("undid: apply_redaction plate_2", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: detect_and_track plates van", await CallOk(client, "undo", new(), ct));
        Assert.DoesNotContain("plate_1", await CallOk(client, "list_layers", new(), ct));
    }

    [Fact]
    public async Task ServerImportsAFolderInOrderAndNormalizesItsVideosInOneJob()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-folder");
        var folder = Directory.CreateDirectory(Path.Combine(root, "episode 1")).FullName;
        foreach (var (name, width) in new[] { ("panel_10.png", 30), ("panel_2.png", 20), ("panel_1.png", 10) })
        {
            TestImages.Solid(Path.Combine(folder, name), width, 16, SKColors.Teal);
        }

        TestAudio.WriteWav(Path.Combine(folder, "voice.wav"), TestAudio.Tone(1, 48000, _ => true), 48000);
        TestVideos.Halves(Path.Combine(folder, "clip.mp4"));
        TestVideos.Rotated(TestVideos.Halves(Path.Combine(root, "halves.mp4")), Path.Combine(folder, "phone.mp4"), 90);
        File.WriteAllText(Path.Combine(folder, "broken.png"), "not a picture");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "panel order");

        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "comic", ["width"] = 320, ["height"] = 240, ["fps"] = 30, ["duration"] = 4.0 }, ct);

        var reply = await CallOk(client, "import_folder", new() { ["path"] = folder }, ct);
        var lines = reply.Split('\n');
        Assert.Equal($"imported 5 files from {folder} (by name), in this order:", lines[0]);
        Assert.StartsWith("- clip [video] assets/clip.mp4 (64x48 @ 30 fps, 1s, 30 frames, h264, no sound)", lines[1]);
        Assert.Equal("- panel_1 [image] assets/panel_1.png (10x16 px)", lines[2]);
        Assert.Equal("- panel_2 [image] assets/panel_2.png (20x16 px)", lines[3]);
        Assert.Equal("- panel_10 [image] assets/panel_10.png (30x16 px)", lines[4]);
        Assert.Equal("- voice [audio] assets/voice.wav (1s)", lines[5]);
        Assert.Equal("skipped 1 file of other types: notes.txt", lines[6]);
        Assert.Equal("couldn't import 1 file:", lines[7]);
        Assert.Equal($"- broken.png: not a readable image file: {Path.Combine(folder, "broken.png")}", lines[8]);
        Assert.StartsWith("1 video needs normalizing, one after another in the background as import_1: phone.mp4 (rotated", lines[9]);
        Assert.EndsWith(". Each is added as an asset when it is done; poll get_job_status with jobId \"import_1\"", lines[9]);
        Assert.Equal(10, lines.Length);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        do
        {
            await Task.Delay(200, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "import_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("import_1: completed", status);
        Assert.Contains("import_folder: normalize 1 video from episode 1", status);
        Assert.Contains("imported 1 normalized video:\n- phone [video] assets/phone.mp4 (48x64 @ 30 fps, 1s, 30 frames, h264, no sound; normalized (rotated", status);
        Assert.Contains("phone [video]", await CallOk(client, "list_assets", new(), ct));

        Assert.Contains("undid: import_folder phone.mp4", await CallOk(client, "undo", new(), ct));
        Assert.Contains("undid: import_folder episode 1 (5 files)", await CallOk(client, "undo", new(), ct));
        Assert.Equal("(no assets; use import_asset)", await CallOk(client, "list_assets", new(), ct));

        Assert.Equal($"imported 2 files from {folder} ('panel_1*', by name), in this order:\n- panel_1 [image] assets/panel_1.png (10x16 px)\n- panel_10 [image] assets/panel_10.png (30x16 px)",
            await CallOk(client, "import_folder", new() { ["path"] = folder, ["pattern"] = "panel_1*" }, ct));
        Assert.EndsWith($": no file in {folder} matches '*.psd'", Text(await Call(client, "import_folder", new() { ["path"] = folder, ["pattern"] = "*.psd" }, ct)));
        Assert.EndsWith($": nothing was imported from {folder}; no file could be read:\n- broken.png: not a readable image file: {Path.Combine(folder, "broken.png")}",
            Text(await Call(client, "import_folder", new() { ["path"] = folder, ["pattern"] = "broken*" }, ct)));
    }

    [Fact]
    public async Task ServerAddsChangesAndRemovesMarkers()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-markers");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "chapters", ["width"] = 320, ["height"] = 240, ["fps"] = 10, ["duration"] = 30.0 }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Hello", ["id"] = "title" }, ct);

        static Dictionary<string, object?> Marker(double time, string name, string? kind = null, string? id = null) =>
            new() { ["time"] = time, ["name"] = name, ["kind"] = kind, ["id"] = id };

        Assert.Equal(
            "added 3 markers: intro, the_chase, outro\nmarkers (3):\n- intro chapter at 0s \"Intro\"\n- the_chase scene at 12s \"The chase\"\n- outro chapter at 20s \"Outro\"",
            await CallOk(client, "add_markers", new() { ["markers"] = new[] { Marker(0, "Intro"), Marker(12, "The chase", "scene"), Marker(20, "Outro") } }, ct));
        Assert.Equal(
            "changed outro\nmarkers (3):\n- intro chapter at 0s \"Intro\"\n- the_chase scene at 12s \"The chase\"\n- outro chapter at 25s \"Credits\"",
            await CallOk(client, "add_markers", new() { ["markers"] = new[] { Marker(25, "Credits", id: "outro") } }, ct));
        Assert.EndsWith(": marker 2 (\"Late\") is at 31s; markers go from 0 to the project end (30s)",
            Text(await Call(client, "add_markers", new() { ["markers"] = new[] { Marker(5, "Fine"), Marker(31, "Late") } }, ct)));

        var chapters = Path.Combine(root, "projects", "chapters", "exports", "chapters_chapters.txt");
        Assert.Equal(
            $"wrote 2 chapters to {chapters} (1 scene marker left out); paste them into the video's description:\n0:00 Intro\n0:25 Credits\nWarnings:\n- YouTube needs at least 3 chapters; there are 2\n- 'outro' (\"Credits\") lasts 5s from 0:25; YouTube needs at least 10 s per chapter",
            await CallOk(client, "export_markers", new(), ct));
        Assert.Equal("0:00 Intro\n0:25 Credits\n", File.ReadAllText(chapters));
        Assert.StartsWith($"wrote 2 chapters to {Path.Combine(root, "projects", "chapters", "exports", "youtube.txt")} (", await CallOk(client, "export_markers", new() { ["fileName"] = "youtube" }, ct));
        Assert.EndsWith(": export_markers writes .txt files (got '.srt')", Text(await Call(client, "export_markers", new() { ["fileName"] = "chapters.srt" }, ct)));

        Assert.Contains("\nmarkers: intro chapter at 0s \"Intro\"; the_chase scene at 12s \"The chase\"; outro chapter at 25s \"Credits\"\n",
            (await CallOk(client, "list_layers", new(), ct)).ReplaceLineEndings("\n"));
        using (var document = JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)))
        {
            var markers = document.RootElement.GetProperty("markers");
            Assert.Equal(["intro", "the_chase", "outro"], markers.EnumerateArray().Select(m => m.GetProperty("id").GetString()));
            Assert.Equal("scene", markers[1].GetProperty("kind").GetString());
        }

        await CallOk(client, "remove_range", new() { ["start"] = 5.0, ["end"] = 10.0 }, ct);
        Assert.Contains("markers: intro chapter at 0s \"Intro\"; the_chase scene at 7s \"The chase\"; outro chapter at 20s \"Credits\"", await CallOk(client, "list_layers", new(), ct));
        Assert.Contains("undid: remove_range all 5–10", await CallOk(client, "undo", new(), ct));

        Assert.Equal("removed 1 marker: the_chase\nmarkers (2):\n- intro chapter at 0s \"Intro\"\n- outro chapter at 25s \"Credits\"",
            await CallOk(client, "remove_markers", new() { ["kind"] = "scene" }, ct));
        Assert.EndsWith(": marker 'nope' not found; markers: intro, outro", Text(await Call(client, "remove_markers", new() { ["markerIds"] = new[] { "nope" } }, ct)));
        Assert.Equal("removed 2 markers: intro, outro\nno markers", await CallOk(client, "remove_markers", new() { ["markerIds"] = new[] { "all" } }, ct));
        Assert.EndsWith(": the project has no markers; add chapters with add_markers", Text(await Call(client, "export_markers", new(), ct)));
        Assert.DoesNotContain("markers:", await CallOk(client, "list_layers", new(), ct));
        Assert.Contains("undid: remove_markers all", await CallOk(client, "undo", new(), ct));
        Assert.Contains("markers: intro chapter at 0s", await CallOk(client, "list_layers", new(), ct));
    }

    [Fact]
    public async Task ServerBuildsAnEpisodeFromAScript()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-script");
        var projects = Path.Combine(root, "projects");
        Core.ScriptTests.MakeLibrary(Path.Combine(root, "library"));
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);

        const string markdown = """
            # Jump
            width: 320
            height: 240
            fps: 10

            ## Ledge (2s)
            - rig tora as hero at 160,200
            - action hop on hero at 0.5
            - sfx impacts/boing at 1
            - caption 0-1.5: Here goes!
            - action flip at 1

            ## Fall (1s)
            - rig tora
            """;
        Assert.Equal(
            $"created and opened project 'Jump' at {Path.Combine(projects, "Jump")} from the script\n" +
            "320x240 @ 10 fps, 3s, 2 scenes:\n" +
            "- scene 1 \"Ledge\" 0–2s: rig tora as hero_body; hop on hero_body 0.5–1.5s; impacts/boing at 1s as boing; 1 caption\n" +
            "- scene 2 \"Fall\" 2–3s: rig tora as tora_body\n" +
            "captions: 1 under 'captions' (style subtitle)\n" +
            "couldn't map 1 thing; the rest was built:\n" +
            "- line 11 (scene 'Ledge'): no action named 'flip' in the library; actions: hop\n" +
            "next: render_contact_sheet to check the scenes; undo takes the build out and leaves the empty project",
            await CallOk(client, "build_from_script", new() { ["script"] = markdown }, ct));

        var layers = await CallOk(client, "list_layers", new(), ct);
        Assert.Contains("markers: ledge scene at 0s \"Ledge\"; fall scene at 2s \"Fall\"", layers);
        Assert.Contains("hero_body [image]", layers);
        Assert.Contains("t=0–2", layers);
        Assert.Contains("captions_1 [text]  \"Here goes!\"", layers);
        Assert.Contains("undid: build_from_script", await CallOk(client, "undo", new(), ct));
        Assert.Contains("(no layers)", await CallOk(client, "list_layers", new(), ct));
        Assert.EndsWith($": a project already exists in '{Path.Combine(projects, "Jump")}'; give another name or directory",
            Text(await Call(client, "build_from_script", new() { ["script"] = markdown }, ct)));

        var file = Path.Combine(root, "drop.json");
        File.WriteAllText(file, """
            { "title": "Fall", "width": 320, "height": 240, "fps": 10, "background": "#000000",
              "scenes": [ { "name": "Drop", "duration": 1.5, "rigs": [ { "rig": "tora" } ], "actions": [ { "action": "hop", "at": 0 } ], "music": "epic" } ] }
            """);
        Assert.Equal(
            $"created and opened project 'Episode 2' at {Path.Combine(projects, "Episode 2")} from {file}\n" +
            "320x240 @ 10 fps, 1.5s, 1 scene:\n" +
            "- scene 1 \"Drop\" 0–1.5s: rig tora as tora_body; hop on tora_body 0–1s\n" +
            "couldn't map 1 thing; the rest was built:\n" +
            "- scene 1 'Drop': unknown field 'music'\n" +
            "next: render_contact_sheet to check the scenes; undo takes the build out and leaves the empty project",
            await CallOk(client, "build_from_script", new() { ["scriptFile"] = file, ["name"] = "Episode 2" }, ct));
        using (var document = JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)))
        {
            Assert.Equal("#000000", document.RootElement.GetProperty("backgroundColor").GetString());
            Assert.Equal(1.5, document.RootElement.GetProperty("duration").GetDouble());
        }

        Assert.EndsWith(": give scriptFile or script, not both", Text(await Call(client, "build_from_script", new() { ["scriptFile"] = file, ["script"] = markdown }, ct)));
        Assert.EndsWith(": the script has no scenes; a Markdown scene starts with '## Scene name (8s)', a JSON one is an entry of \"scenes\"",
            Text(await Call(client, "build_from_script", new() { ["script"] = "# Pilot\nJust an idea.", ["name"] = "pilot" }, ct)));
        Assert.False(Directory.Exists(Path.Combine(projects, "pilot")));
    }

    [Fact]
    public async Task ServerTagsLayersWithLanguagesAndShowsEachVersion()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-languages");
        var projects = Path.Combine(root, "projects");
        var clip = TestVideos.Pattern(Path.Combine(root, "talk.mp4"), seconds: 1, width: 160, height: 120, withAudio: true);
        var red = TestImages.Solid(Path.Combine(root, "title_ja.png"), 100, 60, SKColors.Red);
        var blue = TestImages.Solid(Path.Combine(root, "title_en.png"), 100, 60, SKColors.Blue);
        var dub = TestAudio.WriteWav(Path.Combine(root, "dub_ja.wav"), TestAudio.Tone(1, 48000, _ => true), 48000);

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "bilingual", ["width"] = 320, ["height"] = 240, ["fps"] = 10, ["duration"] = 1.0 }, ct);
        await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
        foreach (var file in new[] { red, blue, dub })
        {
            await CallOk(client, "import_asset", new() { ["path"] = file }, ct);
        }

        Assert.Contains("\"language\":\"en\"", await CallOk(client, "add_video_layer", new() { ["assetId"] = "talk", ["language"] = "EN" }, ct));
        Assert.Contains("\"language\":\"ja\"", await CallOk(client, "add_audio_layer", new() { ["asset"] = "dub_ja", ["role"] = "voice", ["language"] = "ja" }, ct));
        await CallOk(client, "add_image_layer", new() { ["asset"] = "title_ja", ["id"] = "title_ja", ["x"] = 160, ["y"] = 60, ["language"] = "ja" }, ct);
        await CallOk(client, "add_image_layer", new() { ["id"] = "titles_en", ["x"] = 0, ["y"] = 0, ["language"] = "en" }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "title_en", ["id"] = "title_en", ["parentId"] = "titles_en", ["x"] = 160, ["y"] = 60 }, ct);
        Assert.EndsWith(": language 'english' isn't a language code; use one such as en, ja, my or pt-BR",
            Text(await Call(client, "add_text_layer", new() { ["text"] = "Hi", ["language"] = "english" }, ct)));

        var tree = await CallOk(client, "list_layers", new(), ct);
        Assert.Contains("languages: en, ja", tree);
        Assert.Contains("lang=en (sound)", tree);
        Assert.Contains("title_ja [image]  z=1  t=0–end  lang=ja", tree);
        Assert.Contains("dub_ja [audio]  t=0–end  lang=ja", tree);

        Assert.EndsWith(": layer 'title_en': its language ja differs from en on 'titles_en', which it is part of; a child is only in its parent's version, so tag one of them or give it another parent",
            Text(await Call(client, "update_layer", new() { ["layerId"] = "title_en", ["changes"] = new Dictionary<string, object?> { ["language"] = "ja" } }, ct)));
        Assert.Contains("\"language\":\"pt-BR\"", await CallOk(client, "update_layer", new() { ["layerId"] = "title_ja", ["changes"] = new Dictionary<string, object?> { ["language"] = "pt_br" } }, ct));
        Assert.DoesNotContain("language", await CallOk(client, "update_layer", new() { ["layerId"] = "title_ja", ["changes"] = new Dictionary<string, object?> { ["language"] = "" } }, ct));
        await CallOk(client, "update_layer", new() { ["layerId"] = "title_ja", ["changes"] = new Dictionary<string, object?> { ["language"] = "ja" } }, ct);

        async Task<(string Text, SKColor Title)> Frame(string? language)
        {
            var arguments = new Dictionary<string, object?> { ["time"] = 0.5, ["maxWidth"] = 320 };
            if (language is not null)
            {
                arguments["language"] = language;
            }

            var result = await Call(client, "render_frame", arguments, ct);
            Assert.False(result.IsError == true, Text(result));
            using var image = DecodeImage(result);
            return (Text(result), TestImages.Pixel(image, 160, 60));
        }

        var every = await Frame(null);
        Assert.Contains("\nWarnings:\n- this shows the layers of every language (en, ja); give language to see one version", every.Text);
        Assert.Equal(SKColors.Blue, every.Title);
        var japanese = await Frame("ja");
        Assert.Contains("; ja version, 2 layer(s) of other languages left out, sound of 'talk' muted; image 320x240", japanese.Text);
        Assert.DoesNotContain("Warnings", japanese.Text);
        Assert.Equal(SKColors.Red, japanese.Title);
        var english = await Frame("en");
        Assert.Contains("; en version, 2 layer(s) of other languages left out; image 320x240", english.Text);
        Assert.Equal(SKColors.Blue, english.Title);
        var french = await Frame("fr");
        Assert.Contains("- no layer is tagged fr, so this shows only the untagged layers; languages in the project: en, ja", french.Text);
        Assert.NotEqual(SKColors.Red, french.Title);
        Assert.NotEqual(SKColors.Blue, french.Title);
        Assert.EndsWith(": language 'english' isn't a language code; use one such as en, ja, my or pt-BR", Text(await Call(client, "render_frame", new() { ["time"] = 0.5, ["language"] = "english" }, ct)));
        Assert.EndsWith(": 'title_ja' isn't in the en version; leave out language to draw it", Text(await Call(client, "render_frame", new() { ["time"] = 0.5, ["layerId"] = "title_ja", ["language"] = "en" }, ct)));

        Assert.Contains("; ja version, 2 layer(s) of other languages left out, sound of 'talk' muted", await CallOk(client, "render_contact_sheet", new() { ["count"] = 2, ["language"] = "ja" }, ct));
        var thumbnail = await Call(client, "export_thumbnail", new() { ["time"] = 0.5, ["language"] = "en" }, ct);
        Assert.Contains("; preview 960x540; en version, 2 layer(s) of other languages left out", Text(thumbnail));

        async Task<string> Exported(string started)
        {
            var jobId = System.Text.RegularExpressions.Regex.Match(started, "^started (\\S+):").Groups[1].Value;
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(180);
            do
            {
                await Task.Delay(250, ct);
                status = await CallOk(client, "get_export_status", new() { ["jobId"] = jobId }, ct);
            }
            while (status.Contains("running") && DateTime.UtcNow < deadline);

            Assert.Contains(": completed", status);
            return status;
        }

        var all = await CallOk(client, "start_export", new() { ["fileName"] = "all.mp4" }, ct);
        Assert.EndsWith("\nWarnings:\n- this export includes the layers of every language (en, ja); give language for one version, or export_language_versions for one file per language", all);
        Assert.Contains("(2 clip(s) mixed)", await Exported(all));

        var ja = await CallOk(client, "start_export", new() { ["fileName"] = "ja.mp4", ["language"] = "ja" }, ct);
        Assert.Contains(" frames (320x240 @ 10 fps; ja version, 2 layer(s) of other languages left out, sound of 'talk' muted) -> ", ja);
        Assert.DoesNotContain("Warnings", ja);
        Assert.Contains("(1 clip(s) mixed)", await Exported(ja));
        var pixels = Ffmpeg.ExtractFrameRgba(ffmpeg, Path.Combine(projects, "bilingual", "exports", "ja.mp4"), 5, 320, 240);
        var at = (60 * 320 + 160) * 4;
        Assert.True(pixels[at] > 200 && pixels[at + 1] < 60 && pixels[at + 2] < 60, $"rgb {pixels[at]},{pixels[at + 1]},{pixels[at + 2]}");
    }

    [Fact]
    public async Task ServerGivesEachLanguageItsFontAndFindsMissingGlyphs()
    {
        Fonts.Require("Myanmar Text", "Yu Gothic", "Segoe UI");
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-language-fonts");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "fonts", ["width"] = 640, ["height"] = 360, ["fps"] = 10, ["duration"] = 1.0 }, ct);

        var burmese = await CallOk(client, "add_text_layer", new() { ["text"] = "မင်္ဂလာပါ", ["id"] = "hello_my", ["language"] = "my" }, ct);
        Assert.Contains("\"fontFamily\":\"Myanmar Text\"", burmese);
        Assert.DoesNotContain("Warnings", burmese);
        var japanese = await CallOk(client, "add_text_layer", new() { ["text"] = "こんにちは", ["id"] = "hello_ja", ["language"] = "ja-jp" }, ct);
        Assert.Contains("\"fontFamily\":\"Yu Gothic\"", japanese);
        Assert.Contains("\"language\":\"ja-JP\"", japanese);
        Assert.Contains("\"fontFamily\":\"Segoe UI\"", await CallOk(client, "add_text_layer", new() { ["text"] = "Hello", ["id"] = "hello_en", ["language"] = "en" }, ct));

        var wrong = await CallOk(client, "add_text_layer", new() { ["text"] = "မင်္ဂလာပါ", ["id"] = "wrong", ["language"] = "my", ["fontFamily"] = "Segoe UI" }, ct);
        Assert.Contains("\"fontFamily\":\"Segoe UI\"", wrong);
        Assert.Contains("\nWarnings:\n- font 'Segoe UI' has no glyphs for \"မင်္ဂလာပါ\"; pick a font that covers this script", wrong);

        var check = await CallOk(client, "check_project", new(), ct);
        Assert.Contains("glyphs a font lacks (1):\n- text 'wrong' (my): font 'Segoe UI' has no glyphs for \"မင်္ဂလာပါ\", which show as empty boxes; the my font is \"Myanmar Text\" (update_layer fontFamily)", check);
        await CallOk(client, "update_layer", new() { ["layerId"] = "wrong", ["changes"] = new Dictionary<string, object?> { ["fontFamily"] = "Myanmar Text" } }, ct);
        Assert.DoesNotContain("glyphs a font lacks (", await CallOk(client, "check_project", new(), ct));
    }

    [Fact]
    public async Task ServerAddsACaptionTrackPerLanguage()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-caption-tracks");
        var projects = Path.Combine(root, "projects");
        var clip = TestVideos.Pattern(Path.Combine(root, "talk.mp4"), seconds: 4, width: 160, height: 120, withAudio: true);
        var englishSrt = Path.Combine(root, "talk en.srt");
        File.WriteAllText(englishSrt, "1\r\n00:00:00,500 --> 00:00:02,000\r\nHello, everyone.\r\n");

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "tracks", ["width"] = 640, ["height"] = 360, ["fps"] = 10, ["duration"] = 4.0 }, ct);
        await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
        await CallOk(client, "add_video_layer", new() { ["assetId"] = "talk" }, ct);
        string assetFile;
        using (var document = JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)))
        {
            assetFile = document.RootElement.GetProperty("assets")[0].GetProperty("file").GetString()!;
        }

        global::ZeyoCut.Core.Serialization.AnalysisFiles.Save(global::ZeyoCut.Core.Serialization.AnalysisFiles.TranscriptPath(Path.Combine(projects, "tracks"), new global::ZeyoCut.Core.Model.Asset { Id = "talk", File = assetFile }), new global::ZeyoCut.Core.Model.Transcript
        {
            Asset = "talk",
            Language = "en",
            Words =
            [
                new() { Text = "Hello", Start = 0.2, End = 0.6 },
                new() { Text = "world.", Start = 0.7, End = 1.2 },
                new() { Text = "How", Start = 2.0, End = 2.3 },
                new() { Text = "are", Start = 2.3, End = 2.5 },
                new() { Text = "you?", Start = 2.5, End = 2.9 },
            ],
        });

        var english = await CallOk(client, "add_caption_track", new() { ["language"] = "EN", ["fromTranscript"] = "talk" }, ct);
        Assert.StartsWith("added the en caption track 'captions_en': 2 caption(s), font Segoe UI\n- 0.2–1.5s \"Hello world.\"\n- 2–3.2s \"How are you?\"\n", english);
        Assert.DoesNotContain("Warnings", english);

        var mismatched = await CallOk(client, "add_caption_track", new() { ["language"] = "ja", ["fromTranscript"] = "talk" }, ct);
        Assert.EndsWith("\nWarnings:\n- the transcript of 'talk' is in en, but these captions are tagged ja", mismatched);
        var japanese = await CallOk(client, "add_caption_track", new()
        {
            ["language"] = "ja",
            ["cues"] = new object[]
            {
                new Dictionary<string, object?> { ["start"] = 0.2, ["end"] = 1.5, ["text"] = "こんにちは、世界。" },
                new Dictionary<string, object?> { ["start"] = 2.0, ["end"] = 3.2, ["text"] = "お元気ですか？" },
                new Dictionary<string, object?> { ["start"] = 3.5, ["end"] = 6, ["text"] = "またね" },
            },
        }, ct);
        Assert.StartsWith("replaced the ja caption track 'captions_ja': 3 caption(s) (removed 2 old caption layer(s)), font Yu Gothic\n", japanese);
        Assert.EndsWith("\nWarnings:\n- cue 3 (\"またね\") runs past the project end (4s), so it ends there", japanese);

        var burmese = await CallOk(client, "add_caption_track", new() { ["language"] = "my", ["srt"] = "1\n00:00:00,200 --> 00:00:01,500\nမင်္ဂလာပါ\n\n2\n00:00:02,000 --> 00:00:03,200\nနေကောင်းလား\n" }, ct);
        Assert.StartsWith("added the my caption track 'captions_my': 2 caption(s), font Myanmar Text\n- 0.2–1.5s \"မင်္ဂလာပါ\"\n", burmese);
        Assert.DoesNotContain("Warnings", burmese);

        var fromFile = await CallOk(client, "add_caption_track", new() { ["language"] = "en", ["srt"] = englishSrt }, ct);
        Assert.StartsWith("replaced the en caption track 'captions_en': 1 caption(s) (removed 2 old caption layer(s)), font Segoe UI\n- 0.5–2s \"Hello, everyone.\"", fromFile);

        Assert.EndsWith(": which language are the captions in? 'talk en.srt' doesn't end with one (as talk.ja.srt does); give language, e.g. en, ja, my or pt-BR",
            Text(await Call(client, "import_srt", new() { ["path"] = englishSrt }, ct)));
        Assert.StartsWith("imported talk en.srt\nreplaced the en caption track 'captions_en': 1 caption(s) (removed 1 old caption layer(s)), font Segoe UI\n- 0.5–2s \"Hello, everyone.\"",
            await CallOk(client, "import_srt", new() { ["path"] = englishSrt, ["language"] = "en" }, ct));
        var brazilianSrt = Path.Combine(root, "talk.pt-BR.srt");
        File.WriteAllText(brazilianSrt, "1\n00:00:01,000 --> 00:00:02,500\nOlá, pessoal.\n");
        Assert.StartsWith("imported talk.pt-BR.srt (language pt-BR from the file name; give language to change it)\nadded the pt-BR caption track 'captions_pt-BR': 1 caption(s), font Segoe UI\n- 1–2.5s \"Olá, pessoal.\"",
            await CallOk(client, "import_srt", new() { ["path"] = brazilianSrt }, ct));
        Assert.Contains("undid: add_caption_track pt-BR", await CallOk(client, "undo", new(), ct));
        Assert.EndsWith($": import_srt reads .srt subtitle files (got 'talk.mp4')", Text(await Call(client, "import_srt", new() { ["path"] = clip, ["language"] = "en" }, ct)));
        Assert.Contains("srt file not found: ", Text(await Call(client, "import_srt", new() { ["path"] = Path.Combine(root, "missing.ja.srt") }, ct)));

        Assert.EndsWith(": give only one of cues, srt and fromTranscript", Text(await Call(client, "add_caption_track", new() { ["language"] = "en", ["srt"] = englishSrt, ["fromTranscript"] = "talk" }, ct)));
        Assert.EndsWith(": give the captions as cues, srt or fromTranscript", Text(await Call(client, "add_caption_track", new() { ["language"] = "en" }, ct)));
        Assert.Contains("srt file not found: ", Text(await Call(client, "add_caption_track", new() { ["language"] = "en", ["srt"] = Path.Combine(root, "missing.srt") }, ct)));
        Assert.EndsWith(": none of the cues can be used:\n- cue 1 (\"Too late\") starts at 5s, at or after the project end (4s), so it was left out",
            Text(await Call(client, "add_caption_track", new() { ["language"] = "fr", ["cues"] = new object[] { new Dictionary<string, object?> { ["start"] = 5, ["end"] = 6, ["text"] = "Too late" } } }, ct)));

        var tree = await CallOk(client, "list_layers", new(), ct);
        Assert.Contains("languages: en, ja, my", tree);
        Assert.Contains("captions_ja [image]  \"captions (ja)\"", tree);
        Assert.Contains("lang=ja  null (no drawings)", tree);
        Assert.Contains("; ja version, 5 layer(s) of other languages left out; image", Text(await Call(client, "render_frame", new() { ["time"] = 0.5, ["language"] = "ja" }, ct)));
        Assert.Contains("wrote 3 subtitle(s) to ", await CallOk(client, "export_srt", new() { ["fileName"] = "talk_ja.srt", ["groupId"] = "captions_ja" }, ct));
        Assert.Contains("こんにちは、世界。", File.ReadAllText(Path.Combine(projects, "tracks", "exports", "talk_ja.srt")));
    }

    [Fact]
    public async Task ServerExportsEachLanguageVersionWithItsOwnVoiceAndCaptions()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-language-versions");
        var projects = Path.Combine(root, "projects");
        var voices = new Dictionary<string, int> { ["en"] = 440, ["ja"] = 880 };
        foreach (var (language, frequency) in voices)
        {
            ZeyoCut.Mcp.Demo.ToneGenerator.WriteTone(Path.Combine(root, $"voice_{language}.wav"), frequency, 1.0, 0.3);
        }

        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "hello", ["width"] = 320, ["height"] = 180, ["fps"] = 10, ["duration"] = 1.0 }, ct);
        foreach (var (language, color, text) in new[] { ("en", "#FF0000", "Hello"), ("ja", "#0000FF", "こんにちは") })
        {
            await CallOk(client, "import_asset", new() { ["path"] = Path.Combine(root, $"voice_{language}.wav") }, ct);
            await CallOk(client, "add_audio_layer", new() { ["asset"] = $"voice_{language}", ["role"] = "voice", ["language"] = language }, ct);
            await CallOk(client, "add_caption_track", new()
            {
                ["language"] = language,
                ["style"] = "bold_center",
                ["color"] = color,
                ["cues"] = new object[] { new Dictionary<string, object?> { ["start"] = 0, ["end"] = 1, ["text"] = text } },
            }, ct);
        }

        Assert.EndsWith(": languages must list at least one language, e.g. [\"en\", \"ja\"]", Text(await Call(client, "export_language_versions", new() { ["languages"] = Array.Empty<string>(), ["presets"] = new[] { "youtube_1080p" } }, ct)));
        Assert.EndsWith(": language 'ja' is listed twice", Text(await Call(client, "export_language_versions", new() { ["languages"] = new[] { "ja", "JA" }, ["presets"] = new[] { "youtube_1080p" } }, ct)));
        Assert.EndsWith(": unknown preset 'tiktok_4k'; presets: youtube_1080p, shorts_1080p", Text(await Call(client, "export_language_versions", new() { ["languages"] = new[] { "en" }, ["presets"] = new[] { "tiktok_4k" } }, ct)));
        Assert.EndsWith("\nWarnings:\n- these exports include the layers of every language (en, ja); export_language_versions writes one file per language and preset",
            await CallOk(client, "export_batch", new() { ["presets"] = new[] { "youtube_1080p" }, ["fileName"] = "mixed" }, ct));

        var exports = Path.Combine(projects, "hello", "exports");
        string FileOf(string language, string preset) => Path.Combine(exports, $"hello_{language}_{preset}.mp4");
        var started = await CallOk(client, "export_language_versions", new() { ["languages"] = new[] { "en", "ja" }, ["presets"] = new[] { "youtube_1080p", "shorts_1080p" } }, ct);
        Assert.Equal(
            "started versions_1: 4 export(s) of 10 frames @ 10 fps, one after another\n"
            + "- en version, 3 layer(s) of other languages left out\n"
            + "- ja version, 3 layer(s) of other languages left out\n"
            + $"- en youtube_1080p: 1920x1080 -> {FileOf("en", "youtube_1080p")}\n"
            + $"- en shorts_1080p: 1080x1920, the 320x180 canvas fitted over a blurred copy -> {FileOf("en", "shorts_1080p")}\n"
            + $"- ja youtube_1080p: 1920x1080 -> {FileOf("ja", "youtube_1080p")}\n"
            + $"- ja shorts_1080p: 1080x1920, the 320x180 canvas fitted over a blurred copy -> {FileOf("ja", "shorts_1080p")}\n"
            + "poll get_job_status with jobId \"versions_1\"",
            started);

        string status;
        var deadline = DateTime.UtcNow.AddSeconds(240);
        do
        {
            await Task.Delay(250, ct);
            status = await CallOk(client, "get_job_status", new() { ["jobId"] = "versions_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("versions_1: completed", status);
        Assert.Contains("exported 4 file(s):", status);
        Assert.Contains($"- ja shorts_1080p: {FileOf("ja", "shorts_1080p")} — 1.000s, 1080x1920 @ 10 fps, h264 yuv420p", status);
        Assert.Contains($"- batch_1: completed", await CallOk(client, "get_job_status", new(), ct));

        foreach (var (language, frequency) in voices)
        {
            var other = voices.Single(v => v.Key != language).Value;
            foreach (var (preset, width, height) in new[] { ("youtube_1080p", 1920, 1080), ("shorts_1080p", 1080, 1920) })
            {
                var file = FileOf(language, preset);
                var own = ZeyoCut.Tests.Export.DeliveryExportTests.BandLevel(ffmpeg, file, 0.2, 0.6, frequency);
                var others = ZeyoCut.Tests.Export.DeliveryExportTests.BandLevel(ffmpeg, file, 0.2, 0.6, other);
                Assert.True(own > others + 20, $"{Path.GetFileName(file)}: {frequency} Hz at {own:0.0} dB, {other} Hz at {others:0.0} dB");

                var pixels = Ffmpeg.ExtractFrameRgba(ffmpeg, file, 5, width, height);
                var (red, blue) = (0, 0);
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    red += pixels[i] > 200 && pixels[i + 1] < 60 && pixels[i + 2] < 60 ? 1 : 0;
                    blue += pixels[i] < 60 && pixels[i + 1] < 60 && pixels[i + 2] > 200 ? 1 : 0;
                }

                var (captions, wrong) = language == "en" ? (red, blue) : (blue, red);
                Assert.True(captions > 300 && wrong < 50, $"{Path.GetFileName(file)}: {red} red and {blue} blue caption pixels");
            }
        }
    }

    [Fact]
    public async Task ServerReportsWhereAnExportsTimeWent()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-render-stats");
        var projects = Path.Combine(root, "projects");
        var logs = Path.Combine(root, "logs");
        var clip = TestVideos.Pattern(Path.Combine(root, "pattern.mp4"), seconds: 1);
        var client = await StartServer(root, projects, logs, ct);
        try
        {
            Assert.EndsWith(": no export has finished yet; report_render_stats reports a finished start_export, export_batch or export_language_versions", Text(await Call(client, "report_render_stats", new(), ct)));
            await CallOk(client, "create_project", new() { ["name"] = "stats", ["width"] = 320, ["height"] = 180, ["fps"] = 10, ["duration"] = 1.0 }, ct);
            await CallOk(client, "import_video", new() { ["path"] = clip }, ct);
            await CallOk(client, "add_video_layer", new() { ["assetId"] = "pattern" }, ct);
            await CallOk(client, "add_text_layer", new() { ["text"] = "Hi", ["id"] = "title", ["fontSize"] = 40, ["end"] = 0.5 }, ct);

            Assert.StartsWith("started export_1:", await CallOk(client, "start_export", new() { ["fileName"] = "stats.mp4" }, ct));
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while ((status = await CallOk(client, "get_export_status", new() { ["jobId"] = "export_1" }, ct)).Contains("running") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }

            Assert.StartsWith("export_1: completed", status);
            var stats = await CallOk(client, "report_render_stats", new(), ct);
            Assert.StartsWith("export_1 (stats.mp4): 10 frames at 320x180 in ", stats);
            Assert.Contains("\n- decoding video and images: ", stats);
            Assert.Contains("\nslowest layers (each with its own decoding, mattes, effects and drawing):\n- pattern [video]: ", stats);
            Assert.Matches(@"\nslowest frames: f\d+ \([\d.]+ ms\)(, f\d+ \([\d.]+ ms\)){4}$", stats);
            Assert.DoesNotContain("fitting the canvas", stats);

            Assert.StartsWith("started batch_1:", await CallOk(client, "export_batch", new() { ["presets"] = new[] { "shorts_1080p" } }, ct));
            deadline = DateTime.UtcNow.AddSeconds(120);
            while ((status = await CallOk(client, "get_job_status", new() { ["jobId"] = "batch_1" }, ct)).Contains("running") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }

            Assert.Contains("batch_1: completed", status);
            var batch = await CallOk(client, "report_render_stats", new() { ["jobId"] = "batch_1" }, ct);
            Assert.StartsWith("batch_1: 1 file(s), export_batch shorts_1080p\nstats_shorts_1080p.mp4: 10 frames at 1080x1920 in ", batch);
            Assert.Contains("\n- fitting the canvas into the preset: ", batch);

            Assert.Contains(" as frames_1.", await CallOk(client, "export_frames", new() { ["startTime"] = 0.0, ["endTime"] = 0.0 }, ct));
            Assert.EndsWith(": frames_1 isn't an export; report_render_stats reports start_export (export_N), export_batch (batch_N) and export_language_versions (versions_N) jobs",
                Text(await Call(client, "report_render_stats", new() { ["jobId"] = "frames_1" }, ct)));
            Assert.EndsWith(": export job 'export_9' not found; finished exports: batch_1, export_1",
                Text(await Call(client, "report_render_stats", new() { ["jobId"] = "export_9" }, ct)));
        }
        finally
        {
            await client.DisposeAsync();
        }

        // The stats are kept with the jobs, so the next server still reports them.
        var pid = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "jobs", "export_1.json"))).RootElement.GetProperty("processId").GetInt32();
        try
        {
            using var stopped = System.Diagnostics.Process.GetProcessById(pid);
            await stopped.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }

        await using var again = await StartServer(root, projects, logs, ct);
        Assert.StartsWith("batch_1: 1 file(s), export_batch shorts_1080p\nstats_shorts_1080p.mp4: ", await CallOk(again, "report_render_stats", new(), ct));
        var earlier = await CallOk(again, "report_render_stats", new() { ["jobId"] = "export_1" }, ct);
        Assert.StartsWith("export_1 (stats.mp4): 10 frames at 320x180 in ", earlier);
        Assert.Contains("\n- pattern [video]: ", earlier);
    }

    [Fact]
    public async Task ServerLeavesDescribeClipOutUntilAProviderLoads()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-describer");
        var logs = Path.Combine(root, "logs");
        var environment = new Dictionary<string, string?>
        {
            ["ZEYOCUT_DESCRIBER_PROVIDER"] = "gemini",
            ["ZEYOCUT_DESCRIBER_API_KEY"] = "test-key",
        };

        await using (var client = await StartServer(root, Path.Combine(root, "projects"), logs, ct, environment))
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            Assert.Equal(ExpectedTools.Order(), tools.Select(t => t.Name).Order());
        }

        var logFile = Assert.Single(Directory.GetFiles(logs, "zeyocut-*.jsonl"));
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var log = reader.ReadToEnd();
        Assert.Contains("describe_clip is off: there is no clip describer named 'gemini' in this build (available: none yet)", log);
        Assert.DoesNotContain("test-key", log);
    }

    [Fact]
    public async Task ServerUpdatesAnOlderProjectWhenItOpensIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-migrate");
        var projects = Path.Combine(root, "projects");
        Directory.CreateDirectory(Path.Combine(projects, "tora"));
        File.Copy(TestFixtures.OldProject(1), Path.Combine(projects, "tora", "project.json"));
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);

        var opened = await CallOk(client, "open_project", new() { ["nameOrPath"] = "tora" }, ct);
        Assert.Matches(@"\nupdated project\.json from format 1 \(saved by an older ZeyoCut\) to 6; the file as it was is kept as backups\\project-v1-\d{8}-\d{6}\.json\n", opened);
        Assert.Contains("tora_head", opened);
        Assert.Single(Directory.GetFiles(Path.Combine(projects, "tora", "backups"), "project-v1-*.json"));
        Assert.Contains("\"formatVersion\": 6", await CallOk(client, "get_project", new(), ct));

        Assert.DoesNotContain("updated project.json", await CallOk(client, "open_project", new() { ["nameOrPath"] = "tora" }, ct));
    }

    [Fact]
    public async Task ServerKeepsBackupsAndRestoresOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-backups");
        var projects = Path.Combine(root, "projects");
        var file = Path.Combine(projects, "backups", "project.json");
        await using (var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct))
        {
            await CallOk(client, "create_project", new() { ["name"] = "backups", ["width"] = 320, ["height"] = 180, ["duration"] = 2.0 }, ct);
            Assert.StartsWith("no backups yet in ", await CallOk(client, "restore_backup", new(), ct));

            await CallOk(client, "update_project", new() { ["duration"] = 5.0 }, ct);
            await CallOk(client, "add_text_layer", new() { ["text"] = "Hello", ["id"] = "title" }, ct);
            var list = await CallOk(client, "restore_backup", new(), ct);
            Assert.StartsWith("2 backup(s) in ", list);
            Assert.Matches(@"\n- project-\d{8}-\d{6}-\d{3} before add_text_layer title: \d{4}-\d\d-\d\d \d\d:\d\d:\d\d; 0 layer\(s\), 5s\n", list);
            var time = System.Text.RegularExpressions.Regex.Match(list, @"- project-(\d{8}-\d{6}-\d{3}) before update_project: .*; 0 layer\(s\), 2s\n").Groups[1].Value;
            Assert.NotEmpty(time);
            Assert.EndsWith("restore one with restore_backup backup=\"<name>\" (one undo step)", list);

            var restored = await CallOk(client, "restore_backup", new() { ["backup"] = time }, ct);
            Assert.StartsWith($"restored project-{time} before update_project (", restored);
            Assert.Contains("); undo puts back what was there\n", restored);
            var project = JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)).RootElement;
            Assert.Equal(2, project.GetProperty("duration").GetDouble());
            Assert.Equal(0, project.GetProperty("layers").GetArrayLength());

            Assert.StartsWith($"undid: restore_backup project-{time} ", await CallOk(client, "undo", new(), ct));
            Assert.Contains("title", await CallOk(client, "list_layers", new(), ct));
            Assert.Contains("no backup 'nope'", Text(await Call(client, "restore_backup", new() { ["backup"] = "nope" }, ct)));
        }

        File.WriteAllText(file, "{ \"formatVersion\": 6, \"name\": ");
        await using (var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct))
        {
            var broken = await Call(client, "open_project", new() { ["nameOrPath"] = "backups" }, ct);
            Assert.True(broken.IsError == true);
            Assert.Matches(@"invalid project JSON.*\nthe newest backup is backups\\project-\d{8}-\d{6}-\d{3} before undo restore_backup project-\d{8}-\d{6}-\d{3}\.json \(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\); copy it over project\.json to go back to it", Text(broken));
        }
    }

    [Fact]
    public async Task ServerStartsAKilledExportAgainAndFailsOtherKilledJobs()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-killed");
        var projects = Path.Combine(root, "projects");
        var logs = Path.Combine(root, "logs");
        var record = Path.Combine(root, "jobs", "export_1.json");
        var client = await StartServer(root, projects, logs, ct);
        try
        {
            await CallOk(client, "create_project", new() { ["name"] = "killed", ["width"] = 640, ["height"] = 360, ["fps"] = 30, ["duration"] = 30.0 }, ct);
            await CallOk(client, "add_text_layer", new() { ["text"] = "Frame", ["id"] = "label", ["fontSize"] = 48 }, ct);
            await CallOk(client, "set_keyframes", new() { ["entries"] = new object[] { new Dictionary<string, object> { ["layerId"] = "label", ["property"] = "x", ["time"] = 30.0, ["value"] = 600 } } }, ct);
            Assert.StartsWith("started export_1: 900 frames", await CallOk(client, "start_export", new() { ["fileName"] = "long" }, ct));
            Assert.Contains(" as frames_1.", await CallOk(client, "export_frames", new() { ["startTime"] = 0.0, ["endTime"] = 29.9 }, ct));

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!System.Text.RegularExpressions.Regex.IsMatch(await CallOk(client, "get_export_status", new() { ["jobId"] = "export_1" }, ct), @"running \(rendering\) [\d.]+% \([1-9]\d*/900 frames\)"))
            {
                Assert.True(DateTime.UtcNow < deadline, "export_1 didn't start rendering");
                await Task.Delay(50, ct);
            }

            Assert.Contains("frames_1: running", await CallOk(client, "get_job_status", new() { ["jobId"] = "frames_1" }, ct));
            var pid = JsonDocument.Parse(File.ReadAllText(record)).RootElement.GetProperty("processId").GetInt32();
            using var server = System.Diagnostics.Process.GetProcessById(pid);
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(ct);
        }
        finally
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception)
            {
                // The server is gone.
            }
        }

        await using (var again = await StartServer(root, projects, logs, ct))
        {
            var status = await CallOk(again, "get_export_status", new() { ["jobId"] = "export_1" }, ct);
            Assert.Contains("; started again after the server restarted (attempt 2 of 3)", status);
            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (status.Contains("running") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
                status = await CallOk(again, "get_export_status", new() { ["jobId"] = "export_1" }, ct);
            }

            Assert.StartsWith("export_1: completed; started again after the server restarted (attempt 2 of 3)\n", status.ReplaceLineEndings("\n"));
            Assert.Contains("video: h264 yuv420p, 900 frames", status);
            Assert.Matches(@"^frames_1: failed after \d+\.\ds — export_frames f0–f897 as png: the server stopped while this job ran( \(.*\))?, so it didn't finish; start it again$",
                await CallOk(again, "get_job_status", new() { ["jobId"] = "frames_1" }, ct));
            var listed = await CallOk(again, "get_job_status", new(), ct);
            Assert.Contains("- frames_1: failed", listed);
            Assert.Contains("- export_1: completed", listed);
        }

        await using (var third = await StartServer(root, projects, logs, ct))
        {
            var listed = await CallOk(third, "get_job_status", new(), ct);
            Assert.Equal("earlier sessions, newest first: export_1 (completed), frames_1 (failed); get_job_status with a jobId shows one", listed);
            var status = (await CallOk(third, "get_export_status", new() { ["jobId"] = "export_1" }, ct)).ReplaceLineEndings("\n");
            Assert.StartsWith("export_1: completed; started again after the server restarted (attempt 2 of 3)\noutput: ", status);
            Assert.Contains("\nvideo: h264 yuv420p, 900 frames\n", status);

            await CallOk(third, "open_project", new() { ["nameOrPath"] = "killed" }, ct);
            Assert.StartsWith("started export_2:", await CallOk(third, "start_export", new() { ["fileName"] = "again" }, ct));
        }
    }

    [Fact]
    public async Task ServerSaysWhichEncoderAnExportUsed()
    {
        Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-encoder");
        var projects = Path.Combine(root, "projects");
        var box = TestImages.Solid(Path.Combine(root, "box.png"), 40, 40, SKColors.Blue);
        var tests = Path.Combine(root, "library", ZeyoCut.Render.Export.VideoEncoders.FileName);

        async Task<string> Export(McpClient client, string id)
        {
            await CallOk(client, "start_export", new() { ["fileName"] = id }, ct);
            string status;
            var deadline = DateTime.UtcNow.AddSeconds(120);
            do
            {
                await Task.Delay(200, ct);
                status = await CallOk(client, "get_export_status", new() { ["jobId"] = id }, ct);
            }
            while (status.Contains("running") && DateTime.UtcNow < deadline);

            Assert.Contains($"{id}: completed", status);
            return status;
        }

        await using (var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct))
        {
            await CallOk(client, "create_project", new() { ["name"] = "encoder", ["width"] = 320, ["height"] = 240, ["fps"] = 10, ["duration"] = 1.0 }, ct);
            await CallOk(client, "import_asset", new() { ["path"] = box }, ct);
            await CallOk(client, "add_image_layer", new() { ["asset"] = "box" }, ct);
            Assert.Matches(@"\nprocessing: encoder: libx264 \(CPU, CRF 18\): the VideoEncoder setting is x264\r?\n", await Export(client, "export_1"));
            Assert.False(File.Exists(tests));
        }

        await using (var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct, new() { ["ZEYOCUT_VIDEO_ENCODER"] = "auto" }))
        {
            await CallOk(client, "open_project", new() { ["nameOrPath"] = "encoder" }, ct);
            var status = await Export(client, "export_2");
            Assert.Matches(@"\nprocessing: encoder: (h264_(nvenc|qsv|amf) \([^)]+\): the first GPU encoder that works on this computer|libx264 \(CPU, CRF 18\): no GPU encoder works on this computer \(nvenc: .+; qsv: .+; amf: .+\))\r?\n", status);
            Assert.True(File.Exists(tests));
            var tested = File.GetLastWriteTimeUtc(tests);
            var again = await Export(client, "export_3");
            Assert.Equal(System.Text.RegularExpressions.Regex.Match(status, "processing: encoder: [^\r\n]*").Value, System.Text.RegularExpressions.Regex.Match(again, "processing: encoder: [^\r\n]*").Value);
            Assert.Equal(tested, File.GetLastWriteTimeUtc(tests));
        }
    }

    internal static async Task<McpClient> StartServer(string workingDirectory, string projects, string logs, CancellationToken ct, Dictionary<string, string?>? environment = null)
    {
        var variables = new Dictionary<string, string?>
        {
            ["ZEYOCUT_PROJECTS_DIR"] = projects,
            ["ZEYOCUT_LOG_DIR"] = logs,
            ["ZEYOCUT_LIBRARY_DIR"] = Path.Combine(workingDirectory, "library"),
            ["ZEYOCUT_JOBS_DIR"] = Path.Combine(workingDirectory, "jobs"),

            // The test media is made under TestOutput, outside the default import folders.
            ["ZEYOCUT_IMPORT_DIRS"] = TestPaths.Root,

            // libx264 gives the same frames on every computer; ServerSaysWhichEncoderAnExportUsed tries auto.
            ["ZEYOCUT_VIDEO_ENCODER"] = "x264",
        };
        foreach (var (name, value) in environment ?? [])
        {
            variables[name] = value;
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "zeyocut",
            Command = Path.Combine(AppContext.BaseDirectory, "ZeyoCut.Mcp.exe"),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = variables,
        });
        // The server/discover probe waits 5 s by default; a server that takes longer to start while the
        // suite runs in parallel would get the initialize fallback, which a 2026-07-28 server refuses.
        return await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan }, cancellationToken: ct);
    }

    internal static async Task<CallToolResult> Call(McpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken ct) =>
        await client.CallToolAsync(tool, arguments, cancellationToken: ct);

    internal static async Task<string> CallOk(McpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await Call(client, tool, arguments, ct);
        var text = Text(result);
        Assert.False(result.IsError == true, $"{tool} failed: {text}");
        return text;
    }

    internal static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    internal static SKImage DecodeImage(CallToolResult result)
    {
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        return SKImage.FromEncodedData(image.DecodedData.ToArray())!;
    }
}
