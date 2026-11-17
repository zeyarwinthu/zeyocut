// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Windows;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The sounds' waveforms on the media panel's tiles and the timeline's clips: peaks made once in the
// background from the whole file, kept in the project's cache folder, and made again when the file
// changes or the kept copy is gone.
public class WaveformTests
{
    // A mono 16 kHz WAV: a 440 Hz tone at this share of full scale for 'tone' seconds, then silence.
    private static string Tone(string path, double amplitude, double tone, double silence)
    {
        const int Rate = 16000;
        var samples = (int)Math.Round((tone + silence) * Rate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var time = i / (double)Rate;
            writer.Write((short)Math.Round(time < tone ? amplitude * 32767 * Math.Sin(2 * Math.PI * 440 * time) : 0));
        }

        return path;
    }

    private static AudioPeaks Wait(MediaWaveforms waveforms, BlockingCollection<string> ready, Asset asset)
    {
        Assert.Null(waveforms.Get(asset));
        Assert.True(ready.TryTake(out var id, TimeSpan.FromSeconds(30)), $"no waveform of '{asset.Id}'");
        Assert.Equal(asset.Id, id);
        return waveforms.Get(asset)!;
    }

    private static string[] Kept(string directory) =>
        Directory.GetFiles(Path.Combine(directory, "cache", MediaWaveforms.Folder)).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public void ASoundsPeaksFollowItsToneAndAreKeptForNextTime()
    {
        var ffmpeg = TestFiles.Ffmpeg().Ffmpeg;
        var directory = TestFiles.NewProject("waveform-tone");
        var path = Tone(Path.Combine(directory, ProjectStore.AssetsFolder, "tone.wav"), 0.5, 1, 1);
        var tone = new Asset { Id = "tone", Type = AssetType.Audio, File = "assets/tone.wav", Duration = 2 };

        var ready = new BlockingCollection<string>();
        using (var waveforms = new MediaWaveforms(directory, () => ffmpeg, ready.Add))
        {
            var peaks = Wait(waveforms, ready, tone);
            Assert.Equal(2 * AudioPeaks.PerSecond, peaks.Values.Length);
            Assert.All(peaks.Values[..200], v => Assert.InRange(v, 122, 133));
            Assert.All(peaks.Values[200..], v => Assert.Equal(0, v));
            Assert.InRange(peaks.Max(0.05, 0.06)!.Value, 0.48, 0.52);
            Assert.Equal(0.0, peaks.Max(1.05, 1.95));
            Assert.Null(peaks.Max(2, 3));
            Assert.Same(peaks, waveforms.Get(tone));
        }

        // Opened again, they're read from the cache folder (there's no ffmpeg to make them with).
        var kept = Kept(directory);
        var file = Path.Combine(directory, "cache", MediaWaveforms.Folder, Assert.Single(kept));
        var written = File.GetLastWriteTimeUtc(file);
        using (var again = new MediaWaveforms(directory, () => "", ready.Add))
        {
            Assert.Equal(400, Wait(again, ready, tone).Values.Length);
        }

        Assert.Equal(written, File.GetLastWriteTimeUtc(file));

        // A kept copy that's gone is made again.
        File.Delete(file);
        using (var gone = new MediaWaveforms(directory, () => ffmpeg, ready.Add))
        {
            Assert.InRange(Wait(gone, ready, tone).Max(0, 1)!.Value, 0.48, 0.52);
        }

        Assert.Equal(kept, Kept(directory));

        // The file replaced under the same name gets peaks of its own.
        Tone(path, 0.25, 0.5, 0);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        using (var changed = new MediaWaveforms(directory, () => ffmpeg, ready.Add))
        {
            var peaks = Wait(changed, ready, tone);
            Assert.Equal(100, peaks.Values.Length);
            Assert.InRange(peaks.Max(0, 0.5)!.Value, 0.23, 0.27);
        }

        Assert.Equal(2, Kept(directory).Length);
        Assert.Empty(ready);
    }

    // Only sounds have waveforms, and only while their file is there.
    [Fact]
    public void PicturesFootageAndMissingSoundsHaveNone()
    {
        var directory = TestFiles.NewProject("waveform-none");
        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 64, 64);
        var ready = new BlockingCollection<string>();
        using var waveforms = new MediaWaveforms(directory, () => "", ready.Add);
        Assert.Null(waveforms.Get(new Asset { Id = "gone", Type = AssetType.Audio, File = "assets/gone.wav", Duration = 1 }));
        Assert.Null(waveforms.Get(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 64, Height = 64 }));
        Assert.False(ready.TryTake(out _, TimeSpan.FromMilliseconds(300)));
        Assert.False(Directory.Exists(Path.Combine(directory, "cache", MediaWaveforms.Folder)));
    }

    // A clip draws the part of the sound it plays: from its trim in, and nothing past its trim out.
    [Fact]
    public void AClipDrawsThePartOfTheSoundItPlays()
    {
        var project = TestFiles.Project();
        project.Assets.Add(new Asset { Id = "tone", Type = AssetType.Audio, File = "assets/tone.wav", Duration = 2 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "tone", Id = "music", Start = 1, TrimIn = 0.5, TrimOut = 1.5 });
        var clip = TimelineLayout.Lanes(project).SelectMany(l => l.Clips).Single(c => c.LayerId == "music");
        Assert.Equal(("tone", 0.5, (double?)1.5), (clip.Asset, clip.TrimIn, clip.TrimOut));

        // Full scale for the sound's first second, then silent: as tall as the area, mirrored about
        // its middle, for the half of 0.5–1.5 s that's loud.
        var peaks = new AudioPeaks([.. Enumerable.Repeat((byte)255, 200), .. Enumerable.Repeat((byte)0, 200)]);
        var outline = peaks.Outline(0.5, 1.5, new Rect(0, 0, 100, 20), 50);
        Assert.Equal(new Rect(0, 0, 100, 20), outline.Bounds);
        Assert.True(outline.FillContains(new Point(25, 3)));
        Assert.True(outline.FillContains(new Point(25, 17)));
        Assert.False(outline.FillContains(new Point(75, 3)));

        // Nothing past where the clip stops playing the sound, nor past the sound's end.
        var cut = peaks.Outline(0.5, 1.5, new Rect(0, 0, 100, 20), 50, end: 0.75);
        Assert.True(cut.FillContains(new Point(10, 3)));
        Assert.False(cut.FillContains(new Point(40, 3)));
        Assert.Equal(new Rect(0, 10, 100, 0), new AudioPeaks([255]).Outline(1, 2, new Rect(0, 0, 100, 20), 10).Bounds);
    }
}
