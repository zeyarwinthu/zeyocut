// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class CameraTests
{
    // 640x360: a 200x100 box anchored at (100, 100), a child at (150, 100) on the canvas, and a
    // title that ignores the camera.
    private static Project Scene()
    {
        var project = TestProjects.WithImageAssets("box");
        project.Duration = 4;
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "box", Drawings = new Dictionary<string, string> { ["normal"] = "box" }, X = 100, Y = 100 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "child", ParentId = "box", X = 150, Y = 50, AnchorX = 0, AnchorY = 0 });
        var title = ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "title", X = 320, Y = 40 });
        title.IgnoreCamera = true;
        return project;
    }

    private static (double X, double Y) Anchor(Project project, string id, double time)
    {
        var layer = SceneEvaluator.Evaluate(project, time).Single(e => e.Layer.Id == id);
        return layer.World.Apply(layer.Layer.AnchorX, layer.Layer.AnchorY);
    }

    private static void Near((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    private static Dictionary<string, ZeyoCut.Core.Geometry.Affine2D> Worlds(Project project, double time) =>
        SceneEvaluator.Evaluate(project, time).ToDictionary(e => e.Layer.Id, e => e.World);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void TheCameraMovesZoomsAndTurnsTheSceneButNotLayersThatIgnoreIt()
    {
        var project = Scene();
        var before = Worlds(project, 0);
        Assert.Contains("pass x, y, zoom or rotation", Assert.Throws<ZeyoCutException>(() => CameraEditor.Set(project, null, null, null, null, false)).Message);

        CameraEditor.Set(project, x: null, y: null, zoom: 1, rotation: null, reset: false);
        Assert.Equal((320.0, 180.0), (project.Camera!.X.Value, project.Camera.Y.Value));
        Assert.Equal(before, Worlds(project, 0));

        CameraEditor.Set(project, x: 370, y: null, zoom: null, rotation: null, reset: false);
        Near((50, 100), Anchor(project, "box", 0));
        Near((100, 100), Anchor(project, "child", 0));
        Near((320, 40), Anchor(project, "title", 0));

        CameraEditor.Set(project, x: 100, y: 100, zoom: 2, rotation: null, reset: false);
        Near((320, 180), Anchor(project, "box", 0));
        Near((420, 180), Anchor(project, "child", 0));

        CameraEditor.Set(project, x: null, y: null, zoom: 1, rotation: 90, reset: false);
        Near((320, 180), Anchor(project, "box", 0));
        Near((320, 130), Anchor(project, "child", 0));
        Near((320, 40), Anchor(project, "title", 0));

        var change = CameraEditor.Set(project, null, null, null, null, reset: true);
        Assert.Null(project.Camera);
        Assert.Equal(["removed the camera with its keyframes and shake (the canvas is shown as is)"], change.Changes);
        Assert.Equal(before, Worlds(project, 0));
        Assert.Contains("no camera to reset", Assert.Throws<ZeyoCutException>(() => CameraEditor.Set(project, null, null, null, null, true)).Message);
    }

    [Fact]
    public void CameraKeyframesUseTheReservedIdAndRoundTrip()
    {
        var project = Scene();
        Assert.Contains("the project has no camera yet", Assert.Throws<ZeyoCutException>(() => KeyframeEditor.ClearKeyframes(project, "camera", null)).Message);
        KeyframeEditor.SetKeyframes(project,
        [
            new KeyframeEntry("camera", "x", 0, Json(320), "easeInOut"),
            new KeyframeEntry("camera", "x", 2, Json(420)),
            new KeyframeEntry("camera", "Zoom", 0, Json(1)),
            new KeyframeEntry("camera", "zoom", 2, Json(1.5)),
        ]);
        Near((-160, 60), Anchor(project, "box", 2));
        Near((320, 40), Anchor(project, "title", 2));
        Assert.Equal(370, Interpolator.Evaluate(project.Camera!.X, 1), 9);

        var json = ProjectJson.Serialize(project);
        Assert.Contains("\"camera\": {", json);
        Assert.Contains("\"ignoreCamera\": true", json);
        var copy = ProjectJson.Deserialize(json);
        Assert.Equal(Worlds(project, 1.3), Worlds(copy, 1.3));
        Assert.DoesNotContain("camera", ProjectJson.Serialize(Scene()).Replace("ignoreCamera", ""));

        var bad = Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project,
        [
            new KeyframeEntry("camera", "scaleX", 0, Json(1)),
            new KeyframeEntry("camera", "drawing", 0, Json("normal")),
        ]));
        Assert.Contains("unknown camera property 'scaleX'; camera properties: x, y, zoom, rotation, shakeX, shakeY, shakeRotation", bad.Message);
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("camera", "zoom", 3, Json(0))]);
        Assert.Contains("camera.zoom must be between 0.01 and 100", Assert.Throws<ZeyoCutException>(() => ProjectValidator.ThrowIfInvalid(project)).Message);

        Assert.Equal(1, KeyframeEditor.RemoveKeyframes(project, [new KeyframeRef("camera", "zoom", 3)]));
        Assert.Contains("no keyframe at 5s on camera.x", Assert.Throws<ZeyoCutException>(() => KeyframeEditor.RemoveKeyframes(project, [new KeyframeRef("camera", "x", 5)])).Message);
        Assert.Equal(4, KeyframeEditor.ClearKeyframes(project, "camera", null));
        Assert.Contains("camera: x=320 y=180 zoom=1 rotation=0", LayerTree.Format(project));
        Assert.Contains("  ignoreCamera", LayerTree.Format(project).Split('\n').Single(l => l.StartsWith("title ")));
    }

    [Fact]
    public void TheCameraIdIsReservedAndOnlyTopLevelLayersIgnoreIt()
    {
        var project = Scene();
        Assert.Equal("camera_2", ProjectEditor.NewLayerId(project, null, "Camera", "image"));
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "camera" });
        Assert.Contains("layer id 'camera' is reserved for the project camera", string.Join("\n", ProjectValidator.Validate(project)));

        var child = Scene();
        ProjectEditor.UpdateLayer(child, "child", new JsonObject { ["ignoreCamera"] = true });
        Assert.Contains("layer 'child': ignoreCamera only works on top-level layers", string.Join("\n", ProjectValidator.Validate(child)));
        ProjectEditor.UpdateLayer(child, "child", new JsonObject { ["ignoreCamera"] = false });
        Assert.Empty(ProjectValidator.Validate(child));

        var talk = TestProjects.Empty();
        talk.Assets.Add(new Asset { Id = "talk", Type = AssetType.Audio, File = "assets/talk.wav", Duration = 2 });
        ProjectEditor.AddAudioLayer(talk, new AudioLayerOptions { Asset = "talk", Role = "voice" });
        var transcript = new Transcript { Asset = "talk", Words = [new TranscriptWord { Text = "Hello.", Start = 0.2, End = 0.6 }] };
        CaptionBuilder.Generate(talk, transcript, new CaptionOptions { AssetId = "talk" }, null);
        Assert.True(((VisualLayer)talk.FindLayer("captions")!).IgnoreCamera);
    }

    [Fact]
    public void CameraShakeIsDeterministicAndFadesOut()
    {
        static Project Shaken(int seed)
        {
            var project = Scene();
            CameraEditor.Shake(project, 1, 1.5, 20, 12, seed);
            return project;
        }

        var a = Shaken(7);
        var b = Shaken(7);
        var other = Shaken(8);
        string Keys(Project p) => JsonSerializer.Serialize(new[] { p.Camera!.ShakeX, p.Camera.ShakeY, p.Camera.ShakeRotation }, ProjectJson.Options);
        Assert.Equal(Keys(a), Keys(b));
        Assert.NotEqual(Keys(a), Keys(other));
        for (var frame = 0; frame < 120; frame++)
        {
            Assert.Equal(Worlds(a, frame / 30.0), Worlds(b, frame / 30.0));
        }

        var x = a.Camera!.ShakeX.Keyframes;
        Assert.Equal(19, x.Count);
        Assert.Equal((1.0, 0.0), (x[0].Time, x[0].Value));
        Assert.Equal((2.5, 0.0), (x[^1].Time, x[^1].Value));
        Assert.All(x.Skip(1).SkipLast(1).Zip(x.Skip(2).SkipLast(1)), pair => Assert.True(Math.Sign(pair.First.Value) == -Math.Sign(pair.Second.Value)));
        Assert.All(x, k => Assert.True(Math.Abs(k.Value) <= 20 * (1 - (k.Time - 1) / 1.5) + 1e-3));
        Assert.All(a.Camera.ShakeRotation.Keyframes, k => Assert.True(Math.Abs(k.Value) <= 1));
        Assert.Equal(Anchor(Scene(), "box", 0.5), Anchor(a, "box", 0.5));
        Assert.NotEqual(Anchor(Scene(), "box", 1.5), Anchor(a, "box", 1.5));
        Assert.Equal(Anchor(Scene(), "title", 1.5), Anchor(a, "title", 1.5));

        var again = CameraEditor.Shake(a, 2, 1, 5, 4, 3);
        Assert.Equal(15, again.Keyframes);
        Assert.Equal([1.0, 1.083333333, 1.166666667], a.Camera.ShakeX.Keyframes.Take(3).Select(k => k.Time));
        Assert.Contains("past the project's end", Assert.Single(CameraEditor.Shake(a, 3.5, 1, 5, 4, 3).Warnings));
        Assert.Contains("more than half the frame rate", Assert.Single(CameraEditor.Shake(a, 0, 1, 5, 20, 3).Warnings));
        Assert.Contains("intensity must be greater than 0", Assert.Throws<ZeyoCutException>(() => CameraEditor.Shake(a, 0, 1, 0, 4, 3)).Message);
    }

    [Fact]
    public void RippleEditsKeepTheCameraInStepWithThePicture()
    {
        var project = Scene();
        KeyframeEditor.SetKeyframes(project,
        [
            new KeyframeEntry("camera", "x", 0, Json(320)),
            new KeyframeEntry("camera", "x", 4, Json(720)),
            new KeyframeEntry("camera", "zoom", 1.5, Json(1), "hold"),
            new KeyframeEntry("camera", "zoom", 3, Json(2)),
        ]);
        var original = ProjectJson.Clone(project);

        TimelineEditor.RemoveRange(project, "all", 1, 2, ripple: true);

        Assert.Equal(3, project.Duration);
        foreach (var (after, before) in new[] { (0.0, 0.0), (0.5, 0.5), (0.9, 0.9), (1.0, 2.0), (1.5, 2.5), (2.2, 3.2), (2.9, 3.9) })
        {
            var expected = SceneEvaluator.CameraMatrix(original, before);
            var actual = SceneEvaluator.CameraMatrix(project, after);
            Assert.Equal((expected.A, expected.D, expected.E, expected.F), (Math.Round(actual.A, 9), Math.Round(actual.D, 9), Math.Round(actual.E, 9), Math.Round(actual.F, 9)));
        }

        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void ANewCanvasSizeKeepsTheCameraOnTheCenter()
    {
        var project = Scene();
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("camera", "x", 0, Json(320)), new KeyframeEntry("camera", "x", 1, Json(400))]);

        var update = ProjectEditor.UpdateProject(project, new ProjectOptions { Width = 1280, Height = 720 });

        Assert.Contains("camera moved by (320,180) to keep its view on the new canvas center", update.Changes);
        Assert.Equal([640.0, 720.0], project.Camera!.X.Keyframes.Select(k => k.Value));
        Assert.Equal(360, project.Camera.Y.Value);
        Near((100, 100), Anchor(project, "box", 0));
        Near((100 - 80, 100), Anchor(project, "box", 1));
    }
}
