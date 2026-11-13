// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;

namespace ZeyoCut.Editor;

// A layer dragged in the preview: the edit that moves it by the mouse's travel on the canvas. The
// travel is turned into its parent's pixels, where its x and y are, so a child of a turned or scaled
// layer follows the mouse. The edit is PlacementEditor.Set's at the playhead (a keyframe there when x
// or y is animated), or for a caption, the whole track moved by the same amount.
public sealed class PreviewDrag
{
    private readonly Project _project;
    private readonly Affine2D _toParent;
    private readonly double _time;
    private readonly (double X, double Y) _from;
    private readonly IReadOnlyList<string> _track;
    private readonly string? _trackName;

    private PreviewDrag(Project project, string layerId, Affine2D toParent, double time, (double X, double Y) from, IReadOnlyList<string> track, string? trackName)
    {
        _project = project;
        LayerId = layerId;
        _toParent = toParent;
        _time = time;
        _from = from;
        _track = track;
        _trackName = trackName;
    }

    public string LayerId { get; }

    // A drag of the layer an outline belongs to, in the project the outline was drawn from, at time;
    // or a ZeyoCutException that says why it can't be dragged.
    public static PreviewDrag Start(Project project, LayerOutline outline, double time)
    {
        var layer = ProjectEditor.GetVisualLayer(project, outline.LayerId);
        if (outline.Parent?.Inverse() is not { } toParent)
        {
            throw new ZeyoCutException($"'{layer.Id}' is scaled to 0, so it can't be dragged; give it a size in the inspector first");
        }

        var from = (Interpolator.Evaluate(layer.X, time), Interpolator.Evaluate(layer.Y, time));
        if (layer.ParentId is { } parentId && project.FindLayer(parentId) is { } parent && TimelineLayout.IsCaptionGroup(parent))
        {
            var cues = project.Layers.Where(l => l.ParentId == parent.Id && l is TextLayer).Select(l => l.Id).ToList();
            return new PreviewDrag(project, layer.Id, toParent, time, from, cues, $"update_layer {parent.Id}");
        }

        if (PlacementEditor.PositionLocked(layer) is { } locked)
        {
            throw new ZeyoCutException(locked);
        }

        return new PreviewDrag(project, layer.Id, toParent, time, from, [], null);
    }

    // Whether the drag moves a whole caption track.
    public bool MovesTrack => _trackName is not null;

    // The layer's x and y with the mouse dx, dy canvas pixels from where it went down; with oneAxis,
    // only along the axis it moved further on.
    public (double X, double Y) Position(double dx, double dy, bool oneAxis)
    {
        if (oneAxis)
        {
            (dx, dy) = Math.Abs(dx) >= Math.Abs(dy) ? (dx, 0.0) : (0.0, dy);
        }

        // Only the turn and scale apply to a distance, not the offset.
        var (px, py) = (_toParent.A * dx + _toParent.C * dy, _toParent.B * dx + _toParent.D * dy);
        return (Round(_from.X + px), Round(_from.Y + py));
    }

    // The edit that puts the layer there; null when it hasn't moved.
    public EditOperation? Move(double dx, double dy, bool oneAxis)
    {
        var (x, y) = Position(dx, dy, oneAxis);
        var values = new Dictionary<string, double>();
        if (x != Round(_from.X))
        {
            values["x"] = x;
        }

        if (y != Round(_from.Y))
        {
            values["y"] = y;
        }

        if (values.Count == 0)
        {
            return null;
        }

        return _trackName is not null
            ? new ShiftLayersOperation(_trackName, _track, x - _from.X, y - _from.Y)
            : PlacementEditor.Set(_project, LayerId, values, _time);
    }

    // What the status bar says while dragging.
    public string Describe(double dx, double dy, bool oneAxis)
    {
        var (x, y) = Position(dx, dy, oneAxis);
        var what = MovesTrack ? $"the {_track.Count} caption(s) of the track" : $"'{LayerId}'";
        return $"Moving {what} to x {Num(x)}, y {Num(y)} px. Shift keeps it on one axis; Escape puts it back";
    }

    // Positions in hundredths of a pixel, so a drag doesn't save long fractions.
    private static double Round(double value) => Math.Round(value, 2);

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
