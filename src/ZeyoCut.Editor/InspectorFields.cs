// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
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
// false, a choice's one of Choices. Unit is shown after a number; Tip explains the field. A Keyed
// field is animated: it shows the value at the playhead. ToggleKey is its diamond button's edit at
// the playhead's time: it adds a keyframe there, or takes away the one there (KeyHere).
public sealed record InspectorField(string Label, FieldKind Kind, string Value, Func<string, EditOperation>? Commit = null,
    IReadOnlyList<string>? Choices = null, string Unit = "", string? Tip = null, bool Keyed = false, bool KeyHere = false,
    Func<double, EditOperation>? ToggleKey = null)
{
    public const string KeyedTip = "Animated: this is its value at the playhead, and a change adds a keyframe there";

    public bool Editable => Commit is not null;

    public bool HasToggle => ToggleKey is not null;

    public string KeyGlyph => KeyHere ? "◆" : "◇";

    public string KeyTip => KeyHere ? "Take away the keyframe at the playhead"
        : Keyed ? "Add a keyframe at the playhead with this value"
        : "Animate: add a keyframe at the playhead with this value";

    public bool IsOn => Value == "true";

    public IReadOnlyList<FieldChoice> Options => Choices?.Select(c => new FieldChoice(this, c, c == Value)).ToList() ?? [];
}

// One option of a choice field.
public sealed record FieldChoice(InspectorField Field, string Name, bool Selected)
{
    public string Group => "inspector:" + Field.Label;
}

// What the inspector shows for a clip at the playhead's time, and how each of its fields is changed.
// Timing moves and trims the clip as the timeline does; the rest are the layer's fields, as
// update_layer and set_effect set them, or a keyframe at the playhead for an animated one
// (ValueEditor.Set). A caption's text and timing are its own, and its style and position the whole
// track's: a change to them is made to every caption in the track at once, as one undo step.
public static partial class InspectorFields
{
    public const string NoFontFile = "(none)";

    public static List<InspectorField> Layer(Project project, Layer layer, TimelineClip? clip, double time)
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

        if (group is not null && layer is VisualLayer cue)
        {
            var (x, y) = (Interpolator.Evaluate(cue.X, time), Interpolator.Evaluate(cue.Y, time));
            const string shifts = "Moves every caption in the track by the same amount";
            fields.Add(Section($"Position of all {cues.Count} caption(s) in the track"));
            fields.Add(Number("X", x, "px", text => new ShiftLayersOperation($"update_layer {group.Id}", cues, Parse(text, "X") - x, 0), shifts));
            fields.Add(Number("Y", y, "px", text => new ShiftLayersOperation($"update_layer {group.Id}", cues, 0, Parse(text, "Y") - y), shifts));
        }
        else if (layer is VisualLayer placed)
        {
            AddTransform(fields, project, placed, time);
        }

        switch (layer)
        {
            case VideoLayer video:
                fields.Add(Section("Video"));
                fields.Add(Info("Asset", video.Asset));
                fields.Add(Info("Plays", $"{Num(video.TrimIn)} – {Num(video.TrimOut)} s of the file"));
                fields.Add(Info("Speed", $"{Num(video.Speed)}×"));
                AddCrop(fields, project, video);
                AddRedactions(fields, project, video);
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

        if (group is null && layer is VisualLayer shown)
        {
            AddEffects(fields, project, shown, time);
            if (shown is IColorAdjustable)
            {
                AddAdjustments(fields, project, shown, time);
            }
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

    // An animatable number (ValueEditor) at the playhead: a change sets it, or a keyframe there when
    // it's animated, and its diamond adds or takes away a keyframe there. factor turns the value kept
    // into the one shown, as 0.5 into 50 %.
    private static InspectorField Animated(Project project, VisualLayer layer, string property, double time, string label, double factor, string unit, string tip,
        double? min = null, double? max = null)
    {
        var value = AnimatableProperties.Numeric(layer).First(p => p.Name == property).Value;
        var keyed = value.Keyframes.Count > 0;
        return new InspectorField(label, FieldKind.Number, Num((keyed ? Interpolator.Evaluate(value, time) : value.Value) * factor),
            text => ValueEditor.Set(project, layer.Id, new Dictionary<string, double> { [property] = Parse(text, label, min: min, max: max) / factor }, time),
            Unit: unit, Tip: keyed ? $"{InspectorField.KeyedTip}. {tip}" : tip, Keyed: keyed, KeyHere: ValueEditor.KeyAt(project, value, time) is not null,
            ToggleKey: at => ValueEditor.ToggleKeyframe(project, layer.Id, property, at));
    }

    // Where the layer is, its size, turn and opacity. A child's x and y are in its parent's pixels.
    private static void AddTransform(List<InspectorField> fields, Project project, VisualLayer layer, double time)
    {
        InspectorField Field(string property, string label, double factor, string unit, string tip, double? min = null, double? max = null) =>
            Animated(project, layer, property, time, label, factor, unit, tip, min, max);

        fields.Add(Section("Transform"));
        var where = layer.ParentId is { } parent ? $"in its parent '{parent}', in the parent's own pixels" : "on the canvas, in pixels from its top-left corner";
        if (PlacementEditor.PositionLocked(layer) is { } locked)
        {
            fields.Add(new InspectorField("Position", FieldKind.Info, "follows a motion path", Tip: locked));
        }
        else
        {
            fields.Add(Field("x", "X", 1, "px", $"Where the layer's anchor is {where}"));
            fields.Add(Field("y", "Y", 1, "px", $"Where the layer's anchor is {where}"));
        }

        fields.Add(Field("scaleX", "Scale X", 100, "%", "100 is its own size; a negative scale flips it"));
        fields.Add(Field("scaleY", "Scale Y", 100, "%", "100 is its own size; a negative scale flips it"));
        fields.Add(Field("rotation", "Rotation", 1, "°", "Degrees clockwise around the anchor"));
        fields.Add(Field("opacity", "Opacity", 100, "%", "100 is solid, 0 is invisible", min: 0, max: 100));
    }

    // The layer's effects, each switched on and off as set_effect adds and takes it away, and their
    // settings: a color for the whole effect, numbers animatable as the transform's are.
    private static void AddEffects(List<InspectorField> fields, Project project, VisualLayer layer, double time)
    {
        var id = layer.Id;
        static EffectValues Values(string? color = null) => new(color, null, null, null, null, null, null);

        // A new tint needs a color: white, which its color field then changes.
        InspectorField Switch(string label, EffectKind kind, bool on, string tip) =>
            Toggle(label, on, value => value
                ? new SetEffectOperation(id, kind, Values(kind == EffectKind.Tint ? "#FFFFFF" : null))
                : new SetEffectOperation(id, kind, Values(), Remove: true), tip);

        InspectorField Color(string label, EffectKind kind, string color) =>
            new(label, FieldKind.Color, color, value => new SetEffectOperation(id, kind, Values(Required(value, label))), Tip: "#RRGGBB, or #RRGGBBAA with opacity");

        InspectorField Field(string property, string label, double factor, string unit, string tip, double? min = null, double? max = null) =>
            Animated(project, layer, property, time, label, factor, unit, tip, min, max);

        fields.Add(Section("Effects"));
        fields.Add(Switch("Blur", EffectKind.Blur, layer.Blur is not null, "Softens the layer"));
        if (layer.Blur is not null)
        {
            fields.Add(Field("blur.x", "Blur X", 1, "px", "How far it spreads sideways", min: 0, max: EffectEditor.MaxBlur));
            fields.Add(Field("blur.y", "Blur Y", 1, "px", "How far it spreads up and down", min: 0, max: EffectEditor.MaxBlur));
        }

        fields.Add(Switch("Shadow", EffectKind.Shadow, layer.Shadow is not null, "A soft shadow behind the layer"));
        if (layer.Shadow is { } shadow)
        {
            fields.Add(Color("Shadow color", EffectKind.Shadow, shadow.Color));
            fields.Add(Field("shadow.x", "Shadow X", 1, "px", "How far right it falls; negative is left"));
            fields.Add(Field("shadow.y", "Shadow Y", 1, "px", "How far down it falls; negative is up"));
            fields.Add(Field("shadow.blur", "Shadow blur", 1, "px", "How soft its edge is", min: 0, max: EffectEditor.MaxBlur));
            fields.Add(Field("shadow.opacity", "Shadow opacity", 100, "%", "100 is solid", min: 0, max: 100));
        }

        fields.Add(Switch("Border", EffectKind.Outline, layer.Outline is not null, "A line around the layer's shape (the outline effect)"));
        if (layer.Outline is { } outline)
        {
            fields.Add(Color("Border color", EffectKind.Outline, outline.Color));
            fields.Add(Field("outline.width", "Border width", 1, "px", "How thick the line is", min: 0, max: EffectEditor.MaxOutline));
        }

        fields.Add(Switch("Tint", EffectKind.Tint, layer.Tint is not null, "Mixes the layer's colors toward one color"));
        if (layer.Tint is { } tint)
        {
            fields.Add(Color("Tint color", EffectKind.Tint, tint.Color));
            fields.Add(Field("tint.amount", "Tint amount", 100, "%", "100 paints it in the color, keeping its shape", min: 0, max: 100));
        }
    }

    // A picture's brightness, contrast and saturation, animatable.
    private static void AddAdjustments(List<InspectorField> fields, Project project, VisualLayer layer, double time)
    {
        fields.Add(Section("Adjustments"));
        fields.Add(Animated(project, layer, "brightness", time, "Brightness", 100, "%", "0 leaves it as it is; -100 is black, 100 is white", min: -100, max: 100));
        fields.Add(Animated(project, layer, "contrast", time, "Contrast", 100, "%", "100 leaves it as it is; 0 is flat gray", min: 0, max: 400));
        fields.Add(Animated(project, layer, "saturation", time, "Saturation", 100, "%", "100 leaves it as it is; 0 is black and white", min: 0, max: 400));
    }

    // The face blurs (apply_redaction) that hide parts of the clip's footage, with their settings as
    // update_layer sets them on the redaction: a strength for pixelate and blur, a fill for solid.
    private static void AddRedactions(List<InspectorField> fields, Project project, VideoLayer video)
    {
        var redactions = project.Layers.OfType<RedactionLayer>()
            .Where(r => r.Tracks.Select(project.FindTrack).OfType<Track>().Any(t => t.Asset == video.Asset))
            .ToList();
        foreach (var redaction in redactions)
        {
            var id = redaction.Id;
            fields.Add(Section($"Redaction '{id}'"));
            fields.Add(new InspectorField("Style", FieldKind.Choice, EnumNames.ToName(redaction.Style), value => Update(id, "style", value), EnumNames.All<RedactionStyle>().ToList(),
                Tip: "How the boxes are hidden, in every clip of this footage"));
            if (redaction.Style == RedactionStyle.Solid)
            {
                fields.Add(new InspectorField("Fill color", FieldKind.Color, redaction.Color, value => Update(id, "color", Required(value, "Fill color")), Tip: "#RRGGBB"));
            }
            else
            {
                fields.Add(Number("Strength", redaction.Strength, "", text => Update(id, "strength", Parse(text, "Strength", min: 0, max: 100)),
                    "0 to 100: bigger blocks, or more blur"));
            }

            fields.Add(Number("Padding", redaction.PaddingPercent, "%", text => Update(id, "paddingPercent", Parse(text, "Padding", min: 0, max: RedactionEditor.MaxPadding)),
                "Each side of a box grows by this share of its size"));
            fields.Add(Number("Feather", redaction.Feather, "px", text => Update(id, "feather", Parse(text, "Feather", min: 0, max: RedactionEditor.MaxFeather)),
                "A soft edge outside the box, in the footage's pixels"));
            fields.Add(Number("Hold frames", redaction.HoldFrames, "", text => Update(id, "holdFrames", (int)Parse(text, "Hold frames", min: 0, max: RedactionEditor.MaxHold, whole: true)),
                "How many frames a box stays before a face is first found and after it's last seen"));
        }
    }

    // The sides of the picture the video's crop cuts off, in the file's pixels.
    private static void AddCrop(List<InspectorField> fields, Project project, VideoLayer video)
    {
        fields.Add(Section("Crop"));
        if (PlacementEditor.CropLocked(project, video) is { } locked)
        {
            fields.Add(Info("Crop", locked));
            return;
        }

        var sides = PlacementEditor.Sides(video, project.FindAsset(video.Asset)!);
        const string tip = "Pixels of the file cut off this side; the rest of the picture stays where it is";
        fields.Add(Number("Left", sides.Left, "px", text => new CropLayerOperation(video.Id, sides with { Left = Parse(text, "Left", min: 0) }), tip));
        fields.Add(Number("Top", sides.Top, "px", text => new CropLayerOperation(video.Id, sides with { Top = Parse(text, "Top", min: 0) }), tip));
        fields.Add(Number("Right", sides.Right, "px", text => new CropLayerOperation(video.Id, sides with { Right = Parse(text, "Right", min: 0) }), tip));
        fields.Add(Number("Bottom", sides.Bottom, "px", text => new CropLayerOperation(video.Id, sides with { Bottom = Parse(text, "Bottom", min: 0) }), tip));
    }

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

    private static InspectorField Toggle(string label, bool on, Func<bool, EditOperation> commit, string? tip = null) =>
        new(label, FieldKind.Toggle, on ? "true" : "false", value => commit(value == "true"), Tip: tip);

    [GeneratedRegex(@"^\s*([-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][-+]?\d+)?)\s*(?:s|px|×|x|%|°)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();
}
