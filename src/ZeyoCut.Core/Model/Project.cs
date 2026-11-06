// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public sealed class Project
{
    public const int CurrentFormatVersion = 6;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public double Duration { get; set; } = 10;
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public AudioSettings Audio { get; set; } = new();

    // Null until set_camera, a camera keyframe or camera_shake creates it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Camera? Camera { get; set; }

    public List<Asset> Assets { get; set; } = [];
    public List<Layer> Layers { get; set; } = [];

    // Written only when there are some.
    public List<Track> Tracks { get; set; } = [];

    // Written only when there are some; kept in time order.
    public List<Marker> Markers { get; set; } = [];

    public Layer? FindLayer(string id) => Layers.Find(l => l.Id == id);

    public Asset? FindAsset(string id) => Assets.Find(a => a.Id == id);

    public Track? FindTrack(string id) => Tracks.Find(t => t.Id == id);

    public Marker? FindMarker(string id) => Markers.Find(m => m.Id == id);
}
