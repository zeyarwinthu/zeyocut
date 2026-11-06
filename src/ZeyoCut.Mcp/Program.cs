// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ZeyoCut.Mcp;
using ZeyoCut.Render.Export;

if (args.Length > 0)
{
    return Cli.Run(args, Console.Out, Console.Error);
}

Console.SetOut(Console.Error);

var settings = Settings.Load();
var fileLogs = new FileLoggerProvider(settings.LogDirectory);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddProvider(fileLogs);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<EditorState>();
var mcp = builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = ServerInfo.Name, Version = ServerInfo.Version };
        o.ServerInstructions = ServerInfo.Instructions;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly()
    .WithRequestFilters(filters => filters.AddCallToolFilter(ToolCallLog.Filter));
var describer = ClipDescribers.Create(settings, out var describerStatus);
if (describer is not null)
{
    mcp.WithClipDescriber(describer);
}

using var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ZeyoCut");
log.LogInformation("ZeyoCut MCP server {Version} starting; projects root {Root}; log file {Log}", ServerInfo.Version, settings.ProjectsRoot, fileLogs.Path);
if (FfmpegLocator.TryLocate(settings.FfmpegPath, out var ffmpeg, out var problem) && ffmpeg is not null)
{
    log.LogInformation("ffmpeg: {Ffmpeg}; ffprobe: {Ffprobe}", ffmpeg.Ffmpeg, ffmpeg.Ffprobe);
}
else
{
    log.LogWarning("{Problem}", problem);
}

if (describer is null && !string.IsNullOrWhiteSpace(settings.DescriberProvider))
{
    log.LogWarning("{Status}", describerStatus);
}
else
{
    log.LogInformation("{Status}", describerStatus);
}

log.LogInformation("jobs folder {Jobs}", settings.JobsDirectory);
foreach (var note in host.Services.GetRequiredService<EditorState>().RecoverJobs(host.Services.GetRequiredService<ILoggerFactory>()))
{
    log.LogInformation("{Note}", note);
}

await host.RunAsync();
return 0;
