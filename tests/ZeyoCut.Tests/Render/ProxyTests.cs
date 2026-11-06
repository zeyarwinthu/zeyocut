// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class ProxyTests
{
    // A 640x360 project at 10 fps showing a 2880x1620 ramp clip (frame n coded in gray) for 3 s.
    private static (ProjectSession Session, Asset Asset) RampProject(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 640, height: 360, fps: 10, duration: 3));
        var asset = TestVideos.Import(session, TestVideos.Ramp(Path.Combine(directory, "src_ramp.mp4"), seconds: 3, fps: 10, width: 2880, height: 1620), "ramp");
        session.Mutate("layer", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "ramp" }));
        return (session, asset);
    }

    private static int FrameCode(byte[] png)
    {
        using var image = SKImage.FromEncodedData(png)!;
        return TestVideos.FrameCode(image, 0, 0, image.Width, image.Height);
    }

    [Fact]
    public void FootageOver2560PxGetsAProxyWithEveryFrameAtItsTime()
    {
        var ffmpeg = Ffmpeg.Require();
        var (session, asset) = RampProject("proxy-frames");
        Assert.True(Proxies.Wanted(asset));
        Assert.Equal((1920, 1080), Proxies.Size(asset));
        Assert.False(Proxies.Wanted(new Asset { Id = "qhd", Type = AssetType.Video, File = "assets/qhd.mp4", Width = 2560, Height = 1440 }));
        Assert.Equal((1080, 1920), Proxies.Size(new Asset { Id = "tall", Type = AssetType.Video, File = "assets/tall.mp4", Width = 2160, Height = 3840 }));
        Assert.Null(Proxies.Ready(session.Directory, asset));

        var path = Proxies.Make(ffmpeg.Ffmpeg, session.Directory, asset);
        Assert.Equal(path, Proxies.Ready(session.Directory, asset));
        Assert.Equal(Path.Combine(session.Directory, "proxies"), Path.GetDirectoryName(path));
        var probe = VideoProbe.Probe(ffmpeg.Ffprobe, path);
        Assert.Equal((1920, 1080, 30, asset.FrameRate), (probe.Width, probe.Height, probe.FrameCount, probe.FrameRate));
        Assert.False(probe.HasAudio);

        using var renderer = new FrameRenderer(session.Directory, () => ffmpeg.Ffmpeg);
        foreach (var frame in new[] { 0, 13, 29 })
        {
            Assert.Equal(frame, FrameCode(Preview.RenderFrame(renderer, session.Snapshot(), frame / 10.0, 640, debug: false).Png));
        }

        // A changed file needs a proxy of its own.
        File.SetLastWriteTimeUtc(ProjectStore.AssetPath(session.Directory, asset), DateTime.UtcNow.AddMinutes(1));
        Assert.Null(Proxies.Ready(session.Directory, asset));
    }

    [Fact]
    public void PreviewsDrawTheProxyAndEverythingElseTheOriginal()
    {
        var ffmpeg = Ffmpeg.Require();
        var (session, asset) = RampProject("proxy-previews");
        using var renderer = new FrameRenderer(session.Directory, () => ffmpeg.Ffmpeg);
        var before = Preview.RenderFrame(renderer, session.Snapshot(), 1, 640, debug: false);
        Assert.Equal(10, FrameCode(before.Png));
        Assert.Empty(before.Proxied!);

        // A red "proxy" shows which file each path reads.
        var proxy = Proxies.PathOf(session.Directory, asset);
        Directory.CreateDirectory(Path.GetDirectoryName(proxy)!);
        var made = ProcessRunner.Run(ffmpeg.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=1920x1080:r=10:d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", proxy], TimeSpan.FromSeconds(60));
        Assert.True(made.ExitCode == 0, made.StandardError);

        var after = Preview.RenderFrame(renderer, session.Snapshot(), 1, 640, debug: false);
        Assert.Equal(["ramp"], after.Proxied!);
        using (var image = SKImage.FromEncodedData(after.Png)!)
        {
            var pixel = TestImages.Pixel(image, 320, 180);
            Assert.True(pixel.Red > 200 && pixel.Green < 60, $"the preview shows {pixel}");
        }

        var sheet = Preview.RenderContactSheet(renderer, session.Snapshot(), 0, 2, 3, 3, 900, debug: false);
        Assert.Equal(["ramp"], sheet.Proxied!);
        var source = Preview.RenderSourceSheet(renderer, asset, 0, 2, 3, 3, 900);
        Assert.Equal(["ramp"], source.Proxied!);
        using (var image = SKImage.FromEncodedData(source.Png)!)
        {
            Assert.True(TestImages.Pixel(image, 50, 50).Red > 200);
        }

        using var exported = renderer.RenderImage(session.Snapshot(), 10);
        Assert.Equal(10, TestVideos.FrameCode(exported, 0, 0, 640, 360));
    }
}
