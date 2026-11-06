// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ZeyoCut.Core.Model;

public readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    public static bool TryParse(string? text, out RgbaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.Trim();
        if (hex[0] != '#')
        {
            return false;
        }

        hex = hex[1..];
        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        if (hex.Length == 6)
        {
            hex += "FF";
        }

        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new RgbaColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static RgbaColor Parse(string? text) =>
        TryParse(text, out var color)
            ? color
            : throw new ZeyoCutException($"'{text}' is not a color; use #RRGGBB or #RRGGBBAA");
}
