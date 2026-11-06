// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public enum MatteMethod
{
    Chroma,
    EdgeFill,
    AiImage,
    AiVideo
}

// Image and video layers whose background can be removed (remove_background).
public interface IMattable
{
    string Id { get; }

    Matte? Matte { get; set; }
}

// remove_background: which of the layer's pixels are background. chroma keys a color while the layer
// is drawn; the other methods compute an alpha matte once and cache it next to the asset. Tolerance,
// softness and spill run 0..1. shrink (negative grows) and feather (refine_matte) work on the matte's
// edge, in the layer's own pixels.
public sealed class Matte
{
    public MatteMethod Method { get; set; }

    // The key color (chroma) or the background color (edgeFill; null = the color most common along
    // the image's border).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Tolerance { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Softness { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Spill { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Shrink { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Feather { get; set; }
}
