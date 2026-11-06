// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ZeyoCut.Core;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Mcp.Tools;

internal static class ToolSupport
{
    private static readonly JsonSerializerOptions Compact = new(ProjectJson.Options) { WriteIndented = false };

    public static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (McpException)
        {
            throw;
        }
        catch (ZeyoCutException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            throw new McpException($"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpException($"internal error ({ex.GetType().Name}): {ex.Message}", ex);
        }
    }

    public static string LayerJson(Layer layer) => JsonSerializer.Serialize(layer, Compact);

    public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string WithWarnings(string text, IEnumerable<string> warnings)
    {
        var list = warnings.ToList();
        return list.Count == 0 ? text : text + "\nWarnings:\n- " + string.Join("\n- ", list);
    }
}
