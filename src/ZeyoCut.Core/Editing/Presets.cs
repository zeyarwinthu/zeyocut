// SPDX-License-Identifier: MIT

using ZeyoCut.Core.Model;

namespace ZeyoCut.Core.Editing;

public sealed record Preset(string Name, int Width, int Height, int Fps, int Crf, double Loudness, int AudioKbps, string Description)
{
    public bool IsVertical => Height > Width;
}

// Delivery presets: canvas size and frame rate for create_project/update_project, and encoder settings
// for start_export (H.264 high profile, 2 B-frames, a keyframe every half second, AAC 48 kHz).
public static class Presets
{
    public static readonly Preset Youtube1080p = new("youtube_1080p", 1920, 1080, 30, 18, AudioSettings.DefaultLoudness, 192, "YouTube 1920x1080 landscape, 30 fps");

    public static readonly Preset Shorts1080p = new("shorts_1080p", 1080, 1920, 30, 18, AudioSettings.DefaultLoudness, 192, "Shorts / Reels / TikTok 1080x1920 vertical, 30 fps");

    public static readonly IReadOnlyList<Preset> All = [Youtube1080p, Shorts1080p];

    public static string Names => string.Join(", ", All.Select(p => p.Name));

    public static Preset Get(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ZeyoCutException($"unknown preset '{name}'; presets: {Names}");
}
