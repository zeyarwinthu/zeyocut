// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace ZeyoCut.Core.Editing;

public static class EnumNames
{
    public static string ToName<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public static IEnumerable<string> All<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(ToName);

    public static T Parse<T>(string? text, string what) where T : struct, Enum
    {
        var wanted = Normalize(text);
        foreach (var value in Enum.GetValues<T>())
        {
            if (Normalize(value.ToString()) == wanted)
            {
                return value;
            }
        }

        throw new ZeyoCutException($"unknown {what} '{text}'; valid values: {string.Join(", ", All<T>())}");
    }

    private static string Normalize(string? text) =>
        new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
