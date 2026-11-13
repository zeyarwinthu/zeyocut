// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

public enum FieldKind
{
    Section,
    Info,
    Number,
    Text,
    LongText,
    Toggle,
    Choice,
    Color,
}

// One row of the inspector. A field with Commit can be changed there: Commit turns what was typed or
// chosen into the edit that makes the change (the Core operation a timeline drag or an MCP tool
// makes), or throws a ZeyoCutException that says what's wrong with it. A toggle's value is true or
// false, a choice's one of Choices. Unit is shown after a number; Tip explains the field.
public sealed record InspectorField(string Label, FieldKind Kind, string Value, Func<string, EditOperation>? Commit = null,
    IReadOnlyList<string>? Choices = null, string Unit = "", string? Tip = null)
{
    public bool Editable => Commit is not null;

    public bool IsOn => Value == "true";

    public IReadOnlyList<FieldChoice> Options => Choices?.Select(c => new FieldChoice(this, c, c == Value)).ToList() ?? [];
}

// One option of a choice field.
public sealed record FieldChoice(InspectorField Field, string Name, bool Selected)
{
    public string Group => "inspector:" + Field.Label;
}

// What the inspector shows for a clip, and how each of its fields is changed. Timing moves and trims
// the clip as the timeline does; the rest are the layer's fields, as update_layer sets them. A
// caption's text and timing are its own, and its style is the whole track's: a style change is made
// to every caption in the track at once, as one undo step.
public static partial class InspectorFields
{
    public const string NoFontFile = "(none)";

    public static List<InspectorField> Layer(Project project, Layer layer, TimelineClip? clip)
    {
        var id = layer.Id;
        var end = TimelineEditor.ContentEnd(project, layer);
        var group = layer.ParentId is { } parentId && project.FindLayer(parentId) is { } parent && TimelineLayout.IsCaptionGroup(parent) ? parent : null;
        var cues = group is null ? [] : project.Layers.Where(l => l.ParentId == group.Id && l is TextLayer).Select(l => l.Id).ToList();
        var fields = new List<InspectorField> { Section(group is null ? "Clip" : "Caption"), Info("Id", id) };
        if (!string.IsNullOrEmpty(layer.Name) && layer.Name != id)
        {
            fields.Add(Info("Name", layer.Name));
        }

        if (group is not null)
        {
            fields.Add(Info("Track", $"{group.Id}, {cues.Count} caption(s)"));
        }

        var open = layer is ImageLayer or TextLayer && layer.End is null;
        fields.Add(Number("Start", layer.Start, "s", text => new MoveLayerOperation(id, Parse(text, "Start", min: 0))));
        fields.Add(Number("End", end, "s", text => new TrimLayerOperation(id, null, Parse(text, "End", min: 0)), open ? "Runs to the project's end until you change it" : null));
        fields.Add(Number("Length", end - layer.Start, "s", text => new TrimLayerOperation(id, null, layer.Start + Parse(text, "Length", above: 0))));
        if (layer is VisualLayer visual && layer.ParentId is null)
        {
            fields.Add(new InspectorField("Draw level", FieldKind.Number, visual.ZIndex.ToString(CultureInfo.InvariantCulture),
                text => new ReorderLayerOperation(id, (int)Parse(text, "Draw level", whole: true)), Tip: "zIndex: a higher level draws on top. The timeline's lanes follow it"));
        }

        if (layer is AudioLayer)
        {
            fields.Add(Toggle("Muted", !layer.Visible, on => new UpdateLayerOperation(id, new JsonObject { ["visible"] = !on })));
        }
        else
        {
            fields.Add(Toggle("Shown", layer.Visible, on => new UpdateLayerOperation(id, new JsonObject { ["visible"] = on })));
        }

        if (layer.ParentId is { } owner && group is null)
        {
            fields.Add(Info("Parent", owner));
        }

        if (clip?.Children > 0)
        {
            var children = ProjectEditor.Subtree(project, id).Skip(1).Select(l => l.Id).ToList();
            fields.Add(Info("Child layers", children.Count > 6 ? string.Join(", ", children.Take(6)) + $" and {children.Count - 6} more" : string.Join(", ", children)));
        }

        if (layer.Language is { } language)
        {
            fields.Add(Info("Language", language));
        }

        foreach (var mark in clip?.Marks ?? [])
        {
            fields.Add(Info("Followed by", mark));
        }

        switch (layer)
        {
            case VideoLayer video:
                fields.Add(Section("Video"));
                fields.Add(Info("Asset", video.Asset));
                fields.Add(Info("Plays", $"{Num(video.TrimIn)} – {Num(video.TrimOut)} s of the file"));
                fields.Add(Info("Speed", $"{Num(video.Speed)}×"));
                fields.Add(Section("Sound"));
                if (project.FindAsset(video.Asset)?.HasAudio == false)
                {
                    fields.Add(Info("Sound", "the file has none"));
                    break;
                }

                fields.Add(Toggle("Sound on", !video.Muted, on => new UpdateLayerOperation(id, new JsonObject { ["muted"] = !on })));
                AddSound(fields, id, video.Role, video.Volume, video.FadeIn, video.FadeOut);
                break;
            case AudioLayer audio:
                fields.Add(Section("Sound"));
                fields.Add(Info("Asset", audio.Asset));
                fields.Add(Info("Plays", $"{Num(audio.TrimIn)} – {(audio.TrimOut is { } trimOut ? Num(trimOut) + " s" : "the end")} of the file"));
                AddSound(fields, id, audio.Role, audio.Volume, audio.FadeIn, audio.FadeOut);
                break;
            case TextLayer text:
                fields.Add(Section("Text"));
                fields.Add(new InspectorField("Text", FieldKind.LongText, text.Text, value => new UpdateLayerOperation(id, new JsonObject { ["text"] = value.ReplaceLineEndings("\n") }),
                    Tip: "Enter saves it; Shift+Enter starts a new line"));
                fields.Add(Section(group is null ? "Style" : $"Style of all {cues.Count} caption(s) in the track"));
                AddStyle(fields, project, text, changes => group is null
                    ? new UpdateLayerOperation(id, changes)
                    : new UpdateLayersOperation($"update_layer {group.Id}", cues, changes));
                break;
            case ImageLayer image when image.Drawings.Count > 0:
                fields.Add(Section("Image"));
                fields.Add(Info("Drawings", string.Join(", ", image.Drawings.Select(d => $"{d.Key}: {d.Value}"))));
                break;
        }

        return fields;
    }

    // A number as typed: digits with a point or comma, and the unit after it or not.
    public static double Parse(string text, string label, double? min = null, double? above = null, bool whole = false, double? max = null)
    {
        var match = NumberPattern().Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new ZeyoCutException($"{label} must be a number, like 1.5 (got '{text.Trim()}')");
        }

        if (whole && value != Math.Floor(value))
        {
            throw new ZeyoCutException($"{label} must be a whole number (got {Num(value)})");
        }

        if (min is { } low && value < low)
        {
            throw new ZeyoCutException($"{label} can't be less than {Num(low)} (got {Num(value)})");
        }

        if (above is { } floor && value <= floor)
        {
            throw new ZeyoCutException($"{label} must be more than {Num(floor)} (got {Num(value)})");
        }

        if (max is { } high && value > high)
        {
            throw new ZeyoCutException($"{label} can't be more than {Num(high)} (got {Num(value)})");
        }

        return value;
    }

    public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void AddSound(List<InspectorField> fields, string id, AudioRole role, double volume, double fadeIn, double fadeOut)
    {
        fields.Add(new InspectorField("Role", FieldKind.Choice, EnumNames.ToName(role), value => Update(id, "role", value), EnumNames.All<AudioRole>().ToList(),
            Tip: "Music is lowered under voice when the project ducks music"));
        fields.Add(Number("Volume", volume, "×", text => Update(id, "volume", Parse(text, "Volume", min: 0, max: 10)), "1 plays it as recorded, 0 is silent, up to 10"));
        fields.Add(Number("Fade in", fadeIn, "s", text => Update(id, "fadeIn", Parse(text, "Fade in", min: 0))));
        fields.Add(Number("Fade out", fadeOut, "s", text => Update(id, "fadeOut", Parse(text, "Fade out", min: 0))));
    }

    private static void AddStyle(List<InspectorField> fields, Project project, TextLayer text, Func<JsonObject, EditOperation> change)
    {
        EditOperation Set(string key, JsonNode? value) => change(new JsonObject { [key] = value });

        fields.Add(new InspectorField("Font", FieldKind.Text, text.FontFamily, value => Set("fontFamily", Required(value, "Font")),
            Tip: "An installed font's family name, such as Segoe UI. A font file, when chosen, is used instead"));
        var fonts = project.Assets.Where(a => a.Type == AssetType.Font).Select(a => a.Id).ToList();
        if (fonts.Count > 0 || text.FontAsset is not null)
        {
            fields.Add(new InspectorField("Font file", FieldKind.Choice, text.FontAsset ?? NoFontFile, value => Set("fontAsset", value == NoFontFile ? null : value),
                [NoFontFile, .. fonts], Tip: "A font imported into the project"));
        }

        fields.Add(Number("Size", text.FontSize, "px", value => Set("fontSize", Parse(value, "Size", above: 0))));
        fields.Add(Toggle("Bold", text.Bold, on => Set("bold", on)));
        fields.Add(new InspectorField("Color", FieldKind.Color, text.Color, value => Set("color", Required(value, "Color")), Tip: "#RRGGBB, or #RRGGBBAA with opacity"));
        fields.Add(new InspectorField("Outline", FieldKind.Color, text.StrokeColor ?? "", value => Set("strokeColor", Optional(value)), Tip: "#RRGGBB; empty for none"));
        fields.Add(Number("Outline width", text.StrokeWidth, "px", value => Set("strokeWidth", Parse(value, "Outline width", min: 0))));
        fields.Add(new InspectorField("Align", FieldKind.Choice, EnumNames.ToName(text.Align), value => Set("align", value), EnumNames.All<TextAlign>().ToList()));
        fields.Add(new InspectorField("Max width", FieldKind.Number, text.MaxWidth is { } width ? Num(width) : "",
            value => Set("maxWidth", Optional(value) is null ? null : Parse(value, "Max width", above: 0)), Unit: "px", Tip: "Longer lines wrap; empty for no limit"));
        fields.Add(Number("Line height", text.LineHeight, "×", value => Set("lineHeight", Parse(value, "Line height", above: 0))));
        fields.Add(new InspectorField("Box", FieldKind.Color, text.Box?.Color ?? "", value => Set("box", Optional(value) is { } color ? new JsonObject { ["color"] = color } : null),
            Tip: "A box behind the text: #RRGGBBAA; empty for none"));
        if (text.Box is { } box)
        {
            fields.Add(Number("Box padding", box.Padding, "px", value => Set("box", new JsonObject { ["padding"] = Parse(value, "Box padding", min: 0) })));
            fields.Add(Number("Box corners", box.CornerRadius, "px", value => Set("box", new JsonObject { ["cornerRadius"] = Parse(value, "Box corners", min: 0) })));
        }
    }

    private static EditOperation Update(string id, string key, JsonNode? value) => new UpdateLayerOperation(id, new JsonObject { [key] = value });

    private static string Required(string value, string label) =>
        string.IsNullOrWhiteSpace(value) ? throw new ZeyoCutException($"{label} can't be empty") : value.Trim();

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InspectorField Section(string title) => new(title, FieldKind.Section, "");

    private static InspectorField Info(string label, string value) => new(label, FieldKind.Info, value);

    private static InspectorField Number(string label, double value, string unit, Func<string, EditOperation> commit, string? tip = null) =>
        new(label, FieldKind.Number, Num(value), commit, Unit: unit, Tip: tip);

    private static InspectorField Toggle(string label, bool on, Func<bool, EditOperation> commit) =>
        new(label, FieldKind.Toggle, on ? "true" : "false", value => commit(value == "true"));

    [GeneratedRegex(@"^\s*([-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][-+]?\d+)?)\s*(?:s|px|×|x)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();
}
