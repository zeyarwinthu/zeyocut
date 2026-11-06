// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

// Layer effects (set_effect). They work in screen directions (x right, y down) and grow and shrink
// with the layer and the camera, so a shadow keeps its look when the layer is scaled. Blur sizes
// are the Gaussian's standard deviation in pixels. Each effect is on one layer, not its children.
public sealed class DropShadow
{
    public string Color { get; set; } = "#000000";
    public AnimatedValue X { get; set; } = new(8);
    public AnimatedValue Y { get; set; } = new(8);
    public AnimatedValue Blur { get; set; } = new(6);
    public AnimatedValue Opacity { get; set; } = new(0.5);
}

public sealed class Outline
{
    public string Color { get; set; } = "#FFFFFF";
    public AnimatedValue Width { get; set; } = new(4);
}

public sealed class BlurEffect
{
    public AnimatedValue X { get; set; } = new(4);
    public AnimatedValue Y { get; set; } = new(4);
}

// Mixes the layer's colors toward Color; amount 1 paints it solid in that color, keeping its shape.
public sealed class Tint
{
    public string Color { get; set; } = "#FFFFFF";
    public AnimatedValue Amount { get; set; } = new(0.5);
}

public enum MaskType
{
    Rectangle,
    Ellipse,
    Image
}

// Shows the layer only inside the shape (only outside it with invert), in the layer's own pixels:
// x, y is the box's top-left corner. An image mask stretches the image's alpha over the box.
// feather softens the edge over about that many pixels on each side.
public sealed class Mask
{
    public MaskType Type { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asset { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Invert { get; set; }

    public AnimatedValue X { get; set; } = new(0);
    public AnimatedValue Y { get; set; } = new(0);
    public AnimatedValue Width { get; set; } = new(100);
    public AnimatedValue Height { get; set; } = new(100);
    public AnimatedValue Feather { get; set; } = new(0);
}

// Moves the layer's anchor through points (set_motion_path), in the same space as x, y. Each
// segment is a cubic bezier; handles are relative to their point, and a missing one is set for a
// smooth curve. Progress runs from 0 at the first point to Points.Count - 1 at the last, keyed at
// each point's time with its easing; inside a segment the layer moves at an even speed.
public sealed class MotionPath
{
    public List<PathPoint> Points { get; set; } = [];
    public AnimatedValue Progress { get; set; } = new(0);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoOrient { get; set; }
}

public sealed class PathPoint
{
    public double X { get; set; }
    public double Y { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? InX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? InY { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OutX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OutY { get; set; }
}
