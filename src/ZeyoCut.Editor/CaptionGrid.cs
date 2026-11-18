// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// A caption track as the Captions view shows it: its chip ("EN · source", "JA 3/3"), its column's
// heading in the language's own name, and the font its cells show the text in. Pickable: without an
// English track, its chip makes it the source.
public sealed record CaptionColumn(string GroupId, string? Language, string Code, string Heading, bool Source, int Filled, int Rows, string Font, bool Pickable)
{
    public string Chip => Source ? $"{Code} · source" : Code;

    public string Count => Source ? "" : $"{Filled}/{Rows}";

    public bool Full => Filled == Rows;

    public string HeadingCaps => Heading.ToUpper(CultureInfo.InvariantCulture);

    public string Tip => Source
        ? "The source: each row is one of its captions, with the other languages' captions that go with it"
        : $"{Code} captions in {Filled} of {Rows} rows{(Pickable ? "; click to make it the source, which the rows follow" : "")}";
}

// A track's caption in a row, or the place for one: typing into an empty cell adds a caption there.
public sealed class CaptionCell
{
    public CaptionCell(CaptionRow row, CaptionColumn column, TextLayer? cue)
    {
        Row = row;
        Column = column;
        LayerId = cue?.Id;
        Text = cue?.Text ?? "";
    }

    public CaptionRow Row { get; }

    public CaptionColumn Column { get; }

    public string? LayerId { get; }

    public string Text { get; }

    public string Font => Column.Font;

    public string Hint => LayerId is null ? $"No {Column.Code} caption here: type one to add it" : $"{LayerId}: Enter saves, Shift+Enter starts a new line, and an empty cell removes the caption";
}

// A row of the grid: a caption of the source track with the other languages' captions that go with
// it, or captions that go with none of the source's. A draft row, from Add caption at playhead, is
// saved once something is typed into it.
public sealed class CaptionRow : INotifyPropertyChanged
{
    private bool _current;

    public CaptionRow(string key, double start, double end, bool draft)
    {
        Key = key;
        Start = start;
        End = end;
        Draft = draft;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // The id of the caption the row is made from, or "draft".
    public string Key { get; }

    // From 1, counting the saved rows; 0 for a draft.
    public int Number { get; set; }

    public double Start { get; }

    public double End { get; }

    public bool Draft { get; }

    public string StartText => CaptionTime.Text(Start);

    public string EndText => CaptionTime.Text(End);

    public List<CaptionCell> Cells { get; } = [];

    public IReadOnlyList<string> LayerIds => Cells.Select(c => c.LayerId).OfType<string>().ToList();

    // The playhead is in it.
    public bool Current
    {
        get => _current;
        set
        {
            if (_current != value)
            {
                _current = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
            }
        }
    }
}

public sealed record CaptionTable(IReadOnlyList<CaptionColumn> Columns, IReadOnlyList<CaptionRow> Rows);

// A language the preview can show.
public sealed record PreviewChoice(string Language, string Code, bool Chosen)
{
    public string Tip => $"Show the {Name(Language)} version";

    // EN · English, JA · 日本語.
    public static string Name(string language) => $"{language.ToUpperInvariant()} · {CaptionGrid.Heading(language)}";
}

// The Captions view's grid. Rows follow a source track: English when there is one, else the track
// picked as the source, else the first. Another language's caption goes in the row of the source
// caption it overlaps most; one that overlaps none gets a row of its own, shared with the other
// languages' captions it overlaps.
public static class CaptionGrid
{
    public const string DraftKey = "draft";

    // The caption groups at the top of the project, the captions tracks are made of.
    public static List<ImageLayer> Tracks(Project project) =>
        project.Layers.Where(l => l.ParentId is null && TimelineLayout.IsCaptionGroup(l)).Cast<ImageLayer>().ToList();

    public static CaptionTable Build(Project project, string? source, (double Start, double End)? draft = null)
    {
        var tracks = Tracks(project);
        if (tracks.Count == 0)
        {
            return new CaptionTable([], []);
        }

        var english = tracks.FirstOrDefault(t => IsEnglish(TimelineLayout.CaptionLanguage(t)));
        var first = english ?? tracks.FirstOrDefault(t => t.Id == source) ?? tracks[0];
        var ordered = tracks.Where(t => t != first).OrderBy(t => Code(t), StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal).Prepend(first).ToList();
        var cues = ordered.ToDictionary(t => t.Id, t => CaptionTracks.Cues(project, t.Id), StringComparer.Ordinal);
        double End(TextLayer cue) => Math.Min(cue.End ?? project.Duration, project.Duration);
        double Overlap(TextLayer a, TextLayer b) => Math.Min(End(a), End(b)) - Math.Max(a.Start, b.Start);

        var rows = cues[first.Id].Select(c => (Anchor: c, Cells: new Dictionary<string, TextLayer>(StringComparer.Ordinal) { [first.Id] = c }, Own: false)).ToList();
        var orphans = new List<(string Track, TextLayer Cue)>();
        foreach (var track in ordered.Skip(1))
        {
            var pairs = cues[track.Id]
                .SelectMany(c => rows.Where(r => !r.Own).Select(r => (Cue: c, Row: r, Overlap: Overlap(c, r.Anchor))))
                .Where(p => p.Overlap > 1e-9)
                .OrderByDescending(p => p.Overlap)
                .ThenBy(p => p.Row.Anchor.Start)
                .ToList();
            var placed = new HashSet<TextLayer>();
            foreach (var (cue, row, _) in pairs)
            {
                if (!placed.Contains(cue) && !row.Cells.ContainsKey(track.Id))
                {
                    row.Cells[track.Id] = cue;
                    placed.Add(cue);
                }
            }

            orphans.AddRange(cues[track.Id].Where(c => !placed.Contains(c)).Select(c => (track.Id, c)));
        }

        foreach (var (track, cue) in orphans.OrderBy(o => o.Cue.Start).ThenBy(o => End(o.Cue)))
        {
            var shared = rows.Where(r => r.Own && !r.Cells.ContainsKey(track) && Overlap(cue, r.Anchor) > 1e-9).OrderByDescending(r => Overlap(cue, r.Anchor)).FirstOrDefault();
            if (shared.Anchor is not null)
            {
                shared.Cells[track] = cue;
            }
            else
            {
                rows.Add((cue, new Dictionary<string, TextLayer>(StringComparer.Ordinal) { [track] = cue }, true));
            }
        }

        var made = rows.Select(r => (Row: new CaptionRow(r.Anchor.Id, r.Anchor.Start, End(r.Anchor), false), r.Cells)).ToList();
        if (draft is { } at)
        {
            made.Add((new CaptionRow(DraftKey, at.Start, at.End, true), new Dictionary<string, TextLayer>(StringComparer.Ordinal)));
        }

        made = made.OrderBy(r => r.Row.Start).ThenBy(r => r.Row.End).ToList();
        var columns = ordered.Select(t => new CaptionColumn(
            t.Id,
            TimelineLayout.CaptionLanguage(t),
            Code(t),
            Heading(TimelineLayout.CaptionLanguage(t)),
            t == first,
            made.Count(r => !r.Row.Draft && r.Cells.ContainsKey(t.Id)),
            made.Count(r => !r.Row.Draft),
            cues[t.Id].FirstOrDefault() is { } cue ? cue.FontFamily : "Segoe UI",
            english is null && t != first)).ToList();
        var number = 0;
        foreach (var (row, cells) in made)
        {
            row.Number = row.Draft ? 0 : ++number;
            row.Cells.AddRange(columns.Select(c => new CaptionCell(row, c, cells.GetValueOrDefault(c.GroupId))));
        }

        return new CaptionTable(columns, made.Select(r => r.Row).ToList());
    }

    // Where Add caption at playhead puts its draft: from the playhead, or the end of the captions it
    // is in, for two seconds, or up to the next caption or the project end when that comes first.
    // Null when the captions run on to the end from there.
    public static (double Start, double End)? Draft(Project project, CaptionTable table, double time)
    {
        var start = Math.Clamp(time, 0, project.Duration);
        while (table.Rows.FirstOrDefault(r => !r.Draft && r.Start <= start + 1e-9 && start < r.End - 1e-9) is { } inside)
        {
            start = inside.End;
        }

        start = Math.Round(start, 3);
        if (start >= project.Duration - 1e-9)
        {
            return null;
        }

        var next = table.Rows.Where(r => !r.Draft && r.Start > start + 1e-9).Select(r => r.Start).DefaultIfEmpty(project.Duration).Min();
        return (start, Math.Round(Math.Min(Math.Min(start + 2, next), project.Duration), 3));
    }

    // The language the preview shows: the one chosen, when the project has layers in it; else
    // English, else the first. Null when no layer has a language.
    public static string? PreviewLanguage(Project project, string? chosen)
    {
        var used = Languages.Used(project);
        return used.Count == 0 ? null
            : chosen is not null && used.Contains(chosen) ? chosen
            : used.FirstOrDefault(IsEnglish) ?? used[0];
    }

    private static bool IsEnglish(string? language) => language is not null && (language == "en" || language.StartsWith("en-", StringComparison.Ordinal));

    private static string Code(Layer group) => TimelineLayout.CaptionLanguage(group)?.ToUpperInvariant() ?? "CC";

    // The language's name in itself: English, 日本語, မြန်မာ.
    public static string Heading(string? language)
    {
        if (language is null)
        {
            return "Captions";
        }

        try
        {
            var name = CultureInfo.GetCultureInfo(language).NativeName;
            return name.Length == 0 || name.StartsWith("Unknown", StringComparison.Ordinal) ? language.ToUpperInvariant() : char.ToUpper(name[0], CultureInfo.GetCultureInfo(language)) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return language.ToUpperInvariant();
        }
    }
}

// Times in the grid: 00:03.40, 1:02:03.40 past an hour. Typed, they take hours, minutes and seconds
// with a point or a comma, or seconds alone.
public static partial class CaptionTime
{
    public static string Text(double seconds)
    {
        var hundredths = (long)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        var (hours, rest) = Math.DivRem(hundredths, 360000);
        var (minutes, rest2) = Math.DivRem(rest, 6000);
        var text = $"{minutes:00}:{rest2 / 100:00}.{rest2 % 100:00}";
        return hours > 0 ? $"{hours}:{text}" : text;
    }

    public static double Parse(string text, string label)
    {
        var match = Pattern().Match(text);
        if (!match.Success)
        {
            throw new ZeyoCut.Core.ZeyoCutException($"{label} must be a time like 00:03.40 or 3.4 (got '{text.Trim()}')");
        }

        var seconds = double.Parse(match.Groups["s"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        return Math.Round(hours * 3600 + minutes * 60 + seconds, 3);
    }

    [GeneratedRegex(@"^\s*(?:(?:(?<h>\d+):)?(?<m>\d+):)?(?<s>\d+(?:[.,]\d*)?)\s*s?\s*$")]
    private static partial Regex Pattern();
}
