// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// One edit as data: what the editor does when you drag, trim, split, delete or change a field. Each
// is applied through the same Core editor the matching MCP tool uses, so both make the same change.
// Label names it the way the tool would be named in history and backups (trim_layer clip_2).
public abstract record EditOperation
{
    public abstract string Label { get; }

    // The layers it works on. The editor drops an edit when another program changed one of them
    // after the editor last showed it, rather than make it on top of a version you didn't see.
    public abstract IReadOnlyList<string> Layers { get; }

    // Makes the change and returns a one-line summary of it.
    public abstract string Apply(Project project);

    protected static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record MoveLayerOperation(string LayerId, double Start) : EditOperation
{
    public override string Label => $"move_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => TimelineEditor.MoveLayer(project, LayerId, Start).Summary;
}

public sealed record TrimLayerOperation(string LayerId, double? Start, double? End) : EditOperation
{
    public override string Label => $"trim_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => TimelineEditor.TrimLayer(project, LayerId, Start, End).Summary;
}

public sealed record SplitLayerOperation(string LayerId, double Time) : EditOperation
{
    public override string Label => $"split_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project)
    {
        var result = TimelineEditor.SplitLayer(project, LayerId, Time);
        return $"split '{LayerId}' at {Num(Time)}s into '{result.LeftId}' and '{result.RightId}'";
    }
}

public sealed record RemoveLayerOperation(string LayerId, bool WithChildren) : EditOperation
{
    public override string Label => $"remove_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => $"removed: {string.Join(", ", ProjectEditor.RemoveLayer(project, LayerId, WithChildren))}";
}

// Any of a layer's fields, as update_layer takes them: {"volume": 0.5}, {"visible": false} (an
// audio layer's mute), {"fadeIn": 1}, {"text": "Hi"}, {"start": 2, "end": 4}.
public sealed record UpdateLayerOperation(string LayerId, JsonObject Changes) : EditOperation
{
    public override string Label => $"update_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    // A copy of the changes each time: a JSON node can't belong to two documents.
    public override string Apply(Project project)
    {
        var warnings = ProjectEditor.UpdateLayer(project, LayerId, (JsonObject)Changes.DeepClone());
        return $"updated layer '{LayerId}'" + string.Concat(warnings.Select(w => $"; {w}"));
    }
}

// The same fields of several layers in one step, such as the style of every caption in a track.
public sealed record UpdateLayersOperation(string Name, IReadOnlyList<string> LayerIds, JsonObject Changes) : EditOperation
{
    public override string Label => Name;

    public override IReadOnlyList<string> Layers => LayerIds;

    public override string Apply(Project project)
    {
        if (LayerIds.Count == 0)
        {
            throw new ZeyoCutException("there are no layers to change");
        }

        var warnings = LayerIds.SelectMany(id => ProjectEditor.UpdateLayer(project, id, (JsonObject)Changes.DeepClone())).Distinct().ToList();
        return $"updated {LayerIds.Count} layer(s): {string.Join(", ", Changes.Select(c => c.Key))}" + string.Concat(warnings.Select(w => $"; {w}"));
    }
}

// Keyframes, as set_keyframes adds them: each replaces a keyframe at the same time of its property.
public sealed record SetKeyframesOperation(IReadOnlyList<KeyframeEntry> Entries) : EditOperation
{
    public override string Label => $"set_keyframes {string.Join(" ", Layers)}";

    public override IReadOnlyList<string> Layers => Entries.Select(e => e.LayerId).Distinct(StringComparer.Ordinal).ToList();

    public override string Apply(Project project)
    {
        var change = KeyframeEditor.SetKeyframes(project, Entries);
        var targets = Entries.Select(e => $"{e.LayerId}.{e.Property}").Distinct().ToList();
        return $"set {change.Added + change.Replaced} keyframe(s) at {Num(Entries[0].Time)}s on {string.Join(", ", targets)}" + string.Concat(change.Warnings.Select(w => $"; {w}"));
    }
}

// Keyframes dragged in the Animate view's dope sheet or graph, each keeping its easing, as one step.
public sealed record MoveKeyframesOperation(IReadOnlyList<KeyframeMove> Moves) : EditOperation
{
    public override string Label => $"move_keyframes {string.Join(" ", Layers)}";

    public override IReadOnlyList<string> Layers => Moves.Select(m => m.LayerId).Distinct(StringComparer.Ordinal).ToList();

    public override string Apply(Project project) => KeyframeEditor.MoveKeyframes(project, Moves);
}

// Keyframes' easing, as set_keyframes gives it: a named one, or cubicBezier with its four numbers.
public sealed record SetEasingOperation(IReadOnlyList<KeyframeRef> Refs, Easing Easing, IReadOnlyList<double>? Bezier = null) : EditOperation
{
    public override string Label => $"set_keyframes {string.Join(" ", Layers)}";

    public override IReadOnlyList<string> Layers => Refs.Select(r => r.LayerId).Distinct(StringComparer.Ordinal).ToList();

    public override string Apply(Project project)
    {
        var count = KeyframeEditor.SetEasing(project, Refs, Easing, Bezier);
        var targets = Refs.Select(r => $"{r.LayerId}.{r.Property}").Distinct().ToList();
        var numbers = Bezier is null ? "" : " [" + string.Join(", ", Bezier.Select(Num)) + "]";
        return $"set {count} keyframe(s) on {string.Join(", ", targets)} to {EnumNames.ToName(Easing)}{numbers}";
    }
}

// Keyframes taken away, as remove_keyframes takes them.
public sealed record RemoveKeyframesOperation(IReadOnlyList<KeyframeRef> Refs) : EditOperation
{
    public override string Label => $"remove_keyframes {string.Join(" ", Layers)}";

    public override IReadOnlyList<string> Layers => Refs.Select(r => r.LayerId).Distinct(StringComparer.Ordinal).ToList();

    public override string Apply(Project project)
    {
        var removed = KeyframeEditor.RemoveKeyframes(project, Refs);
        var targets = Refs.Select(r => $"{r.LayerId}.{r.Property}").Distinct().ToList();
        return $"removed {removed} keyframe(s) at {Num(Refs[0].Time)}s from {string.Join(", ", targets)}";
    }
}

// An effect added, changed or removed, as set_effect does it (EffectEditor.SetEffect).
public sealed record SetEffectOperation(string LayerId, EffectKind Kind, EffectValues Values, bool Remove = false) : EditOperation
{
    public override string Label => $"set_effect {LayerId} {EnumNames.ToName(Kind)}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project)
    {
        var change = EffectEditor.SetEffect(project, LayerId, Kind, Values, Remove);
        return change.Summary + string.Concat(change.Warnings.Select(w => $"; {w}"));
    }
}

// Layers moved by X, Y pixels at every moment (PlacementEditor.Shift), such as every caption of a
// track when one of them is dragged.
public sealed record ShiftLayersOperation(string Name, IReadOnlyList<string> LayerIds, double X, double Y) : EditOperation
{
    public override string Label => Name;

    public override IReadOnlyList<string> Layers => LayerIds;

    public override string Apply(Project project) =>
        $"moved {PlacementEditor.Shift(project, LayerIds, X, Y)} layer(s) by {Signed(X)}, {Signed(Y)} px";

    private static string Signed(double value) => (value > 0 ? "+" : "") + Num(value);
}

// A video's crop by its sides, keeping what remains of the picture in place (PlacementEditor.Crop).
public sealed record CropLayerOperation(string LayerId, CropSides Sides) : EditOperation
{
    public override string Label => $"crop_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => PlacementEditor.Crop(project, LayerId, Sides);
}

// A video clip's speed, as set_speed changes it with ripple off: the clip keeps its start, and its
// children and keyframes stretch with it; the layers after it stay where they are.
public sealed record SetSpeedOperation(string LayerId, double Speed) : EditOperation
{
    public override string Label => $"set_speed {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project)
    {
        var change = TimelineEditor.SetSpeed(project, LayerId, Speed, ripple: false);
        return change.Summary + string.Concat(change.Warnings.Select(w => $"; {w}"));
    }
}

// A transition from LayerA to LayerB, as add_transition adds it (TimelineEditor.AddTransition):
// baked into keyframes and fades, a crossfade overlapping the clips and moving later layers left
// for what their unused source doesn't cover. Measurer measures text in the project it's applied to.
public sealed record AddTransitionOperation(string LayerA, string LayerB, TransitionType Type, double Duration, TransitionDirection? Direction = null, Func<Project, ITextMeasurer>? Measurer = null) : EditOperation
{
    public override string Label => $"add_transition {LayerA} {LayerB}";

    public override IReadOnlyList<string> Layers => [LayerA, LayerB];

    public override string Apply(Project project)
    {
        var change = TimelineEditor.AddTransition(project, LayerA, LayerB, Type, Duration, Direction, Measurer?.Invoke(project));
        var longer = Math.Abs(change.NewDuration - change.OldDuration) > 1e-9 ? $"; the project is {Num(change.NewDuration)}s long (was {Num(change.OldDuration)}s)" : "";
        return change.Summary + longer + string.Concat(change.Warnings.Select(w => $"; {w}"));
    }
}

// How a layer comes in or goes out (TitleAnimations.Set): the keyframes add_text_layer's animateIn
// and animateOut add; null takes that side's away.
public sealed record TitleAnimationOperation(string LayerId, TitleSide Side, TitleAnimation? Animation) : EditOperation
{
    public override string Label => $"animate_{(Side == TitleSide.In ? "in" : "out")} {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => TitleAnimations.Set(project, LayerId, Side, Animation);
}

// A text layer as add_text_layer adds it (TitleAnimations.AddText): a library style filling in what
// Options leaves out (its font copied from Library into the project's Directory), coming in and going
// out with In and Out. Measurer measures text in the project it's applied to.
public sealed record AddTitleOperation(
    TextLayerOptions Options,
    TextStyle? Style = null,
    string? Directory = null,
    Library? Library = null,
    TitleAnimation? In = null,
    TitleAnimation? Out = null,
    Func<Project, ITextMeasurer>? Measurer = null) : EditOperation
{
    public override string Label => $"add_text_layer {Options.Id ?? Options.Name}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        var text = TitleAnimations.AddText(project, Directory ?? "", Library, Style, Options, In, Out, Measurer?.Invoke(project));
        return $"added text layer '{text.Id}' at {Num(text.Start)}s" + (Style is null ? "" : $" in style '{Style.Name}'");
    }
}

// A library style on a text layer, or on every text layer under a group, as apply_style puts it on
// (TextStyles.Apply): its font copied from Library into the project's Directory. Text, position and
// timing stay. Measurer measures text in the project it's applied to.
public sealed record ApplyStyleOperation(TextStyle Style, string LayerId, string Directory, Library Library, Func<Project, ITextMeasurer>? Measurer = null) : EditOperation
{
    public override string Label => $"apply_style {Style.Name} to {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project)
    {
        var ids = TextStyles.Apply(project, Directory, Library, Style, LayerId, Measurer?.Invoke(project));
        ProjectValidator.ThrowIfInvalid(project);
        return $"applied style '{Style.Name}' to {ids.Count} text layer(s): {Ids.List(ids)}";
    }
}

// A library template's layers as one block from At, as add_template adds them (Templates.Add): its
// files copied from Library into the project's Directory.
public sealed record AddTemplateOperation(string Name, double At, string Directory, Library Library) : EditOperation
{
    public override string Label => $"add_template {Name}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        var placed = Templates.Add(project, Directory, Library, Name, At);
        return Templates.Describe(placed) + string.Concat(placed.Warnings.Select(w => $"; {w}"));
    }
}

// The project's settings, as update_project changes them: the Audio view's mixer, ducking and loudness.
public sealed record UpdateProjectOperation(ProjectOptions Options) : EditOperation
{
    public override string Label => "update_project";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        var update = ProjectEditor.UpdateProject(project, Options);
        return string.Join("; ", update.Changes.Concat(update.Warnings));
    }
}

// Markers, as add_markers adds or changes them, in one step: all of them or none.
public sealed record AddMarkersOperation(IReadOnlyList<MarkerEntry> Entries) : EditOperation
{
    public override string Label => $"add_markers ({Entries.Count} marker{(Entries.Count == 1 ? "" : "s")})";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        var change = MarkerEditor.AddMarkers(project, Entries);
        var parts = new List<string>();
        if (change.Added.Count > 0)
        {
            parts.Add($"added {change.Added.Count} marker{(change.Added.Count == 1 ? "" : "s")}: {string.Join(", ", change.Added.Select(m => m.Id))}");
        }

        if (change.Changed.Count > 0)
        {
            parts.Add($"changed {string.Join(", ", change.Changed.Select(m => m.Id))}");
        }

        return string.Join("; ", parts.Concat(change.Warnings));
    }
}

// Draw order: a layer's zIndex (higher draws on top); the timeline's lanes follow it. Its children
// move with it. With Insert, it gets levels of its own from ZIndex up (see ProjectEditor.Reorder).
public sealed record ReorderLayerOperation(string LayerId, int ZIndex, bool Insert = false) : EditOperation
{
    public override string Label => $"reorder_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) => ProjectEditor.Reorder(project, LayerId, ZIndex, Insert);
}

// The project's length, as update_project sets it. Shown is the length the field showed: when
// another program changed it since, it isn't set over that change.
public sealed record SetDurationOperation(double Duration, double Shown) : EditOperation
{
    public override string Label => "update_project";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        if (project.Duration != Shown)
        {
            throw new ZeyoCutException($"another program changed the project's length to {Num(project.Duration)}s first");
        }

        return string.Join("; ", ProjectEditor.UpdateProject(project, new ProjectOptions { Duration = Duration }).Changes);
    }
}

// Puts layers that never show at the same time on shared draw levels (ProjectEditor.PackLanes).
public sealed record PackLanesOperation : EditOperation
{
    public override string Label => "pack_lanes";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project) => ProjectEditor.PackLanes(project);
}

// A clip of an asset starting at Start, as add_video_layer, add_image_layer and add_audio_layer add
// one: footage fitted to the canvas, a still image for StillLength seconds (made smaller to fit the
// canvas when it's bigger), or sound with Role (music by default). Footage and stills go on draw
// level ZIndex, or on a level of their own on top when it's null. Footage and stills that run past
// the end make the project longer, as add_video_layer does; sound doesn't. TrimIn and TrimOut pick
// the part of footage or sound to use, Muted leaves footage's sound out, and Edit makes room for the
// clip as those tools' edit does (TimelineEditor.Place).
public sealed record AddClipOperation(
    AssetType Type,
    string AssetId,
    double Start,
    int? ZIndex = null,
    string? Role = null,
    double? TrimIn = null,
    double? TrimOut = null,
    bool Muted = false,
    ClipEdit Edit = ClipEdit.Overlay) : EditOperation
{
    public const double StillLength = 5;

    public override string Label => $"add_{EnumNames.ToName(Type)}_layer {AssetId}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        Layer layer;
        var before = project.Duration;
        switch (Type)
        {
            case AssetType.Video:
                layer = ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = AssetId, Start = Start, ZIndex = ZIndex, TrimIn = TrimIn, TrimOut = TrimOut, Muted = Muted });
                break;
            case AssetType.Image:
                var asset = project.FindAsset(AssetId);
                var fit = asset is { Width: > 0, Height: > 0 } ? Math.Min(1, Math.Min(project.Width / (double)asset.Width.Value, project.Height / (double)asset.Height.Value)) : 1;
                layer = ProjectEditor.AddImageLayer(project, new ImageLayerOptions
                {
                    Drawings = new Dictionary<string, string> { ["normal"] = AssetId },
                    Start = Start,
                    End = Start + StillLength,
                    ScaleX = fit,
                    ScaleY = fit,
                    ZIndex = ZIndex,
                });
                ProjectEditor.ExtendToFit(project, Start + StillLength);
                break;
            case AssetType.Audio:
                layer = ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = AssetId, Start = Start, Role = Role, TrimIn = TrimIn, TrimOut = TrimOut });
                break;
            default:
                throw new ZeyoCutException($"'{AssetId}' is a {EnumNames.ToName(Type)}: only video, images and sound go on the timeline");
        }

        var placed = TimelineEditor.Place(project, layer, Edit, before);
        var level = layer is VisualLayer visual ? $" on draw level {visual.ZIndex}" : layer is AudioLayer audio ? $" as {EnumNames.ToName(audio.Role)}" : "";
        var verb = Edit == ClipEdit.Insert ? "inserted" : "added";
        return $"{verb} {layer.TypeName} layer '{layer.Id}' at {Num(Start)}s{level}{(placed.Length > 0 ? $"; {placed}" : "")}";
    }
}

// A file AssetImporter.Prepare copied into the project, added as an asset, as import_asset and
// import_video add it. A normalized video names its original and why it was normalized.
public sealed record AddAssetOperation(PreparedAsset Asset, string? Id = null, string? OriginalFile = null, string? Normalized = null) : EditOperation
{
    public override string Label => $"{(Asset.Type == AssetType.Video ? "import_video" : "import_asset")} {Path.GetFileName(OriginalFile ?? Asset.Source)}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project)
    {
        var added = AssetImporter.Add(project, Asset, Id, OriginalFile, Normalized);
        return $"imported '{added.Id}' ({EnumNames.ToName(added.Type)}, {added.File})";
    }
}

// A copy of a layer and its children, as duplicate_layer makes it by default. The copied layer is
// the first in the summary.
public sealed record DuplicateLayerOperation(string LayerId) : EditOperation
{
    public override string Label => $"duplicate_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project) =>
        "copied " + string.Join(", ", ProjectEditor.DuplicateLayer(project, new DuplicateOptions { LayerId = LayerId }).Select(kv => $"'{kv.Key}' -> '{kv.Value}'"));
}

// Tags added to assets and taken off them, as tag_assets does.
public sealed record TagAssetsOperation(IReadOnlyList<string> AssetIds, IReadOnlyList<string> Add, IReadOnlyList<string> Remove) : EditOperation
{
    public override string Label => $"tag_assets {string.Join(" ", AssetIds)}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project) => AssetTags.Tag(project, AssetIds, Add, Remove);
}

// An asset pointed at a file Prepare copied into assets\, as relink_asset does: Relink.
public sealed record RelinkAssetOperation(string AssetId, PreparedAsset Asset) : EditOperation
{
    public override string Label => $"relink_asset {AssetId}";

    public override IReadOnlyList<string> Layers => [];

    public override string Apply(Project project) => AssetRelinker.Relink(project, AssetId, Asset).Summary;
}

// A caption added to a track at Start–End, styled like the caption before it (CaptionTracks.AddCue);
// Empty gives a track without captions its look. Measurer measures text in the project it's applied to.
public sealed record AddCaptionOperation(string GroupId, double Start, double End, string Text, CaptionOptions? Empty = null, Func<Project, ITextMeasurer>? Measurer = null) : EditOperation
{
    public override string Label => $"add_caption {GroupId}";

    public override IReadOnlyList<string> Layers => [GroupId];

    public override string Apply(Project project)
    {
        var cue = CaptionTracks.AddCue(project, GroupId, Start, End, Text, Empty ?? new CaptionOptions(), Measurer?.Invoke(project));
        return $"added caption '{cue.Id}' to '{GroupId}' at {Num(cue.Start)}–{Num(cue.End ?? project.Duration)}s";
    }
}

// A caption's text, its anchor kept at the same place in the text box (CaptionTracks.SetText).
public sealed record CaptionTextOperation(string LayerId, string Text, Func<Project, ITextMeasurer>? Measurer = null) : EditOperation
{
    public override string Label => $"update_layer {LayerId}";

    public override IReadOnlyList<string> Layers => [LayerId];

    public override string Apply(Project project)
    {
        CaptionTracks.SetText(project, LayerId, Text, Measurer?.Invoke(project));
        return $"updated the text of caption '{LayerId}'";
    }
}

// The same caption in several languages given a new start, a new end or both, as trim_layer trims
// each (CaptionTracks.Retime), as one step.
public sealed record RetimeCaptionsOperation(IReadOnlyList<string> LayerIds, double? Start, double? End) : EditOperation
{
    public override string Label => $"trim_layer {string.Join(", ", LayerIds)}";

    public override IReadOnlyList<string> Layers => LayerIds;

    public override string Apply(Project project) => string.Join("; ", CaptionTracks.Retime(project, LayerIds, Start, End));
}

// The style of the captions of caption tracks, as one step (CaptionTracks.Restyle).
public sealed record RestyleCaptionsOperation(IReadOnlyList<string> GroupIds, CaptionRestyle Change, Func<Project, ITextMeasurer>? Measurer = null) : EditOperation
{
    public override string Label => $"restyle_captions {string.Join(", ", GroupIds)}";

    public override IReadOnlyList<string> Layers => GroupIds;

    public override string Apply(Project project)
    {
        var count = CaptionTracks.Restyle(project, GroupIds, Change, Measurer?.Invoke(project));
        return $"restyled {count} caption(s) in {string.Join(", ", GroupIds.Select(id => $"'{id}'"))}: {CaptionTracks.Describe(Change)}";
    }
}

// An .srt file's captions as their language's caption track, replacing that language's track, as
// import_srt imports them by default: the subtitle style in FontFamily (the language's font).
public sealed record ImportSrtOperation(string FileName, string Language, IReadOnlyList<CueEntry> Cues, IReadOnlyList<string> Notes, string? FontFamily, Func<Project, ITextMeasurer>? Measurer = null) : EditOperation
{
    public override string Label => $"import_srt {FileName}";

    public override IReadOnlyList<string> Layers => [CaptionTracks.GroupId(Language)];

    public override string Apply(Project project)
    {
        var track = CaptionTracks.AddEntries(project, Language, Cues, new CaptionOptions { FontFamily = FontFamily }, Notes, Measurer?.Invoke(project));
        return $"{(track.Created ? "added" : "replaced")} the {Language} captions '{track.GroupId}': {track.Cues.Count} caption(s) from {FileName}"
            + string.Concat(track.Problems.Select(p => $"; {p}"));
    }
}

// Several operations as one undo step, such as a drag to another lane (move and reorder). They run
// in order; if one fails, none is kept.
public sealed record BatchOperation(string Name, IReadOnlyList<EditOperation> Operations) : EditOperation
{
    public override string Label => Name;

    public override IReadOnlyList<string> Layers => Operations.SelectMany(o => o.Layers).Distinct(StringComparer.Ordinal).ToList();

    public override string Apply(Project project)
    {
        if (Operations.Count == 0)
        {
            throw new ZeyoCutException("the batch has no operations");
        }

        return string.Join("; ", Operations.Select(o => o.Apply(project)));
    }
}
