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

        // Saved in the project at format 13, and read back the same.
        var json = ProjectJson.Serialize(project);
        Assert.Contains("\"grade\": {", json);
        Assert.Equal(13, project.FormatVersion);
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
        Assert.Equal("unknown color control 'warmth'; controls: exposure, contrast, highlights, shadows, temperature, tint, saturation, "
            + "lift.red, lift.green, lift.blue, gamma.red, gamma.green, gamma.blue, gain.red, gain.green, gain.blue, look.amount",
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
        Assert.Equal(["'other_1' lost its keyframes on saturation"], result.Warnings);
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

    // The wheels: lift, gamma and gain each move red, green and blue, saved once one of them is set
    // and gone again when all three are back at 0.
    [Fact]
    public void AWheelIsSavedOnceItsSetAndGoesWhenItsNeutral()
    {
        var project = Footage();
        var result = ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("lift.red", -0.02), ("lift.blue", 0.04), ("gain.green", 0.1))));
        Assert.Equal("color of 'clip_1': lift.red -0.02, lift.blue 0.04, gain.green 0.1", result.Summary);
        var grade = Clip(project, "clip_1").Grade!;
        Assert.Equal((-0.02, 0.0, 0.04), (grade.Lift!.Red.Value, grade.Lift.Green.Value, grade.Lift.Blue.Value));
        Assert.Null(grade.Gamma);
        Assert.Equal(0.1, grade.Gain!.Green.Value);
        Assert.Equal(0, grade.Exposure.Value);

        // In project.json under the grade, and read back the same.
        var json = ProjectJson.Serialize(project);
        Assert.Contains("\"lift\": {", json);
        Assert.DoesNotContain("\"gamma\"", json);
        Assert.Equal(0.04, Clip(ProjectJson.Deserialize(json), "clip_1").Grade!.Lift!.Blue.Value);
        Assert.Empty(ProjectValidator.Validate(ProjectJson.Deserialize(json)));

        // Out of range, by set_color or by hand.
        Assert.Equal("gamma.blue must be between -1 and 1 (got 2)",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("gamma.blue", 2))))).Message);
        grade.Gain.Red = new AnimatedValue(-3);
        Assert.Contains(ProjectValidator.Validate(project), e => e.Contains("grade.gain.red", StringComparison.Ordinal));
        grade.Gain.Red = new AnimatedValue(0);

        // Animatable once the wheel is there.
        var key = new KeyframeEntry("clip_1", "grade.gamma.red", 1, JsonSerializer.SerializeToElement(0.5), null, null);
        Assert.Contains("'clip_1' has no gamma wheel; set_color's gamma sets it", Assert.Throws<ZeyoCutException>(() => KeyframeEditor.SetKeyframes(project, [key])).Message);
        KeyframeEditor.SetKeyframes(project, [key with { Property = "grade.lift.red" }, key with { Property = "grade.lift.red", Time = 3, Value = JsonSerializer.SerializeToElement(0.0) }]);
        Assert.Equal(0.25, Interpolator.Evaluate(grade.Lift.Red, 2), 6);
        Assert.Contains(AnimatableProperties.Numeric(Clip(project, "clip_1")), p => p.Name == "grade.gain.blue");
        Assert.Equal("lift.red animated (2 keyframes), lift.blue 0.04, gain.green 0.1", ColorEditor.Describe(Clip(project, "clip_1"), grade: true));

        // A keyframed wheel isn't copied; at 0 and without keyframes, a wheel goes, then the grade.
        KeyframeEditor.ClearKeyframes(project, "clip_1", "grade.lift.red");
        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("lift.red", 0), ("lift.blue", 0))));
        Assert.Null(Clip(project, "clip_1").Grade!.Lift);
        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("gain.green", 0))));
        Assert.Null(Clip(project, "clip_1").Grade);
    }

    // A look is a LUT asset (a .cube file) laid over the clip by an amount: set, changed, copied with
    // the rest of the color, and taken off.
    [Fact]
    public void ALookIsALutAssetLaidOverTheClip()
    {
        var project = Footage();
        project.Assets.Add(new Asset { Id = "warm_sunset", Type = AssetType.Lut, File = "assets/warm_sunset.cube" });
        Assert.Empty(ProjectChecker.Users(project, "warm_sunset"));

        var result = ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(), Look: "warm_sunset"));
        Assert.Equal("color of 'clip_1': look warm_sunset at 1", result.Summary);
        var look = Clip(project, "clip_1").Grade!.Look!;
        Assert.Equal(("warm_sunset", 1.0), (look.Asset, look.Amount.Value));
        Assert.Equal(["clip_1"], ProjectChecker.Users(project, "warm_sunset"));
        Assert.Contains("\"look\": {\n", ProjectJson.Serialize(project).Replace("\r", ""));

        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("look.amount", 0.6), ("exposure", 0.3))));
        Assert.Equal("exposure 0.3, look warm_sunset at 0.6", ColorEditor.Describe(Clip(project, "clip_1")));
        Assert.Contains("effects: grade(exposure 0.3, look warm_sunset at 0.6)", LayerTree.Describe(Clip(project, "clip_1")));
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("clip_1", "grade.look.amount", 0, JsonSerializer.SerializeToElement(0.0), null, null)]);
        Assert.Contains(AnimatableProperties.Numeric(Clip(project, "clip_1")), p => p.Name == "grade.look.amount");
        KeyframeEditor.ClearKeyframes(project, "clip_1", "grade.look.amount");

        // Copied with the rest, and given to every clip of the footage.
        ColorEditor.SetColor(project, "other_1", new ColorChange(Values(), From: "clip_1"));
        Assert.Equal(("warm_sunset", 0.6), (Clip(project, "other_1").Grade!.Look!.Asset, Clip(project, "other_1").Grade!.Look!.Amount.Value));
        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(), SameFootage: true));
        Assert.Equal(0.6, Clip(project, "clip_2").Grade!.Look!.Amount.Value);
        Assert.NotSame(look, Clip(project, "clip_2").Grade!.Look);

        // Another look keeps the amount; "" takes it off.
        project.Assets.Add(new Asset { Id = "bleach", Type = AssetType.Lut, File = "assets/bleach.cube" });
        ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(), Look: "bleach"));
        Assert.Equal(("bleach", 0.6), (Clip(project, "clip_1").Grade!.Look!.Asset, Clip(project, "clip_1").Grade!.Look!.Amount.Value));
        ColorEditor.SetColor(project, "clip_2", new ColorChange(Values(("exposure", 0)), Look: ""));
        Assert.Null(Clip(project, "clip_2").Grade);

        // Refused: a look that isn't a LUT, an amount without a look, a look written by hand that
        // isn't there.
        Assert.Equal("look 'clip' is a video, not a LUT; LUT assets: warm_sunset, bleach. import_asset takes .cube files",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(), Look: "clip"))).Message);
        Assert.Equal("look 'sepia' not found; LUT assets: warm_sunset, bleach. import_asset takes .cube files",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(), Look: "sepia"))).Message);
        Assert.Equal("look.amount is how much of the look shows, and 'clip_2' has no look: give look, a LUT asset (import_asset takes .cube files)",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_2", new ColorChange(Values(("look.amount", 0.5))))).Message);
        Assert.Equal("look.amount must be between 0 and 1 (got 1.5)",
            Assert.Throws<ZeyoCutException>(() => ColorEditor.SetColor(project, "clip_1", new ColorChange(Values(("look.amount", 1.5))))).Message);
        Clip(project, "other_1").Grade!.Look!.Asset = "gone";
        Assert.Contains(ProjectValidator.Validate(project), e => e.Contains("grade.look.asset 'gone' not found; LUT assets: warm_sunset, bleach", StringComparison.Ordinal));
        Clip(project, "other_1").Grade!.Look!.Asset = "clip";
        Assert.Contains(ProjectValidator.Validate(project), e => e.Contains("grade.look.asset 'clip' is a video, not a LUT", StringComparison.Ordinal));
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
