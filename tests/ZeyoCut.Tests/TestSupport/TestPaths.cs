// SPDX-License-Identifier: MIT

namespace ZeyoCut.Tests.TestSupport;

public static class TestPaths
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "TestOutput");

    // The repository's root folder: the first folder up from the test output that holds ZeyoCut.slnx.
    public static string RepositoryRoot
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                if (File.Exists(Path.Combine(folder.FullName, "ZeyoCut.slnx")))
                {
                    return folder.FullName;
                }
            }

            throw new InvalidOperationException($"ZeyoCut.slnx isn't in any folder above {AppContext.BaseDirectory}");
        }
    }

    public static string NewDirectory(string name)
    {
        var directory = Path.Combine(Root, name);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }
}
