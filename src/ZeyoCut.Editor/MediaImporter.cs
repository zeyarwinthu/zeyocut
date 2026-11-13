// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Editor;

// A file being imported, as the media library shows it until it's added: what's happening to it, how
// far normalizing has got, or why it couldn't be imported.
public sealed class PendingImport(string source) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Source { get; } = source;

    public string Name => Path.GetFileName(Source);

    public string Stage { get; private set; } = "waiting";

    public double Progress { get; private set; }

    public bool Failed { get; private set; }

    // The job normalizing it, while it runs.
    public BackgroundJob? Job { get; private set; }

    public bool Normalizing => Job is { State: JobState.Running };

    // Takes the normalizing job's progress.
    public void Update()
    {
        if (Job is { State: JobState.Running } job)
        {
            Show($"{job.Stage} {job.Percent}%", job.Progress);
        }
    }

    internal void Show(string stage, double progress = 0)
    {
        Stage = stage;
        Progress = progress;
        Changed();
    }

    internal void Start(BackgroundJob job)
    {
        Job = job;
        Update();
    }

    internal void Fail(string message)
    {
        Failed = true;
        Job = null;
        Stage = message;
        Changed();
    }

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

// Imports the files you pick into the open project as import_asset and import_video do: images, audio
// and fonts are copied in; a video is copied in when it has a constant frame rate, no rotation and 8
// bits, and is normalized first in a background job otherwise, one video at a time. Files are copied
// before the save takes project.lock, and each is added as its own edit, on the UI thread (post).
public sealed class MediaImporter(EditorDocument document, JobManager jobs, Func<FfmpegPaths> ffmpeg, Action<Action> post)
{
    private readonly SemaphoreSlim _normalizing = new(1, 1);

    // Called on the UI thread when an edit added an asset.
    public event Action<Asset>? Added;

    public ObservableCollection<PendingImport> Pending { get; } = [];

    // Imports the files in order. The task completes when each is added or has failed.
    public Task Import(IEnumerable<string> files)
    {
        var items = files.Select(f => new PendingImport(Path.GetFullPath(f))).ToList();
        foreach (var item in items)
        {
            Pending.Add(item);
        }

        return Task.Run(async () =>
        {
            var normalizing = new List<Task>();
            foreach (var item in items)
            {
                try
                {
                    if (ImportOne(item) is { } job)
                    {
                        normalizing.Add(job);
                    }
                }
                catch (Exception ex) when (ex is ZeyoCutException or IOException or UnauthorizedAccessException)
                {
                    post(() => item.Fail($"couldn't import: {ex.Message}"));
                }
            }

            await Task.WhenAll(normalizing);
        });
    }

    public void Cancel(PendingImport item) => item.Job?.Cancellation.Cancel();

    public void Dismiss(PendingImport item) => Pending.Remove(item);

    // Stops normalizing the videos not added yet, as when the project is closed.
    public void CancelAll()
    {
        foreach (var item in Pending)
        {
            Cancel(item);
        }
    }

    // Copies the file in and adds it, or starts normalizing it and returns the job's task.
    private Task? ImportOne(PendingImport item)
    {
        var directory = document.Session.Directory;
        var type = AssetImporter.DetectType(item.Source)
            ?? throw new ZeyoCutException($"'{Path.GetExtension(item.Source)}' files can't be imported; ZeyoCut imports images, audio and fonts ({AssetImporter.SupportedExtensions}) and video ({AssetImporter.VideoExtensions})");
        if (type != AssetType.Video)
        {
            post(() => item.Show("copying"));
            var prepared = AssetImporter.Prepare(directory, item.Source, new AssetProbe(path => Ffprobe.AudioDuration(ffmpeg().Ffprobe, path)));
            post(() => Add(item, new AddAssetOperation(prepared)));
            return null;
        }

        post(() => item.Show("checking"));
        var paths = ffmpeg();
        var (probe, reasons) = VideoImport.Check(paths, item.Source);
        if (reasons.Count == 0)
        {
            post(() => item.Show("copying"));
            var prepared = VideoImport.Prepare(directory, item.Source, probe);
            post(() => Add(item, new AddAssetOperation(prepared)));
            return null;
        }

        var output = AssetImporter.NewAssetPath(directory, item.Source, ".mp4");
        PreparedAsset? normalized = null;
        var job = jobs.Start("import", $"normalize {item.Name}", (job, ct) =>
        {
            if (!_normalizing.Wait(0, ct))
            {
                job.Report(0, "waiting for the video before it");
                _normalizing.Wait(ct);
            }

            try
            {
                job.Report(0, $"normalizing ({string.Join(", ", reasons)})");
                var result = VideoNormalizer.Normalize(paths, item.Source, probe, output, p => job.Report(p * 0.98), ct);
                normalized = VideoImport.PrepareNormalized(directory, output, result);
                return $"normalized {item.Name}";
            }
            finally
            {
                _normalizing.Release();
            }
        });
        post(() => item.Start(job));
        return job.Completion.ContinueWith(_ => post(() =>
        {
            if (job.State == JobState.Completed && normalized is { } prepared)
            {
                if (!Add(item, new AddAssetOperation(prepared, null, item.Source, VideoImport.Reasons(reasons))))
                {
                    File.Delete(output);
                }
            }
            else
            {
                item.Fail(job.State == JobState.Cancelled ? "cancelled" : $"couldn't normalize: {job.Error}");
            }
        }), TaskScheduler.Default);
    }

    private bool Add(PendingImport item, AddAssetOperation operation)
    {
        if (!document.Apply(operation))
        {
            item.Fail(document.Status);
            return false;
        }

        Pending.Remove(item);
        var added = document.View.Assets.LastOrDefault(a => string.Equals(ProjectStore.AssetPath(document.Session.Directory, a), operation.Asset.Path, StringComparison.OrdinalIgnoreCase));
        if (added is not null)
        {
            Added?.Invoke(added);
        }

        return true;
    }
}
