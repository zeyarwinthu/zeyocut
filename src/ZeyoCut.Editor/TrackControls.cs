// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// What the Track & blur view's viewer shows: the frame without its redactions and the tracks'
// boxes, the frame as it's exported, or the two side by side.
public enum TrackViewMode
{
    Boxes,
    Result,
    Split,
}

// The Track & blur view's viewer: the frame at the playhead fitted to the space with, in Boxes and
// Split, each track's box and label over the frame without its redactions, the selected track's
// motion trail, and the box drawn to track. A press draws a box while Drawing is on; otherwise it
// chooses a track by its box, and dragging the selected box or one of its corners moves it, which
// is a correction (BoxMoved). The boxes are the editor's own, never in the frame.
public sealed class TrackView : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(nameof(Frame), typeof(ImageSource), typeof(TrackView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // A box has to be at least this many pixels across in the view to be drawn.
    public const double MinSide = 6;

    private const double Pad = 16;
    private const double HandleReach = 7;
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush LabelText = Frozen(Color.FromRgb(0x0B, 0x0D, 0x12));
    private static readonly Brush Shade = Frozen(Color.FromArgb(0x99, 0x0B, 0x0C, 0x0F));

    private Size _canvas = new(1, 1);
    private ImageSource? _plain;
    private IReadOnlyList<TrackShape> _shapes = [];
    private IReadOnlyList<Point> _trail = [];
    private string? _selected;
    private Rect? _pending;
    private string _pendingLabel = "";
    private TrackViewMode _mode;
    private Gesture? _gesture;

    public TrackView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // A box drawn to track, on the canvas.
    public event Action<Rect>? BoxDrawn;

    // A track's box moved or resized at the playhead, on the canvas.
    public event Action<string, Rect>? BoxMoved;

    public event Action<string>? TrackChosen;

    // The frame as it's exported, with its redactions.
    public ImageSource? Frame
    {
        get => (ImageSource?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    // The frame without its redactions; Frame until it has been drawn.
    public ImageSource? Plain => _plain;

    public TrackViewMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            CancelDrag();
        }
    }

    // A press draws a box to track.
    public bool Drawing { get; set; }

    public IReadOnlyList<TrackShape> Shapes => _shapes;

    public IReadOnlyList<Point> Trail => _trail;

    public Rect? Pending => _pending;

    public bool Dragging => _gesture is { Active: true };

    // Where the frame is drawn, the canvas fitted into the space.
    public Rect FrameRect
    {
        get
        {
            var width = Math.Max(1, ActualWidth - 2 * Pad);
            var height = Math.Max(1, ActualHeight - 2 * Pad);
            var scale = Math.Min(width / _canvas.Width, height / _canvas.Height);
            var size = new Size(_canvas.Width * scale, _canvas.Height * scale);
            return new Rect((ActualWidth - size.Width) / 2, (ActualHeight - size.Height) / 2, size.Width, size.Height);
        }
    }

    // A point on the canvas where it is in the view, and back.
    public Point ToView(Point canvas)
    {
        var rect = FrameRect;
        return new Point(rect.X + canvas.X / _canvas.Width * rect.Width, rect.Y + canvas.Y / _canvas.Height * rect.Height);
    }

    public Point ToCanvas(Point view)
    {
        var rect = FrameRect;
        return new Point((view.X - rect.X) / rect.Width * _canvas.Width, (view.Y - rect.Y) / rect.Height * _canvas.Height);
    }

    public void Show(Size canvas, ImageSource? plain, IReadOnlyList<TrackShape> shapes, IReadOnlyList<Point> trail, string? selected, Rect? pending, string pendingLabel)
    {
        _canvas = canvas.Width > 0 && canvas.Height > 0 ? canvas : new Size(1, 1);
        (_plain, _shapes, _trail, _selected, _pending, _pendingLabel) = (plain, shapes, trail, selected, pending, pendingLabel);
        if (_gesture is { Track: { } track } && !shapes.Any(s => s.Id == track))
        {
            CancelDrag();
        }

        InvalidateVisual();
    }

    // What pressing the left button at a point, moving the mouse and letting go do. The mouse's
    // events call them, and so can a test.
    public void Press(Point point)
    {
        Focus();
        if (Drawing)
        {
            _gesture = new Gesture(GestureKind.Draw, point, null, new Rect(ToCanvas(point), ToCanvas(point)), -1);
            CaptureMouse();
            return;
        }

        if (_mode == TrackViewMode.Result || (_mode == TrackViewMode.Split && point.X > FrameRect.X + FrameRect.Width / 2))
        {
            return;
        }

        if (_shapes.FirstOrDefault(s => s.Id == _selected) is { Correctable: true } chosen && Corner(chosen, point) is { } corner)
        {
            _gesture = new Gesture(GestureKind.Resize, point, chosen.Id, chosen.Bounds, corner);
            CaptureMouse();
            return;
        }

        var hit = _shapes.LastOrDefault(s => ViewRect(s.Bounds) is var r && r.Contains(point));
        if (hit is null)
        {
            return;
        }

        if (hit.Id != _selected)
        {
            _selected = hit.Id;
            TrackChosen?.Invoke(hit.Id);
            InvalidateVisual();
        }

        if (hit.Correctable)
        {
            _gesture = new Gesture(GestureKind.Move, point, hit.Id, hit.Bounds, -1);
            CaptureMouse();
        }
    }

    public void MoveTo(Point point, bool pressed)
    {
        if (_gesture is not { } gesture || !pressed)
        {
            Cursor = Drawing ? Cursors.Cross
                : _shapes.FirstOrDefault(s => s.Id == _selected) is { Correctable: true } chosen && Corner(chosen, point) is { } corner ? (corner is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW)
                : _mode != TrackViewMode.Result && _shapes.Any(s => s.Correctable && ViewRect(s.Bounds).Contains(point)) ? Cursors.SizeAll
                : null;
            return;
        }

        if (!gesture.Active && Math.Abs(point.X - gesture.Pressed.X) < 3 && Math.Abs(point.Y - gesture.Pressed.Y) < 3)
        {
            return;
        }

        gesture.Active = true;
        var at = ToCanvas(point);
        var from = ToCanvas(gesture.Pressed);
        var start = gesture.Start;
        gesture.Now = gesture.Kind switch
        {
            GestureKind.Draw => new Rect(start.TopLeft, at),
            GestureKind.Move => new Rect(start.X + at.X - from.X, start.Y + at.Y - from.Y, start.Width, start.Height),
            _ => new Rect(Opposite(start, gesture.Corner), at),
        };
        InvalidateVisual();
    }

    public void Release()
    {
        if (_gesture is not { } gesture)
        {
            return;
        }

        _gesture = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        if (!gesture.Active)
        {
            return;
        }

        var box = gesture.Now;
        var view = ViewRect(box);
        if (view.Width < MinSide || view.Height < MinSide)
        {
            return;
        }

        if (gesture.Kind == GestureKind.Draw)
        {
            _pending = box;
            BoxDrawn?.Invoke(box);
        }
        else
        {
            BoxMoved?.Invoke(gesture.Track!, box);
        }
    }

    public void CancelDrag()
    {
        _gesture = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Press(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveTo(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Release();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var result = Frame;
        var plain = _plain ?? result;
        if (result is null && plain is null)
        {
            return;
        }

        var rect = FrameRect;
        var left = new Rect(rect.X, rect.Y, rect.Width / 2, rect.Height);
        var right = new Rect(rect.X + rect.Width / 2, rect.Y, rect.Width / 2, rect.Height);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        switch (_mode)
        {
            case TrackViewMode.Boxes:
                dc.DrawImage(plain, rect);
                break;
            case TrackViewMode.Result:
                dc.DrawImage(result, rect);
                break;
            default:
                dc.PushClip(new RectangleGeometry(left));
                dc.DrawImage(plain, rect);
                dc.Pop();
                dc.PushClip(new RectangleGeometry(right));
                dc.DrawImage(result, rect);
                dc.Pop();
                dc.DrawRectangle(Brushes.White, null, new Rect(right.X - 1, rect.Y, 2, rect.Height));
                Tag(dc, "Original", new Point(left.X + 8, rect.Y + 8), dpi);
                var shown = Text("Result", 11, Brushes.White, dpi);
                Tag(dc, "Result", new Point(rect.Right - shown.Width - 20, rect.Y + 8), dpi);
                break;
        }

        if (_mode == TrackViewMode.Result)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(_mode == TrackViewMode.Split ? left : rect));
        var moving = _gesture is { Active: true, Track: { } dragged } ? dragged : null;
        if (_shapes.FirstOrDefault(s => s.Id == _selected) is { } selected && _trail.Count > 0)
        {
            DrawTrail(dc, ColorOf(selected.Color), ToView(Middle(selected.Bounds)));
        }

        foreach (var shape in _shapes.OrderBy(s => s.Id == _selected))
        {
            if (shape.Id == moving)
            {
                continue;
            }

            DrawShape(dc, shape, dpi);
        }

        if (_pending is { } pending && _gesture is not { Active: true, Kind: GestureKind.Draw })
        {
            var box = ViewRect(pending);
            dc.DrawRectangle(null, new Pen(Brushes.White, 2) { DashStyle = new DashStyle([3, 2], 0) }, box);
            Label(dc, _pendingLabel, box.TopLeft, Brushes.White, dpi);
        }

        if (_gesture is { Active: true } gesture)
        {
            var box = ViewRect(gesture.Now);
            if (gesture.Kind == GestureKind.Draw)
            {
                dc.DrawRectangle(null, new Pen(Brushes.White, 2) { DashStyle = new DashStyle([3, 2], 0) }, box);
            }
            else if (_shapes.FirstOrDefault(s => s.Id == gesture.Track) is { } shape)
            {
                var brush = Frozen(ColorOf(shape.Color));
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0x1A, ColorOf(shape.Color).R, ColorOf(shape.Color).G, ColorOf(shape.Color).B)), new Pen(brush, 2), box, 4, 4);
                Label(dc, $"{shape.Id} · set by hand", box.TopLeft, brush, dpi);
                Handles(dc, box, brush);
            }
        }

        dc.Pop();
    }

    private void DrawShape(DrawingContext dc, TrackShape shape, double dpi)
    {
        var color = ColorOf(shape.Color);
        var brush = Frozen(color);
        var selected = shape.Id == _selected;
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(ToView(shape.Corners[0]), selected, true);
            g.PolyLineTo(shape.Corners.Skip(1).Select(ToView).ToList(), true, true);
        }

        outline.Freeze();
        dc.DrawGeometry(selected ? Frozen(Color.FromArgb(0x1A, color.R, color.G, color.B)) : null, new Pen(brush, 2) { LineJoin = PenLineJoin.Round }, outline);
        var bounds = ViewRect(shape.Bounds);
        Label(dc, shape.Label, bounds.TopLeft, brush, dpi);
        if (selected && shape.Correctable)
        {
            Handles(dc, bounds, brush);
        }
    }

    private void DrawTrail(DrawingContext dc, Color color, Point now)
    {
        var points = _trail.Select(ToView).Append(now).ToList();
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false, false);
            g.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        line.Freeze();
        dc.DrawGeometry(null, new Pen(Frozen(Color.FromArgb(0xB3, color.R, color.G, color.B)), 2) { DashStyle = new DashStyle([1, 2], 0), DashCap = PenLineCap.Round }, line);
        for (var i = 0; i < _trail.Count; i++)
        {
            var opacity = 0.3 + 0.5 * (i + 1) / _trail.Count;
            dc.DrawEllipse(Frozen(Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B)), null, points[i], 3, 3);
        }
    }

    private static void Handles(DrawingContext dc, Rect box, Brush edge)
    {
        var pen = new Pen(edge, 1);
        foreach (var corner in new[] { box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft })
        {
            dc.DrawRectangle(Brushes.White, pen, new Rect(corner.X - 4, corner.Y - 4, 8, 8));
        }
    }

    private static void Label(DrawingContext dc, string text, Point corner, Brush back, double dpi)
    {
        if (text.Length == 0)
        {
            return;
        }

        var label = Text(text, 10, LabelText, dpi, bold: true);
        var top = corner.Y - label.Height - 6 < 0 ? corner.Y + 2 : corner.Y - label.Height - 6;
        var box = new Rect(corner.X - 1, top, label.Width + 12, label.Height + 4);
        dc.DrawRoundedRectangle(back, null, box, 4, 4);
        dc.DrawText(label, new Point(box.X + 6, box.Y + 2));
    }

    private static void Tag(DrawingContext dc, string text, Point at, double dpi)
    {
        var label = Text(text, 11, Brushes.White, dpi);
        dc.DrawRoundedRectangle(Shade, null, new Rect(at.X, at.Y, label.Width + 12, label.Height + 4), 4, 4);
        dc.DrawText(label, new Point(at.X + 6, at.Y + 2));
    }

    private static FormattedText Text(string text, double size, Brush brush, double dpi, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? LabelFace : new Typeface("Segoe UI"), size, brush, dpi);

    // The corner of the box at a point (0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left).
    private int? Corner(TrackShape shape, Point point)
    {
        var box = ViewRect(shape.Bounds);
        var corners = new[] { box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft };
        for (var i = 0; i < corners.Length; i++)
        {
            if (Math.Abs(corners[i].X - point.X) <= HandleReach && Math.Abs(corners[i].Y - point.Y) <= HandleReach)
            {
                return i;
            }
        }

        return null;
    }

    private static Point Opposite(Rect box, int corner) => corner switch
    {
        0 => box.BottomRight,
        1 => box.BottomLeft,
        2 => box.TopLeft,
        _ => box.TopRight,
    };

    private Rect ViewRect(Rect canvas) => new(ToView(canvas.TopLeft), ToView(canvas.BottomRight));

    private static Point Middle(Rect box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);

    private static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private enum GestureKind
    {
        Draw,
        Move,
        Resize,
    }

    private sealed class Gesture(GestureKind kind, Point pressed, string? track, Rect start, int corner)
    {
        public GestureKind Kind { get; } = kind;

        public Point Pressed { get; } = pressed;

        public string? Track { get; } = track;

        public Rect Start { get; } = start;

        public int Corner { get; } = corner;

        public bool Active { get; set; }

        public Rect Now { get; set; } = start;
    }
}

// The Track & blur view's tracking timeline: a row for each track of the clip, its name, and over the
// clip's time on the timeline the spans it's tracked (green) and unsure or lost (orange), a diamond
// at each box set by hand, and the playhead. Pressing on the rows seeks there and chooses the row's
// track; dragging scrubs.
public sealed class TrackStrip : FrameworkElement
{
    public const double NameWidth = 180;
    public const double RulerHeight = 24;
    public const double RowHeight = 44;

    private static readonly Brush NameBack = Frozen(0x12, 0x14, 0x18);
    private static readonly Brush RulerBack = Frozen(0x10, 0x12, 0x16);
    private static readonly Brush ChosenBack = Frozen(0x17, 0x1C, 0x2A);
    private static readonly Brush Line = Frozen(0x23, 0x26, 0x2E);
    private static readonly Brush RowLine = Frozen(0x1A, 0x1D, 0x24);
    private static readonly Brush Tick = Frozen(0x2A, 0x2E, 0x37);
    private static readonly Brush TickText = Frozen(0x7C, 0x83, 0x92);
    private static readonly Brush NameText = Frozen(0xE8, 0xEA, 0xEE);
    private static readonly Brush Quiet = Frozen(0x9A, 0xA1, 0xAE);
    private static readonly Brush Tracked = Frozen(0x2F, 0xA8, 0x66);
    private static readonly Brush Unsure = Frozen(0xD9, 0x90, 0x2F);
    private static readonly Brush PlayheadLine = Frozen(0xFF, 0x5D, 0x5D);
    private static readonly Brush DiamondEdge = Frozen(0x0E, 0x0F, 0x12);
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Mono = new("Consolas");

    private IReadOnlyList<TrackRow> _rows = [];
    private double _from;
    private double _to = 1;
    private double _playhead;
    private string? _selected;
    private bool _scrubbing;

    public TrackStrip()
    {
        Height = RulerHeight + RowHeight;
        Focusable = false;
        ClipToBounds = true;
    }

    public event Action<double>? Seek;

    public event Action<string>? TrackChosen;

    public IReadOnlyList<TrackRow> Rows => _rows;

    public double From => _from;

    public double To => _to;

    public double Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            InvalidateVisual();
        }
    }

    public void Show(IReadOnlyList<TrackRow> rows, double from, double to, string? selected)
    {
        (_rows, _from, _to, _selected) = (rows, from, Math.Max(to, from + 1e-3), selected);
        Height = RulerHeight + Math.Max(1, rows.Count) * RowHeight;
        InvalidateVisual();
    }

    public double XOf(double time) => NameWidth + (time - _from) / (_to - _from) * Math.Max(1, ActualWidth - NameWidth - 8);

    public double TimeAt(double x) => Math.Clamp(_from + (x - NameWidth) / Math.Max(1, ActualWidth - NameWidth - 8) * (_to - _from), _from, _to);

    public void Press(Point point)
    {
        var row = (int)Math.Floor((point.Y - RulerHeight) / RowHeight);
        if (point.Y >= RulerHeight && row >= 0 && row < _rows.Count && _rows[row].Id != _selected)
        {
            _selected = _rows[row].Id;
            TrackChosen?.Invoke(_rows[row].Id);
        }

        if (point.X >= NameWidth)
        {
            _scrubbing = true;
            CaptureMouse();
            Seek?.Invoke(TimeAt(point.X));
        }
    }

    public void MoveTo(Point point, bool pressed)
    {
        if (_scrubbing && pressed)
        {
            Seek?.Invoke(TimeAt(point.X));
        }
    }

    public void Release()
    {
        _scrubbing = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Press(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveTo(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Release();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(NameBack, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(RulerBack, null, new Rect(NameWidth, 0, Math.Max(0, width - NameWidth), RulerHeight));
        dc.DrawRectangle(Line, null, new Rect(0, RulerHeight - 1, width, 1));
        dc.DrawRectangle(Line, null, new Rect(NameWidth - 1, 0, 1, height));

        var step = Step(_to - _from);
        for (var t = Math.Ceiling(_from / step - 1e-9) * step; t <= _to + 1e-9; t += step)
        {
            var x = Math.Round(XOf(t));
            dc.DrawRectangle(Tick, null, new Rect(x, 0, 1, RulerHeight - 1));
            var label = new FormattedText(t.ToString(step < 1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, TickText, dpi);
            if (x + 4 + label.Width < width)
            {
                dc.DrawText(label, new Point(x + 4, 6));
            }
        }

        if (_rows.Count == 0)
        {
            var empty = new FormattedText("No tracked objects on this clip yet", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, Quiet, dpi);
            dc.DrawText(empty, new Point(NameWidth + 12, RulerHeight + (RowHeight - empty.Height) / 2));
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var top = RulerHeight + i * RowHeight;
            if (row.Id == _selected)
            {
                dc.DrawRectangle(ChosenBack, null, new Rect(0, top, width, RowHeight));
            }

            dc.DrawRectangle(RowLine, null, new Rect(0, top + RowHeight - 1, width, 1));
            dc.DrawRoundedRectangle(Frozen((Color)ColorConverter.ConvertFromString(row.Color)), null, new Rect(12, top + RowHeight / 2 - 5, 10, 10), 3, 3);
            var name = new FormattedText(row.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, NameText, dpi)
            {
                MaxTextWidth = NameWidth - 44,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(name, new Point(30, top + (RowHeight - name.Height) / 2));
            for (var s = 0; s < row.Spans.Count; s++)
            {
                var span = row.Spans[s];
                var left = XOf(span.From);
                var bar = new Rect(left, top + 16, Math.Max(1, XOf(span.To) - left), 12);
                dc.DrawRectangle(span.Unsure ? Unsure : Tracked, null, bar);
            }

            foreach (var anchor in row.Anchors)
            {
                var x = XOf(anchor);
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(x, top + 15), true, true);
                    g.PolyLineTo([new Point(x + 7, top + 22), new Point(x, top + 29), new Point(x - 7, top + 22)], true, true);
                }

                diamond.Freeze();
                dc.DrawGeometry(Brushes.White, new Pen(DiamondEdge, 1), diamond);
            }
        }

        if (_playhead >= _from && _playhead <= _to)
        {
            dc.DrawRectangle(PlayheadLine, null, new Rect(Math.Round(XOf(_playhead)) - 1, 0, 2, height));
        }
    }

    // A tick every 0.1, 0.2, 0.5, 1, 2, 5, 10… seconds, about 8 across.
    public static double Step(double span)
    {
        var rough = Math.Max(span, 1e-3) / 8;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        foreach (var factor in new[] { 1, 2, 5, 10 })
        {
            if (factor * power >= rough)
            {
                return Math.Max(0.1, factor * power);
            }
        }

        return Math.Max(0.1, 10 * power);
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(Color.FromRgb(r, g, b));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

// A tracked object's bar in the Track & blur view's list: tracked parts green, unsure or lost ones
// orange.
public sealed class TrackBar : FrameworkElement
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(nameof(Parts), typeof(IReadOnlyList<TrackPart>), typeof(TrackBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Empty = Frozen(0x2A, 0x2E, 0x37);
    private static readonly Brush Tracked = Frozen(0x3D, 0xD6, 0x8C);
    private static readonly Brush Unsure = Frozen(0xF0, 0xA9, 0x4B);

    public TrackBar()
    {
        Height = 6;
    }

    public IReadOnlyList<TrackPart>? Parts
    {
        get => (IReadOnlyList<TrackPart>?)GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(bounds, 3, 3));
        dc.DrawRectangle(Empty, null, bounds);
        foreach (var part in Parts ?? [])
        {
            dc.DrawRectangle(part.Unsure ? Unsure : Tracked, null, new Rect(part.From * ActualWidth, 0, Math.Max(1, (part.To - part.From) * ActualWidth), ActualHeight));
        }

        dc.Pop();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
