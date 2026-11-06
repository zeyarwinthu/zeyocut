// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ImageLayer), "image")]
[JsonDerivedType(typeof(TextLayer), "text")]
[JsonDerivedType(typeof(AudioLayer), "audio")]
[JsonDerivedType(typeof(VideoLayer), "video")]
[JsonDerivedType(typeof(CutoutLayer), "cutout")]
[JsonDerivedType(typeof(RedactionLayer), "redaction")]
public abstract class Layer
{
    [JsonIgnore]
    public abstract string TypeName { get; }

    [JsonPropertyOrder(-10)]
    public string Id { get; set; } = "";

    [JsonPropertyOrder(-9)]
    public string Name { get; set; } = "";

    // The part's name in a rig (head, arm_l, ...); actions match parts by it.
    [JsonPropertyOrder(-9)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RigRole { get; set; }

    [JsonPropertyOrder(-8)]
    public double Start { get; set; }

    [JsonPropertyOrder(-7)]
    public double? End { get; set; }

    [JsonPropertyOrder(-6)]
    public int ZIndex { get; set; }

    [JsonPropertyOrder(-5)]
    public string? ParentId { get; set; }

    [JsonPropertyOrder(-4)]
    public bool Visible { get; set; } = true;

    // en, ja, my, pt-BR: the layer and its children are only in that language's version. A video's
    // tag is the language of its sound; its picture is in every version.
    [JsonPropertyOrder(-4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }
}

public abstract class VisualLayer : Layer
{
    [JsonPropertyOrder(-3)]
    public double AnchorX { get; set; }

    [JsonPropertyOrder(-3)]
    public double AnchorY { get; set; }

    // Drawn in screen space, unmoved by the camera (captions, titles, watermarks). Children follow
    // their top-level layer.
    [JsonPropertyOrder(-3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IgnoreCamera { get; set; }

    [JsonPropertyOrder(10)]
    public AnimatedValue X { get; set; } = new(0);

    [JsonPropertyOrder(10)]
    public AnimatedValue Y { get; set; } = new(0);

    [JsonPropertyOrder(10)]
    public AnimatedValue Rotation { get; set; } = new(0);

    [JsonPropertyOrder(10)]
    public AnimatedValue ScaleX { get; set; } = new(1);

    [JsonPropertyOrder(10)]
    public AnimatedValue ScaleY { get; set; } = new(1);

    [JsonPropertyOrder(10)]
    public AnimatedValue Opacity { get; set; } = new(1);

    // When set, it places the layer instead of x, y.
    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MotionPath? MotionPath { get; set; }

    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Mask? Mask { get; set; }

    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Tint? Tint { get; set; }

    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Outline? Outline { get; set; }

    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DropShadow? Shadow { get; set; }

    [JsonPropertyOrder(12)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlurEffect? Blur { get; set; }
}
