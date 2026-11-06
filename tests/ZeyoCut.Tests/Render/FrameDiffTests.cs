// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Mcp;
using ZeyoCut.Render;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class FrameDiffTests
{
    private static Keyframe Key(double time, double value, Easing easing = Easing.Linear) => new() { Time = time, Value = value, Easing = easing };

    private static Asset Import(ProjectSession session, string file) =>
        session.Mutate("import", p => AssetImporter.Import(p, session.Directory, file, null, new AssetProbe()));

    private static Dictionary<string, string> Drawing(Asset asset) => new() { ["n"] = asset.Id };

    [Fact]
    public void FrameDiffFlagsAnInsertedGlitchFrame()
    {
        var directory = TestPaths.NewDirectory("framediff-glitch");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 180, fps: 10, duration: 4));
        var dot = Import(session, TestImages.Solid(Path.Combine(directory, "dot.png"), 30, 30, SKColors.Black));
        var block = Import(session, TestImages.Solid(Path.Combine(directory, "block.png"), 60, 60, SKColors.Black));
        var logo = Import(session, TestImages.Solid(Path.Combine(directory, "logo.png"), 100, 100, SKColors.Black));
        var black = Import(session, TestImages.Solid(Path.Combine(directory, "black.png"), 320, 180, SKColors.Black));
        session.Mutate("scene", p =>
        {
            var ball = ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "ball", Drawings = Drawing(dot), Y = 40 });
            ball.X.Keyframes = [Key(0, 30), Key(4, 290)];
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "flash", Drawings = Drawing(black), X = 160, Y = 90, Start = 1, End = 1.1, ZIndex = 5 });
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "logo", Drawings = Drawing(logo), X = 160, Y = 120, Start = 2.5 });
            var jumper = ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "jumper", Drawings = Drawing(block), Y = 140 });
            jumper.X.Keyframes = [Key(3, 40), Key(3.1, 280)];
            return jumper;
        });

        using var renderer = new FrameRenderer(directory);
        var progress = new List<double>();
        var result = FrameDiff.Run(renderer, session.Snapshot(), 0, null, progress.Add, CancellationToken.None);
        var scores = result.Scores.Frames;
        var text = result.Describe();

        Assert.Equal(160, result.Scores.Width);
        Assert.Equal(90, result.Scores.Height);
        Assert.Equal(40, scores.Count);
        Assert.Equal(1, progress[^1]);
        Assert.Equal(["glitch", "cut", "spike"], scores.Where(s => s.Label is not null).Select(s => s.Label));
        var glitch = Assert.Single(scores, s => s.Label == "glitch");
        Assert.Equal(10, glitch.Frame);
        Assert.Contains("differs from both neighbors", glitch.Note);
        Assert.Contains("'flash' starts here", glitch.Note);
        Assert.Equal(25, Assert.Single(scores, s => s.Label == "cut").Frame);
        Assert.Equal(31, Assert.Single(scores, s => s.Label == "spike").Frame);
        Assert.InRange(scores[5].Change, 0.1, 2);
        Assert.Equal(0, scores[0].Change);

        Assert.StartsWith("frame_diff 0–3.9s (f0–f39, 40 frames compared at 160x90): median change ", text);
        Assert.Contains("\nflagged (2):\n- f10 1s glitch: differs from both neighbors (", text);
        Assert.Contains("\n- f31 3.1s spike: ", text);
        Assert.Contains("with no layer starting, ending or swapping drawings there", text);
        Assert.Contains("\nexpected cuts (1): f25 2.5s ('logo' starts)", text);
        Assert.EndsWith(Path.Combine(directory, "renders", "frame_diff_00000-00039.json"), text);
        var saved = AnalysisFiles.Load<FrameDiffFile>(result.SavedPath)!;
        Assert.Equal(scores, saved.Frames);
    }

    [Fact]
    public void ASmoothSceneHasNoFlags()
    {
        var directory = TestPaths.NewDirectory("framediff-smooth");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 180, fps: 10, duration: 2));
        var dot = Import(session, TestImages.Solid(Path.Combine(directory, "dot.png"), 30, 30, SKColors.Black));
        session.Mutate("scene", p =>
        {
            var ball = ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "ball", Drawings = Drawing(dot), Y = 90 });
            ball.X.Keyframes = [Key(0, 30), Key(2, 290)];
            return ball;
        });

        using var renderer = new FrameRenderer(directory);
        var result = FrameDiff.Run(renderer, session.Snapshot(), 0.5, 1.5, null, CancellationToken.None);

        Assert.Equal(5, result.Scores.StartFrame);
        Assert.Equal(15, result.Scores.EndFrame);
        Assert.All(result.Scores.Frames, s => Assert.Null(s.Label));
        Assert.Contains("\nno glitches and no unexplained spikes\nscores: ", result.Describe());
        Assert.EndsWith("frame_diff_00005-00015.json", result.SavedPath);
    }

    [Fact]
    public void FastSmoothMotionIsNotASpikeButAFaintOneFrameLayerIsAGlitch()
    {
        var directory = TestPaths.NewDirectory("framediff-fast");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 180, fps: 10, duration: 3));
        var block = Import(session, TestImages.Solid(Path.Combine(directory, "block.png"), 100, 100, SKColors.Black));
        var haze = Import(session, TestImages.Solid(Path.Combine(directory, "haze.png"), 320, 180, new SKColor(240, 240, 240)));
        session.Mutate("scene", p =>
        {
            var pan = ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "pan", Drawings = Drawing(block), Y = 90 });
            pan.X.Keyframes = [Key(0.5, -60), Key(1.1, 380)];
            ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "haze", Drawings = Drawing(haze), X = 160, Y = 90, Start = 2, End = 2.1 });
            return pan;
        });

        using var renderer = new FrameRenderer(directory);
        var result = FrameDiff.Run(renderer, session.Snapshot(), 0, null, null, CancellationToken.None);
        var scores = result.Scores.Frames;

        Assert.True(scores[8].Change > 20, $"the pan should change frame 8 a lot (got {scores[8].Change}%)");
        Assert.Equal([20], scores.Where(s => s.Label is not null).Select(s => s.Frame));
        Assert.Equal("glitch", scores[20].Label);
        Assert.InRange(scores[20].Change, 2, 8);
    }

    [Fact]
    public void CameraShakeIsNotFlagged()
    {
        var directory = TestPaths.NewDirectory("framediff-shake");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 180, fps: 30, duration: 2));
        var block = Import(session, TestImages.Solid(Path.Combine(directory, "block.png"), 80, 80, SKColors.Black));
        session.Mutate("scene", p => ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Id = "block", Drawings = Drawing(block), X = 160, Y = 90 }));
        session.Mutate("shake", p => CameraEditor.Shake(p, 0.3, 1.2, 20, 12, 5));

        using var renderer = new FrameRenderer(directory);
        var result = FrameDiff.Run(renderer, session.Snapshot(), 0, null, null, CancellationToken.None);
        var scores = result.Scores.Frames;

        Assert.True(scores.Max(s => s.Change) > 5, "the shake should move the block");
        Assert.All(scores, s => Assert.Null(s.Label));
        var shake = Assert.Single(result.Scores.Shake);
        Assert.InRange(shake.Start, 0.29, 0.31);
        Assert.InRange(shake.End, 1.49, 1.51);
        Assert.Contains("\nnot checked for glitches and spikes: camera shake at 0.3", result.Describe());
    }

    [Fact]
    public void LossyMovingVideoIsNotFlaggedAndItsAnalyzedSceneCutIsExpected()
    {
        var ffmpeg = Ffmpeg.Require();
        var directory = TestPaths.NewDirectory("framediff-video");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 320, height: 240, fps: 30, duration: 6));
        TestVideos.Import(session, TestVideos.Pattern(Path.Combine(directory, "src_pattern.mp4")), "pattern");
        var scene = TestVideos.Import(session, TestVideos.ToneGapTone(Path.Combine(directory, "src_scene.mp4")), "scene");
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "moving", Asset = "pattern" }));
        session.Mutate("add", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "colors", Asset = "scene", Start = 2 }));
        AnalysisFiles.Save(AnalysisFiles.AnalysisPath(directory, scene), new MediaAnalysis { Asset = "scene", Scenes = [new SceneCut(2, 0.9)] });

        using var renderer = new FrameRenderer(directory, () => ffmpeg.Ffmpeg, streamVideo: true);
        var result = FrameDiff.Run(renderer, session.Snapshot(), 0, null, null, CancellationToken.None);
        var labeled = result.Scores.Frames.Where(s => s.Label is not null).ToList();

        Assert.True(result.Scores.Frames.Take(60).Skip(1).Average(s => s.Change) > 0.5, "the pattern clip should move");
        Assert.Equal([(60, "cut"), (120, "cut")], labeled.Select(s => (s.Frame, s.Label!)));
        Assert.Equal("scene cut in 'colors'", labeled[1].Note);
    }

    [Fact]
    public void HoldKeyframesDrawingSwapsAndSceneCutsAreExpectedChanges()
    {
        var directory = TestPaths.NewDirectory("framediff-expected");
        var project = TestProjects.Empty(width: 320, height: 180, fps: 10, duration: 4);
        project.Assets.Add(new Asset { Id = "a", Type = AssetType.Image, File = "assets/a.png", Width = 10, Height = 10 });
        project.Assets.Add(new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 64, Height = 48, Duration = 10 });
        var sprite = ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "sprite", Drawings = new Dictionary<string, string> { ["open"] = "a", ["shut"] = "a" }, Start = 0.5, End = 3 });
        sprite.Drawing.Keyframes = [new DrawingKeyframe { Time = 0.5, Value = "open" }, new DrawingKeyframe { Time = 1.2, Value = "shut" }];
        sprite.Y.Keyframes = [Key(0.5, 10, Easing.Hold), Key(1.7, 50), Key(2, 50, Easing.Hold), Key(2.4, 50)];
        project.Layers.Add(new VideoLayer { Id = "video", Asset = "clip", TrimIn = 2, TrimOut = 6, Speed = 2, Start = 1, ZIndex = -1 });
        project.Camera = new Camera();
        project.Camera.Zoom.Keyframes = [Key(3.3, 1, Easing.Hold), Key(3.6, 2)];
        AnalysisFiles.Save(AnalysisFiles.AnalysisPath(directory, project.FindAsset("clip")!), new MediaAnalysis
        {
            Asset = "clip",
            Scenes = [new SceneCut(1, 0.9), new SceneCut(4, 0.8), new SceneCut(7, 0.7)],
        });

        var expected = FrameDiff.ExpectedChanges(project, directory);

        Assert.Equal("'sprite' starts", expected[5]);
        Assert.Equal("'sprite' swaps to 'shut'", expected[12]);
        Assert.Equal("'sprite'.y jumps (hold)", expected[17]);
        Assert.False(expected.ContainsKey(24));
        Assert.Equal("'sprite' ends", expected[30]);
        Assert.Equal("'video' starts", expected[10]);
        Assert.Equal("scene cut in 'video'", expected[20]);
        Assert.Equal("camera.zoom jumps (hold)", expected[36]);
        Assert.Single(expected.Values, v => v.StartsWith("scene cut"));
    }

    [Fact]
    public void FrameDiffNeedsAtLeastThreeFrames()
    {
        var project = TestProjects.Empty(fps: 10, duration: 2);

        Assert.Equal((0, 19), FrameDiff.Frames(project, 0, null));
        Assert.Equal((5, 19), FrameDiff.Frames(project, 0.5, 5));
        Assert.Equal("compare at least 3 frames; 1–1.1s covers 2 at 10 fps", Assert.Throws<ZeyoCutException>(() => FrameDiff.Frames(project, 1, 1.1)).Message);
        Assert.Equal("the range must have 0 <= startTime < endTime (got 1–0.5)", Assert.Throws<ZeyoCutException>(() => FrameDiff.Frames(project, 1, 0.5)).Message);
    }
}
