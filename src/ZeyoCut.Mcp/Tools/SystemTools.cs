// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using ZeyoCut.Core;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;
using static ZeyoCut.Mcp.Tools.ToolSupport;

namespace ZeyoCut.Mcp.Tools;

[McpServerToolType]
public static class SystemTools
{
    [McpServerTool(Name = "system_check")]
    [Description("Report this computer: whether FFmpeg (needed for video and every export) and whisper (for transcripts) are installed, with how to install them; the folders ZeyoCut uses; and for the AI features the CPU, RAM, GPU, the ONNX Runtime version, which AI models are downloaded and the device each one runs on (DirectML on the GPU, or the CPU). Then it benchmarks every downloaded model on the CPU and on DirectML, and 1080p decoding, in the background (syscheck_N, usually under a minute): the result gives each AI feature's time per minute of 1080p video (per image for aiImage). The results are saved (library\\benchmark.json): AI jobs estimate their time from them, and AiDevice=auto (the default) runs a model on DirectML only where it worked and took at most 80% of the CPU's time. DirectML is tried in a separate process, stopped after 60 s, so a GPU driver that hangs can't hang the server. Run it again after installing models or changing hardware.")]
    public static string SystemCheck(EditorState state) => Guard(() =>
    {
        var hardware = SystemInfo.Hardware();
        var saved = state.Benchmark();
        var lines = SystemInfo.Describe(hardware);
        lines.Insert(0, FfmpegLocator.TryLocate(state.Settings.FfmpegPath, out var installed, out var missing)
            ? $"FFmpeg: {installed!.Ffmpeg}"
            : $"FFmpeg: not installed, so importing video, drawing video layers and every export fail until it is. {missing}. After installing it or changing the setting, restart the MCP client so it picks up the change");
        lines.Insert(1, WhisperLocator.TryLocate(state.Settings.WhisperPath, state.Settings.WhisperModel, out var whisper, out var noWhisper)
            ? $"whisper: {whisper!.Cli} with {Path.GetFileName(whisper.Model)}, so analyze_video makes transcripts"
            : $"whisper (optional): {noWhisper}; without it analyze_video finds scene cuts and silences but makes no transcript");
        lines.Insert(2, $"folders: projects in {state.Settings.ProjectsRoot}; library {state.Library.Root}; files are imported from there and from {string.Join(", ", state.Settings.ImportFolderList)} (the ImportFolders setting)");
        lines.Add(state.AiDevice switch
        {
            AiDevice.Cpu => "AiDevice=cpu: every model runs on the CPU",
            AiDevice.Gpu => "AiDevice=gpu: every model runs on DirectML, or on the CPU when DirectML can't start",
            _ => $"AiDevice=auto: a model runs on DirectML only where the benchmark found it works and takes at most {Percent(AiBenchmark.FasterShare)} of the CPU's time",
        });

        var models = new List<(AiFeature Feature, string ModelPath)>();
        foreach (var feature in AiFeatures.All)
        {
            var model = feature.Model;
            var about = $"{feature.Name}: {model.Name} ({model.FileName}, {model.Size}, {model.License})";
            if (!state.Models.Has(model))
            {
                lines.Add($"{about}: not downloaded; it downloads the first time it is used");
                continue;
            }

            models.Add((feature, state.Models.PathOf(model)));
            var choice = AiBenchmark.Pick(state.AiDevice, feature, saved);
            lines.Add($"{about}: runs on {(choice.Device == AiDevice.Gpu ? "DirectML" : "the CPU")} ({choice.Why})");
        }

        if (saved is not null)
        {
            lines.Add($"last benchmark: {saved.CheckedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
        }

        FfmpegLocator.TryLocate(state.Settings.FfmpegPath, out var ffmpeg, out _);
        var (executable, prefix) = SelfCommand();
        var runner = new AiBenchmarkRunner(ffmpeg?.Ffmpeg, executable, prefix);
        var what = models.Count == 0 ? "1080p decoding (no AI model is downloaded yet)" : $"{models.Count} model{(models.Count == 1 ? "" : "s")} and 1080p decoding";
        var machine = hardware.Machine;
        var job = state.Jobs.Start("syscheck", $"system_check benchmark of {what}", (job, ct) =>
        {
            if (!MatteTools.ComputeGate.Wait(0, ct))
            {
                job.Report(0, "waiting for an AI job to finish");
                MatteTools.ComputeGate.Wait(ct);
            }

            try
            {
                var results = AiBenchmark.Run(models, runner, machine, (progress, stage) => job.Report(progress, stage), ct);
                AiBenchmark.Save(state.BenchmarkPath, results);
                var found = AiBenchmark.Describe(results, state.AiDevice, state.BenchmarkPath);
                if (models.Count == 0)
                {
                    found.Add("no AI model is downloaded yet; each downloads the first time it is used, so run system_check again after that to measure it");
                }

                return $"benchmark at {AiBenchmark.Width}x{AiBenchmark.Height}, {AiBenchmark.Fps} fps:\n" + string.Join("\n", found.Select(l => "- " + l));
            }
            finally
            {
                MatteTools.ComputeGate.Release();
            }
        });
        lines.Add($"benchmarking {what} in the background as {job.Id} (DirectML in a separate process, stopped after {AiBenchmarkRunner.GpuTimeout.TotalSeconds:0} s per model); poll get_job_status with jobId \"{job.Id}\"");
        return "system check\n" + string.Join("\n", lines.Select(l => "- " + l));
    });

    [McpServerTool(Name = "get_logs")]
    [Description("Read the server's log, oldest first: the last lastN entries of the log files (zeyocut-yyyyMMdd.jsonl in the log folder, one JSON object per line). Every tool call is logged with its arguments and how long it took, a failed call with its error, and the server logs its start, its settings, jobs started again after a restart, and problems. Use it to find out what happened before, or why something failed.")]
    public static string GetLogs(
        EditorState state,
        [Description("How many entries to return, 1-1000. Default 50.")] int lastN = 50,
        [Description("The lowest level to include: trace, debug, information, warning, error or critical. Default: every level.")] string? level = null,
        [Description("Only entries that contain this text, in any case (in the message, the category, a value or the exception).")] string? contains = null) => Guard(() =>
    {
        if (lastN is < 1 or > MaxLogEntries)
        {
            throw new ZeyoCutException($"lastN must be between 1 and {MaxLogEntries} (got {lastN})");
        }

        var minimum = 0;
        if (!string.IsNullOrWhiteSpace(level))
        {
            minimum = Array.FindIndex(LogFiles.Levels, l => l.Equals(level.Trim(), StringComparison.OrdinalIgnoreCase));
            if (minimum < 0)
            {
                throw new ZeyoCutException($"unknown level '{level}'; use {string.Join(", ", LogFiles.Levels)}");
            }
        }

        var directory = state.Settings.LogDirectory;
        var entries = LogFiles.Last(directory, lastN, minimum, contains);
        var which = (minimum > 0 ? $" at {LogFiles.Levels[minimum]} or above" : "") + (string.IsNullOrEmpty(contains) ? "" : $" containing \"{contains}\"");
        if (entries.Count == 0)
        {
            return $"no log entries{which} in {directory}";
        }

        var text = new StringBuilder();
        text.Append(entries.Count == lastN ? (lastN == 1 ? "the last log entry" : $"the last {lastN} log entries") : $"all {entries.Count} log entr{(entries.Count == 1 ? "y" : "ies")}")
            .Append(which).Append(" in ").Append(directory).Append(", oldest first:\n");
        foreach (var entry in entries)
        {
            text.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(" [").Append(entry.Level).Append("] ")
                .Append(entry.Category).Append(": ").Append(Indent(entry.Message)).Append('\n');
            if (entry.Exception is { } exception)
            {
                var lines = exception.ReplaceLineEndings("\n").Split('\n');
                text.Append("  ").Append(string.Join("\n  ", lines.Take(ExceptionLines)));
                if (lines.Length > ExceptionLines)
                {
                    text.Append($"\n  … {lines.Length - ExceptionLines} more lines");
                }

                text.Append('\n');
            }
        }

        return text.ToString().TrimEnd('\n');
    });

    private const int MaxLogEntries = 1000;
    private const int ExceptionLines = 6;

    private static string Indent(string message) => message.ReplaceLineEndings("\n").Replace("\n", "\n  ");

    // What an AI job would compute and how long it should take.
    internal sealed record JobEstimate(AiEstimate Estimate, string What);

    // The reply instead of starting AI jobs that would take more than 5 minutes, unless confirmed:
    // nothing is changed, so the assistant can tell the user first.
    internal static string? NotStarted(IReadOnlyList<JobEstimate> estimates, bool confirmLongJob, string tool)
    {
        if (confirmLongJob || estimates.Count == 0)
        {
            return null;
        }

        var total = estimates.Select(e => e.Estimate).Aggregate((a, b) => a.Plus(b));
        if (!total.IsLong)
        {
            return null;
        }

        return $"not started, and nothing was changed: {string.Join("; ", estimates.Select(e => e.What))} would take {total.Describe()}, more than {AiEstimate.LongSeconds / 60:0} minutes. "
            + $"Tell the user how long it takes first; to go ahead, call {tool} again with the same arguments and confirmLongJob=true.";
    }

    // How to start a copy of this server (for the DirectML benchmark): the exe, or dotnet with the dll.
    private static (string? Executable, IReadOnlyList<string> Prefix) SelfCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return (null, []);
        }

        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = typeof(SystemTools).Assembly.Location;
            return string.IsNullOrEmpty(assembly) ? (null, []) : (path, [assembly]);
        }

        return (path, []);
    }

    private static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
