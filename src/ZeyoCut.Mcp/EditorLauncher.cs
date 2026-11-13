// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ZeyoCut.Core;

namespace ZeyoCut.Mcp;

// Starts the editor window as a program of its own. Process.Start would hand it every handle the
// server can pass on, among them the pipes the MCP client talks to the server through, and the client
// would then wait for the editor to close them before it noticed the server had ended. CreateProcess
// without handle inheritance, in a process group of its own and outside the client's job where the
// job allows it, gives the editor nothing of the server's and lets it stay open after the server ends.
public static class EditorLauncher
{
    public const string FileName = "ZeyoCut.Editor.exe";

    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int UseStdHandles = 0x00000100;
    private const int AccessDenied = 5;

    // The editor: the EditorPath setting, else ZeyoCut.Editor.exe next to the server, as the release
    // zip and the bundle have it.
    public static string Find(Settings settings)
    {
        if (settings.EditorPath is { } path)
        {
            return File.Exists(path)
                ? path
                : throw new ZeyoCutException($"the EditorPath setting names {path}, which doesn't exist; set EditorPath in appsettings.json or {Settings.EditorPathVariable} to where {FileName} is");
        }

        var next = Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(next)
            ? next
            : throw new ZeyoCutException($"the editor isn't next to the server ({next} doesn't exist). {FileName} comes with the server in the release zip and the bundle; "
                + $"if it is somewhere else, set EditorPath in appsettings.json or {Settings.EditorPathVariable} to where it is");
    }

    // Starts program with these arguments in its own folder and returns its process id. It gets the
    // server's environment, so the settings the client gave the server reach it too.
    public static int Start(string program, IReadOnlyList<string> arguments)
    {
        // No standard input, output or error: the editor writes nothing there, and without this
        // Windows would give it the numbers of the server's handles, which mean nothing in it.
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = UseStdHandles };
        var folder = Path.GetDirectoryName(Path.GetFullPath(program));
        var flags = CreateNoWindow | CreateNewProcessGroup;
        var started = CreateProcess(program, new StringBuilder(CommandLine(program, arguments)), IntPtr.Zero, IntPtr.Zero, false, flags | CreateBreakawayFromJob, IntPtr.Zero, folder, ref startup, out var info);
        var error = started ? 0 : Marshal.GetLastWin32Error();
        if (error == AccessDenied)
        {
            // The job the server runs in doesn't let a process leave it; the editor then ends with it.
            started = CreateProcess(program, new StringBuilder(CommandLine(program, arguments)), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, folder, ref startup, out info);
            error = started ? 0 : Marshal.GetLastWin32Error();
        }

        if (!started)
        {
            throw new ZeyoCutException($"couldn't start {program}: {new Win32Exception(error).Message}");
        }

        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return info.ProcessId;
    }

    // The command line a program parses back into these arguments, each quoted the way the C
    // runtime and .NET read them: backslashes double only before a quote.
    public static string CommandLine(string program, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder().Append('"').Append(program).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                builder.Append(argument);
                continue;
            }

            builder.Append('"');
            var slashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }

                builder.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }

            builder.Append('\\', slashes * 2).Append('"');
        }

        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int CharsWidth;
        public int CharsHeight;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
