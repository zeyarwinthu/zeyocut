// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using ZeyoCut.Core;

namespace ZeyoCut.Render.Jobs;

public enum JobState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

// A long-running task (import normalization, analysis) whose progress is polled with get_job_status.
public sealed class BackgroundJob
{
    private readonly object _gate = new();
    private double _progress;
    private string _stage = "starting";

    internal BackgroundJob(string id, string kind, string description, long sequence)
    {
        Id = id;
        Kind = kind;
        Description = description;
        Sequence = sequence;
    }

    internal long Sequence { get; }

    public string Id { get; }

    public string Kind { get; }

    public string Description { get; }

    public JobState State { get; internal set; } = JobState.Running;

    public string? Result { get; internal set; }

    public string? Error { get; internal set; }

    // A picture that goes with the result (detect_and_track's contact sheet), as PNG.
    public byte[]? Image { get; set; }

    // The files an export job (export_batch, export_language_versions) has written, with their times.
    public IReadOnlyList<Export.ExportResult>? Exports { get; set; }

    public DateTime StartedUtc { get; internal set; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; internal set; }

    public Task Completion { get; internal set; } = Task.CompletedTask;

    public CancellationTokenSource Cancellation { get; } = new();

    // 1 for the first run; more when a restart started it again.
    public int Attempt { get; internal set; } = 1;

    // Finished in an earlier server session (read back from the jobs folder).
    public bool Earlier { get; internal set; }

    internal Action? Reported { get; set; }

    public double Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    public string Stage
    {
        get
        {
            lock (_gate)
            {
                return _stage;
            }
        }
    }

    public int Percent => (int)Math.Floor(Progress * 100);

    // Progress is 0..1 within the current stage's share of the job; callers pass the overall fraction.
    public void Report(double progress, string? stage = null)
    {
        lock (_gate)
        {
            _progress = Math.Clamp(progress, _progress, 1);
            if (stage is not null)
            {
                _stage = stage;
            }
        }

        Reported?.Invoke();
    }
}

// How long a running job should still take, from how far it got in the time it has run.
public static class JobTime
{
    public static TimeSpan? Left(double progress, TimeSpan elapsed) =>
        progress is >= 0.02 and < 1 && elapsed >= TimeSpan.FromSeconds(3)
            ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - progress) / progress)
            : null;

    // ", about 40s left", or "" when it's too early to tell.
    public static string LeftText(double progress, TimeSpan elapsed) =>
        Left(progress, elapsed) is { } left ? $", about {Format(left)} left" : "";

    public static string Format(TimeSpan time)
    {
        var seconds = Math.Ceiling(time.TotalSeconds);
        if (seconds < 90)
        {
            return $"{seconds.ToString("0", CultureInfo.InvariantCulture)}s";
        }

        var minutes = (int)Math.Round(seconds / 60);
        return minutes < 90 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }
}

public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, BackgroundJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly JobStore? _store;
    private long _sequence;

    // With a store, every job is written to the jobs folder, so it outlasts the server.
    public JobManager(JobStore? store = null)
    {
        _store = store;
    }

    // This session's jobs, with the ones Recover started again or marked failed.
    public IReadOnlyList<BackgroundJob> All => _jobs.Values.Where(j => !j.Earlier).OrderBy(j => j.Sequence).ToList();

    // Jobs that finished in earlier sessions, newest first.
    public IReadOnlyList<BackgroundJob> Earlier => _jobs.Values.Where(j => j.Earlier).OrderByDescending(j => j.Sequence).ToList();

    // resume, when given, is what a resumer registered for the kind needs to run the job again
    // after a restart (see Recover).
    public BackgroundJob Start(string kind, string description, Func<BackgroundJob, CancellationToken, string> work, JsonObject? resume = null)
    {
        var id = _store?.Claim(kind) ?? $"{kind}_{_counters.AddOrUpdate(kind, 1, (_, n) => n + 1)}";
        var job = new BackgroundJob(id, kind, description, Interlocked.Increment(ref _sequence));
        Run(job, work, _store?.NewRecord(id, kind, description, resume));
        return job;
    }

    // Takes over the jobs no live server works on (JobStore.Unowned, kinds other than export).
    // One that was running when its server stopped is started again under its own id if its
    // kind has a resumer and it has been tried fewer than JobStore.MaxAttempts times; otherwise
    // it's marked failed with the reason. Finished ones can still be looked up by id. Returns a
    // line for the log about each job started again or failed.
    public IReadOnlyList<string> Recover(
        IEnumerable<JobRecord> records,
        IReadOnlyDictionary<string, Func<JsonObject, Func<BackgroundJob, CancellationToken, string>>> resumers)
    {
        var notes = new List<string>();
        foreach (var record in records)
        {
            var job = new BackgroundJob(record.Id, record.Kind, record.Description, Interlocked.Increment(ref _sequence))
            {
                StartedUtc = record.StartedUtc,
                Attempt = record.Attempts,
            };
            if (record.State != JobState.Running)
            {
                job.State = record.State;
                job.Result = record.Result;
                job.Error = record.Error;
                job.FinishedUtc = record.FinishedUtc;
                job.Image = _store?.LoadImage(record.Id);
                job.Exports = record.Exports;
                job.Report(record.State == JobState.Completed ? 1 : record.Progress, record.Stage);
                job.Earlier = true;
                _jobs[job.Id] = job;
                continue;
            }

            Func<BackgroundJob, CancellationToken, string>? work = null;
            var problem = JobStore.WhyNotAgain(record, resumers.ContainsKey(record.Kind));
            if (problem is null)
            {
                try
                {
                    work = resumers[record.Kind](record.Resume!);
                }
                catch (Exception ex) when (ex is ZeyoCutException or InvalidOperationException or ArgumentException or FormatException or NullReferenceException or System.Text.Json.JsonException)
                {
                    problem = JobStore.CantResume(ex);
                }
            }

            if (work is not null)
            {
                JobStore.TakeOver(record);
                job.StartedUtc = DateTime.UtcNow;
                job.Attempt = record.Attempts;
                Run(job, work, record);
                notes.Add($"{job.Id} ({job.Description}) was running when its server stopped; started it again, attempt {job.Attempt} of {JobStore.MaxAttempts}");
                continue;
            }

            job.State = JobState.Failed;
            job.Error = problem;
            job.FinishedUtc = record.LastSeenUtc ?? record.StartedUtc;
            job.Report(record.Progress, record.Stage);
            _jobs[job.Id] = job;
            _store?.Fail(record, problem!);
            notes.Add($"{job.Id} ({job.Description}) failed: {problem}");
        }

        return notes;
    }

    private void Run(BackgroundJob job, Func<BackgroundJob, CancellationToken, string> work, JobRecord? record)
    {
        _jobs[job.Id] = job;
        if (record is not null && _store is { } store)
        {
            store.Save(record);
            job.Reported = store.SaveProgress(record, () => (job.Progress, job.Stage));
        }

        job.Completion = Task.Run(() =>
        {
            try
            {
                job.Result = work(job, job.Cancellation.Token);
                job.Report(1, "done");
                job.State = JobState.Completed;
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Cancelled;
                job.Error = "cancelled";
            }
            catch (Exception ex)
            {
                job.State = JobState.Failed;
                job.Error = ex is ZeyoCutException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                job.FinishedUtc = DateTime.UtcNow;
                if (record is not null && _store is not null)
                {
                    if (job.Image is { } png)
                    {
                        _store.SaveImage(job.Id, png);
                    }

                    _store.SaveFinished(record, job.State, job.FinishedUtc.Value, r =>
                    {
                        r.Result = job.Result;
                        r.Error = job.Error;
                        r.Progress = job.Progress;
                        r.Stage = job.Stage;
                        r.Exports = job.Exports?.ToList();
                    });
                }
            }
        });
    }

    public bool TryGet(string id, out BackgroundJob? job) => _jobs.TryGetValue(id, out job);

    public BackgroundJob Get(string id) =>
        _jobs.TryGetValue(id, out var job)
            ? job
            : throw new ZeyoCutException($"job '{id}' not found; jobs: {(_jobs.IsEmpty ? "(none)" : string.Join(", ", All.Select(j => j.Id)))}");
}
