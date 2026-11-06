// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

// One timeline frame of the sound that drives a mouth: the source seconds it plays during that frame.
public sealed record SoundWindow(int Frame, double SourceStart, double SourceEnd);

// What auto_lip_flap measures: the frames where both the sound and the mouth play.
public sealed record LipFlapPlan(string SourceId, Asset Asset, double Start, double End, IReadOnlyList<SoundWindow> Windows, IReadOnlyList<string> Warnings);

public sealed record LipFlapResult(
    string MouthId,
    string SourceId,
    double Start,
    double End,
    int Frames,
    int Keyframes,
    IReadOnlyList<(string Drawing, int Frames)> Use,
    string Closed,
    double ThresholdDb,
    double Sensitivity,
    IReadOnlyList<string> Warnings)
{
    public string Describe()
    {
        var closed = Use.Where(u => u.Drawing == Closed).Sum(u => u.Frames);
        var open = Use.Where(u => u.Drawing != Closed).ToList();
        var opened = open.Sum(u => u.Frames);
        var detail = open.Count > 1 ? $" ({string.Join(", ", open.Select(u => $"{u.Drawing} {u.Frames}"))})" : open[0].Drawing != "open" ? $" ('{open[0].Drawing}')" : "";
        var rest = Closed != "closed" ? $" ('{Closed}')" : "";
        var threshold = double.IsFinite(ThresholdDb) ? $"the mouth opens above {Db(ThresholdDb)} dBFS at sensitivity {Num(Sensitivity)}" : "the sound is silent";
        return $"lip flap on '{MouthId}' from '{SourceId}' at {Num(Start)}–{Num(End)}s ({Frames} frames): open {opened} frames{detail}, closed {closed}{rest}; "
            + $"{Keyframes} keyframe(s) on {MouthId}.drawing ({threshold}; every drawing holds at least {LipFlap.MinHold} frames)";
    }

    private static string Db(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

// auto_lip_flap: quiet frames get the closed drawing, the other frames step through the open drawings
// by loudness (each drawing about the same share, the loudest frames the widest), and every drawing
// holds at least MinHold frames so the mouth doesn't flicker.
public static class LipFlap
{
    public const int MinHold = 2;
    public const int MaxOpenDrawings = 8;
    public const double DefaultSensitivity = 0.5;

    // Below this the whole sound counts as silent and the mouth stays closed.
    public const double SilentDb = -60;

    // The quiet level never counts as lower than this, and the loud parts are at least MinRangeDb above it.
    public const double FloorDb = -80;
    public const double MinRangeDb = 12;

    public static LipFlapPlan Plan(Project project, string mouthLayerId, string audioLayerId, string closedDrawing, IReadOnlyList<string> openDrawings, double sensitivity)
    {
        var mouth = Mouth(project, mouthLayerId, closedDrawing, openDrawings, sensitivity);
        var source = ProjectEditor.GetLayer(project, audioLayerId, "audio layer");
        var warnings = new List<string>();
        Asset asset;
        double soundEnd;
        switch (source)
        {
            case AudioLayer audio:
                asset = ProjectEditor.GetAsset(project, audio.Asset, AssetType.Audio);
                var sourceEnd = audio.TrimOut ?? asset.Duration ?? double.PositiveInfinity;
                if (asset.Duration is { } length)
                {
                    sourceEnd = Math.Min(sourceEnd, length);
                }

                soundEnd = Math.Min(audio.Start + (sourceEnd - audio.TrimIn), audio.End ?? double.PositiveInfinity);
                break;
            case VideoLayer video:
                asset = ProjectEditor.GetAsset(project, video.Asset, AssetType.Video);
                if (asset.HasAudio != true)
                {
                    throw new ZeyoCutException($"video layer '{video.Id}' has no sound ('{asset.Id}' has no audio track); pick an audio layer or a video layer with sound");
                }

                if (video.Muted)
                {
                    warnings.Add($"'{video.Id}' is muted, so the mouth moves to a sound the export doesn't play");
                }

                soundEnd = video.TimelineEnd;
                break;
            default:
                throw new ZeyoCutException($"'{audioLayerId}' is a {source.TypeName} layer; auto_lip_flap needs an audio layer or a video layer with sound");
        }

        var start = Math.Max(source.Start, mouth.Start);
        var end = Math.Min(Math.Min(soundEnd, SceneEvaluator.EndOf(mouth, project.Duration)), project.Duration);
        if (end - start < 1.0 / project.Fps)
        {
            throw new ZeyoCutException($"'{source.Id}' ({Num(source.Start)}–{Num(soundEnd)}s) and '{mouth.Id}' ({Num(mouth.Start)}–{Num(SceneEvaluator.EndOf(mouth, project.Duration))}s) don't play together for a whole frame within the project (0–{Num(project.Duration)}s)");
        }

        if (!source.Visible)
        {
            warnings.Add($"'{source.Id}' is hidden, so the export doesn't play it");
        }

        var windows = new List<SoundWindow>();
        for (var frame = (int)Math.Ceiling(start * project.Fps - 1e-9); FrameMath.TimeOf(project, frame) < end - 1e-9; frame++)
        {
            var from = FrameMath.TimeOf(project, frame);
            var to = Math.Min(FrameMath.TimeOf(project, frame + 1), end);
            windows.Add(new SoundWindow(frame, SourceTime(source, from), SourceTime(source, to)));
        }

        return new LipFlapPlan(source.Id, asset, FrameMath.TimeOf(project, windows[0].Frame), end, windows, warnings);
    }

    // The drawing for each frame, from its loudness in dBFS, before the minimum hold.
    public static List<string> Shapes(IReadOnlyList<double> levelsDb, string closedDrawing, IReadOnlyList<string> openDrawings, double sensitivity, out double thresholdDb)
    {
        var levels = levelsDb.Select(l => double.IsFinite(l) ? Math.Max(l, -120) : -120).ToList();
        var sorted = levels.Order().ToList();
        var loud = sorted.Count == 0 ? -120 : Percentile(sorted, 0.95);
        if (loud < SilentDb)
        {
            thresholdDb = double.NaN;
            return [.. levels.Select(_ => closedDrawing)];
        }

        var floor = Math.Min(Math.Max(Percentile(sorted, 0.10), FloorDb), loud - MinRangeDb);
        var threshold = floor + (loud - floor) * (1 - sensitivity);
        thresholdDb = threshold;

        // The open frames are shared about evenly among the open drawings, the loudest getting the widest.
        var open = sorted.Where(l => l > threshold).ToList();
        var edges = Enumerable.Range(1, openDrawings.Count - 1)
            .Select(j => open.Count == 0 ? double.PositiveInfinity : open[Math.Min((int)Math.Ceiling(j * open.Count / (double)openDrawings.Count), open.Count - 1)])
            .ToList();
        return [.. levels.Select(level => level <= threshold ? closedDrawing : openDrawings[edges.Count(edge => level >= edge)])];
    }

    // Every run of the same drawing lasts at least MinHold frames (except a last run cut by the end).
    public static List<string> Hold(IReadOnlyList<string> shapes)
    {
        var held = new List<string>(shapes.Count);
        var i = 0;
        while (i < shapes.Count)
        {
            var drawing = shapes[i];
            var run = 0;
            while (i < shapes.Count && (shapes[i] == drawing || run < MinHold))
            {
                held.Add(drawing);
                i++;
                run++;
            }
        }

        return held;
    }

    // Writes hold keyframes on the mouth's drawing over the plan's frames, replacing the keys there.
    // The drawing shown before and after the sound stays as it was.
    public static LipFlapResult Apply(Project project, string mouthLayerId, string closedDrawing, IReadOnlyList<string> openDrawings, double sensitivity, LipFlapPlan plan, IReadOnlyList<double> levelsDb)
    {
        var mouth = Mouth(project, mouthLayerId, closedDrawing, openDrawings, sensitivity);
        if (levelsDb.Count != plan.Windows.Count)
        {
            throw new ZeyoCutException($"internal error: {levelsDb.Count} sound levels for {plan.Windows.Count} frames");
        }

        var held = Hold(Shapes(levelsDb, closedDrawing, openDrawings, sensitivity, out var threshold));
        var track = mouth.Drawing;
        var before = Interpolator.Evaluate(track, plan.Start - 1e-6);
        var after = Interpolator.Evaluate(track, plan.End);
        var hadKeyAtEnd = track.Keyframes.Any(k => Math.Abs(k.Time - plan.End) < 1e-9);
        track.Keyframes.RemoveAll(k => k.Time >= plan.Start - 1e-9 && k.Time < plan.End - 1e-9);
        var keys = new List<DrawingKeyframe>();
        if (plan.Start > mouth.Start + 1e-9 && !track.Keyframes.Any(k => k.Time < plan.Start) && before != held[0])
        {
            keys.Add(new DrawingKeyframe { Time = mouth.Start, Value = before });
        }

        for (var i = 0; i < held.Count; i++)
        {
            if (i == 0 || held[i] != held[i - 1])
            {
                keys.Add(new DrawingKeyframe { Time = FrameMath.TimeOf(project, plan.Windows[i].Frame), Value = held[i] });
            }
        }

        if (!hadKeyAtEnd && plan.End < SceneEvaluator.EndOf(mouth, project.Duration) - 1e-9 && plan.End < project.Duration - 1e-9 && after != held[^1])
        {
            keys.Add(new DrawingKeyframe { Time = plan.End, Value = after });
        }

        track.Keyframes.AddRange(keys);
        track.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        var warnings = plan.Warnings.ToList();
        if (!double.IsFinite(threshold))
        {
            warnings.Add($"the sound of '{plan.SourceId}' is silent at {Num(plan.Start)}–{Num(plan.End)}s (below {Num(SilentDb)} dBFS), so the mouth stays closed");
        }

        var use = new[] { closedDrawing }.Concat(openDrawings).Select(d => (d, held.Count(h => h == d))).ToList();
        return new LipFlapResult(mouth.Id, plan.SourceId, plan.Start, plan.End, held.Count, keys.Count, use, closedDrawing, threshold, sensitivity, warnings);
    }

    private static ImageLayer Mouth(Project project, string mouthLayerId, string closedDrawing, IReadOnlyList<string> openDrawings, double sensitivity)
    {
        if (!double.IsFinite(sensitivity) || sensitivity < 0 || sensitivity > 1)
        {
            throw new ZeyoCutException($"sensitivity must be between 0 (opens only on the loudest sounds) and 1 (opens on anything above the quiet parts); got {Num(sensitivity)}");
        }

        var layer = ProjectEditor.GetLayer(project, mouthLayerId, "mouth layer");
        if (layer is not ImageLayer mouth)
        {
            throw new ZeyoCutException($"'{mouthLayerId}' is a {layer.TypeName} layer; auto_lip_flap needs an image layer with mouth drawings");
        }

        var drawings = string.Join(", ", mouth.Drawings.Keys);
        if (string.IsNullOrWhiteSpace(closedDrawing) || !mouth.Drawings.ContainsKey(closedDrawing))
        {
            throw new ZeyoCutException($"'{mouth.Id}' has no drawing '{closedDrawing}' for the closed mouth; its drawings: {drawings}");
        }

        if (openDrawings is null || openDrawings.Count == 0 || openDrawings.Count > MaxOpenDrawings)
        {
            throw new ZeyoCutException($"give 1 to {MaxOpenDrawings} open drawings, from slightly to wide open; '{mouth.Id}' has: {drawings}");
        }

        foreach (var open in openDrawings)
        {
            if (string.IsNullOrWhiteSpace(open) || !mouth.Drawings.ContainsKey(open))
            {
                throw new ZeyoCutException($"'{mouth.Id}' has no drawing '{open}'; its drawings: {drawings}");
            }

            if (open == closedDrawing)
            {
                throw new ZeyoCutException($"'{open}' is the closed drawing; the open drawings must be other drawings");
            }
        }

        if (openDrawings.Distinct().Count() != openDrawings.Count)
        {
            throw new ZeyoCutException($"the open drawings repeat a drawing: {string.Join(", ", openDrawings)}");
        }

        return mouth;
    }

    private static double SourceTime(Layer layer, double time) => layer switch
    {
        VideoLayer video => VideoTiming.SourceTime(video, time),
        AudioLayer audio => audio.TrimIn + (time - audio.Start),
        _ => time,
    };

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Floor(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
