// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// A control of a mixer strip that sets one number: dragged (from where it was grabbed), stepped
// with the keys, or double-clicked back to its default. It shows the number it's dragged to and
// says what it was let go at; the strip saves it then.
public abstract class MixerHandle : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(MixerHandle),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    protected static readonly Typeface Mono = new("Consolas");
    protected static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x2A, 0x2E, 0x37));
    protected static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x7C, 0x83, 0x92));
    protected static readonly Brush KnobBrush = Frozen(Color.FromRgb(0xD9, 0xDC, 0xE3));
    protected static readonly Brush GripBrush = Frozen(Color.FromRgb(0x5C, 0x62, 0x70));
    protected static readonly Pen FocusPen = new(Frozen(Color.FromRgb(0x7B, 0x93, 0xFF)), 1.5);

    private double? _dragged;
    private Vector _grab;
    private bool _pressed;

    protected MixerHandle()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
    }

    // While dragged or stepped: the value it's at.
    public event EventHandler<double>? Moved;

    // Let go, or stepped: the value to save.
    public event EventHandler<double>? Committed;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    // The value drawn: where it's being dragged, or the saved one.
    public double Shown => _dragged ?? Value;

    // Where the handle is drawn for a value, and the value at a handle's place.
    public abstract Point HandleAt(double value);

    protected abstract Rect HandleBounds(double value);

    protected abstract double ValueAt(Point handle);

    protected abstract double Default { get; }

    // The value a key steps to, or null for a key it doesn't take.
    protected abstract double? Stepped(Key key, double value);

    // What pressing at a point, moving and letting go do; the mouse calls them, and so can a test.
    public void Press(Point point)
    {
        Focus();
        _pressed = true;
        _grab = HandleBounds(Shown).Contains(point) ? point - HandleAt(Shown) : default;
        CaptureMouse();
        MoveTo(point, pressed: true);
    }

    public void MoveTo(Point point, bool pressed)
    {
        if (!_pressed || !pressed)
        {
            return;
        }

        _dragged = ValueAt(point - _grab);
        Moved?.Invoke(this, _dragged.Value);
        InvalidateVisual();
    }

    public void Release()
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        ReleaseMouseCapture();
        if (_dragged is { } value)
        {
            _dragged = null;
            InvalidateVisual();
            if (value != Value)
            {
                Committed?.Invoke(this, value);
            }
        }
    }

    // Sets the value as the keys do: shown at once and saved.
    public void Commit(double value)
    {
        Moved?.Invoke(this, value);
        if (value != Value)
        {
            Committed?.Invoke(this, value);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _pressed = false;
            _dragged = null;
            ReleaseMouseCapture();
            Commit(Default);
            return;
        }

        Press(e.GetPosition(this));
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
        if (_pressed)
        {
            Release();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_pressed && Stepped(e.Key, Value) is { } value)
        {
            e.Handled = true;
            Commit(value);
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

// A strip's fader: its role's gain in dB on the scale's marks, to the half dB. Up and Down step half
// a dB, Page Up and Page Down 3 dB, Home or a double-click goes back to 0 dB.
public sealed class MixerFader : MixerHandle
{
    private const double Pad = 9;
    private const double TrackWidth = 6;

    protected override double Default => 0;

    private double TrackX => ActualWidth / 2 + 8;

    private (double Top, double Bottom) Span => (Pad, Math.Max(Pad + 1, ActualHeight - Pad));

    public override Point HandleAt(double value)
    {
        var (top, bottom) = Span;
        return new Point(TrackX, bottom - MixerModel.Position(value) * (bottom - top));
    }

    protected override Rect HandleBounds(double value)
    {
        var at = HandleAt(value);
        return new Rect(at.X - 17, at.Y - 10, 34, 20);
    }

    protected override double ValueAt(Point handle)
    {
        var (top, bottom) = Span;
        return MixerModel.GainAt((bottom - handle.Y) / (bottom - top));
    }

    protected override double? Stepped(Key key, double value) => key switch
    {
        Key.Up => Math.Min(MixerModel.FaderMarks[0], value + 0.5),
        Key.Down => Math.Max(MixerModel.FaderMarks[^1], value - 0.5),
        Key.PageUp => Math.Min(MixerModel.FaderMarks[0], value + 3),
        Key.PageDown => Math.Max(MixerModel.FaderMarks[^1], value - 3),
        Key.Home => Default,
        _ => null,
    };

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (top, bottom) = Span;
        var x = TrackX;
        foreach (var mark in MixerModel.FaderMarks)
        {
            var y = bottom - MixerModel.Position(mark) * (bottom - top);
            var label = new FormattedText(mark > 0 ? $"+{mark}" : mark.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, LabelBrush, dpi);
            dc.DrawText(label, new Point(x - 22 - label.Width, y - label.Height / 2));
        }

        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(x - TrackWidth / 2, top, TrackWidth, bottom - top), 3, 3);
        var knob = HandleAt(Shown);
        var body = new Rect(knob.X - 15, knob.Y - 8, 30, 16);
        dc.DrawRoundedRectangle(KnobBrush, IsKeyboardFocused ? FocusPen : null, body, 4, 4);
        dc.DrawRectangle(GripBrush, null, new Rect(knob.X - 9, knob.Y - 1, 18, 2));
    }
}

// A strip's pan, from -1 (left) to 1 (right), to the hundredth; it keeps to the middle near it.
// Left and Right step 5%, Home or a double-click centers it.
public sealed class MixerPan : MixerHandle
{
    private const double Pad = 4;

    protected override double Default => 0;

    private (double Left, double Right) Span => (Pad, Math.Max(Pad + 1, ActualWidth - Pad));

    public override Point HandleAt(double value)
    {
        var (left, right) = Span;
        return new Point(left + (Math.Clamp(value, -1, 1) + 1) / 2 * (right - left), ActualHeight / 2);
    }

    protected override Rect HandleBounds(double value)
    {
        var at = HandleAt(value);
        return new Rect(at.X - 5, 0, 10, ActualHeight);
    }

    protected override double ValueAt(Point handle)
    {
        var (left, right) = Span;
        var pan = Math.Round(Math.Clamp((handle.X - left) / (right - left) * 2 - 1, -1, 1), 2);
        return Math.Abs(pan) < 0.04 ? 0 : pan;
    }

    protected override double? Stepped(Key key, double value) => key switch
    {
        Key.Left => Math.Max(-1, Math.Round(value - 0.05, 2)),
        Key.Right => Math.Min(1, Math.Round(value + 0.05, 2)),
        Key.Home => Default,
        _ => null,
    };

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var (left, right) = Span;
        var middle = ActualHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(left, middle - 2, right - left, 4), 2, 2);
        var at = HandleAt(Shown);
        dc.DrawRoundedRectangle(Brushes.White, IsKeyboardFocused ? FocusPen : null, new Rect(at.X - 2, middle - 6, 4, 12), 2, 2);
    }
}

// The Master strip's meter: the peaks of the left and right of the mix the preview plays, in dBFS
// on the meter's marks, green, then yellow, then red near full scale.
public sealed class MixerMeter : FrameworkElement
{
    private const double Pad = 9;
    private const double BarWidth = 8;
    private static readonly Typeface Mono = new("Consolas");
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x7C, 0x83, 0x92));
    private static readonly Brush EmptyBrush = Frozen(Color.FromRgb(0x1A, 0x1D, 0x24));
    private static readonly Brush BarBrush = Bars();

    private (double Left, double Right) _level = (double.NegativeInfinity, double.NegativeInfinity);

    // Each side's level in dBFS; minus infinity for none.
    public (double Left, double Right) Level
    {
        get => _level;
        set
        {
            if (_level != value)
            {
                _level = value;
                InvalidateVisual();
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (top, bottom) = (Pad, Math.Max(Pad + 1, ActualHeight - Pad));
        var x = ActualWidth / 2 + 2;
        foreach (var mark in MixerModel.MeterMarks)
        {
            var y = bottom - MixerModel.MeterPosition(mark) * (bottom - top);
            var label = new FormattedText(mark.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 9, LabelBrush, dpi);
            dc.DrawText(label, new Point(x - 10 - label.Width, y - label.Height / 2));
        }

        foreach (var (left, level) in new[] { (x, _level.Left), (x + BarWidth + 3, _level.Right) })
        {
            var bar = new Rect(left, top, BarWidth, bottom - top);
            dc.DrawRoundedRectangle(BarBrush, null, bar, 2, 2);
            var covered = (1 - MixerModel.MeterPosition(level)) * bar.Height;
            if (covered > 0)
            {
                dc.DrawRectangle(EmptyBrush, null, new Rect(bar.X, bar.Y, bar.Width, covered));
            }
        }
    }

    private static Brush Bars()
    {
        var green = Color.FromRgb(0x2F, 0xBF, 0x71);
        var yellow = Color.FromRgb(0xE8, 0xC1, 0x4A);
        var red = Color.FromRgb(0xFF, 0x5D, 0x5D);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0),
            GradientStops =
            [
                new GradientStop(green, 0), new GradientStop(green, 0.68), new GradientStop(yellow, 0.68),
                new GradientStop(yellow, 0.86), new GradientStop(red, 0.86), new GradientStop(red, 1),
            ],
        };
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
