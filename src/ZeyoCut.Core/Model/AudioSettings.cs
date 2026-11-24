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
    public const double DefaultDuckLevel = -12;
    public const double MinDuckLevel = -40;
    public const double DefaultDuckFadeIn = 0.5;
    public const double DefaultDuckFadeOut = 1.5;
    public const double MaxDuckFade = 5;

    // Integrated loudness of the exported mix in LUFS (two-pass loudnorm); null turns it off.
    public double? LoudnessTarget { get; set; } = DefaultLoudness;

    // Highest true peak allowed by loudnorm, dBTP.
    public double TruePeak { get; set; } = -1;

    // Lower music (role music) while voice (role voice) plays.
    public bool Ducking { get; set; } = true;

    // How far ducking lowers the music, in dB: 0 to -40.
    public double DuckLevel { get; set; } = DefaultDuckLevel;

    // Seconds the music takes to go down before a voice clip starts, and to come back up after it ends.
    public double DuckFadeIn { get; set; } = DefaultDuckFadeIn;
    public double DuckFadeOut { get; set; } = DefaultDuckFadeOut;

    // Each role's strip in the mixer, applied to every clip with that role.
    public RoleMix Voice { get; set; } = new();
    public RoleMix Music { get; set; } = new();
    public RoleMix Effects { get; set; } = new();

    public RoleMix For(AudioRole role) => role switch
    {
        AudioRole.Voice => Voice,
        AudioRole.Music => Music,
        _ => Effects,
    };
}

// A role's gain, balance and mute in the mix.
public sealed class RoleMix
{
    public const double MinGain = -60;
    public const double MaxGain = 6;

    // dB, -60 to +6; 0 leaves the clips as they are.
    public double Gain { get; set; }

    // Balance, from -1 (left only) through 0 to 1 (right only).
    public double Pan { get; set; }

    public bool Muted { get; set; }
}
