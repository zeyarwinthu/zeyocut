// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;

namespace ZeyoCut.Editor;

// ZeyoCut.Editor.exe [project folder or its project.json]
public partial class App : Application
{
    // The file in the log folder that holds the details of errors the editor didn't expect.
    public const string ErrorLogName = "editor-errors.log";

    // The log folder, once the settings are read; before that an error's details are shown.
    private string? _logDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Started for a project another editor window has open (as open_in_editor does when it's
        // open already): that window comes to the front instead.
        if (e.Args is [var project, ..] && ShownElsewhere(project))
        {
            Shutdown();
            return;
        }

        var settings = Settings.Load();
        _logDirectory = settings.LogDirectory;
        var window = new MainWindow(settings, EditorSettings.DefaultPath);
        window.Show();
        if (e.Args is [var path, ..])
        {
            window.OpenProject(path);
        }
    }

    private static bool ShownElsewhere(string path)
    {
        try
        {
            return ProjectClaim.ShownElsewhere(ProjectList.FolderOf(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // The window says what's wrong with the path.
            return false;
        }
    }

    // An error nothing else caught is shown and the editor goes on: every edit is saved as it's made.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Describe(e.Exception, _logDirectory), "ZeyoCut Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    // What the editor says of an error it didn't expect, in plain words. Its details (where in the
    // code it happened) are added to editor-errors.log in the log folder, for a bug report, and
    // shown only when that file can't be written.
    public static string Describe(Exception error, string? logDirectory)
    {
        var said = $"Something went wrong in the editor: {error.Message}\n\nYour changes before it are saved in {ProjectStore.ProjectFileName}.";
        if (logDirectory is not null)
        {
            var log = Path.Combine(logDirectory, ErrorLogName);
            try
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(log, string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}{Environment.NewLine}"));
                return $"{said} The details are in {log}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        return $"{said}\n\nDetails:\n{error}";
    }
}
