// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// The source viewer's scrubber: the file from its first frame to its last, the part between the in
// and out marks shaded and labeled IN and OUT, and the source playhead. Pressing or dragging moves the
// playhead (Seeked).
public sealed class SourceScrubber : FrameworkElement
{
    private const double Top = 16;
    private static readonly Brush Back = Frozen(0xFF, 0x16, 0x18, 0x1D);
    private static readonly Brush Band = Frozen(0x38, 0xF0, 0xA9, 0x4B);
    private static readonly Brush Edge = Frozen(0xFF, 0xF0, 0xA9, 0x4B);
    private static readonly Brush Label = Frozen(0xFF, 0xF5, 0xC2, 0x7A);
    private static readonly Brush Head = Frozen(0xFF, 0xFF, 0x5D, 0x5D);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private bool _pressed;

    public SourceScrubber()
    {
        Height = 46;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public event Action<int>? Seeked;

    public int Frames { get; private set; } = 1;

    public int Frame { get; private set; }

    public int? InFrame { get; private set; }

    public int? OutFrame { get; private set; }

    public void Show(int frames, int frame, int? inFrame, int? outFrame)
    {
        (Frames, Frame, InFrame, OutFrame) = (Math.Max(1, frames), frame, inFrame, outFrame);
        InvalidateVisual();
    }

    // Where a frame starts across the scrubber, and the frame a point is on.
    public double XOf(int frame) => ActualWidth * Math.Clamp(frame, 0, Frames) / Frames;

    public int FrameAt(Point point) => ActualWidth <= 0 ? 0 : Math.Clamp((int)Math.Floor(point.X / ActualWidth * Frames), 0, Frames - 1);

    public void Press(Point point)
    {
        _pressed = true;
        Seeked?.Invoke(FrameAt(point));
    }

    public void MoveTo(Point point)
    {
        if (_pressed)
        {
            Seeked?.Invoke(FrameAt(point));
        }
    }

    public void Release() => _pressed = false;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Press(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) => MoveTo(e.GetPosition(this));

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        Release();
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => Release();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var bar = new Rect(0, Top, width, Math.Max(0, ActualHeight - Top));
        dc.DrawRoundedRectangle(Back, null, bar, 6, 6);
        if (InFrame is not null || OutFrame is not null)
        {
            var left = XOf(InFrame ?? 0);
            var right = XOf(OutFrame is { } last ? last + 1 : Frames);
            dc.DrawRectangle(Band, null, new Rect(left, bar.Top, Math.Max(0, right - left), bar.Height));
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (InFrame is not null)
            {
                dc.DrawRectangle(Edge, null, new Rect(left, bar.Top, 2, bar.Height));
                dc.DrawText(Text("IN", dpi), new Point(Math.Max(0, left - 6), 0));
            }

            if (OutFrame is not null)
            {
                dc.DrawRectangle(Edge, null, new Rect(right - 2, bar.Top, 2, bar.Height));
                var label = Text("OUT", dpi);
                dc.DrawText(label, new Point(Math.Clamp(right - label.Width / 2, 0, Math.Max(0, width - label.Width)), 0));
            }
        }

        var x = XOf(Frame);
        dc.DrawRectangle(Head, null, new Rect(Math.Clamp(x - 1, 0, Math.Max(0, width - 2)), bar.Top - 4, 2, bar.Height + 8));
    }

    private static FormattedText Text(string text, double dpi) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, Label, dpi);

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

// The source viewer's "Where it will go": a stretch of the target lane around the timeline's
// playhead, its clips in the timeline's colors, the new clip dashed from the playhead for its length,
// and the playhead. Pressing moves the timeline's playhead there (Seeked, in seconds).
public sealed class SourceStrip : FrameworkElement
{
    private static readonly Brush Back = Frozen(0xFF, 0x16, 0x18, 0x1D);
    private static readonly Brush NewFill = Frozen(0xFF, 0x3A, 0x2C, 0x14);
    private static readonly Pen NewEdge = FrozenPen(Color.FromRgb(0xF0, 0xA9, 0x4B), 2, dashed: true);
    private static readonly Brush NewText = Frozen(0xFF, 0xF5, 0xC2, 0x7A);
    private static readonly Brush ClipText = Frozen(0xFF, 0xE8, 0xEA, 0xEE);
    private static readonly Brush Head = Frozen(0xFF, 0xFF, 0x5D, 0x5D);
    private static readonly Typeface Face = new("Segoe UI");
    private IReadOnlyList<TimelineClip> _clips = [];
    private double _at;
    private double _length;
    private string _label = "";

    public SourceStrip()
    {
        Height = 34;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public event Action<double>? Seeked;

    // The seconds of the timeline the strip shows.
    public double From { get; private set; }

    public double To { get; private set; } = 10;

    // The lane's clips, the playhead, the new clip's length and its label; duration is the project's.
    public void Show(IReadOnlyList<TimelineClip> clips, double at, double length, string label, double duration)
    {
        (_clips, _at, _length, _label) = (clips, at, length, label);
        var span = Math.Max(10, length * 3);
        From = Math.Clamp(at - span * 0.4, 0, Math.Max(0, Math.Max(duration, at + length) - span));
        To = From + span;
        InvalidateVisual();
    }

    public double XOf(double time) => (time - From) / (To - From) * ActualWidth;

    public double TimeAt(Point point) => ActualWidth <= 0 ? From : Math.Max(0, From + point.X / ActualWidth * (To - From));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Seeked?.Invoke(TimeAt(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, width, height), 6, 6);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), 6, 6));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var clip in _clips.Where(c => c.End > From && c.Start < To))
        {
            var left = XOf(clip.Start) + 1;
            var rect = new Rect(left, 5, Math.Max(1, XOf(clip.End) - left - 1), Math.Max(0, height - 10));
            dc.DrawRoundedRectangle(TimelineView.Fills[clip.Kind], null, rect, 4, 4);
            Label(dc, clip.Label, ClipText, rect, dpi);
        }

        var added = new Rect(XOf(_at), 3, Math.Max(2, XOf(_at + _length) - XOf(_at)), Math.Max(0, height - 6));
        dc.DrawRoundedRectangle(NewFill, NewEdge, added, 4, 4);
        Label(dc, _label, NewText, added, dpi);
        dc.DrawRectangle(Head, null, new Rect(XOf(_at) - 1, 0, 2, height));
        dc.Pop();
    }

    // A clip's name inside it, when there's room for some of it.
    private static void Label(DrawingContext dc, string text, Brush brush, Rect rect, double dpi)
    {
        if (rect.Width < 24)
        {
            return;
        }

        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, brush, dpi)
        {
            MaxTextWidth = rect.Width - 12,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(label, new Point(rect.Left + 8, rect.Top + (rect.Height - label.Height) / 2));
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness, bool dashed)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        if (dashed)
        {
            pen.DashStyle = new DashStyle([3, 2], 0);
        }

        pen.Freeze();
        return pen;
    }
}
