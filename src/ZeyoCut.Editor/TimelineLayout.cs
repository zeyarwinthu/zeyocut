// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

public enum LaneKind
{
    Visual,
    Captions,
    Audio,
}

public enum ClipKind
{
    Video,
    Image,
    Text,
    Group,
    Caption,
    Audio,
}

// A clip as the timeline shows it: a layer from Start to End, in a row of its lane. Children are
// the layers that move, trim and split with it; Marks name the cutouts and redactions that follow it.
// Hidden is visible: false, an audio layer's mute; SoundOff is a video's own sound muted.
public sealed record TimelineClip(
    string LayerId,
    ClipKind Kind,
    string Label,
    string Detail,
    double Start,
    double End,
    int Row,
    bool Hidden,
    bool SoundOff,
    int Children,
    IReadOnlyList<string> Marks);

// A lane: one draw level (the zIndex of the top-level video, image and text layers in it), one
// caption group, or the sound of one role. Clips that overlap stack in rows.
public sealed record TimelineLane(string Key, LaneKind Kind, string Title, int ZIndex, AudioRole? Role, int Rows, IReadOnlyList<TimelineClip> Clips);

// The timeline's lanes, worked out from the layers every time the project changes; nothing about
// them is saved. Draw levels and caption groups from the highest zIndex down, then Voice, Music and
// Effects. A video's own sound is part of its clip.
public static class TimelineLayout
{
    public static IReadOnlyList<TimelineLane> Lanes(Project project)
    {
        var marks = Marks(project);
        var children = Descendants(project);
        var visual = new List<TimelineLane>();
        var groups = project.Layers.Where(l => l.ParentId is null && IsCaptionGroup(l)).Cast<ImageLayer>().ToList();
        foreach (var group in groups)
        {
            var cues = project.Layers.Where(l => l.ParentId == group.Id && l is VisualLayer).ToList();
            var language = group.Language ?? (group.Id.StartsWith(CaptionBuilder.DefaultGroup + "_", StringComparison.Ordinal) ? group.Id[(CaptionBuilder.DefaultGroup.Length + 1)..] : null);
            var title = language is null ? "Captions" : $"Captions · {language}";
            visual.Add(Lane($"captions:{group.Id}", LaneKind.Captions, title, group.ZIndex, null, cues.Select(c => Clip(project, c, ClipKind.Caption, 0, marks)).ToList()));
        }

        var levels = project.Layers
            .Where(l => l.ParentId is null && l is VisualLayer && !groups.Contains(l))
            .Cast<VisualLayer>()
            .GroupBy(l => l.ZIndex);
        foreach (var level in levels)
        {
            var clips = level.Select(l => Clip(project, l, KindOf(l, children), children.GetValueOrDefault(l.Id), marks)).ToList();
            var kinds = clips.Select(c => c.Kind).Distinct().ToList();
            var holds = kinds.Count == 1 ? Plural(kinds[0]) : "Mixed";
            visual.Add(Lane($"z:{level.Key}", LaneKind.Visual, $"{holds} · z {level.Key}", level.Key, null, clips));
        }

        var lanes = visual.OrderByDescending(l => l.ZIndex).ThenBy(l => l.Kind == LaneKind.Captions ? 0 : 1).ToList();
        foreach (var role in new[] { AudioRole.Voice, AudioRole.Music, AudioRole.Effects })
        {
            var clips = project.Layers.OfType<AudioLayer>().Where(a => a.Role == role)
                .Select(a => Clip(project, a, ClipKind.Audio, children.GetValueOrDefault(a.Id), marks)).ToList();
            if (clips.Count > 0)
            {
                lanes.Add(Lane($"audio:{EnumNames.ToName(role)}", LaneKind.Audio, role.ToString(), 0, role, clips));
            }
        }

        return lanes;
    }

    // The null image layer generate_captions and add_caption_track hold their cues in.
    public static bool IsCaptionGroup(Layer layer) =>
        layer is ImageLayer { Drawings.Count: 0 } && (layer.Id == CaptionBuilder.DefaultGroup || layer.Id.StartsWith(CaptionBuilder.DefaultGroup + "_", StringComparison.Ordinal));

    // Every clip's start and end, in time order, once each.
    public static IReadOnlyList<double> EditPoints(IEnumerable<TimelineLane> lanes) =>
        lanes.SelectMany(l => l.Clips).SelectMany(c => new[] { c.Start, c.End }).Distinct().Order().ToList();

    // Rows in the order the layers draw: each clip goes in the first row where it overlaps nothing.
    private static TimelineLane Lane(string key, LaneKind kind, string title, int zIndex, AudioRole? role, List<TimelineClip> clips)
    {
        var rows = new List<List<TimelineClip>>();
        var placed = new List<TimelineClip>();
        foreach (var clip in clips)
        {
            var row = rows.FindIndex(r => r.All(o => o.End <= clip.Start + TimelineEditor.Epsilon || o.Start >= clip.End - TimelineEditor.Epsilon));
            if (row < 0)
            {
                rows.Add([]);
                row = rows.Count - 1;
            }

            var inRow = clip with { Row = row };
            rows[row].Add(inRow);
            placed.Add(inRow);
        }

        return new TimelineLane(key, kind, title, zIndex, role, Math.Max(1, rows.Count), placed);
    }

    private static TimelineClip Clip(Project project, Layer layer, ClipKind kind, int children, Dictionary<string, List<string>> marks)
    {
        var detail = layer switch
        {
            TextLayer text => text.Text.ReplaceLineEndings(" "),
            VideoLayer video => video.Asset,
            AudioLayer audio => audio.Asset,
            ImageLayer image => string.Join(", ", image.Drawings.Values.Distinct()),
            _ => "",
        };
        return new TimelineClip(
            layer.Id,
            kind,
            kind == ClipKind.Caption ? detail : layer.Id,
            kind == ClipKind.Caption ? "" : detail,
            layer.Start,
            TimelineEditor.ContentEnd(project, layer),
            0,
            !layer.Visible,
            layer is VideoLayer { Muted: true },
            children,
            marks.TryGetValue(layer.Id, out var list) ? list : []);
    }

    private static ClipKind KindOf(Layer layer, Dictionary<string, int> children) => layer switch
    {
        VideoLayer => ClipKind.Video,
        TextLayer => ClipKind.Text,
        ImageLayer image when image.Drawings.Count == 0 && children.GetValueOrDefault(layer.Id) > 0 => ClipKind.Group,
        _ => ClipKind.Image,
    };

    // How many layers each layer has under it, children and their children.
    private static Dictionary<string, int> Descendants(Project project)
    {
        var parents = project.Layers.Where(l => l.ParentId is not null).ToDictionary(l => l.Id, l => l.ParentId!, StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, first) in parents)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            for (string? parent = first; parent is not null && seen.Add(parent); parent = parents.GetValueOrDefault(parent))
            {
                counts[parent] = counts.GetValueOrDefault(parent) + 1;
            }
        }

        return counts;
    }

    private static string Plural(ClipKind kind) => kind switch
    {
        ClipKind.Video => "Video",
        ClipKind.Image => "Images",
        ClipKind.Text => "Text",
        _ => "Groups",
    };

    // The cutouts and redactions that follow a clip, by the clip's layer id: a cutout follows its
    // sources; a redaction, every clip that plays the footage of one of its tracks.
    private static Dictionary<string, List<string>> Marks(Project project)
    {
        var marks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string layerId, string mark)
        {
            if (!marks.TryGetValue(layerId, out var list))
            {
                marks[layerId] = list = [];
            }

            if (!list.Contains(mark))
            {
                list.Add(mark);
            }
        }

        foreach (var cutout in project.Layers.OfType<CutoutLayer>())
        {
            foreach (var source in cutout.Sources)
            {
                Add(source, $"cutout '{cutout.Id}'");
            }
        }

        foreach (var redaction in project.Layers.OfType<RedactionLayer>())
        {
            foreach (var track in redaction.Tracks.Select(project.FindTrack).OfType<Track>())
            {
                foreach (var clip in RedactionEditor.Clips(project, track.Asset))
                {
                    Add(clip.Id, $"redaction '{redaction.Id}'");
                }
            }
        }

        return marks;
    }
}
