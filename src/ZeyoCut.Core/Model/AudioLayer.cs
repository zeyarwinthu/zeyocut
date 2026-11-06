// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public sealed class AudioLayer : Layer
{
    [JsonIgnore]
    public override string TypeName => "audio";

    public string Asset { get; set; } = "";
    public double TrimIn { get; set; }
    public double? TrimOut { get; set; }
    public double Volume { get; set; } = 1;
    public AudioRole Role { get; set; } = AudioRole.Music;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
}
