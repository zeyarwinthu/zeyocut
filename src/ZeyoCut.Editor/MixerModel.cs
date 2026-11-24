// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor;

// An effect the mix applies, as a strip lists it, with a green dot while it's on.
public sealed record MixerSlot(string Name, bool On, string Tip)
{
    public Brush Dot => On ? MixerModel.OnBrush : MixerModel.OffBrush;
}

// A sound role's strip: its fader (gain), pan and mute, which are the project's, and solo, which
// only the preview hears.
public sealed record MixerStrip(AudioRole Role, string Name, Brush Brush, double Gain, double Pan, bool Muted, bool Soloed, int Clips, IReadOnlyList<MixerSlot> Slots)
{
    public string GainText => MixerModel.GainText(Gain);

    public string PanText => MixerModel.PanLabel(Pan);

    public string MuteTip => Muted ? $"Unmute {Name.ToLowerInvariant()}" : $"Mute {Name.ToLowerInvariant()} in the export and the preview";

    public string SoloTip => Soloed ? "Hear every role again" : $"Hear only {Name.ToLowerInvariant()} in the preview (never saved)";
}

// A sound on the automation lanes: where it plays, its volume (keyframed in timeline seconds), and
// why it isn't heard, or null when it is.
public sealed record MixerClip(string LayerId, AudioRole Role, double Start, double End, AnimatedValue Volume, string? Asset, double TrimIn, string? Off);

public sealed record MixerLane(AudioRole Role, string Name, Brush Brush, IReadOnlyList<MixerClip> Clips);

// What the Audio view shows: a strip per sound role, as the mix applies the project's audio
// settings, the clips by role with their volumes, where the music ducks, and the scales the faders,
// the volume lines and the Master meter use.
public static partial class MixerModel
{
    public static readonly AudioRole[] Roles = [AudioRole.Voice, AudioRole.Music, AudioRole.Effects];

    // The fader's marks from the top, evenly apart; between two the gain goes evenly in dB. The
    // bottom is the lowest gain a role takes.
    public static readonly double[] FaderMarks = [6, 0, -6, -12, -24, -48, RoleMix.MinGain];

    // The Master meter's, in dBFS: below the last it shows nothing.
    public static readonly double[] MeterMarks = [0, -6, -12, -18, -24, -36, -48];

    public static readonly Brush OnBrush = Frozen(Color.FromRgb(0x3D, 0xD6, 0x8C));
    public static readonly Brush OffBrush = Frozen(Color.FromRgb(0x5C, 0x62, 0x70));

    private static readonly Brush VoiceBrush = Frozen(Color.FromRgb(0x5B, 0x8C, 0xFF));
    private static readonly Brush MusicBrush = Frozen(Color.FromRgb(0xA9, 0x8B, 0xFF));
    private static readonly Brush EffectsBrush = Frozen(Color.FromRgb(0xA9, 0x8B, 0xFF));

    // The loudness targets the card offers; the project can hold any other from -40 to -5.
    public static readonly (string Name, double Lufs)[] Targets = [("YouTube", -14), ("Podcast", -16), ("Broadcast", -23)];

    public static string RoleName(AudioRole role) => role switch
    {
        AudioRole.Voice => "Voice",
        AudioRole.Music => "Music",
        _ => "Sound effects",
    };

    public static Brush RoleBrush(AudioRole role) => role switch
    {
        AudioRole.Voice => VoiceBrush,
        AudioRole.Music => MusicBrush,
        _ => EffectsBrush,
    };

    public static IReadOnlyList<MixerStrip> Strips(Project project, IReadOnlySet<AudioRole> solo)
    {
        var lanes = Lanes(project);
        var settings = project.Audio;
        return Roles.Select(role =>
        {
            var strip = settings.For(role);
            var ducks = Ducks(project, lanes);
            List<MixerSlot> slots = role == AudioRole.Music
                ? [new MixerSlot($"Ducking {Decibels(settings.DuckLevel)}", ducks, !settings.Ducking
                    ? "Off: the music isn't lowered under voice"
                    : ducks ? "Lowered where voice clips play; set it under Music track" : "On, but no voice clip plays with the music")]
                : [];
            return new MixerStrip(role, RoleName(role), RoleBrush(role), strip.Gain, strip.Pan, strip.Muted, solo.Contains(role),
                lanes.Single(l => l.Role == role).Clips.Count, slots);
        }).ToList();
    }

    // The Master strip's effects: the loudness normalization and its true-peak limit.
    public static IReadOnlyList<MixerSlot> MasterSlots(Project project) => project.Audio.LoudnessTarget is { } target
        ?
        [
            new MixerSlot($"Loudness {Number(target)}", true, $"Every export is normalized to {Number(target)} LUFS"),
            new MixerSlot("True-peak limit", true, $"Peaks are kept at most {Number(project.Audio.TruePeak)} dBTP"),
        ]
        :
        [
            new MixerSlot("Loudness off", false, "Exports keep the mix's own loudness"),
            new MixerSlot("True-peak limit", false, "Off with the loudness normalization"),
        ];

    // The sounds by role, as the mix takes them: hidden layers aren't, and footage without sound.
    public static IReadOnlyList<MixerLane> Lanes(Project project)
    {
        var end = FrameMath.RenderedDuration(project);
        var clips = new List<MixerClip>();
        foreach (var layer in project.Layers)
        {
            switch (layer)
            {
                case AudioLayer { Visible: true } audio when audio.Start < end:
                    clips.Add(new MixerClip(audio.Id, audio.Role, audio.Start, Math.Min(end, Core.Editing.TimelineEditor.ContentEnd(project, audio)), audio.Volume, audio.Asset,
                        audio.TrimIn, project.Audio.For(audio.Role).Muted ? "muted" : null));
                    break;
                case VideoLayer { Visible: true } video when video.Start < end && project.FindAsset(video.Asset) is { HasAudio: true }:
                    clips.Add(new MixerClip(video.Id, video.Role, video.Start, Math.Min(end, video.TimelineEnd), video.Volume, null, video.TrimIn,
                        video.Muted ? "sound off" : project.Audio.For(video.Role).Muted ? "muted" : null));
                    break;
            }
        }

        return Roles.Select(role => new MixerLane(role, RoleName(role), RoleBrush(role), clips.Where(c => c.Role == role && c.End > c.Start).OrderBy(c => c.Start).ToList())).ToList();
    }

    // The music's level from ducking over time, as the mix has it (FfmpegArgs.DuckPoints), or none
    // when nothing ducks.
    public static IReadOnlyList<(double Time, double Value)> Duck(Project project, IReadOnlyList<MixerLane> lanes)
    {
        var heard = Heard(lanes);
        var mix = AudioMix.Of(project.Audio);
        return FfmpegArgs.Ducks(heard, mix) ? FfmpegArgs.DuckPoints(heard, mix) : [];
    }

    private static bool Ducks(Project project, IReadOnlyList<MixerLane> lanes) => FfmpegArgs.Ducks(Heard(lanes), AudioMix.Of(project.Audio));

    private static List<AudioClip> Heard(IReadOnlyList<MixerLane> lanes) => lanes.SelectMany(l => l.Clips)
        .Where(c => c.Off is null)
        .Select(c => new AudioClip(c.LayerId, "", c.Start, c.TrimIn, c.End - c.Start, c.Volume.Value, 0, 0, Role: c.Role))
        .ToList();

    // The project as the preview plays it while roles are soloed: a copy with every other role
    // muted. The same project when none is.
    public static Project Heard(Project shown, IReadOnlySet<AudioRole> solo)
    {
        if (solo.Count == 0)
        {
            return shown;
        }

        var copy = ProjectJson.Clone(shown);
        foreach (var role in Roles.Where(r => !solo.Contains(r)))
        {
            copy.Audio.For(role).Muted = true;
        }

        return copy;
    }

    // Where a gain sits on a fader, from 0 (bottom) to 1 (top).
    public static double Position(double decibels)
    {
        var marks = FaderMarks;
        var db = Math.Clamp(decibels, marks[^1], marks[0]);
        for (var i = 0; i + 1 < marks.Length; i++)
        {
            if (db >= marks[i + 1])
            {
                var within = (marks[i] - db) / (marks[i] - marks[i + 1]);
                return (marks.Length - 1 - i - within) / (marks.Length - 1);
            }
        }

        return 0;
    }

    // The gain at a place on a fader, to the half dB.
    public static double GainAt(double position) => Math.Round(Unrounded(position) * 2, MidpointRounding.AwayFromZero) / 2;

    private static double Unrounded(double position)
    {
        var marks = FaderMarks;
        var steps = marks.Length - 1;
        var at = Math.Clamp(position, 0, 1) * steps;
        var below = Math.Min(steps - 1, (int)Math.Floor(at));
        var upper = marks[steps - below - 1];
        var lower = marks[steps - below];
        return lower + (upper - lower) * (at - below);
    }

    // Where a volume sits on an automation line: the fader's scale down to -48 dB, then evenly down to
    // silence at the bottom. Above +6 dB it stays at the top.
    public static double LevelPosition(double volume)
    {
        if (volume <= 0)
        {
            return 0;
        }

        var floor = Math.Pow(10, FaderMarks[^2] / 20);
        return volume >= floor ? Position(20 * Math.Log10(volume)) : volume / floor * Position(FaderMarks[^2]);
    }

    // The volume at a place on an automation line: to the half dB, or 0 at the bottom.
    public static double VolumeAt(double position)
    {
        var lowest = Position(FaderMarks[^2]);
        if (position <= 0.004)
        {
            return 0;
        }

        if (position < lowest)
        {
            return Math.Round(position / lowest * Math.Pow(10, FaderMarks[^2] / 20), 5);
        }

        return Math.Round(Math.Pow(10, GainAt(position) / 20), 4);
    }

    // Where a level in dBFS sits on the Master meter, from 0 to 1.
    public static double MeterPosition(double dbfs)
    {
        var marks = MeterMarks;
        if (double.IsNaN(dbfs) || dbfs <= marks[^1])
        {
            return 0;
        }

        var db = Math.Min(dbfs, marks[0]);
        for (var i = 0; i + 1 < marks.Length; i++)
        {
            if (db >= marks[i + 1])
            {
                var within = (marks[i] - db) / (marks[i] - marks[i + 1]);
                return (marks.Length - 1 - i - within) / (marks.Length - 1);
            }
        }

        return 0;
    }

    // The loudest of the last tenth of a second before seconds, of each side, in dBFS: peaks holds
    // left and right in turn, perSecond pairs a second (AudioAnalyzer.Peaks with two channels).
    public static (double Left, double Right) MeterLevel(float[] peaks, int perSecond, double seconds)
    {
        var last = (int)Math.Floor(seconds * perSecond);
        var first = Math.Max(0, last - Math.Max(1, perSecond / 10) + 1);
        var (left, right) = (0f, 0f);
        for (var i = first; i <= last && 2 * i + 1 < peaks.Length; i++)
        {
            left = Math.Max(left, peaks[2 * i]);
            right = Math.Max(right, peaks[2 * i + 1]);
        }

        return (LoudnessMeter.Decibels(left), LoudnessMeter.Decibels(right));
    }

    // 0.0, -8.5, +3.0: a fader's gain.
    public static string GainText(double gain) => gain.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    // C, L50, R20: a pan.
    public static string PanLabel(double pan) =>
        Math.Abs(pan) < 0.005 ? "C" : $"{(pan < 0 ? "L" : "R")}{Math.Round(Math.Abs(pan) * 100).ToString(CultureInfo.InvariantCulture)}";

    // -6 dB, 0 dB, +3 dB, or silent: a volume as the automation line shows it.
    public static string VolumeText(double volume) =>
        volume <= 0 ? "silent" : Decibels(Math.Round(20 * Math.Log10(volume) * 2, MidpointRounding.AwayFromZero) / 2);

    public static string Decibels(double value) => value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " dB";

    public static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    public static string Seconds(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture) + " s";

    public static string TargetText(double? target) => target switch
    {
        null => "Target: off",
        { } lufs when Targets.FirstOrDefault(t => t.Lufs == lufs) is { Name: { } name } => $"Target: {name} {Number(lufs)} LUFS",
        { } lufs => $"Target: {Number(lufs)} LUFS",
    };

    // What the card says of a measurement: how the export normalizes the mix, and the first thing
    // audio_levels flagged.
    public static string LoudnessNote(AudioLevelsReport report, double? target)
    {
        string text;
        if (target is not { } lufs)
        {
            text = "Normalization is off: exports keep the mix's own loudness.";
        }
        else if (report.Notes.Select(n => Before().Match(n)).FirstOrDefault(m => m.Success) is { } before)
        {
            var measured = double.Parse(before.Groups[1].Value, CultureInfo.InvariantCulture);
            var gap = Math.Round(lufs - measured, 1);
            text = $"Before normalizing, the mix is {Number(measured)} LUFS: " + (Math.Abs(gap) < 0.1
                ? "on the target already."
                : $"{Number(Math.Abs(gap))} dB {(gap > 0 ? "quieter" : "louder")} than the target. Every export is normalized to {Number(lufs)} LUFS.");
        }
        else
        {
            text = "The mix is silent, so it isn't normalized.";
        }

        return report.Flags.Count == 0 ? text : $"{text} {report.Flags.Count} flagged: {report.Flags[0].Message}";
    }

    [GeneratedRegex(@"^loudness: (-?[\d.]+) LUFS → ")]
    private static partial Regex Before();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
