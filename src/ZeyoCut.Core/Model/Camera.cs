// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

// The project's camera. x, y is the scene point shown at the center of the frame, zoom 2 shows half as
// much of the scene, and rotation turns the camera clockwise (so the picture turns the other way).
// The shake channels are written by camera_shake: screen pixels and degrees added on top.
public sealed class Camera
{
    public const string Id = "camera";
    public const double MinZoom = 0.01;
    public const double MaxZoom = 100;

    public AnimatedValue X { get; set; } = new(0);
    public AnimatedValue Y { get; set; } = new(0);
    public AnimatedValue Zoom { get; set; } = new(1);
    public AnimatedValue Rotation { get; set; } = new(0);
    public AnimatedValue ShakeX { get; set; } = new(0);
    public AnimatedValue ShakeY { get; set; } = new(0);
    public AnimatedValue ShakeRotation { get; set; } = new(0);
}
