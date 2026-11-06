// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// One caption as given to add_caption_track or read from an SRT file, seconds on the timeline.
public sealed record CueEntry(double Start, double End, string Text);

// What add_caption_track did: the language's caption group, its captions, and the problems with the
// cues it was given (left out, shortened or overlapping).
public sealed record CaptionTrack(string GroupId, string Language, IReadOnlyList<CaptionCue> Cues, IReadOnlyList<string> LayerIds, int Replaced, bool Created, IReadOnlyList<string> Problems);

// A caption track per language: its captions under a null layer captions_<language> tagged with the
// language, so each language's version shows its own captions and none of the others'.
public static partial class CaptionTracks
{
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

    private static string Short(List<string> lines)
    {
        var text = string.Join(" / ", lines);
        return text.Length > 30 ? text[..30] + "…" : text;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
