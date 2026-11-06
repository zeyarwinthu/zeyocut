// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Render;

// Covered: the share of the text that what is drawn above it hides, 0-1; null where the text draws
// nothing (outside its time, hidden or fully transparent).
public sealed record OcclusionFrame(int Frame, double Time, double? Covered);

public sealed record OcclusionFile(string LayerId, int Fps, int StartFrame, int EndFrame, List<OcclusionFrame> Frames);

public sealed record OcclusionResult(OcclusionFile Values, string SavedPath)
{
    // Runs of frames whose values stay within Band of each other are listed together, up to this
    // many lines; the file has every frame.
    public const double Band = 0.01;
    public const int MaxLines = 40;

    public IReadOnlyList<double> Shown => Values.Frames.Where(f => f.Covered is not null).Select(f => f.Covered!.Value).ToList();

    public string Describe()
    {
        var frames = Values.Frames;
        var first = frames[0];
        var last = frames[^1];
        var span = $"{Num(first.Time)}–{Num(last.Time)}s (f{Values.StartFrame}–f{Values.EndFrame}, {frames.Count} frames)";
        var shown = Shown;
        if (shown.Count == 0)
        {
            return $"text_occlusion '{Values.LayerId}' {span}: the text isn't shown in any of these frames (outside its time, hidden or fully transparent)";
        }

        var notShown = frames.Count - shown.Count;
        var builder = new StringBuilder(
            $"text_occlusion '{Values.LayerId}' {span}: covered min {Pct(shown.Min())}, max {Pct(shown.Max())}, average {Pct(shown.Average())}{(notShown > 0 ? $" over the {shown.Count} frames it shows ({notShown} not shown)" : "")}");
        var runs = new List<List<OcclusionFrame>>();
        foreach (var frame in frames)
        {
            var run = runs.Count > 0 ? runs[^1] : null;
            if (run is not null && Fits(run, frame))
            {
                run.Add(frame);
            }
            else
            {
                runs.Add([frame]);
            }
        }

        foreach (var run in runs.Take(MaxLines))
        {
            var (from, to) = (run[0], run[^1]);
            var where = from.Frame == to.Frame
                ? $"f{from.Frame} ({Num(from.Time)}s)"
                : $"f{from.Frame}–f{to.Frame} ({Num(from.Time)}–{Num(to.Time)}s)";
            builder.Append($"\n- {where}: {Label(run)}");
        }

        if (runs.Count > MaxLines)
        {
            builder.Append($"\n- … and {runs.Count - MaxLines} more (every frame is in the file)");
        }

        builder.Append($"\nper frame: {SavedPath}");
        return builder.ToString();
    }

    // A frame joins a run while the run's values stay within Band (or both are not shown).
    private static bool Fits(List<OcclusionFrame> run, OcclusionFrame frame)
    {
        if (run[0].Covered is null || frame.Covered is null)
        {
            return run[0].Covered is null && frame.Covered is null;
        }

        var values = run.Select(f => f.Covered!.Value).Append(frame.Covered.Value).ToList();
        return values.Max() - values.Min() <= Band + 1e-9;
    }

    private static string Label(List<OcclusionFrame> run)
    {
        if (run[0].Covered is null)
        {
            return "not shown";
        }

        var low = (run.Min(f => f.Covered!.Value) * 100).ToString("0.0", CultureInfo.InvariantCulture);
        var high = Pct(run.Max(f => f.Covered!.Value));
        return low + "%" == high ? high : $"{low}–{high}";
    }

    private static string Pct(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

// text_occlusion: how much of a text layer what is drawn above it hides. Each frame is drawn twice
// with a transparent background: the text alone (its coverage t per pixel) and only the layers above
// it (their coverage a). The share covered is sum(t * a) / sum(t), so a half-transparent layer
// counts half and the text's own opacity doesn't matter.
public static class Occlusion
{
    // generate_captions behindSubject=auto measures this many pixels wide, plenty for a share.
    public const int CaptionWidth = 640;
    // The frames whose time is in [startTime, endTime); by default the text's own time.
    public static (int First, int Last) Frames(Project project, TextLayer text, double? startTime, double? endTime)
    {
        var start = startTime ?? text.Start;
        var end = endTime ?? text.End ?? project.Duration;
        if (!double.IsFinite(start) || start < 0 || !double.IsFinite(end) || end <= start)
        {
            throw new ZeyoCutException($"the range must have 0 <= startTime < endTime (got {Num(start)}–{Num(end)})");
        }

        var count = FrameMath.FrameCount(project);
        var first = Math.Max(0, (int)Math.Ceiling(start * project.Fps - 1e-6));
        var last = Math.Min(count - 1, (int)Math.Ceiling(end * project.Fps - 1e-6) - 1);
        if (last < first)
        {
            throw new ZeyoCutException($"{Num(start)}–{Num(end)}s has no frame of the project (0–{Num(FrameMath.RenderedDuration(project))}s at {project.Fps} fps)");
        }

        return (first, last);
    }

    public static OcclusionResult Run(FrameRenderer renderer, Project project, string textLayerId, int first, int last, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var meter = new OcclusionMeter(renderer, project);
        var frames = new List<OcclusionFrame>();
        for (var frame = first; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var covered = meter.Measure(textLayerId, frame);
            frames.Add(new OcclusionFrame(frame, Math.Round(FrameMath.TimeOf(project, frame), 6), covered is { } c ? Math.Round(c, 4) : null));
            progress?.Invoke((frame - first + 1) / (double)(last - first + 1));
        }

        var file = new OcclusionFile(textLayerId, project.Fps, first, last, frames);
        var path = Path.Combine(renderer.ProjectDirectory, ProjectStore.RendersFolder, $"text_occlusion_{textLayerId}_{first:D5}-{last:D5}.json");
        AnalysisFiles.Save(path, file);
        return new OcclusionResult(file, path);
    }

    // The frames each text shows in (its start to its end), in time order; texts outside the
    // project's frames are left out.
    public static List<(string LayerId, int First, int Last)> TextFrames(Project project, IEnumerable<string> textIds)
    {
        var count = FrameMath.FrameCount(project);
        var spans = new List<(string LayerId, int First, int Last)>();
        foreach (var id in textIds)
        {
            var text = ProjectEditor.GetLayer(project, id);
            var first = Math.Max(0, (int)Math.Ceiling(text.Start * project.Fps - 1e-6));
            var last = Math.Min(count - 1, (int)Math.Ceiling((text.End ?? project.Duration) * project.Fps - 1e-6) - 1);
            if (last >= first)
            {
                spans.Add((id, first, last));
            }
        }

        return spans.OrderBy(s => s.First).ToList();
    }

    // generate_captions behindSubject=auto: how much of each caption the cover layers (the captions'
    // cutout, or the clips that are their own subject) hide, in every frame the caption shows.
    public static List<CaptionCover> Captions(FrameRenderer renderer, Project project, IReadOnlyList<string> textIds, IReadOnlyList<string> cover, Action<double>? progress, CancellationToken cancellationToken)
    {
        var spans = TextFrames(project, textIds);
        var total = Math.Max(1, spans.Sum(s => s.Last - s.First + 1));
        using var meter = new OcclusionMeter(renderer, project, cover.ToHashSet(StringComparer.Ordinal), CaptionWidth);
        var measured = new List<CaptionCover>();
        var done = 0;
        foreach (var (id, first, last) in spans)
        {
            for (var frame = first; frame <= last; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (meter.Measure(id, frame) is { } covered)
                {
                    measured.Add(new CaptionCover(id, frame, Math.Round(covered, 4)));
                }

                progress?.Invoke(++done / (double)total);
            }
        }

        return measured;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

// Measures text layers frame by frame, reusing its two drawing surfaces. cover: only these layers
// count as covering (default: everything drawn above the text); width: measure at this many pixels
// wide (default: the project's size).
public sealed class OcclusionMeter : IDisposable
{
    private readonly FrameRenderer _renderer;
    private readonly Project _project;
    private readonly IReadOnlySet<string>? _cover;
    private readonly int _width;
    private readonly int _height;
    private readonly SKSurface _text;
    private readonly SKSurface _above;
    private readonly byte[] _textAlpha;
    private readonly byte[] _aboveAlpha;

    public OcclusionMeter(FrameRenderer renderer, Project project, IReadOnlySet<string>? cover = null, int? width = null)
    {
        _renderer = renderer;
        _project = project;
        _cover = cover;
        _width = Math.Clamp(width ?? project.Width, 1, project.Width);
        _height = _width == project.Width ? project.Height : Math.Max(1, (int)Math.Round(project.Height * _width / (double)project.Width));
        var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _text = SKSurface.Create(info) ?? throw new ZeyoCutException($"could not create a {_width}x{_height} drawing surface");
        _above = SKSurface.Create(info) ?? throw new ZeyoCutException($"could not create a {_width}x{_height} drawing surface");
        _textAlpha = new byte[_width * _height];
        _aboveAlpha = new byte[_width * _height];
    }

    // The share of the text covered at this frame, or null when the text draws nothing.
    public double? Measure(string textLayerId, int frame)
    {
        Draw(_text, frame, new RenderOptions(Transparent: true, Only: new HashSet<string>(StringComparer.Ordinal) { textLayerId }));
        ReadAlpha(_text, _textAlpha);
        long total = 0;
        var width = _width;
        int left = width, top = _height, right = -1, bottom = -1;
        for (var i = 0; i < _textAlpha.Length; i++)
        {
            var t = _textAlpha[i];
            if (t == 0)
            {
                continue;
            }

            total += t;
            var x = i % width;
            var y = i / width;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }

        if (total == 0)
        {
            return null;
        }

        Draw(_above, frame, _cover is null ? new RenderOptions(Transparent: true, After: textLayerId) : new RenderOptions(Transparent: true, Only: _cover));
        ReadAlpha(_above, _aboveAlpha);
        long covered = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var i = y * width + left; i <= y * width + right; i++)
            {
                covered += _textAlpha[i] * _aboveAlpha[i];
            }
        }

        return Math.Clamp(covered / 255.0 / total, 0, 1);
    }

    public void Dispose()
    {
        _text.Dispose();
        _above.Dispose();
    }

    private void Draw(SKSurface surface, int frame, RenderOptions options)
    {
        var canvas = surface.Canvas;
        canvas.ResetMatrix();
        if (_width != _project.Width)
        {
            canvas.Scale(_width / (float)_project.Width, _height / (float)_project.Height);
        }

        _renderer.Render(_project, frame, canvas, options);
        canvas.ResetMatrix();
    }

    private void ReadAlpha(SKSurface surface, byte[] alpha)
    {
        var info = new SKImageInfo(_width, _height, SKColorType.Alpha8, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(alpha, GCHandleType.Pinned);
        try
        {
            if (!surface.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
            {
                throw new ZeyoCutException("could not read the rendered frame");
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
