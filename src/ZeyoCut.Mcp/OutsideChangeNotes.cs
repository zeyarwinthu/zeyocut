// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Mcp;

// Before every tool call the open project catches up with project.json, in case another program
// (the editor, a second server) saved it. When one did, the reply starts with a note that says what
// changed, so Claude doesn't go on from details that are out of date.
public static class OutsideChangeNotes
{
    public const int ItemsShown = 10;

    private const int EditsShown = 5;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (request, ct) =>
    {
        var state = request.Services?.GetService<EditorState>();
        var session = state?.CurrentSession;
        try
        {
            session?.Sync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The tool's own save reads project.json again under project.lock.
        }

        CallToolResult result;
        try
        {
            result = await next(request, ct);
        }
        catch (McpException ex) when (ex is not McpProtocolException && Take(state, session) is { } note)
        {
            throw new McpException($"{note}\n{ex.Message}", ex);
        }

        if (Take(state, session) is { } text)
        {
            result.Content = [new TextContentBlock { Text = text }, .. result.Content];
        }

        return result;
    };

    public static string Format(OutsideChange change)
    {
        var builder = new StringBuilder();
        if (change.Items.Count > 0)
        {
            builder.Append($"project.json was changed outside this server since your last call (now revision {change.Revision}");
            if (change.Edits.Count > 0)
            {
                var edits = change.Edits.TakeLast(EditsShown).ToList();
                var earlier = change.Edits.Count - edits.Count;
                builder.Append($"; saved: {string.Join(", ", edits)}{(earlier > 0 ? $" and {earlier} earlier" : "")}");
            }

            builder.Append("). What changed:\n");
            foreach (var item in change.Items.Take(ItemsShown))
            {
                builder.Append($"- {item.Describe()}\n");
            }

            if (change.Items.Count > ItemsShown)
            {
                builder.Append($"- and {change.Items.Count - ItemsShown} more\n");
            }
        }

        if (change.Problem is { } problem)
        {
            builder.Append($"project.json was changed outside this server and can't be read ({problem}). The open project is still revision {change.Revision}; "
                + "edits are refused until the file is fixed or restore_backup puts a backup back.\n");
        }

        builder.Append("Re-read with get_project or list_layers before relying on earlier details.");
        return builder.ToString();
    }

    // The note for what changed since the last call, unless the call opened another project.
    private static string? Take(EditorState? state, ProjectSession? session) =>
        session is not null && ReferenceEquals(state?.CurrentSession, session) && session.TakeOutsideChanges() is { } change
            ? Format(change)
            : null;
}
