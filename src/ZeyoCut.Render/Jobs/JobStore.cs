// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Jobs;

// What is kept on disk about a background job, so it outlasts the server: <jobs>\<id>.json.
public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public JobState State { get; set; } = JobState.Running;

    // The server process running it, by id and start time (process ids get reused).
    public int ProcessId { get; set; }
    public DateTime ProcessStartedUtc { get; set; }

    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    // Saved every few seconds while it runs.
    public double Progress { get; set; }
    public string? Stage { get; set; }
    public DateTime? LastSeenUtc { get; set; }

    public int Attempts { get; set; } = 1;
    public string? Result { get; set; }
    public string? Error { get; set; }

    // What it takes to run the job again after a restart; dropped when it finishes.
    public JsonObject? Resume { get; set; }

    // A finished export's file details.
    public ExportResult? Export { get; set; }

    // The files an export_batch or export_language_versions job wrote.
    public List<ExportResult>? Exports { get; set; }
}

// The jobs folder, shared by every server that points at it.
public sealed class JobStore
{
    public const int MaxAttempts = 3;

    public static readonly TimeSpan KeepFinished = TimeSpan.FromDays(7);

    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    private const int SaveAttempts = 5;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly Lazy<(int Id, DateTime StartedUtc)> Self = new(() =>
    {
        using var process = Process.GetCurrentProcess();
        return (process.Id, process.StartTime.ToUniversalTime());
    });

    public JobStore(string directory)
    {
        Directory = Path.GetFullPath(directory);
    }

    public string Directory { get; }

    // Problems writing a record; the job itself goes on.
    public Action<string>? Log { get; set; }

    public static int ProcessId => Self.Value.Id;

    public static DateTime ProcessStartedUtc => Self.Value.StartedUtc;

    // The next free <kind>_<n>, so ids stay unique across restarts and between servers sharing the folder.
    public string Claim(string kind)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var next = Numbers(kind).DefaultIfEmpty(0).Max() + 1;
        for (; ; next++)
        {
            var id = $"{kind}_{next}";
            try
            {
                using (new FileStream(PathOf(id), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                return id;
            }
            catch (IOException) when (File.Exists(PathOf(id)))
            {
            }
        }
    }

    public JobRecord NewRecord(string id, string kind, string description, JsonObject? resume) => new()
    {
        Id = id,
        Kind = kind,
        Description = description,
        ProcessId = ProcessId,
        ProcessStartedUtc = ProcessStartedUtc,
        StartedUtc = DateTime.UtcNow,
        Resume = resume,
    };

    public void Save(JobRecord record)
    {
        var path = PathOf(record.Id);
        var temp = path + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(temp, JsonSerializer.Serialize(record, ProjectJson.Options), Utf8NoBom);
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= SaveAttempts)
                {
                    Log?.Invoke($"couldn't save job record {path}: {ex.Message}");
                    return;
                }

                Thread.Sleep(50 * attempt);
            }
        }
    }

    public void SaveImage(string id, byte[] png)
    {
        try
        {
            File.WriteAllBytes(ImageOf(id), png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"couldn't save the picture of job {id}: {ex.Message}");
        }
    }

    public byte[]? LoadImage(string id)
    {
        try
        {
            return File.Exists(ImageOf(id)) ? File.ReadAllBytes(ImageOf(id)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Saves the record of a running job at most every SaveEvery, for its progress reports.
    public Action SaveProgress(JobRecord record, Func<(double Progress, string? Stage)> read)
    {
        var saved = DateTime.UtcNow;
        return () =>
        {
            lock (record)
            {
                var now = DateTime.UtcNow;
                if (now - saved < SaveEvery || record.State != JobState.Running)
                {
                    return;
                }

                saved = now;
                (record.Progress, record.Stage) = read();
                record.LastSeenUtc = now;
                Save(record);
            }
        };
    }

    // Saves how a job ended. The resume data isn't needed any more.
    public void SaveFinished(JobRecord record, JobState state, DateTime finishedUtc, Action<JobRecord> fill)
    {
        lock (record)
        {
            record.State = state;
            record.FinishedUtc = finishedUtc;
            record.LastSeenUtc = finishedUtc;
            record.Resume = null;
            fill(record);
            Save(record);
        }
    }

    // Why a job that was running when its server stopped isn't run again, or null when it is:
    // its kind can resume, it saved what that takes, and it was tried fewer than MaxAttempts times.
    public static string? WhyNotAgain(JobRecord record, bool resumable)
    {
        if (!resumable || record.Resume is null)
        {
            return $"the server stopped while this job ran{Stopped(record)}, so it didn't finish; start it again";
        }

        return record.Attempts >= MaxAttempts
            ? $"the server stopped {record.Attempts} times while this job ran, so it isn't started again; start it again yourself"
            : null;
    }

    public static string CantResume(Exception ex) => $"the server stopped while this job ran, and it can't be started again: {ex.Message}";

    // This process takes the job over for another attempt.
    public static void TakeOver(JobRecord record)
    {
        record.Attempts++;
        record.ProcessId = ProcessId;
        record.ProcessStartedUtc = ProcessStartedUtc;
        record.Progress = 0;
        record.Stage = null;
        record.LastSeenUtc = DateTime.UtcNow;
    }

    // Marks a job whose server stopped as failed; it ended when it was last heard from.
    public void Fail(JobRecord record, string reason)
    {
        record.State = JobState.Failed;
        record.Error = reason;
        record.FinishedUtc = record.LastSeenUtc ?? record.StartedUtc;
        record.Resume = null;
        record.ProcessId = ProcessId;
        record.ProcessStartedUtc = ProcessStartedUtc;
        Save(record);
    }

    // The records no live server is working on, oldest first. Finished ones older than
    // KeepFinished are deleted on the way.
    public IReadOnlyList<JobRecord> Unowned()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var records = new List<JobRecord>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            JobRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(path, Encoding.UTF8), ProjectJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (record is null || record.Id.Length == 0 || IsAlive(record))
            {
                continue;
            }

            if (record.State != JobState.Running && record.FinishedUtc is { } finished && DateTime.UtcNow - finished > KeepFinished)
            {
                Delete(record.Id);
                continue;
            }

            records.Add(record);
        }

        return records.OrderBy(r => r.StartedUtc).ToList();
    }

    // Whether the server that wrote the record still runs (this one included).
    public static bool IsAlive(JobRecord record)
    {
        if (record.ProcessId == ProcessId)
        {
            return Math.Abs((record.ProcessStartedUtc - ProcessStartedUtc).TotalSeconds) < 1;
        }

        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            return Math.Abs((process.StartTime.ToUniversalTime() - record.ProcessStartedUtc).TotalSeconds) < 1;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // A process that can't be looked at could be the one; leave its jobs alone.
            return true;
        }
    }

    // " (40% done, rendering, last heard from at 18:02:11)"; "" when it never reported.
    private static string Stopped(JobRecord record)
    {
        if (record.LastSeenUtc is not { } seen)
        {
            return "";
        }

        var stage = string.IsNullOrWhiteSpace(record.Stage) ? "" : $", {record.Stage}";
        return $" ({(int)Math.Floor(record.Progress * 100)}% done{stage}, last heard from at {seen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)})";
    }

    private void Delete(string id)
    {
        foreach (var path in new[] { PathOf(id), ImageOf(id) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private IEnumerable<int> Numbers(string kind)
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, kind + "_*.json"))
        {
            var suffix = Path.GetFileNameWithoutExtension(path)[(kind.Length + 1)..];
            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                yield return number;
            }
        }
    }

    private string PathOf(string id) => Path.Combine(Directory, id + ".json");

    private string ImageOf(string id) => Path.Combine(Directory, id + ".png");
}
