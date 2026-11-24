// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Render;

// track_object and reanchor_track's tracking, which the editor runs too: the runs of a TrackPlan
// tracked with ObjectTracker, the boxes saved as a new file of the track (TrackFiles), ready for
// SaveTrackOperation to put the track in the project.
public static class TrackRunner
{
    // The file tracking reads: the footage's proxy when there is one, else the footage itself.
    public static string SourceFile(string projectDirectory, Asset asset)
    {
        var file = Proxies.Ready(projectDirectory, asset) ?? ProjectStore.AssetPath(projectDirectory, asset);
        if (!File.Exists(file))
        {
            throw new ZeyoCutException($"the file of asset '{asset.Id}' is missing ({file})");
        }

        return file;
    }

    // Tracks the plan's runs in 'file' (SourceFile) and saves the boxes: over the frames of 'old', the
    // version a correction replaces, inside the track's first and last frame. Sets the plan's track's
    // File and returns the boxes saved. Progress goes from 0 to 1.
    public static TrackData Run(string ffmpeg, string projectDirectory, string file, TrackPlan plan, TrackData? old, Action<double>? progress, CancellationToken cancellationToken)
    {
        var track = plan.Track;
        var asset = plan.Asset;
        var anchor = plan.Anchor;
        var frames = old?.Frames.ToDictionary(f => f.Frame) ?? [];
        var total = Math.Max(1, plan.Runs.Sum(r => r.Frames));
        var done = 0;
        foreach (var run in plan.Runs)
        {
            var offset = done;
            var from = new TrackBox { Frame = run.From, X = anchor.X, Y = anchor.Y, Width = anchor.Width, Height = anchor.Height };
            foreach (var frame in ObjectTracker.Track(ffmpeg, file, asset, from, track.Method, run.To, p => progress?.Invoke((offset + p * run.Frames) / total), cancellationToken))
            {
                frames[frame.Frame] = frame;
            }

            done += run.Frames;
        }

        var data = new TrackData
        {
            Track = track.Id,
            Asset = asset.Id,
            Width = asset.Width ?? 0,
            Height = asset.Height ?? 0,
            Frames = frames.Values.Where(f => f.Frame >= track.FirstFrame && f.Frame <= track.LastFrame).OrderBy(f => f.Frame).ToList(),
        };
        track.File = TrackFiles.Save(projectDirectory, data);
        return data;
    }
}
