// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// The timeline: a ruler (click or drag it to move the playhead; the markers on it), the lanes
// TimelineLayout works out, and the playhead. Drag a clip to move it, up or down onto another draw
// level or between two for a level of its own; drag an edge to trim. The edit goes out through Edit
// when the mouse is released, and Escape drops it. Ctrl+wheel zooms, the wheel scrolls.
public sealed class TimelineView : FrameworkElement
{
    public const double HeaderWidth = 150;
    public const double RulerHeight = 36;

    private const double EdgeGrip = 6;
    private const double LanePad = 3;
    private const double GapReach = 6;
    private const double SnapPixels = 8;
    private const double DragThreshold = 3;
    private const double KeyMark = 3.5;
    private const double KeyReach = 5;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush MarkBrush = Frozen(0xC0, 0x40, 0x90);
    private static readonly Brush BadgeBrush = Frozen(0x60, 0, 0, 0);
    private static readonly Pen KeyEdge = Frozen(new Pen(Frozen(0xA0, 0, 0, 0), 1));
    private static readonly Brush PastEndBrush = Frozen(0x30, 0, 0, 0);
    private static readonly Dictionary<ClipKind, Brush> Fills = new()
    {
        [ClipKind.Video] = Frozen(0x2F, 0x5C, 0x96),
        [ClipKind.Image] = Frozen(0x2F, 0x77, 0x4A),
        [ClipKind.Text] = Frozen(0x8F, 0x62, 0x24),
        [ClipKind.Group] = Frozen(0x2A, 0x76, 0x76),
        [ClipKind.Caption] = Frozen(0x4A, 0x58, 0x78),
        [ClipKind.Audio] = Frozen(0x6E, 0x48, 0x96),
    };

    private readonly DrawingVisual _content = new();
    private readonly DrawingVisual _overlay = new();
    private readonly VisualCollection _visuals;

    private Project? _project;
    private IReadOnlyList<TimelineLane> _lanes = [];
    private List<double> _laneTops = [];
    private IReadOnlyList<Marker> _markers = [];
    private int _frames = 1;
    private int _fps = 30;
    private double _duration = 1;
    private double _contentEnd = 1;
    private bool _fit = true;
    private double _pixelsPerSecond = 100;
    private double _offset;
    private double _scrollY;
    private int _playhead;
    private string? _selected;
    private Drag? _drag;

    // A drag let go, drawn where it was dropped until the project is shown with its edit (Show): the
    // window saves the edit on a worker thread.
    private Drag? _landing;
    private bool _scrubbing;
    private string? _hovered;
    private IReadOnlyList<double> _keyTimes = [];
    private (double Time, int? Lane)? _mediaDrop;

    // Scrolls the lanes while a drag or a scrub is held at their left or right edge.
    private readonly DispatcherTimer _edgeScroll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };

    public TimelineView()
    {
        _visuals = new VisualCollection(this) { _content, _overlay };
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _edgeScroll.Tick += OnEdgeScroll;
    }

    // The frame the playhead was moved to.
    public event EventHandler<int>? Seek;

    // The clip clicked, or null when the click was on no clip.
    public event EventHandler<string?>? Selected;

    // A finished drag's edit.
    public event EventHandler<TimelineEdit>? Edit;

    // A clip right-clicked; it is selected first.
    public event EventHandler<TimelineClip>? ClipMenu;

    // The zoom or the scroll position changed.
    public event EventHandler? ViewChanged;

    public IReadOnlyList<TimelineLane> Lanes => _lanes;

    public bool Snapping { get; set; } = true;

    public bool Dragging => _drag is { Active: true };

    public int Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            DrawOverlay();
        }
    }

    public string? Selection
    {
        get => _selected;
        set
        {
            _selected = value;
            DrawContent();
        }
    }

    // The selected layer's keyframe times, marked on the selected clip: a click on a mark moves the
    // playhead there.
    public IReadOnlyList<double> KeyTimes
    {
        get => _keyTimes;
        set
        {
            if (value.SequenceEqual(_keyTimes))
            {
                return;
            }

            _keyTimes = value;
            DrawContent();
        }
    }

    public double PixelsPerSecond => _fit ? FitPixelsPerSecond : _pixelsPerSecond;

    public double ViewportWidth => Math.Max(1, ActualWidth - HeaderWidth);

    public double ContentWidth => _contentEnd * PixelsPerSecond;

    public double HorizontalOffset
    {
        get => _fit ? 0 : _offset;
        set => SetScroll(value, _scrollY);
    }

    public double ViewportHeight => Math.Max(1, ActualHeight - RulerHeight);

    public double ContentHeight => _laneTops.Count == 0 ? 0 : _laneTops[^1];

    // Room past the end to drag a clip into.
    public double MaxHorizontalOffset => _fit ? 0 : Math.Max(0, ContentWidth + ViewportWidth * 0.25 - ViewportWidth);

    public double MaxVerticalOffset => Math.Max(0, ContentHeight - ViewportHeight);

    public double VerticalOffset
    {
        get => _scrollY;
        set => SetScroll(HorizontalOffset, value);
    }

    protected override int VisualChildrenCount => _visuals.Count;

    private double FitPixelsPerSecond => ViewportWidth * 0.98 / Math.Max(_duration, 1.0 / _fps);

    private double MaxPixelsPerSecond => _fps * 48.0;

    private TimelineScale Scale => new(PixelsPerSecond, HorizontalOffset);

    private double SecondsPerFrame => 1.0 / _fps;

    public static double LaneHeight(TimelineLane lane) => lane.Rows * RowHeight(lane.Kind) + 2 * LanePad + 1;

    public static double RowHeight(LaneKind kind) => kind switch
    {
        LaneKind.Visual => 34,
        LaneKind.Captions => 26,
        _ => 28,
    };

    public void Show(Project project, IReadOnlyList<TimelineLane> lanes, int frames)
    {
        _landing = null;
        _project = project;
        _lanes = lanes;
        _markers = project.Markers;
        _frames = Math.Max(1, frames);
        _fps = Math.Max(1, project.Fps);
        _duration = Math.Max(SecondsPerFrame, project.Duration);
        _contentEnd = Math.Max(_duration, lanes.SelectMany(l => l.Clips).Select(c => c.End).DefaultIfEmpty(0).Max());
        _laneTops = [0];
        foreach (var lane in lanes)
        {
            _laneTops.Add(_laneTops[^1] + LaneHeight(lane));
        }

        // A clip another program moved while it was being dragged is let go.
        if (_drag is { } drag)
        {
            var now = FindClip(drag.Clip.LayerId);
            if (now is null || now.Start != drag.Clip.Start || now.End != drag.Clip.End)
            {
                CancelDrag();
            }
        }

        SetScroll(_offset, _scrollY);
    }

    public TimelineClip? FindClip(string? id) => id is null ? null : _lanes.SelectMany(l => l.Clips).FirstOrDefault(c => c.LayerId == id);

    // Where media dropped at a point goes: the frame under it, and the lane under it if there is one.
    // The place is marked until ClearMediaDrop.
    public (double Time, TimelineLane? Lane) MediaDropAt(Point point)
    {
        var time = Math.Max(0, Math.Round(Scale.TimeAt(point.X - HeaderWidth) * _fps) / _fps);
        var at = point.Y - RulerHeight + _scrollY;
        int? lane = null;
        for (var i = 0; i < _lanes.Count && point.Y >= RulerHeight; i++)
        {
            if (at >= _laneTops[i] && at < _laneTops[i + 1])
            {
                lane = i;
            }
        }

        _mediaDrop = (time, lane);
        DrawOverlay();
        return (time, lane is { } index ? _lanes[index] : null);
    }

    public void ClearMediaDrop()
    {
        _mediaDrop = null;
        DrawOverlay();
    }

    public void CancelDrag()
    {
        _drag = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        DrawContent();
    }

    public void ZoomBy(double factor) => ZoomTo(PixelsPerSecond * factor, ViewportWidth / 2);

    public void ZoomToFit()
    {
        _fit = true;
        _offset = 0;
        SetScroll(0, _scrollY);
    }

    // Scrolls so the frame is in view, a page at a time, as playback goes past the right edge.
    public void Reveal(int frame)
    {
        if (_fit)
        {
            return;
        }

        var x = Scale.XOf(frame / (double)_fps);
        if (x < 0 || x > ViewportWidth - 8)
        {
            SetScroll(frame / (double)_fps * PixelsPerSecond - ViewportWidth * 0.1, _scrollY);
        }
    }

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        SetScroll(_offset, _scrollY);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        e.Handled = true;
        if (point.X < HeaderWidth)
        {
            return;
        }

        if (point.Y < RulerHeight)
        {
            _scrubbing = true;
            CaptureMouse();
            SeekTo(point.X);
            return;
        }

        if (HitClip(point) is not { } hit)
        {
            ChangeSelection(null);
            SeekTo(point.X);
            return;
        }

        if (KeyAt(point, hit) is { } key)
        {
            var frame = FrameMath.NearestFrame(_project!, key);
            if (frame != _playhead)
            {
                Seek?.Invoke(this, frame);
            }

            return;
        }

        ChangeSelection(hit.Clip.LayerId);
        TimelineLimits limits;
        try
        {
            limits = TimelineEditor.Limits(_project!, hit.Clip.LayerId);
        }
        catch (ZeyoCutException)
        {
            return;
        }

        _drag = new Drag(hit.Clip, hit.Lane, hit.Part, limits, point, HorizontalOffset)
        {
            Place = new DragPlace(hit.Clip.Start, hit.Clip.End, null),
            From = _project!,
        };
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_scrubbing)
        {
            SeekTo(point.X);
            ScrollAtEdge(point);
            return;
        }

        if (_drag is { } drag && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!drag.Active && Math.Abs(point.X - drag.Pressed.X) < DragThreshold && Math.Abs(point.Y - drag.Pressed.Y) < DragThreshold)
            {
                return;
            }

            drag.Active = true;
            UpdateDrag(drag, point);
            ScrollAtEdge(point);
            return;
        }

        var hit = point.X >= HeaderWidth && point.Y >= RulerHeight ? HitClip(point) : null;
        var key = hit is null ? null : KeyAt(point, hit);
        Cursor = key is not null ? Cursors.Hand : hit?.Part is DragPart.Start or DragPart.End ? Cursors.SizeWE : null;
        var hovered = key is { } at ? $"{hit!.Clip.LayerId}@{at}" : hit?.Clip.LayerId;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            ToolTip = hit is null ? null : key is { } time ? $"Keyframe at {Seconds(time)}: click to go there (Alt+Left, Alt+Right)" : Describe(hit.Clip);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        if (_scrubbing)
        {
            _scrubbing = false;
            ReleaseMouseCapture();
            return;
        }

        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        ReleaseMouseCapture();
        var operation = drag.Active ? TimelineEdits.Operation(drag.Clip, drag.Part, drag.Place, drag.Drop) : null;
        _landing = operation is null ? null : drag;
        DrawContent();
        if (operation is not null)
        {
            Edit?.Invoke(this, new TimelineEdit(operation, drag.From));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _scrubbing = false;
        if (_drag is not null)
        {
            _drag = null;
            DrawContent();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var point = e.GetPosition(this);
        if (Dragging || point.X < HeaderWidth || point.Y < RulerHeight || HitClip(point) is not { } hit)
        {
            return;
        }

        e.Handled = true;
        ChangeSelection(hit.Clip.LayerId);
        ClipMenu?.Invoke(this, hit.Clip);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        var notches = e.Delta / 120.0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomTo(PixelsPerSecond * Math.Pow(1.25, notches), Math.Max(0, e.GetPosition(this).X - HeaderWidth));
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || ContentHeight <= ViewportHeight)
        {
            SetScroll(HorizontalOffset - notches * ViewportWidth * 0.15, _scrollY);
        }
        else
        {
            SetScroll(HorizontalOffset, _scrollY - notches * 48);
        }
    }

    private static Brush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string Seconds(double time) => time.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private static string Describe(TimelineClip clip)
    {
        var lines = new List<string> { $"{clip.LayerId} · {clip.Kind.ToString().ToLowerInvariant()}", $"{Seconds(clip.Start)} – {Seconds(clip.End)} ({Seconds(clip.End - clip.Start)})" };
        if (clip.Kind == ClipKind.Caption)
        {
            lines.Add(clip.Label);
        }
        else if (clip.Detail.Length > 0)
        {
            lines.Add(clip.Detail);
        }

        if (clip.Children > 0)
        {
            lines.Add($"{clip.Children} child layer(s) move, trim and split with it");
        }

        if (clip.Hidden)
        {
            lines.Add(clip.Kind == ClipKind.Audio ? "muted" : "hidden");
        }

        if (clip.SoundOff)
        {
            lines.Add("its sound is muted");
        }

        lines.AddRange(clip.Marks.Select(m => $"followed by {m}"));
        return string.Join("\n", lines);
    }

    private void ChangeSelection(string? id)
    {
        if (id == _selected)
        {
            return;
        }

        _selected = id;
        DrawContent();
        Selected?.Invoke(this, id);
    }

    private void SeekTo(double x)
    {
        var frame = TimelineScale.FrameAt(Scale.TimeAt(x - HeaderWidth), _fps, _frames);
        if (frame != _playhead)
        {
            Seek?.Invoke(this, frame);
        }
    }

    private void ZoomTo(double pixelsPerSecond, double anchor)
    {
        var scale = Scale;
        var target = Math.Clamp(pixelsPerSecond, FitPixelsPerSecond, Math.Max(FitPixelsPerSecond, MaxPixelsPerSecond));
        _fit = target <= FitPixelsPerSecond + 1e-9;
        _pixelsPerSecond = target;
        SetScroll(_fit ? 0 : scale.ZoomedAt(target, anchor).Offset, _scrollY);
    }

    private void SetScroll(double offset, double scrollY)
    {
        _offset = Math.Clamp(offset, 0, MaxHorizontalOffset);
        _scrollY = Math.Clamp(scrollY, 0, MaxVerticalOffset);
        DrawContent();
        DrawOverlay();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDrag(Drag drag, Point point)
    {
        var delta = (point.X + HorizontalOffset - drag.Pressed.X - drag.PressedOffset) / PixelsPerSecond;
        var snaps = Snapping ? drag.Snaps ??= TimelineEdits.SnapPoints(_lanes, drag.Clip.LayerId, _playhead / (double)_fps, _markers.Select(m => m.Time)) : [];
        drag.Place = TimelineEdits.Place(drag.Clip, drag.Part, delta, drag.Limits, _fps, snaps, SnapPixels / PixelsPerSecond);
        (drag.DropLane, drag.DropGap) = drag.Part == DragPart.Body && drag.Lane.Kind == LaneKind.Visual ? DropAt(point.Y) : (null, null);
        drag.Drop = TimelineEdits.Drop(VisualLanes(), drag.Lane, drag.DropLane, drag.DropGap);
        Cursor = drag.Part == DragPart.Body ? Cursors.SizeAll : Cursors.SizeWE;
        DrawContent();
    }

    // Starts scrolling when a drag or a scrub reaches an edge of the lanes.
    private void ScrollAtEdge(Point point)
    {
        if (!_edgeScroll.IsEnabled && TimelineScale.EdgeScroll(point.X, HeaderWidth, ActualWidth) != 0)
        {
            _edgeScroll.Start();
        }
    }

    // Each step while held at an edge: scroll, and the drag or the playhead follows. It stops when the
    // mouse leaves the edge, the button is let go, or there's no further to scroll.
    private void OnEdgeScroll(object? sender, EventArgs e)
    {
        var point = Mouse.GetPosition(this);
        var held = IsMouseCaptured && (_scrubbing || Dragging);
        var step = held ? TimelineScale.EdgeScroll(point.X, HeaderWidth, ActualWidth) : 0;
        var before = HorizontalOffset;
        if (step != 0)
        {
            SetScroll(_offset + step, _scrollY);
        }

        if (HorizontalOffset == before)
        {
            _edgeScroll.Stop();
            return;
        }

        if (_scrubbing)
        {
            SeekTo(point.X);
        }
        else if (_drag is { Active: true } drag)
        {
            UpdateDrag(drag, point);
        }
    }

    private List<TimelineLane> VisualLanes() => _lanes.Where(l => l.Kind != LaneKind.Audio).ToList();

    // The visual lane under y, or the gap between two lanes near y.
    private (int? Lane, int? Gap) DropAt(double y)
    {
        var count = VisualLanes().Count;
        var at = y - RulerHeight + _scrollY;
        if (count == 0)
        {
            return (null, null);
        }

        if (at < GapReach)
        {
            return (null, 0);
        }

        for (var i = 0; i < count; i++)
        {
            var (top, bottom) = (_laneTops[i], _laneTops[i + 1]);
            if (at < top + GapReach)
            {
                return (null, i);
            }

            if (at <= bottom - GapReach)
            {
                return (i, null);
            }
        }

        return at <= _laneTops[count] + GapReach ? (null, count) : (null, null);
    }

    private ClipHit? HitClip(Point point)
    {
        var at = point.Y - RulerHeight + _scrollY;
        var time = Scale.TimeAt(point.X - HeaderWidth);
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (at < _laneTops[i] || at >= _laneTops[i + 1])
            {
                continue;
            }

            var lane = _lanes[i];
            var row = (int)Math.Floor((at - _laneTops[i] - LanePad) / RowHeight(lane.Kind));
            var grip = EdgeGrip / PixelsPerSecond;
            foreach (var clip in lane.Clips.Where(c => c.Row == row).Reverse())
            {
                if (time < clip.Start - grip || time > clip.End + grip)
                {
                    continue;
                }

                var wide = (clip.End - clip.Start) * PixelsPerSecond > 3 * EdgeGrip;
                var part = wide && Math.Abs(time - clip.Start) <= grip ? DragPart.Start
                    : wide && Math.Abs(time - clip.End) <= grip ? DragPart.End
                    : time >= clip.Start && time <= clip.End ? DragPart.Body
                    : (DragPart?)null;
                if (part is { } found)
                {
                    return new ClipHit(lane, clip, found);
                }
            }

            return null;
        }

        return null;
    }

    // The keyframe mark under the point, on the selected clip: its time, or null.
    private double? KeyAt(Point point, ClipHit hit)
    {
        if (hit.Clip.LayerId != _selected || _keyTimes.Count == 0)
        {
            return null;
        }

        var rect = ClipRect(hit.Lane, hit.Clip, RulerHeight + _laneTops[IndexOf(hit.Lane)] - _scrollY, Scale);
        if (point.Y < rect.Bottom - 2 * KeyMark - 4)
        {
            return null;
        }

        var scale = Scale;
        return KeysOn(hit.Clip)
            .Select(t => (Time: t, Distance: Math.Abs(HeaderWidth + scale.XOf(t) - point.X)))
            .Where(k => k.Distance <= KeyReach)
            .OrderBy(k => k.Distance)
            .Select(k => (double?)k.Time)
            .FirstOrDefault();
    }

    private IEnumerable<double> KeysOn(TimelineClip clip) => _keyTimes.Where(t => t >= clip.Start - 1e-9 && t <= clip.End + 1e-9);

    private static Rect ClipRect(TimelineLane lane, TimelineClip clip, double laneTop, TimelineScale scale)
    {
        var x0 = HeaderWidth + scale.XOf(clip.Start);
        var x1 = HeaderWidth + scale.XOf(clip.End);
        var rowHeight = RowHeight(lane.Kind);
        return new Rect(Math.Round(x0) + 0.5, Math.Round(laneTop + LanePad + clip.Row * rowHeight) + 1.5, Math.Max(2, Math.Round(x1) - Math.Round(x0) - 1), rowHeight - 3);
    }

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void DrawContent()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (width, height) = (ActualWidth, ActualHeight);
        var panel = Resource("PanelBrush", Brushes.Black);
        var header = Resource("HeaderBrush", Brushes.Black);
        var border = new Pen(Resource("BorderBrush", Brushes.DimGray), 1);
        var text = Resource("TextBrush", Brushes.White);
        var secondary = Resource("SecondaryTextBrush", Brushes.Gray);
        var accent = Resource("AccentBrush", Brushes.SteelBlue);
        var scale = Scale;
        using var dc = _content.RenderOpen();
        dc.DrawRectangle(panel, null, new Rect(0, 0, width, height));
        if (width <= HeaderWidth || height <= RulerHeight)
        {
            return;
        }

        // Lanes and their clips.
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, RulerHeight, width - HeaderWidth, height - RulerHeight)));
        var drag = _drag is { Active: true } d ? d : _landing;
        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            var top = RulerHeight + _laneTops[i] - _scrollY;
            var bottom = RulerHeight + _laneTops[i + 1] - _scrollY;
            if (bottom < RulerHeight || top > height)
            {
                continue;
            }

            if (drag?.DropLane == i)
            {
                dc.DrawRectangle(Resource("HoverBrush", Brushes.DimGray), null, new Rect(HeaderWidth, top, width - HeaderWidth, bottom - top - 1));
            }

            dc.DrawLine(border, new Point(HeaderWidth, Math.Round(bottom) - 0.5), new Point(width, Math.Round(bottom) - 0.5));
            foreach (var clip in lane.Clips)
            {
                var dragged = drag is not null && drag.Clip.LayerId == clip.LayerId;
                DrawClip(dc, lane, clip, top, scale, width, dpi, text, secondary, accent, dragged ? 0.35 : 1);
            }
        }

        // Past the project's end, where the export has stopped.
        var end = Math.Round(HeaderWidth + scale.XOf(_duration)) + 0.5;
        dc.DrawRectangle(PastEndBrush, null, new Rect(end, RulerHeight, Math.Max(0, width - end), height - RulerHeight));

        // Where the dragged clip goes.
        if (drag is not null)
        {
            var laneIndex = drag.DropLane ?? IndexOf(drag.Lane);
            var lane = laneIndex >= 0 ? _lanes[laneIndex] : drag.Lane;
            var top = RulerHeight + _laneTops[Math.Max(0, laneIndex)] - _scrollY;
            var ghost = drag.Clip with { Start = drag.Place.Start, End = drag.Place.End, Row = drag.DropLane is null ? drag.Clip.Row : 0 };
            DrawClip(dc, lane, ghost, top, scale, width, dpi, text, secondary, accent, 0.85, outline: true);
            if (drag.DropGap is { } gap && drag.Drop is not null)
            {
                var y = Math.Round(RulerHeight + _laneTops[gap] - _scrollY) - 0.5;
                dc.DrawLine(new Pen(accent, 3), new Point(HeaderWidth, y), new Point(width, y));
            }

            if (drag.Place.Snapped is { } snapped)
            {
                var x = Math.Round(HeaderWidth + scale.XOf(snapped)) + 0.5;
                dc.DrawLine(new Pen(Resource("WarningBrush", Brushes.Goldenrod), 1) { DashStyle = DashStyles.Dash }, new Point(x, RulerHeight), new Point(x, height));
            }

            var times = drag.Part switch
            {
                DragPart.Start => $"start {Seconds(drag.Place.Start)}",
                DragPart.End => $"end {Seconds(drag.Place.End)}",
                _ => $"{Seconds(drag.Place.Start)} – {Seconds(drag.Place.End)}" + (drag.Drop is { } drop ? drop.Insert ? " · new level" : $" · z {drop.ZIndex}" : ""),
            };
            var label = new FormattedText(times, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, text, dpi);
            var labelX = Math.Clamp(HeaderWidth + scale.XOf(drag.Part == DragPart.End ? drag.Place.End : drag.Place.Start), HeaderWidth + 2, Math.Max(HeaderWidth + 2, width - label.Width - 8));
            var labelY = Math.Max(RulerHeight + 2, top - label.Height - 4);
            dc.DrawRoundedRectangle(header, border, new Rect(labelX - 4, labelY - 1, label.Width + 8, label.Height + 2), 3, 3);
            dc.DrawText(label, new Point(labelX, labelY));
        }

        dc.Pop();

        // The lane titles.
        dc.DrawRectangle(header, null, new Rect(0, RulerHeight, HeaderWidth, height - RulerHeight));
        dc.DrawLine(border, new Point(HeaderWidth - 0.5, 0), new Point(HeaderWidth - 0.5, height));
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, HeaderWidth, height - RulerHeight)));
        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            var top = RulerHeight + _laneTops[i] - _scrollY;
            var swatch = lane.Kind switch
            {
                LaneKind.Captions => Fills[ClipKind.Caption],
                LaneKind.Audio => Fills[ClipKind.Audio],
                _ => Fills[lane.Clips.Select(c => c.Kind).Distinct().Count() == 1 ? lane.Clips[0].Kind : ClipKind.Group],
            };
            dc.DrawRectangle(swatch, null, new Rect(8, top + LanePad + 4, 3, Math.Min(RowHeight(lane.Kind), LaneHeight(lane) - 2 * LanePad) - 8));
            var title = new FormattedText(lane.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, secondary, dpi)
            {
                MaxTextWidth = HeaderWidth - 26,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(title, new Point(18, top + LanePad + (RowHeight(lane.Kind) - title.Height) / 2));
            dc.DrawLine(border, new Point(0, Math.Round(RulerHeight + _laneTops[i + 1] - _scrollY) - 0.5), new Point(HeaderWidth, Math.Round(RulerHeight + _laneTops[i + 1] - _scrollY) - 0.5));
        }

        dc.Pop();
        if (_lanes.Count == 0)
        {
            var empty = new FormattedText("No layers yet. Ask Claude to build the video, then fine-tune it here.", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, secondary, dpi);
            dc.DrawText(empty, new Point(HeaderWidth + 16, RulerHeight + 16));
        }

        DrawRuler(dc, scale, width, header, border, secondary, dpi);
    }

    private void DrawClip(DrawingContext dc, TimelineLane lane, TimelineClip clip, double laneTop, TimelineScale scale, double width, double dpi, Brush text, Brush secondary, Brush accent, double opacity, bool outline = false)
    {
        var x0 = HeaderWidth + scale.XOf(clip.Start);
        var x1 = HeaderWidth + scale.XOf(clip.End);
        if (x1 < HeaderWidth - 2 || x0 > width + 2)
        {
            return;
        }

        var rowHeight = RowHeight(lane.Kind);
        var rect = ClipRect(lane, clip, laneTop, scale);
        var selected = clip.LayerId == _selected;
        dc.PushOpacity(clip.Hidden ? opacity * 0.45 : opacity);
        dc.DrawRoundedRectangle(Fills[clip.Kind], selected || outline ? new Pen(selected ? Brushes.White : accent, selected ? 2 : 1.5) : null, rect, 3, 3);
        if (clip.Marks.Count > 0)
        {
            dc.DrawRectangle(MarkBrush, null, new Rect(rect.X + 2, rect.Bottom - 4, Math.Max(0, rect.Width - 4), 2.5));
        }

        var right = rect.Right - 4;
        if (clip.Children > 0 && rect.Width > 56)
        {
            var badge = new FormattedText($"+{clip.Children}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, text, dpi);
            var box = new Rect(right - badge.Width - 6, rect.Y + 3, badge.Width + 6, badge.Height + 1);
            dc.DrawRoundedRectangle(BadgeBrush, null, box, 3, 3);
            dc.DrawText(badge, new Point(box.X + 3, box.Y));
            right = box.X - 2;
        }

        // The name is laid out no wider than the part of the clip on screen: a long clip zoomed in is
        // wider than WPF lays text out in.
        right = Math.Min(right, width - 4);
        var left = Math.Max(rect.X + 5, HeaderWidth + 4);
        if (right - left > 14)
        {
            var name = clip.Label + (clip.Hidden ? clip.Kind == ClipKind.Audio ? " (muted)" : " (hidden)" : "") + (clip.SoundOff ? " · sound off" : "");
            var label = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, clip.Kind == ClipKind.Caption ? Face : Bold, 11, text, dpi)
            {
                MaxTextWidth = right - left,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var detailed = clip.Detail.Length > 0 && rowHeight >= 30;
            dc.DrawText(label, new Point(left, rect.Y + (detailed ? 2 : (rect.Height - label.Height) / 2)));
            if (detailed)
            {
                var detail = new FormattedText(clip.Detail, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, secondary, dpi)
                {
                    MaxTextWidth = right - left,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(detail, new Point(left, rect.Y + 2 + label.Height - 1));
            }
        }

        // The selected layer's keyframes, as diamonds along the clip's bottom edge.
        if (selected && !outline)
        {
            var y = rect.Bottom - KeyMark - 2;
            foreach (var time in KeysOn(clip))
            {
                var x = Math.Round(HeaderWidth + scale.XOf(time)) + 0.5;
                var diamond = new StreamGeometry();
                using (var context = diamond.Open())
                {
                    context.BeginFigure(new Point(x, y - KeyMark), true, true);
                    context.PolyLineTo([new Point(x + KeyMark, y), new Point(x, y + KeyMark), new Point(x - KeyMark, y)], true, false);
                }

                diamond.Freeze();
                dc.DrawGeometry(Brushes.White, KeyEdge, diamond);
            }
        }

        dc.Pop();
    }

    private void DrawRuler(DrawingContext dc, TimelineScale scale, double width, Brush header, Pen border, Brush secondary, double dpi)
    {
        dc.DrawRectangle(header, null, new Rect(0, 0, width, RulerHeight));
        dc.DrawLine(border, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, width - HeaderWidth, RulerHeight)));
        var pixelsPerSecond = scale.PixelsPerSecond;
        var step = TimelineScale.LabelStep(pixelsPerSecond);
        var minor = step / 5 * pixelsPerSecond >= 6 ? step / 5 : step;
        var first = Math.Max(0, Math.Floor(scale.TimeAt(0) / minor));
        var last = Math.Min(_contentEnd, scale.TimeAt(width - HeaderWidth)) / minor;
        for (var i = first; i <= last + 1e-9; i++)
        {
            var time = i * minor;
            var x = Math.Round(HeaderWidth + scale.XOf(time)) + 0.5;
            var major = Math.Abs(time / step - Math.Round(time / step)) < 1e-6;
            dc.DrawLine(border, new Point(x, RulerHeight - (major ? 10 : 5)), new Point(x, RulerHeight));
            if (major)
            {
                var label = new FormattedText(Timecode.Short(time, step < 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, secondary, dpi);
                dc.DrawText(label, new Point(x + 3, RulerHeight - 10 - label.Height + 2));
            }
        }

        // The project's end.
        var end = Math.Round(HeaderWidth + scale.XOf(_duration)) + 0.5;
        dc.DrawRectangle(PastEndBrush, null, new Rect(end, 0, Math.Max(0, width - end), RulerHeight));

        foreach (var marker in _markers)
        {
            var x = HeaderWidth + scale.XOf(marker.Time);
            var color = marker.Kind == MarkerKind.Chapter ? Resource("AccentBrush", Brushes.SteelBlue) : Resource("WarningBrush", Brushes.Goldenrod);
            var flag = new StreamGeometry();
            using (var g = flag.Open())
            {
                g.BeginFigure(new Point(x, 2), true, true);
                g.LineTo(new Point(x + 7, 2), true, false);
                g.LineTo(new Point(x + 7, 8), true, false);
                g.LineTo(new Point(x, 12), true, false);
            }

            flag.Freeze();
            dc.DrawGeometry(color, null, flag);
            var name = new FormattedText(string.IsNullOrEmpty(marker.Name) ? marker.Id : marker.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, color, dpi)
            {
                MaxTextWidth = Math.Max(1, Math.Min(160, width - x - 10)),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(name, new Point(x + 10, 0));
        }

        dc.Pop();
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_mediaDrop is { } drop)
        {
            // The lane the media goes in, or a line across all of them for a level of its own.
            var accent = Resource("AccentBrush", Brushes.SteelBlue);
            var at = Math.Round(HeaderWidth + Scale.XOf(drop.Time)) + 0.5;
            var (top, bottom) = drop.Lane is { } lane
                ? (RulerHeight + _laneTops[lane] - _scrollY, RulerHeight + _laneTops[lane + 1] - _scrollY)
                : (RulerHeight, ActualHeight);
            if (drop.Lane is not null)
            {
                var band = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0x8D, 0xFF));
                band.Freeze();
                dc.DrawRectangle(band, null, new Rect(HeaderWidth, top, Math.Max(0, ActualWidth - HeaderWidth), Math.Max(0, bottom - top)));
            }

            dc.DrawLine(new Pen(accent, 2), new Point(at, top), new Point(at, bottom));
        }

        var x = Math.Round(HeaderWidth + Scale.XOf(_playhead / (double)_fps)) + 0.5;
        if (x < HeaderWidth || x > ActualWidth)
        {
            return;
        }

        var brush = Resource("ErrorBrush", Brushes.Red);
        dc.DrawLine(new Pen(brush, 1), new Point(x, RulerHeight - 10), new Point(x, ActualHeight));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(x - 5, RulerHeight - 10), true, true);
            g.LineTo(new Point(x + 5, RulerHeight - 10), true, false);
            g.LineTo(new Point(x, RulerHeight - 4), true, false);
        }

        head.Freeze();
        dc.DrawGeometry(brush, null, head);
    }

    private int IndexOf(TimelineLane lane)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i].Key == lane.Key)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record ClipHit(TimelineLane Lane, TimelineClip Clip, DragPart Part);

    private sealed class Drag(TimelineClip clip, TimelineLane lane, DragPart part, TimelineLimits limits, Point pressed, double pressedOffset)
    {
        public TimelineClip Clip { get; } = clip;

        public TimelineLane Lane { get; } = lane;

        public DragPart Part { get; } = part;

        public TimelineLimits Limits { get; } = limits;

        public Point Pressed { get; } = pressed;

        // How far the lanes were scrolled when the mouse went down: scrolling at an edge moves the
        // clip on as far as the lanes went.
        public double PressedOffset { get; } = pressedOffset;

        public bool Active { get; set; }

        public IReadOnlyList<double>? Snaps { get; set; }

        public required DragPlace Place { get; set; }

        // The project shown when the mouse went down.
        public required Project From { get; init; }

        public LaneDrop? Drop { get; set; }

        public int? DropLane { get; set; }

        public int? DropGap { get; set; }
    }
}
