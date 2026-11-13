// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;
using static ZeyoCut.Tests.Mcp.McpServerTests;

namespace ZeyoCut.Tests.Mcp;

// The manual editor's acceptance test, scripted: Claude builds a video through the real server, the
// user fine-tunes it in the editor (its document from Core, which the window's commands use), Claude
// keeps working on top of that, undo takes back only each side's own edits, and the export has both.
public partial class EditorAcceptanceTests
{
    private static Dictionary<string, object?> Cue(double start, double end, string text) => new() { ["start"] = start, ["end"] = end, ["text"] = text };

    private static double Peak(string levels) => double.Parse(WholePeak().Match(levels).Groups[1].Value, CultureInfo.InvariantCulture);

    private static double SectionLufs(string levels, string section) =>
        double.Parse(Regex.Match(levels, $@"\n- {section}s: ([-+]?\d+\.\d) LUFS").Groups[1].Value, CultureInfo.InvariantCulture);

    private static AudioLayer Music(string directory) => (AudioLayer)ProjectStore.Load(directory).FindLayer("music")!;

    [Fact]
    public async Task ClaudeBuildsTheUserFineTunesAndBothReachTheExport()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("editor-acceptance");
        var projects = Path.Combine(root, "projects");
        var directory = Path.Combine(projects, "street");
        var red = TestImages.Solid(Path.Combine(root, "red.png"), 320, 180, SKColors.Red);
        var green = TestImages.Solid(Path.Combine(root, "green.png"), 320, 180, SKColors.Lime);
        var music = Path.Combine(root, "music.wav");
        ZeyoCut.Mcp.Demo.ToneGenerator.WriteTone(music, 440, 6, 0.5);
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);

        // 1. Claude builds it: two stills, music and English captions. Loudness normalization is off,
        // so the music's volume shows in the levels.
        await CallOk(client, "create_project", new() { ["name"] = "street", ["width"] = 320, ["height"] = 180, ["fps"] = 10, ["duration"] = 6.0, ["backgroundColor"] = "#000000" }, ct);
        await CallOk(client, "update_project", new() { ["loudnessTarget"] = 0.0 }, ct);
        foreach (var file in new[] { red, green, music })
        {
            await CallOk(client, "import_asset", new() { ["path"] = file }, ct);
        }

        await CallOk(client, "add_image_layer", new() { ["asset"] = "red", ["id"] = "red", ["start"] = 0.0, ["end"] = 3.0 }, ct);
        await CallOk(client, "add_image_layer", new() { ["asset"] = "green", ["id"] = "green", ["start"] = 3.0, ["end"] = 6.0 }, ct);
        await CallOk(client, "add_audio_layer", new() { ["asset"] = "music", ["id"] = "music" }, ct);
        await CallOk(client, "add_caption_track", new() { ["language"] = "en", ["cues"] = new[] { Cue(0.2, 1.8, "Helo street"), Cue(3.6, 4.8, "Green light") } }, ct);
        var before = Peak(await CallOk(client, "audio_levels", new() { ["sectionSeconds"] = 1.0 }, ct));

        // 2. The user fine-tunes it in the editor: trims the red still, moves the green one up to it and
        // splits it, fixes a caption's typo and lowers the music with a fade at the end. Each edit is
        // saved as it is made.
        var editor = EditorDocument.Open(directory);
        var typo = editor.View.Layers.OfType<TextLayer>().Single(l => l.Text == "Helo street").Id;
        Assert.True(editor.Apply(new TrimLayerOperation("red", null, 2)), editor.Status);
        Assert.True(editor.Apply(new MoveLayerOperation("green", 2)), editor.Status);
        Assert.True(editor.Apply(new SplitLayerOperation("green", 3.5)), editor.Status);
        Assert.True(editor.Apply(new UpdateLayerOperation(typo, new JsonObject { ["text"] = "Hello street" })), editor.Status);
        Assert.True(editor.Apply(new UpdateLayerOperation("music", new JsonObject { ["volume"] = 0.25, ["fadeOut"] = 1.0 })), editor.Status);
        Assert.StartsWith("Saved", editor.Status);
        var second = editor.View.Layers.Single(l => l.Id.StartsWith("green", StringComparison.Ordinal) && l.Id != "green").Id;

        // 3. Claude adds Japanese captions. Its reply first says what the user changed, and the
        // user's edits stay.
        var added = await CallOk(client, "add_caption_track", new() { ["language"] = "ja", ["cues"] = new[] { Cue(2.2, 3.4, "こんにちは") } }, ct);
        Assert.StartsWith("project.json was changed outside this server since your last call", added);
        Assert.Contains("editor trim_layer red", added);
        Assert.Contains("editor update_layer music", added);
        Assert.Contains("added the ja caption track 'captions_ja'", added);
        var saved = ProjectStore.Load(directory);
        Assert.Equal((0.0, 2.0), (saved.FindLayer("red")!.Start, saved.FindLayer("red")!.End!.Value));
        Assert.Equal((2.0, 3.5), (saved.FindLayer("green")!.Start, saved.FindLayer("green")!.End!.Value));
        Assert.Equal((3.5, 5.0), (saved.FindLayer(second)!.Start, saved.FindLayer(second)!.End!.Value));
        Assert.Equal("Hello street", ((TextLayer)saved.FindLayer(typo)!).Text);
        Assert.Equal((0.25, 1.0), (Music(directory).Volume, Music(directory).FadeOut));
        Assert.NotNull(saved.FindLayer("captions_ja"));

        // The editor shows Claude's captions and names the edit.
        Assert.True(editor.Refresh());
        Assert.Equal(["add_caption_track ja"], editor.TakeOutsideChanges()!.Edits);
        Assert.NotNull(editor.View.FindLayer("captions_ja"));

        // 4. Undo in the editor takes back the user's last edit and keeps Claude's captions; redo puts
        // it back. Claude's undo takes back only Claude's captions, and its redo puts them back.
        Assert.True(editor.Undo(), editor.Status);
        Assert.Equal((1.0, 0.0), (Music(directory).Volume, Music(directory).FadeOut));
        Assert.NotNull(ProjectStore.Load(directory).FindLayer("captions_ja"));
        Assert.True(editor.Redo(), editor.Status);
        Assert.Equal(0.25, Music(directory).Volume);
        Assert.Contains("undid: add_caption_track ja", await CallOk(client, "undo", new(), ct));
        Assert.Null(ProjectStore.Load(directory).FindLayer("captions_ja"));
        Assert.Equal(("Hello street", 0.25), (((TextLayer)ProjectStore.Load(directory).FindLayer(typo)!).Text, Music(directory).Volume));
        Assert.Contains("redid: add_caption_track ja", await CallOk(client, "redo", new(), ct));
        Assert.True(editor.Refresh());
        Assert.NotNull(editor.View.FindLayer("captions_ja"));

        // 5. The export has every edit: its frames are the ones render_frame draws (the red still to
        // 2 s, the green one moved up to it and split, black after it), and the music is 12 dB
        // quieter, fading out over the last second.
        Assert.Contains("started export_1", await CallOk(client, "start_export", new() { ["fileName"] = "street.mp4" }, ct));
        string status;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        do
        {
            await Task.Delay(200, ct);
            status = await CallOk(client, "get_export_status", new() { ["jobId"] = "export_1" }, ct);
        }
        while (status.Contains("running") && DateTime.UtcNow < deadline);

        Assert.Contains("export_1: completed", status);
        var output = Path.Combine(directory, ProjectStore.ExportsFolder, "street.mp4");
        foreach (var (time, color) in new[] { (1.0, SKColors.Red), (2.5, SKColors.Lime), (4.0, SKColors.Lime), (5.5, SKColors.Black) })
        {
            using var frame = DecodeImage(await Call(client, "render_frame", new() { ["time"] = time }, ct));
            Assert.Equal(color, TestImages.Pixel(frame, 160, 40));
            var error = Ffmpeg.MeanAbsoluteError(TestImages.Pixels(frame), Ffmpeg.ExtractFrameRgba(ffmpeg, output, (int)Math.Round(time * 10), 320, 180));
            Assert.True(error < 3, $"the exported frame at {time}s differs from render_frame by {error:0.00}/255 on average");
        }

        var levels = await CallOk(client, "audio_levels", new() { ["sectionSeconds"] = 1.0 }, ct);
        Assert.InRange(before - Peak(levels), 11, 13);
        Assert.True(SectionLufs(levels, "5–6") < SectionLufs(levels, "4–5") - 3, levels);

        // 6. Opened again, the project is as it was left.
        var reopened = EditorDocument.Open(directory);
        Assert.Equal(ProjectJson.Serialize(editor.View), ProjectJson.Serialize(reopened.View));
        Assert.Equal("Hello street", ((TextLayer)reopened.View.FindLayer(typo)!).Text);
    }

    [GeneratedRegex(@"integrated, peak ([-+]?\d+\.\d) dBFS")]
    private static partial Regex WholePeak();
}
