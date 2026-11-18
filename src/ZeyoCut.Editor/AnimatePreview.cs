// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// The Animate view's preview: the frame at the playhead as the Edit view's preview draws it, fitted
// to the space, with the onion skin's ghosts over it and the selected layer's motion path, a dashed
// line through where its anchor goes with a dot at each keyframe. The ghosts and the path are the
// editor's own, never in the frame.
public sealed class AnimatePreview : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(nameof(Frame), typeof(ImageSource), typeof(AnimatePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Pad = 16;

    private Size _canvas = new(1, 1);
    private ImageSource? _ghosts;
    private IReadOnlyList<Point> _path = [];
    private IReadOnlyList<Point> _keys = [];

    public ImageSource? Frame
    {
        get => (ImageSource?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public ImageSource? Ghosts => _ghosts;

    public IReadOnlyList<Point> Path => _path;

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

    public void Show(Size canvas, ImageSource? ghosts, IReadOnlyList<Point> path, IReadOnlyList<Point> keys)
    {
        _canvas = canvas.Width > 0 && canvas.Height > 0 ? canvas : new Size(1, 1);
        _ghosts = ghosts;
        _path = path;
        _keys = keys;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Frame is not { } frame)
        {
            return;
        }

        var rect = FrameRect;
        dc.DrawImage(frame, rect);
        dc.PushClip(new RectangleGeometry(rect));
        if (_ghosts is { } ghosts)
        {
            dc.DrawImage(ghosts, rect);
        }

        if (_path.Count > 1)
        {
            Point On(Point canvas) => new(rect.X + canvas.X / _canvas.Width * rect.Width, rect.Y + canvas.Y / _canvas.Height * rect.Height);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(On(_path[0]), false, false);
                g.PolyLineTo(_path.Skip(1).Select(On).ToList(), true, true);
            }

            line.Freeze();
            var accent = TryFindResource("AccentBrush") as SolidColorBrush;
            var color = accent?.Color ?? Colors.SteelBlue;
            var shade = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 4) { LineJoin = PenLineJoin.Round };
            var dashed = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, color.R, color.G, color.B)), 2) { DashStyle = new DashStyle([3, 2.5], 0), LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, shade, line);
            dc.DrawGeometry(null, dashed, line);
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12)), 1.5);
            foreach (var key in _keys)
            {
                dc.DrawEllipse(accent ?? Brushes.SteelBlue, edge, On(key), 4, 4);
            }
        }

        dc.Pop();
    }
}
