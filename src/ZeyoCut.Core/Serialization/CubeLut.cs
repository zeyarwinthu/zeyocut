// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ZeyoCut.Core.Serialization;

// A 3D LUT read from a .cube file (Adobe's Cube LUT format, which Resolve, Premiere and most LUT
// makers write): Size points along each of red, green and blue, and for each point the color it
// becomes, red changing fastest, then green, then blue. A color between points is mixed from the 8
// around it. The domain is the input range the points cover, 0 to 1 unless the file says otherwise.
public sealed class CubeLut
{
    public const int MinSize = 2;
    public const int MaxSize = 129;

    private CubeLut(string title, int size, float[] domainMin, float[] domainMax, float[] data)
    {
        Title = title;
        Size = size;
        DomainMin = domainMin;
        DomainMax = domainMax;
        Data = data;
    }

    public string Title { get; }

    public int Size { get; }

    public IReadOnlyList<float> DomainMin { get; }

    public IReadOnlyList<float> DomainMax { get; }

    // Red, green and blue for each point, Size³ of them in the file's order.
    public float[] Data { get; }

    public static CubeLut Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ZeyoCutException($"could not read the LUT file '{path}': {ex.Message}", ex);
        }

        return Parse(text, Path.GetFileName(path));
    }

    public static CubeLut Parse(string text, string name)
    {
        var title = "";
        int? size = null;
        float[] min = [0, 0, 0];
        float[] max = [1, 1, 1];
        var data = new List<float>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0].ToUpperInvariant();
            if (char.IsLetter(keyword[0]) && data.Count > 0)
            {
                throw Bad(name, number, $"'{parts[0]}' comes after the table; a .cube file's keywords come first");
            }

            switch (keyword)
            {
                case "TITLE":
                    title = line[parts[0].Length..].Trim().Trim('"');
                    break;
                case "LUT_1D_SIZE":
                    throw new ZeyoCutException($"'{name}' is a 1D LUT (LUT_1D_SIZE); ZeyoCut takes 3D LUTs, with LUT_3D_SIZE");
                case "LUT_3D_SIZE":
                    if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var given))
                    {
                        throw Bad(name, number, "LUT_3D_SIZE takes one whole number");
                    }

                    if (given is < MinSize or > MaxSize)
                    {
                        throw Bad(name, number, $"LUT_3D_SIZE is {given}; ZeyoCut takes {MinSize} to {MaxSize} points a side");
                    }

                    size = given;
                    break;
                case "DOMAIN_MIN":
                    min = Triple(parts, name, number);
                    break;
                case "DOMAIN_MAX":
                    max = Triple(parts, name, number);
                    break;
                case "LUT_3D_INPUT_RANGE":
                    if (parts.Length != 3 || !TryFloat(parts[1], out var low) || !TryFloat(parts[2], out var high))
                    {
                        throw Bad(name, number, "LUT_3D_INPUT_RANGE takes two numbers");
                    }

                    (min, max) = ([low, low, low], [high, high, high]);
                    break;
                default:
                    if (char.IsLetter(keyword[0]))
                    {
                        continue;
                    }

                    data.AddRange(Triple(["", .. parts], name, number));
                    break;
            }
        }

        if (size is not { } points)
        {
            throw new ZeyoCutException($"'{name}' isn't a 3D LUT: a .cube file needs a LUT_3D_SIZE line");
        }

        for (var channel = 0; channel < 3; channel++)
        {
            if (max[channel] <= min[channel])
            {
                throw new ZeyoCutException($"'{name}': DOMAIN_MAX must be above DOMAIN_MIN for each of red, green and blue");
            }
        }

        var rows = points * points * points;
        if (data.Count != rows * 3)
        {
            throw new ZeyoCutException($"'{name}' has {data.Count / 3} rows of red, green and blue; a LUT of {points} points a side has {rows}");
        }

        return new CubeLut(title, points, min, max, [.. data]);
    }

    // The color the LUT makes of red, green and blue (each 0 to 1 of its domain), mixed from the 8
    // points around it.
    public (double Red, double Green, double Blue) Sample(double red, double green, double blue)
    {
        var last = Size - 1;
        var at = new double[3];
        var input = new[] { red, green, blue };
        for (var channel = 0; channel < 3; channel++)
        {
            var share = (input[channel] - DomainMin[channel]) / (DomainMax[channel] - DomainMin[channel]);
            at[channel] = Math.Clamp(share, 0, 1) * last;
        }

        var low = at.Select(p => Math.Min((int)Math.Floor(p), last - 1)).ToArray();
        var result = new double[3];
        for (var corner = 0; corner < 8; corner++)
        {
            var weight = 1.0;
            var index = new int[3];
            for (var channel = 0; channel < 3; channel++)
            {
                var up = (corner >> channel & 1) == 1;
                var fraction = at[channel] - low[channel];
                weight *= up ? fraction : 1 - fraction;
                index[channel] = low[channel] + (up ? 1 : 0);
            }

            var row = ((index[2] * Size + index[1]) * Size + index[0]) * 3;
            for (var channel = 0; channel < 3; channel++)
            {
                result[channel] += weight * Data[row + channel];
            }
        }

        return (result[0], result[1], result[2]);
    }

    private static float[] Triple(string[] parts, string name, int number)
    {
        if (parts.Length != 4 || !TryFloat(parts[1], out var r) || !TryFloat(parts[2], out var g) || !TryFloat(parts[3], out var b))
        {
            throw Bad(name, number, "expected three numbers: red, green and blue");
        }

        return [r, g, b];
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static ZeyoCutException Bad(string name, int line, string problem) => new($"'{name}' line {line}: {problem}");
}
