// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Input;
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
