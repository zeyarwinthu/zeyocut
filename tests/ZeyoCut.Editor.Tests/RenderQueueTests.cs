// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;

namespace ZeyoCut.Editor.Tests;

public class RenderQueueTests
{
    // A short title over a plain background, with an asset nothing uses whose file is gone (it
    // doesn't keep the export from starting).
    private static string Titled(string name)
    {
        var project = TestFiles.Project(duration: 1);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", Start = 0, End = 1 });
        project.Assets.Add(new Asset { Id = "unused", Type = AssetType.Image, File = "assets/unused.png", Width = 8, Height = 8 });
        return Create(name, project);
    }

    // A project folder named name.
    private static string Create(string name, Project project)
    {
        var directory = Path.Combine(TestFiles.NewFolder(name), name);
        ProjectSession.Create(directory, project);
        return directory;
    }

    private static (RenderQueue Queue, List<string> Started) Queue(string name)
    {
        var start = new ExportStart(VideoEncoder.X264, TestFiles.NewFolder(name + "-library"), TestFiles.Ffmpeg);
        var exports = new ExportJobManager { ChooseEncoder = crf => start.ChooseEncoder(crf) };
        var started = new List<string>();
        var queue = new RenderQueue(item =>
        {
            started.Add(item.Name);
            return start.Start(exports, item.Snapshot, item.Directory, item.Name, item.Crf, item.Preset, item.Language, null, workers: 1);
        }, job => exports.Cancel(job.Id));
        return (queue, started);
    }

    private static QueueItem Item(string directory, string name) =>
        new() { Directory = directory, Snapshot = ProjectStore.Load(directory), Name = name, Crf = 32, Settings = ExportChoices.ProjectSettings };

    // Polls the queue, as the window does, until the export has finished.
    private static void Finish(RenderQueue queue, QueueItem item)
    {
        for (var i = 0; i < 6000 && item.State is QueueState.Running or QueueState.Queued; i++)
        {
            queue.Poll();
            Thread.Sleep(10);
        }

        Assert.NotEqual(QueueState.Running, item.State);
    }

    [Fact]
    public void QueuedExportsRunOneAfterAnotherAndCanBePausedAndRemoved()
    {
        var directory = Titled("queue-order");
        var (queue, started) = Queue("queue-order");
        var a = queue.Add(Item(directory, "a.mp4"));
        var b = queue.Add(Item(directory, "b.mp4"));
        var c = queue.Add(Item(directory, "c.mp4"));
        Assert.Equal([QueueState.Running, QueueState.Queued, QueueState.Queued], new[] { a, b, c }.Select(i => i.State));
        Assert.Equal(["a.mp4"], started);
        Assert.Equal((3, 2), (queue.Left, queue.Waiting.Count));
        var now = DateTime.Now;
        Assert.Equal(["next", "2nd in line"], new[] { b, c }.Select(i => queue.Row(i, now).Detail));
        Assert.Equal(["Remove", "Remove"], new[] { b, c }.Select(i => queue.Row(i, now).Action));
        Assert.Equal("queue-order · Project settings", queue.Row(a, now).Line);

        // The same file can't wait twice.
        Assert.Equal("b.mp4 of queue-order is in the render queue already", Assert.Throws<ZeyoCutException>(() => queue.Add(Item(directory, "b.mp4"))).Message);

        // Paused, the running export finishes and the next one doesn't start.
        queue.Pause(true);
        Assert.Equal("the queue is paused", queue.Row(b, DateTime.Now).Detail);
        Finish(queue, a);
        Assert.Equal((QueueState.Done, QueueState.Queued), (a.State, b.State));
        Assert.Null(queue.Running);
        Assert.Equal(["a.mp4"], started);
        var done = queue.Row(a, DateTime.Now);
        Assert.Equal(("Done", "Open", QueueTone.Done), (done.Status, done.Action, done.Tone));
        Assert.Matches(@"^\d+(\.\d)? (KB|MB|bytes) · \d\d:\d\d$", done.Detail);

        // Removed, c won't render; a running export can't be removed.
        Assert.True(queue.Remove(c));
        queue.Pause(false);
        Assert.Equal(QueueState.Running, b.State);
        Assert.False(queue.Remove(b));
        Finish(queue, b);
        Assert.Equal(["a.mp4", "b.mp4"], started);
        var exports = Path.Combine(directory, ProjectStore.ExportsFolder);
        Assert.True(File.Exists(Path.Combine(exports, "a.mp4")) && File.Exists(Path.Combine(exports, "b.mp4")));
        Assert.False(File.Exists(Path.Combine(exports, "c.mp4")));

        // Finished ones are listed newest first.
        Assert.Equal(["b.mp4", "a.mp4"], queue.Rows([], DateTime.Now).Select(r => r.File));
    }

    [Fact]
    public void CancellingStopsTheRunningExportAndOneWaitingNeverStarts()
    {
        var directory = Titled("queue-cancel");
        var (queue, started) = Queue("queue-cancel");
        var a = queue.Add(Item(directory, "a.mp4"));
        var b = queue.Add(Item(directory, "b.mp4"));
        queue.Cancel(b);
        Assert.Equal(QueueState.Stopped, b.State);
        queue.Cancel(a);
        Finish(queue, a);
        Assert.Equal(QueueState.Stopped, a.State);
        Assert.Equal(["a.mp4"], started);
        Assert.Equal(("Stopped", "nothing was written", "Remove"), (queue.Row(a, DateTime.Now).Status, queue.Row(a, DateTime.Now).Detail, queue.Row(a, DateTime.Now).Action));
        Assert.Equal(0, queue.Left);
        Assert.False(File.Exists(Path.Combine(directory, ProjectStore.ExportsFolder, "a.mp4")));
    }

    // An export whose project uses a file that's gone fails before it starts, with Relink, and the
    // next one runs.
    [Fact]
    public void AnExportWithAMissingFileFailsWithRelinkAndTheNextRuns()
    {
        var project = TestFiles.Project(duration: 1);
        project.Assets.Add(new Asset { Id = "clouds", Type = AssetType.Image, File = "assets/clouds.png", Width = 8, Height = 8 });
        new AddClipOperation(AssetType.Image, "clouds", 0).Apply(project);
        var broken = Create("queue-missing", project);
        var directory = Titled("queue-after-missing");
        var (queue, started) = Queue("queue-missing");
        var a = queue.Add(Item(broken, "draft.mp4"));
        Assert.Equal(QueueState.Failed, a.State);
        Assert.Equal("clouds", a.Missing?.Id);
        var row = queue.Row(a, DateTime.Now);
        Assert.Equal(("Failed", "clouds.png is missing", "Relink", QueueTone.Failed), (row.Status, row.Detail, row.Action, row.Tone));
        Assert.Empty(started);

        var b = queue.Add(Item(directory, "b.mp4"));
        Assert.Equal(QueueState.Running, b.State);
        Finish(queue, b);
        Assert.Equal(QueueState.Done, b.State);
    }

    // Claude's exports are listed from every project's job records, read-only.
    [Fact]
    public void ClaudesRunningExportsAreListedFromEveryProject()
    {
        var folder = TestFiles.NewFolder("queue-claude");
        var one = Path.Combine(folder, "timeline-demo");
        var two = Path.Combine(folder, "comic-panels");
        JobRecord Record(string id, double progress, string? stage, JsonObject? resume) =>
            new() { Id = id, Kind = id[..id.IndexOf('_')], Progress = progress, Stage = stage, Resume = resume, StartedUtc = DateTime.UtcNow };
        var rows = RenderQueue.ClaudeRows(
        [
            Record("export_1", 0.62, "rendering", new JsonObject { ["projectDirectory"] = one, ["outputPath"] = Path.Combine(one, "exports", "timeline-demo-ja.mp4") }),
            Record("batch_1", 0.25, "exporting shorts_1080p", new JsonObject
            {
                ["projectDirectory"] = two + Path.DirectorySeparatorChar,
                ["outputs"] = new JsonArray(Path.Combine(two, "exports", "a.mp4"), Path.Combine(two, "exports", "b.mp4")),
                ["presets"] = new JsonArray("youtube_1080p", "shorts_1080p"),
            }),
            Record("analyze_1", 0.5, null, null),
        ], DateTime.Now);

        Assert.Equal(["timeline-demo-ja.mp4", "a.mp4, b.mp4"], rows.Select(r => r.File));
        Assert.Equal(["timeline-demo · Project settings", "comic-panels · youtube_1080p, shorts_1080p"], rows.Select(r => r.Line));
        Assert.Equal(["Rendering 62%", "Exporting shorts_1080p 25%"], rows.Select(r => r.Status));
        Assert.All(rows, r => Assert.Equal(("CLAUDE", (string?)null, QueueTone.Running, true), (r.Who, r.Action, r.Tone, r.HasBar)));
        Assert.Equal([one, two + Path.DirectorySeparatorChar], rows.Select(r => r.Directory));
    }

    [Fact]
    public void PlacesInLineAreOrdinals() =>
        Assert.Equal(["1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "22nd", "111th"], new[] { 1, 2, 3, 4, 11, 12, 13, 21, 22, 111 }.Select(RenderQueue.Ordinal));

    // Clear deletes the export chunks, preview sound, pictures and waveforms of each project, and
    // nothing else; a file a program has open stays.
    [Fact]
    public void ClearDeletesOnlyTheFoldersThatAreMadeAgain()
    {
        var folder = TestFiles.NewFolder("queue-cache");
        List<string> projects = [Path.Combine(folder, "one"), Path.Combine(folder, "two")];
        string Write(string project, string path, int bytes)
        {
            var file = Path.Combine(project, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[bytes]);
            return file;
        }

        var kept = new List<string>();
        foreach (var project in projects)
        {
            Write(project, @"cache\chunks\0001.mp4", 100);
            Write(project, @"cache\chunks\deep\0002.mp4", 100);
            Write(project, @"cache\preview\mix-3.m4a", 30);
            Write(project, @"cache\thumbnails\logo.png", 40);
            Write(project, @"cache\waveforms\talk.bin", 50);
            kept.Add(Write(project, @"cache\other\keep.bin", 7));
            kept.Add(Write(project, @"proxies\talk.mp4", 7));
            kept.Add(Write(project, @"exports\talk.mp4", 7));
            kept.Add(Write(project, @"backups\project-1.json", 7));
            kept.Add(Write(project, @"tracks\face.json", 7));
            kept.Add(Write(project, ProjectStore.ProjectFileName, 7));
        }

        var sizes = CacheFolders.Sizes([.. projects, projects[0]]);
        Assert.Equal(["Export chunks", "Preview sound", "Pictures", "Waveforms"], sizes.Select(s => s.Kind.Name));
        Assert.Equal([400L, 60, 80, 100], sizes.Select(s => s.Bytes));

        string open = Path.Combine(projects[1], @"cache\waveforms\talk.bin");
        CacheCleared cleared;
        using (File.Open(open, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            cleared = CacheFolders.Clear(projects);
        }

        Assert.Equal((590L, 1), (cleared.Freed, cleared.Kept));
        Assert.All(kept, f => Assert.True(File.Exists(f), f));
        Assert.True(File.Exists(open));
        Assert.Equal([0L, 0, 0, 50], CacheFolders.Sizes(projects).Select(s => s.Bytes));
        Assert.Equal([0L, 0, 0, 0], CacheFolders.Sizes([Path.Combine(folder, "none")]).Select(s => s.Bytes));
    }
}
