// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public enum ItemChange
{
    Added,
    Changed,
    Removed,
}

// One item that differs between two versions of a project. Kind is layer, asset, track or marker
// (with its id and name), settings (Name lists the fields), camera, order (of the layers) or
// project (something else in project.json).
public sealed record ProjectItemChange(string Kind, string Id, string? Name, ItemChange Change)
{
    private const int NameLength = 40;

    public string Describe()
    {
        var verb = Change switch
        {
            ItemChange.Added => "added",
            ItemChange.Removed => "removed",
            _ => "changed",
        };
        return Kind switch
        {
            "settings" => $"{verb} the project settings ({Name})",
            "camera" => $"{verb} the camera",
            "order" => "changed the order of the layers",
            "project" => "changed project.json",
            _ => $"{verb} {Kind} '{Id}'" + (string.IsNullOrWhiteSpace(Name) || Name == Id ? "" : $" ({Shorten(Name)})"),
        };
    }

    private static string Shorten(string text) => text.Length <= NameLength ? text : text[..NameLength] + "…";
}

// What another program changed in project.json: the revision it is now, the edits it saved (from
// the backups it kept, oldest first; none for a hand edit) and the items that differ. Problem is
// set when the file it left can't be read; the open project then stays as it was, at Revision.
public sealed record OutsideChange(long Revision, IReadOnlyList<string> Edits, IReadOnlyList<ProjectItemChange> Items, string? Problem)
{
    // Several changes in a row as one: an item added and then removed drops out.
    public static OutsideChange Merge(IReadOnlyList<OutsideChange> changes)
    {
        var items = new List<ProjectItemChange>();
        foreach (var item in changes.SelectMany(c => c.Items))
        {
            var at = items.FindIndex(i => i.Kind == item.Kind && i.Id == item.Id);
            if (at < 0)
            {
                items.Add(item);
                continue;
            }

            var first = items[at];
            ItemChange? merged = (first.Change, item.Change) switch
            {
                (ItemChange.Added, ItemChange.Removed) => null,
                (ItemChange.Added, _) => ItemChange.Added,
                (_, ItemChange.Removed) => ItemChange.Removed,
                _ => ItemChange.Changed,
            };
            if (merged is { } change)
            {
                var name = item.Kind == "settings"
                    ? string.Join(", ", $"{first.Name}, {item.Name}".Split(", ").Distinct())
                    : item.Name ?? first.Name;
                items[at] = item with { Change = change, Name = name };
            }
            else
            {
                items.RemoveAt(at);
            }
        }

        var last = changes[^1];
        return new OutsideChange(last.Revision, changes.SelectMany(c => c.Edits).ToList(), items, last.Problem);
    }
}

public static class ProjectDiff
{
    private static readonly JsonSerializerOptions Options = ProjectJson.Options;

    // The items that differ, in the order: settings, camera, assets, layers, the layers' order,
    // tracks, markers. revision and formatVersion don't count.
    public static List<ProjectItemChange> Compare(Project before, Project after)
    {
        var changes = new List<ProjectItemChange>();
        var settings = new List<string>();
        if (before.Name != after.Name)
        {
            settings.Add("name");
        }

        if (before.Width != after.Width)
        {
            settings.Add("width");
        }

        if (before.Height != after.Height)
        {
            settings.Add("height");
        }

        if (before.Fps != after.Fps)
        {
            settings.Add("fps");
        }

        if (before.Duration != after.Duration)
        {
            settings.Add("duration");
        }

        if (before.BackgroundColor != after.BackgroundColor)
        {
            settings.Add("backgroundColor");
        }

        if (Json(before.Audio) != Json(after.Audio))
        {
            settings.Add("audio");
        }

        if (settings.Count > 0)
        {
            changes.Add(new ProjectItemChange("settings", "", string.Join(", ", settings), ItemChange.Changed));
        }

        if (Json(before.Camera) != Json(after.Camera))
        {
            var change = before.Camera is null ? ItemChange.Added : after.Camera is null ? ItemChange.Removed : ItemChange.Changed;
            changes.Add(new ProjectItemChange("camera", "", null, change));
        }

        Items(changes, "asset", before.Assets, after.Assets, a => a.Id, _ => null, Json);
        Items(changes, "layer", before.Layers, after.Layers, l => l.Id, l => l.Name, ProjectJson.LayerToJson);
        var afterIds = after.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var beforeIds = before.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (!before.Layers.Select(l => l.Id).Where(afterIds.Contains).SequenceEqual(after.Layers.Select(l => l.Id).Where(beforeIds.Contains)))
        {
            changes.Add(new ProjectItemChange("order", "", null, ItemChange.Changed));
        }

        Items(changes, "track", before.Tracks, after.Tracks, t => t.Id, _ => null, Json);
        Items(changes, "marker", before.Markers, after.Markers, m => m.Id, m => m.Name, Json);

        // A field none of the above covers.
        if (changes.Count == 0 && Whole(before) != Whole(after))
        {
            changes.Add(new ProjectItemChange("project", "", null, ItemChange.Changed));
        }

        return changes;
    }

    private static void Items<T>(List<ProjectItemChange> changes, string kind, List<T> before, List<T> after, Func<T, string> id, Func<T, string?> name, Func<T, string> json)
    {
        var old = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in before)
        {
            old.TryAdd(id(item), item);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in after)
        {
            var key = id(item);
            if (!seen.Add(key))
            {
                continue;
            }

            if (!old.TryGetValue(key, out var was))
            {
                changes.Add(new ProjectItemChange(kind, key, name(item), ItemChange.Added));
            }
            else if (json(was) != json(item))
            {
                changes.Add(new ProjectItemChange(kind, key, name(item), ItemChange.Changed));
            }
        }

        foreach (var (key, item) in old)
        {
            if (!seen.Contains(key))
            {
                changes.Add(new ProjectItemChange(kind, key, name(item), ItemChange.Removed));
            }
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static string Whole(Project project)
    {
        var node = JsonSerializer.SerializeToNode(project, Options)!.AsObject();
        node.Remove("revision");
        node.Remove("formatVersion");
        return node.ToJsonString();
    }
}
