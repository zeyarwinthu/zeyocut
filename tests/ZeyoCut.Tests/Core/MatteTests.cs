// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class MatteTests
{
    private static readonly MatteValues None = new(null, null, null, null);

    // An image layer 'art' (until 2s), a video layer 'clip', a text layer 'title' and a null layer 'group'.
    private static Project Layers()
    {
        var project = TestProjects.WithImageAssets("art");
        project.Assets.Add(new Asset { Id = "clip", Type = AssetType.Video, File = "assets/clip.mp4", Width = 640, Height = 360, FrameRate = "30", FrameCount = 90, Duration = 3 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "art", Drawings = new Dictionary<string, string> { ["normal"] = "art" }, End = 2 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "clip", Asset = "clip" });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello" });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "group" });
        return project;
    }

    private static Matte Build(Project project, string layerId, MatteMethod method, MatteValues? values = null) =>
        MatteEditor.Build(MatteEditor.GetMattable(project, layerId), method, values ?? None);

    [Fact]
    public void EachMethodWorksOnItsKindOfLayer()
    {
        var project = Layers();
        Assert.Equal("edgeFill is for image layers; use chroma or aiVideo on video layer 'clip'",
            Assert.Throws<ZeyoCutException>(() => Build(project, "clip", MatteMethod.EdgeFill)).Message);
        Assert.Equal("aiImage is for image layers; use chroma or aiVideo on video layer 'clip'",
            Assert.Throws<ZeyoCutException>(() => Build(project, "clip", MatteMethod.AiImage)).Message);
        Assert.Equal("aiVideo is for video layers; use aiImage, edgeFill or chroma on image layer 'art'",
            Assert.Throws<ZeyoCutException>(() => Build(project, "art", MatteMethod.AiVideo)).Message);
        Assert.Equal("remove_background works on image and video layers; 'title' is a text layer",
            Assert.Throws<ZeyoCutException>(() => MatteEditor.GetMattable(project, "title")).Message);
        Assert.Equal("'group' is a null layer (no drawings), so it has no background to remove",
            Assert.Throws<ZeyoCutException>(() => MatteEditor.GetMattable(project, "group")).Message);
        Assert.Equal(MatteMethod.Chroma, Build(project, "clip", MatteMethod.Chroma).Method);
        Assert.Equal(MatteMethod.Chroma, Build(project, "art", MatteMethod.Chroma).Method);
    }

    [Fact]
    public void SettingsAreCheckedAndDefaultsFilledIn()
    {
        var project = Layers();
        Assert.Equal("edgeFill takes color, tolerance; softness is not for edgeFill",
            Assert.Throws<ZeyoCutException>(() => Build(project, "art", MatteMethod.EdgeFill, new MatteValues(null, null, 0.2, null))).Message);
        Assert.Equal("aiImage takes no settings; color, tolerance are not for aiImage",
            Assert.Throws<ZeyoCutException>(() => Build(project, "art", MatteMethod.AiImage, new MatteValues("#FFFFFF", 0.1, null, null))).Message);
        Assert.Equal("tolerance must be between 0 and 1 (got 1.5)",
            Assert.Throws<ZeyoCutException>(() => Build(project, "art", MatteMethod.Chroma, new MatteValues(null, 1.5, null, null))).Message);
        Assert.Equal("'green' is not a color; use #RRGGBB",
            Assert.Throws<ZeyoCutException>(() => Build(project, "art", MatteMethod.Chroma, new MatteValues("green", null, null, null))).Message);
        Assert.StartsWith("key color #808080 is too close to gray to key on",
            Assert.Throws<ZeyoCutException>(() => Build(project, "art", MatteMethod.Chroma, new MatteValues("#808080", null, null, null))).Message);

        var chroma = Build(project, "clip", MatteMethod.Chroma, new MatteValues("#00b140", null, null, null));
        Assert.Equal(("#00B140", 0.4, 0.2, 1.0), (chroma.Color, chroma.Tolerance, chroma.Softness, chroma.Spill));
        var edge = Build(project, "art", MatteMethod.EdgeFill);
        Assert.Equal((null, 0.1), (edge.Color, edge.Tolerance));
        Assert.Null(edge.Softness);
    }

    [Fact]
    public void AnotherMethodReplacesTheMatteAndKeepsTheRefinedEdge()
    {
        var project = Layers();
        Assert.Equal("removed the background of 'art' with edgeFill (background from the border, tolerance 0.1)",
            MatteEditor.Set(project, "art", Build(project, "art", MatteMethod.EdgeFill)));
        var refined = MatteEditor.Refine(project, "art", 2, 3);
        Assert.Equal("refined the matte of 'art': shrink 2 px, feather 3 px", refined.Summary);
        Assert.Empty(refined.Warnings);

        var summary = MatteEditor.Set(project, "art", Build(project, "art", MatteMethod.Chroma, new MatteValues("#00FF00", null, null, null)));
        Assert.Equal("removed the background of 'art' with chroma (key #00FF00, tolerance 0.4, softness 0.2, spill 1); shrink 2 px, feather 3 px (was edgeFill)", summary);
        Assert.Contains("matte(chroma, shrink 2, feather 3)", LayerTree.Describe(project.FindLayer("art")!));

        Assert.Equal("took the background removal (chroma) off 'art'; it shows its whole picture again", MatteEditor.Remove(project, "art"));
        Assert.Null(((ImageLayer)project.FindLayer("art")!).Matte);
        Assert.Equal("'art' has no background removal to take off", Assert.Throws<ZeyoCutException>(() => MatteEditor.Remove(project, "art")).Message);
    }

    [Fact]
    public void RefineNeedsAMatteAndChecksItsRanges()
    {
        var project = Layers();
        Assert.Equal("'art' has no matte to refine; remove its background with remove_background first",
            Assert.Throws<ZeyoCutException>(() => MatteEditor.Refine(project, "art", 1, null)).Message);
        MatteEditor.Set(project, "art", Build(project, "art", MatteMethod.EdgeFill));
        Assert.Equal("give shrink, feather or both", Assert.Throws<ZeyoCutException>(() => MatteEditor.Refine(project, "art", null, null)).Message);
        Assert.Equal("shrink must be between -100 and 100 pixels (negative grows the edge); got 150",
            Assert.Throws<ZeyoCutException>(() => MatteEditor.Refine(project, "art", 150, null)).Message);
        Assert.Equal("feather must be between 0 and 100 pixels; got -1",
            Assert.Throws<ZeyoCutException>(() => MatteEditor.Refine(project, "art", null, -1)).Message);

        var change = MatteEditor.Refine(project, "art", -2.5, null);
        Assert.Equal("refined the matte of 'art': shrink -2.5 px (grows), feather 0 px", change.Summary);
        Assert.Equal("shrink works in whole pixels, so -2.5 acts as -3", Assert.Single(change.Warnings));
    }

    [Fact]
    public void MattesSurviveJsonAndFollowSplits()
    {
        var project = Layers();
        MatteEditor.Set(project, "art", Build(project, "art", MatteMethod.EdgeFill, new MatteValues("#ffffff", 0.05, null, null)));
        MatteEditor.Refine(project, "art", 1, 0);
        var json = ProjectJson.Serialize(project);
        Assert.Contains("\"matte\": {\n        \"method\": \"edgeFill\",\n        \"color\": \"#FFFFFF\",\n        \"tolerance\": 0.05,\n        \"shrink\": 1\n      }", json.Replace("\r\n", "\n"));

        var copy = ProjectJson.Deserialize(json);
        Assert.Empty(ProjectValidator.Validate(copy));
        TimelineEditor.SplitLayer(copy, "art", 1);
        Assert.All(copy.Layers.OfType<ImageLayer>().Where(l => l.Drawings.Count > 0), l => Assert.Equal(MatteMethod.EdgeFill, l.Matte?.Method));
        Assert.Equal(2, copy.Layers.OfType<ImageLayer>().Count(l => l.Matte is not null));
    }

    [Fact]
    public void TheValidatorCatchesHandEditedMattes()
    {
        var project = Layers();
        ((ImageLayer)project.FindLayer("art")!).Matte = new Matte { Method = MatteMethod.AiVideo, Tolerance = 3 };
        ((VideoLayer)project.FindLayer("clip")!).Matte = new Matte { Method = MatteMethod.Chroma, Color = "#777777", Tolerance = 0.4, Softness = 0.2, Spill = 1, Feather = 500 };
        var errors = ProjectValidator.Validate(project);
        Assert.Contains("layer 'art': matte method aiVideo is for video layers", errors);
        Assert.Contains("layer 'art': matte.tolerance is not used by aiVideo", errors);
        Assert.Contains("layer 'art': matte.tolerance must be between 0 and 1 (got 3)", errors);
        Assert.Contains("layer 'clip': key color #777777 is too close to gray to key on", errors);
        Assert.Contains("layer 'clip': matte.feather must be between 0 and 100 (got 500)", errors);
    }
}
