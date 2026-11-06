// SPDX-License-Identifier: MIT

using ZeyoCut.Core;
using ZeyoCut.Mcp;
using ZeyoCut.Render.Export;
using ZeyoCut.Tests.TestSupport;
using static ZeyoCut.Tests.Mcp.McpServerTests;

namespace ZeyoCut.Tests.Mcp;

public class AllowedFoldersTests
{
    private static Settings For(string root) => new(
        null,
        Path.Combine(root, "projects"),
        Path.Combine(root, "logs"),
        LibraryPath: Path.Combine(root, "library"),
        ImportFolders: [Path.Combine(root, "media")]);

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void FilesAreReadOnlyFromTheProjectsTheLibraryAndTheImportFolders()
    {
        var root = TestPaths.NewDirectory("allowed-read");
        var settings = For(root);
        var media = Touch(Path.Combine(root, "media", "clip.png"));
        Assert.Equal(media, AllowedFolders.Readable(settings, $"\"{media}\""));
        Assert.Equal(Path.Combine(root, "media", "sub", "a.wav"), AllowedFolders.Readable(settings, Path.Combine(root, "media", "sub", "a.wav")));
        Assert.Equal(Path.Combine(root, "library", "sfx", "boom.wav"), AllowedFolders.Readable(settings, Path.Combine(root, "library", "sfx", "boom.wav")));
        Assert.Equal(Path.Combine(root, "projects", "a", "assets", "x.png"), AllowedFolders.Readable(settings, Path.Combine(root, "projects", "a", "assets", "x.png")));

        var secret = Touch(Path.Combine(root, "private", "keys.txt"));
        var refused = Assert.Throws<ZeyoCutException>(() => AllowedFolders.Readable(settings, secret)).Message;
        Assert.StartsWith($"{secret} is outside the folders ZeyoCut may read: the projects folder, the library and the import folders (", refused);
        Assert.EndsWith("add its folder to ImportFolders in appsettings.json or to ZEYOCUT_IMPORT_DIRS (folders separated by ;)", refused);
        Assert.Throws<ZeyoCutException>(() => AllowedFolders.Readable(settings, Path.Combine(root, "media", "..", "private", "keys.txt")));
        Assert.Throws<ZeyoCutException>(() => AllowedFolders.Readable(settings, Path.Combine(root, "media-other", "clip.png")));
        Assert.Throws<ZeyoCutException>(() => AllowedFolders.Readable(settings, @"C:\Windows\win.ini"));
        Assert.Throws<ZeyoCutException>(() => AllowedFolders.Readable(settings, @"\\server\share\clip.png"));
    }

    [Fact]
    public void ALinkInsideAnAllowedFolderCantLeadOutsideIt()
    {
        // A folder of its own each run: a junction left by an earlier run can be slow to delete.
        var root = TestPaths.NewDirectory($"allowed-link-{Guid.NewGuid():N}");
        var settings = For(root);
        Touch(Path.Combine(root, "private", "keys.txt"));
        Directory.CreateDirectory(Path.Combine(root, "media"));
        var link = Path.Combine(root, "media", "shortcut");
        var made = ProcessRunner.Run("cmd.exe", ["/c", "mklink", "/J", link, Path.Combine(root, "private")], TimeSpan.FromSeconds(30));
        Assert.SkipWhen(made.ExitCode != 0 || !Directory.Exists(link), $"couldn't make a junction: {made.StandardError}");
        try
        {
            var refused = Assert.Throws<ZeyoCutException>(() => AllowedFolders.Readable(settings, Path.Combine(link, "keys.txt"))).Message;
            Assert.StartsWith($"{Path.Combine(link, "keys.txt")} (it leads to {Path.Combine(root, "private", "keys.txt")}) is outside the folders", refused);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectsLiveInsideTheProjectsFolder()
    {
        var root = TestPaths.NewDirectory("allowed-projects");
        var settings = For(root);
        var inside = Path.Combine(root, "projects", "episode 1");
        Assert.Equal(inside, AllowedFolders.Project(settings, inside));
        Assert.Equal(Path.Combine(root, "projects", "shows", "ep2"), AllowedFolders.Project(settings, Path.Combine(root, "projects", "shows", "ep2")));

        var refused = Assert.Throws<ZeyoCutException>(() => AllowedFolders.Project(settings, Path.Combine(root, "elsewhere"))).Message;
        Assert.Equal($"{Path.Combine(root, "elsewhere")} is not inside the projects folder {settings.ProjectsRoot}; projects live there. To keep them somewhere else, set ProjectsRoot in appsettings.json or ZEYOCUT_PROJECTS_DIR", refused);
        Assert.Throws<ZeyoCutException>(() => AllowedFolders.Project(settings, settings.ProjectsRoot));
        Assert.Throws<ZeyoCutException>(() => AllowedFolders.Project(settings, Path.Combine(root, "projects", "..", "x")));
        Assert.Throws<ZeyoCutException>(() => AllowedFolders.Project(settings, Path.Combine(root, "projects2", "x")));
    }

    [Fact]
    public void TheDefaultImportFoldersAreTheUsersMediaFolders()
    {
        var folders = new Settings(null, @"C:\p", @"C:\l").ImportFolderList;
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), folders);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), folders);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZeyoCut", "projects"), Settings.DefaultProjectsRoot);
    }

    [Fact]
    public async Task TheServerRefusesFilesAndProjectsOutsideItsFolders()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("allowed-server");
        var projects = Path.Combine(root, "projects");
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);

        var outside = Text(await Call(client, "create_project", new() { ["name"] = "x", ["directory"] = @"C:\Windows\Temp\zeyocut-x" }, ct));
        Assert.Contains(@"C:\Windows\Temp\zeyocut-x is not inside the projects folder", outside);
        Assert.Contains("is not inside the projects folder", Text(await Call(client, "open_project", new() { ["nameOrPath"] = @"C:\Windows" }, ct)));
        Assert.Contains("is not inside the projects folder", Text(await Call(client, "open_project", new() { ["nameOrPath"] = ".." }, ct)));

        await CallOk(client, "create_project", new() { ["name"] = "inside" }, ct);
        var refused = Text(await Call(client, "import_asset", new() { ["path"] = @"C:\Windows\win.ini" }, ct));
        Assert.Contains(@"C:\Windows\win.ini is outside the folders ZeyoCut may read", refused);
        Assert.Contains("is outside the folders ZeyoCut may read", Text(await Call(client, "import_srt", new() { ["path"] = @"C:\Windows\win.srt" }, ct)));
        Assert.Contains("is outside the folders ZeyoCut may read", Text(await Call(client, "build_from_script", new() { ["scriptFile"] = @"C:\Windows\script.md" }, ct)));
    }
}
