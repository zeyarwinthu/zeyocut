// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace ZeyoCut.Core.Model;

public enum TextAlign
{
    Left,
    Center,
    Right
}

public sealed class TextBackground
{
    public string Color { get; set; } = "#000000B3";
    public double Padding { get; set; } = 16;
    public double CornerRadius { get; set; }
}

public sealed class TextLayer : VisualLayer
{
    [JsonIgnore]
    public override string TypeName => "text";

    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Segoe UI";
    public string? FontAsset { get; set; }
    public bool Bold { get; set; }
    public double FontSize { get; set; } = 48;
    public string Color { get; set; } = "#000000";
    public string? StrokeColor { get; set; }
    public double StrokeWidth { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Left;
    public double? MaxWidth { get; set; }
    public double LineHeight { get; set; } = 1.0;
    public TextBackground? Box { get; set; }
}
