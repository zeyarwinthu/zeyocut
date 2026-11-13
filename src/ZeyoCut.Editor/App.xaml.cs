// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Threading;
using ZeyoCut.Core;

namespace ZeyoCut.Editor;

// ZeyoCut.Editor.exe [project folder or its project.json]
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        var window = new MainWindow(Settings.Load(), EditorSettings.DefaultPath);
        window.Show();
        if (e.Args is [var path, ..])
        {
            window.OpenProject(path);
        }
    }

    // An error nothing else caught is shown and the editor goes on: every edit is saved as it's made.
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "ZeyoCut Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
