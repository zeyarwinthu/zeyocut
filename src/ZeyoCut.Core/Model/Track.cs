// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

// Faces, Plates: found on every frame by detect_and_track's face or plate detection, not followed from a box.
public enum TrackMethod
{
    Csrt,
    Template,
    OpticalFlow,
    Faces,
    Plates
}

public enum TrackDirection
{
    Forward,
    Backward,
    Both
}

// A box in a video asset's own pixels at one of its frames.
public sealed class TrackBox
{
    public int Frame { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

// One frame of a track. Confidence (0-1) is how closely the box still matches the picture at the
// anchor it was tracked from; lost frames hold the box before them.
public sealed class TrackedFrame
{
    public int Frame { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Confidence { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Lost { get; set; }
}

// track_object: a box followed through a video asset's frames, in the asset's own frames and pixels,
// so trims, speed changes and splits of the clips that play it never break it. The boxes live in
// File (tracks/<id>-<hash>.json); every change writes a new file, so undo restores the old one.
// Origin is the frame track_object started from; anchors are the boxes set by hand (track_object and
// reanchor_track), sorted by frame. Tracking runs away from the origin, so frames past an anchor
// come from it.
public sealed class Track
{
    public string Id { get; set; } = "";
    public string Asset { get; set; } = "";

    // The clip the box was drawn on; reanchor_track maps times and boxes through it by default.
    public string Layer { get; set; } = "";
    public TrackMethod Method { get; set; }
    public TrackDirection Direction { get; set; }
    public int FirstFrame { get; set; }
    public int LastFrame { get; set; }
    public int Origin { get; set; }
    public string File { get; set; } = "";
    public List<TrackBox> Anchors { get; set; } = [];
}

// The contents of a track's file.
public sealed class TrackData
{
    public string Track { get; set; } = "";
    public string Asset { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public List<TrackedFrame> Frames { get; set; } = [];
}
