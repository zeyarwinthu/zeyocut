// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public enum RedactionStyle
{
    Pixelate,
    Blur,
    Solid
}

// apply_redaction: hides the boxes of its tracks in every clip that plays their footage. It works on
// the clip's video frame before the clip is drawn, so the clip's moves and effects, the camera and a
// subject cutout of the clip all show it hidden; and it is in the footage's own frames, so splits,
// trims and speed changes keep it in step. Each frame's box comes from the track through the
// fail-safe (RedactionCover), or from Boxes after tracks_to_keyframes. Only its name, visibility and
// the settings below are its own; start, end, zIndex and parentId stay unset.
public sealed class RedactionLayer : Layer
{
    [JsonIgnore]
    public override string TypeName => "redaction";

    public List<string> Tracks { get; set; } = [];

    public RedactionStyle Style { get; set; } = RedactionStyle.Pixelate;

    // 0-100: fewer blocks (or more blur) across the box.
    public double Strength { get; set; } = 50;

    // Each side of a track's box grows by this share of its width or height.
    public double PaddingPercent { get; set; } = 15;

    // A soft edge outside the box, in source pixels.
    public double Feather { get; set; } = 4;

    // How many frames a box is held, and grows, before a track's first detection and after its last.
    public int HoldFrames { get; set; } = 15;

    // The fill of solid.
    public string Color { get; set; } = "#000000";

    // tracks_to_keyframes: a track's box as keyframes in the footage's own seconds and pixels, used
    // instead of the box worked out from the track, from its first keyframe to its last.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RedactionBox>? Boxes { get; set; }
}

public sealed class RedactionBox
{
    public AnimatedValue X { get; set; } = new();
    public AnimatedValue Y { get; set; } = new();
    public AnimatedValue Width { get; set; } = new();
    public AnimatedValue Height { get; set; } = new();
}
