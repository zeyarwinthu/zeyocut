// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Jobs;

namespace ZeyoCut.Render.Export;

public enum ExportState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class ExportJob
{
    private int _framesDone;

    internal ExportJob(string id, string projectDirectory, string outputPath, int totalFrames)
    {
        Id = id;
        ProjectDirectory = projectDirectory;
        OutputPath = outputPath;
        TotalFrames = totalFrames;
        StartedUtc = DateTime.UtcNow;
    }

    public string Id { get; }
    public string ProjectDirectory { get; }
    public string OutputPath { get; }
    public int TotalFrames { get; }
    public int FramesDone => Volatile.Read(ref _framesDone);
    public ExportState State { get; internal set; } = ExportState.Running;
    public string? Error { get; internal set; }
    public ExportResult? Result { get; internal set; }
    public DateTime StartedUtc { get; internal set; }
    public DateTime? FinishedUtc { get; internal set; }
    public Task Completion { get; internal set; } = Task.CompletedTask;
    internal CancellationTokenSource Cancellation { get; } = new();

    // 1 for the first run; more when a restart started it again.
    public int Attempt { get; internal set; } = 1;

    // Finished in an earlier server session (read back from the jobs folder).
    public bool Earlier { get; internal set; }

    internal Action? Reported { get; set; }

    public double Percent => TotalFrames == 0 ? 100 : Math.Round(100.0 * FramesDone / TotalFrames, 1);

    internal void ReportFrames(int done)
    {
        Volatile.Write(ref _framesDone, done);
        Reported?.Invoke();
    }
}

public sealed class ExportJobManager
{
    public const string Kind = "export";

    private readonly ConcurrentDictionary<string, ExportJob> _jobs = new(StringComparer.Ordinal);
    private readonly JobStore? _store;
    private int _counter;

    // With a store, every export is written to the jobs folder, and Recover starts one again
    // after a restart.
    public ExportJobManager(JobStore? store = null)
    {
        _store = store;
    }

    // Picks the video encoder for a CRF when the export starts; without it, exports use libx264.
    public Func<int, EncoderChoice>? ChooseEncoder { get; init; }

    public ExportJob Start(Project snapshot, string projectDirectory, string outputPath, int crf, FfmpegPaths ffmpeg, Action<string>? log = null, Preset? preset = null)
    {
        ProjectValidator.ThrowIfInvalid(snapshot);
        MatteFiles.ThrowIfPending(snapshot, projectDirectory);
        var id = _store?.Claim(Kind) ?? $"export_{Interlocked.Increment(ref _counter)}";
        var job = new ExportJob(id, projectDirectory, Path.GetFullPath(outputPath), FrameMath.FrameCount(snapshot));
        var resume = new JsonObject
        {
            ["projectDirectory"] = projectDirectory,
            ["outputPath"] = job.OutputPath,
            ["crf"] = crf,
            ["preset"] = preset?.Name,
            ["project"] = JsonNode.Parse(ProjectJson.Serialize(snapshot)),
        };
        Run(job, snapshot, crf, () => ffmpeg, log, preset, _store?.NewRecord(id, Kind, Describe(job), resume));
        return job;
    }

    // Takes over the exports no live server works on (JobStore.Unowned, kind export): one that
    // was running when its server stopped is started again from its saved snapshot under its own
    // id, up to JobStore.MaxAttempts runs, or else marked failed. Finished ones can still be
    // looked up by id. Returns a line for the log about each export started again or failed.
    public IReadOnlyList<string> Recover(IEnumerable<JobRecord> records, Func<FfmpegPaths> ffmpeg, Action<string>? log = null)
    {
        var notes = new List<string>();
        foreach (var record in records)
        {
            if (record.State != JobState.Running)
            {
                var earlier = new ExportJob(record.Id, "", record.Export?.OutputPath ?? "", record.Export?.FrameCount ?? 0)
                {
                    StartedUtc = record.StartedUtc,
                    FinishedUtc = record.FinishedUtc,
                    State = ToExportState(record.State),
                    Error = record.Error,
                    Result = record.Export,
                    Attempt = record.Attempts,
                    Earlier = true,
                };
                _jobs[earlier.Id] = earlier;
                continue;
            }

            var problem = JobStore.WhyNotAgain(record, resumable: true);
            if (problem is null)
            {
                try
                {
                    var resume = record.Resume!;
                    var project = ProjectJson.Deserialize(resume["project"]!.ToJsonString());
                    var presetName = resume["preset"]?.GetValue<string>();
                    var job = new ExportJob(record.Id, resume["projectDirectory"]!.GetValue<string>(), resume["outputPath"]!.GetValue<string>(), FrameMath.FrameCount(project));
                    var crf = resume["crf"]!.GetValue<int>();
                    JobStore.TakeOver(record);
                    job.Attempt = record.Attempts;
                    Run(job, project, crf, ffmpeg, log, presetName is null ? null : Presets.Get(presetName), record);
                    notes.Add($"{job.Id} ({record.Description}) was running when its server stopped; started it again, attempt {job.Attempt} of {JobStore.MaxAttempts}");
                    continue;
                }
                catch (Exception ex) when (ex is ZeyoCutException or InvalidOperationException or ArgumentException or FormatException or NullReferenceException or System.Text.Json.JsonException)
                {
                    problem = JobStore.CantResume(ex);
                }
            }

            var failed = new ExportJob(record.Id, "", "", 0)
            {
                StartedUtc = record.StartedUtc,
                FinishedUtc = record.LastSeenUtc ?? record.StartedUtc,
                State = ExportState.Failed,
                Error = problem,
                Attempt = record.Attempts,
            };
            _jobs[failed.Id] = failed;
            _store?.Fail(record, problem);
            notes.Add($"{failed.Id} ({record.Description}) failed: {problem}");
        }

        return notes;
    }

    private void Run(ExportJob job, Project snapshot, int crf, Func<FfmpegPaths> ffmpeg, Action<string>? log, Preset? preset, JobRecord? record)
    {
        _jobs[job.Id] = job;
        if (record is not null && _store is { } store)
        {
            store.Save(record);
            job.Reported = store.SaveProgress(record, () => (job.Percent / 100, job.FramesDone >= job.TotalFrames ? "finishing encode" : "rendering"));
        }

        job.Completion = Task.Run(() =>
        {
            try
            {
                job.Result = Exporter.Export(
                    snapshot,
                    job.ProjectDirectory,
                    job.OutputPath,
                    crf,
                    ffmpeg(),
                    (done, _) => job.ReportFrames(done),
                    log,
                    job.Cancellation.Token,
                    preset,
                    workers: Exporter.DefaultWorkers,
                    encoder: ChooseEncoder?.Invoke(crf));
                job.State = ExportState.Completed;
            }
            catch (OperationCanceledException)
            {
                job.State = ExportState.Cancelled;
                job.Error = "the export was cancelled";
            }
            catch (Exception ex)
            {
                job.State = ExportState.Failed;
                job.Error = ex.Message;
                log?.Invoke($"export {job.Id} failed: {ex}");
            }
            finally
            {
                job.FinishedUtc = DateTime.UtcNow;
                if (record is not null && _store is not null)
                {
                    _store.SaveFinished(record, ToJobState(job.State), job.FinishedUtc.Value, r =>
                    {
                        r.Error = job.Error;
                        r.Export = job.Result;
                        r.Progress = job.Percent / 100;
                    });
                }
            }
        });
    }

    public ExportJob Get(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && _jobs.TryGetValue(id, out var job))
        {
            return job;
        }

        var known = _jobs.Keys.Order(StringComparer.Ordinal).ToList();
        throw new ZeyoCutException($"export job '{id}' not found; jobs: {(known.Count == 0 ? "(none)" : string.Join(", ", known))}");
    }

    public bool TryGet(string id, out ExportJob? job) => _jobs.TryGetValue(id, out job);

    public void Cancel(string id) => Get(id).Cancellation.Cancel();

    // This session's exports, with the ones Recover started again or marked failed.
    public IReadOnlyList<ExportJob> All => _jobs.Values.Where(j => !j.Earlier).OrderBy(j => j.StartedUtc).ToList();

    // Exports that finished in earlier sessions, newest first.
    public IReadOnlyList<ExportJob> Earlier => _jobs.Values.Where(j => j.Earlier).OrderByDescending(j => j.StartedUtc).ToList();

    private static string Describe(ExportJob job) => $"start_export to {Path.GetFileName(job.OutputPath)}, {job.TotalFrames} frames";

    private static ExportState ToExportState(JobState state) => state switch
    {
        JobState.Completed => ExportState.Completed,
        JobState.Cancelled => ExportState.Cancelled,
        JobState.Failed => ExportState.Failed,
        _ => ExportState.Running,
    };

    private static JobState ToJobState(ExportState state) => state switch
    {
        ExportState.Completed => JobState.Completed,
        ExportState.Cancelled => JobState.Cancelled,
        ExportState.Failed => JobState.Failed,
        _ => JobState.Running,
    };
}
