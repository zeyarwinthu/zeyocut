// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;

namespace ZeyoCut.Editor;

public enum QueueState
{
    Queued,
    Running,
    Done,
    Failed,
    Stopped
}

// How a row of the Render queue says how it's going: its status's color.
public enum QueueTone
{
    Waiting,
    Running,
    Done,
    Failed,
    Stopped
}

// An export in the Render queue: the project as it was saved when it was added, so edits made after
// aren't in it (as with start_export), the file it writes and how, and how it went.
public sealed class QueueItem
{
    public required string Directory { get; init; }

    public required Project Snapshot { get; init; }

    // The project's revision the snapshot is of, for its picture.
    public long Revision { get; init; }

    // The MP4's name in the project's exports folder.
    public required string Name { get; init; }

    public required int Crf { get; init; }

    public Preset? Preset { get; init; }

    public string? Language { get; init; }

    // How it's exported, as the queue says it: "Project settings · JA captions".
    public required string Settings { get; init; }

    // The .srt files saved next to it once it's done, as export_srt writes them.
    public IReadOnlyList<(string Name, List<(double Start, double End, string Text)> Cues)> Srt { get; init; } = [];

    public QueueState State { get; internal set; }

    public ExportStarted? Started { get; internal set; }

    public string? Error { get; internal set; }

    // A file the project uses that isn't there: Relink finds it.
    public Asset? Missing { get; internal set; }

    // What happened to the .srt files.
    public string? SrtNote { get; internal set; }

    public DateTime? Finished { get; internal set; }

    public string Output => Path.GetFullPath(Path.Combine(Directory, ProjectStore.ExportsFolder, Name));

    public string Project => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
}

// A row of the Render queue: an export of yours, or one of Claude's (read-only) that a ZeyoCut server
// runs now. Picture is the project's, once it's drawn.
public sealed record QueueRow(object Key, string File, bool Claude, string Project, string Settings, double? Percent, string Status, QueueTone Tone, string Detail, string? Action,
    string? ActionTip)
{
    public string Directory { get; init; } = "";

    public object? Picture { get; init; }

    public string Who => Claude ? "CLAUDE" : "YOU";

    public string Line => $"{Project} · {Settings}";

    public bool HasBar => Percent is not null;

    public bool Running => Tone == QueueTone.Running;
}

// A row of the Render queue as the window shows it: its buttons stay while its text and progress
// change.
public sealed class QueueRowView : INotifyPropertyChanged
{
    private QueueRow _row;

    public QueueRowView(QueueRow row)
    {
        _row = row;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public QueueRow Row
    {
        get => _row;
        set
        {
            _row = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Row)));
        }
    }
}

// The editor's exports, from any project, rendered one after another while you go on editing (S20).
// Pausing lets the running one finish and starts no other. The window calls Poll on its thread to
// move the queue on; nothing here runs on another thread but the exports themselves.
public sealed class RenderQueue
{
    private readonly Func<QueueItem, ExportStarted> _start;
    private readonly Action<ExportJob> _cancel;
    private readonly List<QueueItem> _items = [];

    // start starts an item's export (ExportStart.Start), cancel stops one.
    public RenderQueue(Func<QueueItem, ExportStarted> start, Action<ExportJob> cancel)
    {
        _start = start;
        _cancel = cancel;
    }

    public IReadOnlyList<QueueItem> Items => _items;

    public bool Paused { get; private set; }

    public QueueItem? Running => _items.FirstOrDefault(i => i.State == QueueState.Running);

    public IReadOnlyList<QueueItem> Waiting => _items.Where(i => i.State == QueueState.Queued).ToList();

    // How many will still render: the running one and those waiting.
    public int Left => _items.Count(i => i.State is QueueState.Queued or QueueState.Running);

    // Puts an export at the end of the queue; it starts at once when nothing runs and the queue isn't
    // paused. Another export waiting or running to the same file is refused.
    public QueueItem Add(QueueItem item)
    {
        if (_items.Any(i => i.State is QueueState.Queued or QueueState.Running && string.Equals(i.Output, item.Output, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ZeyoCutException($"{item.Name} of {item.Project} is in the render queue already");
        }

        item.State = QueueState.Queued;
        _items.Add(item);
        Next();
        return item;
    }

    public void Pause(bool paused)
    {
        Paused = paused;
        Next();
    }

    // Takes an export that isn't running off the list; a waiting one won't render. Files already
    // written stay.
    public bool Remove(QueueItem item) => item.State != QueueState.Running && _items.Remove(item);

    // Stops the running export (Poll marks it stopped once it has), or one waiting.
    public void Cancel(QueueItem item)
    {
        if (item.State == QueueState.Running && item.Started is { } started)
        {
            _cancel(started.Job);
        }
        else if (item.State == QueueState.Queued)
        {
            item.State = QueueState.Stopped;
            item.Finished = DateTime.Now;
        }
    }

    // Stops every export, the running one and those waiting.
    public void CancelAll()
    {
        foreach (var item in _items.Where(i => i.State is QueueState.Queued or QueueState.Running).ToList())
        {
            Cancel(item);
        }
    }

    // Moves the queue on: the running export, once finished, is marked done (its .srt files saved),
    // failed or stopped, and the next one waiting starts. Returns the exports that finished, with
    // those that couldn't start.
    public IReadOnlyList<QueueItem> Poll()
    {
        var finished = new List<QueueItem>();
        if (Running is { Started.Job: { State: not ExportState.Running } job } item)
        {
            item.State = job.State switch
            {
                ExportState.Completed => QueueState.Done,
                ExportState.Cancelled => QueueState.Stopped,
                _ => QueueState.Failed,
            };
            item.Error = job.State == ExportState.Failed ? job.Error : null;
            item.Finished = (job.FinishedUtc ?? DateTime.UtcNow).ToLocalTime();
            if (item.State == QueueState.Done)
            {
                SaveSubtitles(item);
            }

            finished.Add(item);
        }

        finished.AddRange(Next());
        return finished;
    }

    // The place of a waiting export, from 1 for the next.
    public int Place(QueueItem item) => _items.Where(i => i.State == QueueState.Queued).ToList().IndexOf(item) + 1;

    // The queue's rows: yours and Claude's running first, then yours waiting in their order, then the
    // finished ones, newest first.
    public IReadOnlyList<QueueRow> Rows(IReadOnlyList<QueueRow> claude, DateTime now)
    {
        var rows = new List<QueueRow>();
        if (Running is { } running)
        {
            rows.Add(Row(running, now));
        }

        rows.AddRange(claude);
        rows.AddRange(Waiting.Select(i => Row(i, now)));
        rows.AddRange(_items.Where(i => i.State is QueueState.Done or QueueState.Failed or QueueState.Stopped).OrderByDescending(i => i.Finished).Select(i => Row(i, now)));
        return rows;
    }

    public QueueRow Row(QueueItem item, DateTime now)
    {
        var job = item.Started?.Job;
        var (percent, status, tone, detail, action, tip) = item.State switch
        {
            QueueState.Running when job is not null => ((double?)job.Percent,
                job.FramesDone >= job.TotalFrames ? "Finishing" : $"Rendering {job.Percent.ToString("0", CultureInfo.InvariantCulture)}%",
                QueueTone.Running,
                JobTime.Left(job.Percent / 100, now.ToUniversalTime() - job.StartedUtc) is { } left ? $"about {JobTime.Format(left)} left" : "just started",
                "Cancel", "Stop this export; nothing is written to its file"),
            QueueState.Queued => (null, "Queued", QueueTone.Waiting,
                Paused ? "the queue is paused" : Place(item) == 1 ? "next" : $"{Ordinal(Place(item))} in line",
                "Remove", "Take it off the queue; it won't render"),
            QueueState.Done => (null, "Done", QueueTone.Done,
                $"{(job?.Result is { } result ? ExportSummary.FileSize(result.Media.FileSize) + " · " : "")}{item.Finished:HH:mm}",
                "Open", "Play it in the default video player"),
            QueueState.Failed => (null, "Failed", QueueTone.Failed,
                item.Missing is { } missing ? $"{Path.GetFileName(missing.File)} is missing" : item.Error ?? "",
                item.Missing is null ? "Remove" : "Relink", item.Missing is null ? "Take it off the list" : "Open the project and find its missing files"),
            _ => (null, "Stopped", QueueTone.Stopped, "nothing was written", "Remove", "Take it off the list"),
        };
        return new QueueRow(item, item.Name, false, item.Project, item.Settings, percent, status, tone, detail, action, tip) { Directory = item.Directory };
    }

    // Claude's exports that ZeyoCut servers run now, from any project, read from the jobs folder: a
    // row for each, read-only.
    public static IReadOnlyList<QueueRow> ClaudeRows(IEnumerable<JobRecord> running, DateTime now)
    {
        var rows = new List<QueueRow>();
        foreach (var record in running)
        {
            if (record.Resume is not { } resume || resume["projectDirectory"]?.GetValue<string>() is not { } directory)
            {
                continue;
            }

            List<string> outputs = resume["outputPath"]?.GetValue<string>() is { } output
                ? [output]
                : resume["outputs"] is JsonArray list ? list.Select(o => o?.GetValue<string>()).OfType<string>().ToList() : [];
            if (outputs.Count == 0)
            {
                continue;
            }

            List<string> presets = resume["preset"]?.GetValue<string>() is { } preset
                ? [preset]
                : resume["presets"] is JsonArray names ? names.Select(p => p?.GetValue<string>()).OfType<string>().ToList() : [];
            var percent = Math.Round(record.Progress * 100);
            var stage = string.IsNullOrEmpty(record.Stage) ? "rendering" : record.Stage;
            var left = JobTime.Left(record.Progress, now.ToUniversalTime() - record.StartedUtc);
            rows.Add(new QueueRow(record.Id, string.Join(", ", outputs.Select(o => Path.GetFileName(o))), true, Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)),
                presets.Count == 0 ? ExportChoices.ProjectSettings : string.Join(", ", presets), percent,
                $"{char.ToUpperInvariant(stage[0])}{stage[1..]} {percent.ToString("0", CultureInfo.InvariantCulture)}%", QueueTone.Running,
                left is { } time ? $"about {JobTime.Format(time)} left" : "just started", null, null) { Directory = directory });
        }

        return rows;
    }

    // 2nd, 3rd, 11th, 21st.
    public static string Ordinal(int number)
    {
        var suffix = (number % 100) is 11 or 12 or 13 ? "th" : (number % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return $"{number}{suffix}";
    }

    // Starts the first waiting export unless one runs or the queue is paused. One that can't start (a
    // file it uses is missing, the preset doesn't fit, ffmpeg isn't there) fails, and the next is tried.
    private List<QueueItem> Next()
    {
        var failed = new List<QueueItem>();
        while (!Paused && Running is null && _items.FirstOrDefault(i => i.State == QueueState.Queued) is { } item)
        {
            if (Relinking.Missing(item.Snapshot, item.Directory).FirstOrDefault(a => ProjectChecker.Users(item.Snapshot, a.Id).Any()) is { } missing)
            {
                Fail(item, $"{Path.GetFileName(missing.File)} is missing", missing);
                failed.Add(item);
                continue;
            }

            try
            {
                item.Started = _start(item);
                item.State = QueueState.Running;
            }
            catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
            {
                Fail(item, ex.Message, null);
                failed.Add(item);
            }
        }

        return failed;
    }

    private static void Fail(QueueItem item, string error, Asset? missing)
    {
        item.State = QueueState.Failed;
        item.Error = error;
        item.Missing = missing;
        item.Finished = DateTime.Now;
    }

    // The .srt files chosen for an export that's done, as export_srt writes them.
    private static void SaveSubtitles(QueueItem item)
    {
        if (item.Srt.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var (name, cues) in item.Srt)
            {
                CaptionBuilder.WriteSrt(item.Directory, name, cues);
            }

            item.SrtNote = $"Saved {string.Join(", ", item.Srt.Select(f => f.Name))} next to it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.SrtNote = $"Couldn't save the .srt files: {ex.Message}";
        }
    }
}
