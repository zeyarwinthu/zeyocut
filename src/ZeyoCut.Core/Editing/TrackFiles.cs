// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// Where track boxes live: <project>/tracks/<id>-<hash>.json, one file per version of a track. A
// change writes a new file and leaves the old one, so undo and redo find theirs.
public static class TrackFiles
{
    public const string Folder = "tracks";

    private static readonly JsonSerializerOptions Compact = new(ProjectJson.Options) { WriteIndented = false };

    public static string PathOf(string projectDirectory, Track track) =>
        ProjectStore.IsFileIn(Folder, track.File)
            ? Path.GetFullPath(Path.Combine(projectDirectory, track.File.Replace('/', Path.DirectorySeparatorChar)))
            : throw new ZeyoCutException($"track '{track.Id}': file '{track.File}' must be a file in the {Folder} folder");

    // Writes the boxes (rounded to 0.01 px) and returns the project-relative file name.
    public static string Save(string projectDirectory, TrackData data)
    {
        foreach (var frame in data.Frames)
        {
            frame.X = Math.Round(frame.X, 2);
            frame.Y = Math.Round(frame.Y, 2);
            frame.Width = Math.Round(frame.Width, 2);
            frame.Height = Math.Round(frame.Height, 2);
            frame.Confidence = Math.Round(frame.Confidence, 3);
        }

        var json = JsonSerializer.Serialize(data, Compact);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8];
        var name = $"{data.Track}-{hash}.json";
        var path = Path.Combine(projectDirectory, Folder, name);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }

        return $"{Folder}/{name}";
    }

    public static TrackData Load(string projectDirectory, Track track)
    {
        var path = PathOf(projectDirectory, track);
        if (!File.Exists(path))
        {
            throw new ZeyoCutException($"track '{track.Id}': its file {track.File} is missing; run track_object again");
        }

        try
        {
            return JsonSerializer.Deserialize<TrackData>(File.ReadAllText(path), ProjectJson.Options)
                ?? throw new ZeyoCutException($"track '{track.Id}': {track.File} is empty; run track_object again");
        }
        catch (JsonException ex)
        {
            throw new ZeyoCutException($"track '{track.Id}': {track.File} is not valid ({ex.Message}); run track_object again");
        }
    }
}
