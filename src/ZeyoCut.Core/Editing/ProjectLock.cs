// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ZeyoCut.Core.Editing;

// project.lock beside project.json, open in one process at a time while it checks and saves
// project.json, so two programs on one project save one after the other. A process that dies lets
// go of it, so a lock can't go stale; the file itself means nothing while no one has it open. It
// holds the highest revision saved, so a revision never goes back, even after an older
// project.json is put back.
public sealed class ProjectLock : IDisposable
{
    public const string FileName = "project.lock";

    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private readonly FileStream _stream;

    private ProjectLock(FileStream stream, long highest)
    {
        _stream = stream;
        Highest = highest;
    }

    // The highest revision any program has saved, as far as project.lock knows.
    public long Highest { get; private set; }

    // Waits up to timeout while another program has it.
    public static ProjectLock Acquire(string directory, TimeSpan timeout)
    {
        var path = Path.Combine(directory, FileName);
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is SharingViolation or LockViolation)
            {
                if (clock.Elapsed >= timeout)
                {
                    throw new ZeyoCutException(
                        $"another program is saving this project (it has {FileName} open) and didn't finish within {timeout.TotalSeconds:0.#} s, so nothing was saved; try again", ex);
                }

                Thread.Sleep(Math.Min(10 * attempt, 50));
                continue;
            }

            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var text = reader.ReadToEnd().Trim();
                return new ProjectLock(stream, long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var highest) ? highest : 0);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    // Records a revision just saved. project.json is already saved, so a lock file that can't be
    // written only loses the highest revision; the next save still counts on from project.json.
    public void Saved(long revision)
    {
        if (revision <= Highest)
        {
            return;
        }

        Highest = revision;
        try
        {
            _stream.SetLength(0);
            _stream.Position = 0;
            _stream.Write(Encoding.UTF8.GetBytes(revision.ToString(CultureInfo.InvariantCulture) + "\n"));
            _stream.Flush();
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => _stream.Dispose();
}
