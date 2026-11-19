// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// set_color and the editor's Color view: a clip's grade, contrast and saturation set, copied from
// another clip, put back as shot and given to every clip of the same footage.
public class ColorTests
{
    private static VideoLayer Clip(Project project, string id) => Assert.IsType<VideoLayer>(project.FindLayer(id));

    private static Dictionary<string, double> Values(params (string Name, double Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);

    // Footage's two clips of 'clip', and a third clip of another video.
    private static Project Footage()
    {
        var project = TestProjects.Footage();
        project.Assets.Add(new Asset { Id = "other", Type = AssetType.Video, File = "assets/other.mp4", Width = 640, Height = 360, FrameRate = "30", FrameCount = 300, Duration = 10, Codec = "h264" });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "other", Id = "other_1", Start = 0, TrimIn = 0, TrimOut = 2, ZIndex = 3 });
        return project;
    }

    [Fact]
    public void AGradeIsSavedWithTheClipAndGoesWhenItsValuesAreAsShot()
    {
        var project = Footage();
        var result = ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("exposure", 0.3), ("temperature", 0.35), ("saturation", 1.2))));
        Assert.Equal("color of 'clip_1': exposure 0.3, temperature 0.35, saturation 1.2", result.Summary);
        Assert.Equal(["clip_1"], result.Layers);
        Assert.Empty(result.Warnings);

        var clip = Clip(project, "clip_1");
        Assert.Equal((0.3, 0.0, 0.0, 0.35, 0.0), (clip.Grade!.Exposure.Value, clip.Grade.Highlights.Value, clip.Grade.Shadows.Value, clip.Grade.Temperature.Value, clip.Grade.Tint.Value));
        Assert.Equal(1.2, clip.Saturation.Value);
        Assert.Null(Clip(project, "clip_2").Grade);
        Assert.True(ColorEditor.Graded(clip));
        Assert.False(ColorEditor.Graded(Clip(project, "clip_2")));

        // Saved in the project at format 12, and read back the same.
        var json = ProjectJson.Serialize(project);
        Assert.Contains("\"grade\": {", json);
        Assert.Equal(12, project.FormatVersion);
        var read = Clip(ProjectJson.Deserialize(json), "clip_1");
        Assert.Equal(0.35, read.Grade!.Temperature.Value);
        Assert.Empty(ProjectValidator.Validate(ProjectJson.Deserialize(json)));
        Assert.Contains("effects: grade(exposure 0.3, temperature 0.35)", LayerTree.Describe(read));

        // Back to 0, the grade goes; contrast and saturation stay on the clip as ever.
        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("exposure", 0), ("temperature", 0))));
        Assert.Null(clip.Grade);
        Assert.DoesNotContain("\"grade\"", ProjectJson.Serialize(project));
        Assert.Equal("saturation 1.2", ColorEditor.Describe(clip));
    }

    [Fact]
    public void ValuesOutsideTheirRangeAndLayersWithoutColorAreRefused()
    {
        var project = Footage();
        Assert.Equal("exposure must be between -3 and 3 (got 4)",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("exposure", 4))))).Message);
        Assert.Equal("tint must be between -1 and 1 (got -1.5)",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("tint", -1.5))))).Message);
        Assert.Equal("unknown color control 'warmth'; controls: exposure, contrast, highlights, shadows, temperature, tint, saturation",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("warmth", 1))))).Message);
        Assert.Equal("'label' is a text layer; color is for image and video layers",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "label", new ColorChange(Values(("exposure", 1))))).Message);
        Assert.StartsWith("nothing to change",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values()))).Message);

        // A grade written by hand out of range is a problem the validator names.
        Clip(project, "clip_1").Grade = new ColorGrade { Shadows = new AnimatedValue(2) };
        Assert.Contains(ProjectValidator.Validate(project), e => e.Contains("grade.shadows", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGradeIsAnimatableOnceAClipHasOne()
    {
        var project = Footage();
        var key = new KeyframeEntry("clip_1", "grade.exposure", 1, JsonSerializer.SerializeToElement(1.0), null, null);
        Assert.Contains("'clip_1' has no grade; set_color gives it one", Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [key])).Message);
        Assert.Contains("'grade.exposure' is only on image and video layers; 'label' is a text layer",
            Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [key with { LayerId = "label" }])).Message);

        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("tint", 0.2))));
        KeyframeEditor.SetKeyframes(project, [key, key with { Time = 3, Value = JsonSerializer.SerializeToElement(-1.0) }]);
        var grade = Clip(project, "clip_1").Grade!;
        Assert.Equal(0, Interpolator.Evaluate(grade.Exposure, 2), 6);
        Assert.Contains(AnimatableProperties.Numeric(Clip(project, "clip_1")), p => p.Name == "grade.tint");

        // A value set on a keyframed one is its static value, as update_layer's are.
        var result = ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("exposure", 0.5))));
        Assert.Equal(["grade.exposure has 2 keyframe(s), so the value you set is only used after clear_keyframes"], result.Warnings);
        Assert.Equal("color of 'clip_1': exposure animated (2 keyframes), tint 0.2", result.Summary);

        // The inspector's way: a keyframed value gets a keyframe at the playhead, the others their
        // static value.
        new BatchOperation("set", [ValueEditor.Set(project, "clip_1", new Dictionary<string, double> { ["grade.exposure"] = 0.25, ["grade.tint"] = -0.3 }, 2)]).Apply(project);
        Assert.Equal(3, grade.Exposure.Keyframes.Count);
        Assert.Equal(-0.3, Clip(project, "clip_1").Grade!.Tint.Value);
    }

    [Fact]
    public void AnotherClipsColorIsCopiedAsFixedValues()
    {
        var project = Footage();
        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("exposure", -0.5), ("contrast", 1.12), ("shadows", 0.18))));
        var other = Clip(project, "other_1");
        other.Saturation.Keyframes.Add(new Keyframe { Time = 0, Value = 0 });
        other.Saturation.Keyframes.Add(new Keyframe { Time = 1, Value = 1 });

        var result = ColorEditor.SetColor(project, "other_1", new ColorChange(Values(("highlights", -0.2)), From: "clip_1"));
        Assert.Equal("copied the color of 'clip_1' to 'other_1': exposure -0.5, contrast 1.12, highlights -0.2, shadows 0.18", result.Summary);
        Assert.Equal(["the keyframes of 'other_1''s saturation were taken away"], result.Warnings);
        Assert.Empty(other.Saturation.Keyframes);
        Assert.NotSame(Clip(project, "clip_1").Grade, other.Grade);
        Assert.Equal(0, Clip(project, "clip_1").Grade!.Highlights.Value);

        Assert.Equal("'clip_1' can't take its own color; from names another clip",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(), From: "clip_1"))).Message);
        Clip(project, "clip_1").Contrast.Keyframes.Add(new Keyframe { Time = 1, Value = 2 });
        Assert.Equal("'clip_1' animates its contrast with keyframes; its color is copied as fixed values, so clear_keyframes first",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "other_1", new ColorChange(Values(), From: "clip_1"))).Message);
    }

    [Fact]
    public void SameFootageGivesEveryClipOfTheVideoTheColorAndResetPutsItBack()
    {
        var project = Footage();
        Assert.Equal(["clip_2"], ColorEditor.SameFootage(project, "clip_1").Select(l => l.Id));
        Assert.Empty(ColorEditor.SameFootage(project, "other_1"));
        Assert.Equal("'other_1' is the only clip of 'other'",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "other_1", new ColorChange(Values(("tint", 0.1)), SameFootage: true))).Message);

        var result = ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("temperature", 0.4), ("saturation", 0.8)), SameFootage: true));
        Assert.Equal("color of 'clip_1': temperature 0.4, saturation 0.8; the same on clip_2 (every clip of 'clip')", result.Summary);
        Assert.Equal(["clip_1", "clip_2"], result.Layers);
        Assert.Equal((0.4, 0.8), (Clip(project, "clip_2").Grade!.Temperature.Value, Clip(project, "clip_2").Saturation.Value));
        Assert.Null(Clip(project, "other_1").Grade);

        // Pictures of one image are one footage too.
        var pictures = TestProjects.Rig();
        ProjectEditor.AddImageLayer(pictures, new ImageLayerOptions { Id = "tail_2", Drawings = new Dictionary<string, string> { ["normal"] = "tail" } });
        Assert.Equal(["tail_2"], ColorEditor.SameFootage(pictures, "tora_tail").Select(l => l.Id));

        var reset = ColorEditor.SetColor(project, "clip_2", new ColorChange(Values(), Reset: true));
        Assert.Equal("put the color of 'clip_2' back as shot", reset.Summary);
        Assert.Null(Clip(project, "clip_2").Grade);
        Assert.Equal((1.0, 1.0), (Clip(project, "clip_2").Contrast.Value, Clip(project, "clip_2").Saturation.Value));
        Assert.Equal("as shot", ColorEditor.Describe(Clip(project, "clip_2")));
        Assert.NotNull(Clip(project, "clip_1").Grade);
    }

    // The editor's operation makes set_color's change, as one undo step of the session.
    [Fact]
    public void TheOperationIsOneStepThatUndoes()
    {
        var directory = TestPaths.NewDirectory("color-undo");
        var session = ProjectSession.Create(directory, Footage());
        var before = ProjectJson.Serialize(session.Snapshot());
        var operation = new SetColorOperation("clip_1", new ColorChange(Values(("exposure", 1)), SameFootage: true));
        Assert.Equal("set_color clip_1", operation.Label);
        session.Mutate(operation.Label, operation.Apply);
        Assert.NotNull(Clip(session.Snapshot(), "clip_2").Grade);
        Assert.Equal("set_color clip_1", session.Undo());
        Assert.Equal(TestProjects.WithoutRevision(before), TestProjects.WithoutRevision(ProjectJson.Serialize(session.Snapshot())));
    }
}
