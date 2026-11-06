// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// One marker for add_markers. An existing id changes that marker (its kind stays unless given);
// a new or missing id adds one, a missing id made from the name.
public sealed record MarkerEntry(string? Id, double Time, string? Name, string? Kind);

public sealed record MarkerChange(IReadOnlyList<Marker> Added, IReadOnlyList<Marker> Changed, IReadOnlyList<string> Warnings);

// The project's markers, kept in time order.
public static class MarkerEditor
{
    private const double Tolerance = 1e-6;

    public static MarkerChange AddMarkers(Project project, IReadOnlyList<MarkerEntry> entries)
    {
        if (entries.Count == 0)
        {
            throw new ZeyoCutException("give at least one marker: [{time, name, kind}]");
        }

        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in entries.Select(e => e.Id?.Trim()).Where(id => !string.IsNullOrEmpty(id)))
        {
            Ids.EnsureValid(id!, "marker");
            if (!named.Add(id!))
            {
                throw new ZeyoCutException($"marker '{id}' is given twice");
            }
        }

        var checkedEntries = new List<(string? Id, double Time, string Name, MarkerKind? Kind)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var name = entry.Name?.Trim() ?? "";
            var what = string.IsNullOrEmpty(entry.Id) ? $"marker {i + 1}" : $"marker '{entry.Id.Trim()}'";
            if (name.Length == 0)
            {
                throw new ZeyoCutException($"{what} has no name");
            }

            if (name.Any(char.IsControl))
            {
                throw new ZeyoCutException($"the name of {what} must be one line of text");
            }

            if (!double.IsFinite(entry.Time) || entry.Time < 0 || entry.Time > project.Duration + Tolerance)
            {
                throw new ZeyoCutException($"{what} (\"{name}\") is at {Num(entry.Time)}s; markers go from 0 to the project end ({Num(project.Duration)}s)");
            }

            MarkerKind? kind = entry.Kind is null ? null : EnumNames.Parse<MarkerKind>(entry.Kind, "marker kind");
            checkedEntries.Add((entry.Id?.Trim(), KeyframeWindow.Round(Math.Min(entry.Time, project.Duration)), name, kind));
        }

        var added = new List<Marker>();
        var changed = new List<Marker>();
        foreach (var (given, time, name, kind) in checkedEntries)
        {
            var id = given;
            if (!string.IsNullOrEmpty(id) && project.FindMarker(id) is { } existing)
            {
                existing.Time = time;
                existing.Name = name;
                existing.Kind = kind ?? existing.Kind;
                changed.Add(existing);
                continue;
            }

            if (string.IsNullOrEmpty(id))
            {
                id = Ids.Unique(Ids.Slug(name, EnumNames.ToName(kind ?? MarkerKind.Chapter)), c => named.Contains(c) || project.FindMarker(c) is not null);
            }

            var marker = new Marker { Id = id, Time = time, Name = name, Kind = kind ?? MarkerKind.Chapter };
            project.Markers.Add(marker);
            added.Add(marker);
        }

        Sort(project);
        return new MarkerChange(added, changed, SameTime(project, added.Concat(changed)));
    }

    // The markers by id, or every marker (["all"]) of 'kind' when given; no ids with a kind is every marker of that kind.
    public static List<Marker> RemoveMarkers(Project project, IReadOnlyList<string>? ids, string? kind)
    {
        var none = ids is null || ids.Count == 0;
        if (none && kind is null)
        {
            throw new ZeyoCutException("give markerIds (or [\"all\"]) or a kind");
        }

        MarkerKind? only = kind is null ? null : EnumNames.Parse<MarkerKind>(kind, "marker kind");
        var all = none || (ids!.Count == 1 && string.Equals(ids[0]?.Trim(), "all", StringComparison.OrdinalIgnoreCase));
        var removed = new List<Marker>();
        if (all)
        {
            removed.AddRange(project.Markers.Where(m => only is null || m.Kind == only));
            if (removed.Count == 0)
            {
                throw new ZeyoCutException(project.Markers.Count == 0
                    ? "the project has no markers; nothing was changed"
                    : $"the project has no {EnumNames.ToName(only!.Value)} markers; nothing was changed");
            }
        }
        else
        {
            if (only is not null)
            {
                throw new ZeyoCutException("give markerIds or a kind, not both (a kind goes with markerIds [\"all\"] or none)");
            }

            foreach (var raw in ids!)
            {
                var id = raw?.Trim() ?? "";
                var marker = project.FindMarker(id) ?? throw new ZeyoCutException($"marker '{id}' not found; markers: {Ids.List(project.Markers.Select(m => m.Id))}");
                if (!removed.Contains(marker))
                {
                    removed.Add(marker);
                }
            }
        }

        project.Markers.RemoveAll(removed.Contains);
        return removed;
    }

    // A ripple took [start, end) out: later markers move left with the time after it, markers inside move to start.
    public static void RemoveTime(Project project, double start, double end)
    {
        var length = end - start;
        if (length <= Tolerance)
        {
            return;
        }

        foreach (var marker in project.Markers)
        {
            if (marker.Time >= end - Tolerance)
            {
                marker.Time = KeyframeWindow.Round(marker.Time - length);
            }
            else if (marker.Time > start)
            {
                marker.Time = KeyframeWindow.Round(start);
            }
        }

        Sort(project);
    }

    // A ripple put 'length' seconds in at 'at': markers at or after it move right.
    public static void InsertTime(Project project, double at, double length)
    {
        if (length <= Tolerance)
        {
            return;
        }

        foreach (var marker in project.Markers.Where(m => m.Time >= at - Tolerance))
        {
            marker.Time = KeyframeWindow.Round(marker.Time + length);
        }
    }

    public static string Describe(Marker marker) => $"{marker.Id} {EnumNames.ToName(marker.Kind)} at {Num(marker.Time)}s \"{marker.Name}\"";

    // Every marker, one per line.
    public static string List(Project project) => project.Markers.Count == 0
        ? "no markers"
        : $"markers ({project.Markers.Count}):\n" + string.Join("\n", project.Markers.Select(m => "- " + Describe(m)));

    private static void Sort(Project project) => project.Markers = project.Markers.OrderBy(m => m.Time).ToList();

    private static List<string> SameTime(Project project, IEnumerable<Marker> changed)
    {
        var ids = changed.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return project.Markers
            .GroupBy(m => (m.Kind, Time: Math.Round(m.Time, 3)))
            .Where(g => g.Count() > 1 && g.Any(m => ids.Contains(m.Id)))
            .Select(g => $"{EnumNames.ToName(g.Key.Kind)} markers {string.Join(", ", g.Select(m => $"'{m.Id}'"))} are at the same time ({Num(g.Key.Time)}s)")
            .ToList();
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
