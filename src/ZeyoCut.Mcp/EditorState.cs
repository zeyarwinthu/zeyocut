// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Mcp.Tools;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Jobs;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Mcp;

public sealed class EditorState : IDisposable
{
    private readonly object _gate = new();
    private readonly object _encoderGate = new();
    private readonly Lazy<string> _machine = new(() => SystemInfo.Hardware().Machine);
    private readonly ProxyJobs _proxies;
    private ProjectSession? _session;
    private FrameRenderer? _renderer;
    private FfmpegPaths? _ffmpeg;

    public EditorState(Settings settings)
    {
        Settings = settings;
        Library = new Library(settings.LibraryPath ?? Settings.DefaultLibraryPath);
        Models = new ModelStore(settings.ModelsPath ?? Path.Combine(settings.LibraryPath ?? Settings.DefaultLibraryPath, "models"));
        AiDevice = AiSession.ParseDevice(settings.AiDevice);
        BenchmarkPath = Path.Combine(settings.LibraryPath ?? Settings.DefaultLibraryPath, AiBenchmark.FileName);
        EncoderTestsPath = Path.Combine(settings.LibraryPath ?? Settings.DefaultLibraryPath, VideoEncoders.FileName);
        try
        {
            VideoEncoder = VideoEncoders.Parse(settings.VideoEncoder);
        }
        catch (ZeyoCutException ex)
        {
            Console.Error.WriteLine($"warning: VideoEncoder setting: {ex.Message}; using auto");
        }

        JobRecords = settings.JobsDirectory is { } jobs ? new JobStore(jobs) : null;
        Exports = new ExportJobManager(JobRecords) { ChooseEncoder = ChooseEncoder };
        Jobs = new JobManager(JobRecords);
        _proxies = new ProxyJobs(Jobs, RequireFfmpeg);
        PreviewPage = new PreviewServer(this);
    }

    public Settings Settings { get; }

    public Library Library { get; }

    public ExportJobManager Exports { get; }

    public JobManager Jobs { get; }

    // preview_server's page (stopped until it is started).
    public PreviewServer PreviewPage { get; }

    // The jobs folder (Settings.JobsDirectory), or null when jobs live in memory only.
    public JobStore? JobRecords { get; }

    public ModelStore Models { get; }

    // auto (default), cpu or gpu; PickDevice says what auto means for a model.
    public AiDevice AiDevice { get; }

    // system_check's results (<library>\benchmark.json).
    public string BenchmarkPath { get; }

    // The VideoEncoder setting, or null for auto.
    public VideoEncoder? VideoEncoder { get; }

    // What the encoder test encodes found (<library>\encoders.json).
    public string EncoderTestsPath { get; }

    public string Machine => _machine.Value;

    // Running AI matte jobs by the matte file they write, so asking again joins the running job.
    public ConcurrentDictionary<string, BackgroundJob> MatteJobs { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Tracks being computed, by track id: their tracking_N job, or null while a call tracks directly.
    public ConcurrentDictionary<string, BackgroundJob?> TrackWork { get; } = new(StringComparer.Ordinal);

    public object RenderGate { get; } = new();

    // The last benchmark, unless it was measured on other hardware.
    public BenchmarkResults? Benchmark() => AiBenchmark.Load(BenchmarkPath, Machine);

    public DeviceChoice PickDevice(AiFeature feature) => AiBenchmark.Pick(AiDevice, feature, Benchmark());

    // The encoder for an export at this CRF; the first export on a computer tests the GPU encoders.
    public EncoderChoice ChooseEncoder(int crf)
    {
        var ffmpeg = RequireFfmpeg();
        lock (_encoderGate)
        {
            return VideoEncoders.Choose(VideoEncoder, crf, EncoderTestsPath, Machine, VideoEncoders.Stamp(ffmpeg), encoder => VideoEncoders.Try(ffmpeg, encoder));
        }
    }

    // Starts the jobs that make the proxies the session's footage over 2560 px is missing (ProxyJobs.Start).
    public string StartProxies(ProjectSession session, IReadOnlyCollection<string>? assetIds = null) => _proxies.Start(session, assetIds);

    public ProjectSession Session
    {
        get
        {
            lock (_gate)
            {
                return _session ?? throw new McpException(
                    "no project is open; call create_project or open_project first (list_projects shows existing projects)");
            }
        }
    }

    public FrameRenderer Renderer
    {
        get
        {
            lock (_gate)
            {
                var session = Session;
                return _renderer ??= new FrameRenderer(session.Directory, () => RequireFfmpeg().Ffmpeg);
            }
        }
    }

    // The open project's session; null when none is open.
    public ProjectSession? CurrentSession
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    public bool HasSession
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public IAssetProbe AssetProbe => new AssetProbe(path => Ffprobe.AudioDuration(RequireFfmpeg().Ffprobe, path));

    public void Open(ProjectSession session)
    {
        lock (_gate)
        {
            _renderer?.Dispose();
            _renderer = null;
            _session = session;
        }
    }

    public FfmpegPaths RequireFfmpeg()
    {
        lock (_gate)
        {
            return _ffmpeg ??= FfmpegLocator.Locate(Settings.FfmpegPath);
        }
    }

    // Null with the reason when whisper-cli or its model can't be found (transcripts are then skipped).
    public WhisperPaths? FindWhisper(out string? problem)
    {
        WhisperLocator.TryLocate(Settings.WhisperPath, Settings.WhisperModel, out var paths, out problem);
        return paths;
    }

    // Takes over the jobs earlier servers left in the jobs folder: an export (export_N, batch_N, versions_N)
    // that was running starts again, other running jobs are marked failed, and finished ones can
    // be looked up by id. Returns a line for the log about each job started again or failed.
    public IReadOnlyList<string> RecoverJobs(ILoggerFactory loggers)
    {
        if (JobRecords is null)
        {
            return [];
        }

        var jobsLog = loggers.CreateLogger("ZeyoCut.Jobs");
        var exportLog = loggers.CreateLogger("ZeyoCut.Export");
        JobRecords.Log = line => jobsLog.LogWarning("{Line}", line);
        var records = JobRecords.Unowned();
        var notes = new List<string>();
        notes.AddRange(Exports.Recover(records.Where(r => r.Kind == ExportJobManager.Kind), RequireFfmpeg, line => exportLog.LogInformation("{Line}", line)));
        notes.AddRange(Jobs.Recover(
            records.Where(r => r.Kind != ExportJobManager.Kind),
            new Dictionary<string, Func<JsonObject, Func<BackgroundJob, CancellationToken, string>>>(StringComparer.Ordinal)
            {
                ["batch"] = resume => ExportTools.ResumeBatch(this, exportLog, resume),
                ["versions"] = resume => ExportTools.ResumeVersions(this, exportLog, resume),
            }));
        return notes;
    }

    public string ResolveProjectDirectory(string nameOrPath)
    {
        var text = nameOrPath.Trim().Trim('"');
        if (Path.IsPathRooted(text) || text.Contains('\\') || text.Contains('/'))
        {
            return AllowedFolders.Project(Settings, text);
        }

        return AllowedFolders.Project(Settings, Path.Combine(Settings.ProjectsRoot, text));
    }

    public void Dispose()
    {
        PreviewPage.Dispose();
        lock (_gate)
        {
            _renderer?.Dispose();
            _renderer = null;
        }
    }
}
