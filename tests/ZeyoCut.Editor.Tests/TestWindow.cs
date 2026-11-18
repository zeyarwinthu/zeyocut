// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ZeyoCut.Core;

namespace ZeyoCut.Editor.Tests;

// The editor's window for a test, shown off screen and never activated. One thread runs WPF for
// every such test, with the editor's styles, and a test runs on that thread inside the window's
// own message loop. Tests that use it must not run at once: keep them in one class.
public static class TestWindow
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    // Runs test with a new window whose settings, projects folder, log folder and import folder
    // (media) are in the test's folder, then closes it, which can first finish what it's doing hidden.
    // An error nothing in the window caught (one the editor would show in its error dialog) fails the
    // test too. The editor's settings file is there, as it is after the first run, unless firstRun.
    public static void Run(string name, Action<MainWindow, string> test, bool firstRun = false)
    {
        ExceptionDispatchInfo? failed = null;
        Ui.Value.Invoke(() =>
        {
            var folder = TestFiles.NewFolder(name);
            var unhandled = new List<Exception>();
            void Record(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                unhandled.Add(e.Exception);
                e.Handled = true;
            }

            Dispatcher.CurrentDispatcher.UnhandledException += Record;
            var closed = false;
            var settings = new Settings(null, Path.Combine(folder, "projects"), Path.Combine(folder, "logs"), ImportFolders: [Path.Combine(folder, "media")]);
            if (!firstRun)
            {
                File.WriteAllText(Path.Combine(folder, "editor.json"), "{}");
            }

            var window = new MainWindow(settings, Path.Combine(folder, "editor.json"))
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Width = 1400,
                Height = 900,
            };
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                Pump();
                test(window, folder);
                Pump();
            }
            catch (Exception ex)
            {
                failed = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }

                for (var i = 0; i < 2000 && !closed; i++)
                {
                    Pump(10);
                }

                Pump();
                Dispatcher.CurrentDispatcher.UnhandledException -= Record;
            }

            if (failed is null && unhandled.Count > 0)
            {
                failed = ExceptionDispatchInfo.Capture(unhandled[0]);
            }
        });
        failed?.Throw();
    }

    // Lets the window do what it has queued (layout, drawing, work posted to it), and goes on doing
    // that for milliseconds more.
    public static void Pump(int milliseconds = 0)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (milliseconds > 0)
            {
                Thread.Sleep(10);
            }
        }
        while (DateTime.UtcNow < until);
    }

    // A key pressed with no modifier held, as the window receives it.
    public static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }

        Pump();
    }

    private static Dispatcher Start()
    {
        var started = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            try
            {
                // The styles App.xaml holds, on an application that every test closes its own window
                // in. Not the editor's App: WPF runs its start-up once this thread's dispatcher starts,
                // and it would show a window with the user's settings.
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/ZeyoCut.Editor;component/Theme.xaml") },
                };
                started.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                started.SetException(ex);
                return;
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "editor window",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task.GetAwaiter().GetResult();
    }
}
