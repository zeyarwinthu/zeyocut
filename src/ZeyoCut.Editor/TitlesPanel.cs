// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// A card of the Titles view: a library style or template, with what it is.
public sealed record TitleCard(string Name, bool Template, string Detail, DateTime Saved)
{
    public System.Windows.Media.ImageSource? Picture { get; init; }
}

// What the Titles view's fields hold: a new title's, or the selected text's.
public sealed record TitleDraft(string First, string Second, string Fill, string Outline, string In, string Out);

// The Titles view: the library's styles and templates, and a title's two lines, colors and the way
// it comes in and goes out (TitleAnimations' presets).
public static class TitlesPanel
{
    public const string None = "None";
    public const string Keyframes = "Keyframes";

    // A new title's length, or up to the project's end when that's sooner.
    public const double Length = 4;

    public static readonly TitleDraft Fresh = new("Title", "", "#FFFFFF", "", TitleAnimations.Name(TitleAnimation.Pop), TitleAnimations.Name(TitleAnimation.Fade));

    public static IReadOnlyList<string> Animations { get; } = [None, .. Enum.GetValues<TitleAnimation>().Select(TitleAnimations.Name)];

    public static TitleAnimation? Animation(string name) =>
        Enum.GetValues<TitleAnimation>().Where(a => TitleAnimations.Name(a) == name).Select(a => (TitleAnimation?)a).FirstOrDefault();

    // How a side of a layer comes in or goes out: a preset's name, Keyframes when it has keyframes of
    // its own there, or None.
    public static string Shown(Project project, VisualLayer layer, TitleSide side) =>
        TitleAnimations.Detect(project, layer, side) is { } preset ? TitleAnimations.Name(preset)
            : TitleAnimations.Keyed(project, layer, side) ? Keyframes : None;

    // A text's first line and the rest.
    public static (string First, string Second) Lines(string text)
    {
        var at = text.IndexOf('\n');
        return at < 0 ? (text, "") : (text[..at], text[(at + 1)..]);
    }

    public static string Text(string first, string second) => second.Length == 0 ? first : $"{first}\n{second}";

    // What the fields show for a text on the timeline.
    public static TitleDraft Of(Project project, TextLayer text)
    {
        var (first, second) = Lines(text.Text);
        return new TitleDraft(first, second, text.Color, text.StrokeColor ?? "", Shown(project, text, TitleSide.In), Shown(project, text, TitleSide.Out));
    }

    // The fields a style fills in for a new title: its colors.
    public static TitleDraft Styled(TitleDraft draft, TextStyle? style) =>
        style is null ? draft : draft with { Fill = style.Color, Outline = style.StrokeColor ?? "" };

    // A new title from the playhead, Length seconds long or up to the project's end, centered, with
    // the fields' lines and colors; a style fills in the rest.
    public static TextLayerOptions Title(Project project, double at, TitleDraft draft) => new()
    {
        Text = Text(draft.First, draft.Second),
        Start = at,
        End = Math.Min(project.Duration, at + Length),
        Color = Color(draft.Fill),
        StrokeColor = Color(draft.Outline),
        Align = "center",
    };

    public static AddTitleOperation Add(Project project, double at, TitleDraft draft, TextStyle? style, string directory, Library library, Func<Project, ITextMeasurer>? measurer) =>
        new(Title(project, at, draft), style, directory, library, Animation(draft.In), Animation(draft.Out), measurer);

    // A text on the timeline told to come in or go out another way; null when it already does.
    public static TitleAnimationOperation? Animate(Project project, TextLayer text, TitleSide side, string choice) =>
        choice == Keyframes || choice == Shown(project, text, side) ? null : new TitleAnimationOperation(text.Id, side, Animation(choice));

    // A text's lines or colors changed as the inspector changes them; null when nothing changed.
    public static UpdateLayerOperation? Change(TextLayer text, TitleDraft draft)
    {
        var changes = new System.Text.Json.Nodes.JsonObject();
        var words = Text(draft.First, draft.Second);
        if (words != text.Text)
        {
            changes["text"] = words;
        }

        if (Color(draft.Fill) is { } fill && !string.Equals(fill, text.Color, StringComparison.OrdinalIgnoreCase))
        {
            changes["color"] = fill;
        }

        if (!string.Equals(Color(draft.Outline), text.StrokeColor, StringComparison.OrdinalIgnoreCase))
        {
            changes["strokeColor"] = Color(draft.Outline);
        }

        return changes.Count == 0 ? null : new UpdateLayerOperation(text.Id, changes);
    }

    // A title's text: a text layer that isn't a caption.
    public static TextLayer? Title(Project project, string? layerId) =>
        layerId is not null && project.FindLayer(layerId) is TextLayer text && !(text.ParentId is { } parent && project.FindLayer(parent) is { } group && CaptionTracks.IsTrack(group)) ? text : null;

    // What a style card says: its look.
    public static string Detail(TextStyle style) =>
        $"{(style.Font is null ? style.FontFamily : Path.GetFileNameWithoutExtension(style.Font.File))}{(style.Bold ? " bold" : "")}, {Num(style.FontSize)} px";

    // What a template card says: its canvas, length and layers.
    public static string Detail(TemplateFile template) =>
        $"{template.Project.Width}×{template.Project.Height}, {Num(template.Project.Duration)} s, {template.Project.Layers.Count} layer{(template.Project.Layers.Count == 1 ? "" : "s")}";

    private static string? Color(string text) => text.Trim() is { Length: > 0 } color ? color : null;

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
