// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
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

    // Whether this change touched the item of a delta (a settings delta: that field).
    public bool Touches(ItemDelta delta) => Items.Any(i => delta.Kind == "settings"
        ? i.Kind == "settings" && (i.Name ?? "").Split(", ").Contains(delta.Id)
        : i.Kind == delta.Kind && i.Id == delta.Id);
}

// One item an edit changed, as JSON before and after it (null: not there). Kind is settings (Id is
// the field), camera, asset, layer, track, marker or order (the relative order of the layers that
// were there both before and after, as a JSON array of ids). PreviousBefore and PreviousAfter are
// the ids of the items just before it in its list, to put it back in its place.
public sealed record ItemDelta(string Kind, string Id, string? Name, string? Before, string? After, string? PreviousBefore, string? PreviousAfter)
{
    public ItemChange Change => Before is null ? ItemChange.Added : After is null ? ItemChange.Removed : ItemChange.Changed;

    public bool IsDuration => Kind == "settings" && Id == "duration";

    public string Describe() => Kind switch
    {
        "settings" => $"the project's {Id}",
        "camera" => "the camera",
        "order" => "the order of the layers",
        _ => $"{Kind} '{Id}'",
    };
}

public static class ProjectDiff
{
    private static readonly JsonSerializerOptions Options = ProjectJson.Options;

    // The items that differ, for a note: settings first (as one item), then the camera, assets,
    // layers, the layers' order, tracks and markers. revision and formatVersion don't count.
    public static List<ProjectItemChange> Compare(Project before, Project after)
    {
        var deltas = Deltas(before, after);
        var changes = new List<ProjectItemChange>();
        var settings = deltas.Where(d => d.Kind == "settings").Select(d => d.Id).ToList();
        if (settings.Count > 0)
        {
            changes.Add(new ProjectItemChange("settings", "", string.Join(", ", settings), ItemChange.Changed));
        }

        changes.AddRange(deltas.Where(d => d.Kind != "settings").Select(d => new ProjectItemChange(d.Kind, d.Id, d.Name, d.Change)));

        // A field none of the above covers.
        if (changes.Count == 0 && Whole(before) != Whole(after))
        {
            changes.Add(new ProjectItemChange("project", "", null, ItemChange.Changed));
        }

        return changes;
    }

    // Every item that differs, with its JSON before and after. Every field of Project is covered;
    // a new one needs a line here.
    public static List<ItemDelta> Deltas(Project before, Project after)
    {
        var deltas = new List<ItemDelta>();
        Setting(deltas, "name", before.Name, after.Name);
        Setting(deltas, "width", before.Width, after.Width);
        Setting(deltas, "height", before.Height, after.Height);
        Setting(deltas, "fps", before.Fps, after.Fps);
        Setting(deltas, "duration", before.Duration, after.Duration);
        Setting(deltas, "backgroundColor", before.BackgroundColor, after.BackgroundColor);
        Setting(deltas, "audio", before.Audio, after.Audio);
        var cameraBefore = before.Camera is null ? null : Json(before.Camera);
        var cameraAfter = after.Camera is null ? null : Json(after.Camera);
        if (cameraBefore != cameraAfter)
        {
            deltas.Add(new ItemDelta("camera", "", null, cameraBefore, cameraAfter, null, null));
        }

        Items(deltas, "asset", before.Assets, after.Assets, a => a.Id, _ => null, Json);
        Items(deltas, "layer", before.Layers, after.Layers, l => l.Id, l => l.Name, ProjectJson.LayerToJson);
        var afterIds = after.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var beforeIds = before.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var orderBefore = before.Layers.Select(l => l.Id).Where(afterIds.Contains).ToList();
        var orderAfter = after.Layers.Select(l => l.Id).Where(beforeIds.Contains).ToList();
        if (!orderBefore.SequenceEqual(orderAfter))
        {
            deltas.Add(new ItemDelta("order", "", null, Json(orderBefore), Json(orderAfter), null, null));
        }

        Items(deltas, "track", before.Tracks, after.Tracks, t => t.Id, _ => null, Json);
        Items(deltas, "marker", before.Markers, after.Markers, m => m.Id, m => m.Name, Json);
        return deltas;
    }

    // The deltas an edit made, for undo: each item's JSON as project.json holds it once read back
    // (normalized), so it compares equal to the item after another program saved it; an item that
    // then doesn't differ drops out. Only the changed items are read back, not both versions whole.
    public static List<ItemDelta> Changes(Project before, Project after) =>
        Deltas(before, after)
            .Select(d => d with { Before = Normalized(d, d.Before, before), After = Normalized(d, d.After, after) })
            .Where(d => d.Before != d.After)
            .ToList();

    // The deltas whose item isn't as the edit left it (undo, backwards) or found it (redo): another
    // program changed it since. The project's duration only counts when it is all the edit changed;
    // otherwise it moved because layers did (a clip that grew the project, a ripple).
    public static List<ItemDelta> Conflicts(Project project, IReadOnlyList<ItemDelta> deltas, bool backwards)
    {
        var durationIsSoft = deltas.Any(d => !d.IsDuration);
        var conflicts = new List<ItemDelta>();
        foreach (var delta in deltas)
        {
            if (durationIsSoft && delta.IsDuration)
            {
                continue;
            }

            var expected = backwards ? delta.After : delta.Before;
            var same = delta.Kind == "order"
                ? SameOrder(project, expected!)
                : Current(project, delta) == expected;
            if (!same)
            {
                conflicts.Add(delta);
            }
        }

        return conflicts;
    }

    // What another program did to a conflicting item: "removed layer 'x'", "changed the order of the layers".
    public static string DescribeConflict(Project project, ItemDelta delta, bool backwards)
    {
        var verb = delta.Kind is "settings" or "order" ? "changed"
            : Current(project, delta) is null ? "removed"
            : (backwards ? delta.After : delta.Before) is null ? "added"
            : "changed";
        return $"{verb} {delta.Describe()}";
    }

    // Puts every item back as it was before the edit (backwards) or as the edit left it. Check
    // Conflicts first. A soft duration is set only when it is still what the edit found or left
    // and no other layer runs past it; going forward, the project still grows to fit the edit's
    // layers, as the edit did.
    public static void Apply(Project project, IReadOnlyList<ItemDelta> deltas, bool backwards)
    {
        var durationIsSoft = deltas.Any(d => !d.IsDuration);
        ItemDelta? softDuration = null;
        foreach (var delta in deltas)
        {
            var target = backwards ? delta.Before : delta.After;
            var previous = backwards ? delta.PreviousBefore : delta.PreviousAfter;
            switch (delta.Kind)
            {
                case "settings":
                    if (durationIsSoft && delta.IsDuration)
                    {
                        softDuration = delta;
                        break;
                    }

                    SetSetting(project, delta.Id, target!);
                    break;
                case "camera":
                    project.Camera = target is null ? null : JsonSerializer.Deserialize<Camera>(target, Options);
                    break;
                case "asset":
                    Put(project.Assets, delta.Id, target, previous, a => a.Id, json => JsonSerializer.Deserialize<Asset>(json, Options)!);
                    break;
                case "layer":
                    Put(project.Layers, delta.Id, target, previous, l => l.Id, json => ProjectJson.LayerFromNode(JsonNode.Parse(json)!.AsObject()));
                    break;
                case "track":
                    Put(project.Tracks, delta.Id, target, previous, t => t.Id, json => JsonSerializer.Deserialize<Track>(json, Options)!);
                    break;
                case "marker":
                    Put(project.Markers, delta.Id, target, previous, m => m.Id, json => JsonSerializer.Deserialize<Marker>(json, Options)!);
                    break;
                case "order":
                    Reorder(project, JsonSerializer.Deserialize<List<string>>(target!, Options)!);
                    break;
            }
        }

        if (softDuration is { } duration)
        {
            SetSoftDuration(project, deltas, duration, backwards);
        }
    }

    private static void SetSoftDuration(Project project, IReadOnlyList<ItemDelta> deltas, ItemDelta duration, bool backwards)
    {
        var target = JsonSerializer.Deserialize<double>((backwards ? duration.Before : duration.After)!, Options);
        var edited = deltas.Where(d => d.Kind == "layer").Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var othersEnd = project.Layers.Where(l => !edited.Contains(l.Id)).Select(FixedEnd).OfType<double>().DefaultIfEmpty(0).Max();
        if (Current(project, duration) == (backwards ? duration.After : duration.Before) && othersEnd <= target + 1e-9)
        {
            project.Duration = target;
        }

        if (!backwards)
        {
            foreach (var layer in project.Layers.Where(l => edited.Contains(l.Id)))
            {
                ProjectEditor.ExtendToFit(project, FixedEnd(layer) ?? 0);
            }
        }
    }

    // Where a layer ends when that doesn't depend on the project's duration.
    private static double? FixedEnd(Layer layer) => layer is VideoLayer video ? video.TimelineEnd : layer.End;

    private static string? Normalized(ItemDelta delta, string? json, Project owner)
    {
        if (json is null || delta.Kind == "order")
        {
            return json;
        }

        var scratch = new Project { Width = owner.Width, Height = owner.Height };
        Apply(scratch, [delta with { Before = json, PreviousBefore = null }], backwards: true);
        ProjectJson.Normalize(scratch);
        return Current(scratch, delta);
    }

    private static void Setting<T>(List<ItemDelta> deltas, string field, T before, T after)
    {
        var was = Json(before);
        var now = Json(after);
        if (was != now)
        {
            deltas.Add(new ItemDelta("settings", field, null, was, now, null, null));
        }
    }

    private static void SetSetting(Project project, string field, string json)
    {
        switch (field)
        {
            case "name":
                project.Name = JsonSerializer.Deserialize<string>(json, Options) ?? "";
                break;
            case "width":
                project.Width = JsonSerializer.Deserialize<int>(json, Options);
                break;
            case "height":
                project.Height = JsonSerializer.Deserialize<int>(json, Options);
                break;
            case "fps":
                project.Fps = JsonSerializer.Deserialize<int>(json, Options);
                break;
            case "duration":
                project.Duration = JsonSerializer.Deserialize<double>(json, Options);
                break;
            case "backgroundColor":
                project.BackgroundColor = JsonSerializer.Deserialize<string>(json, Options) ?? "#FFFFFF";
                break;
            case "audio":
                project.Audio = JsonSerializer.Deserialize<AudioSettings>(json, Options) ?? new AudioSettings();
                break;
        }
    }

    // The item's JSON in the project now; null when it isn't there.
    private static string? Current(Project project, ItemDelta delta) => delta.Kind switch
    {
        "settings" => delta.Id switch
        {
            "name" => Json(project.Name),
            "width" => Json(project.Width),
            "height" => Json(project.Height),
            "fps" => Json(project.Fps),
            "duration" => Json(project.Duration),
            "backgroundColor" => Json(project.BackgroundColor),
            "audio" => Json(project.Audio),
            _ => null,
        },
        "camera" => project.Camera is null ? null : Json(project.Camera),
        "asset" => project.FindAsset(delta.Id) is { } asset ? Json(asset) : null,
        "layer" => project.FindLayer(delta.Id) is { } layer ? ProjectJson.LayerToJson(layer) : null,
        "track" => project.FindTrack(delta.Id) is { } track ? Json(track) : null,
        "marker" => project.FindMarker(delta.Id) is { } marker ? Json(marker) : null,
        _ => null,
    };

    // Whether the layers named in 'expected' that are still there are in that order.
    private static bool SameOrder(Project project, string expected)
    {
        var ids = JsonSerializer.Deserialize<List<string>>(expected, Options)!;
        var present = project.Layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return ids.Where(present.Contains).SequenceEqual(project.Layers.Select(l => l.Id).Where(wanted.Contains));
    }

    // Puts these layers in this order, in the places they take in the list now.
    private static void Reorder(Project project, List<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var places = new List<int>();
        var byId = new Dictionary<string, Layer>(StringComparer.Ordinal);
        for (var i = 0; i < project.Layers.Count; i++)
        {
            if (wanted.Contains(project.Layers[i].Id) && byId.TryAdd(project.Layers[i].Id, project.Layers[i]))
            {
                places.Add(i);
            }
        }

        var order = ids.Where(byId.ContainsKey).ToList();
        for (var i = 0; i < places.Count; i++)
        {
            project.Layers[places[i]] = byId[order[i]];
        }
    }

    // Replaces, removes or puts back one item; one put back goes after 'previous', or first.
    private static void Put<T>(List<T> list, string id, string? json, string? previous, Func<T, string> idOf, Func<string, T> read)
    {
        var index = list.FindIndex(item => idOf(item) == id);
        if (json is null)
        {
            if (index >= 0)
            {
                list.RemoveAt(index);
            }

            return;
        }

        var value = read(json);
        if (index >= 0)
        {
            list[index] = value;
            return;
        }

        var after = previous is null ? -1 : list.FindIndex(item => idOf(item) == previous);
        list.Insert(previous is null ? 0 : after < 0 ? list.Count : after + 1, value);
    }

    private static void Items<T>(List<ItemDelta> deltas, string kind, List<T> before, List<T> after, Func<T, string> id, Func<T, string?> name, Func<T, string> json)
    {
        var old = new Dictionary<string, (T Item, string? Previous)>(StringComparer.Ordinal);
        string? last = null;
        foreach (var item in before)
        {
            old.TryAdd(id(item), (item, last));
            last = id(item);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        last = null;
        foreach (var item in after)
        {
            var key = id(item);
            var previous = last;
            last = key;
            if (!seen.Add(key))
            {
                continue;
            }

            if (!old.TryGetValue(key, out var was))
            {
                deltas.Add(new ItemDelta(kind, key, name(item), null, json(item), null, previous));
            }
            else
            {
                var wasJson = json(was.Item);
                var nowJson = json(item);
                if (wasJson != nowJson)
                {
                    deltas.Add(new ItemDelta(kind, key, name(item), wasJson, nowJson, was.Previous, previous));
                }
            }
        }

        foreach (var (key, was) in old)
        {
            if (!seen.Contains(key))
            {
                deltas.Add(new ItemDelta(kind, key, name(was.Item), json(was.Item), null, was.Previous, null));
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
