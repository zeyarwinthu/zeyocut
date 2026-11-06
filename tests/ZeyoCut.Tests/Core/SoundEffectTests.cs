// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class SoundEffectTests
{
    // sfx/whoosh.mp3, sfx/impacts/boom.wav, sfx/impacts/thud.wav, sfx/ui/thud.ogg and a text file.
    private static Library SfxLibrary(string name)
    {
        var library = new Library(TestPaths.NewDirectory(name));
        var files = new[] { "whoosh.mp3", "impacts/boom.wav", "impacts/thud.wav", "ui/thud.ogg", "readme.txt" };
        foreach (var file in files)
        {
            var path = Path.Combine(library.SfxFolder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"sound {file}");
        }

        return library;
    }

    [Fact]
    public void TheSfxFolderListsAudioFilesByCategory()
    {
        var library = SfxLibrary("sfx-list");
        Assert.Equal(["whoosh", "impacts/boom", "impacts/thud", "ui/thud"], SoundEffects.List(library).Select(e => e.Name));
        Assert.Equal(["", "impacts", "impacts", "ui"], SoundEffects.List(library).Select(e => e.Category));
        Assert.Equal(["impacts/thud", "ui/thud"], SoundEffects.List(library, "THUD").Select(e => e.Name));

        Assert.Equal("impacts/boom", SoundEffects.Find(library, "boom").Name);
        Assert.Equal("ui/thud", SoundEffects.Find(library, @"ui\thud").Name);
        Assert.Contains("'thud' matches 2 sound effects: impacts/thud, ui/thud; use the full name", Assert.Throws<ZeyoCutException>(() => SoundEffects.Find(library, "thud")).Message);
        Assert.Contains("no sound effect named 'thu'; similar: impacts/thud, ui/thud", Assert.Throws<ZeyoCutException>(() => SoundEffects.Find(library, "thu")).Message);
        Assert.Contains("no sound effect named 'zap'; sound effects: whoosh, impacts/boom, impacts/thud, ui/thud", Assert.Throws<ZeyoCutException>(() => SoundEffects.Find(library, "zap")).Message);

        var empty = new Library(TestPaths.NewDirectory("sfx-empty"));
        Assert.Empty(SoundEffects.List(empty));
        Assert.Contains("there are no sound effects yet; put wav, mp3, ogg or other audio files in", Assert.Throws<ZeyoCutException>(() => SoundEffects.Find(empty, "boom")).Message);
    }

    [Fact]
    public void AddSfxPlacesAnEffectsLayerAndImportsTheSoundOnce()
    {
        var library = SfxLibrary("sfx-add");
        var directory = TestPaths.NewDirectory("sfx-add-project");
        var project = TestProjects.Empty(duration: 3);

        var first = SoundEffects.Add(project, directory, library, "boom", 1.2, 0.8, new FakeAssetProbe());
        Assert.True(first.Imported);
        Assert.Equal("boom", first.Layer.Id);
        Assert.Equal(1.2, first.Layer.Start);
        Assert.Equal(0.8, first.Layer.Volume);
        Assert.Equal(AudioRole.Effects, first.Layer.Role);
        Assert.Equal("boom", first.Layer.Asset);
        Assert.Equal(AssetType.Audio, first.Asset.Type);
        Assert.Equal(1.5, first.Asset.Duration);
        Assert.Equal("sound impacts/boom.wav", File.ReadAllText(ProjectStore.AssetPath(directory, first.Asset)));
        Assert.Empty(first.Warnings);

        var second = SoundEffects.Add(project, directory, library, "impacts/boom", 2.0, 1, new FakeAssetProbe());
        Assert.False(second.Imported);
        Assert.Same(first.Asset, second.Asset);
        Assert.Equal("boom_2", second.Layer.Id);
        Assert.Single(project.Assets);
        Assert.Contains("the sound lasts 1.5s and runs past the project end (3s), so the export cuts it at the end", second.Warnings);

        var thud = SoundEffects.Add(project, directory, library, "ui/thud", 0, 1, new FakeAssetProbe());
        Assert.True(thud.Imported);
        Assert.Equal(2, project.Assets.Count);

        Assert.Contains("time must be from 0 to before the project end (3s); got 3", Assert.Throws<ZeyoCutException>(() => SoundEffects.Add(project, directory, library, "boom", 3, 1, new FakeAssetProbe())).Message);
        Assert.Contains("volume must be between 0 and 10", Assert.Throws<ZeyoCutException>(() => SoundEffects.Add(project, directory, library, "boom", 1, 11, new FakeAssetProbe())).Message);
        Assert.Equal(3, project.Layers.Count);
    }
}
