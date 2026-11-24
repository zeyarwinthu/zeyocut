// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// Where one clip ends and the next starts on a lane: the Effects view puts a transition on it.
public sealed record TimelineCut(string Lane, string LayerA, string LayerB, double Time)
{
    public string Label => $"{LayerA} → {LayerB}";
}

public enum EffectCategory
{
    Transitions,
    VideoEffects,
    TextAnimations,
}

// A tile of the Effects view: Key is the transition, effect or preset it puts on; Sketch names the
// drawing of it (EffectSketch).
public sealed record EffectTile(string Key, string Name, string Meta, string Sketch, string Tip);

// One of Core's six transitions as the Effects view offers it, with the ways it can go.
public sealed record TransitionChoice(TransitionType Type, string Name, string Tip, IReadOnlyList<TransitionDirection> Directions, TransitionDirection? Default);

// The Effects view: the cuts on the timeline's lanes; Core's six transitions, applied as
// add_transition applies them; the blur, shadow, border and tint the inspector switches on; and the
// title presets add_text_layer's animateIn and animateOut put on.
public static class EffectsPanel
{
    // add_transition's default length.
    public const double DefaultDuration = 0.5;

    private static readonly TransitionDirection[] Sides = [TransitionDirection.Left, TransitionDirection.Right, TransitionDirection.Up, TransitionDirection.Down];

    public static readonly IReadOnlyList<TransitionChoice> Transitions =
    [
        new(TransitionType.Crossfade, "Crossfade", "The second clip fades in over the first, using unused footage on each side where there is some", [], null),
        new(TransitionType.FadeToBlack, "Dip to black", "The first clip goes to black, then the second comes up from it", [], null),
        new(TransitionType.Wipe, "Wipe", "An edge sweeps across the clip on top", Sides, TransitionDirection.Right),
        new(TransitionType.Slide, "Slide", "The second clip pushes the first out of the frame", Sides, TransitionDirection.Left),
        new(TransitionType.Zoom, "Zoom", "The first clip grows (in) or shrinks (out) while the second comes to its own size", [TransitionDirection.In, TransitionDirection.Out], TransitionDirection.In),
        new(TransitionType.WhipPan, "Whip pan", "A fast slide with motion blur", Sides, TransitionDirection.Left),
    ];

    public static readonly IReadOnlyList<(EffectKind Kind, string Name, string Tip)> Effects =
    [
        (EffectKind.Blur, "Blur", "Softens the layer"),
        (EffectKind.Shadow, "Shadow", "A soft shadow behind the layer"),
        (EffectKind.Outline, "Border", "A line around the layer's shape"),
        (EffectKind.Tint, "Tint", "Colors the layer, white until you pick a color in the inspector"),
    ];

    public static string Name(EffectCategory category) => category switch
    {
        EffectCategory.Transitions => "Transitions",
        EffectCategory.VideoEffects => "Video effects",
        _ => "Text animations",
    };

    public static int Count(EffectCategory category) => category switch
    {
        EffectCategory.Transitions => Transitions.Count,
        EffectCategory.VideoEffects => Effects.Count,
        _ => Enum.GetValues<TitleAnimation>().Length,
    };

    // A category's tiles whose name has every word of the search.
    public static IReadOnlyList<EffectTile> Tiles(EffectCategory category, string search)
    {
        IEnumerable<EffectTile> tiles = category switch
        {
            EffectCategory.Transitions => Transitions.Select(t => new EffectTile(EnumNames.ToName(t.Type), t.Name, $"{Seconds(DefaultDuration)} s", EnumNames.ToName(t.Type), t.Tip)),
            EffectCategory.VideoEffects => Effects.Select(e => new EffectTile(EnumNames.ToName(e.Kind), e.Name, "", EnumNames.ToName(e.Kind), e.Tip)),
            _ => Enum.GetValues<TitleAnimation>().Select(a => new EffectTile(EnumNames.ToName(a), TitleAnimations.Name(a), $"{Seconds(TitleAnimations.Window)} s", EnumNames.ToName(a),
                "In at the layer's start or out at its end, as keyframes on its opacity, scale and y")),
        };
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tiles.Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static TransitionChoice Choice(TransitionType type) => Transitions.First(t => t.Type == type);

    // A direction as the Effects view names it: where the new picture comes from, the opposite of
    // the way add_transition's direction moves it.
    public static string DirectionName(TransitionDirection direction) => direction switch
    {
        TransitionDirection.Left => "From right",
        TransitionDirection.Right => "From left",
        TransitionDirection.Up => "From bottom",
        TransitionDirection.Down => "From top",
        TransitionDirection.In => "In",
        _ => "Out",
    };

    // The cuts on a lane in time order: each clip and the next one on its row that starts within a
    // frame of its end. Caption tracks have none.
    public static IReadOnlyList<TimelineCut> Cuts(TimelineLane lane, double fps)
    {
        if (lane.Kind == LaneKind.Captions)
        {
            return [];
        }

        var frame = 1.0 / fps;
        var cuts = new List<TimelineCut>();
        foreach (var row in lane.Clips.GroupBy(c => c.Row))
        {
            var clips = row.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
            for (var i = 0; i + 1 < clips.Count; i++)
            {
                var (a, b) = (clips[i], clips[i + 1]);
                if (Math.Abs(b.Start - a.End) <= frame + 1e-6 && b.Start > a.Start)
                {
                    cuts.Add(new TimelineCut(lane.Key, a.LayerId, b.LayerId, b.Start));
                }
            }
        }

        return cuts.OrderBy(c => c.Time).ToList();
    }

    // The cut at a clip's end (with the clip after it) or at its start (with the clip before it).
    public static TimelineCut? At(TimelineLane lane, string layerId, bool end, double fps) =>
        Cuts(lane, fps).FirstOrDefault(c => end ? c.LayerA == layerId : c.LayerB == layerId);

    // The cut as the project has it now: null when one of its clips is gone or it's on another lane.
    public static TimelineCut? Current(IReadOnlyList<TimelineLane> lanes, TimelineCut? cut, double fps) =>
        cut is null ? null : lanes.Where(l => l.Key == cut.Lane).SelectMany(l => Cuts(l, fps)).FirstOrDefault(c => c.LayerA == cut.LayerA && c.LayerB == cut.LayerB);

    // The transition on one cut, as add_transition adds it; a crossfade or a dip to black has no direction.
    public static AddTransitionOperation Transition(TimelineCut cut, TransitionType type, double duration, TransitionDirection? direction, Func<Project, ITextMeasurer>? measurer) =>
        new(cut.LayerA, cut.LayerB, type, duration, Choice(type).Directions.Count == 0 ? null : direction, measurer);

    // The transition on every cut of a lane, as one step, in time order.
    public static EditOperation Every(IReadOnlyList<TimelineCut> cuts, TransitionType type, double duration, TransitionDirection? direction, Func<Project, ITextMeasurer>? measurer) =>
        cuts.Count == 1
            ? Transition(cuts[0], type, duration, direction, measurer)
            : new BatchOperation($"add_transition ({cuts.Count} cuts)", cuts.Select(c => (EditOperation)Transition(c, type, duration, direction, measurer)).ToList());

    // An effect switched on as the inspector switches it on (a tint starts white), or taken off.
    public static SetEffectOperation Effect(string layerId, EffectKind kind, bool on) =>
        on ? new SetEffectOperation(layerId, kind, new EffectValues(kind == EffectKind.Tint ? "#FFFFFF" : null, null, null, null, null, null, null))
            : new SetEffectOperation(layerId, kind, new EffectValues(null, null, null, null, null, null, null), Remove: true);

    public static bool Has(VisualLayer layer, EffectKind kind) => kind switch
    {
        EffectKind.Blur => layer.Blur is not null,
        EffectKind.Shadow => layer.Shadow is not null,
        EffectKind.Outline => layer.Outline is not null,
        _ => layer.Tint is not null,
    };

    // The length of a duration typed in seconds, or null when it isn't one.
    public static double? Duration(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0 ? value : null;

    public static string Seconds(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);
}
