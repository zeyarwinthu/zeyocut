// SPDX-License-Identifier: MIT

using System.Text.Json;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Mcp;

// DescriberProvider names the describe_clip provider; its API key is read from the environment
// variable DescriberKeyVariable names, never from the settings file. ModelsPath holds the AI models
// (downloaded on first use); AiDevice is auto (default: DirectML for a model only where system_check
// found it works and is faster), cpu or gpu (DirectML). JobsDirectory keeps a record of every
// background job, so jobs outlast a restart; null keeps them in memory only. LanguageFonts is the
// font a text in a language gets when it is given none (my: Myanmar Text and ja: Yu Gothic by default).
// VideoEncoder is auto (default: the first GPU encoder that works, else libx264), x264, nvenc, qsv or amf.
// ImportFolders are the folders tools may import from besides the projects folder and the library
// (default: Desktop, Documents, Downloads, Music, Pictures and Videos); see AllowedFolders.
public sealed record Settings(
    string? FfmpegPath,
    string ProjectsRoot,
    string LogDirectory,
    string? WhisperPath = null,
    string? WhisperModel = null,
    string? LibraryPath = null,
    string? DescriberProvider = null,
    string DescriberKeyVariable = Settings.DefaultDescriberKeyVariable,
    string? ModelsPath = null,
    string? AiDevice = null,
    string? JobsDirectory = null,
    IReadOnlyDictionary<string, string>? LanguageFonts = null,
    string? VideoEncoder = null,
    IReadOnlyList<string>? ImportFolders = null)
{
    public IReadOnlyDictionary<string, string> Fonts => LanguageFonts ?? Languages.DefaultFonts;

    public IReadOnlyList<string> ImportFolderList => ImportFolders is { Count: > 0 } folders ? folders : AllowedFolders.DefaultImportFolders();

    public const string ProjectsRootVariable = "ZEYOCUT_PROJECTS_DIR";
    public const string LogDirectoryVariable = "ZEYOCUT_LOG_DIR";
    public const string JobsDirectoryVariable = "ZEYOCUT_JOBS_DIR";
    public const string FfmpegPathVariable = "FFMPEG_PATH";
    public const string WhisperPathVariable = "WHISPER_PATH";
    public const string WhisperModelVariable = "WHISPER_MODEL";
    public const string LibraryPathVariable = "ZEYOCUT_LIBRARY_DIR";
    public const string DescriberProviderVariable = "ZEYOCUT_DESCRIBER_PROVIDER";
    public const string DefaultDescriberKeyVariable = "ZEYOCUT_DESCRIBER_API_KEY";
    public const string ModelsPathVariable = "ZEYOCUT_MODELS_DIR";
    public const string AiDeviceVariable = "ZEYOCUT_AI_DEVICE";
    public const string VideoEncoderVariable = "ZEYOCUT_VIDEO_ENCODER";
    public const string ImportFoldersVariable = "ZEYOCUT_IMPORT_DIRS";

    // Projects and the library share one folder in the user's profile: %USERPROFILE%\ZeyoCut.
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut");

    public static string DefaultProjectsRoot => Path.Combine(DefaultRoot, "projects");

    public static string DefaultLibraryPath => Path.Combine(DefaultRoot, "library");

    public static Settings Load()
    {
        string? ffmpeg = null;
        string? projects = null;
        string? logs = null;
        string? jobs = null;
        string? whisper = null;
        string? whisperModel = null;
        string? library = null;
        string? describer = null;
        string? describerKey = null;
        string? models = null;
        string? device = null;
        string? encoder = null;
        List<string>? imports = null;
        var fonts = new Dictionary<string, string>(Languages.DefaultFonts, StringComparer.Ordinal);

        var file = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(file))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (document.RootElement.TryGetProperty("ZeyoCut", out var section))
                {
                    ffmpeg = Read(section, "FfmpegPath");
                    projects = Read(section, "ProjectsRoot");
                    logs = Read(section, "LogDirectory");
                    jobs = Read(section, "JobsDirectory");
                    whisper = Read(section, "WhisperPath");
                    whisperModel = Read(section, "WhisperModel");
                    library = Read(section, "LibraryPath");
                    models = Read(section, "ModelsPath");
                    device = Read(section, "AiDevice");
                    encoder = Read(section, "VideoEncoder");
                    if (section.TryGetProperty("ImportFolders", out var importFolders))
                    {
                        imports = importFolders.ValueKind switch
                        {
                            JsonValueKind.Array => importFolders.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
                            JsonValueKind.String => [.. importFolders.GetString()!.Split(';')],
                            _ => null,
                        };
                    }
                    if (section.TryGetProperty("ClipDescriber", out var clips) && clips.ValueKind == JsonValueKind.Object)
                    {
                        describer = Read(clips, "Provider");
                        describerKey = Read(clips, "ApiKeyVariable");
                    }

                    if (section.TryGetProperty("LanguageFonts", out var languageFonts) && languageFonts.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in languageFonts.EnumerateObject())
                        {
                            if (!Languages.TryNormalize(entry.Name, out var language))
                            {
                                Console.Error.WriteLine($"warning: ignoring LanguageFonts entry '{entry.Name}' in {file}: it isn't a language code such as en, ja, my or pt-BR");
                            }
                            else if (entry.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.Value.GetString()))
                            {
                                fonts[language] = entry.Value.GetString()!.Trim();
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"warning: ignoring invalid {file}: {ex.Message}");
            }
        }

        ffmpeg = Env(FfmpegPathVariable) ?? ffmpeg;
        projects = Env(ProjectsRootVariable) ?? projects ?? DefaultProjectsRoot;
        logs = Env(LogDirectoryVariable) ?? logs
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeyoCut", "logs");
        jobs = Env(JobsDirectoryVariable) ?? jobs
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeyoCut", "jobs");

        whisper = Env(WhisperPathVariable) ?? whisper;
        whisperModel = Env(WhisperModelVariable) ?? whisperModel;
        library = Env(LibraryPathVariable) ?? library ?? DefaultLibraryPath;
        describer = Env(DescriberProviderVariable) ?? describer;
        library = Path.GetFullPath(Environment.ExpandEnvironmentVariables(library));
        models = Env(ModelsPathVariable) ?? models ?? Path.Combine(library, "models");
        device = Env(AiDeviceVariable) ?? device;
        encoder = Env(VideoEncoderVariable) ?? encoder;
        imports = Env(ImportFoldersVariable) is { } importVariable ? [.. importVariable.Split(';')] : imports;
        var importList = imports?.Select(f => f.Trim().Trim('"')).Where(f => f.Length > 0)
            .Select(f => Path.GetFullPath(Environment.ExpandEnvironmentVariables(f))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new Settings(ffmpeg, Path.GetFullPath(Environment.ExpandEnvironmentVariables(projects)), Path.GetFullPath(Environment.ExpandEnvironmentVariables(logs)), whisper, whisperModel,
            library, describer, describerKey?.Trim() ?? DefaultDescriberKeyVariable, Path.GetFullPath(Environment.ExpandEnvironmentVariables(models)), device,
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(jobs)), fonts, encoder, importList is { Count: > 0 } ? importList : null);
    }

    private static string? Read(JsonElement section, string name) =>
        section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }
}
