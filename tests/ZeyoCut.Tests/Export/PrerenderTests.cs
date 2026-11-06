// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Export;

public class PrerenderTests
{
    private const int Width = 160;
    private const int Height = 120;

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // 160x120 at 10 fps for 4 s: a red logo all the time, and at 1–3 s a title that moves right and
    // fades to 40%, with a drop shadow and a yellow badge at 60% as its child.
    private static ProjectSession Timeline(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: Width, height: Height, fps: 10, duration: 4));
        session.Mutate("import", p =>
        {
            AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "src_logo.png"), 40, 40, new SKColor(220, 30, 30)), "logo", new AssetProbe());
            return AssetImporter.Import(p, directory, TestImages.Solid(Path.Combine(directory, "src_badge.png"), 16, 16, new SKColor(250, 220, 20)), "badge", new AssetProbe());
        });
        session.Mutate("layers", p =>
        {
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["n"] = "logo" }, X = 40, Y = 30 });
            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Prerender", FontFamily = "Segoe UI", FontSize = 22, Color = "#2040E0", Y = 95, Start = 1, End = 3 });
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "badge", Drawings = new Dictionary<string, string> { ["n"] = "badge" }, ParentId = "title", Y = -22, Opacity = 0.6 });
            EffectEditor.SetEffect(p, "title", EffectKind.Shadow, new EffectValues("#000000", 3, 3, 4, 0.7, null, null), false);
            return KeyframeEditor.SetKeyframes(p,
            [
                new KeyframeEntry("title", "x", 1, Json(50)), new KeyframeEntry("title", "x", 3, Json(110)),
                new KeyframeEntry("title", "opacity", 1, Json(1)), new KeyframeEntry("title", "opacity", 2, Json(0.4)),
            ]);
        });
        return session;
    }

    private static byte[] Premultiplied(SKImage image)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        Assert.True(image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0));
        return bitmap.GetPixelSpan().ToArray();
    }

    // ffmpeg's rgba has straight alpha.
    private static byte[] Premultiply(byte[] straight)
    {
        var result = new byte[straight.Length];
        for (var i = 0; i < straight.Length; i += 4)
        {
            var alpha = straight[i + 3];
            for (var c = 0; c < 3; c++)
            {
                result[i + c] = (byte)Math.Round(straight[i + c] * alpha / 255.0);
            }

            result[i + 3] = alpha;
        }

        return result;
    }

    [Fact]
    public void APrerenderedLayerIsTheTransparentRenderOfThatLayerAlone()
    {
        var ffmpeg = Ffmpeg.Require();
        var session = Timeline("prerender-alpha");
        var project = session.Snapshot();
        var folder = Path.Combine(session.Directory, "exports", "test_prerender");

        var result = Prerender.Make(project, session.Directory, folder, "title", ffmpeg, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Reused);
        Assert.Equal(new PrerenderedLayer("title", "title.mov", Width, Height, 10, 10, 20, result.Layer.Key), result.Layer);
        Assert.Equal(Path.Combine(folder, "title.mov"), result.Path);
        Assert.Equal([result.Layer], Prerender.List(folder));
        Assert.False(File.Exists(Path.Combine(folder, "title.partial.mov")));

        var probe = ProcessRunner.Run(ffmpeg.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,profile,pix_fmt,nb_read_frames", "-of", "default=noprint_wrappers=1", result.Path], TimeSpan.FromSeconds(60));
        Assert.True(probe.ExitCode == 0, probe.StandardError);
        Assert.Contains("codec_name=prores", probe.StandardOutput);
        Assert.Contains("profile=4444", probe.StandardOutput);
        Assert.Contains("pix_fmt=yuva444p", probe.StandardOutput);
        Assert.Contains("nb_read_frames=20", probe.StandardOutput);

        var (_, only) = Prerender.Choose(project, "title");
        Assert.Equal(["title", "badge"], only.Order(StringComparer.Ordinal).Reverse());
        using var renderer = new FrameRenderer(session.Directory);
        foreach (var index in new[] { 0, 6, 12, 19 })
        {
            using var alone = renderer.RenderImage(project, 10 + index, new RenderOptions(Transparent: true, Only: only));
            var expected = Premultiplied(alone);
            var decoded = Premultiply(Ffmpeg.ExtractFrameRgba(ffmpeg, result.Path, index, Width, Height));
            var alphaError = 0;
            var covered = 0;
            for (var i = 3; i < expected.Length; i += 4)
            {
                alphaError = Math.Max(alphaError, Math.Abs(expected[i] - decoded[i]));
                covered += expected[i] > 0 ? 1 : 0;
            }

            Assert.True(covered > 300, $"frame {10 + index}: only {covered} pixels drawn");
            Assert.True(alphaError <= 1, $"frame {10 + index}: alpha differs by up to {alphaError}");
            var error = Ffmpeg.MeanAbsoluteError(expected, decoded);
            Assert.True(error < 0.5, $"frame {10 + index}: mean color error {error:0.###}");

            // The logo isn't in it, though it is in the frame.
            var logo = (30 * Width + 40) * 4;
            Assert.Equal(0, decoded[logo + 3]);
            using var whole = renderer.RenderImage(project, 10 + index);
            Assert.Equal(new SKColor(220, 30, 30), SKBitmap.FromImage(whole).GetPixel(40, 30));
        }
    }

    [Fact]
    public void APrerenderIsDrawnAgainOnlyWhenSomethingItShowsChanges()
    {
        var ffmpeg = Ffmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var session = Timeline("prerender-current");
        var folder = Path.Combine(session.Directory, "exports", "test_prerender");
        var first = Prerender.Make(session.Snapshot(), session.Directory, folder, "title", ffmpeg, cancellationToken: ct);
        var written = File.GetLastWriteTimeUtc(first.Path);

        var again = Prerender.Make(session.Snapshot(), session.Directory, folder, "title", ffmpeg, cancellationToken: ct);
        Assert.True(again.Reused);
        Assert.Equal(first.Layer, again.Layer);

        session.Mutate("move the logo", p => ProjectEditor.UpdateLayer(p, "logo", new JsonObject { ["x"] = 120 }));
        session.Mutate("a keyframe after the title", p => KeyframeEditor.SetKeyframes(p, [new KeyframeEntry("title", "x", 3.5, Json(140))]));
        Assert.True(Prerender.Make(session.Snapshot(), session.Directory, folder, "title", ffmpeg, cancellationToken: ct).Reused);
        Assert.Equal(written, File.GetLastWriteTimeUtc(first.Path));

        session.Mutate("fade the badge", p => ProjectEditor.UpdateLayer(p, "badge", new JsonObject { ["opacity"] = 0.3 }));
        var changed = Prerender.Make(session.Snapshot(), session.Directory, folder, "title", ffmpeg, cancellationToken: ct);
        Assert.False(changed.Reused);
        Assert.NotEqual(first.Layer.Key, changed.Layer.Key);
        Assert.Equal([changed.Layer], Prerender.List(folder));

        var logo = Prerender.Make(session.Snapshot(), session.Directory, folder, "logo", ffmpeg, cancellationToken: ct);
        Assert.Equal(new PrerenderedLayer("logo", "logo.mov", Width, Height, 10, 0, 40, logo.Layer.Key), logo.Layer);
        Assert.Equal(["logo", "title"], Prerender.List(folder).Select(l => l.LayerId));

        session.Mutate("hide the logo", p => ProjectEditor.UpdateLayer(p, "logo", new JsonObject { ["visible"] = false }));
        Assert.Equal("layer 'logo' is hidden, so it has nothing to prerender; update_layer with visible true shows it",
            Assert.Throws<ZeyoCutException>(() => Prerender.Make(session.Snapshot(), session.Directory, folder, "logo", ffmpeg, cancellationToken: ct)).Message);
    }
}
