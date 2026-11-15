// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor.Tests;

// The editor's window itself (TestWindow), with projects saved as the server or a hand edit leaves
// them.
public class WindowTests
{
    // A project folder with a picture in its media, its project.json then changed as a hand edit
    // might.
    private static string Saved(string folder, string name, Action<JsonObject>? change = null)
    {
        var directory = Path.Combine(folder, name);
        var session = ProjectSession.Create(directory, TestFiles.Project());
        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 8, 8);
        session.Mutate("import_asset logo.png", p =>
        {
            p.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 8, Height = 8 });
            return 0;
        });
        if (change is not null)
        {
            var file = Path.Combine(directory, ProjectStore.ProjectFileName);
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            change(json);
            File.WriteAllText(file, json.ToJsonString());
        }

        return directory;
    }

    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;

    // Lets the window finish the saves it started, and those of the commands that waited for them;
    // its status line says "Saving…" until then.
    private static void WaitForSaves(MainWindow window)
    {
        for (var i = 0; i < 1000; i++)
        {
            TestWindow.Pump();
            if (Text(window, "SaveStatus") != "Saving…")
            {
                return;
            }

            TestWindow.Pump(10);
        }
    }

    // The inspector's text box for a field.
    private static TextBox Field(MainWindow window, string label)
    {
        TestWindow.Pump();
        var list = (ItemsControl)window.FindName("InspectorList");
        var field = list.Items.OfType<InspectorField>().Single(f => f.Label == label);
        return Descendant<TextBox>((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(field))!;
    }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // The editor's own start-up (App.OnStartup) doesn't run in the tests: it would show a window on
    // screen with the user's settings, and take the test runner's arguments for a project to open.
    [Fact]
    public void TheTestsWindowIsTheOnlyOne() => TestWindow.Run("window-only", (window, folder) =>
        Assert.Same(window, Assert.Single(Application.Current.Windows.OfType<Window>())));

    [Fact]
    public void AProjectTheWindowCantShowLeavesItUsable() => TestWindow.Run("window-cant-show", (window, folder) =>
    {
        window.OpenProject(Saved(folder, "broken", p => p["fps"] = 0));
        window.OpenProject(Saved(folder, "good"));
        Assert.Equal("good — ZeyoCut Editor", window.Title);
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));

        // The media list takes a click: the picture is shown in the inspector.
        ((ListBox)window.FindName("MediaList")).SelectedIndex = 0;
        TestWindow.Pump();
        Assert.Equal("IMAGE ASSET", Text(window, "InspectorTitle"));
    });

    [Fact]
    public void AProjectWhoseFrameRateIsZeroOpensWithTheProblemNamed() => TestWindow.Run("window-fps-0", (window, folder) =>
    {
        window.OpenProject(Saved(folder, "broken", p => p["fps"] = 0));
        Assert.Equal("broken — ZeyoCut Editor", window.Title);
        Assert.Equal("project.json has a problem, and edits aren't saved until it's fixed: fps must be between 1 and 120 (got 0)", Text(window, "SaveStatus"));
        Assert.Equal("00:00:00:00", Text(window, "TimecodeText"));

        // It has one frame to step through.
        TestWindow.Press(window, Key.Right);
        TestWindow.Press(window, Key.End);
        TestWindow.Press(window, Key.Left);
        Assert.Equal("00:00:00:00", Text(window, "TimecodeText"));
    });

    [Fact]
    public void AMatteFinishedWithoutASaveIsShownWhilePaused() => TestWindow.Run("window-matte", (window, folder) =>
    {
        var directory = Path.Combine(folder, "matte");
        var matte = TestFiles.PendingMatte(directory);
        window.OpenProject(directory);

        byte[] Shown()
        {
            var bitmap = (WriteableBitmap)((Image)window.FindName("PreviewImage")).Source;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        bool Drawn() => Text(window, "RenderText").Length > 0;
        for (var i = 0; i < 500 && !Drawn(); i++)
        {
            TestWindow.Pump(10);
        }

        Assert.True(Drawn());
        Assert.False(TestFiles.OnlyBackground(Shown()));

        // The server computes the matte; the paused frame shows it within a few seconds.
        ZeyoCut.Render.Pixels.SaveGray(new byte[64 * 64], 64, 64, matte);
        for (var i = 0; i < 50 && !TestFiles.OnlyBackground(Shown()); i++)
        {
            TestWindow.Pump(100);
        }

        Assert.True(TestFiles.OnlyBackground(Shown()));

        // Nothing changed since: it isn't asked for again.
        var shown = Text(window, "RenderText");
        TestWindow.Pump(2500);
        Assert.Equal(shown, Text(window, "RenderText"));
    });

    [Fact]
    public void TheProjectsLengthIsTypedInTheInspectorAndTheLanesPastItAreShaded() => TestWindow.Run("window-duration", (window, folder) =>
    {
        // A picture from 0 to 2 s in a project 1 s long.
        var project = TestFiles.Project(duration: 1);
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 8, Height = 8 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Start = 0, End = 2 });
        var directory = Path.Combine(folder, "short");
        ProjectSession.Create(directory, project);
        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 8, 8);
        window.OpenProject(directory);

        // Zoomed in, the timeline scrolled to show the second before the end (1 s) and after it: the
        // picture's color just before the end, and past it.
        var timeline = (TimelineView)window.FindName("Timeline");
        TestWindow.Pump();
        TestWindow.Press(window, Key.Add);
        (Color Before, Color Past) Picture()
        {
            var span = (timeline.ActualWidth - TimelineView.HeaderWidth) / timeline.PixelsPerSecond;
            timeline.HorizontalOffset = (1 - span / 3) * timeline.PixelsPerSecond;
            TestWindow.Pump();
            var bitmap = new RenderTargetBitmap((int)timeline.ActualWidth, (int)timeline.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(timeline);
            Color At(double time)
            {
                var pixel = new byte[4];
                var x = (int)(TimelineView.HeaderWidth + time * timeline.PixelsPerSecond - timeline.HorizontalOffset);
                bitmap.CopyPixels(new Int32Rect(x, (int)TimelineView.RulerHeight + 20, 1, 1), pixel, 4, 0);
                return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            }

            return (At(1 - span / 6), At(1 + span / 3));
        }

        // Past the end, the lane is shaded as the ruler is.
        var (before, past) = Picture();
        Assert.True(past.R + past.G + past.B < before.R + before.G + before.B, $"past the end {past}, before it {before}");

        // Two seconds typed in the project's Duration: the picture isn't past the end anymore.
        var box = Field(window, "Duration");
        Assert.Equal("1", box.Text);
        box.Text = "2";
        TestWindow.Press(box, Key.Return);
        WaitForSaves(window);
        Assert.Equal(2, ProjectStore.Load(directory).Duration);
        Assert.Equal("duration 1s → 2s", Text(window, "MessageText"));
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));
        (before, past) = Picture();
        Assert.Equal(before, past);

        // A length that can't be is named, and nothing is saved.
        box = Field(window, "Duration");
        box.Text = "0";
        TestWindow.Press(box, Key.Return);
        Assert.Equal("Duration must be more than 0 (got 0)", Text(window, "MessageText"));
        Assert.Equal(2, ProjectStore.Load(directory).Duration);
    });

    [Fact]
    public void TheOnceASecondCheckLeavesTheMenusAlone() => TestWindow.Run("window-poll", (window, folder) =>
    {
        // Two titles at different levels that never show at once: the lanes can be packed.
        var first = Saved(folder, "first", p =>
        {
            var layers = p["layers"]!.AsArray();
            layers.Add(new JsonObject { ["type"] = "text", ["id"] = "a", ["text"] = "A", ["start"] = 0, ["end"] = 1, ["zIndex"] = 0 });
            layers.Add(new JsonObject { ["type"] = "text", ["id"] = "b", ["text"] = "B", ["start"] = 1, ["end"] = 2, ["zIndex"] = 1 });
        });
        var second = Saved(folder, "second");
        window.OpenProject(second);
        window.OpenProject(first);
        var pack = (Button)window.FindName("PackButton");
        Assert.True(pack.IsEnabled);

        var recent = (MenuItem)window.FindName("RecentMenu");
        var items = recent.Items.Cast<MenuItem>().ToList();
        Assert.Equal([first, second], items.Select(i => (string)i.Tag));

        // Checks for another program's changes don't build the menu again.
        TestWindow.Pump(1500);
        Assert.Equal(items, recent.Items.Cast<MenuItem>());

        // Opened, it leaves out a project that is gone.
        Directory.Delete(second, recursive: true);
        recent.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, recent));
        Assert.Equal([first], recent.Items.Cast<MenuItem>().Select(i => (string)i.Tag));

        // Packed, there's nothing more to pack.
        pack.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        WaitForSaves(window);
        Assert.False(pack.IsEnabled);
    });

    [Fact]
    public void ClosingAProjectDoesntWaitForTheFrameBeingDrawn() => TestWindow.Run("window-close-drawing", (window, folder) =>
    {
        // 300 titles blurred by 100 px: their frame takes seconds to draw.
        var project = TestFiles.Project(1920, 1080);
        for (var i = 0; i < 300; i++)
        {
            var title = ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = $"t{i}", Text = "Slow to draw", FontSize = 400 });
            title.Blur = new BlurEffect { X = new(100), Y = new(100) };
        }

        var directory = Path.Combine(folder, "slow");
        ProjectSession.Create(directory, project);
        window.OpenProject(directory);
        TestWindow.Pump(300);
        Assert.Equal("", Text(window, "RenderText"));

        var watch = Stopwatch.StartNew();
        ((MenuItem)window.FindName("CloseItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"closing took {watch.ElapsedMilliseconds} ms");
        Assert.Equal("ZeyoCut Editor", window.Title);
    });

    // A project with a title from 0 to 2 s, open with the title selected.
    private static string WithTitle(MainWindow window, string folder)
    {
        var project = TestFiles.Project();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 0, End = 2 });
        var directory = Path.Combine(folder, "titled");
        ProjectSession.Create(directory, project);
        window.OpenProject(directory);
        window.SelectLayer("title");
        return directory;
    }

    private static TextLayer Title(string directory) => (TextLayer)ProjectStore.Load(directory).FindLayer("title")!;

    // A title from 0 to 1 s in a project 2 s long, open with the title selected: the timeline, and the
    // point in the middle of the title's lane at a time.
    private static (string Directory, TimelineView Timeline, Func<double, Point> At) ShortTitle(MainWindow window, string folder)
    {
        var project = TestFiles.Project();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", Start = 0, End = 1 });
        var directory = Path.Combine(folder, "short-title");
        ProjectSession.Create(directory, project);
        window.OpenProject(directory);
        window.SelectLayer("title");
        TestWindow.Pump();
        var timeline = (TimelineView)window.FindName("Timeline");
        Assert.Contains(timeline.Lanes[0].Clips, c => c.LayerId == "title");
        return (directory, timeline, time => new Point(
            TimelineView.HeaderWidth + time * timeline.PixelsPerSecond - timeline.HorizontalOffset,
            TimelineView.RulerHeight + TimelineView.LaneHeight(timeline.Lanes[0]) / 2));
    }

    // A clip being dragged when another program moves it is let go of: letting go of the mouse then
    // saves nothing, and the other program's change stays.
    [Fact]
    public void AClipBeingDraggedIsLetGoWhenAnotherProgramMovesIt() => TestWindow.Run("window-drag-overtaken", (window, folder) =>
    {
        var (directory, timeline, at) = ShortTitle(window, folder);
        timeline.Press(at(0.5));
        timeline.MoveTo(at(1), pressed: true);
        Assert.True(timeline.Dragging);

        ProjectSession.Open(directory).Mutate("update_layer title", p =>
        {
            var title = p.FindLayer("title")!;
            (title.Start, title.End) = (0.25, 1.25);
            return 0;
        });
        for (var i = 0; i < 300 && timeline.Dragging; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.False(timeline.Dragging);
        Assert.StartsWith("Another program saved: ", Text(window, "MessageText"));
        timeline.MoveTo(at(1.5), pressed: true);
        timeline.Release();
        WaitForSaves(window);
        Assert.Equal((0.25, 2), (Title(directory).Start, ProjectStore.Load(directory).Revision));
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));
    });

    // A clip let go while another program is saving stays drawn as it was while dragged (faded where
    // it was, outlined where it goes) until its edit is saved after that, then is drawn where it went.
    [Fact]
    public void ADroppedClipIsDrawnWhereItWentWhileItsBeingSaved() => TestWindow.Run("window-drag-landing", (window, folder) =>
    {
        var (directory, timeline, at) = ShortTitle(window, folder);
        Color Pixel(double time)
        {
            var bitmap = new RenderTargetBitmap((int)timeline.ActualWidth, (int)timeline.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(timeline);
            var point = at(time);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }

        var (clip, empty) = (Pixel(0.75), Pixel(1.75));
        Assert.NotEqual(clip, empty);

        // Dragged half a second later, to 0.5 to 1.5 s, while another program saves.
        var held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
        timeline.Press(at(0.5));
        timeline.MoveTo(at(1), pressed: true);
        var (goes, was) = (Pixel(1.25), Pixel(0.25));
        Assert.NotEqual(empty, goes);
        Assert.NotEqual(clip, was);

        timeline.Release();
        TestWindow.Pump(300);
        Assert.Equal("Saving…", Text(window, "SaveStatus"));
        Assert.Equal((goes, was), (Pixel(1.25), Pixel(0.25)));

        held.Dispose();
        WaitForSaves(window);
        Assert.Equal(0.5, Title(directory).Start);
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));
        Assert.Equal((clip, empty), (Pixel(1.25), Pixel(0.25)));
    });

    [Fact]
    public void AFieldIsSavedOnEnterAndWhenTheKeyboardLeavesIt() => TestWindow.Run("window-fields", (window, folder) =>
    {
        var directory = WithTitle(window, folder);
        Assert.Equal("TEXT LAYER", Text(window, "InspectorTitle"));

        var start = Field(window, "Start");
        start.Text = "0.5";
        TestWindow.Press(start, Key.Return);
        WaitForSaves(window);
        Assert.Equal(0.5, Title(directory).Start);
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));

        // Typed into Length, then the keyboard moves on to Start.
        var length = Field(window, "Length");
        Assert.True(length.Focus());
        length.Text = "1";
        Assert.True(Field(window, "Start").Focus());
        TestWindow.Pump();
        WaitForSaves(window);
        Assert.Equal(1.5, Title(directory).End);
        Assert.Equal("Saved · revision 3", Text(window, "SaveStatus"));
        Assert.Equal("1", Field(window, "Length").Text);
    });

    [Fact]
    public void ATypedValueIsKeptWhenAnotherProgramChangedTheLayerFirst() => TestWindow.Run("window-kept", (window, folder) =>
    {
        var directory = WithTitle(window, folder);
        var start = Field(window, "Start");
        Assert.True(start.Focus());
        start.Text = "1";

        // Another program moves the title meanwhile. The window takes it, but the field keeps what
        // was typed.
        ProjectSession.Open(directory).Mutate("move_layer title", p => TimelineEditor.MoveLayer(p, "title", 0.25));
        TestWindow.Pump(1500);
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));
        Assert.Same(start, Field(window, "Start"));
        Assert.Equal("1", start.Text);

        // Enter doesn't save it over their change: the window shows theirs, with what was typed
        // back in the field.
        TestWindow.Press(start, Key.Return);
        WaitForSaves(window);
        Assert.Equal(0.25, Title(directory).Start);
        Assert.StartsWith("Your change wasn't saved: another program changed 'title' first", Text(window, "SaveStatus"));
        start = Field(window, "Start");
        Assert.Equal("1", start.Text);
        Assert.Equal("What you typed is still in the field: Enter saves it over the other program's change, Escape drops it", Text(window, "MessageText"));

        // Enter again saves it.
        TestWindow.Press(start, Key.Return);
        WaitForSaves(window);
        Assert.Equal(1, Title(directory).Start);
        Assert.Equal("Saved · revision 3", Text(window, "SaveStatus"));
    });

    [Fact]
    public void AnEditWaitingForAnotherProgramsSaveDoesntHoldUpTheWindow() => TestWindow.Run("window-save-waits", (window, folder) =>
    {
        var directory = WithTitle(window, folder);

        // Another program is saving: it has project.lock open for a while.
        var held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
        var start = Field(window, "Start");
        start.Text = "0.5";
        var watch = Stopwatch.StartNew();
        TestWindow.Press(start, Key.Return);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Enter took {watch.ElapsedMilliseconds} ms");
        TestWindow.Pump(300);
        Assert.Equal("Saving…", Text(window, "SaveStatus"));
        Assert.Equal(0, Title(directory).Start);

        // Once it's done, the edit is saved after it.
        held.Dispose();
        WaitForSaves(window);
        Assert.Equal(0.5, Title(directory).Start);
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));
        Assert.Equal("0.5", Field(window, "Start").Text);

        // Undo goes the same way.
        ((MenuItem)window.FindName("UndoItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WaitForSaves(window);
        Assert.Equal(0, Title(directory).Start);
        Assert.Equal("Saved · revision 3", Text(window, "SaveStatus"));
        Assert.Equal("0", Field(window, "Start").Text);
    });

    // A file dropped on the window is imported as an edit saved like the others: while another
    // program is saving, the window goes on working, and the file is added after it. (WPF makes a
    // drop's event arguments only for a real drag.)
    [Fact]
    public void AnImportWaitingForAnotherProgramsSaveDoesntHoldUpTheWindow() => TestWindow.Run("window-import-waits", (window, folder) =>
    {
        var directory = Saved(folder, "project");
        window.OpenProject(directory);
        var picture = TestFiles.Png(Path.Combine(folder, "picture.png"), 8, 8);

        var held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
        var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [new DataObject(DataFormats.FileDrop, new[] { picture }), DragDropKeyStates.None, DragDropEffects.Copy, window, new Point()], null)!;
        drop.RoutedEvent = DragDrop.DropEvent;
        window.RaiseEvent(drop);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 100 && Text(window, "SaveStatus") != "Saving…"; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.True(watch.ElapsedMilliseconds < 1500, $"the import held up the window for {watch.ElapsedMilliseconds} ms");
        Assert.Equal("Saving…", Text(window, "SaveStatus"));
        Assert.Null(ProjectStore.Load(directory).FindAsset("picture"));

        held.Dispose();
        WaitForSaves(window);
        Assert.NotNull(ProjectStore.Load(directory).FindAsset("picture"));
        Assert.Equal("Saved · revision 3", Text(window, "SaveStatus"));
        Assert.Equal("Imported 'picture'", Text(window, "MessageText"));
    });

    // Space plays from the playhead and pauses where it is; played to the end (2 s), it stops on the
    // last frame.
    [Fact]
    public void SpacePlaysAndPausesAndPlayingStopsAtTheEnd() => TestWindow.Run("window-playback", (window, folder) =>
    {
        WithTitle(window, folder);
        var play = (Button)window.FindName("PlayButton");
        string Frame() => Text(window, "FrameText");
        void PumpWhile(Func<bool> condition)
        {
            for (var i = 0; i < 500 && condition(); i++)
            {
                TestWindow.Pump(10);
            }
        }

        Assert.EndsWith("frame 0 of 59", Frame());
        TestWindow.Press(window, Key.Space);
        Assert.Equal("Pause (Space)", play.ToolTip);
        PumpWhile(() => Frame().EndsWith("frame 0 of 59", StringComparison.Ordinal));
        TestWindow.Press(window, Key.Space);
        Assert.Equal("Play (Space)", play.ToolTip);
        var paused = Frame();
        Assert.DoesNotContain("frame 0 of", paused);
        TestWindow.Pump(300);
        Assert.Equal(paused, Frame());

        TestWindow.Press(window, Key.Space);
        PumpWhile(() => (string)play.ToolTip == "Pause (Space)");
        Assert.Equal("Play (Space)", play.ToolTip);
        Assert.EndsWith("frame 59 of 59", Frame());
        Assert.Equal("00:00:01:29", Text(window, "TimecodeText"));
    });

    // Split, Delete, Pack lanes, a keyframe's diamond and Export act on the project as shown: given
    // while an edit is being saved, they wait for it, as they did when saves held up the window.
    [Fact]
    public void CommandsGivenWhileAnEditIsBeingSavedWaitForIt() => TestWindow.Run("window-commands-wait", (window, folder) =>
    {
        var directory = WithTitle(window, folder);
        var list = (ItemsControl)window.FindName("InspectorList");
        Button Diamond()
        {
            TestWindow.Pump();
            var row = list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<InspectorField>().Single(f => f.Label == "X"));
            return Descendant<Button>((DependencyObject)row)!;
        }

        // The X diamond clicked twice while its keyframe is being saved: the second click takes it
        // away again.
        var held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
        Diamond().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Diamond().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("Saving…", Text(window, "SaveStatus"));
        held.Dispose();
        WaitForSaves(window);
        Assert.Empty(Title(directory).X.Keyframes);
        Assert.Equal("Saved · revision 3", Text(window, "SaveStatus"));

        // Delete pressed twice: the title is deleted once.
        held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
        TestWindow.Press(window, Key.Delete);
        TestWindow.Press(window, Key.Delete);
        held.Dispose();
        WaitForSaves(window);
        Assert.Null(ProjectStore.Load(directory).FindLayer("title"));
        Assert.Equal("Saved · revision 4", Text(window, "SaveStatus"));
        Assert.StartsWith("removed: title", Text(window, "MessageText"));
    });

    [Fact]
    public void ClosingTheWindowWaitsForTheEditBeingSaved()
    {
        string? directory = null;
        TestWindow.Run("window-close-saving", (window, folder) =>
        {
            directory = WithTitle(window, folder);
            var held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
            var start = Field(window, "Start");
            start.Text = "0.5";
            TestWindow.Press(start, Key.Return);

            // The other program finishes once the window is closing.
            _ = Task.Delay(500).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);
        });

        Assert.Equal(0.5, Title(directory!).Start);
    }

    // Closed while an edit waits for another program's save, the window is hidden at once, and
    // closes once the edit is saved.
    [Fact]
    public void ClosingTheWindowWhileAnEditIsBeingSavedDoesntHoldItUp()
    {
        string? directory = null;
        TestWindow.Run("window-close-hidden", (window, folder) =>
        {
            directory = WithTitle(window, folder);
            var held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
            var start = Field(window, "Start");
            start.Text = "0.5";
            TestWindow.Press(start, Key.Return);
            _ = Task.Delay(2000).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

            var watch = Stopwatch.StartNew();
            window.Close();
            Assert.True(watch.ElapsedMilliseconds < 1000, $"closing held up the window for {watch.ElapsedMilliseconds} ms");
            Assert.False(window.IsVisible);
            Assert.Equal(0, Title(directory).Start);
        });

        Assert.Equal(0.5, Title(directory!).Start);
    }

    [Fact]
    public void ALongClipIsDrawnAtTheClosestZoom() => TestWindow.Run("window-long-clip", (window, folder) =>
    {
        // Two hours (the longest a project can be) at 48 px a frame is 10 million px, past what WPF
        // lays text out in.
        var project = TestFiles.Project(duration: 7200);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "A talk", Start = 0, End = 7200 });
        var directory = Path.Combine(folder, "talk");
        ProjectSession.Create(directory, project);
        window.OpenProject(directory);

        var timeline = (TimelineView)window.FindName("Timeline");
        for (var i = 0; i < 40 && timeline.PixelsPerSecond < 30 * 48; i++)
        {
            TestWindow.Press(window, Key.Add);
        }

        Assert.Equal(30 * 48, timeline.PixelsPerSecond);

        // From its start, from its end and from inside.
        foreach (var offset in new[] { 0, timeline.MaxHorizontalOffset, timeline.MaxHorizontalOffset / 3 })
        {
            timeline.HorizontalOffset = offset;
            TestWindow.Pump();
        }
    });
}
