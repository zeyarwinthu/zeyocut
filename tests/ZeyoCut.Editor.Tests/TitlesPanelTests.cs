// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The Titles view: a title's two lines, colors and presets, and a style's sample.
public class TitlesPanelTests
{
    [Fact]
    public void ANewTitleIsTheFieldsAtThePlayhead()
    {
        var project = TestFiles.Project(duration: 6);
        Assert.Equal(("Big", "small\nsmaller"), TitlesPanel.Lines("Big\nsmall\nsmaller"));
        Assert.Equal(("Big", ""), TitlesPanel.Lines("Big"));
        Assert.Equal(("Big\nsmall", "Big"), (TitlesPanel.Text("Big", "small"), TitlesPanel.Text("Big", "")));

        var draft = TitlesPanel.Fresh with { First = "Tora Jumps!", Second = "トラのジャンプ", Outline = " " };
        var options = TitlesPanel.Title(project, 1, draft);
        Assert.Equal(("Tora Jumps!\nトラのジャンプ", 1.0, 5.0, "#FFFFFF", null, "center"), (options.Text, options.Start!.Value, options.End!.Value, options.Color, options.StrokeColor, options.Align));
        Assert.Equal(6.0, TitlesPanel.Title(project, 3, draft).End);

        // Pop in and fade out, as add_text_layer's animateIn and animateOut.
        var add = TitlesPanel.Add(project, 1, draft, null, "", new Library(Path.GetTempPath()), null);
        Assert.Equal((TitleAnimation.Pop, TitleAnimation.Fade), (add.In!.Value, add.Out!.Value));
        add.Apply(project);
        var text = Assert.IsType<TextLayer>(Assert.Single(project.Layers));
        Assert.Equal(text, TitlesPanel.Title(project, text.Id));
        Assert.Equal(draft with { Outline = "" }, TitlesPanel.Of(project, text));

        var styled = TitlesPanel.Styled(TitlesPanel.Fresh, new TextStyle { Name = "Gold", Color = "#FFD700", StrokeColor = "#5A3A22" });
        Assert.Equal(("#FFD700", "#5A3A22"), (styled.Fill, styled.Outline));
    }

    [Fact]
    public void TheFieldsChangeTheSelectedText()
    {
        var project = TestFiles.Project(duration: 6);
        var text = ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "One\nTwo", Start = 1, End = 4, Color = "#FFFFFF" });
        var draft = TitlesPanel.Of(project, text);
        Assert.Equal(new TitleDraft("One", "Two", "#FFFFFF", "", TitlesPanel.None, TitlesPanel.None), draft);
        Assert.Null(TitlesPanel.Change(text, draft with { Fill = "#ffffff" }));
        var change = TitlesPanel.Change(text, draft with { First = "Uno", Outline = "#000000" })!;
        Assert.Equal("""{"text":"Uno\nTwo","strokeColor":"#000000"}""", change.Changes.ToJsonString());
        Assert.Equal("""{"strokeColor":null}""", TitlesPanel.Change(new TextLayer { Text = "One\nTwo", Color = "#FFFFFF", StrokeColor = "#000000" }, draft)!.Changes.ToJsonString());

        Assert.Null(TitlesPanel.Animate(project, text, TitleSide.In, TitlesPanel.None));
        TitlesPanel.Animate(project, text, TitleSide.In, "Slide up")!.Apply(project);
        Assert.Equal("Slide up", TitlesPanel.Shown(project, text, TitleSide.In));
        Assert.Null(TitlesPanel.Animate(project, text, TitleSide.In, "Slide up"));
        Assert.Equal(new TitleAnimationOperation("title", TitleSide.In, null), TitlesPanel.Animate(project, text, TitleSide.In, TitlesPanel.None));

        // Keyframes of its own at its end are shown as such, and None takes them off.
        text.Opacity.Keyframes = [new Keyframe { Time = 3.8, Value = 1 }, new Keyframe { Time = 4, Value = 0.5 }];
        Assert.Equal(TitlesPanel.Keyframes, TitlesPanel.Shown(project, text, TitleSide.Out));
        Assert.Null(TitlesPanel.Animate(project, text, TitleSide.Out, TitlesPanel.Keyframes));

        CaptionTracks.AddEntries(project, "en", [new CueEntry(0, 2, "Hello")], new CaptionOptions(), [], null);
        Assert.Null(TitlesPanel.Title(project, project.Layers.OfType<TextLayer>().Single(t => t.Text == "Hello").Id));
    }

    [Fact]
    public void AStyleIsDrawnAsItsNameOnTheCanvasItWasSavedFor()
    {
        TextStyle Style(bool box) => new() { Name = "Caption box", ReferenceSize = 720, FontFamily = "Segoe UI", Bold = !box, FontSize = 40, Color = "#FFFFFF", Box = box ? new TextBackground { Color = "#000000BF", Padding = 12 } : null };
        var sample = LibraryPictures.StyleSample(Style(box: true));
        Assert.Equal((1280, 720, "#C9CED8"), (sample.Width, sample.Height, sample.BackgroundColor));
        var text = Assert.IsType<TextLayer>(Assert.Single(sample.Layers));
        Assert.Equal(("Caption box", 40.0, "#000000BF", 640.0, 360.0, TextAlign.Center), (text.Text, text.FontSize, text.Box!.Color, text.X.Value, text.Y.Value, text.Align));
        Assert.Equal("#232A3A", LibraryPictures.StyleSample(Style(box: false)).BackgroundColor);
        Assert.Equal("#C9CED8", LibraryPictures.StyleSample(new TextStyle { Name = "Ink", Color = "#1E1E1E" }).BackgroundColor);
        Assert.Equal((true, false), (LibraryPictures.Light("#FFF4E0"), LibraryPictures.Light("#5A3A22")));
        Assert.Equal(("Segoe UI, 40 px", "Segoe UI bold, 40 px"), (TitlesPanel.Detail(Style(box: true)), TitlesPanel.Detail(Style(box: false))));
    }
}
