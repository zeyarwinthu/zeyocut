// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// A tile's drawing of what it does: a transition from clip A (blue) to clip B (red), an effect on a
// shape, or a title coming in. A sketch, not a render: applying one shows the real thing.
public sealed class EffectSketch : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(EffectSketch), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Color A = Color.FromRgb(0x2B, 0x4A, 0x8A);
    private static readonly Color B = Color.FromRgb(0xC9, 0x47, 0x3D);
    private static readonly Brush ABrush = Frozen(A);
    private static readonly Brush BBrush = Frozen(B);
    private static readonly Brush Dark = Frozen(Color.FromRgb(0x16, 0x18, 0x1D));
    private static readonly Brush Shape = Frozen(Color.FromRgb(0xF0, 0xA9, 0x4B));
    private static readonly Brush White = Frozen(Colors.White);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (w, h) = (ActualWidth, ActualHeight);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var all = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(all, 7, 7));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var transition = true;
        switch (Kind)
        {
            case "crossfade":
                dc.DrawRectangle(ABrush, null, all);
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, B.R, B.G, B.B), B, 0), null, all);
                break;
            case "fadeToBlack":
                dc.DrawRectangle(ABrush, null, new Rect(0, 0, w / 2, h));
                dc.DrawRectangle(BBrush, null, new Rect(w / 2, 0, w / 2, h));
                var black = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                black.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.2));
                black.GradientStops.Add(new GradientStop(Colors.Black, 0.5));
                black.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.8));
                dc.DrawRectangle(black, null, all);
                break;
            case "wipe":
                dc.DrawRectangle(ABrush, null, all);
                dc.DrawRectangle(BBrush, null, new Rect(0, 0, w * 0.45, h));
                dc.DrawRectangle(White, null, new Rect(w * 0.45 - 1, 0, 2, h));
                Arrow(dc, new Point(w * 0.52, h * 0.3), new Point(w * 0.7, h * 0.3));
                break;
            case "slide":
            case "whipPan":
                dc.DrawRectangle(ABrush, null, new Rect(-w * 0.35, 0, w, h));
                var shadow = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0x90, 0, 0, 0), 0);
                dc.DrawRectangle(shadow, null, new Rect(w * 0.5, 0, w * 0.1, h));
                dc.DrawRectangle(BBrush, null, new Rect(w * 0.6, 0, w * 0.4, h));
                if (Kind == "whipPan")
                {
                    var streak = new Pen(Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1.5);
                    foreach (var y in new[] { 0.3, 0.45, 0.6 })
                    {
                        dc.DrawLine(streak, new Point(w * 0.15, h * y), new Point(w * 0.85, h * y));
                    }
                }

                Arrow(dc, new Point(w * 0.82, h * 0.3), new Point(w * 0.64, h * 0.3));
                break;
            case "zoom":
                dc.DrawRectangle(ABrush, null, all);
                var inner = new Rect(w * 0.3, h * 0.25, w * 0.4, h * 0.5);
                dc.DrawRoundedRectangle(BBrush, new Pen(White, 1.5), inner, 3, 3);
                var corner = new Pen(White, 1.5);
                foreach (var (from, to) in new[] { (inner.TopLeft, new Point(w * 0.12, h * 0.1)), (inner.TopRight, new Point(w * 0.88, h * 0.1)),
                    (inner.BottomLeft, new Point(w * 0.12, h * 0.9)), (inner.BottomRight, new Point(w * 0.88, h * 0.9)) })
                {
                    dc.DrawLine(corner, from, to);
                }

                break;
            default:
                transition = false;
                DrawOther(dc, w, h, dpi);
                break;
        }

        if (transition)
        {
            Letter(dc, "A", new Point(6, h - 6), dpi, left: true);
            Letter(dc, "B", new Point(w - 6, h - 6), dpi, left: false);
        }

        dc.Pop();
    }

    private void DrawOther(DrawingContext dc, double w, double h, double dpi)
    {
        dc.DrawRectangle(Dark, null, new Rect(0, 0, w, h));
        var box = new Rect(w / 2 - h * 0.28, h * 0.22, h * 0.56, h * 0.56);
        switch (Kind)
        {
            case "blur":
                var soft = new RadialGradientBrush(Color.FromRgb(0xF0, 0xA9, 0x4B), Color.FromArgb(0, 0xF0, 0xA9, 0x4B));
                dc.DrawEllipse(soft, null, new Point(w / 2, h / 2), h * 0.42, h * 0.42);
                break;
            case "shadow":
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xB0, 0, 0, 0)), null, new Rect(box.X + 7, box.Y + 7, box.Width, box.Height), 6, 6);
                dc.DrawRoundedRectangle(Shape, null, box, 6, 6);
                break;
            case "outline":
                dc.DrawRoundedRectangle(Shape, new Pen(White, 4), box, 6, 6);
                break;
            case "tint":
                dc.DrawRoundedRectangle(Shape, null, box, 6, 6);
                dc.PushClip(new RectangleGeometry(new Rect(w / 2, 0, w / 2, h)));
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xB0, 0x4B, 0x8B, 0xF0)), null, box, 6, 6);
                dc.Pop();
                break;
            default:
                Title(dc, w, h, dpi);
                break;
        }
    }

    // A title coming in: faded, popping out of a smaller copy, or rising from below.
    private void Title(DrawingContext dc, double w, double h, double dpi)
    {
        FormattedText Text(double size, Brush brush) => new("Aa", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, size, brush, dpi);
        var big = Text(h * 0.36, White);
        var at = new Point((w - big.Width) / 2, (h - big.Height) / 2);
        switch (Kind)
        {
            case "fade":
                dc.PushOpacityMask(new LinearGradientBrush(Color.FromArgb(0x30, 0, 0, 0), Colors.Black, 0));
                dc.DrawText(big, at);
                dc.Pop();
                break;
            case "pop":
                var small = Text(h * 0.18, Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)));
                dc.DrawText(big, at);
                dc.DrawText(small, new Point((w - small.Width) / 2, (h - small.Height) / 2));
                var ring = new Pen(Shape, 1.5);
                dc.DrawEllipse(null, ring, new Point(w / 2, h / 2), big.Width * 0.75, big.Height * 0.6);
                break;
            default:
                dc.DrawText(big, new Point(at.X, at.Y - h * 0.06));
                var rise = new Pen(Shape, 1.5);
                dc.DrawLine(rise, new Point(w * 0.3, h * 0.88), new Point(w * 0.3, h * 0.66));
                dc.DrawLine(rise, new Point(w * 0.7, h * 0.88), new Point(w * 0.7, h * 0.66));
                break;
        }
    }

    private static void Arrow(DrawingContext dc, Point from, Point to)
    {
        var pen = new Pen(White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pen, from, to);
        var back = from.X < to.X ? -5 : 5;
        dc.DrawLine(pen, to, new Point(to.X + back, to.Y - 4));
        dc.DrawLine(pen, to, new Point(to.X + back, to.Y + 4));
    }

    private static void Letter(DrawingContext dc, string letter, Point corner, double dpi, bool left)
    {
        var text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, White, dpi);
        dc.DrawText(text, new Point(left ? corner.X : corner.X - text.Width, corner.Y - text.Height));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

// The lane of the chosen cut around it, in the Effects view: its clips, a row above another where
// they overlap, a mark at each of its cuts and the chosen one in the accent color, also once a
// transition overlaps its clips. A click near a mark chooses that cut.
public sealed class CutStrip : FrameworkElement
{
    private const double Reach = 8;
    private static readonly Brush Back = Frozen(0xFF, 0x16, 0x18, 0x1D);
    private static readonly Brush ClipText = Frozen(0xFF, 0xE8, 0xEA, 0xEE);
    private static readonly Brush Mark = Frozen(0xFF, 0x9A, 0xA1, 0xAE);
    private static readonly Typeface Face = new("Segoe UI");
    private IReadOnlyList<TimelineClip> _clips = [];
    private IReadOnlyList<TimelineCut> _cuts = [];
    private TimelineCut? _chosen;

    public CutStrip()
    {
        Height = 40;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public event Action<TimelineCut>? Chosen;

    public double From { get; private set; }

    public double To { get; private set; } = 10;

    public void Show(IReadOnlyList<TimelineClip> clips, IReadOnlyList<TimelineCut> cuts, TimelineCut? chosen, double duration)
    {
        (_clips, _cuts, _chosen) = (clips, cuts, chosen);
        var span = Math.Min(Math.Max(duration, 1), 12);
        var at = chosen?.Time ?? 0;
        From = Math.Clamp(at - span / 2, 0, Math.Max(0, duration - span));
        To = From + span;
        InvalidateVisual();
    }

    public double XOf(double time) => (time - From) / (To - From) * ActualWidth;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (_cuts.Select(c => (Cut: c, Distance: Math.Abs(XOf(c.Time) - x))).Where(c => c.Distance <= Reach).OrderBy(c => c.Distance).Select(c => c.Cut).FirstOrDefault() is { } cut)
        {
            Chosen?.Invoke(cut);
        }

        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, width, height), 6, 6);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), 6, 6));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rows = _clips.Count == 0 ? 1 : _clips.Max(c => c.Row) + 1;
        var band = Math.Max(1, (height - 14 - (rows - 1) * 2) / rows);
        foreach (var clip in _clips.Where(c => c.End > From && c.Start < To))
        {
            var left = XOf(clip.Start) + 1;
            var rect = new Rect(left, 7 + clip.Row * (band + 2), Math.Max(1, XOf(clip.End) - left - 1), band);
            dc.DrawRoundedRectangle(TimelineView.Fills[clip.Kind], null, rect, 4, 4);
            if (rect.Width >= 24 && rect.Height >= 12)
            {
                var label = new FormattedText(clip.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, rows > 1 ? 9 : 11, ClipText, dpi)
                {
                    MaxTextWidth = rect.Width - 12,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(label, new Point(rect.Left + 8, rect.Top + (rect.Height - label.Height) / 2));
            }
        }

        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.Orange;
        foreach (var cut in _chosen is { } marked && !_cuts.Contains(marked) ? _cuts.Append(marked) : _cuts)
        {
            var x = Math.Round(XOf(cut.Time));
            var chosen = cut == _chosen;
            dc.DrawRectangle(chosen ? accent : Mark, null, new Rect(x - (chosen ? 1.5 : 1), 2, chosen ? 3 : 2, height - 4));
            if (chosen)
            {
                dc.DrawRectangle(accent, null, new Rect(x - 5, 2, 10, 3));
                dc.DrawRectangle(accent, null, new Rect(x - 5, height - 5, 10, 3));
            }
        }

        dc.Pop();
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
