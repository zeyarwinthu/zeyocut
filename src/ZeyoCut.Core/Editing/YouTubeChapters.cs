// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record ChapterList(string Text, IReadOnlyList<Marker> Chapters, IReadOnlyList<string> Warnings);

// A YouTube chapter list from the project's chapter markers, as YouTube reads it from a video's
// description: a "m:ss Name" line per chapter ("h:mm:ss" from an hour on), in whole seconds rounded
// down. YouTube shows the chapters only when the first is at 0:00, there are at least 3 and each
// lasts at least 10 s; the warnings name each rule the markers break.
public static class YouTubeChapters
{
    public const int MinCount = 3;
    public const double MinSeconds = 10;

    public static ChapterList Make(Project project)
    {
        var chapters = project.Markers.Where(m => m.Kind == MarkerKind.Chapter).OrderBy(m => m.Time).ToList();
        if (chapters.Count == 0)
        {
            throw new ZeyoCutException(project.Markers.Count == 0
                ? "the project has no markers; add chapters with add_markers"
                : "the project has scene markers but no chapters; add chapters with add_markers (kind chapter is the default)");
        }

        var starts = chapters.Select(c => Math.Floor(c.Time + 1e-6)).ToList();
        var warnings = new List<string>();
        if (starts[0] > 0)
        {
            warnings.Add($"YouTube needs the first chapter at 0:00, but the first one, {Name(chapters[0])}, is at {Stamp(starts[0])}; add a chapter at 0 or move this one there with add_markers");
        }

        if (chapters.Count < MinCount)
        {
            warnings.Add($"YouTube needs at least {MinCount} chapters; there {(chapters.Count == 1 ? "is 1" : $"are {chapters.Count}")}");
        }

        for (var i = 0; i < chapters.Count; i++)
        {
            var length = (i + 1 < chapters.Count ? starts[i + 1] : project.Duration) - starts[i];
            if (length < MinSeconds)
            {
                warnings.Add($"{Name(chapters[i])} lasts {length.ToString("0.###", CultureInfo.InvariantCulture)}s from {Stamp(starts[i])}; YouTube needs at least {MinSeconds} s per chapter");
            }
        }

        var text = string.Concat(chapters.Select((c, i) => $"{Stamp(starts[i])} {c.Name}\n"));
        return new ChapterList(text, chapters, warnings);
    }

    public static string Stamp(double seconds)
    {
        var total = (long)Math.Max(0, seconds);
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:D2}:{total % 60:D2}"
            : $"{total / 60}:{total % 60:D2}";
    }

    private static string Name(Marker marker) => $"'{marker.Id}' (\"{marker.Name}\")";
}
