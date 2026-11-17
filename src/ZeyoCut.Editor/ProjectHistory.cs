// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// A save of project.json, as the copy kept before it in the backups folder records it: when, the
// edit that was saved, and whether the editor saved it or Claude did (through the MCP server).
// Revision is the one it saved, one more than the copy's; null when the copy doesn't say.
public sealed record SavedChange(DateTime Time, string Edit, bool ByEditor)
{
    public long? Revision { get; init; }

    public ProjectBackup? Backup { get; init; }

    public string Who => ByEditor ? "You" : "Claude";
}

// Saves in a row by the same side, each under five minutes after the one before, newest first: one
// step on the Activity view. A restore is a step of its own, so the one before it can be put back. Restore is the copy that holds the project as the step left it, the
// one kept before the next save: there is none for the newest step, which is the project as it is
// now, nor when the next save isn't kept. Gap says that saves just after the step aren't kept. Day
// heads the first step of a day shown.
public sealed record SavedStep(IReadOnlyList<SavedChange> Saves)
{
    public ProjectBackup? Restore { get; init; }

    public bool Newest { get; init; }

    public bool Gap { get; init; }

    public string? Day { get; init; }

    public bool ByEditor => Saves[0].ByEditor;

    public string Who => Saves[0].Who;

    public string Initial => Who[..1];

    public long? Revision => Saves[0].Revision;

    // Named by rule, never written: its first edit, then how many came after it. ("and N more" would
    // run into the editor's labels for an edit to several layers, "update_layer title and 1 more".)
    public string Title => Saves.Count == 1 ? Saves[0].Edit : $"{Saves[^1].Edit}, then {Saves.Count - 1} more";

    public string Time
    {
        get
        {
            var from = $"{Saves[^1].Time:HH:mm}";
            var to = $"{Saves[0].Time:HH:mm}";
            return from == to ? to : $"{from} – {to}";
        }
    }

    public string Details => Revision is { } revision ? $"{Time} · rev {revision}" : Time;

    // Its edits, oldest first; the same edit made again and again is one, with how many times.
    public IReadOnlyList<string> Edits
    {
        get
        {
            var edits = new List<(string Edit, int Times)>();
            foreach (var save in Saves.Reverse())
            {
                if (edits.Count > 0 && edits[^1].Edit == save.Edit)
                {
                    edits[^1] = (save.Edit, edits[^1].Times + 1);
                }
                else
                {
                    edits.Add((save.Edit, 1));
                }
            }

            return edits.Select(e => e.Times == 1 ? e.Edit : $"{e.Edit} ×{e.Times}").ToList();
        }
    }
}

// The project's saves, for the Activity view and the header.
public static class ProjectHistory
{
    public static readonly TimeSpan StepGap = TimeSpan.FromMinutes(5);

    private const string EditorPrefix = ProjectBackups.EditorProgram + " ";
    private const string RestorePrefix = "restore_backup ";
    private const int RevisionRead = 4096;

    // A copy never changes once it's in the backups folder, so its revision is read once.
    private static readonly ConcurrentDictionary<string, long> Revisions = new(StringComparer.OrdinalIgnoreCase);

    // Newest first: the editor's and the server's together, as many as the backups folder keeps,
    // with the revisions they saved.
    public static IReadOnlyList<SavedChange> Saves(string directory) =>
        Listed(directory).Select(s => s with { Revision = RevisionOf(s.Backup!.Path) + 1 }).ToList();

    // The newest save, without reading the copies; null when there's none.
    public static SavedChange? Newest(string directory) => Listed(directory).FirstOrDefault();

    public static IReadOnlyList<SavedStep> Steps(string directory) => Steps(Saves(directory));

    // 'saves' newest first, as Saves lists them. A save follows the one listed after it only when it
    // saved the next revision; otherwise saves between them aren't kept (or a copy doesn't say).
    public static IReadOnlyList<SavedStep> Steps(IReadOnlyList<SavedChange> saves)
    {
        static bool Follows(SavedChange after, SavedChange save) => after.Revision is { } next && save.Revision is { } revision && next == revision + 1;
        static bool Restores(SavedChange save) => save.Edit.StartsWith(RestorePrefix, StringComparison.Ordinal);

        var steps = new List<SavedStep>();
        for (var start = 0; start < saves.Count;)
        {
            var end = start + 1;
            while (end < saves.Count && saves[end].ByEditor == saves[start].ByEditor && Follows(saves[end - 1], saves[end])
                && saves[end - 1].Time - saves[end].Time < StepGap && !Restores(saves[start]) && !Restores(saves[end]))
            {
                end++;
            }

            var followed = start > 0 && Follows(saves[start - 1], saves[start]);
            steps.Add(new SavedStep(saves.Skip(start).Take(end - start).ToList())
            {
                Restore = followed ? saves[start - 1].Backup : null,
                Newest = start == 0,
                Gap = start > 0 && !followed && saves[start - 1].Revision is not null && saves[start].Revision is not null,
            });
            start = end;
        }

        return steps;
    }

    // The steps by one side (null: both), each day's first headed "Today", "Yesterday" or its date. A
    // gap stays shown when the step it was on is filtered out.
    public static IReadOnlyList<SavedStep> Shown(IReadOnlyList<SavedStep> steps, bool? byEditor, DateTime now)
    {
        var shown = new List<SavedStep>();
        string? day = null;
        var gap = false;
        foreach (var step in steps)
        {
            gap |= step.Gap;
            if (byEditor is { } side && step.ByEditor != side)
            {
                continue;
            }

            var saved = step.Saves[0].Time.Date;
            var heading = saved == now.Date ? "Today" : saved == now.Date.AddDays(-1) ? "Yesterday" : $"{saved:yyyy-MM-dd}";
            shown.Add(step with { Day = heading == day ? null : heading, Gap = gap });
            day = heading;
            gap = false;
        }

        return shown;
    }

    // Claude's newest save that the backups folder keeps; null when it has none.
    public static SavedChange? LastByClaude(string directory) => Listed(directory).FirstOrDefault(s => !s.ByEditor);

    // How long ago a save was, as the header says it: "just now", "5 min ago", "3 h ago", or the day.
    public static string Ago(DateTime time, DateTime now)
    {
        var since = now - time;
        return since < TimeSpan.FromMinutes(1) ? "just now"
            : since < TimeSpan.FromHours(1) ? $"{(int)since.TotalMinutes} min ago"
            : since < TimeSpan.FromDays(1) ? $"{(int)since.TotalHours} h ago"
            : $"on {time:yyyy-MM-dd}";
    }

    private static IEnumerable<SavedChange> Listed(string directory) =>
        ProjectBackups.List(directory)
            .Where(b => b.Before is not null)
            .Select(b => (b.Before!.StartsWith(EditorPrefix, StringComparison.Ordinal)
                    ? new SavedChange(b.Time, b.Before[EditorPrefix.Length..], true)
                    : new SavedChange(b.Time, b.Before, false))
                with { Backup = b });

    // The revision a copy of project.json holds, from the start of the file, where it's saved
    // second; null when it can't be read there.
    private static long? RevisionOf(string path)
    {
        if (Revisions.TryGetValue(path, out var known))
        {
            return known;
        }

        try
        {
            var buffer = new byte[RevisionRead];
            int read;
            using (var file = File.OpenRead(path))
            {
                read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }

            var bom = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            var reader = new Utf8JsonReader(buffer.AsSpan(bom, read - bom), isFinalBlock: read < buffer.Length, default);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("revision"u8))
                {
                    if (reader.Read() && reader.TryGetInt64(out var revision))
                    {
                        Revisions[path] = revision;
                        return revision;
                    }

                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Not known: the steps on either side of the save aren't joined.
        }

        return null;
    }
}
