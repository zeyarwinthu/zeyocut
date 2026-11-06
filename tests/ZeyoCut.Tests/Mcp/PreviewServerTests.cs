// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using ZeyoCut.Mcp;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;
using static ZeyoCut.Tests.Mcp.McpServerTests;

namespace ZeyoCut.Tests.Mcp;

public class PreviewServerTests
{
    [Theory]
    [InlineData(null, null, 0, 999)]
    [InlineData("bytes=100-199", true, 100, 199)]
    [InlineData("bytes=900-", true, 900, 999)]
    [InlineData("bytes=-100", true, 900, 999)]
    [InlineData("bytes=-5000", true, 0, 999)]
    [InlineData("bytes=500-5000", true, 500, 999)]
    [InlineData("bytes=1000-", false, 0, 999)]
    [InlineData("bytes=-0", false, 0, 999)]
    [InlineData("bytes=0-1,5-9", null, 0, 999)]
    [InlineData("bytes=9-5", null, 0, 999)]
    [InlineData("bytes=x-5", null, 0, 999)]
    [InlineData("items=0-5", null, 0, 999)]
    public void RangeReadsOneByteRange(string? header, bool? expected, long start, long end)
    {
        Assert.Equal(expected, PreviewServer.Range(header, 1000, out var from, out var to));
        Assert.Equal((start, end), (from, to));
    }

    [Fact]
    public async Task ServerShowsThePreviewPage()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-preview");
        await using var client = await StartServer(root, Path.Combine(root, "projects"), Path.Combine(root, "logs"), ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var started = await CallOk(client, "preview_server", new(), ct);
        var url = Regex.Match(started, @"http://127\.0\.0\.1:\d+/[0-9a-f]{32}/").Value;
        Assert.StartsWith($"preview page at {url}\nopen it in a browser on this computer. No project is open, so it shows none until open_project or create_project opens one; then it shows its frames at up to 640 px, about 15 a second while playing, with the sound mixed as the export mixes it", started);
        Assert.Equal("{\"open\":false}", await http.GetStringAsync(url + "state", ct));

        await CallOk(client, "create_project", new() { ["name"] = "page", ["width"] = 320, ["height"] = 240, ["fps"] = 10, ["duration"] = 2.0, ["backgroundColor"] = "#FF0000" }, ct);
        var tone = TestAudio.WriteWav(Path.Combine(root, "tone.wav"), TestAudio.Tone(2, 48000, _ => true), 48000);
        await CallOk(client, "import_asset", new() { ["path"] = tone, ["id"] = "tone" }, ct);
        await CallOk(client, "add_audio_layer", new() { ["asset"] = "tone" }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Hi", ["id"] = "title", ["start"] = 0.5, ["end"] = 1.5 }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "!", ["id"] = "mark", ["parentId"] = "title" }, ct);
        Assert.Equal($"the preview page is already running at {url}", await CallOk(client, "preview_server", new() { ["action"] = "start" }, ct));

        // The page allows only its own script, by hash, and nothing lets other origins read it.
        using (var page = await http.GetAsync(url, ct))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
            Assert.False(page.Headers.Contains("Access-Control-Allow-Origin"));
            var html = await page.Content.ReadAsStringAsync(ct);
            var script = html[(html.IndexOf("<script>", StringComparison.Ordinal) + 8)..html.IndexOf("</script>", StringComparison.Ordinal)];
            Assert.Contains($"default-src 'none'; script-src 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}';", page.Headers.GetValues("Content-Security-Policy").Single());
        }

        var port = new Uri(url).Port;
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/{new string('0', 32)}/state", ct)).StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Get, url + "state"))
        {
            request.Headers.Host = $"evil.example:{port}";
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(request, ct)).StatusCode);
        }

        using (var post = await http.PostAsync(url + "state", new StringContent("{}"), ct))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            Assert.Equal("the preview page only shows the project; edit it with ZeyoCut's tools", await post.Content.ReadAsStringAsync(ct));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(url + "state?language=x_1", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(url + "frame?n=first", ct)).StatusCode);

        string revision;
        using (var state = JsonDocument.Parse(await http.GetStringAsync(url + "state", ct)))
        {
            var s = state.RootElement;
            revision = s.GetProperty("revision").GetString()!;
            Assert.Equal((20, 2.0), (s.GetProperty("frames").GetInt32(), s.GetProperty("duration").GetDouble()));
            Assert.Equal("{\"width\":320,\"height\":240,\"step\":1}", s.GetProperty("preview").GetRawText());
            Assert.Equal(
                "tone audio 0-2 depth 0|title text 0.5-1.5 depth 0 Hi|mark text 0.5-1.5 depth 1 !",
                string.Join("|", s.GetProperty("layers").EnumerateArray().Select(l =>
                    $"{l.GetProperty("id")} {l.GetProperty("type")} {l.GetProperty("start")}-{l.GetProperty("end")} depth {l.GetProperty("depth")}{(l.GetProperty("text").ValueKind == JsonValueKind.String ? $" {l.GetProperty("text")}" : "")}")));
        }

        using (var frame = SKBitmap.Decode(await http.GetByteArrayAsync(url + "frame?n=0", ct)))
        {
            Assert.Equal((320, 240), (frame.Width, frame.Height));
            var pixel = frame.GetPixel(5, 5);
            Assert.True(pixel.Red > 230 && pixel.Green < 30 && pixel.Blue < 30, pixel.ToString());
        }

        // An edit gives a new revision, and the frames show it.
        await CallOk(client, "update_project", new() { ["backgroundColor"] = "#0000FF" }, ct);
        using (var state = JsonDocument.Parse(await http.GetStringAsync(url + "state", ct)))
        {
            Assert.NotEqual(revision, state.RootElement.GetProperty("revision").GetString());
        }

        using (var frame = SKBitmap.Decode(await http.GetByteArrayAsync(url + "frame?n=0", ct)))
        {
            var pixel = frame.GetPixel(5, 5);
            Assert.True(pixel.Blue > 230 && pixel.Red < 30 && pixel.Green < 30, pixel.ToString());
        }

        // The mix is made in the background and can be read from any byte.
        JsonElement audio;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            await Task.Delay(200, ct);
            using var state = JsonDocument.Parse(await http.GetStringAsync(url + "state", ct));
            audio = state.RootElement.GetProperty("audio").Clone();
        }
        while (audio.GetProperty("status").GetString() == "making" && DateTime.UtcNow < deadline);

        Assert.Equal("ready", audio.GetProperty("status").GetString());
        var key = audio.GetProperty("key").GetString()!;
        var file = Path.Combine(root, "projects", "page", "cache", "preview", $"mix-{key}.m4a");
        Assert.Equal(2.0, Ffprobe.AudioDuration(ffmpeg.Ffprobe, file), 2);
        var mix = await http.GetByteArrayAsync(url + $"audio?k={key}", ct);
        Assert.Equal(File.ReadAllBytes(file), mix);
        using (var request = new HttpRequestMessage(HttpMethod.Get, url + $"audio?k={key}"))
        {
            request.Headers.Range = new RangeHeaderValue(100, 199);
            using var part = await http.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
            Assert.Equal($"bytes 100-199/{mix.Length}", part.Content.Headers.ContentRange?.ToString());
            Assert.Equal(mix[100..200], await part.Content.ReadAsByteArrayAsync(ct));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url + "audio?k=0123456789abcdef", ct)).StatusCode);

        Assert.Equal($"stopped the preview page at {url}", await CallOk(client, "preview_server", new() { ["action"] = "stop" }, ct));
        using (var fresh = new HttpClient())
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => fresh.GetStringAsync(url + "state", ct));
        }

        Assert.Equal("the preview page isn't running", await CallOk(client, "preview_server", new() { ["action"] = "stop" }, ct));
        Assert.EndsWith(": action must be start or stop (got 'pause')", Text(await Call(client, "preview_server", new() { ["action"] = "pause" }, ct)));

        var again = await CallOk(client, "preview_server", new(), ct);
        Assert.DoesNotContain(url, again);
        Assert.Contains("\nopen it in a browser on this computer. It shows 'page' (320x240 @ 10 fps) as 320x240 frames, 10 a second while playing, with the sound mixed as the export mixes it (made in the background), and follows every edit within a second.", again);
    }
}
