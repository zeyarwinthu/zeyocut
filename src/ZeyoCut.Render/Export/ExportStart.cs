// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// An export that was started: its job, the file it writes and the language version it is.
public sealed record ExportStarted(ExportJob Job, string Output, LanguageVersion Version);

// Starting an export the way start_export does, for the server and the editor alike: the file name,
// the language version, the preset check, and the video encoder (the VideoEncoder setting, or else
// the GPU encoders tested once on this computer, kept in the library's encoders.json).
public sealed class ExportStart
{
    private readonly object _gate = new();
    private readonly Lazy<string> _machine = new(() => SystemInfo.Hardware().Machine);
    private readonly Func<FfmpegPaths> _ffmpeg;

    public ExportStart(VideoEncoder? videoEncoder, string libraryPath, Func<FfmpegPaths> ffmpeg)
    {
        VideoEncoder = videoEncoder;
        EncoderTestsPath = Path.Combine(libraryPath, VideoEncoders.FileName);
        _ffmpeg = ffmpeg;
    }

    // The VideoEncoder setting, or null for auto.
    public VideoEncoder? VideoEncoder { get; }

    // What the encoder test encodes found (<library>\encoders.json).
    public string EncoderTestsPath { get; }

    public string Machine => _machine.Value;

    // The encoder for an export at this CRF; the first export on a computer tests the GPU encoders.
    public EncoderChoice ChooseEncoder(int crf)
    {
        var ffmpeg = _ffmpeg();
        lock (_gate)
        {
            return VideoEncoders.Choose(VideoEncoder, crf, EncoderTestsPath, Machine, VideoEncoders.Stamp(ffmpeg), encoder => VideoEncoders.Try(ffmpeg, encoder));
        }
    }

    // A plain file name like "jump.mp4", without folders; .mp4 is added when it has no extension.
    public static string FileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ZeyoCutException($"fileName '{fileName}' must be a plain file name like \"jump.mp4\" without folders");
        }

        var name = fileName.Trim();
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            return name + ".mp4";
        }

        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? name
            : throw new ZeyoCutException($"only .mp4 export is supported (got '{extension}')");
    }

    // Starts exporting the project (a snapshot nothing else changes) to exports\<fileName> in its
    // folder, at crf (by default the preset's, else 18), as one language's version when language is
    // given. Later edits don't affect it.
    public ExportStarted Start(ExportJobManager exports, Project project, string directory, string fileName, int? crf, Preset? preset, string? language, Action<string>? log)
    {
        var name = FileName(fileName);
        var ffmpeg = _ffmpeg();
        var version = Languages.Choose(project, language, export: true);
        var output = Path.Combine(directory, ProjectStore.ExportsFolder, name);
        Exporter.CheckPreset(project, preset);
        var job = exports.Start(project, directory, output, crf ?? preset?.Crf ?? 18, ffmpeg, log, preset);
        return new ExportStarted(job, output, version);
    }
}
