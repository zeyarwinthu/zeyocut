// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;

namespace ZeyoCut.Editor.Tests;

// Dragging a layer in the preview: the edit the mouse's travel on the canvas makes.
public class PreviewDragTests
{
    private static Project Titled()
    {
        var project = TestFiles.Project(640, 360, duration: 8);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", X = 320, Y = 180, Start = 0, End = 3 });
        return project;
    }

    // An outline as the preview gets it from the renderer; only the parent matrix matters to a drag.
    private static LayerOutline Outline(string id, Affine2D? parent) => new(id, [(0, 0), (10, 0), (10, 10), (0, 10)], (5, 5), parent);

    private static T Layer<T>(Project project, string id) where T : Layer => Assert.IsType<T>(project.FindLayer(id));

    [Fact]
    public void ALayerMovesByTheMousesTravel()
    {
        var project = Titled();
        var drag = PreviewDrag.Start(project, Outline("title", Affine2D.Identity), 1);
        Assert.Equal(("title", false), (drag.LayerId, drag.MovesTrack));
        Assert.Null(drag.Move(0, 0, oneAxis: false));
        Assert.Equal((330.0, 180.0), drag.Position(10, -5, oneAxis: true));
        Assert.Equal((320.0, 172.5), drag.Position(2, -7.5, oneAxis: true));
        Assert.Equal("Moving 'title' to x 330, y 175 px. Shift keeps it on one axis; Escape puts it back", drag.Describe(10, -5, oneAxis: false));

        var move = drag.Move(10.004, -5, oneAxis: false)!;
        Assert.Equal("update_layer title", move.Label);
        move.Apply(project);
        Assert.Equal((330.0, 175.0), (Layer<TextLayer>(project, "title").X.Value, Layer<TextLayer>(project, "title").Y.Value));

        // Along one axis, only that value changes.
        var across = PreviewDrag.Start(project, Outline("title", Affine2D.Identity), 1).Move(4, 1, oneAxis: true)!;
        Assert.Equal(JsonSerializer.Serialize(new { x = 334.0 }), ((UpdateLayerOperation)across).Changes.ToJsonString());
    }

    [Fact]
    public void AChildFollowsTheMouseInItsParentsPixels()
    {
        var project = Titled();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "tag", Text = "!", ParentId = "title", X = 50, Y = 0 });

        // The parent is turned 90° clockwise and drawn twice its size: 20 px down the canvas is 10 px
        // along its own x.
        var parent = Affine2D.Translation(100, 100) * Affine2D.Rotation(90) * Affine2D.Scaling(2, 2);
        PreviewDrag.Start(project, Outline("tag", parent), 1).Move(0, 20, oneAxis: false)!.Apply(project);
        Assert.Equal((60.0, 0.0), (Layer<TextLayer>(project, "tag").X.Value, Layer<TextLayer>(project, "tag").Y.Value));
    }

    [Fact]
    public void AnAnimatedPositionGetsAKeyframeAtThePlayhead()
    {
        var project = Titled();
        KeyframeEditor.SetKeyframes(project, [new KeyframeEntry("title", "y", 0, JsonSerializer.SerializeToElement(100.0)), new KeyframeEntry("title", "y", 2, JsonSerializer.SerializeToElement(200.0))]);
        var drag = PreviewDrag.Start(project, Outline("title", Affine2D.Identity), 1);
        Assert.Equal((330.0, 160.0), drag.Position(10, 10, oneAxis: false));
        var move = drag.Move(10, 10, oneAxis: false)!;
        Assert.Equal("set_keyframes title", move.Label);
        move.Apply(project);
        var title = Layer<TextLayer>(project, "title");
        Assert.Equal((330.0, 0), (title.X.Value, title.X.Keyframes.Count));
        Assert.Equal([(0.0, 100.0), (1, 160), (2, 200)], title.Y.Keyframes.Select(k => (k.Time, k.Value)));
    }

    [Fact]
    public void WhatCantBeDraggedSaysWhy()
    {
        var project = Titled();
        Assert.Equal("'title' is scaled to 0, so it can't be dragged; give it a size in the inspector first",
            Assert.Throws<ZeyoCutException>(() => PreviewDrag.Start(project, Outline("title", null), 1)).Message);
        Layer<TextLayer>(project, "title").MotionPath = new MotionPath { Points = [new PathPoint { X = 0, Y = 0 }, new PathPoint { X = 10, Y = 10 }] };
        Assert.Equal(PlacementEditor.PositionLocked(Layer<TextLayer>(project, "title")),
            Assert.Throws<ZeyoCutException>(() => PreviewDrag.Start(project, Outline("title", Affine2D.Identity), 1)).Message);
    }

    [Fact]
    public void ACaptionTakesItsWholeTrackAlong()
    {
        var project = Titled();
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 2, "Hello"), new CueEntry(2, 4, "World")], project.Duration).Cues, new CaptionOptions(), [], null);
        var cues = project.Layers.Where(l => l.ParentId == "captions_en").Select(l => l.Id).ToList();
        var before = cues.Select(id => Layer<TextLayer>(project, id).Y.Value).ToList();

        var drag = PreviewDrag.Start(project, Outline(cues[0], Affine2D.Identity), 1);
        Assert.True(drag.MovesTrack);
        Assert.StartsWith("Moving the 2 caption(s) of the track to x 320, y ", drag.Describe(0, -30, oneAxis: false));
        var move = drag.Move(0, -30, oneAxis: false)!;
        Assert.Equal(("update_layer captions_en", 2), (move.Label, move.Layers.Count));
        move.Apply(project);
        Assert.Equal(before.Select(y => y - 30), cues.Select(id => Layer<TextLayer>(project, id).Y.Value));
    }
}
