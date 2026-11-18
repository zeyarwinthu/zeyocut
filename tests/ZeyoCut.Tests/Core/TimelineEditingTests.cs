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

    private static KeyframeEntry Key(string layer, string property, double time, object value, string? easing = null, double[]? bezier = null) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value), easing, bezier);

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
            Key("tora_tail", "rotation", 0.2, 0, bezier: [0.2, -0.5, 0.7, 1.3]),
            Key("tora_tail", "rotation", 2.8, 40),
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

    // Two clips on draw level 0 (0–4 s and 4–8 s) with a text attached to the second,
    // a title on level 1 from 1 to 3 s, a logo on level 1 from 5 s to the end, music from 0 to 8 s,
    // a voice from 5 to 7 s and a marker at 6 s; the clip to place is 2 s of footage.
    private static Project Sequence()
    {
        var project = TestProjects.WithImageAssets("logo", "card");
        project.Duration = 8;
        project.Assets.Add(Clip());
        project.Assets.Add(Clip("broll", 6));
        project.Assets.Add(new Asset { Id = "song", Type = AssetType.Audio, File = "assets/song.mp3", Duration = 20 });
        project.Assets.Add(new Asset { Id = "talk", Type = AssetType.Audio, File = "assets/talk.wav", Duration = 2 });
        project.Assets.Add(new Asset { Id = "sting", Type = AssetType.Audio, File = "assets/sting.wav", Duration = 1.5 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "intro", Start = 0, TrimIn = 0, TrimOut = 4, ZIndex = 0 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "clip", Id = "main", Start = 4, TrimIn = 4, TrimOut = 8, ZIndex = 0 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "label", Text = "Second", ParentId = "main", Start = 4, End = 8, ZIndex = 2 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 1, End = 3, ZIndex = 1 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Start = 5, ZIndex = 1 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "song", Id = "music", End = 8 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "talk", Id = "talk", Start = 5, Role = "voice" });
        KeyframeEditor.SetKeyframes(project, [Key("main", "x", 4, 300), Key("main", "x", 8, 340), Key("logo", "opacity", 5, 0), Key("logo", "opacity", 6, 1)]);
        project.Markers.Add(new Marker { Id = "m1", Time = 6, Name = "Outro" });
        return project;
    }

    // Insert at 3 s: the clip under 3 s on its level is split there, everything that starts at 3 s or
    // later goes 2 s later with its children, keyframes and markers, and the project is 2 s longer.
    // What played before 3 s plays as it did, and so does what played after, 2 s later; layers that
    // started before (the title, the music) stay as they were.
    [Fact]
    public void InsertSplitsTheLaneAtThePlayheadAndPushesWhatComesLater()
    {
        var project = Sequence();
        project.Layers.Remove(project.FindLayer("music")!);
        project.Layers.Remove(project.FindLayer("title")!);
        var before = Times(project, 8).ToDictionary(t => Math.Round(t * 30), t => Frame(project, t));
        var added = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "broll", Id = "broll", Start = 3, TrimIn = 1, TrimOut = 3, ZIndex = 0 });

        Assert.Equal("split 'intro' at 3s into 'intro' and 'intro_2'; moved 5 later layer(s) 2s later; the project is 10s long (was 8s)", TimelineEditor.Place(project, added, ClipEdit.Insert, 8));

        Assert.Equal(10, project.Duration);
        var tail = Assert.IsType<VideoLayer>(project.FindLayer("intro_2"));
        Assert.Equal((5.0, 3.0, 4.0), (tail.Start, tail.TrimIn, tail.TrimOut));
        Assert.Equal(3, ((VideoLayer)project.FindLayer("intro")!).TrimOut);
        Assert.Equal((6.0, 10.0), (project.FindLayer("main")!.Start, ((VideoLayer)project.FindLayer("main")!).TimelineEnd));
        Assert.Equal((6.0, 10.0), (project.FindLayer("label")!.Start, project.FindLayer("label")!.End));
        Assert.Equal(7, project.FindLayer("logo")!.Start);
        Assert.Equal(7, project.FindLayer("talk")!.Start);
        Assert.Equal(8, Assert.Single(project.Markers).Time);
        Assert.Empty(ProjectValidator.Validate(project));

        foreach (var t in Times(project, 10))
        {
            if (t < 3 - 1e-9)
            {
                SameFrame(before[Math.Round(t * 30)], Frame(project, t), $"t={At(t)}");
            }
            else if (t >= 5 - 1e-9)
            {
                SameFrame(before[Math.Round((t - 2) * 30)], Frame(project, t), $"t={At(t)} (was {At(t - 2)})");
            }
            else
            {
                Assert.Equal("broll", Frame(project, t)[0].Content);
            }
        }
    }

    [Fact]
    public void InsertLeavesLayersThatStartedBeforeWhereTheyAre()
    {
        var project = Sequence();
        var added = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "broll", Id = "broll", Start = 2, TrimIn = 0, TrimOut = 2, ZIndex = 0 });
        TimelineEditor.Place(project, added, ClipEdit.Insert, 8);

        Assert.Equal((1.0, 3.0), (project.FindLayer("title")!.Start, project.FindLayer("title")!.End));
        Assert.Equal((0.0, 8.0), (project.FindLayer("music")!.Start, project.FindLayer("music")!.End));
        Assert.Equal(6, project.FindLayer("main")!.Start);
        Assert.Equal(10, project.Duration);

        // At the end nothing starts there to move, and the project grows to fit the clip as it would anyway.
        var last = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "broll", Id = "end", Start = 10, TrimIn = 0, TrimOut = 1, ZIndex = 0 });
        Assert.Equal("the project is 11s long (was 10s)", TimelineEditor.Place(project, last, ClipEdit.Insert, 10));
        Assert.Equal(11, project.Duration);
    }

    // Overwrite from 3 to 5 s: the lane's clips under it are cut (intro's end, main's start with
    // the text attached to it), other lanes keep everything, and every frame outside 3–5 s is as it was.
    [Fact]
    public void OverwriteCutsOnlyWhatTheLaneHasUnderTheClip()
    {
        var project = Sequence();
        var before = Times(project, 8).ToDictionary(t => Math.Round(t * 30), t => Frame(project, t));
        var added = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "broll", Id = "broll", Start = 3, TrimIn = 1, TrimOut = 3, ZIndex = 0 });

        Assert.Equal("cut 'intro', 'main' under it", TimelineEditor.Place(project, added, ClipEdit.Overwrite, 8));

        Assert.Equal(8, project.Duration);
        Assert.Equal(3, ((VideoLayer)project.FindLayer("intro")!).TrimOut);
        var second = (VideoLayer)project.FindLayer("main")!;
        Assert.Equal((5.0, 5.0, 8.0), (second.Start, second.TrimIn, second.TrimOut));
        Assert.Equal((5.0, 8.0), (project.FindLayer("label")!.Start, project.FindLayer("label")!.End));
        Assert.Equal((1.0, 3.0), (project.FindLayer("title")!.Start, project.FindLayer("title")!.End));
        Assert.Equal((0.0, 8.0), (project.FindLayer("music")!.Start, project.FindLayer("music")!.End));
        Assert.Equal(6, Assert.Single(project.Markers).Time);
        Assert.Empty(ProjectValidator.Validate(project));
        foreach (var t in Times(project, 8).Where(t => t < 3 - 1e-9 || t >= 5 - 1e-9))
        {
            SameFrame(before[Math.Round(t * 30)], Frame(project, t), $"t={At(t)}");
        }

        // Over the middle of a clip it splits it; over a whole one it removes it.
        var middle = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "broll", Id = "middle", Start = 6, TrimIn = 0, TrimOut = 1, ZIndex = 0 });
        Assert.Equal("cut 'main' under it; new pieces: main_2 (from main)", TimelineEditor.Place(project, middle, ClipEdit.Overwrite, 8));
        Assert.Equal((5.0, 6.0), (project.FindLayer("main")!.Start, ((VideoLayer)project.FindLayer("main")!).TimelineEnd));
        Assert.Equal((7.0, 8.0), (project.FindLayer("main_2")!.Start, ((VideoLayer)project.FindLayer("main_2")!).TimelineEnd));
        var whole = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "broll", Id = "whole", Start = 2.5, TrimIn = 0, TrimOut = 3, ZIndex = 0 });
        Assert.Equal("cut 'intro', 'main' under it; removed 'broll'", TimelineEditor.Place(project, whole, ClipEdit.Overwrite, 8));
        Assert.Null(project.FindLayer("broll"));
    }

    // A sound's lane is its role: overwrite and insert cut and split that role's sound only.
    [Fact]
    public void SoundGoesOnItsRolesLane()
    {
        var project = Sequence();
        var sting = ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "sting", Id = "sting", Start = 6, Role = "voice" });
        Assert.Equal("cut 'talk' under it", TimelineEditor.Place(project, sting, ClipEdit.Overwrite, 8));
        var talk = (AudioLayer)project.FindLayer("talk")!;
        Assert.Equal((5.0, 6.0), (talk.Start, talk.End));
        Assert.Equal((0.0, 8.0), (project.FindLayer("music")!.Start, project.FindLayer("music")!.End));

        var music = ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "sting", Id = "bed", Start = 2 });
        Assert.Equal("split 'music' at 2s into 'music' and 'music_2'; moved 6 later layer(s) 1.5s later; the project is 9.5s long (was 8s)", TimelineEditor.Place(project, music, ClipEdit.Insert, 8));
        var tail = (AudioLayer)project.FindLayer("music_2")!;
        Assert.Equal((3.5, 2.0, 9.5), (tail.Start, tail.TrimIn, tail.End));
        Assert.Equal(2, project.FindLayer("music")!.End);
    }

    [Fact]
    public void InsertAndOverwriteNeedAClipOfItsOwnWithALength()
    {
        var project = Sequence();
        var card = ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "card", Drawings = new Dictionary<string, string> { ["normal"] = "card" }, Start = 2, ZIndex = 0 });
        Assert.Equal("insert needs the picture's length: give it an end", Assert.Throws<ZeyoCutException>(() => TimelineEditor.Place(project, card, ClipEdit.Insert, 8)).Message);
        var child = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "broll", Id = "child", ParentId = "intro", Start = 1, TrimOut = 1, ZIndex = 0 });
        Assert.Equal("overwrite places a clip of its own: a video, picture or sound without a parent", Assert.Throws<ZeyoCutException>(() => TimelineEditor.Place(project, child, ClipEdit.Overwrite, 8)).Message);
        Assert.Equal("", TimelineEditor.Place(project, card, ClipEdit.Overlay, 8));
    }
}
