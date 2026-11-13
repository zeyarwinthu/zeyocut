// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

// The size and time of project.json, to tell cheaply whether it may have changed.
public readonly record struct ProjectFileStamp(long Length, DateTime LastWrite);

public static class ProjectStore
{
    public const string ProjectFileName = "project.json";
    public const string AssetsFolder = "assets";
    public const string RendersFolder = "renders";
    public const string ExportsFolder = "exports";
    public const string BackupsFolder = "backups";

    private const int SaveAttempts = 5;

    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenamePosixSemantics = 0x2;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool IsProjectDirectory(string directory) =>
        File.Exists(Path.Combine(directory, ProjectFileName));

    public static Project Load(string directory) => Load(directory, out _);

    // savedVersion is the format the file was written in; an older one is migrated as it's read.
    public static Project Load(string directory, out int savedVersion) => Parse(ReadBytes(directory, out _), out savedVersion);

    // project.json as it is on disk. It is opened the way every read outside project.lock must be,
    // sharing read, write and delete, so a reader never stops another program from saving: Save
    // renames its new file over this one while it is open. stamp is the file's size and time as it
    // was opened.
    public static byte[] ReadBytes(string directory, out ProjectFileStamp stamp)
    {
        var path = Path.Combine(directory, ProjectFileName);
        if (!File.Exists(path))
        {
            throw new ZeyoCutException($"no {ProjectFileName} in '{directory}'");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stamp = new ProjectFileStamp(stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    // The size and time of project.json, without opening it; default when there is none.
    public static ProjectFileStamp Stamp(string directory)
    {
        var file = new FileInfo(Path.Combine(directory, ProjectFileName));
        return file.Exists ? new ProjectFileStamp(file.Length, file.LastWriteTimeUtc) : default;
    }

    // The project in project.json's bytes, updated from an older format. A byte order mark is
    // read the way File.ReadAllText reads it.
    public static Project Parse(byte[] bytes, out int savedVersion)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ProjectJson.Deserialize(ProjectMigrations.Migrate(reader.ReadToEnd(), out savedVersion));
    }

    // SHA-256 of the file's bytes: whether project.json changed is decided by this, not by revision.
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // Returns the bytes written.
    public static byte[] Save(Project project, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ProjectFileName);
        var temp = path + ".tmp";
        var bytes = Utf8NoBom.GetBytes(ProjectJson.Serialize(project));

        // On disk before the rename, so a crash or power cut leaves the old file or the new one.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        // A virus scanner or the search indexer can hold project.json for a moment; try again briefly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !RenameOver(temp, path))
                {
                    File.Move(temp, path, overwrite: true);
                }

                return bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < SaveAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    public static void EnsureFolders(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, AssetsFolder));
        Directory.CreateDirectory(Path.Combine(directory, RendersFolder));
        Directory.CreateDirectory(Path.Combine(directory, ExportsFolder));
    }

    // An asset's file is always assets/<name> inside its project (or rig or template) folder. A
    // project.json from someone else can't point it anywhere else, to read or write files there.
    public static bool IsAssetFile(string? file) => IsFileIn(AssetsFolder, file);

    // True when file is folder/<name>: one plain file name, with no further folders, no '.' or '..',
    // no drive and no characters a file name can't have.
    public static bool IsFileIn(string folder, string? file)
    {
        var parts = (file ?? "").Replace('\\', '/').Split('/');
        return parts.Length == 2 && parts[0] == folder && IsPlainFileName(parts[1]);
    }

    public static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(['/', '\\', ':']) < 0;

    public static string AssetPath(string directory, Asset asset) =>
        IsAssetFile(asset.File)
            ? Path.GetFullPath(Path.Combine(directory, asset.File.Replace('/', Path.DirectorySeparatorChar)))
            : throw new ZeyoCutException($"asset '{asset.Id}': file '{asset.File}' must be a file in the {AssetsFolder} folder");

    // Renames from over to with POSIX semantics, which Windows allows while another process has the
    // file open if it shares delete (File.Move's rename doesn't). False when that didn't work, such
    // as on a file system without them (FAT, some network folders): File.Move then tries.
    private static bool RenameOver(string from, string to)
    {
        using var handle = CreateFile(from, DeleteAccess | SynchronizeAccess, (uint)(FileShare.Read | FileShare.Write | FileShare.Delete), IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        // FILE_RENAME_INFO: flags, a root directory handle, the name's length in bytes, the name.
        var name = Encoding.Unicode.GetBytes(to);
        var lengthOffset = IntPtr.Size * 2;
        var nameOffset = lengthOffset + sizeof(uint);
        var info = new byte[nameOffset + name.Length + sizeof(char)];
        BitConverter.TryWriteBytes(info.AsSpan(0), RenameReplaceIfExists | RenamePosixSemantics);
        BitConverter.TryWriteBytes(info.AsSpan(lengthOffset), (uint)name.Length);
        name.CopyTo(info, nameOffset);
        return SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)info.Length);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, byte[] info, uint size);
}
