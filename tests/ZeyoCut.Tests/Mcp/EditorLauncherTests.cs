// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using ZeyoCut.Core;
using ZeyoCut.Mcp;
using ZeyoCut.Tests.TestSupport;
using static ZeyoCut.Tests.Mcp.McpServerTests;

namespace ZeyoCut.Tests.Mcp;

public class EditorLauncherTests
{
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public void ArgumentsReachTheProgramAsTheyWere()
    {
        string[] arguments =
        [
            @"C:\Users\me\ZeyoCut\projects\my video", "plain", "", "say \"hi\"", @"ends\", @"C:\a b\", @"a\\""b", @"\\server\share", "tab\there",
        ];
        Assert.Equal([@"C:\Program Files\ZeyoCut\ZeyoCut.Editor.exe", .. arguments], Parse(EditorLauncher.CommandLine(@"C:\Program Files\ZeyoCut\ZeyoCut.Editor.exe", arguments)));
        Assert.Equal("\"C:\\x.exe\" plain \"a b\" \"\" \"C:\\a b\\\\\"", EditorLauncher.CommandLine(@"C:\x.exe", ["plain", "a b", "", @"C:\a b\"]));
    }

    [Fact]
    public async Task TheProgramStartsInItsFolderWithTheArguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("launcher-args");
        var script = Path.Combine(root, "stand in.ps1");
        var output = Path.Combine(root, "args.txt");
        File.WriteAllText(script, "Set-Content -LiteralPath (Join-Path $PSScriptRoot 'args.txt') -Value (@($args) + (Get-Location).Path)");

        var process = EditorLauncher.Start(PowerShell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, @"C:\projects\my video", "two"]);
        Assert.True(process > 0);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string[] lines;
        while ((lines = Lines(output)).Length < 3)
        {
            Assert.True(DateTime.UtcNow < deadline, "the program didn't write its arguments");
            await Task.Delay(50, ct);
        }

        Assert.Equal([@"C:\projects\my video", "two", Path.GetDirectoryName(PowerShell)], lines);

        static string[] Lines(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllLines(path) : [];
            }
            catch (IOException)
            {
                return [];
            }
        }
    }

    [Fact]
    public void TheProgramGetsNoneOfTheServersHandles()
    {
        // Process.Start passes on every handle that can be passed on, such as the end of a pipe the
        // MCP client reads: the pipe stays open while the program runs.
        Assert.False(PipeEnds(() => Process.Start(new ProcessStartInfo(PowerShell, "-NoProfile -Command Start-Sleep 60") { UseShellExecute = false, CreateNoWindow = true })!.Id, TimeSpan.FromSeconds(2)));

        // The editor gets none, so the pipe ends as soon as the server closes it. Another test may
        // start a program in the meantime that takes the pipe until it ends, hence the long wait.
        Assert.True(PipeEnds(() => EditorLauncher.Start(PowerShell, ["-NoProfile", "-Command", "Start-Sleep 60"]), TimeSpan.FromSeconds(45)));

        static bool PipeEnds(Func<int> start, TimeSpan wait)
        {
            using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            using var process = Process.GetProcessById(start());
            try
            {
                pipe.DisposeLocalCopyOfClientHandle();
                var read = Task.Run(pipe.ReadByte);
                return read.Wait(wait) && read.Result == -1;
            }
            finally
            {
                process.Kill();
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public void TheEditorIsTheSettingOrTheOneNextToTheServer()
    {
        var root = TestPaths.NewDirectory("launcher-find");
        var editor = Path.Combine(root, "My Editor.exe");
        File.WriteAllText(editor, "");
        Assert.Equal(editor, EditorLauncher.Find(new Settings(null, root, root, EditorPath: editor)));

        var missing = Path.Combine(root, "gone.exe");
        Assert.Equal(
            $"the EditorPath setting names {missing}, which doesn't exist; set EditorPath in appsettings.json or ZEYOCUT_EDITOR_PATH to where ZeyoCut.Editor.exe is",
            Assert.Throws<ZeyoCutException>(() => EditorLauncher.Find(new Settings(null, root, root, EditorPath: missing))).Message);

        // The tests run without the editor next to them.
        Assert.StartsWith(
            $"the editor isn't next to the server ({Path.Combine(AppContext.BaseDirectory, "ZeyoCut.Editor.exe")} doesn't exist). ZeyoCut.Editor.exe comes with the server in the release zip and the bundle; ",
            Assert.Throws<ZeyoCutException>(() => EditorLauncher.Find(new Settings(null, root, root))).Message);

        Assert.StartsWith($"couldn't start {missing}: ", Assert.Throws<ZeyoCutException>(() => EditorLauncher.Start(missing, [])).Message);
    }

    [Fact]
    public async Task ServerOpensAProjectInTheEditor()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-open-in-editor");
        var projects = Path.Combine(root, "projects");
        Directory.CreateDirectory(Path.Combine(projects, "not a project"));
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct, new() { ["ZEYOCUT_EDITOR_PATH"] = PowerShell });

        Assert.Contains("no project is open", Text(await Call(client, "open_in_editor", new(), ct)));
        await CallOk(client, "create_project", new() { ["name"] = "first", ["width"] = 320, ["height"] = 180 }, ct);
        await CallOk(client, "create_project", new() { ["name"] = "second", ["width"] = 320, ["height"] = 180 }, ct);

        var opened = await CallOk(client, "open_in_editor", new(), ct);
        Assert.Matches($@"^opened {System.Text.RegularExpressions.Regex.Escape(Path.Combine(projects, "second"))} in the editor \({System.Text.RegularExpressions.Regex.Escape(PowerShell)}, process \d+\); the user can now edit it by hand while you keep working on it$", opened);
        Assert.StartsWith($"opened {Path.Combine(projects, "first")} in the editor (", await CallOk(client, "open_in_editor", new() { ["project"] = "first" }, ct));

        Assert.Contains($"no project found at '{Path.Combine(projects, "not a project")}'; projects: ", Text(await Call(client, "open_in_editor", new() { ["project"] = "not a project" }, ct)));
        Assert.Contains("is not inside the projects folder", Text(await Call(client, "open_in_editor", new() { ["project"] = root }, ct)));
    }

    private static string[] Parse(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
