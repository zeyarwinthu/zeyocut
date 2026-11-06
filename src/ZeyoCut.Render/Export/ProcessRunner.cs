// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using ZeyoCut.Core;

namespace ZeyoCut.Render.Export;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    public static ProcessResult Run(string executable, IEnumerable<string> arguments, TimeSpan timeout)
    {
        using var process = Process.Start(StartInfo(executable, arguments))
            ?? throw new ZeyoCutException($"could not start {executable}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new ZeyoCutException($"{Path.GetFileName(executable)} timed out after {timeout.TotalSeconds:0}s");
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    // Runs to the end, handing every output line to the callbacks; cancelling kills the process.
    public static ProcessResult RunLines(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? standardOutput,
        Action<string>? standardError,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(StartInfo(executable, arguments))
            ?? throw new ZeyoCutException($"could not start {executable}");
        var errorTail = new Queue<string>();
        var stderr = Task.Run(() =>
        {
            while (process.StandardError.ReadLine() is { } line)
            {
                standardError?.Invoke(line);
                lock (errorTail)
                {
                    errorTail.Enqueue(line);
                    if (errorTail.Count > 40)
                    {
                        errorTail.Dequeue();
                    }
                }
            }
        }, CancellationToken.None);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        while (process.StandardOutput.ReadLine() is { } line)
        {
            standardOutput?.Invoke(line);
        }

        process.WaitForExit();
        stderr.Wait(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        lock (errorTail)
        {
            return new ProcessResult(process.ExitCode, "", string.Join("\n", errorTail));
        }
    }

    // Waits for the process to exit; false when it is still running after 'timeout'. Cancelling stops the wait.
    public static bool WaitForExit(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!process.WaitForExit(200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (waited.Elapsed >= timeout)
            {
                return false;
            }
        }

        process.WaitForExit();
        return true;
    }

    public static ProcessResult RunBinary(string executable, IEnumerable<string> arguments, TimeSpan timeout, out byte[] output)
    {
        using var process = Process.Start(StartInfo(executable, arguments))
            ?? throw new ZeyoCutException($"could not start {executable}");
        var buffer = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new ZeyoCutException($"{Path.GetFileName(executable)} timed out after {timeout.TotalSeconds:0}s");
        }

        process.WaitForExit();
        stdout.Wait();
        output = buffer.ToArray();
        return new ProcessResult(process.ExitCode, "", stderr.Result);
    }
}
