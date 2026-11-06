// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Render.Export;

// The mix as an export has it (fades, ducking, loudness), as an AAC file for the preview page to play.
public static class PreviewAudio
{
    public const string Folder = "preview";

    // A hash of everything the mix depends on: the clips, their files (stamped with size and time),
    // the audio settings and the length. Null when nothing is heard.
    public static string? Key(Project project, string projectDirectory)
    {
        var audio = FfmpegArgs.AudioClips(project, projectDirectory);
        if (audio.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder($"{FrameMath.FrameCount(project)}/{project.Fps}|{JsonSerializer.Serialize(project.Audio, ProjectJson.Options)}");
        foreach (var clip in audio)
        {
            var file = new FileInfo(clip.Path);
            builder.Append('\n').Append(JsonSerializer.Serialize(clip)).Append('|')
                .Append(file.Exists ? $"{file.Length}|{file.LastWriteTimeUtc.Ticks}" : "missing");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16].ToLowerInvariant();
    }

    // cache\preview\mix-<key>.m4a in the project.
    public static string PathOf(string projectDirectory, string key) =>
        Path.Combine(projectDirectory, ChunkedExport.CacheFolder, Folder, $"mix-{key}.m4a");

    // Writes the mix to output (through a .partial.m4a file) and returns what the export does to it.
    public static IReadOnlyList<string> Make(Project project, string projectDirectory, FfmpegPaths ffmpeg, string output, CancellationToken cancellationToken)
    {
        var audio = FfmpegArgs.AudioClips(project, projectDirectory);
        foreach (var clip in audio.Where(c => !File.Exists(c.Path)))
        {
            throw new ZeyoCutException($"layer '{clip.LayerId}': file is missing: {clip.Path}");
        }

        var duration = FrameMath.RenderedDuration(project);
        var notes = new List<string>();
        var mix = Exporter.PlanAudio(project, audio, duration, ffmpeg, notes, null, cancellationToken)
            ?? throw new ZeyoCutException("nothing in the project is heard");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var partial = Path.ChangeExtension(output, ".partial.m4a");
        try
        {
            var result = ProcessRunner.RunLines(ffmpeg.Ffmpeg, FfmpegArgs.PreviewMixArguments(audio, mix, duration, partial), null, null, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new ZeyoCutException($"ffmpeg could not mix the sound (exit code {result.ExitCode}):\n{result.StandardError}");
            }

            File.Move(partial, output, overwrite: true);
            return notes;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}
