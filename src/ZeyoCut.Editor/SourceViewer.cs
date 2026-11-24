// SPDX-License-Identifier: MIT

using System.Globalization;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// Where the source viewer puts a clip: a lane of the timeline (a draw level for footage, a role for
// sound), or a draw level of its own on top (ZIndex and Role null).
public sealed record SourceTarget(string Key, string Title, int? ZIndex, AudioRole? Role);

// The part of the file the clip will be: In and Out are the clip's trim in and out, in the file's
// seconds; InFrame and OutFrame are the frames marked, when they are, OutFrame being the clip's last.
public sealed record SourceRange(double In, double Out, int? InFrame, int? OutFrame)
{
    public double Length => Out - In;
}

// The source viewer: a video or a sound on its own, drawn and heard as the timeline would draw and
// play it alone, with in and out marks that become the clip's trim in and out, inserted or
// overwritten at the timeline's playhead.
public static class SourceViewer
{
    public const string OnTop = "top";

    // The file alone: a project the footage's size and frame rate, as long as the file, holding one
    // clip of all of it, with its sound as it is (no loudness target, no ducking). A sound gets the
    // project's frame rate, and a small canvas with nothing on it.
    public static Project Of(Project project, Asset asset)
    {
        var video = asset.Type == AssetType.Video;
        var fps = video ? Math.Clamp((int)Math.Round(VideoTiming.Fps(asset)), 1, 120) : Math.Max(1, project.Fps);
        var source = new Project
        {
            Name = asset.Id,
            Width = video && asset.Width is { } width ? width : 320,
            Height = video && asset.Height is { } height ? height : 180,
            Fps = fps,
            Duration = Math.Max(1.0 / fps, asset.Duration ?? 0),
            BackgroundColor = "#000000",
            Audio = new AudioSettings { LoudnessTarget = null, Ducking = false },
        };
        source.Assets.Add(asset);
        if (video)
        {
            ProjectEditor.AddVideoLayer(source, new VideoLayerOptions { Asset = asset.Id, Id = "source" });
        }
        else
        {
            ProjectEditor.AddAudioLayer(source, new AudioLayerOptions { Asset = asset.Id, Id = "source" });
        }

        return source;
    }

    // The range the marks give: from the in frame's start (or the file's) to the out frame's end (or
    // the file's).
    public static SourceRange Range(Project source, int? inFrame, int? outFrame) => new(
        inFrame is { } first ? FrameMath.TimeOf(source, first) : 0,
        outFrame is { } last ? Math.Min(source.Duration, FrameMath.TimeOf(source, last + 1)) : source.Duration,
        inFrame,
        outFrame);

    // Marking in at a frame; an out mark before it goes.
    public static (int? In, int? Out) MarkIn(int frame, int? outFrame) => (frame, outFrame is { } last && last < frame ? null : outFrame);

    // Marking out at a frame, the clip's last; an in mark after it goes.
    public static (int? In, int? Out) MarkOut(int frame, int? inFrame) => (inFrame is { } first && first > frame ? null : inFrame, frame);

    // The lanes a clip of the file can go on: footage and pictures on any draw level the timeline
    // shows, or on one of its own on top; a sound in any role.
    public static IReadOnlyList<SourceTarget> Targets(Project project, Asset asset) => asset.Type == AssetType.Audio
        ? [Role(AudioRole.Voice, "Voice"), Role(AudioRole.Music, "Music"), Role(AudioRole.Effects, "Sound effects")]
        : [.. TimelineLayout.Lanes(project).Where(l => l.Kind == LaneKind.Visual).Select(l => new SourceTarget(l.Key, l.Title, l.ZIndex, null)), new(OnTop, "A new track on top", null, null)];

    // The lane of the clip selected on the timeline when the file can go there; else, for footage, the
    // lowest draw level that has footage (Video 1), the lowest one, or one of its own; for a sound,
    // Music, as add_audio_layer's default.
    public static SourceTarget Default(Project project, Asset asset, string? selectedLayer)
    {
        var targets = Targets(project, asset);
        var lanes = TimelineLayout.Lanes(project);
        if (selectedLayer is not null && lanes.FirstOrDefault(l => l.Clips.Any(c => c.LayerId == selectedLayer)) is { } selected
            && targets.FirstOrDefault(t => t.Key == selected.Key) is { } chosen)
        {
            return chosen;
        }

        if (asset.Type == AssetType.Audio)
        {
            return targets.First(t => t.Role == AudioRole.Music);
        }

        var levels = lanes.Where(l => l.Kind == LaneKind.Visual).OrderBy(l => l.ZIndex).ToList();
        var level = levels.FirstOrDefault(l => l.Clips.Any(c => c.Kind == ClipKind.Video)) ?? levels.FirstOrDefault();
        return level is null ? targets[^1] : targets.First(t => t.Key == level.Key);
    }

    // The clip Insert or Overwrite adds: the range of the file at the playhead on the target lane,
    // footage with its sound or without.
    public static AddClipOperation Clip(Asset asset, double at, SourceTarget target, SourceRange range, bool sound, ClipEdit edit) => new(
        asset.Type,
        asset.Id,
        at,
        target.ZIndex,
        target.Role is { } role ? EnumNames.ToName(role) : null,
        KeyframeWindow.Round(range.In),
        KeyframeWindow.Round(range.Out),
        Muted: asset.Type == AssetType.Video && !sound,
        Edit: edit);

    // A time in the file as the source viewer shows it: minutes, seconds and hundredths (00:01.20).
    public static string Stamp(double seconds)
    {
        var hundredths = (long)Math.Round(Math.Max(0, seconds) * 100);
        return string.Create(CultureInfo.InvariantCulture, $"{hundredths / 6000:00}:{hundredths / 100 % 60:00}.{hundredths % 100:00}");
    }

    // "In 00:01.20 · Out 00:04.20 · 3.00 s", naming only the marks there are.
    public static string Marks(SourceRange range)
    {
        var parts = new List<string>();
        if (range.InFrame is not null)
        {
            parts.Add($"In {Stamp(range.In)}");
        }

        if (range.OutFrame is not null)
        {
            parts.Add($"Out {Stamp(range.Out)}");
        }

        parts.Add(Seconds(range.Length));
        return string.Join(" · ", parts);
    }

    public static string Seconds(double seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    // What the two edits do, for the clip's length.
    public static string Note(double length) =>
        $"Insert pushes everything from the playhead on {Seconds(length)} to the right. Overwrite replaces what's there.";

    private static SourceTarget Role(AudioRole role, string title) => new($"audio:{EnumNames.ToName(role)}", title, null, role);
}
