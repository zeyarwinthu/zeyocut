// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class AiMatteTests
{
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Red = new(255, 0, 0);

    // The models the smoke tests use, when they are installed (ZEYOCUT_MODELS_DIR or library\models).
    private static ModelStore InstalledModels() =>
        new(Environment.GetEnvironmentVariable("ZEYOCUT_MODELS_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "library", "models"));

    private static string RequireModel(AiModel model)
    {
        var store = InstalledModels();
        Assert.SkipWhen(!store.Has(model), $"{model.FileName} isn't in {store.Folder}; skipping the AI smoke test");
        return store.PathOf(model);
    }

    // A 2 s TestVideos.GreenScreen clip (the red box at x = 8 + 4n in frame n) as layer 'clip'.
    private static ProjectSession GreenScreen(string name, VideoLayerOptions? options = null)
    {
        Ffmpeg.Require();
        var directory = TestPaths.NewDirectory(name);
        var file = TestVideos.GreenScreen(Path.Combine(directory, "screen.mp4"), seconds: 2);
        var session = ProjectSession.Create(Path.Combine(directory, "project"), TestProjects.Empty(width: 96, height: 64, fps: 10, duration: 1));
        session.Mutate("background", p => ProjectEditor.UpdateProject(p, new ProjectOptions { BackgroundColor = "#0000FF" }));
        TestVideos.Import(session, file, "screen");
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, options ?? new VideoLayerOptions { Id = "clip", Asset = "screen" }));
        session.Mutate("matte", p => MatteEditor.Set(p, "clip", MatteEditor.Build(MatteEditor.GetMattable(p, "clip"), MatteMethod.AiVideo, new MatteValues(null, null, null, null))));
        return session;
    }

    private static string MattePath(ProjectSession session)
    {
        var project = session.Snapshot();
        var layer = (VideoLayer)project.FindLayer("clip")!;
        return MatteFiles.CachePath(session.Directory, project.FindAsset(layer.Asset)!, layer.Matte!);
    }

    // What an AI matte of the GreenScreen clip should be: white exactly over the box, in every frame.
    private static void WriteBoxMatte(string path)
    {
        var ffmpeg = Ffmpeg.Require();
        var result = ProcessRunner.Run(
            ffmpeg.Ffmpeg,
            [
                "-v", "error", "-y", "-f", "lavfi",
                "-i", "color=c=black:s=96x64:r=10:d=2,format=gray,geq=lum='if(between(X,8+N*4,31+N*4)*between(Y,16,39),255,0)'",
                "-c:v", "ffv1", "-g", "1", "-pix_fmt", "gray", "-f", "matroska", path,
            ],
            TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [Fact]
    public void ModelsDownloadOnceAndBadDownloadsAreThrownAway()
    {
        var folder = TestPaths.NewDirectory("ai-models");
        var body = new byte[3 * 1024 * 1024 + 17];
        new Random(4).NextBytes(body);
        var sha = Convert.ToHexStringLower(SHA256.HashData(body));
        using var server = new TinyServer(new Dictionary<string, byte[]> { ["/model.onnx"] = body });
        var store = new ModelStore(folder);
        var good = new AiModel("test", "test.onnx", server.Url("/model.onnx"), body.Length, sha, "Test model", "MIT");

        var progress = new List<long>();
        var path = store.Ensure(good, (done, total) => progress.Add(done), TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(folder, "test.onnx"), path);
        Assert.Equal(body, File.ReadAllBytes(path));
        Assert.Equal(body.Length, progress[^1]);
        Assert.True(progress.Count > 1);
        Assert.True(store.Has(good));
        Assert.Equal(1, server.Requests);
        store.Ensure(good, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, server.Requests);

        var altered = good with { FileName = "altered.onnx", Sha256 = new string('0', 64) };
        var mismatch = Assert.Throws<ZeyoCutException>(() => store.Ensure(altered, null, TestContext.Current.CancellationToken));
        Assert.StartsWith($"the download of altered.onnx from 127.0.0.1 doesn't match its checksum (SHA-256 {sha[..12]}…, expected 000000000000…), so it was deleted; check the internet connection, or download {altered.Url} yourself and save it as {Path.Combine(folder, "altered.onnx")}", mismatch.Message);

        var shorter = good with { FileName = "short.onnx", Bytes = body.Length + 5 };
        Assert.StartsWith($"the download of short.onnx from 127.0.0.1 has {body.Length} bytes instead of {body.Length + 5}", Assert.Throws<ZeyoCutException>(() => store.Ensure(shorter, null, TestContext.Current.CancellationToken)).Message);

        var gone = good with { FileName = "gone.onnx", Url = server.Url("/gone.onnx") };
        Assert.StartsWith("could not download gone.onnx from 127.0.0.1: HTTP 404 Not Found", Assert.Throws<ZeyoCutException>(() => store.Ensure(gone, null, TestContext.Current.CancellationToken)).Message);
        Assert.Equal(["test.onnx"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void AiMatteFilesDependOnTheMethodModelAndFile()
    {
        var session = GreenScreen("ai-matte-files");
        var path = MattePath(session);
        Assert.Matches(@"[\\/]assets[\\/]screen\.matte-aivideo-[0-9a-f]{8}\.mkv$", path);
        var project = session.Snapshot();
        var asset = project.FindAsset("screen")!;
        Assert.NotEqual(path, MatteFiles.CachePath(session.Directory, asset, new Matte { Method = MatteMethod.Chroma, Color = "#00FF00", Tolerance = 0.4 }));
        Assert.EndsWith(".png", MatteFiles.CachePath(session.Directory, asset, new Matte { Method = MatteMethod.AiImage }));
        Assert.Equal((1920, 1080), MatteFiles.VideoMatteSize(new Asset { Id = "uhd", Type = AssetType.Video, File = "uhd.mp4", Width = 3840, Height = 2160 }));
        Assert.Equal((1080, 1920), MatteFiles.VideoMatteSize(new Asset { Id = "tall", Type = AssetType.Video, File = "tall.mp4", Width = 1080, Height = 1920 }));
        Assert.Equal((96, 64), MatteFiles.VideoMatteSize(asset));
    }

    [Fact]
    public void UntilTheMatteIsComputedPreviewsWarnExportsRefuseAndTheCheckSaysSo()
    {
        var session = GreenScreen("ai-matte-pending");
        var project = session.Snapshot();
        var pending = Assert.Single(MatteFiles.Pending(project, session.Directory));
        Assert.Equal(("clip", "screen", MatteMethod.AiVideo), (pending.LayerId, pending.AssetId, pending.Method));

        using (var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg))
        {
            using var image = renderer.RenderImage(project, 0);
            Assert.Equal(new SKColor(40, 190, 60), TestImages.Pixel(image, 60, 50), new ColorComparer(3));
            Assert.Equal(
                "layer 'clip': the aiVideo matte of 'screen' isn't computed yet, so it is drawn with its background; get_job_status shows its matte job, or run remove_background again",
                Assert.Single(renderer.TakeWarnings()));
        }

        var refusal = "background removal isn't finished: layer 'clip' (aiVideo matte of 'screen'); wait for its matte job (get_job_status) or run remove_background again, then export";
        Assert.Equal(refusal, Assert.Throws<ZeyoCutException>(() => MatteFiles.ThrowIfPending(project, session.Directory)).Message);
        var output = Path.Combine(session.Directory, "exports", "out.mp4");
        Assert.Equal(refusal, Assert.Throws<ZeyoCutException>(() => Exporter.Export(project, session.Directory, output, 18, Ffmpeg.Require(), cancellationToken: TestContext.Current.CancellationToken)).Message);
        Assert.Equal(refusal, Assert.Throws<ZeyoCutException>(() => new ExportJobManager().Start(project, session.Directory, output, 18, Ffmpeg.Require())).Message);
        using (var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg))
        {
            Assert.Equal(refusal, Assert.Throws<ZeyoCutException>(() => FrameExport.Thumbnail(renderer, project, 0, 96, 64, Path.Combine(session.Directory, "exports", "t.png"), 96)).Message);
        }

        var report = ProjectChecker.Check(project, session.Directory, null);
        var issue = Assert.Single(report.Issues, i => i.Subject == "clip");
        Assert.Equal(CheckKind.MissingFile, issue.Kind);
        Assert.Equal(
            $"layer 'clip': the aiVideo matte of 'screen' isn't computed yet ({MatteFiles.Relative(session.Directory, MattePath(session))}), so the layer is drawn with its background and exports refuse; wait for its matte job (get_job_status) or run remove_background again",
            issue.Message);

        WriteBoxMatte(MattePath(session));
        Assert.Empty(MatteFiles.Pending(project, session.Directory));
        Assert.DoesNotContain(ProjectChecker.Check(project, session.Directory, null).Issues, i => i.Subject == "clip");
    }

    [Fact]
    public void VideoMattesFollowTheSourceFrameUnderTrimAndSpeed()
    {
        // Timeline frame f shows source time 0.3 + 0.2 f, so source frame 3 + 2f: the box at x = 20 + 8f.
        var session = GreenScreen("ai-matte-trim", new VideoLayerOptions { Id = "clip", Asset = "screen", TrimIn = 0.3, Speed = 2 });
        WriteBoxMatte(MattePath(session));
        var project = session.Snapshot();

        void Check(SKImage image, int frame)
        {
            var left = 20 + 8 * frame;
            Assert.Equal(Red, TestImages.Pixel(image, left + 2, 28), new ColorComparer(3));
            Assert.Equal(Red, TestImages.Pixel(image, left + 21, 28), new ColorComparer(3));
            Assert.Equal(Blue, TestImages.Pixel(image, left - 2, 28));
            Assert.Equal(Blue, TestImages.Pixel(image, left + 26, 28));
            Assert.Equal(Blue, TestImages.Pixel(image, left + 10, 10));
        }

        using (var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg))
        {
            foreach (var frame in new[] { 4, 0, 2 })
            {
                using var image = renderer.RenderImage(project, frame);
                Check(image, frame);
            }

            Assert.Empty(renderer.TakeWarnings());
        }

        // Exports read the matte as a stream next to the clip's own.
        using (var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg, streamVideo: true))
        {
            for (var frame = 0; frame < 5; frame++)
            {
                using var image = renderer.RenderImage(project, frame);
                Check(image, frame);
            }
        }

        // shrink is in source pixels: 2 px off each side of the box (source frame 5, x = 28 to 51).
        session.Mutate("refine", p => MatteEditor.Refine(p, "clip", 2, null));
        using (var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg))
        using (var image = renderer.RenderImage(session.Snapshot(), 1))
        {
            Assert.Equal(Blue, TestImages.Pixel(image, 29, 28));
            Assert.Equal(Red, TestImages.Pixel(image, 30, 28), new ColorComparer(3));
            Assert.Equal(Red, TestImages.Pixel(image, 49, 28), new ColorComparer(3));
            Assert.Equal(Blue, TestImages.Pixel(image, 50, 28));
        }
    }

    [Fact]
    public void BiRefNetFindsTheSubjectOfAPicture()
    {
        var model = RequireModel(AiModels.BiRefNetLite);
        using var surface = SKSurface.Create(new SKImageInfo(320, 240))!;
        surface.Canvas.Clear(new SKColor(235, 235, 235));
        using (var paint = new SKPaint())
        {
            paint.IsAntialias = true;
            paint.Color = new SKColor(220, 90, 30);
            surface.Canvas.DrawCircle(160, 120, 70, paint);
        }

        using var picture = surface.Snapshot();
        using var session = AiSession.Open(model, AiDevice.Cpu);
        Assert.Equal("CPU", session.Device);
        var alpha = AiMattes.Image(session, picture);
        Assert.Equal(320 * 240, alpha.Length);
        Assert.True(alpha[120 * 320 + 160] > 200, $"center {alpha[120 * 320 + 160]}");
        Assert.True(alpha[10 * 320 + 10] < 50, $"corner {alpha[10 * 320 + 10]}");
        Assert.True(alpha[120 * 320 + 300] < 50, $"right {alpha[120 * 320 + 300]}");
        Assert.InRange(AiMattes.Background(alpha), 0.6, 0.85);
    }

    [Fact]
    public void RobustVideoMattingWritesAMatteForEveryFrame()
    {
        var model = RequireModel(AiModels.RvmMobileNet);
        var session = GreenScreen("ai-matte-rvm");
        var project = session.Snapshot();
        var asset = project.FindAsset("screen")!;
        var path = MattePath(session);
        using var ai = AiSession.Open(model, AiDevice.Cpu);
        var progress = new List<int>();
        var result = AiMattes.Video(ai, Ffmpeg.Require().Ffmpeg, ProjectStore.AssetPath(session.Directory, asset), asset, path, (done, _) => progress.Add(done), TestContext.Current.CancellationToken);
        Assert.Equal((path, 20, 96, 64), (result.Path, result.Frames, result.Width, result.Height));
        Assert.Equal(Enumerable.Range(1, 20), progress);
        Assert.False(File.Exists(path + ".part"));
        Assert.InRange(result.Background, 0, 1);

        var info = VideoProbe.Probe(Ffmpeg.Require().Ffprobe, path);
        Assert.Equal((96, 64, 20), (info.Width, info.Height, info.FrameCount));
        using var renderer = new FrameRenderer(session.Directory, () => Ffmpeg.Require().Ffmpeg);
        using var image = renderer.RenderImage(project, 3);
        Assert.Empty(renderer.TakeWarnings());
    }

    private sealed class ColorComparer(int tolerance) : IEqualityComparer<SKColor>
    {
        public bool Equals(SKColor a, SKColor b) =>
            Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance && a.Alpha == b.Alpha;

        public int GetHashCode(SKColor color) => 0;
    }

    // Serves fixed files over HTTP/1.1 on 127.0.0.1; anything else is a 404.
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Dictionary<string, byte[]> _files;
        private readonly Task _loop;
        private int _requests;

        public TinyServer(Dictionary<string, byte[]> files)
        {
            _files = files;
            _listener.Start();
            _loop = Task.Run(Serve);
        }

        public int Requests => Volatile.Read(ref _requests);

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

        public void Dispose()
        {
            _listener.Stop();
            try
            {
                _loop.Wait(2000);
            }
            catch (AggregateException)
            {
            }
        }

        private async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[1];
                    while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) == 1)
                    {
                        request.Append((char)buffer[0]);
                    }

                    Interlocked.Increment(ref _requests);
                    var path = request.ToString().Split(' ')[1];
                    var found = _files.TryGetValue(path, out var body);
                    body ??= Encoding.ASCII.GetBytes("not found");
                    var head = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                }
            }
        }
    }
}
