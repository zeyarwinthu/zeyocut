// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public sealed record TimedWord(int Index, string Text, double Start, double End);

public sealed record CaptionCue(double Start, double End, IReadOnlyList<string> Lines, int FirstWord, int LastWord)
{
    public string Text => string.Join("\n", Lines);
}

// generate_captions behindSubject: off, on (the captions go behind the subject of the clip), or auto
// (also in front wherever the subject would cover too much of a caption).
public enum BehindSubject
{
    Off,
    On,
    Auto
}

public sealed record CaptionOptions
{
    public string? AssetId { get; init; }
    public string? LayerId { get; init; }
    public string Style { get; init; } = "subtitle";
    public int MaxWordsPerLine { get; init; } = 6;
    public int MaxLines { get; init; } = 2;
    public string? FontFamily { get; init; }
    public double? FontSize { get; init; }
    public string? Color { get; init; }
    public string GroupId { get; init; } = CaptionBuilder.DefaultGroup;
    public BehindSubject BehindSubject { get; init; }

    // A library style used instead of a built-in one, and its font as a project asset.
    public TextStyle? LibraryStyle { get; init; }
    public string? FontAsset { get; init; }
}

// Subject: what behindSubject did (on or auto); Removed: the note when off took the captions' cutout away.
public sealed record CaptionResult(string GroupId, IReadOnlyList<CaptionCue> Cues, IReadOnlyList<string> LayerIds, int Words, int Replaced, bool CreatedGroup, CaptionSubject? Subject = null, string? Removed = null);

// One frame of a caption measured for behindSubject=auto: the share of it the subject covers, 0-1.
public sealed record CaptionCover(string LayerId, int Frame, double Covered);

// Where the captions show in front of the subject (its cutout faded out): the captions covered too
// much in it and the most any of them was covered.
public sealed record FrontSpan(double Start, double End, IReadOnlyList<string> LayerIds, double Covered);

// Captions are text layers under a null layer ('captions'), timed from the transcript through the
// clips that play it, so cuts made before generating them are followed.
public static class CaptionBuilder
{
    public const string DefaultGroup = "captions";
    public const double Pause = 0.6;
    public const double Hold = 0.3;
    public const double MinShow = 0.5;
    public static readonly string[] Styles = ["subtitle", "bold_center", "minimal"];

    // behindSubject=auto: in front where the subject covers more than this percent of a caption; the
    // cutout fades over FadeSeconds, finished before such a span starts, and spans closer than
    // MergeGap join so the subject doesn't flicker.
    public const double DefaultOcclusionThreshold = 35;
    public const double FadeSeconds = 0.25;
    public const double MergeGap = 0.5;

    // The source layers: one layer, or every video/audio layer playing the asset.
    public static (Asset Asset, List<Layer> Layers) Sources(Project project, string? assetId, string? layerId)
    {
        if (string.IsNullOrWhiteSpace(assetId) == string.IsNullOrWhiteSpace(layerId))
        {
            throw new ZeyoCutException("give either assetId (captions for every clip of that asset) or layerId (one clip)");
        }

        if (!string.IsNullOrWhiteSpace(layerId))
        {
            var (layer, asset) = SilenceCutter.MediaLayer(project, layerId);
            return (asset, [layer]);
        }

        var source = project.FindAsset(assetId!) ?? throw new ZeyoCutException($"asset '{assetId}' not found; assets: {Ids.List(project.Assets.Select(a => a.Id))}");
        var layers = project.Layers
            .Where(l => l.Visible && (l is VideoLayer v && v.Asset == source.Id || l is AudioLayer a && a.Asset == source.Id))
            .ToList();
        if (layers.Count == 0)
        {
            throw new ZeyoCutException($"no layer plays asset '{source.Id}'; add it with add_video_layer or add_audio_layer first");
        }

        return (source, layers);
    }

    // Transcript words placed on the timeline through each layer's source range; words that were cut
    // out (or that are mostly outside every layer) are left out.
    public static List<TimedWord> TimelineWords(Project project, Transcript transcript, IEnumerable<Layer> layers)
    {
        var timed = new List<TimedWord>();
        foreach (var layer in layers)
        {
            var range = SilenceCutter.SourceRange(project, layer);
            for (var i = 0; i < transcript.Words.Count; i++)
            {
                var word = transcript.Words[i];
                var start = Math.Max(word.Start, range.Start);
                var end = Math.Min(word.End, range.End);
                var length = word.End - word.Start;
                if (length <= 0)
                {
                    // whisper gives some short words no duration; such a word is on the clip when its instant is.
                    if (word.Start < range.Start || word.Start >= range.End)
                    {
                        continue;
                    }

                    (start, end) = (word.Start, word.Start);
                }
                else if (end <= start || end - start < length / 2)
                {
                    continue;
                }

                var t0 = SilenceCutter.ToTimeline(layer, start);
                var t1 = Math.Min(SilenceCutter.ToTimeline(layer, end), project.Duration);
                if (t0 < project.Duration && t1 >= t0)
                {
                    timed.Add(new TimedWord(i, word.Text, Math.Round(t0, 6), Math.Round(t1, 6)));
                }
            }
        }

        return timed.OrderBy(w => w.Start).ThenBy(w => w.Index).ToList();
    }

    // Cues of up to maxLines balanced lines; a new cue starts after a sentence end or a pause.
    public static List<CaptionCue> Cues(IReadOnlyList<TimedWord> words, int maxWordsPerLine, int maxLines, double projectDuration)
    {
        if (maxWordsPerLine is < 1 or > 20)
        {
            throw new ZeyoCutException($"maxWordsPerLine must be between 1 and 20 (got {maxWordsPerLine})");
        }

        if (maxLines is < 1 or > 4)
        {
            throw new ZeyoCutException($"maxLines must be between 1 and 4 (got {maxLines})");
        }

        var groups = new List<List<TimedWord>>();
        var current = new List<TimedWord>();
        foreach (var word in words)
        {
            if (current.Count > 0
                && (current.Count >= maxWordsPerLine * maxLines
                    || word.Start - current[^1].End >= Pause
                    || AnalysisFiles.EndsSentence(current[^1].Text)))
            {
                groups.Add(current);
                current = [];
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        var cues = new List<CaptionCue>();
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var lineCount = (group.Count + maxWordsPerLine - 1) / maxWordsPerLine;
            var lines = new List<string>();
            var taken = 0;
            for (var l = 0; l < lineCount; l++)
            {
                var size = (group.Count - taken + (lineCount - l) - 1) / (lineCount - l);
                lines.Add(string.Join(" ", group.Skip(taken).Take(size).Select(w => w.Text)));
                taken += size;
            }

            var start = group[0].Start;
            var next = g + 1 < groups.Count ? groups[g + 1][0].Start : projectDuration;
            var end = Math.Min(Math.Max(group[^1].End + Hold, start + MinShow), next);
            end = Math.Max(Math.Min(end, projectDuration), Math.Min(group[^1].End, projectDuration));
            cues.Add(new CaptionCue(start, end, lines, group[0].Index, group[^1].Index));
        }

        return cues;
    }

    public static CaptionResult Generate(Project project, Transcript transcript, CaptionOptions options, ITextMeasurer? measurer)
    {
        StyleKey(options);
        var (asset, layers) = Sources(project, options.AssetId, options.LayerId);
        var clips = layers.OfType<VideoLayer>().ToList();
        if (options.BehindSubject != BehindSubject.Off && clips.Count == 0)
        {
            throw new ZeyoCutException(
                $"behindSubject needs a video clip to cut the subject from, and {(options.LayerId is null ? $"'{asset.Id}' only plays on audio layers" : $"'{layers[0].Id}' is an audio layer")}; generate the captions with behindSubject=off, then put them behind a clip with text_behind_subject (textLayerIds [\"{options.GroupId}\"])");
        }

        var words = TimelineWords(project, transcript, layers);
        if (words.Count == 0)
        {
            throw new ZeyoCutException("none of the transcript's words are on the timeline (the clips play other parts of the source)");
        }

        var cues = Cues(words, options.MaxWordsPerLine, options.MaxLines, project.Duration);
        var (groupId, ids, replaced, created) = Place(project, cues, options, measurer);
        if (options.BehindSubject == BehindSubject.Off)
        {
            return new CaptionResult(groupId, cues, ids, words.Count, replaced, created, null, SubjectEditor.RemoveCaptionCutout(project, groupId));
        }

        var subject = SubjectEditor.CaptionsBehind(project, groupId, asset.Id, clips);
        return new CaptionResult(groupId, cues, ids, words.Count, replaced, created, subject);
    }

    // Puts the cues in as text layers under the caption group options.GroupId, which is made when
    // it is missing; the group's earlier captions are replaced.
    public static (string GroupId, List<string> LayerIds, int Replaced, bool Created) Place(Project project, IReadOnlyList<CaptionCue> cues, CaptionOptions options, ITextMeasurer? measurer)
    {
        var style = StyleKey(options);
        Ids.EnsureValid(options.GroupId, "layer");

        var replaced = 0;
        var group = project.FindLayer(options.GroupId);
        var created = group is null;
        if (group is null)
        {
            var top = project.Layers.Where(l => l is VisualLayer or CutoutLayer).Select(l => l.ZIndex).DefaultIfEmpty(0).Max() + 10;
            var newGroup = ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = options.GroupId, Name = "captions", X = 0, Y = 0, ZIndex = top });
            newGroup.IgnoreCamera = true;
            group = newGroup;
        }
        else if (group is not ImageLayer { Drawings.Count: 0 })
        {
            throw new ZeyoCutException($"layer '{options.GroupId}' exists and is not a caption group (a null layer); pass another groupId");
        }
        else
        {
            var old = ProjectEditor.Subtree(project, group.Id).Where(l => l.Id != group.Id).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            replaced = old.Count;
            project.Layers.RemoveAll(l => old.Contains(l.Id));
        }

        var spec = Spec(project, style, options);
        var zIndex = ((VisualLayer)group).ZIndex;
        var ids = new List<string>();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var id = Ids.Unique($"{group.Id}_{i + 1}", candidate => project.FindLayer(candidate) is not null);
            var label = cue.Text.Replace('\n', ' ');
            var layer = ProjectEditor.AddTextLayer(project, new TextLayerOptions
            {
                Id = id,
                Name = label.Length > 40 ? label[..40] : label,
                ParentId = group.Id,
                Text = cue.Text,
                FontFamily = options.FontFamily ?? options.LibraryStyle?.FontFamily,
                FontAsset = options.FontFamily is null ? options.FontAsset : null,
                FontSize = spec.Size,
                Bold = spec.Bold,
                Color = options.Color ?? options.LibraryStyle?.Color ?? "#FFFFFF",
                StrokeColor = spec.Stroke,
                StrokeWidth = spec.Stroke is null ? 0 : spec.StrokeWidth,
                Align = "center",
                MaxWidth = spec.MaxWidth,
                LineHeight = spec.LineHeight,
                Box = spec.Box,
                X = project.Width / 2.0,
                Y = spec.Y,
                ZIndex = zIndex,
                Start = cue.Start,
                End = cue.End,
            }, measurer);
            if (measurer is null)
            {
                layer.AnchorX = spec.MaxWidth / 2;
                layer.AnchorY = cue.Lines.Count * spec.Size * 1.2 * spec.LineHeight / 2;
            }

            if (spec.Bottom)
            {
                layer.AnchorY *= 2;
            }

            ids.Add(id);
        }

        return (group.Id, ids, replaced, created);
    }

    // The built-in style's key (subtitle, bold_center, minimal), checked unless a library style is used.
    private static string StyleKey(CaptionOptions options)
    {
        var style = options.Style.Trim().ToLowerInvariant().Replace('-', '_');
        if (options.LibraryStyle is null && !Styles.Contains(style))
        {
            throw new ZeyoCutException($"unknown caption style '{options.Style}'; use {string.Join(", ", Styles)}");
        }

        return style;
    }

    // behindSubject=auto, once the captions were measured with the subject over them: fades the
    // captions' cutout out where the subject covers more than threshold percent of a caption, or, for
    // a subject that is its own layer, draws those captions in front of it. Returns the reply lines.
    public static List<string> Auto(Project project, CaptionSubject subject, IReadOnlyList<CaptionCover> measured, double threshold)
    {
        var limit = threshold / 100;
        var most = measured.MaxBy(m => m.Covered);
        if (most is null)
        {
            return ["auto: the captions aren't shown in any frame, so there was nothing to measure"];
        }

        var found = $"the subject covers up to {Pct(most.Covered)} of a caption ('{most.LayerId}' at {Num(FrameMath.TimeOf(project, most.Frame))}s; {measured.Count} frames measured)";
        var over = measured.Where(m => m.Covered > limit).ToList();
        if (subject.Change.Cutout?.Id is { } cutoutId)
        {
            var cutout = (CutoutLayer)ProjectEditor.GetLayer(project, cutoutId);
            var spans = FrontSpans(over, project.Fps, project.Duration);
            Fade(cutout, spans, project.Duration);
            if (spans.Count == 0)
            {
                return [$"auto: {found}, not more than {Num(threshold)}%, so the captions stay behind it the whole time"];
            }

            var lines = new List<string>
            {
                $"auto: {found}; where it covers more than {Num(threshold)}%, the captions show in front: '{cutout.Id}' fades out over {Num(FadeSeconds)}s before each of these spans and back in after",
            };
            lines.AddRange(spans.Select(s => $"in front {Num(s.Start)}–{Num(s.End)}s ({string.Join(", ", s.LayerIds.Select(id => $"'{id}'"))}, up to {Pct(s.Covered)})"));
            return lines;
        }

        var front = over.GroupBy(m => m.LayerId)
            .Select(g => (Id: g.Key, Covered: g.Max(m => m.Covered), Frame: g.Min(m => m.Frame)))
            .OrderBy(f => f.Frame)
            .ToList();
        var shown = string.Join(", ", subject.Cover.Select(id => $"'{id}'"));
        if (front.Count == 0)
        {
            return [$"auto: {found}, not more than {Num(threshold)}%, so the captions stay behind {shown}"];
        }

        SubjectEditor.InFront(project, front.Select(f => f.Id).ToList(), subject.Cover);
        var names = string.Join(", ", front.Select(f => $"'{f.Id}' ({Pct(f.Covered)})"));
        return [$"auto: {found}; {names} would be covered more than {Num(threshold)}%, so {(front.Count == 1 ? "it draws" : "they draw")} in front of {shown} (a caption can't fade there, since the subject is its own layer); the other captions stay behind it"];
    }

    // The spans where some caption is covered more than the threshold: runs of such frames, joined
    // when less than MergeGap apart.
    public static List<FrontSpan> FrontSpans(IReadOnlyList<CaptionCover> over, int fps, double duration)
    {
        var spans = new List<FrontSpan>();
        var frames = over.GroupBy(m => m.Frame).OrderBy(g => g.Key).ToList();
        var i = 0;
        while (i < frames.Count)
        {
            var j = i;
            while (j + 1 < frames.Count && frames[j + 1].Key == frames[j].Key + 1)
            {
                j++;
            }

            var items = frames.Skip(i).Take(j - i + 1).SelectMany(g => g).ToList();
            var start = Math.Round(FrameMath.TimeOf(frames[i].Key, fps), 6);
            var end = Math.Round(Math.Min(duration, FrameMath.TimeOf(frames[j].Key + 1, fps)), 6);
            var ids = items.Select(m => m.LayerId).Distinct(StringComparer.Ordinal).ToList();
            var covered = items.Max(m => m.Covered);
            if (spans.Count > 0 && start - spans[^1].End < MergeGap - 1e-9)
            {
                var last = spans[^1];
                spans[^1] = new FrontSpan(last.Start, end, last.LayerIds.Concat(ids).Distinct(StringComparer.Ordinal).ToList(), Math.Max(last.Covered, covered));
            }
            else
            {
                spans.Add(new FrontSpan(start, end, ids, covered));
            }

            i = j + 1;
        }

        return spans;
    }

    // Opacity keyframes on the captions' cutout: it fades out over FadeSeconds, finished when a span
    // starts, and back in after it ends. A span that starts sooner than that after 0 is in front from
    // the start; one that ends less than that before the end stays in front to the end.
    public static void Fade(CutoutLayer cutout, IReadOnlyList<FrontSpan> spans, double duration)
    {
        var value = cutout.Opacity.Value;
        var keys = new List<Keyframe>();
        foreach (var span in spans)
        {
            if (span.Start < FadeSeconds - 1e-9)
            {
                keys.Add(new Keyframe { Time = 0, Value = 0 });
            }
            else
            {
                keys.Add(new Keyframe { Time = Math.Round(span.Start - FadeSeconds, 6), Value = value });
                keys.Add(new Keyframe { Time = span.Start, Value = 0 });
            }

            if (span.End + FadeSeconds <= duration + 1e-9)
            {
                keys.Add(new Keyframe { Time = span.End, Value = 0 });
                keys.Add(new Keyframe { Time = Math.Round(span.End + FadeSeconds, 6), Value = value });
            }
        }

        cutout.Opacity.Keyframes = keys;
    }

    // Cues from the text layers under a caption group, for export_srt.
    public static List<(double Start, double End, string Text)> GroupCues(Project project, string groupId)
    {
        var group = ProjectEditor.GetLayer(project, groupId);
        var cues = ProjectEditor.Subtree(project, group.Id)
            .OfType<TextLayer>()
            .Where(t => t.Visible)
            .Select(t => (t.Start, End: Math.Min(t.End ?? project.Duration, project.Duration), t.Text))
            .Where(c => c.End > c.Start)
            .OrderBy(c => c.Start)
            .ToList();
        if (cues.Count == 0)
        {
            throw new ZeyoCutException($"'{groupId}' has no caption text layers; run generate_captions first");
        }

        return cues;
    }

    public static string Srt(IReadOnlyList<(double Start, double End, string Text)> cues) =>
        string.Concat(cues.Select((c, i) => $"{i + 1}\r\n{AnalysisFiles.SrtTime(c.Start)} --> {AnalysisFiles.SrtTime(c.End)}\r\n{c.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")}\r\n\r\n"));

    private sealed record StyleSpec(double Size, bool Bold, string? Stroke, double StrokeWidth, TextBackground? Box, double Y, bool Bottom, double LineHeight, double MaxWidth);

    private static StyleSpec Spec(Project project, string style, CaptionOptions options)
    {
        double scale = Math.Min(project.Width, project.Height);
        var width = project.Width * 0.86;
        double Size(double fraction) => options.FontSize ?? Math.Round(scale * fraction);
        var centerY = project.Height * (project.Height > project.Width ? 0.62 : 0.5);
        if (options.LibraryStyle is { } library)
        {
            var factor = TextStyles.Scale(project, library);
            var size = options.FontSize ?? Math.Round(library.FontSize * factor, 1);
            var box = library.Box is { } b ? new TextBackground { Color = b.Color, Padding = Math.Round(b.Padding * factor, 1), CornerRadius = Math.Round(b.CornerRadius * factor, 1) } : null;
            var center = library.CaptionPosition == CaptionPosition.Center;
            var stroke = library.StrokeWidth > 0 ? library.StrokeColor : null;
            return new StyleSpec(size, library.Bold, stroke, Math.Round(library.StrokeWidth * factor, 1), box, center ? centerY : project.Height * 0.93, !center, library.LineHeight, width);
        }

        switch (style)
        {
            case "bold_center":
                var bold = Size(0.07);
                return new StyleSpec(bold, true, "#000000", Math.Round(bold * 0.1, 1), null, centerY, false, 1.05, width);
            case "minimal":
                var small = Size(0.04);
                return new StyleSpec(small, false, "#000000", Math.Round(small * 0.06, 1), null, project.Height * 0.95, true, 1.1, width);
            default:
                var size = Size(0.045);
                var box = new TextBackground { Color = "#000000B3", Padding = Math.Round(size * 0.3), CornerRadius = Math.Round(size * 0.2) };
                return new StyleSpec(size, false, null, 0, box, project.Height * 0.93, true, 1.1, width);
        }
    }

    public static string Describe(CaptionResult result, int listed = 12)
    {
        var lines = new List<string>
        {
            $"{(result.CreatedGroup ? "added" : "replaced")} captions under '{result.GroupId}': {result.Cues.Count} cue(s) from {result.Words} words{(result.Replaced > 0 ? $" (removed {result.Replaced} old caption layer(s))" : "")}",
        };
        foreach (var cue in result.Cues.Take(listed))
        {
            lines.Add($"- {Num(cue.Start)}–{Num(cue.End)}s \"{cue.Text.Replace("\n", " / ")}\"");
        }

        if (result.Cues.Count > listed)
        {
            lines.Add($"- ... and {result.Cues.Count - listed} more");
        }

        lines.Add("captions don't follow later cuts; run generate_captions again after editing (it replaces them)");
        if (result.Removed is { } removed)
        {
            lines.Add(removed);
        }

        if (result.Subject is { } subject)
        {
            lines.Add(subject.Change.Summary);
            lines.AddRange(subject.Change.Notes.Select(n => "- " + n));
        }

        return string.Join("\n", lines);
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Pct(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
