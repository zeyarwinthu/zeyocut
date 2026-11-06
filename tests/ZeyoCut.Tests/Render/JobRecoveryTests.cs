// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Render;

public class JobRecoveryTests
{
    [Fact]
    public void IdsContinueAcrossServersSharingTheFolder()
    {
        var folder = TestPaths.NewDirectory("jobs-ids");
        var first = new JobManager(new JobStore(folder));
        var a = first.Start("frames", "a", (_, _) => "done");
        var b = first.Start("frames", "b", (_, _) => "done");
        var c = first.Start("framediff", "c", (_, _) => "done");
        Task.WaitAll(a.Completion, b.Completion, c.Completion);
        Assert.Equal(["frames_1", "frames_2", "framediff_1"], new[] { a.Id, b.Id, c.Id });

        var second = new JobManager(new JobStore(folder));
        var d = second.Start("frames", "d", (_, _) => "done");
        d.Completion.Wait();
        Assert.Equal("frames_3", d.Id);

        var store = new JobStore(folder);
        var ids = Enumerable.Range(0, 20).AsParallel().Select(_ => store.Claim("export")).ToList();
        Assert.Equal(20, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 20).Select(n => $"export_{n}").Order(), ids.Order());
    }

    [Fact]
    public void AFinishedJobIsWrittenDownWithItsResult()
    {
        var folder = TestPaths.NewDirectory("jobs-finished");
        var manager = new JobManager(new JobStore(folder));
        var job = manager.Start("beats", "detect_beats song", (job, _) =>
        {
            job.Report(0.5, "listening");
            job.Image = [1, 2, 3];
            return "120 BPM";
        }, new JsonObject { ["asset"] = "song" });
        job.Completion.Wait();

        var record = Read(folder, job.Id);
        Assert.Equal((JobState.Completed, "120 BPM", "detect_beats song"), (record.State, record.Result, record.Description));
        Assert.Null(record.Resume);
        Assert.Equal(JobStore.ProcessId, record.ProcessId);
        Assert.Equal(job.FinishedUtc, record.FinishedUtc);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(folder, job.Id + ".png")));
        Assert.False(File.Exists(Path.Combine(folder, job.Id + ".json.tmp")));

        // This server still runs, so another one using the folder leaves its jobs alone.
        Assert.Empty(new JobStore(folder).Unowned());
    }

    [Fact]
    public void AfterARestartExportsRunAgainAndOtherJobsFail()
    {
        var folder = TestPaths.NewDirectory("jobs-restart");
        var store = new JobStore(folder);
        var now = DateTime.UtcNow;
        var seen = now.AddMinutes(-3);
        store.Save(Gone("batch_1", "batch", now.AddMinutes(-10), new JsonObject { ["files"] = 2 }, attempts: 1, 0.4, "exporting youtube_1080p", seen));
        store.Save(Gone("frames_1", "frames", now.AddMinutes(-9), null, attempts: 1, 0.25, "writing frames", seen));
        store.Save(Gone("batch_2", "batch", now.AddMinutes(-8), new JsonObject { ["files"] = 1 }, attempts: 3, 0.9, "exporting shorts_1080p", seen));
        var done = Gone("analyze_1", "analyze", now.AddMinutes(-7), null, attempts: 1, 1, "done", now.AddMinutes(-6));
        (done.State, done.Result, done.FinishedUtc) = (JobState.Completed, "3 scene cuts", now.AddMinutes(-6));
        store.Save(done);
        var old = Gone("analyze_2", "analyze", now.AddDays(-9), null, attempts: 1, 0.1, null, null);
        (old.State, old.Error, old.FinishedUtc) = (JobState.Failed, "no ffmpeg", now.AddDays(-8));
        store.Save(old);
        var live = Gone("matte_1", "matte", now.AddMinutes(-5), null, attempts: 1, 0.5, "matting", seen);
        live.ProcessStartedUtc = JobStore.ProcessStartedUtc;
        store.Save(live);
        File.WriteAllText(Path.Combine(folder, "detect_1.json"), "");

        var records = store.Unowned();
        Assert.Equal(["batch_1", "frames_1", "batch_2", "analyze_1"], records.Select(r => r.Id));
        Assert.False(File.Exists(Path.Combine(folder, "analyze_2.json")));
        Assert.True(File.Exists(Path.Combine(folder, "matte_1.json")));

        var manager = new JobManager(store);
        var notes = manager.Recover(records, new Dictionary<string, Func<JsonObject, Func<BackgroundJob, CancellationToken, string>>>
        {
            ["batch"] = resume => (job, _) => $"exported {resume["files"]} files again",
        });
        Assert.Equal(3, notes.Count);
        Assert.Equal("batch_1 (batch_1 job) was running when its server stopped; started it again, attempt 2 of 3", notes[0]);

        var resumed = manager.Get("batch_1");
        resumed.Completion.Wait();
        Assert.Equal((JobState.Completed, "exported 2 files again", 2), (resumed.State, resumed.Result, resumed.Attempt));
        var saved = Read(folder, "batch_1");
        Assert.Equal((JobState.Completed, 2, JobStore.ProcessId), (saved.State, saved.Attempts, saved.ProcessId));
        Assert.Null(saved.Resume);

        var frames = manager.Get("frames_1");
        var heard = seen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal(JobState.Failed, frames.State);
        Assert.Equal($"the server stopped while this job ran (25% done, writing frames, last heard from at {heard}), so it didn't finish; start it again", frames.Error);
        Assert.Equal(seen, frames.FinishedUtc);
        Assert.Equal(JobState.Failed, Read(folder, "frames_1").State);

        Assert.Equal("the server stopped 3 times while this job ran, so it isn't started again; start it again yourself", manager.Get("batch_2").Error);
        Assert.Null(Read(folder, "batch_2").Resume);

        Assert.Equal(["batch_1", "frames_1", "batch_2"], manager.All.Select(j => j.Id));
        var earlier = Assert.Single(manager.Earlier);
        Assert.Equal(("analyze_1", JobState.Completed, "3 scene cuts"), (earlier.Id, earlier.State, earlier.Result));
        Assert.True(manager.TryGet("analyze_1", out _));

        // Only the finished job is left for another server to show, and new ids follow the old ones.
        Assert.Equal(["analyze_1"], new JobStore(folder).Unowned().Select(r => r.Id));
        Assert.Equal("batch_3", manager.Start("batch", "next", (_, _) => "").Id);
        Assert.Equal("detect_2", manager.Start("detect", "next", (_, _) => "").Id);
    }

    [Fact]
    public void AnExportThatCantBeReadBackFailsWithTheReason()
    {
        var folder = TestPaths.NewDirectory("jobs-broken");
        var store = new JobStore(folder);
        store.Save(Gone("batch_1", "batch", DateTime.UtcNow, new JsonObject(), attempts: 1, 0, null, null));

        var manager = new JobManager(store);
        manager.Recover(store.Unowned(), new Dictionary<string, Func<JsonObject, Func<BackgroundJob, CancellationToken, string>>>
        {
            ["batch"] = _ => throw new ZeyoCutException("the saved batch doesn't list one file per preset"),
        });
        Assert.Equal("the server stopped while this job ran, and it can't be started again: the saved batch doesn't list one file per preset", manager.Get("batch_1").Error);
    }

    [Fact]
    public void TheTimeLeftComesFromTheProgressSoFar()
    {
        Assert.Null(JobTime.Left(0.01, TimeSpan.FromSeconds(10)));
        Assert.Null(JobTime.Left(0.5, TimeSpan.FromSeconds(2)));
        Assert.Null(JobTime.Left(1, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(10), JobTime.Left(0.5, TimeSpan.FromSeconds(10)));
        Assert.Equal(", about 30s left", JobTime.LeftText(0.25, TimeSpan.FromSeconds(10)));
        Assert.Equal("", JobTime.LeftText(0.25, TimeSpan.FromSeconds(1)));
        Assert.Equal("89s", JobTime.Format(TimeSpan.FromSeconds(88.2)));
        Assert.Equal("2 min", JobTime.Format(TimeSpan.FromSeconds(100)));
        Assert.Equal("1 h 40 min", JobTime.Format(TimeSpan.FromSeconds(6000)));
    }

    // A running job's record from a server that's gone: this process id with another start time.
    private static JobRecord Gone(string id, string kind, DateTime started, JsonObject? resume, int attempts, double progress, string? stage, DateTime? seen) => new()
    {
        Id = id,
        Kind = kind,
        Description = $"{id} job",
        ProcessId = JobStore.ProcessId,
        ProcessStartedUtc = JobStore.ProcessStartedUtc.AddMinutes(-30),
        StartedUtc = started,
        Attempts = attempts,
        Progress = progress,
        Stage = stage,
        LastSeenUtc = seen,
        Resume = resume,
    };

    private static JobRecord Read(string folder, string id) =>
        JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(Path.Combine(folder, id + ".json")), ProjectJson.Options)!;
}
