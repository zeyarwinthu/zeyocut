// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// What the Color view's viewer shows: the clip as graded, as shot left of a line and graded right
// of it, or as shot.
public enum GradeViewMode
{
    After,
    Split,
    Before,
}

// The Color view's viewer: the frame at the playhead fitted to the space as the preview draws it
// (Frame), with the clip's color as shot (Before), or the two either side of a line that a drag
// moves. The line, its handle and the tags are the editor's own, never in the frame.
public sealed class GradeView : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(nameof(Frame), typeof(ImageSource), typeof(GradeView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Pad = 16;
    private const double HandleRadius = 14;
    private static readonly Brush Shade = Frozen(Color.FromArgb(0xCC, 0x0A, 0x0C, 0x10));
    private static readonly Brush HandleMark = Frozen(Color.FromRgb(0x0E, 0x0F, 0x12));
    private static readonly Geometry Arrows = (Geometry)Geometry.Parse("M9,6 L3,12 L9,18 M15,6 L21,12 L15,18").GetAsFrozen();

    private Size _canvas = new(1, 1);
    private ImageSource? _before;
    private GradeViewMode _mode = GradeViewMode.Split;
    private double _split = 0.5;
    private bool _dragging;

    public GradeView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // The frame as the preview draws it, graded.
    public ImageSource? Frame
    {
        get => (ImageSource?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    // The frame with the clip as shot; null while it's the frame itself or isn't drawn yet.
    public ImageSource? Before => _before;

    public GradeViewMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Release();
            InvalidateVisual();
        }
    }

    // Where the line is across the frame, from 0 at its left edge to 1 at its right.
    public double Split
    {
        get => _split;
        set
        {
            _split = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    public bool Dragging => _dragging;

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

    public void Show(Size canvas, ImageSource? before)
    {
        _canvas = canvas.Width > 0 && canvas.Height > 0 ? canvas : new Size(1, 1);
        _before = before;
        InvalidateVisual();
    }

    // What pressing the left button at a point, moving the mouse and letting go do: in Split, the
    // line goes where the mouse is. The mouse's events call them, and so can a test.
    public void Press(Point point)
    {
        if (_mode != GradeViewMode.Split)
        {
            return;
        }

        Focus();
        _dragging = true;
        CaptureMouse();
        MoveTo(point, pressed: true);
    }

    public void MoveTo(Point point, bool pressed)
    {
        if (_dragging && pressed)
        {
            var rect = FrameRect;
            Split = (point.X - rect.X) / rect.Width;
            return;
        }

        Cursor = _mode == GradeViewMode.Split && FrameRect.Contains(point) ? Cursors.SizeWE : null;
    }

    public void Release()
    {
        _dragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
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

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var after = Frame;
        var before = _before ?? after;
        if (after is null)
        {
            return;
        }

        var rect = FrameRect;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        switch (_mode)
        {
            case GradeViewMode.After:
                dc.DrawImage(after, rect);
                break;
            case GradeViewMode.Before:
                dc.DrawImage(before, rect);
                DrawTag(dc, "Before", new Point(rect.X + 10, rect.Y + 10), dpi);
                break;
            default:
                var x = rect.X + rect.Width * _split;
                dc.PushClip(new RectangleGeometry(new Rect(rect.X, rect.Y, x - rect.X, rect.Height)));
                dc.DrawImage(before, rect);
                dc.Pop();
                dc.PushClip(new RectangleGeometry(new Rect(x, rect.Y, rect.Right - x, rect.Height)));
                dc.DrawImage(after, rect);
                dc.Pop();
                dc.DrawRectangle(Brushes.White, null, new Rect(x - 1, rect.Y, 2, rect.Height));
                var middle = new Point(x, rect.Y + rect.Height / 2);
                dc.DrawEllipse(Brushes.White, null, middle, HandleRadius, HandleRadius);
                dc.PushTransform(new MatrixTransform(14 / 24.0, 0, 0, 14 / 24.0, middle.X - 7, middle.Y - 7));
                dc.DrawGeometry(null, new Pen(HandleMark, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Arrows);
                dc.Pop();
                DrawTag(dc, "Before", new Point(rect.X + 10, rect.Y + 10), dpi);
                var shown = Text("After", dpi);
                DrawTag(dc, "After", new Point(rect.Right - shown.Width - 24, rect.Y + 10), dpi);
                break;
        }
    }

    private static void DrawTag(DrawingContext dc, string text, Point at, double dpi)
    {
        var label = Text(text, dpi);
        dc.DrawRoundedRectangle(Shade, null, new Rect(at.X, at.Y, label.Width + 14, label.Height + 4), 5, 5);
        dc.DrawText(label, new Point(at.X + 7, at.Y + 2));
    }

    private static FormattedText Text(string text, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, dpi);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
