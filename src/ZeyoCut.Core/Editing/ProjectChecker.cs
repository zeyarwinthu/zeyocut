// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Geometry;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public enum CheckKind
{
    MissingFile,
    OutsideCanvas,
    OutsideSafeArea,
    MissingGlyphs,
    EmptyFrames,
    DeadKeyframes,
    UnusedAsset
}

public sealed record CheckIssue(CheckKind Kind, string Subject, string Message);

public sealed record CheckReport(string ProjectName, int Frames, IReadOnlyList<CheckIssue> Issues)
{
    public const int MaxLinesPerKind = 15;

    public string Describe()
    {
        if (Issues.Count == 0)
        {
            return $"check_project: no problems in '{ProjectName}' ({Frames} frames checked for missing files, layers off the canvas, text outside the safe area, glyphs a font lacks, empty frames, keyframes that never play and unused assets)";
        }

        var builder = new StringBuilder($"check_project: {Issues.Count} problem(s) in '{ProjectName}' ({Frames} frames checked)");
        foreach (var group in Issues.GroupBy(i => i.Kind))
        {
            var items = group.ToList();
            builder.Append($"\n{Title(group.Key)} ({items.Count}):");
            foreach (var issue in items.Take(MaxLinesPerKind))
            {
                builder.Append($"\n- {issue.Message}");
            }

            if (items.Count > MaxLinesPerKind)
            {
                builder.Append($"\n- … and {items.Count - MaxLinesPerKind} more");
            }
        }

        return builder.ToString();
    }

    private static string Title(CheckKind kind) => kind switch
    {
        CheckKind.MissingFile => "missing files",
        CheckKind.OutsideCanvas => "off the canvas",
        CheckKind.OutsideSafeArea => "text outside the safe area",
        CheckKind.MissingGlyphs => "glyphs a font lacks",
        CheckKind.EmptyFrames => "empty frames",
        CheckKind.DeadKeyframes => "keyframes that never play",
        _ => "unused assets",
    };
}

// check_project: problems you can find without rendering, frame by frame through the scene math.
public static class ProjectChecker
{
    // Title-safe margin on every side, as a share of the canvas.
    public const double SafeMargin = 0.05;

    // On vertical (Shorts) canvases the YouTube player covers these strips with its buttons and text.
    public const double ShortsTop = 0.08;
    public const double ShortsBottom = 0.25;
    public const double ShortsRight = 0.12;

    private const double Tolerance = 1e-6;
    private const double PixelSlack = 0.5;

    public static bool IsVertical(Project project) => project.Height > project.Width * 1.5;

    // fonts: the font for each language (appsettings.json LanguageFonts), suggested for its texts.
    public static CheckReport Check(Project project, string projectDirectory, ITextMeasurer? measurer, IReadOnlyDictionary<string, string>? fonts = null)
    {
        var issues = new List<CheckIssue>();
        MissingFiles(project, projectDirectory, issues);
        Layout(project, measurer, issues);
        Glyphs(project, measurer, fonts ?? Languages.DefaultFonts, issues);
        DeadKeyframes(project, issues);
        UnusedAssets(project, issues);
        return new CheckReport(project.Name, FrameMath.FrameCount(project), issues);
    }

    private static void MissingFiles(Project project, string projectDirectory, List<CheckIssue> issues)
    {
        foreach (var asset in project.Assets)
        {
            if (File.Exists(ProjectStore.AssetPath(projectDirectory, asset)))
            {
                continue;
            }

            var users = Users(project, asset.Id).ToList();
            issues.Add(new CheckIssue(CheckKind.MissingFile, asset.Id,
                $"asset '{asset.Id}' ({asset.File}) has no file{(users.Count > 0 ? $"; used by {Ids.List(users)}, which won't render or export" : "")}; put the file back, or import it again"));
        }

        foreach (var pending in MatteFiles.Pending(project, projectDirectory))
        {
            var shows = pending.Cutout ? "the subject isn't drawn over the text" : "the layer is drawn with its background";
            issues.Add(new CheckIssue(CheckKind.MissingFile, pending.LayerId,
                $"{(pending.Cutout ? "cutout" : "layer")} '{pending.LayerId}': the {EnumNames.ToName(pending.Method)} matte of '{pending.AssetId}' isn't computed yet ({MatteFiles.Relative(projectDirectory, pending.Path)}), so {shows} and exports refuse; wait for its matte job (get_job_status) or run {pending.Tool} again"));
        }
    }

    private static void Glyphs(Project project, ITextMeasurer? measurer, IReadOnlyDictionary<string, string> fonts, List<CheckIssue> issues)
    {
        if (measurer is null)
        {
            return;
        }

        foreach (var text in project.Layers.OfType<TextLayer>())
        {
            var missing = measurer.MissingGlyphs(text);
            if (missing.Length == 0)
            {
                continue;
            }

            var language = Languages.VersionOf(project, text);
            var font = text.FontAsset is { } asset ? $"font asset '{asset}'" : $"font '{text.FontFamily}'";
            var suggested = Languages.FontFor(fonts, language);
            var fix = suggested is not null && suggested != text.FontFamily
                ? $"the {language} font is \"{suggested}\" (update_layer fontFamily)"
                : "give it a font that has them (update_layer fontFamily or fontAsset)";
            issues.Add(new CheckIssue(CheckKind.MissingGlyphs, text.Id,
                $"text '{text.Id}'{(language is null ? "" : $" ({language})")}: {font} has no glyphs for \"{missing}\", which show as empty boxes; {fix}"));
        }
    }

    private static void UnusedAssets(Project project, List<CheckIssue> issues)
    {
        foreach (var asset in project.Assets.Where(a => !Users(project, a.Id).Any()))
        {
            issues.Add(new CheckIssue(CheckKind.UnusedAsset, asset.Id, $"{EnumNames.ToName(asset.Type)} asset '{asset.Id}' isn't used by any layer"));
        }
    }

    private static IEnumerable<string> Users(Project project, string assetId) =>
        project.Layers.Where(l => l switch
        {
            ImageLayer image => image.Drawings.Values.Contains(assetId),
            TextLayer text => text.FontAsset == assetId,
            AudioLayer audio => audio.Asset == assetId,
            VideoLayer video => video.Asset == assetId,
            _ => false,
        } || l is VisualLayer { Mask.Asset: { } mask } && mask == assetId).Select(l => l.Id);

    // Every frame: which layers show content, where it lands, and whether anything shows at all.
    private static void Layout(Project project, ITextMeasurer? measurer, List<CheckIssue> issues)
    {
        var frames = FrameMath.FrameCount(project);
        var textSizes = new Dictionary<string, (double Width, double Height)>(StringComparer.Ordinal);
        var shownFrames = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var onCanvasFrames = new Dictionary<string, int>(StringComparer.Ordinal);
        var unsafeFrames = new Dictionary<(string Layer, string Zone), List<int>>();
        var empty = new List<int>();
        var canvas = new Rect(0, 0, project.Width, project.Height);
        var zones = SafeZones(project);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = FrameMath.TimeOf(project, frame);
            var anything = false;
            foreach (var layer in SceneEvaluator.Evaluate(project, time))
            {
                if (!layer.Shown || layer.Opacity <= 0.001 || LocalBounds(project, layer.Layer, time, measurer, textSizes) is not { } local)
                {
                    continue;
                }

                var screen = Transform(layer.World, local);
                var id = layer.Layer.Id;
                Add(shownFrames, id, frame);
                if (!screen.Intersects(canvas))
                {
                    continue;
                }

                anything = true;
                onCanvasFrames[id] = onCanvasFrames.GetValueOrDefault(id) + 1;
                if (layer.Layer is TextLayer)
                {
                    foreach (var (zone, area) in zones)
                    {
                        if (screen.Intersects(area))
                        {
                            Add(unsafeFrames, (id, zone), frame);
                        }
                    }
                }
            }

            if (!anything)
            {
                empty.Add(frame);
            }
        }

        foreach (var (id, shown) in shownFrames.Where(s => !onCanvasFrames.ContainsKey(s.Key)))
        {
            issues.Add(new CheckIssue(CheckKind.OutsideCanvas, id,
                $"'{id}' is never on the canvas while it's shown ({Ranges(project, shown)}); check its x/y, its parent and the camera"));
        }

        foreach (var ((id, zone), list) in unsafeFrames.Where(u => onCanvasFrames.ContainsKey(u.Key.Layer)))
        {
            issues.Add(new CheckIssue(CheckKind.OutsideSafeArea, id, $"text '{id}' reaches into {zone} at {Ranges(project, list)}"));
        }

        foreach (var range in Runs(empty))
        {
            var count = range.Last - range.First + 1;
            issues.Add(new CheckIssue(CheckKind.EmptyFrames, $"{range.First}",
                $"nothing but the background shows at {Span(project, range.First, range.Last)} ({count} frame{(count == 1 ? "" : "s")})"));
        }
    }

    // The strips text should stay out of: the title-safe margins, plus the Shorts player's buttons
    // and captions on vertical canvases.
    private static List<(string Zone, Rect Area)> SafeZones(Project project)
    {
        double w = project.Width, h = project.Height;
        var zones = new List<(string, Rect)>();
        if (IsVertical(project))
        {
            zones.Add(($"the Shorts UI zone (top {Percent(ShortsTop)})", new Rect(0, 0, w, h * ShortsTop)));
            zones.Add(($"the Shorts UI zone (bottom {Percent(ShortsBottom)})", new Rect(0, h * (1 - ShortsBottom), w, h)));
            zones.Add(($"the Shorts UI zone (right {Percent(ShortsRight)})", new Rect(w * (1 - ShortsRight), 0, w, h)));
            zones.Add(($"the title-safe margin ({Percent(SafeMargin)}, left)", new Rect(0, 0, w * SafeMargin, h)));
        }
        else
        {
            zones.Add(($"the title-safe margin ({Percent(SafeMargin)}, top)", new Rect(0, 0, w, h * SafeMargin)));
            zones.Add(($"the title-safe margin ({Percent(SafeMargin)}, bottom)", new Rect(0, h * (1 - SafeMargin), w, h)));
            zones.Add(($"the title-safe margin ({Percent(SafeMargin)}, left)", new Rect(0, 0, w * SafeMargin, h)));
            zones.Add(($"the title-safe margin ({Percent(SafeMargin)}, right)", new Rect(w * (1 - SafeMargin), 0, w, h)));
        }

        return zones;
    }

    // The layer's content box in its own pixels, or null when it has none or its size isn't known.
    public static (double Left, double Top, double Width, double Height)? ContentBox(Project project, VisualLayer layer, double time, ITextMeasurer? measurer) =>
        LocalBounds(project, layer, time, measurer, new()) is { } box ? (box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top) : null;

    private static Rect? LocalBounds(Project project, VisualLayer layer, double time, ITextMeasurer? measurer, Dictionary<string, (double Width, double Height)> textSizes)
    {
        switch (layer)
        {
            case ImageLayer image when image.Drawings.Count > 0:
                var name = Interpolator.Evaluate(image.Drawing, time);
                if (!image.Drawings.TryGetValue(name, out var assetId) || project.FindAsset(assetId) is not { Width: { } width, Height: { } height })
                {
                    return null;
                }

                var left = image.FlipX ? 2 * image.AnchorX - width : 0;
                return new Rect(left, 0, left + width, height);
            case TextLayer text when measurer is not null:
                if (!textSizes.TryGetValue(text.Id, out var size))
                {
                    size = measurer.Measure(text);
                    textSizes[text.Id] = size;
                }

                return size.Width > 0 && size.Height > 0 ? new Rect(0, 0, size.Width, size.Height) : null;
            case VideoLayer video when project.FindAsset(video.Asset) is { } asset:
                var (cropWidth, cropHeight) = VideoTiming.CropSize(video, asset);
                return cropWidth > 0 && cropHeight > 0 ? new Rect(0, 0, cropWidth, cropHeight) : null;
            default:
                return null;
        }
    }

    // Keyframes that can't change anything: before the layer starts (but the last one, which shapes
    // the start) and after it ends (but the first one when it still shapes a non-hold segment).
    private static void DeadKeyframes(Project project, List<CheckIssue> issues)
    {
        foreach (var layer in project.Layers.OfType<VisualLayer>())
        {
            var (start, end) = Window(project, layer);
            foreach (var (name, value) in AnimatableProperties.Numeric(layer))
            {
                Report(project, $"{layer.Id}.{name}", value.Keyframes.Select(k => (k.Time, k.Easing == Easing.Hold)).ToList(), start, end, issues);
            }

            if (layer is ImageLayer image)
            {
                Report(project, $"{layer.Id}.drawing", image.Drawing.Keyframes.Select(k => (k.Time, true)).ToList(), start, end, issues);
            }
        }

        if (project.Camera is { } camera)
        {
            foreach (var (name, value) in AnimatableProperties.Numeric(camera))
            {
                Report(project, $"camera.{name}", value.Keyframes.Select(k => (k.Time, k.Easing == Easing.Hold)).ToList(), 0, project.Duration, issues);
            }
        }
    }

    private static void Report(Project project, string where, List<(double Time, bool Hold)> keys, double start, double end, List<CheckIssue> issues)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var before = keys.Where(k => k.Time < start - Tolerance).ToList();
        var atStart = keys.Any(k => Math.Abs(k.Time - start) <= Tolerance);
        var deadBefore = atStart ? before : before.SkipLast(1).ToList();

        var after = keys.Where(k => k.Time > end + Tolerance).ToList();
        var last = keys.FindLastIndex(k => k.Time <= end + Tolerance);
        var shapesEnd = last < 0 || (keys[last].Time < end - Tolerance && !keys[last].Hold);
        var deadAfter = shapesEnd ? after.Skip(1).ToList() : after;

        var limits = $"{Num(start)}–{Num(end)}s";
        if (deadBefore.Count > 0)
        {
            issues.Add(new CheckIssue(CheckKind.DeadKeyframes, where,
                $"{where}: {deadBefore.Count} keyframe(s) at {Times(deadBefore)} come before its time ({limits}) and never play; remove_keyframes them"));
        }

        if (deadAfter.Count > 0)
        {
            issues.Add(new CheckIssue(CheckKind.DeadKeyframes, where,
                $"{where}: {deadAfter.Count} keyframe(s) at {Times(deadAfter)} come after its time ({limits}) and never play; remove_keyframes them or extend the layer{(end >= project.Duration - Tolerance ? " (or the project)" : "")}"));
        }
    }

    // When a layer can show: its own time, limited by every parent's and by the project.
    private static (double Start, double End) Window(Project project, Layer layer)
    {
        var start = layer.Start;
        var end = Math.Min(SceneEvaluator.EndOf(layer, project.Duration), project.Duration);
        var seen = new HashSet<string>(StringComparer.Ordinal) { layer.Id };
        var current = layer;
        while (current.ParentId is { } parentId && project.FindLayer(parentId) is { } parent && seen.Add(parent.Id))
        {
            start = Math.Max(start, parent.Start);
            end = Math.Min(end, SceneEvaluator.EndOf(parent, project.Duration));
            current = parent;
        }

        return (start, end);
    }

    private static Rect Transform(Affine2D world, Rect local)
    {
        var corners = new[] { world.Apply(local.Left, local.Top), world.Apply(local.Right, local.Top), world.Apply(local.Right, local.Bottom), world.Apply(local.Left, local.Bottom) };
        return new Rect(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    private static void Add<TKey>(Dictionary<TKey, List<int>> map, TKey key, int frame) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(frame);
    }

    private static IEnumerable<(int First, int Last)> Runs(List<int> frames)
    {
        for (var i = 0; i < frames.Count;)
        {
            var j = i;
            while (j + 1 < frames.Count && frames[j + 1] == frames[j] + 1)
            {
                j++;
            }

            yield return (frames[i], frames[j]);
            i = j + 1;
        }
    }

    private static string Ranges(Project project, List<int> frames)
    {
        var runs = Runs(frames).ToList();
        var text = string.Join(", ", runs.Take(4).Select(r => Span(project, r.First, r.Last)));
        return runs.Count > 4 ? $"{text} and {runs.Count - 4} more time(s)" : text;
    }

    // Frames first..last cover first/fps up to (last+1)/fps.
    private static string Span(Project project, int first, int last) =>
        $"{Num(FrameMath.TimeOf(project, first))}–{Num(FrameMath.TimeOf(project, last + 1))}s";

    private static string Times(List<(double Time, bool Hold)> keys) =>
        string.Join(", ", keys.Take(6).Select(k => $"{Num(k.Time)}s")) + (keys.Count > 6 ? ", …" : "");

    private static string Percent(double share) => $"{Math.Round(share * 100)}%";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct Rect(double Left, double Top, double Right, double Bottom)
    {
        public bool Intersects(Rect other) =>
            Left < other.Right - PixelSlack && Right > other.Left + PixelSlack && Top < other.Bottom - PixelSlack && Bottom > other.Top + PixelSlack;
    }
}
