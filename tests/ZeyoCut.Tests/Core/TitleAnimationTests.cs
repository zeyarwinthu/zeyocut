// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class TitleAnimationTests
{
    private static (Project Project, TextLayer Title) Title(double start, double? end)
    {
        var project = TestProjects.Empty(duration: 6);
        var title = ProjectEditor.AddTextLayer(project, new TextLayerOptions { Text = "Hello", Id = "title", Start = start, End = end, Y = 200 });
        return (project, title);
    }

    private static (double Time, double Value, Easing Easing)[] Keys(AnimatedValue value) =>
        value.Keyframes.Select(k => (k.Time, k.Value, k.Easing)).ToArray();

    [Fact]
    public void EachPresetIsKeyframesOverHalfASecond()
    {
        var (project, title) = Title(1, 4);

        Assert.Equal("'title' comes in with Fade over 1–1.5s", TitleAnimations.Set(project, "title", TitleSide.In, TitleAnimation.Fade));
        Assert.Equal([(1, 0, Easing.EaseOut), (1.5, 1, Easing.Linear)], Keys(title.Opacity));
        Assert.Equal("'title' goes out with Pop over 3.5–4s", TitleAnimations.Set(project, "title", TitleSide.Out, TitleAnimation.Pop));
        Assert.Equal([(3.5, 1, Easing.EaseIn), (4, 0, Easing.Linear)], Keys(title.ScaleX));
        Assert.Equal(Keys(title.ScaleX), Keys(title.ScaleY));

        TitleAnimations.Set(project, "title", TitleSide.In, TitleAnimation.SlideUp);
        Assert.Equal([(1, 218, Easing.EaseOut), (1.5, 200, Easing.Linear)], Keys(title.Y));
        Assert.Equal([(1, 0, Easing.EaseOut), (1.5, 1, Easing.Linear)], Keys(title.Opacity));
        TitleAnimations.Set(project, "title", TitleSide.In, TitleAnimation.Pop);
        Assert.Empty(title.Y.Keyframes);
        Assert.Empty(title.Opacity.Keyframes);
        Assert.Equal([(1, 0, Easing.EaseOutBack), (1.5, 1, Easing.Linear), (3.5, 1, Easing.EaseIn), (4, 0, Easing.Linear)], Keys(title.ScaleX));
        Assert.Equal((200.0, 1.0), (title.Y.Value, title.Opacity.Value));

        Assert.Equal((TitleAnimation.Pop, TitleAnimation.Pop), (TitleAnimations.Detect(project, title, TitleSide.In), TitleAnimations.Detect(project, title, TitleSide.Out)));
        var before = ProjectJson.Serialize(project);
        TitleAnimations.Set(project, "title", TitleSide.Out, TitleAnimation.Pop);
        Assert.Equal(before, ProjectJson.Serialize(project));

        Assert.Equal("'title' comes in with no animation over 1–1.5s", TitleAnimations.Set(project, "title", TitleSide.In, null));
        Assert.Equal([(3.5, 1, Easing.EaseIn), (4, 0, Easing.Linear)], Keys(title.ScaleX));
        Assert.Equal(1, title.ScaleX.Value);
        Assert.Null(TitleAnimations.Detect(project, title, TitleSide.In));
        Assert.False(TitleAnimations.Keyed(project, title, TitleSide.In));
        Assert.True(TitleAnimations.Keyed(project, title, TitleSide.Out));
    }

    [Fact]
    public void PresetsFitShortLayersAndKeepTheLayersOwnKeyframes()
    {
        var (project, title) = Title(0, 0.6);
        TitleAnimations.Set(project, "title", TitleSide.In, TitleAnimation.Fade);
        TitleAnimations.Set(project, "title", TitleSide.Out, TitleAnimation.Fade);
        Assert.Equal([(0, 0, Easing.EaseOut), (0.2, 1, Easing.Linear), (0.4, 1, Easing.EaseIn), (0.6, 0, Easing.Linear)], Keys(title.Opacity));
        Assert.Equal((TitleAnimation.Fade, TitleAnimation.Fade), (TitleAnimations.Detect(project, title, TitleSide.In), TitleAnimations.Detect(project, title, TitleSide.Out)));

        var (keyed, faded) = Title(1, 4);
        faded.Opacity.Keyframes = [new Keyframe { Time = 2, Value = 0.8 }, new Keyframe { Time = 3, Value = 0.4 }];
        TitleAnimations.Set(keyed, "title", TitleSide.In, TitleAnimation.Fade);
        TitleAnimations.Set(keyed, "title", TitleSide.Out, TitleAnimation.Fade);
        Assert.Equal([(1, 0, Easing.EaseOut), (1.5, 0.8, Easing.Linear), (2, 0.8, Easing.Linear), (3, 0.4, Easing.Linear), (3.5, 0.4, Easing.EaseIn), (4, 0, Easing.Linear)], Keys(faded.Opacity));

        var (open, endless) = Title(2, null);
        TitleAnimations.Set(open, "title", TitleSide.Out, TitleAnimation.SlideUp);
        Assert.Equal([(5.5, 200, Easing.EaseIn), (6, 182, Easing.Linear)], Keys(endless.Y));
        Assert.Null(TitleAnimations.Detect(open, endless, TitleSide.In));

        open.Layers.Add(new AudioLayer { Id = "music", Asset = "song" });
        Assert.Contains("'music' is not a text, picture or video layer", Assert.Throws<ZeyoCutException>(() => TitleAnimations.Set(open, "music", TitleSide.In, TitleAnimation.Fade)).Message);
    }
}
