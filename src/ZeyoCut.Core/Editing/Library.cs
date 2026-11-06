// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;

namespace ZeyoCut.Core.Editing;

public enum LibraryKind
{
    Rig,
    Action,
    Style,
    Template
}

// The library shared by all projects: rigs\<name>\rig.json, actions\<name>.json,
// styles\<name>.json, templates\<name>\template.json, and sound effects in sfx\.
public sealed partial class Library
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$")]
    private static partial Regex ValidName();

    private const int MoveAttempts = 5;

    public Library(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string SfxFolder => Path.Combine(Root, "sfx");

    public static string Plural(LibraryKind kind) => kind switch
    {
        LibraryKind.Rig => "rigs",
        LibraryKind.Action => "actions",
        LibraryKind.Style => "styles",
        _ => "templates",
    };

    public static string CheckName(string? name, LibraryKind kind)
    {
        var trimmed = name?.Trim() ?? "";
        if (!ValidName().IsMatch(trimmed))
        {
            throw new ZeyoCutException($"'{name}' is not a valid {EnumNames.ToName(kind)} name: use up to 64 letters, digits, spaces, '_' or '-', starting with a letter or digit");
        }

        return trimmed;
    }

    public string Folder(LibraryKind kind) => Path.Combine(Root, Plural(kind));

    // The item's own folder for rigs and templates; its json file for actions and styles.
    public string PathOf(LibraryKind kind, string name) => kind switch
    {
        LibraryKind.Rig or LibraryKind.Template => Path.Combine(Folder(kind), name),
        _ => Path.Combine(Folder(kind), name + ".json"),
    };

    public string FileOf(LibraryKind kind, string name) => kind switch
    {
        LibraryKind.Rig => Path.Combine(PathOf(kind, name), "rig.json"),
        LibraryKind.Template => Path.Combine(PathOf(kind, name), "template.json"),
        _ => PathOf(kind, name),
    };

    public List<string> Names(LibraryKind kind)
    {
        var folder = Folder(kind);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var names = kind is LibraryKind.Rig or LibraryKind.Template
            ? Directory.GetDirectories(folder).Where(d => File.Exists(FileOf(kind, Path.GetFileName(d)))).Select(Path.GetFileName)
            : Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension);
        return names.OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Exists(LibraryKind kind, string name) => File.Exists(FileOf(kind, name));

    public T Load<T>(LibraryKind kind, string? name) where T : class
    {
        var checkedName = CheckName(name, kind);
        var file = FileOf(kind, checkedName);
        if (!File.Exists(file))
        {
            var names = Names(kind);
            throw new ZeyoCutException($"no {EnumNames.ToName(kind)} named '{checkedName}' in the library; {(names.Count == 0 ? $"there are no {Plural(kind)} yet" : $"{Plural(kind)}: {string.Join(", ", names)}")}");
        }

        return AnalysisFiles.Load<T>(file)!;
    }

    // Writes an item. Folder items (rigs, templates) are built in a temporary folder by 'write' and
    // then moved into place, so a failed save leaves the old item as it was.
    public string Save(LibraryKind kind, string name, bool overwrite, Action<string> write)
    {
        if (Exists(kind, name) && !overwrite)
        {
            throw new ZeyoCutException($"the library already has a {EnumNames.ToName(kind)} named '{name}'; pass overwrite=true to replace it");
        }

        var target = PathOf(kind, name);
        Directory.CreateDirectory(Folder(kind));
        if (kind is LibraryKind.Action or LibraryKind.Style)
        {
            write(target);
            return target;
        }

        var temp = target + ".saving";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        Directory.CreateDirectory(temp);
        try
        {
            write(temp);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            // A virus scanner or the search indexer can hold a file just written for a moment; try again briefly.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(temp, target);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
        catch
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            throw;
        }

        return target;
    }

    // Copies an asset's file into an item folder's assets\ and returns the copy's metadata, with
    // File pointing into that folder.
    public static Asset CopyAsset(string projectDirectory, Asset asset, string itemFolder, HashSet<string> usedNames)
    {
        var source = ProjectStore.AssetPath(projectDirectory, asset);
        if (!File.Exists(source))
        {
            throw new ZeyoCutException($"asset '{asset.Id}' is missing its file: {source}");
        }

        var name = Path.GetFileName(source);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; !usedNames.Add(name); n++)
        {
            name = $"{stem}_{n}{extension}";
        }

        Directory.CreateDirectory(Path.Combine(itemFolder, ProjectStore.AssetsFolder));
        File.Copy(source, Path.Combine(itemFolder, ProjectStore.AssetsFolder, name), overwrite: true);
        var copy = ProjectJson.Copy(asset);
        copy.File = $"{ProjectStore.AssetsFolder}/{name}";
        copy.OriginalFile = null;
        return copy;
    }
}
