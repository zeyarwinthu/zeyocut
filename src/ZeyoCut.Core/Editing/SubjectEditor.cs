// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// What text_behind_subject works on. With an own-subject reason the subject is already a layer of its
// own (a removed background, a transparent picture, a group) and only the texts move; otherwise a
// cutout of its sources (the clip or picture and the pieces splits made of it) is drawn above them.
public sealed record SubjectTarget(Layer Subject, IReadOnlyList<TextLayer> Texts, string? OwnSubject, IReadOnlyList<Layer> Sources, CutoutLayer? Existing);

public sealed record SubjectChange(string Summary, IReadOnlyList<string> Notes, CutoutLayer? Cutout);

// generate_captions behindSubject: Cover is what draws over the captions (their cutout, or the clips
// that are their own subject); Clips are the clips the subject comes from.
public sealed record CaptionSubject(SubjectChange Change, IReadOnlyList<string> Cover, IReadOnlyList<string> Clips);

// text_behind_subject and the subject cutouts it makes: a cutout always follows its sources, so the
// edits that would change what it shows are made on the sources and carried over.
public static class SubjectEditor
{
    public static readonly IReadOnlyList<string> OwnFields = ["name", "zIndex", "visible", "opacity"];

    public static ZeyoCutException Follows(CutoutLayer cutout)
    {
        var sources = string.Join(", ", cutout.Sources.Select(s => $"'{s}'"));
        var them = cutout.Sources.Count == 1 ? sources : "them";
        return new ZeyoCutException(
            $"'{cutout.Id}' is the subject cutout of {sources}; it always follows {them}, so make this change on {them} (its own settings are only name, zIndex, visible and opacity)");
    }

    // A layer an edit may change; cutouts follow their sources instead.
    public static Layer GetEditableLayer(Project project, string? id, string role = "layer")
    {
        var layer = ProjectEditor.GetLayer(project, id, role);
        return layer switch
        {
            CutoutLayer cutout => throw Follows(cutout),
            RedactionLayer redaction => throw RedactionEditor.Follows(redaction),
            _ => layer,
        };
    }

    // After layers were split, cut or removed: each cutout follows the pieces its sources became, and
    // a cutout whose sources (or caption group) are all gone goes too. Returns the ids of the cutouts
    // removed.
    public static List<string> Follow(Project project, IReadOnlyDictionary<string, List<string>>? pieces = null)
    {
        var ids = project.Layers.Where(l => l is not CutoutLayer).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var cutout in project.Layers.OfType<CutoutLayer>().ToList())
        {
            var sources = new List<string>();
            foreach (var source in cutout.Sources)
            {
                var now = pieces is not null && pieces.TryGetValue(source, out var list) ? list : [source];
                sources.AddRange(now.Where(ids.Contains));
            }

            cutout.Sources = sources.Distinct(StringComparer.Ordinal).ToList();
            if (cutout.Sources.Count == 0 || cutout.CaptionGroup is { } group && project.FindLayer(group) is not ImageLayer { Drawings.Count: 0 })
            {
                project.Layers.Remove(cutout);
                removed.Add(cutout.Id);
            }
        }

        return removed;
    }

    // transparent: the subject is an image whose drawings already have a transparent background.
    public static SubjectTarget Resolve(Project project, string? subjectId, IReadOnlyList<string>? textIds, bool transparent)
    {
        var subject = ProjectEditor.GetLayer(project, subjectId, "videoLayerId");
        switch (subject)
        {
            case CutoutLayer cutout:
                throw new ZeyoCutException($"'{cutout.Id}' is already a subject cutout (of {string.Join(", ", cutout.Sources.Select(s => $"'{s}'"))}); pass its source as videoLayerId");
            case TextLayer or AudioLayer:
                throw new ZeyoCutException($"'{subject.Id}' is a {subject.TypeName} layer; videoLayerId takes the clip (or picture, or group) whose subject goes in front of the text");
        }

        var subtree = ProjectEditor.Subtree(project, subject.Id);
        var texts = Texts(project, textIds);
        if (texts.FirstOrDefault(t => subtree.Contains(t)) is { } inside)
        {
            throw new ZeyoCutException($"'{inside.Id}' is part of '{subject.Id}' (a child layer), so it can't go behind it; give it another parent first (update_layer parentId)");
        }

        string? own = subject switch
        {
            ImageLayer { Drawings.Count: 0 } when !subtree.Any(l => l is VideoLayer or ImageLayer { Drawings.Count: > 0 }) =>
                throw new ZeyoCutException($"'{subject.Id}' is a null layer with no pictures under it, so it has no subject"),
            ImageLayer { Drawings.Count: 0 } => $"'{subject.Id}' is a group, so its layers are the subject",
            IMattable { Matte: { } matte } => $"'{subject.Id}' already has its background removed ({EnumNames.ToName(matte.Method)}), so it is its own subject",
            ImageLayer when transparent => $"'{subject.Id}' already has a transparent background, so it is its own subject",
            _ => null,
        };
        if (own is not null)
        {
            return new SubjectTarget(subject, texts, own, [], null);
        }

        var pieces = project.Layers.Where(l => ReferenceEquals(l, subject) || IsPiece(l, subject)).ToList();
        var cutouts = project.Layers.OfType<CutoutLayer>().Where(c => c.CaptionGroup is null).ToList();
        var existing = cutouts.FirstOrDefault(c => c.Sources.Contains(subject.Id))
            ?? cutouts.FirstOrDefault(c => c.Sources.Any(s => pieces.Any(p => p.Id == s)));
        var sources = project.Layers.Where(l => pieces.Contains(l) || existing?.Sources.Contains(l.Id) == true).ToList();
        return new SubjectTarget(subject, texts, null, sources, existing);
    }

    // matte: the cutout's matte (required for a new cutout; null keeps an existing cutout's).
    public static SubjectChange Apply(Project project, string? subjectId, IReadOnlyList<string>? textIds, bool transparent, Matte? matte)
    {
        var target = Resolve(project, subjectId, textIds, transparent);
        var subject = target.Subject;
        var names = string.Join(", ", target.Texts.Select(t => $"'{t.Id}'"));
        if (target.OwnSubject is { } own)
        {
            var layers = ProjectEditor.Subtree(project, subject.Id).Where(l => l is VisualLayer).ToList();
            var notes = Below(project, own, layers, subject.Id, target.Texts, "the text now draws");
            return new SubjectChange($"put {names} behind '{subject.Id}'", notes, null);
        }

        var pieces = target.Sources.Where(s => s.Id != subject.Id).Select(s => $"'{s.Id}'").ToList();
        var follows = pieces.Count == 0
            ? $"'{subject.Id}'"
            : $"'{subject.Id}' and {string.Join(", ", pieces)} ({(pieces.Count == 1 ? "another piece" : "other pieces")} of the same clip on its track)";
        var (cutout, created, was, cutNotes) = Cut(project, target.Texts, target.Sources, target.Existing, matte, $"{subject.Id}_subject", null, follows);
        var method = MatteEditor.Describe(cutout.Matte!);
        var summary = created
            ? $"put {names} behind the subject of '{subject.Id}': cutout '{cutout.Id}' draws the subject again above the text, cut out with {method} (zIndex {cutout.ZIndex})"
            : $"put {names} behind the subject of '{subject.Id}' with its cutout '{cutout.Id}' ({method}{(was is not null && matte is not null && was.Method != matte.Method ? $", was {EnumNames.ToName(was.Method)}" : "")}; zIndex {cutout.ZIndex})";
        return new SubjectChange(summary, cutNotes, cutout);
    }

    // generate_captions behindSubject: the captions under a group go behind the subject of the clips
    // that play the transcribed asset. They get a cutout of their own (its captionGroup is the group),
    // so its fades (auto) leave other texts behind the subject; a new one takes the matte of the clip's
    // cutout from text_behind_subject, or aiVideo. Clips whose background is already removed are their
    // own subject: the captions move directly below them instead.
    public static CaptionSubject CaptionsBehind(Project project, string groupId, string assetId, IReadOnlyList<VideoLayer> clips)
    {
        var existing = CaptionCutout(project, groupId);
        var texts = Texts(project, [groupId]);
        var own = clips.Where(c => c.Matte is not null).ToList();
        var cut = clips.Where(c => c.Matte is null).ToList();
        if (cut.Count == 0)
        {
            var reason = own.Count == 1
                ? $"'{own[0].Id}' already has its background removed ({EnumNames.ToName(own[0].Matte!.Method)}), so it is its own subject"
                : $"{List(own)} already have their backgrounds removed, so they are their own subject";
            var lowest = own.MinBy(c => Position(project, c))!;
            var notes = Below(project, reason, own, lowest.Id, texts, "the captions now draw");
            if (existing is not null)
            {
                project.Layers.Remove(existing);
                notes.Add($"removed '{existing.Id}', the captions' earlier cutout, since it isn't needed");
            }

            var ids = own.Select(c => c.Id).ToList();
            return new CaptionSubject(new SubjectChange($"put the captions behind '{lowest.Id}'", notes, null), ids, ids);
        }

        var sources = project.Layers.Where(l => cut.Any(c => ReferenceEquals(l, c) || IsPiece(l, c))).ToList();
        Matte? matte = null;
        CutoutLayer? lender = null;
        if (existing is null)
        {
            lender = project.Layers.OfType<CutoutLayer>()
                .FirstOrDefault(c => c.CaptionGroup is null && c.Matte is not null && c.Sources.Any(s => sources.Any(l => l.Id == s)));
            matte = lender?.Matte is { } m
                ? new Matte { Method = m.Method, Color = m.Color, Tolerance = m.Tolerance, Softness = m.Softness, Spill = m.Spill, Shrink = m.Shrink, Feather = m.Feather }
                : new Matte { Method = MatteMethod.AiVideo };
        }

        var shown = sources.Take(6).Select(l => $"'{l.Id}'").ToList();
        var follows = $"the {(sources.Count == 1 ? "clip that plays" : "clips that play")} '{assetId}' ({string.Join(", ", shown)}{(sources.Count > shown.Count ? $" and {sources.Count - shown.Count} more" : "")})";
        var (cutout, created, _, cutNotes) = Cut(project, texts, sources, existing, matte, $"{groupId}_subject", groupId, follows);
        cutout.Opacity.Keyframes.Clear();
        if (lender is not null)
        {
            cutNotes.Add($"it uses the matte of '{lender.Id}', the cutout of the same clip from text_behind_subject");
        }

        if (own.Count > 0)
        {
            cutNotes.Add($"{List(own)} {(own.Count == 1 ? "has its" : "have their")} background removed already, so {(own.Count == 1 ? "it isn't" : "they aren't")} part of the cutout");
        }

        var method = MatteEditor.Describe(cutout.Matte!);
        var summary = created
            ? $"put the captions behind the subject of '{sources[0].Id}': cutout '{cutout.Id}' draws the subject again above them, cut out with {method} (zIndex {cutout.ZIndex})"
            : $"put the captions behind the subject of '{sources[0].Id}' with their cutout '{cutout.Id}' ({method}; zIndex {cutout.ZIndex})";
        return new CaptionSubject(new SubjectChange(summary, cutNotes, cutout), [cutout.Id], sources.Select(l => l.Id).ToList());
    }

    // The cutout generate_captions made for the captions under this group.
    public static CutoutLayer? CaptionCutout(Project project, string groupId) =>
        project.Layers.OfType<CutoutLayer>().FirstOrDefault(c => c.CaptionGroup == groupId);

    // behindSubject=off: the captions' cutout goes; returns the note, or null when there was none.
    public static string? RemoveCaptionCutout(Project project, string groupId)
    {
        if (CaptionCutout(project, groupId) is not { } cutout)
        {
            return null;
        }

        project.Layers.Remove(cutout);
        return $"removed '{cutout.Id}', the captions' subject cutout, since behindSubject is off";
    }

    // behindSubject=auto on a subject that is its own layer: these captions draw directly above it.
    public static void InFront(Project project, IReadOnlyList<string> textIds, IReadOnlyList<string> subjectIds)
    {
        var top = subjectIds.Select(id => ProjectEditor.GetLayer(project, id)).MaxBy(l => Position(project, l))!;
        foreach (var id in textIds)
        {
            PlaceAbove(project, ProjectEditor.GetLayer(project, id), top);
            top = ProjectEditor.GetLayer(project, id);
        }
    }

    // The texts move directly below the subject's lowest layer.
    private static List<string> Below(Project project, string reason, IReadOnlyList<Layer> subjectLayers, string subjectId, IReadOnlyList<TextLayer> texts, string what)
    {
        var notes = new List<string>();
        var lowest = subjectLayers.MinBy(l => Position(project, l))!;
        foreach (var text in texts.OrderBy(t => Position(project, t)).ToList())
        {
            if (Position(project, text) < Position(project, lowest))
            {
                notes.Add($"'{text.Id}' was already drawn below '{subjectId}'");
                continue;
            }

            PlaceBelow(project, text, lowest);
        }

        var below = lowest.Id == subjectId ? $"'{subjectId}'" : $"'{lowest.Id}', the lowest layer of '{subjectId}'";
        notes.Insert(0, $"{reason} and needs no cutout: {what} directly below {below} (zIndex {lowest.ZIndex})");
        return notes;
    }

    // The cutout of 'sources' (made, or 'existing' reused) drawn above the texts; texts below the
    // sources are lifted just above them.
    private static (CutoutLayer Cutout, bool Created, Matte? Was, List<string> Notes) Cut(
        Project project, IReadOnlyList<TextLayer> texts, IReadOnlyList<Layer> sources, CutoutLayer? existing, Matte? matte, string idBase, string? captionGroup, string follows)
    {
        var notes = new List<string>();
        var cutout = existing;
        var created = cutout is null;
        var was = cutout?.Matte;
        if (cutout is null)
        {
            if (matte is null)
            {
                throw new ZeyoCutException("a new cutout needs a matte method");
            }

            var id = Ids.Unique(idBase, c => c == Camera.Id || project.FindLayer(c) is not null);
            cutout = new CutoutLayer { Id = id, Name = id, CaptionGroup = captionGroup };
            project.Layers.Add(cutout);
        }

        cutout.Sources = sources.Select(s => s.Id).ToList();
        if (matte is not null)
        {
            matte.Shrink = was?.Shrink ?? matte.Shrink;
            matte.Feather = was?.Feather ?? matte.Feather;
            cutout.Matte = matte;
        }

        var topSource = sources.MaxBy(s => Position(project, s))!;
        Layer anchor = topSource;
        foreach (var text in texts.OrderBy(t => Position(project, t)).ToList())
        {
            if (Position(project, text) < Position(project, topSource))
            {
                PlaceAbove(project, text, anchor);
                anchor = text;
                notes.Add($"'{text.Id}' was drawn below '{topSource.Id}', so it now draws just above it");
            }
        }

        var topText = texts.MaxBy(t => Position(project, t))!;
        if (created || Position(project, cutout) < Position(project, topText))
        {
            PlaceAbove(project, cutout, topText);
        }

        var order = DrawOrder(project);
        var between = order
            .Skip(order.IndexOf(topSource) + 1)
            .Take(order.IndexOf(cutout) - order.IndexOf(topSource) - 1)
            .Where(l => !texts.Contains(l) && !sources.Contains(l) && l is not (CutoutLayer or ImageLayer { Drawings.Count: 0 }))
            .ToList();
        var inBetween = between.OfType<TextLayer>().Select(l => $"'{l.Id}'").ToList();
        var others = between.Where(l => l is not TextLayer).Select(l => $"'{l.Id}'").ToList();
        if (inBetween.Count > 0)
        {
            notes.Add($"{string.Join(", ", inBetween)} {(inBetween.Count == 1 ? "is" : "are")} behind the subject too");
        }

        if (others.Count > 0)
        {
            notes.Add($"{string.Join(", ", others)} {(others.Count == 1 ? "is" : "are")} also drawn between '{topSource.Id}' and the cutout, so the subject covers {(others.Count == 1 ? "it" : "them")} where they overlap");
        }

        notes.Insert(0, $"the cutout follows {follows}: its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity");
        return (cutout, created, was, notes);
    }

    private static string List(IEnumerable<Layer> layers) => string.Join(", ", layers.Select(l => $"'{l.Id}'"));

    // Text layers by id; a group (like captions) stands for the text layers under it.
    private static List<TextLayer> Texts(Project project, IReadOnlyList<string>? textIds)
    {
        if (textIds is null || textIds.Count == 0)
        {
            throw new ZeyoCutException("textLayerIds is empty; pass the text layers (or a group of them, like captions) to put behind the subject");
        }

        var texts = new List<TextLayer>();
        foreach (var id in textIds)
        {
            var layer = ProjectEditor.GetLayer(project, id, "text layer");
            switch (layer)
            {
                case TextLayer text:
                    texts.Add(text);
                    break;
                case ImageLayer { Drawings.Count: 0 }:
                    var under = ProjectEditor.Subtree(project, layer.Id).OfType<TextLayer>().ToList();
                    texts.AddRange(under.Count > 0 ? under : throw new ZeyoCutException($"'{layer.Id}' has no text layers under it"));
                    break;
                default:
                    throw new ZeyoCutException($"'{layer.Id}' is a {layer.TypeName} layer; textLayerIds takes text layers or a group of them (like captions)");
            }
        }

        return texts.Distinct().ToList();
    }

    // Another piece of the same clip or picture on the same track (what splits leave), without a
    // background removal of its own.
    private static bool IsPiece(Layer layer, Layer subject) =>
        layer.ZIndex == subject.ZIndex
        && layer.ParentId == subject.ParentId
        && (layer, subject) switch
        {
            (VideoLayer { Matte: null } a, VideoLayer b) => a.Asset == b.Asset,
            (ImageLayer { Matte: null } a, ImageLayer b) => a.Drawings.Count > 0 && a.Drawings.Count == b.Drawings.Count && a.Drawings.All(d => b.Drawings.TryGetValue(d.Key, out var asset) && asset == d.Value),
            _ => false,
        };

    // Image, text and video layers and cutouts in the order they are drawn (zIndex, then array position).
    public static List<Layer> DrawOrder(Project project) =>
        project.Layers.Select((layer, index) => (Layer: layer, Index: index))
            .Where(e => e.Layer is VisualLayer or CutoutLayer)
            .OrderBy(e => e.Layer.ZIndex)
            .ThenBy(e => e.Index)
            .Select(e => e.Layer)
            .ToList();

    private static int Position(Project project, Layer layer) => DrawOrder(project).IndexOf(layer);

    // Draws 'layer' directly above 'anchor': the same zIndex, right after it in the layers array.
    private static void PlaceAbove(Project project, Layer layer, Layer anchor)
    {
        project.Layers.Remove(layer);
        layer.ZIndex = anchor.ZIndex;
        project.Layers.Insert(project.Layers.IndexOf(anchor) + 1, layer);
    }

    private static void PlaceBelow(Project project, Layer layer, Layer anchor)
    {
        project.Layers.Remove(layer);
        layer.ZIndex = anchor.ZIndex;
        project.Layers.Insert(project.Layers.IndexOf(anchor), layer);
    }
}
