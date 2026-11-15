// SPDX-License-Identifier: MIT

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
        TestWindow.Pump();
        Assert.False(pack.IsEnabled);
    });

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
