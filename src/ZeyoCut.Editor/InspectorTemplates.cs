// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ZeyoCut.Editor;

// The inspector's row template for each kind of field.
public sealed class InspectorTemplates : DataTemplateSelector
{
    public DataTemplate? Section { get; set; }

    public DataTemplate? Info { get; set; }

    public DataTemplate? Number { get; set; }

    public DataTemplate? Text { get; set; }

    public DataTemplate? LongText { get; set; }

    public DataTemplate? Toggle { get; set; }

    public DataTemplate? Choice { get; set; }

    public DataTemplate? Color { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is not InspectorField field
        ? base.SelectTemplate(item, container)
        : field.Kind switch
        {
            FieldKind.Section => Section,
            FieldKind.Number => Number,
            FieldKind.Text => Text,
            FieldKind.LongText => LongText,
            FieldKind.Toggle => Toggle,
            FieldKind.Choice => Choice,
            FieldKind.Color => Color,
            _ => Info,
        };
}

// The color a #RRGGBB or #RRGGBBAA field names, for the swatch beside it; none when it names none.
public sealed class ColorSwatch : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim() ?? "";
        if (text.Length is not (7 or 9) || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
        {
            return null;
        }

        var alpha = text.Length == 9 ? (byte)(rgba & 0xFF) : (byte)0xFF;
        var rgb = text.Length == 9 ? rgba >> 8 : rgba;
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
