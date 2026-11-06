// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeyoCut.Mcp.Tools;

public sealed class KeyframeArg
{
    [JsonPropertyName("layerId")]
    [Description("Layer id, or \"camera\" for the project camera.")]
    public string LayerId { get; set; } = "";

    [JsonPropertyName("property")]
    [Description("x, y, rotation, scaleX, scaleY, opacity; brightness, contrast, saturation (image and video layers); cropX, cropY (video layers); drawing (image layers); shadow.x, shadow.y, shadow.blur, shadow.opacity, outline.width, blur.x, blur.y, tint.amount (after set_effect); mask.x, mask.y, mask.width, mask.height, mask.feather (after set_mask). Camera: x, y (the scene point at the frame's center), zoom, rotation, shakeX, shakeY, shakeRotation.")]
    public string Property { get; set; } = "";

    [JsonPropertyName("time")]
    [Description("Time in seconds.")]
    public double Time { get; set; }

    [JsonPropertyName("value")]
    [Description("A number, or a drawing name (string) for the drawing property.")]
    public JsonElement Value { get; set; }

    [JsonPropertyName("easing")]
    [Description("Easing of the segment that STARTS at this keyframe: linear (default), hold, easeIn, easeOut, easeInOut, easeOutBack, easeOutBounce, easeOutElastic. Ignored for drawing (always hold).")]
    public string? Easing { get; set; }
}

public sealed class PathPointArg
{
    [JsonPropertyName("time")]
    [Description("When the layer reaches this point, seconds.")]
    public double Time { get; set; }

    [JsonPropertyName("x")]
    [Description("Point x, pixels (the parent's space, the canvas for top-level layers).")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    [Description("Point y, pixels.")]
    public double Y { get; set; }

    [JsonPropertyName("inX")]
    [Description("Incoming handle x, relative to the point. Optional.")]
    public double? InX { get; set; }

    [JsonPropertyName("inY")]
    [Description("Incoming handle y, relative to the point. Optional.")]
    public double? InY { get; set; }

    [JsonPropertyName("outX")]
    [Description("Outgoing handle x, relative to the point. Optional.")]
    public double? OutX { get; set; }

    [JsonPropertyName("outY")]
    [Description("Outgoing handle y, relative to the point. Optional.")]
    public double? OutY { get; set; }

    [JsonPropertyName("easing")]
    [Description("Easing of the segment that starts at this point: linear (default), hold, easeIn, easeOut, easeInOut, easeOutBack, easeOutBounce, easeOutElastic.")]
    public string? Easing { get; set; }
}

public sealed class BoxArg
{
    [JsonPropertyName("x")]
    [Description("Left edge, canvas pixels.")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    [Description("Top edge, canvas pixels.")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    [Description("Width, canvas pixels.")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    [Description("Height, canvas pixels.")]
    public double Height { get; set; }
}

public sealed class OffsetArg
{
    [JsonPropertyName("x")]
    [Description("Right of the box's center, canvas pixels (negative: left).")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    [Description("Below the box's center, canvas pixels (negative: above).")]
    public double Y { get; set; }
}

public sealed class WordRangeArg
{
    [JsonPropertyName("from")]
    [Description("Index of the first word to remove (the [n] numbers in get_transcript).")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    [Description("Index of the last word to remove (inclusive). Same as 'from' for a single word.")]
    public int To { get; set; }
}

public sealed class KeyframeRefArg
{
    [JsonPropertyName("layerId")]
    [Description("Layer id, or \"camera\".")]
    public string LayerId { get; set; } = "";

    [JsonPropertyName("property")]
    [Description("x, y, rotation, scaleX, scaleY, opacity, brightness, contrast, saturation, cropX, cropY, drawing, or an effect or mask property such as shadow.x or mask.width; for the camera x, y, zoom, rotation, shakeX, shakeY or shakeRotation.")]
    public string Property { get; set; } = "";

    [JsonPropertyName("time")]
    [Description("Time of the keyframe to remove, in seconds.")]
    public double Time { get; set; }
}

public sealed class MarkerArg
{
    [JsonPropertyName("time")]
    [Description("Time in seconds, from 0 to the project end.")]
    public double Time { get; set; }

    [JsonPropertyName("name")]
    [Description("The marker's name, one line; for a chapter, its title.")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    [Description("chapter (the default for a new marker) or scene.")]
    public string? Kind { get; set; }

    [JsonPropertyName("id")]
    [Description("An existing marker's id, to change that marker; or the id for a new one (default: made from the name).")]
    public string? Id { get; set; }
}

public sealed class CueArg
{
    [JsonPropertyName("start")]
    [Description("When the caption appears, seconds on the timeline.")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    [Description("When it goes, seconds (after start).")]
    public double End { get; set; }

    [JsonPropertyName("text")]
    [Description("The caption's text; \\n breaks the line.")]
    public string Text { get; set; } = "";
}

public sealed class TextBoxArg
{
    [JsonPropertyName("color")]
    [Description("Box color, #RRGGBB or #RRGGBBAA (default #000000B3).")]
    public string? Color { get; set; }

    [JsonPropertyName("padding")]
    [Description("Padding around the text in pixels (default 16).")]
    public double? Padding { get; set; }

    [JsonPropertyName("cornerRadius")]
    [Description("Corner radius in pixels (default 0).")]
    public double? CornerRadius { get; set; }
}
