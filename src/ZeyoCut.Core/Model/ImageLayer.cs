// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public sealed class ImageLayer : VisualLayer, IColorAdjustable, IMattable
{
    [JsonIgnore]
    public override string TypeName => "image";

    public bool FlipX { get; set; }

    public Dictionary<string, string> Drawings { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyOrder(9)]
    public DrawingTrack Drawing { get; set; } = new();

    [JsonPropertyOrder(11)]
    public AnimatedValue Brightness { get; set; } = new(0);

    [JsonPropertyOrder(11)]
    public AnimatedValue Contrast { get; set; } = new(1);

    [JsonPropertyOrder(11)]
    public AnimatedValue Saturation { get; set; } = new(1);

    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Matte? Matte { get; set; }
}
