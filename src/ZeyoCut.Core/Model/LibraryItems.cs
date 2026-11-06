// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

// A character saved with save_rig. Part ids are the part names (the root keeps its own name, the
// others are their roles), parentIds point at part names, times are relative to the root's start,
// and every part holds its pose without keyframes. Assets point at files in the rig's folder.
public sealed class RigFile
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public string Root { get; set; } = "";
    public List<Layer> Parts { get; set; } = [];
    public List<Asset> Assets { get; set; } = [];
}

public enum ActionMode
{
    Absolute,
    Offset,
    Factor
}

public sealed class ActionTrack
{
    public string Role { get; set; } = "";
    public string Property { get; set; } = "";
    public ActionMode Mode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Keyframe>? Keyframes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DrawingKeyframe>? Drawings { get; set; }
}

// Keyframes saved with save_action, by part role, with times from 0 to Duration. The root's x, y
// and rotation are offsets from where the rig stands and its scale is a factor; everything else
// is a plain value.
public sealed class ActionFile
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public double Duration { get; set; }
    public List<ActionTrack> Tracks { get; set; } = [];
}

public enum CaptionPosition
{
    Bottom,
    Center
}

// A text look saved with save_style. Sizes scale with the canvas: ReferenceSize is the smaller
// side of the canvas the style was saved from.
public sealed class TextStyle
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public double ReferenceSize { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";

    // A font file saved beside the style (File is relative to the styles folder).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Asset? Font { get; set; }

    public bool Bold { get; set; }
    public double FontSize { get; set; } = 48;
    public string Color { get; set; } = "#000000";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StrokeColor { get; set; }

    public double StrokeWidth { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Left;
    public double LineHeight { get; set; } = 1.0;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextBackground? Box { get; set; }

    public CaptionPosition CaptionPosition { get; set; } = CaptionPosition.Bottom;
}

// A project saved with save_template: canvas, audio settings, the chosen layers and their assets
// (files in the template's folder). PinToEnd layers keep their distance from the project's end.
public sealed class TemplateFile
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public Project Project { get; set; } = new();
    public List<string> PinToEnd { get; set; } = [];
}
