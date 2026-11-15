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
