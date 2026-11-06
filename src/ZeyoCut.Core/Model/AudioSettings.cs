// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Model;

// What a sound is for in the mix: music is ducked under voice when ducking is on.
public enum AudioRole
{
    Voice,
    Music,
    Effects
}

public sealed class AudioSettings
{
    public const double DefaultLoudness = -14;
    public const double MinLoudness = -40;
    public const double MaxLoudness = -5;

    // Integrated loudness of the exported mix in LUFS (two-pass loudnorm); null turns it off.
    public double? LoudnessTarget { get; set; } = DefaultLoudness;

    // Highest true peak allowed by loudnorm, dBTP.
    public double TruePeak { get; set; } = -1;

    // Lower music (role music) while voice (role voice) plays.
    public bool Ducking { get; set; } = true;
}
