// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// A file the Relink view offers for a missing asset: a likely match found for it, or one you chose,
// with why it's offered (same size, chosen).
public sealed class RelinkCandidate(string path, string reason, ImageSource? picture) : INotifyPropertyChanged
{
    private bool _chosen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    public string Reason { get; } = reason;

    public ImageSource? Picture { get; } = picture;

    public bool Chosen
    {
        get => _chosen;
        set
        {
            if (_chosen != value)
            {
                _chosen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chosen)));
            }
        }
    }
}

// An asset whose file is missing, as the Relink view shows it: where its file was, what uses it,
// the files offered for it and which is chosen, and what happened to the search or the relink.
public sealed class MissingFile(Asset asset, int users) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = asset.Id;

    public AssetType Type { get; } = asset.Type;

    public string Name { get; } = System.IO.Path.GetFileName(asset.File.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string WasAt { get; } = $"Was at {asset.File.Replace('/', System.IO.Path.DirectorySeparatorChar)}";

    public string UsedBy { get; } = users switch
    {
        0 => "not used by a clip",
        1 => "used by 1 clip",
        _ => $"used by {users} clips",
    };

    public ObservableCollection<RelinkCandidate> Candidates { get; } = [];

    // What the view says about the search: looking, found, nothing found, or why a file was refused.
    public string Status { get; private set; } = "Looking for it…";

    public bool Found => Candidates.Count > 0;

    public bool Failed { get; private set; }

    public RelinkCandidate? Choice => Candidates.FirstOrDefault(c => c.Chosen);

    // Offers a file, chosen when nothing else is or when you picked it; a file already offered is
    // chosen instead of offered twice.
    public void Offer(RelinkCandidate candidate, bool choose)
    {
        var existing = Candidates.FirstOrDefault(c => string.Equals(c.Path, candidate.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Candidates.Add(candidate);
            existing = candidate;
        }

        if (choose || Choice is null)
        {
            Choose(existing);
        }

        Show(choose ? "Relinks to the file you chose" : Candidates.Count == 1 ? "Found a likely match" : $"Found {Candidates.Count} likely matches");
    }

    public void Choose(RelinkCandidate? candidate)
    {
        foreach (var other in Candidates)
        {
            other.Chosen = ReferenceEquals(other, candidate);
        }

        Changed();
    }

    // The search of a place is done: says so when nothing was found.
    public void Searched(string where)
    {
        if (Candidates.Count == 0 && !Failed)
        {
            Show($"Nothing of that name and kind in {where}");
        }
    }

    public void Show(string status, bool failed = false)
    {
        Status = status;
        Failed = failed;
        Changed();
    }

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

// What the Relink view finds out: the assets whose files are missing, where it looks for them first,
// and a small picture of an image file it offers.
public static class Relinking
{
    // The assets whose file isn't in the project's assets folder.
    public static List<Asset> Missing(Project project, string directory) =>
        project.Assets.Where(a => !ProjectStore.IsAssetFile(a.File) || !File.Exists(ProjectStore.AssetPath(directory, a))).ToList();

    // The project folder, then the import folders.
    public static IReadOnlyList<string> Folders(Settings settings, string directory) =>
        [directory, .. settings.ImportFolderList.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))];

    // An image file's picture, 128 pixels across, for the card that offers it; null for other files or
    // one that can't be read.
    public static ImageSource? Picture(string path, AssetType type)
    {
        if (type != AssetType.Image)
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.DecodePixelWidth = 128;
            picture.StreamSource = stream;
            picture.EndInit();
            picture.Freeze();
            return picture;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // The file filter of the Choose file dialog for an asset's kind.
    public static string Filter(AssetType type)
    {
        var extensions = (type == AssetType.Video ? AssetImporter.VideoExtensions : AssetImporter.SupportedExtensions)
            .Split(", ").Where(e => type == AssetType.Video || AssetImporter.DetectType("x" + e) == type);
        var name = type switch
        {
            AssetType.Image => "Images",
            AssetType.Audio => "Audio",
            AssetType.Video => "Video",
            AssetType.Lut => "3D LUTs",
            _ => "Fonts",
        };
        return $"{name}|{string.Join(";", extensions.Select(e => "*" + e))}|All files|*.*";
    }
}
