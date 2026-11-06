// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public sealed class RedactionRenderTests
{
    // TestVideos.MovingSquare as 'square' in 'clip' on a 320x180 canvas, fit at scale 1, so canvas
    // pixels are the footage's pixels.
    private static ProjectSession Square(string name)
    {
        Ffmpeg.Require();
        var directory = TestPaths.NewDirectory(name);
        var file = TestVideos.MovingSquare(Path.Combine(directory, "square.mp4"));
        var session = ProjectSession.Create(Path.Combine(directory, "project"), TestProjects.Empty(width: 320, height: 180, fps: 30, duration: 2));
        TestVideos.Import(session, file, "square");
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "square" }));
        return session;
    }

    // The moving square's own box on frames 0-59, lost on 'lost'.
    private static List<TrackedFrame> OnTheSquare(IEnumerable<int>? lost = null)
    {
        var gone = (lost ?? []).ToHashSet();
        return Enumerable.Range(0, 60).Select(f =>
        {
            var (x, y, size) = TestVideos.SquareAt(f);
            return new TrackedFrame { Frame = f, X = x, Y = y, Width = size, Height = size, Confidence = gone.Contains(f) ? 0 : 1, Lost = gone.Contains(f) };
        }).ToList();
    }

    // The same box on every frame of the footage.
    private static List<TrackedFrame> Still(int frames, double x, double y, double width, double height) =>
        Enumerable.Range(0, frames).Select(f => new TrackedFrame { Frame = f, X = x, Y = y, Width = width, Height = height, Confidence = 1 }).ToList();

    private static void AddTrack(ProjectSession session, string id, string asset, List<TrackedFrame> frames)
    {
        var file = TrackFiles.Save(session.Directory, new TrackData { Track = id, Asset = asset, Frames = frames });
        var first = frames[0];
        session.Mutate("track", p =>
        {
            p.Tracks.Add(new Track
            {
                Id = id,
                Asset = asset,
                Layer = "clip",
                FirstFrame = first.Frame,
                LastFrame = frames[^1].Frame,
                Origin = first.Frame,
                File = file,
                Anchors = [new TrackBox { Frame = first.Frame, X = first.X, Y = first.Y, Width = first.Width, Height = first.Height }],
            });
            return 0;
        });
    }

    private static RedactionChange Redact(ProjectSession session, string[] tracks, RedactionStyle style, double strength = 50, double padding = 0, double feather = 0, int hold = 0, string color = "#000000") =>
        session.Mutate("redact", p => RedactionEditor.Apply(p, tracks, new RedactionSettings(style, strength, padding, feather, hold, color)));

    private static FrameRenderer Renderer(ProjectSession session) => new(session.Directory, () => Ffmpeg.Require().Ffmpeg);

    private static SKColor[,] Colors(SKImage image)
    {
        var pixels = TestImages.Pixels(image);
        var colors = new SKColor[image.Width, image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var at = (y * image.Width + x) * 4;
                colors[x, y] = new SKColor(pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]);
            }
        }

        return colors;
    }

    private static bool IsBlack(SKColor color) => color.Red < 12 && color.Green < 12 && color.Blue < 12;

    // Lengths of the runs of equal pixels along a row, from 'left' to just before 'right'.
    private static List<int> Runs(SKColor[,] colors, int y, int left, int right)
    {
        var runs = new List<int> { 1 };
        for (var x = left + 1; x < right; x++)
        {
            if (colors[x, y] == colors[x - 1, y])
            {
                runs[^1]++;
            }
            else
            {
                runs.Add(1);
            }
        }

        return runs;
    }

    // The spec's test: the pixelate block scales with the box. At strength 100 both boxes get 4 blocks
    // across: 15 px blocks in the 60 px box, 30 px blocks in the 120 px one.
    [Fact]
    public void PixelateBlocksScaleWithTheBox()
    {
        var session = Square("redact-blocks");
        AddTrack(session, "small", "square", Still(60, 100, 5, 60, 60));
        AddTrack(session, "big", "square", Still(60, 180, 5, 120, 120));
        using var renderer = Renderer(session);
        using var before = renderer.RenderImage(session.Snapshot(), 0);
        Redact(session, ["small", "big"], RedactionStyle.Pixelate, strength: 100);
        using var after = renderer.RenderImage(session.Snapshot(), 0);

        var colors = Colors(after);
        var original = Colors(before);
        foreach (var y in new[] { 10, 35, 60 })
        {
            Assert.Equal([15, 15, 15, 15], Runs(colors, y, 100, 160));
        }

        foreach (var y in new[] { 10, 65, 120 })
        {
            Assert.Equal([30, 30, 30, 30], Runs(colors, y, 180, 300));
        }

        // Each block is the average of the footage under it.
        double red = 0, green = 0;
        for (var y = 5; y < 20; y++)
        {
            for (var x = 100; x < 115; x++)
            {
                red += original[x, y].Red;
                green += original[x, y].Green;
            }
        }

        Assert.InRange(colors[107, 12].Red, red / 225 - 2, red / 225 + 2);
        Assert.InRange(colors[107, 12].Green, green / 225 - 2, green / 225 + 2);

        // Outside the boxes nothing changes.
        Assert.Equal(original[99, 35], colors[99, 35]);
        Assert.Equal(original[160, 35], colors[160, 35]);
        Assert.Equal(original[179, 65], colors[179, 65]);
        Assert.Equal(original[240, 125], colors[240, 125]);
    }

    // The spec's test: with the tracker lost for 5 frames, the square stays hidden on every frame.
    [Fact]
    public void NoFrameIsUncoveredWhileTheTrackerIsLostForFiveFrames()
    {
        var session = Square("redact-lost");
        AddTrack(session, "track_1", "square", OnTheSquare(lost: Enumerable.Range(20, 5)));
        Redact(session, ["track_1"], RedactionStyle.Solid, padding: 15, feather: 4, hold: 15);
        using var renderer = Renderer(session);
        var project = session.Snapshot();
        for (var frame = 0; frame < 60; frame++)
        {
            using var image = renderer.RenderImage(project, frame);
            var colors = Colors(image);
            var (x0, y0, size) = TestVideos.SquareAt(frame);
            for (var y = (int)y0; y < y0 + size; y++)
            {
                for (var x = (int)x0; x < x0 + size; x++)
                {
                    Assert.True(IsBlack(colors[x, y]), $"frame {frame}: the square shows at {x},{y} ({colors[x, y]})");
                }
            }

            Assert.False(IsBlack(colors[310, 5]), $"frame {frame}: the corner is black too");
        }
    }

    // A scene cut that analyze_video finds ends the hold, in the same renderer and in the frame keys
    // that the chunk and preview caches use.
    [Fact]
    public void AHoldEndsAtTheSceneCutAnalyzeVideoFound()
    {
        var session = Square("redact-cut");
        AddTrack(session, "track_1", "square", OnTheSquare().Take(30).ToList());
        Redact(session, ["track_1"], RedactionStyle.Solid, hold: 15);
        using var renderer = Renderer(session);
        var project = session.Snapshot();

        // A pixel the square left after frame 29, under the box held from there.
        var (x, y, _) = TestVideos.SquareAt(29);
        bool Hidden(int frame)
        {
            using var image = renderer.RenderImage(project, frame);
            return IsBlack(Colors(image)[(int)x + 2, (int)y + 2]);
        }

        var before = new FrameKeys(project, session.Directory).Of(40);
        Assert.True(Hidden(44));
        AnalysisFiles.Save(AnalysisFiles.AnalysisPath(session.Directory, project.FindAsset("square")!), new MediaAnalysis { Asset = "square", Scenes = [new SceneCut(1.2, 30)] });
        Assert.NotEqual(before, new FrameKeys(project, session.Directory).Of(40));
        Assert.True(Hidden(29));
        Assert.True(Hidden(36));
        Assert.False(Hidden(37));
        Assert.False(Hidden(44));
    }

    // The spec's test: splits, trims (a ripple cut) and speed changes keep the redaction on the
    // square, since it works on the footage's own frames.
    [Fact]
    public void SplitsTrimsAndSpeedChangesKeepTheRedactionInStep()
    {
        var session = Square("redact-edits");
        AddTrack(session, "track_1", "square", OnTheSquare());
        Redact(session, ["track_1"], RedactionStyle.Solid);
        using var renderer = Renderer(session);

        void AssertHidden(string after)
        {
            var project = session.Snapshot();
            var asset = project.FindAsset("square")!;
            var checkedFrames = 0;
            for (var frame = 0; frame < FrameMath.FrameCount(project); frame += 3)
            {
                var time = FrameMath.TimeOf(project, frame);
                var clip = project.Layers.OfType<VideoLayer>().LastOrDefault(v => SceneEvaluator.IsActive(v, time, project.Duration));
                if (clip is null)
                {
                    continue;
                }

                var source = VideoTiming.FrameAt(asset, VideoTiming.SourceTime(clip, time));
                using var image = renderer.RenderImage(project, frame);
                var colors = Colors(image);
                var (x0, y0, size) = TestVideos.SquareAt(source);
                var shown = 0;
                for (var y = (int)y0; y < y0 + size; y++)
                {
                    for (var x = (int)x0; x < x0 + size; x++)
                    {
                        shown += IsBlack(colors[x, y]) ? 0 : 1;
                    }
                }

                Assert.True(shown == 0, $"after {after}: at {time:0.###}s '{clip.Id}' shows source frame {source}, and {shown} pixels of the square show");
                checkedFrames++;
            }

            Assert.True(checkedFrames >= 8, $"after {after}: only {checkedFrames} frames checked");
        }

        AssertHidden("nothing");
        session.Mutate("split", p => TimelineEditor.SplitLayer(p, "clip", 0.5));
        AssertHidden("a split");
        session.Mutate("cut", p => TimelineEditor.RemoveRange(p, "clip_2", 0.7, 1, ripple: true));
        AssertHidden("a ripple cut");
        session.Mutate("speed", p => TimelineEditor.SetSpeed(p, "clip", 2, ripple: true));
        AssertHidden("a speed change");
        session.Mutate("move", p => TimelineEditor.MoveLayer(p, "clip_2", 0.4));
        AssertHidden("a move");
        Assert.Equal(0, session.Snapshot().FindLayer("redaction_1")!.Start);
    }

    // The spec's test: a subject cutout of the clip (drawn above a title) shows the redaction too.
    [Fact]
    public void ACutoutOfTheClipStaysRedacted()
    {
        Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("redact-cutout");
        var file = TestVideos.GreenScreen(Path.Combine(directory, "screen.mp4"));
        var session = ProjectSession.Create(Path.Combine(directory, "project"), TestProjects.Empty(width: 96, height: 64, fps: 10, duration: 1));
        TestVideos.Import(session, file, "screen");
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "screen" }));
        session.Mutate("title", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hi", FontSize = 14, Color = "#FFFF00", Box = new TextBackground { Color = "#FFFF00", Padding = 4 }, X = 30, Y = 20 }));
        session.Mutate("behind", p => SubjectEditor.Apply(p, "clip", ["title"], false, MatteEditor.Build((IMattable)p.FindLayer("clip")!, MatteMethod.Chroma, new MatteValues("#28BE3C", null, null, null))));

        // The red box: 24x24 at (8 + 4n, 16) on frame n.
        var frames = Enumerable.Range(0, 10).Select(f => new TrackedFrame { Frame = f, X = 8 + 4 * f, Y = 16, Width = 24, Height = 24, Confidence = 1 }).ToList();
        AddTrack(session, "track_1", "screen", frames);
        Redact(session, ["track_1"], RedactionStyle.Solid, color: "#0000FF");
        using var renderer = Renderer(session);
        var project = session.Snapshot();
        for (var frame = 0; frame < 10; frame++)
        {
            using var cutout = renderer.RenderImage(project, frame, new RenderOptions(Transparent: true, Only: new HashSet<string> { "clip_subject" }));
            using var full = renderer.RenderImage(project, frame);
            var subject = Colors(cutout);
            var all = Colors(full);
            for (var y = 16; y < 40; y++)
            {
                for (var x = 8 + 4 * frame; x < 32 + 4 * frame; x++)
                {
                    Assert.True(subject[x, y] is { Blue: > 200, Red: < 40, Alpha: > 200 }, $"frame {frame}: the cutout shows {subject[x, y]} at {x},{y}");
                    Assert.True(all[x, y] is { Blue: > 200, Red: < 40 }, $"frame {frame}: the picture shows {all[x, y]} at {x},{y}");
                }
            }
        }
    }

    // Blur hides the detail inside the box and keeps its colors; the feather softens a band just
    // outside it; past that the picture is untouched. A hidden redaction hides nothing.
    [Fact]
    public void BlurAndFeatherChangeOnlyTheBoxAndItsEdge()
    {
        var session = Square("redact-blur");
        AddTrack(session, "track_1", "square", Still(60, 120, 50, 80, 80));
        using var renderer = Renderer(session);
        using var before = renderer.RenderImage(session.Snapshot(), 0);
        Redact(session, ["track_1"], RedactionStyle.Blur, feather: 8);
        using var after = renderer.RenderImage(session.Snapshot(), 0);
        var original = Colors(before);
        var blurred = Colors(after);

        static (double Mean, double Variance) Stats(SKColor[,] colors, int left, int top, int size)
        {
            var values = new List<double>();
            for (var y = top; y < top + size; y++)
            {
                for (var x = left; x < left + size; x++)
                {
                    values.Add(colors[x, y].Green);
                }
            }

            var mean = values.Average();
            return (mean, values.Average(v => (v - mean) * (v - mean)));
        }

        var inside = Stats(blurred, 130, 60, 60);
        var was = Stats(original, 130, 60, 60);
        Assert.True(inside.Variance < was.Variance / 4, $"variance {inside.Variance:0.#}, was {was.Variance:0.#}");
        Assert.InRange(inside.Mean, was.Mean - 15, was.Mean + 15);

        // The feather band (up to 8 px out) changes; beyond it nothing does.
        Assert.NotEqual(original[118, 90], blurred[118, 90]);
        Assert.Equal(original[110, 90], blurred[110, 90]);
        Assert.Equal(original[160, 40], blurred[160, 40]);

        session.Mutate("hide", p => ProjectEditor.UpdateLayer(p, "redaction_1", new System.Text.Json.Nodes.JsonObject { ["visible"] = false }));
        using var hidden = renderer.RenderImage(session.Snapshot(), 0);
        Assert.Equal(TestImages.Pixels(before), TestImages.Pixels(hidden));
    }

    // After tracks_to_keyframes the keyframed box is what gets hidden, and editing it moves it.
    [Fact]
    public void AKeyframedBoxIsWhatGetsHidden()
    {
        var session = Square("redact-keys");
        AddTrack(session, "track_1", "square", OnTheSquare());
        Redact(session, ["track_1"], RedactionStyle.Solid, padding: 15);
        using var renderer = Renderer(session);
        session.Mutate("keys", p =>
        {
            var layer = (RedactionLayer)p.FindLayer("redaction_1")!;
            var asset = p.FindAsset("square")!;
            layer.Boxes = new Dictionary<string, RedactionBox> { ["track_1"] = RedactionEditor.ToKeyframes(renderer.Cover(p.FindTrack("track_1")!, asset, layer), asset) };
            return 0;
        });

        using (var keyed = renderer.RenderImage(session.Snapshot(), 30))
        {
            var (x, y, size) = TestVideos.SquareAt(30);
            var colors = Colors(keyed);
            Assert.True(IsBlack(colors[(int)x, (int)y]) && IsBlack(colors[(int)(x + size - 1), (int)(y + size - 1)]));
        }

        // A box set by hand at the top-right corner for the whole footage.
        session.Mutate("move", p =>
        {
            var layer = (RedactionLayer)p.FindLayer("redaction_1")!;
            AnimatedValue Keys(double value) => new(value) { Keyframes = [new Keyframe { Time = 0, Value = value }, new Keyframe { Time = 2, Value = value }] };
            layer.Boxes!["track_1"] = new RedactionBox { X = Keys(260), Y = Keys(0), Width = Keys(60), Height = Keys(40) };
            return 0;
        });
        using var moved = renderer.RenderImage(session.Snapshot(), 30);
        var after = Colors(moved);
        var (sx, sy, _) = TestVideos.SquareAt(30);
        Assert.False(IsBlack(after[(int)sx + 20, (int)sy + 20]));
        Assert.True(IsBlack(after[300, 20]));
    }
}
