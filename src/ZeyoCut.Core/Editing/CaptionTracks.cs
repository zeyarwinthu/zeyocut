// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// One caption as given to add_caption_track or read from an SRT file, seconds on the timeline.
public sealed record CueEntry(double Start, double End, string Text);

// What add_caption_track did: the language's caption group, its captions, and the problems with the
// cues it was given (left out, shortened or overlapping).
public sealed record CaptionTrack(string GroupId, string Language, IReadOnlyList<CaptionCue> Cues, IReadOnlyList<string> LayerIds, int Replaced, bool Created, IReadOnlyList<string> Problems);

// Where a track's captions sit: inside the title-safe margin at the top or the bottom, or in the
// middle of the canvas.
public enum CaptionPlace
{
    Top,
    Middle,
    Bottom
}

// What is behind a caption's letters: a dark box, an outline, or nothing.
public enum CaptionLook
{
    Box,
    Outline,
    None
}

// A change to the style of caption tracks; what is null stays as it is.
public sealed record CaptionRestyle
{
    public string? FontFamily { get; init; }
    public double? FontSize { get; init; }
    public CaptionLook? Look { get; init; }
    public CaptionPlace? Place { get; init; }
    public double? MaxWidth { get; init; }
}

// A caption track per language: its captions under a null layer captions_<language> tagged with the
// language, so each language's version shows its own captions and none of the others'.
public static partial class CaptionTracks
{
    // The box the subtitle style puts behind captions, and the outline Outline gives them, as shares
    // of the font size.
    public const string BoxColor = "#000000B3";
    public const double BoxPadding = 0.3;
    public const double BoxCorners = 0.2;
    public const double OutlineWidth = 0.1;

    public static string GroupId(string language) => $"captions_{language}";

    [GeneratedRegex(@"^\s*(?<start>\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>|\{\\[^}]*\}")]
    private static partial Regex Tags();

    // SRT text: blocks of an optional number, a 'start --> end' line and the caption's lines, with
    // blank lines between them. Formatting tags (<i>, <font ...>, {\an8}) are dropped.
    public static (List<CueEntry> Cues, List<string> Problems) ParseSrt(string text)
    {
        var cues = new List<CueEntry>();
        var problems = new List<string>();
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            var first = i + 1;
            var block = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                block.Add(lines[i]);
                i++;
            }

            var timeIndex = block.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
            if (timeIndex is < 0 or > 1)
            {
                problems.Add($"line {first}: this block has no 'start --> end' line, so it was left out");
                continue;
            }

            var match = TimeLine().Match(block[timeIndex]);
            if (!match.Success)
            {
                problems.Add($"line {first + timeIndex}: '{block[timeIndex].Trim()}' isn't a time line like 00:00:01,500 --> 00:00:03,000, so the block was left out");
                continue;
            }

            var body = block.Skip(timeIndex + 1).Select(l => Tags().Replace(l, "").Trim()).Where(l => l.Length > 0);
            cues.Add(new CueEntry(Time(match.Groups["start"].Value), Time(match.Groups["end"].Value), string.Join("\n", body)));
        }

        return (cues, problems);
    }

    // The cues as captions, in time order: one without text or with its end before its start is left
    // out, one after the project end too, and one running past it ends there. Overlaps are named.
    public static (List<CaptionCue> Cues, List<string> Problems) Check(IReadOnlyList<CueEntry> entries, double duration)
    {
        var cues = new List<CaptionCue>();
        var problems = new List<string>();
        for (var n = 0; n < entries.Count; n++)
        {
            var entry = entries[n];
            var lines = (entry.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var label = $"cue {n + 1}";
            if (lines.Count == 0)
            {
                problems.Add($"{label} has no text, so it was left out");
                continue;
            }

            label += $" (\"{Short(lines)}\")";
            var start = Math.Round(entry.Start, 3);
            var end = Math.Round(entry.End, 3);
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            {
                problems.Add($"{label} must start at 0 s or later and end after it starts (got {Num(entry.Start)}–{Num(entry.End)}s), so it was left out");
                continue;
            }

            if (start >= duration)
            {
                problems.Add($"{label} starts at {Num(start)}s, at or after the project end ({Num(duration)}s), so it was left out");
                continue;
            }

            if (end > duration + 1e-6)
            {
                problems.Add($"{label} runs past the project end ({Num(duration)}s), so it ends there");
                end = duration;
            }

            cues.Add(new CaptionCue(start, end, lines, n, n));
        }

        cues = cues.OrderBy(c => c.Start).ToList();
        for (var i = 1; i < cues.Count; i++)
        {
            var before = cues.Take(i).Where(c => c.End > cues[i].Start + 1e-6).ToList();
            foreach (var other in before)
            {
                problems.Add($"cues {other.FirstWord + 1} and {cues[i].FirstWord + 1} overlap at {Num(cues[i].Start)}–{Num(Math.Min(other.End, cues[i].End))}s, so both show at once, one over the other");
            }
        }

        return (cues, problems);
    }

    // Puts the cues in as the language's caption track, replacing its earlier captions.
    public static CaptionTrack Add(Project project, string language, IReadOnlyList<CaptionCue> cues, CaptionOptions options, IReadOnlyList<string> problems, ITextMeasurer? measurer)
    {
        if (cues.Count == 0)
        {
            throw new ZeyoCutException(problems.Count == 0 ? "there are no captions to add" : "none of the cues can be used:\n- " + string.Join("\n- ", problems));
        }

        var groupId = GroupId(language);
        if (project.FindLayer(groupId) is { } existing and not ImageLayer { Drawings.Count: 0 })
        {
            throw new ZeyoCutException($"layer '{groupId}' exists and isn't a caption group (a null layer); add_caption_track keeps the {language} captions under that id, so remove that layer first");
        }

        var (id, ids, replaced, created) = CaptionBuilder.Place(project, cues, options with { GroupId = groupId }, measurer);
        var group = project.FindLayer(id)!;
        group.Language = language;
        if (created)
        {
            group.Name = $"captions ({language})";
        }

        return new CaptionTrack(id, language, cues, ids, replaced, created, problems);
    }

    // Cues given as add_caption_track's cues or srt, checked against the project (Check) and put in
    // as the language's caption track; the problems with them come after the notes.
    public static CaptionTrack AddEntries(Project project, string language, IReadOnlyList<CueEntry> entries, CaptionOptions options, IReadOnlyList<string> notes, ITextMeasurer? measurer)
    {
        var (cues, problems) = Check(entries, project.Duration);
        return Add(project, language, cues, options, [.. notes, .. problems], measurer);
    }

    // A caption track's group: a null layer, as Add and generate_captions make it.
    public static ImageLayer Group(Project project, string groupId) =>
        ProjectEditor.GetLayer(project, groupId) is ImageLayer { Drawings.Count: 0 } group
            ? group
            : throw new ZeyoCutException($"'{groupId}' isn't a caption track (a null layer with the captions under it)");

    // A track's captions: the text layers under its group, in time order.
    public static List<TextLayer> Cues(Project project, string groupId) =>
        project.Layers.OfType<TextLayer>().Where(t => t.ParentId == groupId).OrderBy(t => t.Start).ToList();

    // A caption added to a track at start–end, styled like the caption before it in the track (the
    // first one after it, when there's none before): the same font, size, colors, box, width, place,
    // draw level and effects, with its anchor at the same place in the text box. A track without
    // captions gives it the look add_caption_track gives captions with these options. The end is cut
    // at the project end.
    public static TextLayer AddCue(Project project, string groupId, double start, double end, string text, CaptionOptions empty, ITextMeasurer? measurer)
    {
        var group = Group(project, groupId);
        var lines = Lines(text);
        if (lines.Count == 0)
        {
            throw new ZeyoCutException("a caption needs some text");
        }

        start = Math.Round(start, 3);
        end = Math.Min(Math.Round(end, 3), project.Duration);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || start >= project.Duration || end <= start)
        {
            throw new ZeyoCutException($"a caption must start at 0 s or later and before the project end ({Num(project.Duration)}s), and end after it starts (got {Num(start)}–{Num(end)}s)");
        }

        var cues = Cues(project, group.Id);
        var like = cues.LastOrDefault(c => c.Start <= start) ?? cues.FirstOrDefault();
        if (like is null)
        {
            var (_, ids, _, _) = CaptionBuilder.Place(project, [new CaptionCue(start, end, lines, 0, 0)], empty with { GroupId = group.Id }, measurer);
            return (TextLayer)project.FindLayer(ids[0])!;
        }

        var copy = (TextLayer)ProjectJson.LayerFromNode(ProjectJson.LayerToNode(like));
        var before = Size(like, measurer);
        copy.Id = Ids.Unique($"{group.Id}_{cues.Count + 1}", id => project.FindLayer(id) is not null);
        copy.Text = string.Join("\n", lines);
        copy.Name = Label(copy.Text);
        copy.Visible = true;
        foreach (var (_, value) in AnimatableProperties.Numeric(copy))
        {
            KeyframeWindow.Shift(value, start - like.Start);
        }

        copy.Start = start;
        copy.End = end;
        Reanchor(copy, before, Size(copy, measurer));
        project.Layers.Insert(project.Layers.IndexOf(like) + 1, copy);
        return copy;
    }

    // A caption's new text, its anchor kept at the same place in the text box, so a caption that gets
    // a second line still stands on the same bottom edge. Lines are trimmed and empty ones left out;
    // a name that was the old text becomes the new one.
    public static TextLayer SetText(Project project, string layerId, string text, ITextMeasurer? measurer)
    {
        var layer = ProjectEditor.GetLayer(project, layerId) as TextLayer ?? throw new ZeyoCutException($"'{layerId}' isn't a text layer");
        var lines = Lines(text);
        if (lines.Count == 0)
        {
            throw new ZeyoCutException("a caption needs some text");
        }

        var before = Size(layer, measurer);
        if (layer.Name == Label(layer.Text))
        {
            layer.Name = Label(string.Join("\n", lines));
        }

        layer.Text = string.Join("\n", lines);
        Reanchor(layer, before, Size(layer, measurer));
        return layer;
    }

    // Restyles the captions of tracks in one go: the font, the size (the box and the outline grow with
    // it), what is behind the letters, the width lines wrap at, and the place, inside the title-safe
    // margin. Anchors keep their place in the text box, or go to its top, middle or bottom with a place.
    public static int Restyle(Project project, IReadOnlyList<string> groupIds, CaptionRestyle change, ITextMeasurer? measurer)
    {
        if (change.FontFamily is { } family && string.IsNullOrWhiteSpace(family))
        {
            throw new ZeyoCutException("give the font's name, such as Segoe UI");
        }

        if (change.FontSize is { } size && (!double.IsFinite(size) || size <= 0))
        {
            throw new ZeyoCutException($"the size must be more than 0 px (got {Num(size)})");
        }

        if (change.MaxWidth is { } width && (!double.IsFinite(width) || width <= 0))
        {
            throw new ZeyoCutException($"the width must be more than 0 px (got {Num(width)})");
        }

        var cues = groupIds.SelectMany(id => Cues(project, Group(project, id).Id)).ToList();
        if (cues.Count == 0)
        {
            throw new ZeyoCutException("there are no captions to restyle");
        }

        var safe = ProjectChecker.SafeArea(project);
        foreach (var cue in cues)
        {
            var before = Size(cue, measurer);
            if (change.FontFamily is { } font)
            {
                cue.FontFamily = font.Trim();
                cue.FontAsset = null;
            }

            if (change.FontSize is { } fontSize)
            {
                var ratio = fontSize / cue.FontSize;
                cue.FontSize = Math.Round(fontSize, 1);
                cue.StrokeWidth = Math.Round(cue.StrokeWidth * ratio, 1);
                if (cue.Box is { } box)
                {
                    // A new box: captions placed together share theirs until the project is saved.
                    cue.Box = new TextBackground { Color = box.Color, Padding = Math.Round(box.Padding * ratio, 1), CornerRadius = Math.Round(box.CornerRadius * ratio, 1) };
                }
            }

            switch (change.Look)
            {
                case CaptionLook.Box:
                    cue.Box ??= new TextBackground { Color = BoxColor, Padding = Math.Round(cue.FontSize * BoxPadding), CornerRadius = Math.Round(cue.FontSize * BoxCorners) };
                    cue.StrokeColor = null;
                    cue.StrokeWidth = 0;
                    break;
                case CaptionLook.Outline:
                    cue.Box = null;
                    if (cue.StrokeColor is null || cue.StrokeWidth <= 0)
                    {
                        cue.StrokeColor ??= "#000000";
                        cue.StrokeWidth = Math.Round(cue.FontSize * OutlineWidth, 1);
                    }

                    break;
                case CaptionLook.None:
                    cue.Box = null;
                    cue.StrokeColor = null;
                    cue.StrokeWidth = 0;
                    break;
            }

            if (change.MaxWidth is { } maxWidth)
            {
                cue.MaxWidth = Math.Round(maxWidth, 1);
            }

            var after = Size(cue, measurer);
            Reanchor(cue, before, after);
            if (change.Place is { } place)
            {
                if (cue.Y.Keyframes.Count > 0)
                {
                    throw new ZeyoCutException($"caption '{cue.Id}' moves up or down (its Y has keyframes), so it can't be put at the {EnumNames.ToName(place)}; remove its Y keyframes first");
                }

                var edge = cue.Box?.Padding ?? cue.StrokeWidth;
                (cue.AnchorY, cue.Y.Value) = place switch
                {
                    CaptionPlace.Top => (0, safe.Top + edge),
                    CaptionPlace.Middle => (after.Height / 2, project.Height / 2.0),
                    _ => (after.Height, safe.Bottom - edge),
                };
                cue.AnchorY = Math.Round(cue.AnchorY, 3);
                cue.Y.Value = Math.Round(cue.Y.Value, 1);
            }
        }

        return cues.Count;
    }

    // The same caption in several languages given a new start, a new end or both, as trim_layer
    // trims each; those already there stay as they are. Returns the trims' summaries.
    public static List<string> Retime(Project project, IReadOnlyList<string> layerIds, double? start, double? end)
    {
        if (start is null && end is null)
        {
            throw new ZeyoCutException("give a new start, a new end or both");
        }

        var summaries = new List<string>();
        foreach (var id in layerIds)
        {
            var layer = ProjectEditor.GetLayer(project, id) as TextLayer ?? throw new ZeyoCutException($"'{id}' isn't a text layer");
            var newStart = start is { } s && Math.Abs(Math.Round(s, 3) - layer.Start) > 1e-9 ? Math.Round(s, 3) : (double?)null;
            var newEnd = end is { } e && Math.Abs(Math.Round(e, 3) - TimelineEditor.ContentEnd(project, layer)) > 1e-9 ? Math.Round(e, 3) : (double?)null;
            if (newStart is not null || newEnd is not null)
            {
                summaries.Add(TimelineEditor.TrimLayer(project, id, newStart, newEnd).Summary);
            }
        }

        return summaries.Count > 0
            ? summaries
            : throw new ZeyoCutException($"the caption{(layerIds.Count == 1 ? " runs" : "s run")} {(start is { } at ? $"from {Num(at)}s" : $"to {Num(end!.Value)}s")} already");
    }

    // What is behind a caption's letters.
    public static CaptionLook LookOf(TextLayer cue) =>
        cue.Box is not null ? CaptionLook.Box : cue.StrokeColor is not null && cue.StrokeWidth > 0 ? CaptionLook.Outline : CaptionLook.None;

    // Where a caption sits: in the top or the bottom third of the canvas, or in between.
    public static CaptionPlace PlaceOf(Project project, TextLayer cue)
    {
        var y = Interpolator.Evaluate(cue.Y, cue.Start);
        return y < project.Height / 3.0 ? CaptionPlace.Top : y > project.Height * 2 / 3.0 ? CaptionPlace.Bottom : CaptionPlace.Middle;
    }

    public static string Describe(CaptionRestyle change)
    {
        var parts = new List<string>();
        if (change.FontFamily is { } font)
        {
            parts.Add($"font {font.Trim()}");
        }

        if (change.FontSize is { } size)
        {
            parts.Add($"size {Num(Math.Round(size, 1))}px");
        }

        if (change.Look is { } look)
        {
            parts.Add(look switch { CaptionLook.Box => "a box behind them", CaptionLook.Outline => "an outline", _ => "nothing behind them" });
        }

        if (change.MaxWidth is { } width)
        {
            parts.Add($"lines up to {Num(Math.Round(width, 1))}px");
        }

        if (change.Place is { } place)
        {
            parts.Add($"at the {EnumNames.ToName(place)}");
        }

        return string.Join(", ", parts);
    }

    public static string Describe(CaptionTrack track, string font, int listed = 12)
    {
        var lines = new List<string>
        {
            $"{(track.Created ? "added" : "replaced")} the {track.Language} caption track '{track.GroupId}': {track.Cues.Count} caption(s){(track.Replaced > 0 ? $" (removed {track.Replaced} old caption layer(s))" : "")}, font {font}",
        };
        foreach (var cue in track.Cues.Take(listed))
        {
            lines.Add($"- {Num(cue.Start)}–{Num(cue.End)}s \"{cue.Text.Replace("\n", " / ")}\"");
        }

        if (track.Cues.Count > listed)
        {
            lines.Add($"- ... and {track.Cues.Count - listed} more");
        }

        lines.Add($"'{track.GroupId}' is only in the {track.Language} version: give language {track.Language} to render_frame or start_export to see it; export_srt with groupId {track.GroupId} writes it as subtitles");
        return string.Join("\n", lines);
    }

    private static double Time(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60 + double.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    private static List<string> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static string Label(string text)
    {
        var label = text.Replace('\n', ' ');
        return label.Length > 40 ? label[..40] : label;
    }

    // The text box: measured, or estimated the way CaptionBuilder.Place estimates it without a measurer.
    private static (double Width, double Height) Size(TextLayer text, ITextMeasurer? measurer) =>
        measurer?.Measure(text) ?? (text.MaxWidth ?? 0, Lines(text.Text).Count * text.FontSize * 1.2 * text.LineHeight);

    // The anchor at the same place in the text box at its new size; as it was when the size is.
    private static void Reanchor(TextLayer text, (double Width, double Height) before, (double Width, double Height) after)
    {
        if (before.Width > 0 && after.Width != before.Width)
        {
            text.AnchorX = Math.Round(text.AnchorX * after.Width / before.Width, 3);
        }

        if (before.Height > 0 && after.Height != before.Height)
        {
            text.AnchorY = Math.Round(text.AnchorY * after.Height / before.Height, 3);
        }
    }

    private static string Short(List<string> lines)
    {
        var text = string.Join(" / ", lines);
        return text.Length > 30 ? text[..30] + "…" : text;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
