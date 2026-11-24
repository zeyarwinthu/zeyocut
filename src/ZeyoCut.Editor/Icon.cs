// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// An icon drawn from an outline on a 24×24 grid, scaled to the element's size, in the text color
// around it: lines Thickness units wide with round ends, or the shape filled (Filled).
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(Icon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Icon),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (Data is not { } data)
        {
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        context.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        var pen = Filled ? null : new Pen(Foreground, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.DrawGeometry(Filled ? Foreground : null, pen, data);
        context.Pop();
        context.Pop();
    }
}
