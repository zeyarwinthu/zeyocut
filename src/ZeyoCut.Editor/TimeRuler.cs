// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// The project's time from start to end: seconds, the markers (chapters blue, scenes yellow) and the
// playhead. Clicking or dragging on it moves the playhead.
public sealed class TimeRuler : FrameworkElement
{
    public const double Inset = 12;

    private static readonly double[] Steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
    private static readonly Typeface Face = new("Segoe UI");

    private int _frames;
    private int _fps = 30;
    private IReadOnlyList<Marker> _markers = [];
    private int _playhead;

    public TimeRuler()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    // Raised with the frame the playhead was moved to.
    public event EventHandler<int>? Seek;

    public int Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            InvalidateVisual();
        }
    }

    // Seconds between labelled ticks, the shortest that leaves room for a label.
    public static double LabelStep(double pixelsPerSecond, double minimumPixels = 64)
    {
        foreach (var step in Steps)
        {
            if (step * pixelsPerSecond >= minimumPixels)
            {
                return step;
            }
        }

        return Steps[^1];
    }

    // The frame shown at x, for a ruler width pixels wide: frame f covers f/fps to (f+1)/fps.
    public static int FrameAt(double x, double width, int frames)
    {
        if (frames <= 0 || width <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor(x / width * frames), 0, frames - 1);
    }

    public static double XOf(int frame, double width, int frames) => frames <= 0 ? 0 : frame * width / frames;

    public void Show(int frames, int fps, IReadOnlyList<Marker> markers)
    {
        _frames = frames;
        _fps = fps;
        _markers = markers;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 44);

    protected override void OnRender(DrawingContext dc)
    {
        var background = Brush("PanelBrush", Brushes.Black);
        var text = Brush("SecondaryTextBrush", Brushes.Gray);
        var ticks = Brush("BorderBrush", Brushes.DimGray);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var width = ActualWidth - 2 * Inset;
        if (_frames <= 0 || width <= 0)
        {
            return;
        }

        var duration = _frames / (double)_fps;
        var pixelsPerSecond = width / duration;
        var step = LabelStep(pixelsPerSecond);
        var tickPen = new Pen(ticks, 1);
        var minor = step / 5 * pixelsPerSecond >= 6 ? step / 5 : step;
        for (var i = 0; i * minor <= duration + 1e-9; i++)
        {
            var time = i * minor;
            var x = Math.Round(Inset + time * pixelsPerSecond) + 0.5;
            var major = Math.Abs(time / step - Math.Round(time / step)) < 1e-6;
            dc.DrawLine(tickPen, new Point(x, ActualHeight - (major ? 12 : 6)), new Point(x, ActualHeight));
            if (major)
            {
                var label = new FormattedText(Timecode.Short(time, step < 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, text, dpi);
                if (x + 3 + label.Width <= ActualWidth)
                {
                    dc.DrawText(label, new Point(x + 3, ActualHeight - 12 - label.Height + 2));
                }
            }
        }

        foreach (var marker in _markers)
        {
            var x = Inset + Math.Clamp(marker.Time, 0, duration) * pixelsPerSecond;
            var color = marker.Kind == MarkerKind.Chapter ? Brush("AccentBrush", Brushes.SteelBlue) : Brush("WarningBrush", Brushes.Goldenrod);
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
                MaxTextWidth = Math.Max(1, Math.Min(160, ActualWidth - x - 10)),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(name, new Point(x + 10, 1));
        }

        var playhead = Brush("ErrorBrush", Brushes.Red);
        var px = Math.Round(Inset + XOf(_playhead, width, _frames)) + 0.5;
        dc.DrawLine(new Pen(playhead, 1), new Point(px, 14), new Point(px, ActualHeight));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(px - 5, 14), true, true);
            g.LineTo(new Point(px + 5, 14), true, false);
            g.LineTo(new Point(px, 20), true, false);
        }

        head.Freeze();
        dc.DrawGeometry(playhead, null, head);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        SeekTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
        {
            SeekTo(e.GetPosition(this).X);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SeekTo(double x)
    {
        var frame = FrameAt(x - Inset, ActualWidth - 2 * Inset, _frames);
        if (frame != _playhead)
        {
            Seek?.Invoke(this, frame);
        }
    }

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
