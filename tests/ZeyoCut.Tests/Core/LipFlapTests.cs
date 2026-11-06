// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class LipFlapTests
{
    // 10 fps, 4 s. 'mouth' (closed, open, wide) for the whole project; 'voice' starts at 1s and plays
    // source 0.5–2s, so it sounds at 1–2.5s (frames 10–24).
    private static Project MouthProject()
    {
        var project = TestProjects.WithImageAssets("m_closed", "m_open", "m_wide");
        project.Fps = 10;
        project.Duration = 4;
        project.Assets.Add(new Asset { Id = "voice", Type = AssetType.Audio, File = "assets/voice.wav", Duration = 2.0 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Id = "mouth",
            Drawings = new Dictionary<string, string> { ["closed"] = "m_closed", ["open"] = "m_open", ["wide"] = "m_wide" },
        });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "voice", Id = "voice", Start = 1.0, TrimIn = 0.5, Role = "voice" });
        return project;
    }

    private static ImageLayer Mouth(Project project) => (ImageLayer)project.FindLayer("mouth")!;

    private static DrawingKeyframe Key(double time, string value) => new() { Time = time, Value = value };

    // 5 loud frames, 5 silent, 5 loud.
    private static double[] LoudQuietLoud() => [.. Enumerable.Range(0, 15).Select(i => i is >= 5 and < 10 ? -120.0 : -10.0)];

    [Fact]
    public void ThePlanCoversTheFramesWhereTheSoundAndTheMouthBothPlay()
    {
        var project = MouthProject();
        var plan = LipFlap.Plan(project, "mouth", "voice", "closed", ["open"], 0.5);
        Assert.Equal("voice", plan.SourceId);
        Assert.Equal(1.0, plan.Start, 9);
        Assert.Equal(2.5, plan.End, 9);
        Assert.Equal(15, plan.Windows.Count);
        Assert.Equal(10, plan.Windows[0].Frame);
        Assert.Equal(0.5, plan.Windows[0].SourceStart, 9);
        Assert.Equal(0.6, plan.Windows[0].SourceEnd, 9);
        Assert.Equal(24, plan.Windows[^1].Frame);
        Assert.Equal(2.0, plan.Windows[^1].SourceEnd, 9);
        Assert.Empty(plan.Warnings);

        Mouth(project).End = 2.0;
        project.FindLayer("voice")!.Visible = false;
        var shorter = LipFlap.Plan(project, "mouth", "voice", "closed", ["open"], 0.5);
        Assert.Equal(2.0, shorter.End, 9);
        Assert.Equal(10, shorter.Windows.Count);
        Assert.Contains("'voice' is hidden, so the export doesn't play it", shorter.Warnings);
    }

    [Fact]
    public void LouderFramesStepThroughTheOpenDrawings()
    {
        double[] levels = [-120, -120, -120, -120, -30, -30, -15, -15, -2, -2];
        var shapes = LipFlap.Shapes(levels, "closed", ["open", "wide"], 0.5, out var threshold);
        Assert.Equal(-41, threshold, 6);
        Assert.Equal(["closed", "closed", "closed", "closed", "open", "open", "wide", "wide", "wide", "wide"], shapes);

        Assert.Equal(["closed", "closed", "closed", "closed", "open", "open", "wide", "wide", "wide", "wide"], LipFlap.Shapes(levels, "closed", ["open", "wide"], 0.9, out var lower));
        Assert.Equal(-72.2, lower, 6);
        Assert.Equal(["closed", "closed", "closed", "closed", "closed", "closed", "closed", "closed", "wide", "wide"], LipFlap.Shapes(levels, "closed", ["open", "wide"], 0.1, out _));

        double[] speech = [-120, -120, -40, -35, -30, -25, -20, -15];
        Assert.Equal(["x", "x", "a", "a", "b", "b", "c", "c"], LipFlap.Shapes(speech, "x", ["a", "b", "c"], 0.5, out _));

        var silent = LipFlap.Shapes([-120, -90, -70], "closed", ["open"], 1, out var none);
        Assert.True(double.IsNaN(none));
        Assert.All(silent, s => Assert.Equal("closed", s));
    }

    [Fact]
    public void EveryDrawingHoldsAtLeastTwoFrames()
    {
        Assert.Equal(["c", "c", "c", "c", "o", "o", "o", "c"], LipFlap.Hold(["c", "o", "c", "c", "o", "o", "o", "c"]));
        Assert.Equal(["o", "o", "o", "c", "c", "c"], LipFlap.Hold(["o", "c", "o", "c", "o", "c"]));
        Assert.Equal(["o"], LipFlap.Hold(["o"]));
    }

    [Fact]
    public void ApplyWritesHoldKeysAndKeepsTheDrawingBeforeAndAfterTheSound()
    {
        var project = MouthProject();
        var mouth = Mouth(project);
        mouth.Drawing.Keyframes = [Key(0, "closed"), Key(1.5, "wide"), Key(3.0, "closed")];
        var plan = LipFlap.Plan(project, "mouth", "voice", "closed", ["open"], 0.5);

        var result = LipFlap.Apply(project, "mouth", "closed", ["open"], 0.5, plan, LoudQuietLoud());
        Assert.Equal(
            [(0.0, "closed"), (1.0, "open"), (1.5, "closed"), (2.0, "open"), (2.5, "wide"), (3.0, "closed")],
            mouth.Drawing.Keyframes.Select(k => (Math.Round(k.Time, 9), k.Value)));
        Assert.Equal(4, result.Keyframes);
        Assert.Equal(
            "lip flap on 'mouth' from 'voice' at 1–2.5s (15 frames): open 10 frames, closed 5; 4 keyframe(s) on mouth.drawing (the mouth opens above -45.0 dBFS at sensitivity 0.5; every drawing holds at least 2 frames)",
            result.Describe());
        Assert.Empty(result.Warnings);

        var again = LipFlap.Apply(project, "mouth", "closed", ["open"], 0.5, plan, LoudQuietLoud());
        Assert.Equal(3, again.Keyframes);
        Assert.Equal(
            [(0.0, "closed"), (1.0, "open"), (1.5, "closed"), (2.0, "open"), (2.5, "wide"), (3.0, "closed")],
            mouth.Drawing.Keyframes.Select(k => (Math.Round(k.Time, 9), k.Value)));
    }

    [Fact]
    public void AMouthWithoutKeysShowsItsOwnDrawingAroundTheSound()
    {
        var project = MouthProject();
        var mouth = Mouth(project);
        mouth.Drawing.Value = "wide";
        var plan = LipFlap.Plan(project, "mouth", "voice", "closed", ["open"], 0.5);

        var result = LipFlap.Apply(project, "mouth", "closed", ["open"], 0.5, plan, [.. Enumerable.Repeat(-12.0, 15)]);
        Assert.Equal(3, result.Keyframes);
        Assert.Equal("wide", Interpolator.Evaluate(mouth.Drawing, 0.5));
        Assert.Equal("open", Interpolator.Evaluate(mouth.Drawing, 1.0));
        Assert.Equal("open", Interpolator.Evaluate(mouth.Drawing, 2.4));
        Assert.Equal("wide", Interpolator.Evaluate(mouth.Drawing, 2.5));

        var silent = LipFlap.Apply(project, "mouth", "closed", ["open"], 0.5, plan, [.. Enumerable.Repeat(-120.0, 15)]);
        Assert.Contains("the sound of 'voice' is silent at 1–2.5s (below -60 dBFS), so the mouth stays closed", silent.Warnings);
        Assert.Contains("the sound is silent", silent.Describe());
        Assert.Equal("closed", Interpolator.Evaluate(mouth.Drawing, 1.7));
    }

    [Fact]
    public void BadInputIsRejected()
    {
        var project = MouthProject();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi" });
        string Fails(string mouth, string source, string closed, string[] open, double sensitivity = 0.5) =>
            Assert.Throws<ZeyoCutException>(() => LipFlap.Plan(project, mouth, source, closed, open, sensitivity)).Message;

        Assert.Contains("sensitivity must be between 0", Fails("mouth", "voice", "closed", ["open"], 1.5));
        Assert.Contains("'title' is a text layer; auto_lip_flap needs an image layer", Fails("title", "voice", "closed", ["open"]));
        Assert.Contains("has no drawing 'shut' for the closed mouth; its drawings: closed, open, wide", Fails("mouth", "voice", "shut", ["open"]));
        Assert.Contains("give 1 to 8 open drawings", Fails("mouth", "voice", "closed", []));
        Assert.Contains("'mouth' has no drawing 'ah'", Fails("mouth", "voice", "closed", ["ah"]));
        Assert.Contains("'closed' is the closed drawing", Fails("mouth", "voice", "closed", ["open", "closed"]));
        Assert.Contains("the open drawings repeat a drawing: open, open", Fails("mouth", "voice", "closed", ["open", "open"]));
        Assert.Contains("'title' is a text layer; auto_lip_flap needs an audio layer or a video layer with sound", Fails("mouth", "title", "closed", ["open"]));

        Mouth(project).End = 0.5;
        Assert.Contains("don't play together for a whole frame", Fails("mouth", "voice", "closed", ["open"]));
    }
}
