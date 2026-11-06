// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public interface IColorAdjustable
{
    AnimatedValue Brightness { get; set; }
    AnimatedValue Contrast { get; set; }
    AnimatedValue Saturation { get; set; }
}

public sealed class VideoLayer : VisualLayer, IColorAdjustable, IMattable
{
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 10;

    [JsonIgnore]
    public override string TypeName => "video";

    public string Asset { get; set; } = "";
    public double TrimIn { get; set; }
    public double TrimOut { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public AudioRole Role { get; set; } = AudioRole.Voice;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public double? CropWidth { get; set; }
    public double? CropHeight { get; set; }

    [JsonPropertyOrder(11)]
    public AnimatedValue Brightness { get; set; } = new(0);

    [JsonPropertyOrder(11)]
    public AnimatedValue Contrast { get; set; } = new(1);

    [JsonPropertyOrder(11)]
    public AnimatedValue Saturation { get; set; } = new(1);

    [JsonPropertyOrder(12)]
    public AnimatedValue CropX { get; set; } = new(0);

    [JsonPropertyOrder(12)]
    public AnimatedValue CropY { get; set; } = new(0);

    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Matte? Matte { get; set; }

    // Timeline seconds the clip occupies; video layers have no 'end', it follows from the trims and speed.
    [JsonIgnore]
    public double Length => Speed > 0 ? (TrimOut - TrimIn) / Speed : 0;

    [JsonIgnore]
    public double TimelineEnd => Start + Length;
}
