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

    // Copies a file that needs no normalizing into the project and adds it, as one undo step.
    public static Asset AddAsIs(ProjectSession session, string label, string source, string? id, VideoProbeResult probe)
    {
        var info = probe.ToAssetInfo();
        return session.Mutate(label, p => AssetImporter.Import(p, session.Directory, source, id, new AssetProbe(video: _ => info), AssetType.Video));
    }

    // Adds a normalized file the project's assets folder already holds, noting the original and why it
    // was normalized, as one undo step. The file is deleted when it can't be added.
    public static Asset AddNormalized(ProjectSession session, string label, string output, string? id, VideoProbeResult normalized, string source, IReadOnlyList<string> reasons)
    {
        try
        {
            var info = normalized.ToAssetInfo();
            return session.Mutate(label, p =>
            {
                var added = AssetImporter.Import(p, session.Directory, output, id, new AssetProbe(video: _ => info), AssetType.Video);
                added.OriginalFile = source;
                added.Normalized = string.Join("; ", reasons);
                return added;
            });
        }
        catch
        {
            File.Delete(output);
            throw;
        }
    }
}
