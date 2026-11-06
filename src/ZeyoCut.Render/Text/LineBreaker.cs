// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ZeyoCut.Render.Text;

public static class LineBreaker
{
    private const string NoLineStart = "、。，．・：；？！ー」』）〕］｝〉》】ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ々゛゜…‥％,.!?:;)]}၊။";
    private const string NoLineEnd = "「『（〔［｛〈《【([{";

    public static List<string> Wrap(string paragraph, double maxWidth, Func<string, double> measure)
    {
        var lines = new List<string>();
        if (paragraph.Length == 0)
        {
            lines.Add("");
            return lines;
        }

        var pieces = new List<string>();
        foreach (var phrase in SplitAtBreaks(paragraph, spacesOnly: true))
        {
            if (measure(phrase.Trim()) <= maxWidth)
            {
                pieces.Add(phrase);
                continue;
            }

            foreach (var piece in SplitAtBreaks(phrase))
            {
                if (measure(piece.Trim()) > maxWidth)
                {
                    pieces.AddRange(Graphemes(piece));
                }
                else
                {
                    pieces.Add(piece);
                }
            }
        }

        var current = "";
        foreach (var piece in pieces)
        {
            if (current.Length == 0)
            {
                current = lines.Count == 0 ? piece : piece.TrimStart();
                continue;
            }

            var candidate = current + piece;
            if (measure(candidate.TrimEnd()) <= maxWidth)
            {
                current = candidate;
            }
            else
            {
                lines.Add(current.TrimEnd());
                current = piece.TrimStart();
            }
        }

        if (current.Length > 0 || lines.Count == 0)
        {
            lines.Add(current.TrimEnd());
        }

        return lines;
    }

    public static List<string> SplitAtBreaks(string text, bool spacesOnly = false)
    {
        var boundaries = GraphemeBoundaries(text);
        var pieces = new List<string>();
        var start = 0;
        for (var i = 1; i < text.Length; i++)
        {
            if (boundaries.Contains(i) && (spacesOnly ? IsSpaceBreak(text, i) : IsBreak(text, i)))
            {
                pieces.Add(text[start..i]);
                start = i;
            }
        }

        pieces.Add(text[start..]);
        return pieces;
    }

    private static bool IsSpaceBreak(string text, int i) => IsSpace(text[i - 1]) && !IsSpace(text[i]);

    private static bool IsBreak(string text, int i)
    {
        var previous = text[i - 1];
        var current = text[i];

        if (IsSpace(current))
        {
            return false;
        }

        if (IsSpace(previous))
        {
            return true;
        }

        if (NoLineStart.Contains(current) || NoLineEnd.Contains(previous))
        {
            return false;
        }

        if (IsCjk(previous) || IsCjk(current))
        {
            return true;
        }

        if (IsMyanmar(previous) && IsMyanmarSyllableStart(current))
        {
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            return previous != '္' && next != '်' && next != '္';
        }

        return false;
    }

    private static HashSet<int> GraphemeBoundaries(string text)
    {
        var boundaries = new HashSet<int>();
        var index = 0;
        while (index < text.Length)
        {
            boundaries.Add(index);
            index += StringInfo.GetNextTextElementLength(text, index);
        }

        return boundaries;
    }

    private static IEnumerable<string> Graphemes(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '　';

    private static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ'
            or >= '㐀' and <= '䶿'
            or >= '一' and <= '鿿'
            or >= '豈' and <= '﫿'
            or >= '가' and <= '힯'
            or >= '　' and <= '〿'
            or >= '＀' and <= '￯';

    private static bool IsMyanmar(char c) => c is >= 'က' and <= '႟' or >= 'ꩠ' and <= 'ꩿ';

    private static bool IsMyanmarSyllableStart(char c) =>
        c is >= 'က' and <= 'ဪ' or 'ဿ' or >= '၀' and <= '၉' or >= '၌' and <= '၏';
}
