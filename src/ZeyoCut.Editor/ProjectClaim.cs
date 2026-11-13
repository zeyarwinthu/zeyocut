// SPDX-License-Identifier: MIT

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ZeyoCut.Editor;

// One editor window per project. The window that has a project open holds a named pipe whose name
// comes from the project's folder, which only this user can open. An editor asked to open the same
// project finds the pipe taken, lets the window holding it come to the front and asks it to, rather
// than open a second copy whose edits would cross the first one's. The pipe goes when the window
// closes the project or ends, so a crash leaves nothing behind.
public sealed class ProjectClaim : IDisposable
{
    private const byte ShowRequest = 1;

    private readonly NamedPipeServerStream _pipe;
    private readonly Action _show;
    private readonly CancellationTokenSource _stop = new();

    private ProjectClaim(NamedPipeServerStream pipe, Action show)
    {
        _pipe = pipe;
        _show = show;
        _ = Listen();
    }

    // The pipe's name for a project folder, the same for every way of writing the folder's path.
    public static string PipeName(string directory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        return "ZeyoCut.Editor." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }

    // Claims the project for this window, or null when another window has it. show runs (on a
    // background thread) each time another editor asks this window to come to the front.
    public static ProjectClaim? TryClaim(string directory, Action show)
    {
        try
        {
            var pipe = new NamedPipeServerStream(PipeName(directory), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly);
            return new ProjectClaim(pipe, show);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    // True when another window has the project open, after asking it to come to the front.
    public static bool ShownElsewhere(string directory)
    {
        using (var probe = TryClaim(directory, () => { }))
        {
            if (probe is not null)
            {
                return false;
            }
        }

        return AskToShow(directory, TimeSpan.FromSeconds(2));
    }

    // Asks the window that has the project open to come to the front. False when no window answers
    // in time, such as one that is closing the project just now.
    public static bool AskToShow(string directory, TimeSpan wait)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)wait.TotalMilliseconds);

            // Windows lets a program bring its window to the front only while the user works in it,
            // or when the program in front passes that on, as this one does when the user started it.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var owner))
            {
                AllowSetForegroundWindow(owner);
            }

            pipe.WriteByte(ShowRequest);
            pipe.WaitForPipeDrain();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _pipe.Dispose();
    }

    private async Task Listen()
    {
        var buffer = new byte[1];
        while (true)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token);
                if (await _pipe.ReadAsync(buffer, _stop.Token) == 1 && buffer[0] == ShowRequest)
                {
                    _show();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                // The other editor went away mid-way.
            }

            try
            {
                _pipe.Disconnect();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // It wasn't connected any more.
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
