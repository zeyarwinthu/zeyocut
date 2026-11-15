// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;

namespace ZeyoCut.Render.Video;

// Starts the background jobs that make the proxies of footage over 2560 px (Proxies), for the server
// and the editor. A proxy being made isn't started again, and the jobs run one at a time.
public sealed class ProxyJobs(JobManager jobs, Func<FfmpegPaths> ffmpeg)
{
    private readonly HashSet<string> _making = new(StringComparer.OrdinalIgnoreCase);

    // Proxy jobs run one at a time.
    public static SemaphoreSlim Gate { get; } = new(1, 1);

    // Starts a background job that makes the proxies the session's footage over 2560 px is missing (of
    // the given assets, or of all of it), one after another. Returns a line for the reply, starting
    // with a newline: the job, or why none can be made; empty when none is missing or the missing
    // ones are being made.
    public string Start(ProjectSession session, IReadOnlyCollection<string>? assetIds = null)
    {
        var directory = session.Directory;
        var missing = session.Read(p => p.Assets.Where(a => Proxies.Wanted(a) && (assetIds is null || assetIds.Contains(a.Id))).ToList())
            .Where(a => ProjectStore.IsAssetFile(a.File) && File.Exists(ProjectStore.AssetPath(directory, a)) && Proxies.Ready(directory, a) is null)
            .ToList();
        if (missing.Count == 0)
        {
            return "";
        }

        FfmpegPaths paths;
        try
        {
            paths = ffmpeg();
        }
        catch (ZeyoCutException ex)
        {
            return $"\npreviews of {Quoted(missing)} read the original, since no proxy can be made: {ex.Message}";
        }

        lock (_making)
        {
            missing = missing.Where(a => _making.Add(Proxies.PathOf(directory, a))).ToList();
        }

        if (missing.Count == 0)
        {
            return "";
        }

        var outputs = missing.Select(a => Proxies.PathOf(directory, a)).ToList();
        var names = Quoted(missing);
        var count = missing.Count == 1 ? "a 1920 px proxy" : $"{missing.Count} 1920 px proxies";
        try
        {
            var job = jobs.Start("proxy", $"make {count} of {names} for previews (open_project makes any that are missing)", (job, ct) =>
            {
                try
                {
                    if (!Gate.Wait(0, ct))
                    {
                        job.Report(0, "waiting for another proxy job to finish");
                        Gate.Wait(ct);
                    }

                    try
                    {
                        return Make(job, paths, directory, missing, ct);
                    }
                    finally
                    {
                        Gate.Release();
                    }
                }
                finally
                {
                    Done(outputs);
                }
            });
            return $"\nmaking {count} of {names} in the background as {job.Id}: previews and object tracking read it once it is done, exports always read the original";
        }
        catch
        {
            Done(outputs);
            throw;
        }
    }

    private static string Make(BackgroundJob job, FfmpegPaths ffmpeg, string directory, List<Asset> assets, CancellationToken ct)
    {
        var made = new List<string>();
        var problems = new List<string>();
        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            var index = i;
            job.Report((double)i / assets.Count, $"making the proxy of '{asset.Id}' ({i + 1} of {assets.Count})");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var path = Proxies.Make(ffmpeg.Ffmpeg, directory, asset, p => job.Report((index + p) / assets.Count), ct,
                    () => job.Report((double)index / assets.Count, $"waiting for another program making the proxy of '{asset.Id}' ({index + 1} of {assets.Count})"));
                var (width, height) = Proxies.Size(asset);
                var megabytes = new FileInfo(path).Length / 1048576.0;
                made.Add($"- '{asset.Id}': {width}x{height}, {megabytes.ToString("0.0", CultureInfo.InvariantCulture)} MB, in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s: {Path.GetRelativePath(directory, path)}");
            }
            catch (Exception ex) when (ex is ZeyoCutException or IOException)
            {
                problems.Add($"- '{asset.Id}': {ex.Message}");
            }
        }

        if (made.Count == 0)
        {
            throw new ZeyoCutException($"no proxy could be made:\n{string.Join("\n", problems)}");
        }

        return $"made {made.Count} prox{(made.Count == 1 ? "y" : "ies")} for previews and object tracking (exports read the original):\n{string.Join("\n", made)}"
            + (problems.Count > 0 ? $"\ncouldn't make {problems.Count}:\n{string.Join("\n", problems)}" : "");
    }

    private void Done(IEnumerable<string> paths)
    {
        lock (_making)
        {
            foreach (var path in paths)
            {
                _making.Remove(path);
            }
        }
    }

    private static string Quoted(IEnumerable<Asset> assets) => string.Join(", ", assets.Select(a => $"'{a.Id}'"));
}
