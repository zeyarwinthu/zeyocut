// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

public enum MarkerKind
{
    Chapter,
    Scene
}

// A named point on the timeline: a chapter or a scene. Ripple edits move markers with the time
// around them, like the camera.
public sealed class Marker
{
    public string Id { get; set; } = "";
    public double Time { get; set; }
    public string Name { get; set; } = "";
    public MarkerKind Kind { get; set; }
}
