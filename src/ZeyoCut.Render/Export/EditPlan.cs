// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Render.Export;

// A clip on another editor's timeline: Start and Length in timeline seconds, From the media second it
// starts at, Speed how fast the media plays. Media is the file, MediaLength its length in seconds.
public sealed record PlannedClip(string LayerId, string Media, double MediaLength, double Start, double Length, double From, double Speed = 1);

public sealed record PlannedTrack(string Name, bool Audio, bool Prerendered, IReadOnlyList<PlannedClip> Clips);

// The project as tracks for an editor that reads OTIO or FCPXML: the clips (each video layer's
// picture, and the sound the export mixes, on tracks of their own), the current prerenders of
// prerender_layers on tracks above the clips, and the markers. Missing names what the timeline
// can't hold (keyframes, effects, the camera, layers only ZeyoCut draws, the mix), Stale the
// prerenders that no longer match their layers.
public sealed record EditPlan(Project Project, IReadOnlyList<PlannedTrack> Tracks, IReadOnlyList<Marker> Markers, IReadOnlyList<string> Missing, IReadOnlyList<string> Stale)
{
    public static EditPlan Make(Project project, string projectDirectory, string prerenderFolder)
    {
        var fps = project.Fps;
        var order = project.Layers.Select((layer, index) => (layer, index)).ToDictionary(p => p.layer.Id, p => (p.layer is VisualLayer visual ? visual.ZIndex : 0, p.index), StringComparer.Ordinal);
        var missing = new List<string>();
        var stale = new List<string>();

        var covered = new HashSet<string>(StringComparer.Ordinal);
        var prerendered = new List<(PlannedClip Clip, IReadOnlySet<string> Only)>();
        foreach (var done in Prerender.List(prerenderFolder))
        {
            if (project.FindLayer(done.LayerId) is null)
            {
                continue;
            }

            var file = Path.Combine(prerenderFolder, done.File);
            IReadOnlySet<string> only;
            int first;
            int count;
            try
            {
                var (root, ids) = Prerender.Choose(project, done.LayerId);
                (only, (first, count)) = (ids, Prerender.Span(project, root));
            }
            catch (ZeyoCut.Core.ZeyoCutException)
            {
                continue;
            }

            var current = File.Exists(file) && done.Width == project.Width && done.Height == project.Height && done.Fps == fps
                && done.FirstFrame == first && done.FrameCount == count && done.Key == Prerender.Key(project, projectDirectory, only, first, count);
            if (!current)
            {
                stale.Add(done.LayerId);
                continue;
            }

            covered.UnionWith(only);
            prerendered.Add((new PlannedClip(done.LayerId, file, count / (double)fps, first / (double)fps, count / (double)fps, 0), only));
        }

        // A layer inside another layer's prerender is in that one already.
        var overlays = prerendered
            .Where(p => !prerendered.Any(o => o.Clip.LayerId != p.Clip.LayerId && o.Only.Contains(p.Clip.LayerId)))
            .Select(p => (p.Clip, p.Clip.LayerId))
            .ToList();

        var pictures = new List<(PlannedClip Clip, string Order)>();
        foreach (var video in project.Layers.OfType<VideoLayer>().Where(v => v.Visible))
        {
            var (first, count) = Prerender.Span(project, video);
            if (count == 0 || project.FindAsset(video.Asset) is not { } asset)
            {
                continue;
            }

            var start = first / (double)fps;
            pictures.Add((new PlannedClip(video.Id, ProjectStore.AssetPath(projectDirectory, asset), asset.Duration ?? 0, start, count / (double)fps, VideoTiming.SourceTime(video, start), video.Speed), video.Id));
            if (!covered.Contains(video.Id))
            {
                missing.AddRange(VideoMissing(project, video, asset));
            }
        }

        var assets = project.Assets.ToDictionary(a => ProjectStore.AssetPath(projectDirectory, a), a => a, StringComparer.OrdinalIgnoreCase);
        var audio = FfmpegArgs.AudioClips(project, projectDirectory);
        var sounds = audio.Select(c => (new PlannedClip(c.LayerId, c.Path, assets.TryGetValue(c.Path, out var a) ? a.Duration ?? 0 : 0, c.Start, c.Length, c.TrimIn, c.Speed), c.LayerId)).ToList();

        var tracks = new List<PlannedTrack>();
        tracks.AddRange(Lanes(pictures, order, stacked: true).Select((clips, i) => new PlannedTrack($"V{i + 1}", false, false, clips)));
        tracks.AddRange(Lanes(overlays, order, stacked: true).Select((clips, i) => new PlannedTrack($"V{tracks.Count + i + 1}", false, true, clips)));
        tracks.AddRange(Lanes(sounds, order, stacked: false).Select((clips, i) => new PlannedTrack($"A{i + 1}", true, false, clips)));

        missing.AddRange(Drawn(project, covered));
        if (project.Layers.OfType<RedactionLayer>().Where(r => r.Visible).ToList() is { Count: > 0 } redactions)
        {
            var assetsHidden = project.Tracks.Where(t => redactions.Any(r => r.Tracks.Contains(t.Id))).Select(t => t.Asset).ToHashSet(StringComparer.Ordinal);
            var clips = project.Layers.OfType<VideoLayer>().Where(v => v.Visible && assetsHidden.Contains(v.Asset) && !covered.Contains(v.Id)).Select(v => $"'{v.Id}'").ToList();
            if (clips.Count > 0)
            {
                missing.Add($"the redactions ({string.Join(", ", redactions.Select(r => $"'{r.Id}'"))}) over {string.Join(", ", clips)}: prerender those clips to carry them");
            }
        }

        missing.AddRange(SoundMissing(project, audio));
        if (Languages.Used(project) is { Count: > 1 } languages)
        {
            missing.Add($"one language's version: the layers and sound of every language ({string.Join(", ", languages)}) are in it");
        }

        return new EditPlan(project, tracks, project.Markers.OrderBy(m => m.Time).ToList(), missing, stale);
    }

    // Clips on as few tracks as they fit. Stacked keeps the draw order: a clip goes above every clip
    // drawn before it that it overlaps. Sound takes the lowest track that is free.
    private static List<List<PlannedClip>> Lanes(List<(PlannedClip Clip, string Order)> clips, Dictionary<string, (int Z, int Index)> order, bool stacked)
    {
        var placed = new List<(PlannedClip Clip, int Lane)>();
        foreach (var (clip, _) in clips.OrderBy(c => order.TryGetValue(c.Order, out var o) ? o : (0, 0)))
        {
            var taken = placed.Where(p => p.Clip.Start < clip.Start + clip.Length - 1e-6 && clip.Start < p.Clip.Start + p.Clip.Length - 1e-6).Select(p => p.Lane).ToHashSet();
            var lane = stacked ? taken.Select(l => l + 1).DefaultIfEmpty(0).Max() : Enumerable.Range(0, taken.Count + 1).First(l => !taken.Contains(l));
            placed.Add((clip, lane));
        }

        return placed.GroupBy(p => p.Lane).OrderBy(g => g.Key).Select(g => g.Select(p => p.Clip).OrderBy(c => c.Start).ToList()).ToList();
    }

    // What a clip's picture has that a timeline's clip doesn't: it fills the frame there, as it is.
    private static IEnumerable<string> VideoMissing(Project project, VideoLayer video, Asset asset)
    {
        var keyframed = new (string Name, AnimatedValue Value)[]
        {
            ("x", video.X), ("y", video.Y), ("rotation", video.Rotation), ("scaleX", video.ScaleX), ("scaleY", video.ScaleY), ("opacity", video.Opacity),
            ("brightness", video.Brightness), ("contrast", video.Contrast), ("saturation", video.Saturation), ("cropX", video.CropX), ("cropY", video.CropY),
        }.Where(p => p.Value.Keyframes.Count > 0).Select(p => p.Name).ToList();
        if (keyframed.Count > 0)
        {
            yield return $"keyframes on '{video.Id}' ({string.Join(", ", keyframed)})";
        }

        var (width, height) = VideoTiming.CropSize(video, asset);
        var fit = width > 0 && height > 0 ? Math.Min(project.Width / width, project.Height / height) : 1;
        var moved = video.ParentId is not null || video.MotionPath is not null
            || !Near(video.X.Value, project.Width / 2.0) || !Near(video.Y.Value, project.Height / 2.0)
            || !Near(video.AnchorX, width / 2) || !Near(video.AnchorY, height / 2)
            || !Near(video.ScaleX.Value, fit) || !Near(video.ScaleY.Value, fit) || !Near(video.Rotation.Value, 0);
        if (moved && keyframed.Count == 0)
        {
            yield return $"the place and size of '{video.Id}' (another editor fits it to the frame)";
        }

        if (keyframed.Count == 0 && !Near(video.Opacity.Value, 1))
        {
            yield return $"the opacity of '{video.Id}' ({Percent(video.Opacity.Value)})";
        }

        var effects = new List<string>();
        if (video.CropWidth is not null || video.CropHeight is not null)
        {
            effects.Add("crop");
        }

        if (!Near(video.Brightness.Value, 0) || !Near(video.Contrast.Value, 1) || !Near(video.Saturation.Value, 1))
        {
            effects.Add("color");
        }

        effects.AddRange(new (string Name, object? Effect)[] { ("shadow", video.Shadow), ("outline", video.Outline), ("blur", video.Blur), ("tint", video.Tint), ("mask", video.Mask), ("background removal", video.Matte) }
            .Where(e => e.Effect is not null).Select(e => e.Name));
        if (effects.Count > 0)
        {
            yield return $"the {string.Join(", ", effects)} of '{video.Id}'";
        }

        if (!video.IgnoreCamera && project.Camera is { } camera && Moves(project, camera))
        {
            yield return $"the camera's move over '{video.Id}'";
        }
    }

    // The layers only ZeyoCut draws (text, images, cutouts), by the top layer of each group.
    private static IEnumerable<string> Drawn(Project project, HashSet<string> covered)
    {
        var drawn = project.Layers
            .Where(l => l is TextLayer || l is CutoutLayer || l is ImageLayer image && (image.Drawings.Count > 0 || project.Layers.Any(c => c.ParentId == image.Id)))
            .Where(l => l.Visible && !covered.Contains(l.Id))
            .ToList();
        var ids = drawn.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var root in drawn.Where(l => l.ParentId is null || !ids.Contains(l.ParentId)))
        {
            var inside = ProjectEditor.Subtree(project, root.Id).Count(l => !ReferenceEquals(l, root) && ids.Contains(l.Id));
            var kind = root switch
            {
                TextLayer => "text ",
                CutoutLayer => "subject cutout ",
                ImageLayer { Drawings.Count: > 0 } => "image ",
                _ => "",
            };
            names.Add($"{kind}'{root.Id}'{(inside > 0 ? $" and its {inside} layer(s)" : "")}");
        }

        if (names.Count > 0)
        {
            yield return $"layers only ZeyoCut draws: {string.Join(", ", names)}; prerender_layers draws them into video with alpha for the timeline";
        }
    }

    private static IEnumerable<string> SoundMissing(Project project, List<AudioClip> audio)
    {
        var levels = audio.Where(c => !Near(c.Volume, 1)).Select(c => $"'{c.LayerId}' at {Percent(c.Volume)}").ToList();
        if (levels.Count > 0)
        {
            yield return $"the volume of {string.Join(", ", levels)}";
        }

        var fades = audio.Where(c => c.FadeIn > 0 || c.FadeOut > 0).Select(c => $"'{c.LayerId}'").ToList();
        if (fades.Count > 0)
        {
            yield return $"the sound fades of {string.Join(", ", fades)}";
        }

        var settings = project.Audio;
        if (FfmpegArgs.Ducks(audio, new AudioMix(settings.Ducking, settings.LoudnessTarget, settings.TruePeak, null)))
        {
            yield return "the music ducking under the voice";
        }

        if (audio.Count > 0 && settings.LoudnessTarget is { } target)
        {
            yield return $"the loudness normalization to {target.ToString("0.#", CultureInfo.InvariantCulture)} LUFS";
        }
    }

    // The camera at rest looks at the frame's center, unzoomed and level.
    private static bool Moves(Project project, Camera camera) =>
        new[] { camera.X, camera.Y, camera.Zoom, camera.Rotation, camera.ShakeX, camera.ShakeY, camera.ShakeRotation }.Any(v => v.Keyframes.Count > 0)
        || !Near(camera.X.Value, project.Width / 2.0) || !Near(camera.Y.Value, project.Height / 2.0) || !Near(camera.Zoom.Value, 1) || !Near(camera.Rotation.Value, 0)
        || !Near(camera.ShakeX.Value, 0) || !Near(camera.ShakeY.Value, 0) || !Near(camera.ShakeRotation.Value, 0);

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static string Percent(double value) => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
