// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Render.Export;

namespace ZeyoCut.Editor.Tests;

public static class TestFiles
{
    private static readonly Lazy<FfmpegPaths?> FfmpegPaths = new(() =>
        FfmpegLocator.TryLocate(null, out var paths, out _) ? paths : null);

    // An empty folder of the test's own, under the test output.
    public static string NewFolder(string name)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "TestOutput", name);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    // The repository's root folder: the first folder up from the test output that holds ZeyoCut.slnx.
    public static string RepositoryRoot
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                if (File.Exists(Path.Combine(folder.FullName, "ZeyoCut.slnx")))
                {
                    return folder.FullName;
                }
            }

            throw new InvalidOperationException($"ZeyoCut.slnx isn't in any folder above {AppContext.BaseDirectory}");
        }
    }

    // A copy of one of the repository's examples (comic-panels) in a folder; returns the copy's.
    public static string Example(string name, string folder)
    {
        var from = Path.Combine(RepositoryRoot, "examples", name);
        var copy = Path.Combine(folder, name);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(copy, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        return copy;
    }

    public static Project Project(int width = 320, int height = 180, double duration = 2) => new()
    {
        Name = "test",
        Width = width,
        Height = height,
        Fps = 30,
        Duration = duration,
        BackgroundColor = "#202020",
    };

    // A project folder holding project, saved.
    public static string NewProject(string name, Project? project = null)
    {
        var folder = Path.Combine(NewFolder(name), "project");
        ProjectSession.Create(folder, project ?? Project());
        return folder;
    }

    // A 4 s project with a sound in each role: the voice 'talk' (1-3 s), the music 'song' (0-4 s) and
    // the effect 'boing' (2.5-3 s).
    public static Project Sounds()
    {
        var project = Project(duration: 4);
        foreach (var (id, seconds, start, role) in new[] { ("talk", 2.0, 1.0, "voice"), ("song", 4.0, 0.0, "music"), ("boing", 0.5, 2.5, "effects") })
        {
            project.Assets.Add(new Asset { Id = id, Type = AssetType.Audio, File = $"assets/{id}.wav", Duration = seconds });
            ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = id, Id = id, Start = start, Role = role });
        }

        return project;
    }

    // Sounds() saved in a project folder with a tone for each sound.
    public static string NewSoundProject(string name)
    {
        var project = Sounds();
        var folder = NewProject(name, project);
        foreach (var (asset, frequency) in project.Assets.Zip(new[] { 300.0, 1000, 2000 }))
        {
            Tone(Path.Combine(folder, asset.File), asset.Duration!.Value, frequency, 0.2);
        }

        return folder;
    }

    // A mono 48 kHz WAV of a steady tone.
    public static string Tone(string path, double seconds, double frequency, double amplitude)
    {
        const int Rate = 48000;
        var samples = (int)Math.Round(seconds * Rate);
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
            writer.Write((short)Math.Round(amplitude * 32767 * Math.Sin(2 * Math.PI * frequency * i / Rate)));
        }

        return path;
    }

    public static string Png(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Orange);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    // A project of a 64 px orange picture, layer 'logo', whose aiImage matte isn't computed yet, saved
    // in directory. Returns the path the matte is computed to.
    public static string PendingMatte(string directory)
    {
        var project = Project();
        var asset = new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 64, Height = 64 };
        project.Assets.Add(asset);
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" } });
        var matte = MatteEditor.Build(MatteEditor.GetMattable(project, "logo"), MatteMethod.AiImage, new MatteValues(null, null, null, null));
        MatteEditor.Set(project, "logo", matte);
        ProjectSession.Create(directory, project);
        Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 64, 64);
        return MatteFiles.CachePath(directory, asset, matte);
    }

    // Whether a frame's BGRA pixels are all Project's background, #202020.
    public static bool OnlyBackground(byte[] pixels) => pixels.Chunk(4).All(p => p is [0x20, 0x20, 0x20, 0xFF]);

    public static FfmpegPaths Ffmpeg()
    {
        Assert.SkipWhen(FfmpegPaths.Value is null, "FFmpeg not found (install it or set FFMPEG_PATH); skipping");
        return FfmpegPaths.Value!;
    }

    // A video made by FFmpeg from a lavfi source, such as testsrc2=s=160x120:r=30:d=1.
    public static string Video(string path, string source, params string[] extra)
    {
        var result = ProcessRunner.Run(Ffmpeg().Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", source, .. extra, "-c:v", "libx264", "-pix_fmt", "yuv420p", path], TimeSpan.FromSeconds(120));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }
}
