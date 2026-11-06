// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Mcp.Demo;
using ZeyoCut.Render;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Mcp;

public static class Cli
{
    public const string Usage = """
        ZeyoCut — MCP-first 2D video & animation editor

        Usage:
          ZeyoCut.Mcp.exe                                        run the MCP server on stdio
          ZeyoCut.Mcp.exe render-frame <projectDir> <time> <out.png> [--debug]
          ZeyoCut.Mcp.exe export <projectDir> <out.mp4> [--crf 18]
          ZeyoCut.Mcp.exe demo <outDir>                          build the demo project and export it
          ZeyoCut.Mcp.exe benchmark <feature> <model.onnx> <cpu|gpu>   time one AI model (system_check runs this)
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "render-frame" => RenderFrame(args, output, error),
                "export" => Export(args, output, error),
                "demo" => Demo(args, output, error),
                "benchmark" => Benchmark(args, output, error),
                "help" or "--help" or "-h" or "/?" => PrintUsage(output, 0),
                _ => PrintUsage(error, 2, $"unknown command '{args[0]}'"),
            };
        }
        catch (ZeyoCutException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RenderFrame(string[] args, TextWriter output, TextWriter error)
    {
        var positional = Positional(args);
        if (positional.Count != 3)
        {
            return PrintUsage(error, 2, "render-frame needs <projectDir> <time> <out.png>");
        }

        var directory = positional[0];
        var time = ParseDouble(positional[1], "time");
        var outPath = Path.GetFullPath(positional[2]);
        var debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);

        var project = ProjectStore.Load(directory);
        ProjectValidator.ThrowIfInvalid(project);
        using var renderer = new FrameRenderer(directory, () => FfmpegLocator.Locate(Settings.Load().FfmpegPath).Ffmpeg);
        var frame = FrameMath.NearestFrame(project, time);
        using var image = debug
            ? Preview.RenderDebugFrame(renderer, project, frame, project.Width, project.Height)
            : renderer.RenderImage(project, frame);
        Png.Save(image, outPath);

        output.WriteLine(
            $"frame {frame} (t={FrameMath.TimeOf(project, frame).ToString("0.###", CultureInfo.InvariantCulture)}s) -> {outPath} ({image.Width}x{image.Height})");
        foreach (var warning in renderer.TakeWarnings())
        {
            error.WriteLine($"warning: {warning}");
        }

        return 0;
    }

    private static int Export(string[] args, TextWriter output, TextWriter error)
    {
        var positional = Positional(args);
        if (positional.Count != 2)
        {
            return PrintUsage(error, 2, "export needs <projectDir> <out.mp4>");
        }

        var directory = positional[0];
        var outPath = Path.GetFullPath(positional[1]);
        var crfText = Option(args, "--crf");
        var crf = crfText is null ? 18 : (int)ParseDouble(crfText, "--crf");

        var project = ProjectStore.Load(directory);
        var ffmpeg = FfmpegLocator.Locate(Settings.Load().FfmpegPath);
        var result = Exporter.Export(project, directory, outPath, crf, ffmpeg, Progress(error));

        output.WriteLine(ExportSummary.Describe(result));
        foreach (var warning in result.Warnings)
        {
            error.WriteLine($"warning: {warning}");
        }

        return 0;
    }

    private static int Demo(string[] args, TextWriter output, TextWriter error)
    {
        var positional = Positional(args);
        if (positional.Count != 1)
        {
            return PrintUsage(error, 2, "demo needs <outDir> (an empty or new folder)");
        }

        var directory = Path.GetFullPath(positional[0]);
        var ffmpeg = FfmpegLocator.Locate(Settings.Load().FfmpegPath);
        var project = DemoBuilder.Build(directory);
        output.WriteLine($"demo project: {directory}");

        using (var renderer = new FrameRenderer(directory))
        {
            var last = FrameMath.TimeOf(project, FrameMath.FrameCount(project) - 1);
            var sheet = Preview.RenderContactSheet(renderer, project, 0, last, 12, 4, 1600, debug: false);
            output.WriteLine($"contact sheet: {sheet.SavedPath}");
            var rig = Preview.RenderFrame(renderer, project, DemoBuilder.ApexTime, 1280, debug: true);
            output.WriteLine($"debug frame: {rig.SavedPath}");
            foreach (var warning in sheet.Warnings.Concat(rig.Warnings).Distinct())
            {
                error.WriteLine($"warning: {warning}");
            }
        }

        var outPath = Path.Combine(directory, ProjectStore.ExportsFolder, DemoBuilder.ExportFileName);
        var result = Exporter.Export(project, directory, outPath, 18, ffmpeg, Progress(error));
        output.WriteLine(ExportSummary.Describe(result));
        foreach (var warning in result.Warnings)
        {
            error.WriteLine($"warning: {warning}");
        }

        return 0;
    }

    // One model on one device, as JSON on one line: system_check runs DirectML this way, in a process
    // of its own, so a driver that hangs only hangs this process.
    private static int Benchmark(string[] args, TextWriter output, TextWriter error)
    {
        var positional = Positional(args);
        if (positional.Count != 3 || positional[2].ToLowerInvariant() is not ("cpu" or "gpu"))
        {
            return PrintUsage(error, 2, "benchmark needs <feature> <model.onnx> <cpu|gpu>");
        }

        var feature = AiFeatures.Get(positional[0]);
        try
        {
            var measured = AiBenchmark.Measure(feature, Path.GetFullPath(positional[1]), AiSession.ParseDevice(positional[2]), CancellationToken.None);
            output.WriteLine(JsonSerializer.Serialize(measured, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }
        catch (Exception ex) when (ex is not ZeyoCutException)
        {
            error.WriteLine($"error: {ex.Message.Split('\n')[0].Trim()}");
            return 1;
        }
    }

    private static Action<int, int> Progress(TextWriter error)
    {
        var lastStep = -1;
        return (done, total) =>
        {
            var percent = done * 100 / total;
            if (percent / 10 != lastStep)
            {
                lastStep = percent / 10;
                error.WriteLine($"rendering {percent}% ({done}/{total} frames)");
            }
        };
    }

    private static List<string> Positional(string[] args)
    {
        var result = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[i] is "--crf" && i + 1 < args.Length)
                {
                    i++;
                }

                continue;
            }

            result.Add(args[i]);
        }

        return result;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double ParseDouble(string text, string what) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new ZeyoCutException($"{what} must be a number (got '{text}')");

    private static int PrintUsage(TextWriter writer, int exitCode, string? message = null)
    {
        if (message is not null)
        {
            writer.WriteLine($"error: {message}");
            writer.WriteLine();
        }

        writer.WriteLine(Usage);
        return exitCode;
    }
}
