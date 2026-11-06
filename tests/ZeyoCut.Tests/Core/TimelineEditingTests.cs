// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class TimelineEditingTests
{
    private sealed record Seen(string Content, double[] Values);

    private static KeyframeEntry Key(string layer, string property, double time, object value, string? easing = null) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value), easing);

    private static Asset Clip(string id = "clip", double seconds = 10) => new()
    {
        Id = id,
        Type = AssetType.Video,
        File = $"assets/{id}.mp4",
        Width = 640,
        Height = 360,
        FrameRate = "30",
        FrameCount = (int)Math.Round(seconds * 30),
        Duration = seconds,
        HasAudio = true,
        Codec = "h264",
    };

    private static VideoLayer AddClip(Project project, string id, double start, double trimIn, double trimOut, double speed = 1, string asset = "clip") =>
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = asset, Id = id, Start = start, TrimIn = trimIn, TrimOut = trimOut, Speed = speed });

    // Everything that decides how a frame looks: the shown layers in draw order with their world
    // transform, opacity, color and which source frame / drawing / text they show.
    private static List<Seen> Frame(Project project, double time) =>
        SceneEvaluator.Evaluate(project, time)
            .Where(e => e.Shown)
            .Select(e =>
            {
                var w = e.World;
                var values = new List<double> { w.A, w.B, w.C, w.D, w.E, w.F, e.Opacity };
                var content = e.Layer switch
                {
                    VideoLayer v => v.Asset,
                    ImageLayer i => Interpolator.Evaluate(i.Drawing, time),
                    TextLayer t => t.Text,
                    _ => "",
                };
                if (e.Layer is VideoLayer video)
                {
                    values.Add(VideoTiming.SourceTime(video, time));
                }

                if (e.Layer is IColorAdjustable color)
                {
                    values.Add(Interpolator.Evaluate(color.Brightness, time));
                    values.Add(Interpolator.Evaluate(color.Saturation, time));
                }

                return new Seen(content, values.ToArray());
            })
            .ToList();

    private static void SameFrame(List<Seen> expected, List<Seen> actual, string at)
    {
        Assert.True(expected.Count == actual.Count, $"{at}: {expected.Count} layers shown before, {actual.Count} after");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Content == actual[i].Content, $"{at}: layer {i} showed '{expected[i].Content}', now '{actual[i].Content}'");
            Assert.Equal(expected[i].Values.Length, actual[i].Values.Length);
            for (var j = 0; j < expected[i].Values.Length; j++)
            {
                Assert.True(Math.Abs(expected[i].Values[j] - actual[i].Values[j]) < 1e-6,
                    $"{at}: layer {i} value {j} was {expected[i].Values[j]}, now {actual[i].Values[j]}");
            }
        }
    }

    private static List<double> Times(Project project, double until)
    {
        var times = new List<double>();
        for (var n = 0; n / (double)project.Fps < until - 1e-9; n++)
        {
            times.Add(n / (double)project.Fps);
        }

        return times;
    }

    private static string At(double t) => t.ToString("0.###", CultureInfo.InvariantCulture);

    private static Project AnimatedRig()
    {
        var project = TestProjects.Rig();
        KeyframeEditor.SetKeyframes(project,
        [
            Key("tora_body", "x", 0, 100),
            Key("tora_body", "x", 2, 500),
            Key("tora_body", "rotation", 0.5, 0, "easeInOut"),
            Key("tora_body", "rotation", 2.5, 90),
            Key("tora_head", "drawing", 0, "normal"),
            Key("tora_head", "drawing", 1.2, "shocked"),
            Key("tora_head", "scaleX", 1, 1, "easeOutBack"),
            Key("tora_head", "scaleX", 2, 1.5),
            Key("tora_tail", "opacity", 0.5, 1, "hold"),
            Key("tora_tail", "opacity", 1.7, 0.2),
            Key("tora_body", "saturation", 0, 1),
            Key("tora_body", "saturation", 3, 0),
        ]);
        return project;
    }

    [Fact]
    public void SplitKeepsEveryFrameOfARigWithChildren()
    {
        var project = AnimatedRig();
        var times = Times(project, project.Duration);
        var before = times.Select(t => Frame(project, t)).ToList();

        var result = TimelineEditor.SplitLayer(project, "tora_body", 1.5);

        Assert.Equal("tora_body", result.LeftId);
        Assert.Equal("tora_body_2", result.RightId);
        Assert.Equal(6, project.Layers.Count);
        Assert.Equal("tora_body_2", project.FindLayer("tora_head_2")!.ParentId);
        Assert.Equal("tora_body_2", project.FindLayer("tora_tail_2")!.ParentId);
        Assert.Equal("tora_body", project.FindLayer("tora_head")!.ParentId);
        Assert.Equal(1.5, project.FindLayer("tora_body")!.End);
        Assert.Null(project.FindLayer("tora_body_2")!.End);
        Assert.Equal(1.5, project.FindLayer("tora_body_2")!.Start);
        Assert.Empty(ProjectValidator.Validate(project));
        Assert.Equal(3, project.Duration);
        for (var i = 0; i < times.Count; i++)
        {
            SameFrame(before[i], Frame(project, times[i]), $"t={At(times[i])}");
        }

        Assert.Contains("the split time must be inside that (got 3)",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.SplitLayer(project, "tora_body_2", 3)).Message);
        Assert.Contains("runs 0–1.5s",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.SplitLayer(project, "tora_body", 1.5)).Message);
    }

    [Fact]
    public void SplitVideoContinuesTheSourceAndKeepsOuterFades()
    {
        var project = TestProjects.Empty(duration: 6);
        project.Assets.Add(Clip());
        var clip = AddClip(project, "clip", start: 0.5, trimIn: 1, trimOut: 9, speed: 2);
        clip.FadeIn = 0.3;
        clip.FadeOut = 0.4;
        KeyframeEditor.SetKeyframes(project, [Key("clip", "opacity", 1, 0.5), Key("clip", "opacity", 4, 1)]);
        var times = Times(project, project.Duration);
        var before = times.Select(t => Frame(project, t)).ToList();

        TimelineEditor.SplitLayer(project, "clip", 2);

        var left = Assert.IsType<VideoLayer>(project.FindLayer("clip"));
        var right = Assert.IsType<VideoLayer>(project.FindLayer("clip_2"));
        Assert.Equal(1, left.TrimIn);
        Assert.Equal(4, left.TrimOut);
        Assert.Equal(4, right.TrimIn);
        Assert.Equal(9, right.TrimOut);
        Assert.Equal(2, right.Start);
        Assert.Equal(2, left.TimelineEnd, 9);
        Assert.Equal(4.5, right.TimelineEnd, 9);
        Assert.Equal((0.3, 0.0), (left.FadeIn, left.FadeOut));
        Assert.Equal((0.0, 0.4), (right.FadeIn, right.FadeOut));
        Assert.Empty(ProjectValidator.Validate(project));
        for (var i = 0; i < times.Count; i++)
        {
            SameFrame(before[i], Frame(project, times[i]), $"t={At(times[i])}");
        }
    }

    [Fact]
    public void RippleRemoveShiftsEverythingAfterTheCut()
    {
        var project = TestProjects.WithImageAssets("logo", "dot");
        project.Duration = 6;
        project.Assets.Add(Clip());
        project.Assets.Add(new Asset { Id = "song", Type = AssetType.Audio, File = "assets/song.mp3", Duration = 10 });
        AddClip(project, "clip", start: 0, trimIn: 0, trimOut: 6);
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, End = 5 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "dot", Drawings = new Dictionary<string, string> { ["normal"] = "dot" } });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", End = 1 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "outro", Text = "Bye", Start = 4 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Id = "music", Asset = "song" });
        KeyframeEditor.SetKeyframes(project,
        [
            Key("dot", "x", 0, 0),
            Key("dot", "x", 6, 600),
            Key("dot", "y", 1, 0, "easeInOut"),
            Key("dot", "y", 4, 300),
            Key("clip", "brightness", 2.5, 0),
            Key("clip", "brightness", 5, -0.5),
        ]);
        var old = Frame(project, 0);
        var oldFrames = Times(project, 6).ToDictionary(t => Math.Round(t * 30), t => Frame(project, t));

        var result = TimelineEditor.RemoveRange(project, "all", 2, 3, ripple: true);

        Assert.Equal(1, result.RemovedSeconds, 9);
        Assert.Equal(6, result.OldDuration);
        Assert.Equal(5, result.NewDuration);
        Assert.Equal(5, project.Duration);
        Assert.Equal(4, project.FindLayer("logo")!.End);
        Assert.Null(project.FindLayer("logo_2"));
        Assert.Equal(2, project.FindLayer("dot")!.End);
        Assert.Equal(2, project.FindLayer("dot_2")!.Start);
        Assert.Equal(1, project.FindLayer("title")!.End);
        Assert.Null(project.FindLayer("title_2"));
        Assert.Equal(3, project.FindLayer("outro")!.Start);
        Assert.Equal(3, ((VideoLayer)project.FindLayer("clip_2")!).TrimIn);
        Assert.Equal(2, project.FindLayer("clip_2")!.Start);
        var music = Assert.IsType<AudioLayer>(project.FindLayer("music"));
        var music2 = Assert.IsType<AudioLayer>(project.FindLayer("music_2"));
        Assert.Equal(2, music.End);
        Assert.Equal((2.0, 3.0), (music2.Start, music2.TrimIn));
        Assert.Contains("clip", result.Cut);
        Assert.Contains("logo", result.Cut);
        Assert.Empty(ProjectValidator.Validate(project));
        SameFrame(old, Frame(project, 0), "t=0");

        foreach (var t in Times(project, 5))
        {
            var oldTime = t < 2 - 1e-9 ? t : t + 1;
            SameFrame(oldFrames[Math.Round(oldTime * 30)], Frame(project, t), $"new t={At(t)} (old {At(oldTime)})");
        }
    }

    [Fact]
    public void RemoveFromOneLayerLeavesEarlierLayersAndMovesLaterOnes()
    {
        var project = TestProjects.WithImageAssets("pic");
        project.Duration = 6;
        project.Assets.Add(Clip());
        AddClip(project, "a", start: 0, trimIn: 0, trimOut: 3);
        AddClip(project, "b", start: 3, trimIn: 3, trimOut: 6);
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "early", Drawings = new Dictionary<string, string> { ["normal"] = "pic" }, Start = 1, End = 2 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "inside", Drawings = new Dictionary<string, string> { ["normal"] = "pic" }, Start = 4, End = 4.5 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "late", Drawings = new Dictionary<string, string> { ["normal"] = "pic" }, Start = 5, End = 6 });
        KeyframeEditor.SetKeyframes(project, [Key("late", "x", 5, 0), Key("late", "x", 6, 100)]);

        var result = TimelineEditor.RemoveRange(project, "b", 3.5, 4.5, ripple: true);

        Assert.Equal(5, result.NewDuration);
        Assert.Equal((0.0, 3.0), (project.FindLayer("a")!.Start, ((VideoLayer)project.FindLayer("a")!).TimelineEnd));
        Assert.Equal((1.0, 2.0), (project.FindLayer("early")!.Start, project.FindLayer("early")!.End!.Value));
        var b = (VideoLayer)project.FindLayer("b")!;
        var b2 = (VideoLayer)project.FindLayer("b_2")!;
        Assert.Equal((3.0, 3.5), (b.TrimIn, b.TrimOut));
        Assert.Equal((3.5, 4.5, 6.0), (b2.Start, b2.TrimIn, b2.TrimOut));
        Assert.Equal((3.5, 4.0), (project.FindLayer("inside")!.Start, project.FindLayer("inside")!.End!.Value));
        var late = (ImageLayer)project.FindLayer("late")!;
        Assert.Equal((4.0, 5.0), (late.Start, late.End!.Value));
        Assert.Equal([4.0, 5.0], late.X.Keyframes.Select(k => k.Time));
        Assert.Empty(ProjectValidator.Validate(project));

        Assert.Contains("layer 'a' runs 0–3s, so it has nothing between 3.5 and 4",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.RemoveRange(project, "a", 3.5, 4, ripple: true)).Message);
        Assert.Contains("the range must have 0 <= start < end",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.RemoveRange(project, "all", 2, 2, ripple: true)).Message);
        Assert.Contains("that would remove the whole project",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.RemoveRange(project, "all", 0, 9, ripple: true)).Message);
    }

    [Fact]
    public void RemoveWithoutRippleLeavesAGap()
    {
        var project = TestProjects.Empty(duration: 4);
        project.Assets.Add(Clip());
        AddClip(project, "clip", start: 0, trimIn: 0, trimOut: 4);
        var before = Frame(project, 3.2);

        var result = TimelineEditor.RemoveRange(project, null, 1, 2, ripple: false);

        Assert.Equal(4, result.NewDuration);
        Assert.Equal(4, project.Duration);
        Assert.Empty(Frame(project, 1.5));
        Assert.Single(Frame(project, 0.5));
        SameFrame(before, Frame(project, 3.2), "t=3.2");
        Assert.Equal(2, project.FindLayer("clip_2")!.Start);
        Assert.Empty(result.Shifted);
    }

    [Fact]
    public void MoveLayerTakesChildrenAndKeyframesAlong()
    {
        var project = AnimatedRig();
        project.Assets.Add(Clip());
        var clip = AddClip(project, "clip", start: 0, trimIn: 0, trimOut: 3);
        clip.ParentId = "tora_body";
        var before = Times(project, 2).Select(t => Frame(project, t)).ToList();
        foreach (var layer in project.Layers.Where(l => l is ImageLayer))
        {
            layer.End = 3;
        }

        var change = TimelineEditor.MoveLayer(project, "tora_body", 1);

        Assert.Equal(4, project.Duration, 9);
        Assert.Equal(3, change.OldDuration);
        Assert.Contains("moved 'tora_body' with 3 child layer(s) by +1s", change.Summary);
        Assert.Equal(1, project.FindLayer("tora_head")!.Start);
        Assert.Equal(4, project.FindLayer("tora_head")!.End);
        Assert.Equal(4, clip.TimelineEnd, 9);
        Assert.Empty(Frame(project, 0.5));
        var times = Times(project, 2);
        for (var i = 0; i < times.Count; i++)
        {
            SameFrame(before[i], Frame(project, times[i] + 1), $"t={At(times[i])}");
        }

        clip.Start = 0.2;
        Assert.Contains("can't move 'tora_body' by -0.5s: child 'clip' starts at 0.2s and would start before 0",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.MoveLayer(project, "tora_body", 0.5)).Message);
        Assert.Contains("start must be >= 0 (got -1)",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.MoveLayer(project, "tora_head", -1)).Message);
    }

    [Fact]
    public void SetSpeedStretchesTheClipAndRipplesLaterLayers()
    {
        var project = TestProjects.Empty(duration: 4);
        project.Assets.Add(Clip());
        var a = AddClip(project, "a", start: 0, trimIn: 0, trimOut: 4, speed: 2);
        var b = AddClip(project, "b", start: 2, trimIn: 4, trimOut: 6);
        KeyframeEditor.SetKeyframes(project, [Key("a", "opacity", 1, 0.25), Key("a", "opacity", 2, 1)]);

        var change = TimelineEditor.SetSpeed(project, "a", 1, ripple: true);

        Assert.Equal(4, a.Length, 9);
        Assert.Equal(4, b.Start, 9);
        Assert.Equal(6, project.Duration, 9);
        Assert.Equal([2.0, 4.0], a.Opacity.Keyframes.Select(k => k.Time));
        Assert.Equal(3, VideoTiming.SourceTime(a, 3), 9);
        Assert.Contains("'a' now plays at 1x (was 2x): 0–4s", change.Summary);

        TimelineEditor.SetSpeed(project, "a", 4, ripple: false);
        Assert.Equal(1, a.Length, 9);
        Assert.Equal(4, b.Start, 9);
        Assert.Equal(6, project.Duration, 9);

        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi" });
        Assert.Contains("set_speed works on video layers; 'title' is a text layer",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.SetSpeed(project, "title", 1, true)).Message);
        Assert.Contains("speed must be between 0.1 and 10; got 20",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.SetSpeed(project, "a", 20, true)).Message);
    }

    [Fact]
    public void CrossfadeUsesHandlesAndKeepsTheDuration()
    {
        var project = TestProjects.Empty(duration: 4);
        project.Assets.Add(Clip());
        var a = AddClip(project, "a", start: 0, trimIn: 2, trimOut: 4);
        var b = AddClip(project, "b", start: 2, trimIn: 4, trimOut: 6);

        var change = TimelineEditor.AddTransition(project, "a", "b", TransitionType.Crossfade, 1);

        Assert.Equal(4, project.Duration);
        Assert.Equal(4.5, a.TrimOut, 9);
        Assert.Equal(2.5, a.TimelineEnd, 9);
        Assert.Equal((1.5, 3.5), (b.Start, b.TrimIn));
        Assert.Equal(4, b.TimelineEnd, 9);
        Assert.Equal(0, Interpolator.Evaluate(b.Opacity, 1.5), 9);
        Assert.Equal(0.5, Interpolator.Evaluate(b.Opacity, 2), 9);
        Assert.Equal(1, Interpolator.Evaluate(b.Opacity, 2.5), 9);
        Assert.Equal(1, Interpolator.Evaluate(a.Opacity, 2), 9);
        Assert.Equal((1.0, 1.0), (a.FadeOut, b.FadeIn));
        Assert.Contains("0.5s handle from 'a'", change.Summary);
        Assert.Contains("0.5s handle from 'b'", change.Summary);
        Assert.Equal(2, Frame(project, 2).Count);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void CrossfadeWithoutHandlesRipplesLaterLayers()
    {
        var project = TestProjects.Empty(duration: 5);
        project.Assets.Add(Clip("short", seconds: 2));
        var a = AddClip(project, "a", start: 0, trimIn: 0, trimOut: 2, asset: "short");
        var b = AddClip(project, "b", start: 2, trimIn: 0, trimOut: 2, asset: "short");
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "outro", Text = "Bye", Start = 4, End = 5 });
        a.ZIndex = 5;

        var change = TimelineEditor.AddTransition(project, "a", "b", TransitionType.Crossfade, 1);

        Assert.Equal(5, change.OldDuration);
        Assert.Equal(4, project.Duration, 9);
        Assert.Equal(2, a.TimelineEnd, 9);
        Assert.Equal(1, b.Start, 9);
        Assert.Equal(0, b.TrimIn);
        Assert.Equal((3.0, 4.0), (project.FindLayer("outro")!.Start, project.FindLayer("outro")!.End!.Value));
        Assert.Equal(1, Interpolator.Evaluate(a.Opacity, 1), 9);
        Assert.Equal(0, Interpolator.Evaluate(a.Opacity, 2), 9);
        Assert.Empty(b.Opacity.Keyframes);
        Assert.Contains("'a' (drawn on top) fades out 1–2s", change.Summary);
        Assert.Contains("moved 'b' and 1 later layer(s) left by 1s", change.Summary);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void FadeToBlackDarkensAcrossTheCut()
    {
        var project = TestProjects.Empty(duration: 4);
        project.Assets.Add(Clip());
        var a = AddClip(project, "a", start: 0, trimIn: 0, trimOut: 2);
        var b = AddClip(project, "b", start: 2, trimIn: 5, trimOut: 7);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", Start = 0, End = 2 });

        TimelineEditor.AddTransition(project, "a", "b", TransitionType.FadeToBlack, 1);

        Assert.Equal(4, project.Duration);
        Assert.Equal(0, Interpolator.Evaluate(a.Brightness, 1.5), 9);
        Assert.Equal(-0.5, Interpolator.Evaluate(a.Brightness, 1.75), 9);
        Assert.Equal(-1, Interpolator.Evaluate(a.Brightness, 2), 9);
        Assert.Equal(-1, Interpolator.Evaluate(b.Brightness, 2), 9);
        Assert.Equal(-0.5, Interpolator.Evaluate(b.Brightness, 2.25), 9);
        Assert.Equal(0, Interpolator.Evaluate(b.Brightness, 2.5), 9);
        Assert.Equal(0, Interpolator.Evaluate(a.Brightness, 0.5), 9);
        Assert.Equal((0.5, 0.5), (a.FadeOut, b.FadeIn));
        Assert.Equal((2.0, 2.0), (a.TimelineEnd, b.Start));

        b.Start = 2.5;
        Assert.Contains("'a' ends at 2s but 'b' starts at 2.5s; close the gap first",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.AddTransition(project, "a", "b", TransitionType.FadeToBlack, 1)).Message);
        b.Start = 2;
        Assert.Contains("fadeToBlack needs image or video layers",
            Assert.Throws<ZeyoCutException>(() => TimelineEditor.AddTransition(project, "title", "b", TransitionType.FadeToBlack, 1)).Message);
    }
}
