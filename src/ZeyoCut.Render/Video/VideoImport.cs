// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Render.Video;

// The steps of importing a video file, shared by the server's import_video and import_folder and the
// editor: probe it, then add it as it is, or normalize it (VideoNormalizer) and add the result.
public static class VideoImport
{
    // Probes the file and says why it needs normalizing; with no reasons it can be added as it is.
    public static (VideoProbeResult Probe, IReadOnlyList<string> Reasons) Check(FfmpegPaths ffmpeg, string source)
    {
        var probe = VideoProbe.Probe(ffmpeg.Ffprobe, source);
        return (probe, probe.NormalizeReasons());
    }

    // Copies a file that needs no normalizing into the project's assets folder (AssetImporter.Prepare).
    public static PreparedAsset Prepare(string projectDirectory, string source, VideoProbeResult probe)
    {
        var info = probe.ToAssetInfo();
        return AssetImporter.Prepare(projectDirectory, source, new AssetProbe(video: _ => info), AssetType.Video);
    }

    // A normalized file the project's assets folder already holds, ready to be added.
    public static PreparedAsset PrepareNormalized(string projectDirectory, string output, VideoProbeResult normalized)
    {
        var info = normalized.ToAssetInfo();
        return AssetImporter.Prepare(projectDirectory, output, new AssetProbe(video: _ => info), AssetType.Video);
    }

    // Probes images and fonts, audio with ffprobe, and a video that needs no normalizing; a video
    // that does is refused. For what takes a file as it is: relinking.
    public static IAssetProbe AsIsProbe(Func<FfmpegPaths> ffmpeg) => new AssetProbe(
        path => Ffprobe.AudioDuration(ffmpeg().Ffprobe, path),
        path =>
        {
            var (probe, reasons) = Check(ffmpeg(), path);
            return reasons.Count == 0
                ? probe.ToAssetInfo()
                : throw new ZeyoCutException($"'{Path.GetFileName(path)}' needs normalizing ({Reasons(reasons)}), and relinking uses a file as it is; import it instead, then use the new asset in the clips");
        });

    // What the asset notes about why it was normalized.
    public static string Reasons(IReadOnlyList<string> reasons) => string.Join("; ", reasons);

    // Copies a file that needs no normalizing into the project, then adds it as one undo step.
    public static Asset AddAsIs(ProjectSession session, string label, string source, string? id, VideoProbeResult probe)
    {
        var prepared = Prepare(session.Directory, source, probe);
        return session.Mutate(label, p => AssetImporter.Add(p, prepared, id));
    }

    // Adds a normalized file the project's assets folder already holds, noting the original and why it
    // was normalized, as one undo step. The file is deleted when it can't be added.
    public static Asset AddNormalized(ProjectSession session, string label, string output, string? id, VideoProbeResult normalized, string source, IReadOnlyList<string> reasons)
    {
        try
        {
            var prepared = PrepareNormalized(session.Directory, output, normalized);
            return session.Mutate(label, p => AssetImporter.Add(p, prepared, id, source, Reasons(reasons)));
        }
        catch
        {
            File.Delete(output);
            throw;
        }
    }
}
