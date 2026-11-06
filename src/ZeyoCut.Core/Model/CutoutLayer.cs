// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

// text_behind_subject: the subject of a clip or picture drawn a second time, above the text, cut out
// with its matte. It always follows its source layers (one clip and the pieces splits made of it):
// their timing, trims, speed, transforms, effects and keyframes. Only its name, zIndex, visibility
// and opacity are its own; start, end and parentId stay unset. generate_captions (behindSubject)
// makes one for its caption group, so its fades leave other texts behind the subject.
public sealed class CutoutLayer : Layer, IMattable
{
    [JsonIgnore]
    public override string TypeName => "cutout";

    public List<string> Sources { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaptionGroup { get; set; }

    [JsonPropertyOrder(10)]
    public AnimatedValue Opacity { get; set; } = new(1);

    [JsonPropertyOrder(12)]
    public Matte? Matte { get; set; }
}
