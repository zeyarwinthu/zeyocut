// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZeyoCut.Core;
using ZeyoCut.Core.Animation;
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

    private static Visibility Shown(MainWindow window, string name) => ((UIElement)window.FindName(name)).Visibility;

    private static void Click(MainWindow window, string name) =>
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    // The start page's recent projects, once it has listed as many.
    private static List<string> Recent(MainWindow window, int count)
    {
        var list = (ItemsControl)window.FindName("RecentList");
        for (var i = 0; i < 500 && list.Items.Count != count; i++)
        {
            TestWindow.Pump(10);
        }

        return list.Items.Cast<ProjectEntry>().Select(p => p.Directory).ToList();
    }

    // Home's projects after the recent ones, once it has listed as many.
    private static List<string> Others(MainWindow window, int count)
    {
        var list = (ItemsControl)window.FindName("ProjectsList");
        for (var i = 0; i < 500 && list.Items.Count != count; i++)
        {
            TestWindow.Pump(10);
        }

        return list.Items.Cast<ProjectEntry>().Select(p => p.Name).ToList();
    }

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

    // The Captions grid's boxes: each row's start and end, and its cells.
    private static List<TextBox> CaptionBoxes(MainWindow window)
    {
        TestWindow.Pump();
        return Descendants<TextBox>((ItemsControl)window.FindName("CaptionRows")).ToList();
    }

    // A row's start or end; row 0 is a new row's.
    private static TextBox CaptionTime(MainWindow window, int row, string part) =>
        CaptionBoxes(window).Single(b => b.Tag is CaptionRow r && r.Number == row && System.Windows.Automation.AutomationProperties.GetName(b) == part);

    private static TextBox CaptionCell(MainWindow window, int row, string track) =>
        CaptionBoxes(window).Single(b => b.Tag is CaptionCell c && c.Row.Number == row && c.Column.GroupId == track);

    // The inspector's text box for a field, also one of a row's parts.
    private static TextBox Field(MainWindow window, string label)
    {
        TestWindow.Pump();
        return Descendants<TextBox>((ItemsControl)window.FindName("InspectorList")).Single(b => b.DataContext is InspectorField field && field.Label == label);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }

    // Clicks one of the inspector's tabs.
    private static void Tab(MainWindow window, string name)
    {
        var tab = ((Panel)window.FindName("InspectorTabs")).Children.OfType<RadioButton>().Single(t => (string)t.Content == name);
        tab.IsChecked = true;
        tab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        TestWindow.Pump();
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
        Assert.Equal(("IMAGE", "logo"), (Text(window, "InspectorKind"), Text(window, "InspectorName")));
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
    public void ATimeTypedInTheCaptionsGridMovesItsRowInEveryLanguageAsOneStep() => TestWindow.Run("window-captions-time", (window, folder) =>
    {
        var directory = TestFiles.Example("comic-panels", folder);
        window.OpenProject(directory);
        Assert.Equal(Command.Captions, window.RunKey(Key.D5, ModifierKeys.Control, inField: false));
        Assert.Equal(Visibility.Visible, Shown(window, "CaptionsPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "Workspace"));
        string[] row = ["captions_en_2", "captions_ja_2", "captions_my_2"];

        // The second row's start, typed in seconds.
        var start = CaptionTime(window, 2, "Start");
        Assert.Equal("00:03.40", start.Text);
        start.Text = "3.1";
        TestWindow.Press(start, Key.Return);
        WaitForSaves(window);
        var project = ProjectStore.Load(directory);
        Assert.Equal([(3.1, 5.7), (3.1, 5.7), (3.1, 5.7)], row.Select(id => (project.FindLayer(id)!.Start, project.FindLayer(id)!.End!.Value)));
        Assert.StartsWith("trimmed 'captions_en_2': now 3.1–5.7s (was 3.4–5.7s); trimmed 'captions_ja_2'", Text(window, "MessageText"));
        Assert.Equal("00:03.10", CaptionTime(window, 2, "Start").Text);

        // One step: undo puts it back in every language.
        Click(window, "UndoButton");
        WaitForSaves(window);
        project = ProjectStore.Load(directory);
        Assert.Equal([3.4, 3.4, 3.4], row.Select(id => project.FindLayer(id)!.Start));
        Assert.Equal("00:03.40", CaptionTime(window, 2, "Start").Text);
    });

    // tora-jump open in the Animate view with the tora group selected, and its keyframe view.
    private static (string Directory, KeyframeView Keys) Animate(MainWindow window, string folder)
    {
        var directory = TestFiles.Example("tora-jump", folder);
        window.OpenProject(directory);
        Assert.Equal(Command.Animate, window.RunKey(Key.D3, ModifierKeys.Control, inField: false));
        window.SelectLayer("tora");
        TestWindow.Pump();
        return (directory, (KeyframeView)window.FindName("Keyframes"));
    }

    private static Keyframe YKey(Project project, string layer, double time) =>
        ((VisualLayer)project.FindLayer(layer)!).Y.Keyframes.Single(k => Math.Abs(k.Time - time) < 1e-9);

    // Dragging a key in the dope sheet retimes it by whole frames with its value and easing, as one
    // step; in the graph, dragging it up changes its value and keeps its time.
    [Fact]
    public void RetimingAKeyframeKeepsItsValueAndEasingAndUndoesInOneStep() => TestWindow.Run("window-animate-retime", (window, folder) =>
    {
        var (directory, keys) = Animate(window, folder);
        Assert.Equal(Visibility.Visible, Shown(window, "AnimatePage"));
        Assert.Equal(["tora", "tora_body", "tora_head", "tora_tail"], keys.Layers.Select(l => l.LayerId).Where(id => id.StartsWith("tora", StringComparison.Ordinal)));
        var row = keys.Layers.Single(l => l.LayerId == "tora").Rows.Single(r => r.Property == "y");
        var (first, top) = (keys.PointOf(row.Keys[0])!.Value, keys.PointOf(row.Keys[1])!.Value);
        var perSecond = (top.X - first.X) / 0.45;

        // 1.35 s to 1.45 s: three frames later.
        keys.Press(top, ModifierKeys.None);
        keys.MoveTo(new Point(top.X + perSecond * 0.1 + 1, top.Y), pressed: true);
        keys.Release();
        WaitForSaves(window);
        var project = ProjectStore.Load(directory);
        Assert.Equal([0.9, 1.45, 1.8], ((VisualLayer)project.FindLayer("tora")!).Y.Keyframes.Select(k => k.Time));
        Assert.Equal((360.0, Easing.EaseIn), (YKey(project, "tora", 1.45).Value, YKey(project, "tora", 1.45).Easing));
        Assert.Equal("moved tora.y's keyframe from 1.35s to 1.45s", Text(window, "MessageText"));
        Assert.Equal(new KeyframeRef("tora", "y", 1.45), Assert.Single(keys.Selection).Ref);

        // A layer selected low in the list is scrolled into view with its values.
        window.SelectLayer("tora_head");
        var drawing = keys.RowBounds("tora_head", AnimatableProperties.Drawing)!.Value;
        Assert.True(drawing.Bottom <= keys.ActualHeight && drawing.Top >= KeyframeView.RulerHeight, $"{drawing} in {keys.ActualHeight}");
        window.SelectLayer("tora");
        Assert.StartsWith("Key at 1.45 s · tora · Position Y = 360 px", Text(window, "KeyStatus"));

        // One step back.
        Click(window, "UndoButton");
        WaitForSaves(window);
        project = ProjectStore.Load(directory);
        Assert.Equal([0.9, 1.35, 1.8], ((VisualLayer)project.FindLayer("tora")!).Y.Keyframes.Select(k => k.Time));
        Assert.Equal((360.0, Easing.EaseIn), (YKey(project, "tora", 1.35).Value, YKey(project, "tora", 1.35).Easing));

        // A key can't land on another: 1.8 s is 27 frames after 0.9 s. (1.35 s is between frames,
        // and keys move by whole frames.)
        keys.Select([new KeyframeRef("tora", "y", 1.8)]);
        var last = keys.PointOf(keys.Selection[0])!.Value;
        keys.Press(last, ModifierKeys.None);
        keys.MoveTo(new Point(last.X - perSecond * 0.9, last.Y), pressed: true);
        keys.Release();
        WaitForSaves(window);
        Assert.Equal([0.9, 1.35, 1.8], ((VisualLayer)ProjectStore.Load(directory).FindLayer("tora")!).Y.Keyframes.Select(k => k.Time));
        Assert.Contains("a keyframe can't land on another", Text(window, "SaveStatus"));

        // In the graph, dragging straight up (Shift) changes the value, not the time: Position Y is
        // drawn the way the layer moves, so up is a smaller y. The selected layer's curves are
        // drawn; clicking a value in the tree takes its curve away.
        ((RadioButton)window.FindName("GraphMode")).IsChecked = true;
        Assert.Equal(KeyframeMode.Graph, keys.Mode);
        Assert.Equal(["x", "y", "rotation"], keys.ShownRows.Select(r => r.Property));
        Assert.Equal(Visibility.Visible, Shown(window, "FitCurvesToggle"));
        foreach (var property in new[] { "x", "rotation" })
        {
            var bounds = keys.RowBounds("tora", property)!.Value;
            keys.Press(new Point(bounds.X + 100, bounds.Y + bounds.Height / 2), ModifierKeys.None);
            keys.Release();
        }

        Assert.Equal(["y"], keys.ShownRows.Select(r => r.Property));
        var key = keys.Layers.Single(l => l.LayerId == "tora").Rows.Single(r => r.Property == "y").Keys[1];
        var at = keys.PointOf(key)!.Value;
        keys.Press(at, ModifierKeys.None);
        keys.MoveTo(new Point(at.X + 2, at.Y - 30), pressed: true, ModifierKeys.Shift);
        keys.Release();
        WaitForSaves(window);
        var moved = YKey(ProjectStore.Load(directory), "tora", 1.35);
        Assert.True(moved.Value < 360, $"{moved.Value}");
        Assert.Equal(Math.Round(moved.Value, 1), moved.Value);
        Assert.Equal(Easing.EaseIn, moved.Easing);
    });

    // The eight tiles give the selected keys Core's eight easings; the custom curve gives them the
    // cubicBezier set_keyframes gives, whose values are the curve's.
    [Fact]
    public void TheEasingTilesSetCoresEightEasingsAndACustomBezierIsWhatSetKeyframesMakes() => TestWindow.Run("window-animate-easing", (window, folder) =>
    {
        var (directory, keys) = Animate(window, folder);
        var original = ProjectStore.Load(directory);
        KeyframeRef[] selected = [new("tora", "y", 0.9), new("tora", "y", 1.35)];
        keys.Select(selected);
        TestWindow.Pump();
        Assert.Equal("2 keyframes selected", Text(window, "EasingSelectionText"));
        var tiles = (ItemsControl)window.FindName("EasingTiles");
        Assert.Equal(["Linear", "Ease in", "Ease out", "In-out", "Back", "Bounce", "Elastic", "Hold"], tiles.Items.Cast<EasingTile>().Select(t => t.Name));
        Assert.DoesNotContain(tiles.Items.Cast<EasingTile>(), t => t.Chosen);

        foreach (var (easing, name) in AnimateModel.Tiles)
        {
            TestWindow.Pump();
            Descendants<Button>(tiles).Single(b => Equals(b.Tag, easing)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            WaitForSaves(window);
            var project = ProjectStore.Load(directory);
            Assert.Equal([easing, easing], selected.Select(r => YKey(project, "tora", r.Time).Easing));
            Assert.Equal(name, Assert.Single(tiles.Items.Cast<EasingTile>(), t => t.Chosen).Name);
        }

        Assert.Equal(true, ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("HoldButton")).IsChecked);
        Assert.Equal(Command.EaseInOut, window.RunKey(Key.F9, ModifierKeys.None, inField: false));
        WaitForSaves(window);
        Assert.Equal(Easing.EaseInOut, YKey(ProjectStore.Load(directory), "tora", 0.9).Easing);
        Assert.Equal(true, ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("SmoothButton")).IsChecked);

        // The custom curve: as set_keyframes makes it from the project as it was.
        Assert.Equal(["0.00", "0.00", "0.40", "1.00"], new[] { "BezierX1", "BezierY1", "BezierX2", "BezierY2" }.Select(n => ((TextBox)window.FindName(n)).Text));
        Click(window, "BezierApply");
        WaitForSaves(window);
        KeyframeEditor.SetKeyframes(original,
        [
            new KeyframeEntry("tora", "y", 0.9, System.Text.Json.JsonSerializer.SerializeToElement(605), "cubicBezier", [0, 0, 0.4, 1]),
            new KeyframeEntry("tora", "y", 1.35, System.Text.Json.JsonSerializer.SerializeToElement(360), "cubicBezier", [0, 0, 0.4, 1]),
        ]);
        var expected = JsonNode.Parse(ZeyoCut.Core.Serialization.ProjectJson.Serialize(original))!.AsObject();
        expected.Remove("revision");
        Assert.Equal(expected.ToJsonString(), ProjectJson(directory));
        var y = ((VisualLayer)ProjectStore.Load(directory).FindLayer("tora")!).Y;
        Assert.Equal(605 + (360 - 605) * 0.765988682530208, Interpolator.Evaluate(y, 0.9 + 0.225), 1e-9);
        Assert.Equal(605 + (360 - 605) * 0.941991889400904, Interpolator.Evaluate(y, 0.9 + 0.3375), 1e-9);
        Assert.DoesNotContain(tiles.Items.Cast<EasingTile>(), t => t.Chosen);

        // Numbers it can't take are refused with Core's reason.
        ((TextBox)window.FindName("BezierX1")).Text = "1.5";
        Click(window, "BezierApply");
        WaitForSaves(window);
        Assert.Contains("x1 and x2 must be from 0 to 1", Text(window, "SaveStatus"));
    });

    // The onion skin draws the selected layer around the playhead, Alt+O turns it off and on, and the
    // motion path is the layer's way across the frame; Alt+K keys its position at the playhead, and
    // Delete takes the selected keys away.
    [Fact]
    public void TheAnimateViewDrawsTheSelectedLayersOnionSkinAndMotionPath() => TestWindow.Run("window-animate-onion", (window, folder) =>
    {
        var (directory, keys) = Animate(window, folder);
        var preview = (AnimatePreview)window.FindName("AnimateFrame");
        for (var i = 0; i < 30; i++)
        {
            window.RunKey(Key.Right, ModifierKeys.None, inField: false);
        }

        Assert.Equal("frame 30 of 89", Text(window, "AnimateFrameText"));
        Assert.Equal("Onion skin · 3 before / 2 after", Text(window, "OnionText"));
        for (var i = 0; i < 500 && preview.Ghosts is null; i++)
        {
            TestWindow.Pump(10);
        }

        var ghosts = Assert.IsAssignableFrom<BitmapSource>(preview.Ghosts);
        Assert.Equal((1280, 720), (ghosts.PixelWidth, ghosts.PixelHeight));
        Assert.Equal(90, preview.Path.Count);
        Assert.Equal(30, keys.Playhead);

        Assert.Equal(Command.OnionSkin, window.RunKey(Key.O, ModifierKeys.Alt, inField: false));
        Assert.Null(preview.Ghosts);
        Assert.Equal("Onion skin off", Text(window, "MessageText"));
        Assert.Contains("\"onionSkin\": false", File.ReadAllText(Path.Combine(folder, "editor.json")));
        window.RunKey(Key.O, ModifierKeys.Alt, inField: false);
        for (var i = 0; i < 500 && preview.Ghosts is null; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.NotNull(preview.Ghosts);

        // Alt+K: the position at the playhead, 1 s, with the values it has there.
        var before = (VisualLayer)ProjectStore.Load(directory).FindLayer("tora")!;
        Assert.Equal(Command.AddKeyframe, window.RunKey(Key.K, ModifierKeys.Alt, inField: false));
        WaitForSaves(window);
        var tora = (VisualLayer)ProjectStore.Load(directory).FindLayer("tora")!;
        Assert.Equal([0.9, 1, 1.8], tora.X.Keyframes.Select(k => k.Time));
        Assert.Equal([0.9, 1, 1.35, 1.8], tora.Y.Keyframes.Select(k => k.Time));
        Assert.Equal(Interpolator.Evaluate(before.X, 1), tora.X.Keyframes[1].Value, 1e-9);
        Assert.Equal(Interpolator.Evaluate(before.Y, 1), tora.Y.Keyframes[1].Value, 1e-9);

        // Delete: the keys selected, as one step.
        keys.Select([new KeyframeRef("tora", "x", 1), new KeyframeRef("tora", "y", 1)]);
        Assert.Equal(Command.Delete, window.RunKey(Key.Delete, ModifierKeys.None, inField: false));
        WaitForSaves(window);
        tora = (VisualLayer)ProjectStore.Load(directory).FindLayer("tora")!;
        Assert.Equal([0.9, 1.8], tora.X.Keyframes.Select(k => k.Time));
        Assert.Equal([0.9, 1.35, 1.8], tora.Y.Keyframes.Select(k => k.Time));
        Assert.Empty(keys.Selection);
        Assert.False(window.CanRun(Command.Delete));
    });

    [Fact]
    public void ACaptionAddedAtThePlayheadIsStyledLikeItsNeighbours() => TestWindow.Run("window-captions-add", (window, folder) =>
    {
        var directory = TestFiles.Example("comic-panels", folder);
        window.OpenProject(directory);
        window.RunKey(Key.D5, ModifierKeys.Control, inField: false);

        // Going into the first row takes the playhead there; the new row comes after it, up to the next.
        Assert.True(CaptionCell(window, 1, "captions_en").Focus());
        TestWindow.Pump();
        Assert.Equal("00:00:00:12", Text(window, "TimecodeText"));
        Click(window, "CaptionAdd");
        TestWindow.Pump();
        var draft = CaptionBoxes(window).Where(b => b.Tag is CaptionCell { Row.Draft: true }).ToList();
        Assert.Equal(3, draft.Count);
        Assert.Equal(("00:02.70", "00:03.40"), (CaptionTime(window, 0, "Start").Text, CaptionTime(window, 0, "End").Text));
        Assert.Same(draft[0], Keyboard.FocusedElement);

        // Typed into, the English cell saves a caption there, made like the track's others.
        draft[0].Text = "He gets ready.";
        TestWindow.Press(draft[0], Key.Return);
        WaitForSaves(window);
        var project = ProjectStore.Load(directory);
        var added = (TextLayer)project.FindLayer("captions_en_4")!;
        Assert.Equal(("captions_en", 2.7, 3.4, "He gets ready."), (added.ParentId, added.Start, added.End!.Value, added.Text));
        static string Style(Layer layer)
        {
            var node = ZeyoCut.Core.Serialization.ProjectJson.LayerToNode(layer);
            foreach (var key in new[] { "id", "name", "text", "start", "end" })
            {
                node.Remove(key);
            }

            return node.ToJsonString();
        }

        Assert.Equal(Style(project.FindLayer("captions_en_1")!), Style(added));
        Assert.Equal(["captions_en_1", "captions_en_4", "captions_en_2", "captions_en_3"], project.Layers.Where(l => l.ParentId == "captions_en").Select(l => l.Id));
        Assert.Equal("added caption 'captions_en_4' to 'captions_en' at 2.7–3.4s", Text(window, "MessageText"));

        // It's a row of its own now, with the keyboard still in it, and the other tracks have room for theirs.
        Assert.Equal(["", "3/4", "3/4"], ((ItemsControl)window.FindName("CaptionChips")).Items.Cast<CaptionColumn>().Select(c => c.Count));
        TestWindow.Pump();
        Assert.Same(CaptionCell(window, 2, "captions_en"), Keyboard.FocusedElement);
        Assert.Equal("", CaptionCell(window, 2, "captions_ja").Text);
    });

    [Fact]
    public void ThePreviewDrawsOneLanguagesVersionAsRenderFrameDoes() => TestWindow.Run("window-captions-language", (window, folder) =>
    {
        var directory = TestFiles.Example("comic-panels", folder);
        window.OpenProject(directory);
        var json = File.ReadAllText(Path.Combine(directory, ProjectStore.ProjectFileName));

        // English first, in the Edit view's button too.
        Assert.Equal(Visibility.Visible, Shown(window, "LanguageButton"));
        Assert.Equal("EN", Text(window, "LanguageText"));
        window.RunKey(Key.D5, ModifierKeys.Control, inField: false);
        Assert.True(CaptionCell(window, 1, "captions_en").Focus());
        TestWindow.Pump();

        byte[] OnScreen()
        {
            var bitmap = (WriteableBitmap)((Image)window.FindName("PreviewImage")).Source;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        byte[] Drawn(string language)
        {
            var project = ProjectStore.Load(directory);
            Languages.Choose(project, language, export: false);
            using var renderer = new ZeyoCut.Render.FrameRenderer(directory);
            using var image = renderer.RenderImage(project, 12, new ZeyoCut.Render.RenderOptions(Proxies: true));
            using var bitmap = SkiaSharp.SKBitmap.FromImage(image);
            using var bgra = bitmap.Copy(SkiaSharp.SKColorType.Bgra8888);
            return bgra.Bytes;
        }

        bool Showing(byte[] frame)
        {
            for (var i = 0; i < 500 && !frame.AsSpan().SequenceEqual(OnScreen()); i++)
            {
                TestWindow.Pump(10);
            }

            return frame.AsSpan().SequenceEqual(OnScreen());
        }

        var english = Drawn("en");
        Assert.True(Showing(english));

        // Japanese chosen: the frame render_frame draws with language ja, here and in the Edit view.
        var japanese = Drawn("ja");
        Assert.False(english.AsSpan().SequenceEqual(japanese));
        var ja = Descendants<RadioButton>((ItemsControl)window.FindName("CaptionLanguages")).Single(r => (string)r.Tag == "ja");
        ja.IsChecked = true;
        Assert.True(Showing(japanese));
        Assert.Equal("JA", Text(window, "LanguageText"));
        window.RunKey(Key.D1, ModifierKeys.Control, inField: false);
        TestWindow.Pump();
        Assert.True(Showing(japanese));

        // An editor setting: the project is as it was.
        Assert.Contains("\"previewLanguage\": \"ja\"", File.ReadAllText(Path.Combine(folder, "editor.json")));
        Assert.Equal(json, File.ReadAllText(Path.Combine(directory, ProjectStore.ProjectFileName)));
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
    public void TheWayBackToTheProjectsListsTheRecentOnesStillThere() => TestWindow.Run("window-recent", (window, folder) =>
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

        // Packed, there's nothing more to pack.
        pack.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        WaitForSaves(window);
        Assert.False(pack.IsEnabled);

        // "Projects" in the header closes the project; the start page lists both, the last opened first.
        Click(window, "ProjectsLink");
        Assert.Equal("ZeyoCut Editor", window.Title);
        Assert.Equal(Visibility.Visible, Shown(window, "StartPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "Crumbs"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ProjectCommands"));
        Assert.False(((Button)window.FindName("LogoButton")).IsEnabled);
        Assert.Equal([first, second], Recent(window, 2));

        // The logo does too, and the list leaves out a project that is gone.
        Directory.Delete(second, recursive: true);
        window.OpenProject(first);
        Click(window, "LogoButton");
        Assert.Equal([first], Recent(window, 1));
    });

    [Fact]
    public void HomeShowsTheRecentProjectsThenTheOthersAndSearchesTheirNames() => TestWindow.Run("window-home", (window, folder) =>
    {
        var projects = Path.Combine(folder, "projects");
        var alpha = Saved(projects, "alpha");
        Saved(projects, "beta");
        window.OpenProject(alpha);
        Click(window, "LogoButton");
        Assert.Equal([alpha], Recent(window, 1));
        Assert.Equal(["beta"], Others(window, 1));
        Assert.Equal(("RECENT", Visibility.Visible), (Text(window, "RecentTitle"), Shown(window, "ProjectsTitle")));
        Assert.Equal(ProjectList.Shorten(projects), Text(window, "HomeFolder"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "HomeNoMatch"));

        // The search keeps the projects whose names have it in them, recent or not.
        var search = (TextBox)window.FindName("HomeSearch");
        search.Text = "BET";
        Assert.Empty(Recent(window, 0));
        Assert.Equal(["beta"], Others(window, 1));
        Assert.Equal(("PROJECTS", Visibility.Collapsed, Visibility.Collapsed), (Text(window, "RecentTitle"), Shown(window, "ProjectsTitle"), Shown(window, "RecentList")));
        search.Text = "gamma";
        Assert.Empty(Others(window, 0));
        Assert.Equal(Visibility.Visible, Shown(window, "HomeNoMatch"));
        Assert.Equal("No project's name has “gamma” in it.", Text(window, "HomeNoMatch"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "HomeListHeader"));

        // Shown as a list, which the editor's settings keep.
        search.Text = "";
        Assert.Equal(["beta"], Others(window, 1));
        var list = (ItemsControl)window.FindName("ProjectsList");
        Assert.Same(window.FindResource("ProjectCard"), list.ItemTemplate);
        ((RadioButton)window.FindName("HomeRows")).IsChecked = true;
        Assert.Same(window.FindResource("ProjectRow"), list.ItemTemplate);
        Assert.True(EditorSettings.Load(Path.Combine(folder, "editor.json")).ProjectsAsList);
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
        Click(window, "LogoButton");
        Assert.True(watch.ElapsedMilliseconds < 1000, $"closing took {watch.ElapsedMilliseconds} ms");
        Assert.Equal("ZeyoCut Editor", window.Title);
    });

    // The header names the project and whether it's saved, undoes and redoes, says when Claude saved
    // last and leads to the Export view and back; the status bar names the selection.
    [Fact]
    public void TheHeaderNamesTheProjectAndItsLastSaves() => TestWindow.Run("window-header", (window, folder) =>
    {
        var directory = WithTitle(window, folder);
        var undo = (Button)window.FindName("UndoButton");
        var redo = (Button)window.FindName("RedoButton");
        Assert.Equal("titled", Text(window, "ProjectName"));
        Assert.Equal("Saved · rev 1", Text(window, "SaveBadgeText"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "SaveStatus"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ClaudeBadge"));
        Assert.False(undo.IsEnabled);
        Assert.False(redo.IsEnabled);
        Assert.Equal("title selected", Text(window, "SelectionText"));
        Assert.Equal("1 layer · 1 lane", Text(window, "LayersText"));

        var start = Field(window, "Start");
        start.Text = "0.5";
        TestWindow.Press(start, Key.Return);
        WaitForSaves(window);
        Assert.Equal("Saved · rev 2", Text(window, "SaveBadgeText"));
        Assert.True(undo.IsEnabled);
        Assert.False(redo.IsEnabled);

        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal("Saved · rev 3", Text(window, "SaveBadgeText"));
        Assert.Equal(0, Title(directory).Start);
        Assert.False(undo.IsEnabled);
        Assert.True(redo.IsEnabled);

        // Another program's save is Claude's, named in the header with when it was made.
        ProjectSession.Open(directory).Mutate("update_layer title", p =>
        {
            p.FindLayer("title")!.End = 3;
            return 0;
        });
        for (var i = 0; i < 300 && Shown(window, "ClaudeBadge") != Visibility.Visible; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.Equal("Claude saved just now", Text(window, "ClaudeText"));
        Assert.Equal("Saved · rev 4", Text(window, "SaveBadgeText"));

        // The Export button opens the Export view, whose header has a way back in place of the tabs.
        Click(window, "ExportViewButton");
        Assert.Equal(Visibility.Visible, Shown(window, "ExportPage"));
        Assert.Equal(Visibility.Visible, Shown(window, "ExportCrumb"));
        Assert.Equal(Visibility.Visible, Shown(window, "BackToEdit"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ModeBar"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ExportViewButton"));
        Assert.Equal("", Text(window, "SelectionText"));
        Click(window, "BackToEdit");
        Assert.Equal(Visibility.Visible, Shown(window, "Workspace"));
        Assert.Equal(Visibility.Visible, Shown(window, "ModeBar"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ExportCrumb"));
        Assert.Equal("title selected", Text(window, "SelectionText"));

        // The Activity tab shows Claude's save first.
        ((RadioButton)window.FindName("ActivityMode")).IsChecked = true;
        Assert.Equal(Visibility.Visible, Shown(window, "ActivityPage"));
        var newest = (SavedStep)((ItemsControl)window.FindName("ActivitySteps")).Items[0];
        Assert.Equal(("update_layer title", "Claude", "Today", 4L), (newest.Title, newest.Who, newest.Day, newest.Revision));
    });

    // The Activity view groups the saves into steps, filters them by who saved, and puts a step back
    // as the next revision, which undo takes back.
    [Fact]
    public void TheActivityViewRestoresAStepAsTheNextRevision() => TestWindow.Run("window-activity", (window, folder) =>
    {
        var directory = WithTitle(window, folder);
        ProjectSession.Open(directory).Mutate("move_layer title", p => new MoveLayerOperation("title", 0.5).Apply(p));
        var editor = ProjectSession.Open(directory, ProjectBackups.EditorProgram);
        editor.Mutate("trim_layer title", p => new TrimLayerOperation("title", null, 2).Apply(p));
        editor.Mutate("trim_layer title", p => new TrimLayerOperation("title", null, 1.5).Apply(p));
        for (var i = 0; i < 300 && Text(window, "SaveBadgeText") != "Saved · rev 4"; i++)
        {
            TestWindow.Pump(10);
        }

        ((RadioButton)window.FindName("ActivityMode")).IsChecked = true;
        TestWindow.Pump();
        var list = (ItemsControl)window.FindName("ActivitySteps");
        List<SavedStep> Steps() => list.Items.Cast<SavedStep>().ToList();
        Assert.Equal([("trim_layer title, then 1 more", "You", 4L), ("move_layer title", "Claude", 2L)], Steps().Select(s => (s.Title, s.Who, s.Revision)));
        Assert.Equal(["trim_layer title ×2"], Steps()[0].Edits);
        Assert.Equal(Visibility.Visible, Shown(window, "ActivityOlder"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ActivityEmpty"));

        // The newest step is the project as it is, with nothing to restore; the one before has Restore.
        var restores = Descendants<Button>(list).Where(b => b.IsVisible).ToList();
        Assert.Equal([Steps()[1]], restores.Select(b => b.Tag));
        Assert.Equal("Restore", restores[0].Content);
        Assert.Equal("Put the project back as it was after this step, as a new save; undo takes it back", restores[0].ToolTip);

        ((RadioButton)window.FindName("ActivityClaude")).IsChecked = true;
        Assert.Equal(["Claude"], Steps().Select(s => s.Who));
        ((RadioButton)window.FindName("ActivityYou")).IsChecked = true;
        Assert.Equal(["You"], Steps().Select(s => s.Who));
        ((RadioButton)window.FindName("ActivityAll")).IsChecked = true;
        TestWindow.Pump();

        // Restoring Claude's step saves the project as it left it: moved, not yet trimmed.
        var claudes = Steps()[1];
        Descendants<Button>(list).Single(b => b.IsVisible).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        WaitForSaves(window);
        var saved = ProjectStore.Load(directory);
        Assert.Equal((5L, 0.5, 2.5), (saved.Revision, Title(directory).Start, Title(directory).End));
        Assert.Equal($"Restored the project as it was after {claudes.Title} ({claudes.Details}). Undo takes it back.", Text(window, "MessageText"));
        var label = ProjectBackups.RestoreLabel(claudes.Restore!);
        Assert.Equal([(label, "You", 5L), ("trim_layer title, then 1 more", "You", 4L), ("move_layer title", "Claude", 2L)], Steps().Select(s => (s.Title, s.Who, s.Revision)));
        Assert.Equal("editor " + label, ProjectBackups.List(directory)[0].Before);

        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal((6L, 1.5), (ProjectStore.Load(directory).Revision, Title(directory).End));
    });

    // The safe-area guide and the thirds grid are drawn over the frame, never into it, and kept in
    // the editor's settings, not the project.
    [Fact]
    public void TheGuidesAreDrawnOverTheFrameAndKeptInTheEditorsSettings() => TestWindow.Run("window-guides", (window, folder) =>
    {
        var directory = WithTitle(window, folder);
        var image = (Image)window.FindName("PreviewImage");
        for (var i = 0; i < 500 && Text(window, "RenderText").Length == 0; i++)
        {
            TestWindow.Pump(10);
        }

        byte[] Pixels()
        {
            var bitmap = (WriteableBitmap)image.Source;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        Assert.Equal("16:9 · 320×180 · 30 fps", Text(window, "CanvasText"));
        var before = Pixels();
        Assert.Equal(Visibility.Collapsed, Shown(window, "SafeAreaBox"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "GridLines"));
        foreach (var name in new[] { "SafeAreaToggle", "GridToggle" })
        {
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName(name);
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }

        TestWindow.Pump();
        var box = (System.Windows.Shapes.Rectangle)window.FindName("SafeAreaBox");
        var scale = image.ActualWidth / 320;
        Assert.Equal(Visibility.Visible, box.Visibility);
        Assert.Equal(Visibility.Visible, Shown(window, "GridLines"));
        Assert.Equal((16 * scale, 9 * scale, 288 * scale), (Canvas.GetLeft(box), Canvas.GetTop(box), box.Width), new ToleranceComparer());
        Assert.Equal(before, Pixels());
        Assert.Equal(1, ProjectStore.Load(directory).Revision);
        var settings = EditorSettings.Load(Path.Combine(folder, "editor.json"));
        Assert.True(settings.SafeArea && settings.Grid);
    });

    // With Loop on, playing past the last frame goes on from the first.
    [Fact]
    public void WithLoopOnPlayingGoesOnFromTheStart() => TestWindow.Run("window-loop", (window, folder) =>
    {
        WithTitle(window, folder);
        var loop = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("LoopToggle");
        loop.IsChecked = true;
        loop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        TestWindow.Press(window, Key.End);
        for (var i = 0; i < 10; i++)
        {
            TestWindow.Press(window, Key.Left);
        }

        int Frame() => int.Parse(Text(window, "FrameText").Split(' ')[1]);
        Assert.Equal(49, Frame());
        var play = (Button)window.FindName("PlayButton");
        TestWindow.Press(window, Key.Space);
        for (var i = 0; i < 800 && Frame() >= 49; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.True(Frame() < 49, $"still at frame {Frame()}");
        Assert.Equal("Pause (Space)", play.ToolTip);
        TestWindow.Press(window, Key.Space);
        Assert.Equal("Play (Space)", play.ToolTip);
    });

    // The tab chosen stays chosen for the next clip that has it; a child's chip selects it; the
    // opacity slider saves once, when it's let go.
    [Fact]
    public void TheInspectorsTabsChipsAndSliderWork() => TestWindow.Run("window-inspector", (window, folder) =>
    {
        var project = TestFiles.Project();
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 0, End = 2 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "sub", Text = "Hi", ParentId = "title", Start = 0, End = 2, FontSize = 20 });
        var directory = Path.Combine(folder, "titled");
        ProjectSession.Create(directory, project);
        window.OpenProject(directory);
        window.SelectLayer("title");
        TestWindow.Pump();

        var tabs = (Panel)window.FindName("InspectorTabs");
        Assert.Equal(["Text", "Style", "Effects"], tabs.Children.OfType<RadioButton>().Select(t => (string)t.Content));
        Tab(window, "Style");
        var size = Field(window, "Size").Text;
        Assert.Throws<InvalidOperationException>(() => Field(window, "Start"));
        window.SelectLayer(null);
        TestWindow.Pump();
        Assert.Equal(("PROJECT", Visibility.Collapsed), (Text(window, "InspectorKind"), Shown(window, "InspectorTabs")));
        window.SelectLayer("title");
        TestWindow.Pump();
        Assert.Equal(size, Field(window, "Size").Text);

        // The chip of the title's child selects it, on the tab chosen.
        Tab(window, "Text");
        var chip = Descendants<Button>((ItemsControl)window.FindName("InspectorList")).Single(b => b.Tag is Attachment { LayerId: "sub" });
        chip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        TestWindow.Pump();
        Assert.Equal(("sub", "“Hi”"), (Text(window, "InspectorName"), Text(window, "InspectorDetails")));
        window.SelectLayer("title");
        TestWindow.Pump();

        var slider = Descendants<Slider>((ItemsControl)window.FindName("InspectorList")).Single();
        var box = Field(window, "Opacity");
        foreach (var value in new[] { 80.0, 60, 40.4 })
        {
            slider.Value = value;
            TestWindow.Pump();
        }

        Assert.Equal("40", box.Text);
        Assert.Equal("Saved · revision 1", Text(window, "SaveStatus"));
        slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        WaitForSaves(window);
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));
        Assert.Equal(0.4, Title(directory).Opacity.Value);

        // Let go again without a move, the slider saves nothing.
        slider = Descendants<Slider>((ItemsControl)window.FindName("InspectorList")).Single();
        slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        WaitForSaves(window);
        Assert.Equal("Saved · revision 2", Text(window, "SaveStatus"));
    });

    private sealed class ToleranceComparer : IEqualityComparer<(double, double, double)>
    {
        public bool Equals((double, double, double) a, (double, double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < 0.01 && Math.Abs(a.Item2 - b.Item2) < 0.01 && Math.Abs(a.Item3 - b.Item3) < 0.01;

        public int GetHashCode((double, double, double) value) => 0;
    }

    // With the Blade (B), a click on a clip splits it at the frame clicked, as split_layer does;
    // Escape goes back to selecting.
    [Fact]
    public void TheBladeSplitsAClipWhereItsClicked() => TestWindow.Run("window-blade", (window, folder) =>
    {
        var (directory, timeline, at) = ShortTitle(window, folder);
        TestWindow.Press(window, Key.B);
        Assert.Equal(TimelineTool.Blade, timeline.Tool);
        Assert.True(((RadioButton)window.FindName("BladeTool")).IsChecked);

        // Over a clip, a line shows where it would be cut.
        timeline.MoveTo(at(0.4), pressed: false);
        var bitmap = new RenderTargetBitmap((int)timeline.ActualWidth, (int)timeline.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(timeline);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Round(at(0.4).X), (int)at(0.4).Y, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, pixel);

        var revision = ProjectStore.Load(directory).Revision;
        timeline.Press(at(0.4));
        timeline.Release();
        WaitForSaves(window);
        var project = ProjectStore.Load(directory);
        var right = project.Layers.OfType<TextLayer>().Single(l => l.Id != "title");
        Assert.Equal(0.4, Title(directory).End!.Value, 6);
        Assert.Equal((0.4, 1.0), (Math.Round(right.Start, 6), right.End));
        Assert.Equal(revision + 1, project.Revision);

        TestWindow.Press(window, Key.Escape);
        Assert.Equal(TimelineTool.Select, timeline.Tool);
        Assert.True(((RadioButton)window.FindName("SelectTool")).IsChecked);
    });

    // A lane's eye hides every clip in it in one step, and shows them all once all are hidden; a
    // sound lane's mutes its clips.
    [Fact]
    public void ALanesEyeHidesEveryClipInItAsOneStep() => TestWindow.Run("window-eye", (window, folder) =>
    {
        var project = TestFiles.Project();
        project.Assets.Add(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.wav", Duration = 0.5 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", Start = 0, End = 1, ZIndex = 0 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "credits", Text = "Bye", Start = 1.5, End = 2, ZIndex = 0 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "music", Id = "music", End = 0.5 });
        var directory = Path.Combine(folder, "eye");
        ProjectSession.Create(directory, project);
        Silence(Path.Combine(directory, ProjectStore.AssetsFolder, "music.wav"));
        window.OpenProject(directory);
        TestWindow.Pump();
        var timeline = (TimelineView)window.FindName("Timeline");
        Assert.Equal(["Text", "Music"], timeline.Lanes.Select(l => l.Title));
        void Eye(string lane)
        {
            var bounds = timeline.EyeBounds(timeline.Lanes.ToList().FindIndex(l => l.Title == lane));
            timeline.Press(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            timeline.Release();
            WaitForSaves(window);
        }

        bool Shown(string id) => ProjectStore.Load(directory).FindLayer(id)!.Visible;

        var revision = ProjectStore.Load(directory).Revision;
        Eye("Text");
        Assert.Equal((false, false, true), (Shown("title"), Shown("credits"), Shown("music")));
        Assert.Equal(revision + 1, ProjectStore.Load(directory).Revision);

        // One undo shows both again.
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal((true, true), (Shown("title"), Shown("credits")));

        // With one hidden, the eye hides the other; with all hidden, it shows them all.
        window.SelectLayer("title");
        TestWindow.Pump();
        ProjectSession.Open(directory).Mutate("update_layer credits", p => ProjectEditor.UpdateLayer(p, "credits", new JsonObject { ["visible"] = false }));
        for (var i = 0; i < 200 && timeline.FindClip("credits") is not { Hidden: true }; i++)
        {
            TestWindow.Pump(20);
        }

        Eye("Text");
        Assert.Equal((false, false), (Shown("title"), Shown("credits")));
        Eye("Text");
        Assert.Equal((true, true), (Shown("title"), Shown("credits")));

        // The sound lane's mutes its clips, and unmutes them.
        Eye("Music");
        Assert.False(Shown("music"));
        Eye("Music");
        Assert.True(Shown("music"));
    });

    // M, or the Add marker button, puts a chapter marker at the playhead, as add_markers does.
    [Fact]
    public void AddMarkerPutsAChapterMarkerAtThePlayhead() => TestWindow.Run("window-marker", (window, folder) =>
    {
        var (directory, timeline, at) = ShortTitle(window, folder);
        timeline.Press(new Point(at(0.5).X, TimelineView.RulerHeight / 2));
        timeline.Release();
        TestWindow.Pump();
        TestWindow.Press(window, Key.M);
        WaitForSaves(window);
        Assert.Equal([("marker_1", 0.5, "Marker 1", MarkerKind.Chapter)], ProjectStore.Load(directory).Markers.Select(m => (m.Id, m.Time, m.Name, m.Kind)));

        Click(window, "MarkerButton");
        WaitForSaves(window);
        Assert.Equal(["marker_1", "marker_2"], ProjectStore.Load(directory).Markers.Select(m => m.Id));

        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal(["marker_1"], ProjectStore.Load(directory).Markers.Select(m => m.Id));
    });

    // The media panel's tiles: a thumbnail or a waveform, a length on footage and sound, a missing file marked;
    // the chips show one kind, and Fonts is there while the project has a font.
    [Fact]
    public void TheMediaPanelShowsTilesThatTheChipsFilter() => TestWindow.Run("window-media", (window, folder) =>
    {
        var directory = Path.Combine(folder, "media");
        var project = TestFiles.Project();
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 64, Height = 64 });
        project.Assets.Add(new Asset { Id = "music", Type = AssetType.Audio, File = "assets/music.wav", Duration = 0.5 });
        project.Assets.Add(new Asset { Id = "font", Type = AssetType.Font, File = "assets/font.ttf", FontFamily = "Gone" });
        ProjectSession.Create(directory, project);
        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 64, 64);
        Silence(Path.Combine(directory, ProjectStore.AssetsFolder, "music.wav"));
        window.OpenProject(directory);
        TestWindow.Pump();

        var list = (ListBox)window.FindName("MediaList");
        MediaItem Tile(string id) => list.Items.Cast<MediaItem>().Single(i => i.Id == id);
        string[] Tiles() => list.Items.Cast<MediaItem>().Select(i => i.Id).ToArray();
        Assert.Equal(["logo", "music", "font"], Tiles());
        Assert.Equal((null, "0:00.5", null), (Tile("logo").Length, Tile("music").Length, Tile("font").Length));
        Assert.True(Tile("font").Missing);
        Assert.StartsWith("logo.png · 64×64\n", Tile("logo").Tip);
        for (var i = 0; i < 500 && (Tile("logo").Picture is null || Tile("music").Picture is null); i++)
        {
            TestWindow.Pump(20);
        }

        Assert.IsType<ImageBrush>(Tile("logo").Picture);
        Assert.IsType<DrawingBrush>(Tile("music").Picture);
        Assert.Null(Tile("font").Picture);

        void Chip(string name)
        {
            ((Panel)window.FindName("MediaFilters")).Children.OfType<RadioButton>().Single(c => (string)c.Content == name).IsChecked = true;
            TestWindow.Pump();
        }

        Assert.Equal(Visibility.Visible, Shown(window, "FontsFilter"));
        Chip("Images");
        Assert.Equal(["logo"], Tiles());
        Chip("Audio");
        Assert.Equal(["music"], Tiles());
        Chip("Fonts");
        Assert.Equal(["font"], Tiles());
        Chip("Video");
        Assert.Empty(Tiles());
        Assert.Equal(("No video in this project.", Visibility.Visible), (Text(window, "MediaEmpty"), Shown(window, "MediaEmpty")));
        Chip("All");
        Assert.Equal(3, Tiles().Length);
        Assert.Equal(Visibility.Collapsed, Shown(window, "MediaEmpty"));
    });

    // The zoom slider follows the zoom, and zooms the timeline when moved.
    [Fact]
    public void TheZoomSliderFollowsAndSetsTheZoom() => TestWindow.Run("window-zoom", (window, folder) =>
    {
        var (_, timeline, _) = ShortTitle(window, folder);
        var slider = (Slider)window.FindName("ZoomSlider");
        var fit = timeline.PixelsPerSecond;
        Assert.Equal(0, slider.Value);
        TestWindow.Press(window, Key.Add);
        Assert.InRange(slider.Value, 0.01, 0.99);

        slider.Value = 1;
        TestWindow.Pump();
        Assert.Equal(30 * 48, timeline.PixelsPerSecond, 6);
        slider.Value = 0;
        TestWindow.Pump();
        Assert.Equal((fit, 0.0), (timeline.PixelsPerSecond, timeline.HorizontalOffset));
    });

    // Clips are drawn rounded in their kind's color, a picture's starting with its thumbnail; the
    // selected one has an accent ring, a hidden one is faded, and the playhead is red with a head.
    [Fact]
    public void ClipsAreDrawnRoundedInTheirColorsWithThumbnails() => TestWindow.Run("window-clips", (window, folder) =>
    {
        var project = TestFiles.Project();
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 64, Height = 64 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Start = 0, End = 2 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hi", Start = 0, End = 2, ZIndex = 1 });
        var directory = Path.Combine(folder, "clips");
        ProjectSession.Create(directory, project);
        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 64, 64);
        window.OpenProject(directory);
        TestWindow.Pump();
        var list = (ListBox)window.FindName("MediaList");
        for (var i = 0; i < 500 && list.Items.Cast<MediaItem>().Single().Picture is null; i++)
        {
            TestWindow.Pump(20);
        }

        TestWindow.Pump();
        var timeline = (TimelineView)window.FindName("Timeline");
        Assert.Equal(["Text", "Images"], timeline.Lanes.Select(l => l.Title));
        double X(double time) => TimelineView.HeaderWidth + time * timeline.PixelsPerSecond - timeline.HorizontalOffset;
        double Top(int lane) => TimelineView.RulerHeight + timeline.Lanes.Take(lane).Sum(TimelineView.LaneHeight);
        double Middle(int lane) => Top(lane) + 3 + TimelineView.RowHeight(LaneKind.Visual) / 2;
        Color Pixel(double x, double y)
        {
            var bitmap = new RenderTargetBitmap((int)timeline.ActualWidth, (int)timeline.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(timeline);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)x, (int)y, 1, 1), pixel, 4, 0);
            return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }

        // The text clip's color, away from its name; its rounded corner shows the lane behind it.
        Assert.Equal(Color.FromRgb(0x7A, 0x53, 0x20), Pixel(X(1.9), Middle(0)));
        Assert.NotEqual(Color.FromRgb(0x7A, 0x53, 0x20), Pixel(X(0), Math.Round(Top(0) + 3) + 1));

        // The picture's thumbnail starts its clip: the orange picture, in the middle of its box.
        var orange = Pixel(X(0) + 20, Middle(1));
        Assert.True(orange is { R: > 200, B: < 80 }, $"thumbnail pixel {orange}");
        Assert.Equal(Color.FromRgb(0x1F, 0x6B, 0x52), Pixel(X(1.9), Middle(1)));

        // Selected, the picture's clip has an accent ring along its edge.
        window.SelectLayer("logo");
        TestWindow.Pump();
        Assert.Equal(Color.FromRgb(0x7B, 0x93, 0xFF), Pixel(X(1.5), Math.Round(Top(1) + 3) + 1));

        // The playhead, moved to 1 s: a red line two pixels wide, and its head on the ruler.
        timeline.Press(new Point(X(1), TimelineView.RulerHeight / 2));
        timeline.Release();
        TestWindow.Pump();
        var playhead = Math.Round(X(1));
        Assert.Equal(Color.FromRgb(0xFF, 0x5D, 0x5D), Pixel(playhead - 1, Middle(0)));
        Assert.Equal(Color.FromRgb(0xFF, 0x5D, 0x5D), Pixel(playhead, Middle(1)));
        Assert.Equal(Color.FromRgb(0xFF, 0x5D, 0x5D), Pixel(playhead + 5, 6));

        // Hidden, the text clip is faded into the panel.
        var eye = timeline.EyeBounds(0);
        timeline.Press(new Point(eye.X + eye.Width / 2, eye.Y + eye.Height / 2));
        WaitForSaves(window);
        TestWindow.Pump();
        Assert.True(timeline.FindClip("title")!.Hidden);
        Assert.Equal(Color.FromRgb(0x41, 0x30, 0x1C), Pixel(X(1.9), Middle(0)));
    });

    // Half a second of silence, as a WAV file.
    private static void Silence(string path)
    {
        const int Rate = 8000;
        const int Samples = 4000;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + Samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(Samples * 2);
        writer.Write(new byte[Samples * 2]);
    }

    // A project with a title from 0 to 2 s, open with the title selected.
    // The Export view exports at the quality chosen, which reaches the encoder as start_export's crf,
    // and saves the captions chosen as .srt files, the same files export_srt writes. Its cards say what
    // a preset needs, and a file's card plays the file or shows it in its folder.
    [Fact]
    public void TheExportViewExportsAtTheQualityChosenWithTheSrtFilesExportSrtWrites() => TestWindow.Run("window-export", (window, folder) =>
    {
        var project = TestFiles.Project(duration: 1);
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 0, End = 1 });
        CaptionTracks.Add(project, "en", CaptionTracks.Check([new CueEntry(0, 0.5, "Hello"), new CueEntry(0.5, 1, "there")], project.Duration).Cues, new CaptionOptions(), [], null);
        var directory = Path.Combine(folder, "talk");
        ProjectSession.Create(directory, project);
        window.OpenProject(directory);
        Click(window, "ExportViewButton");
        TestWindow.Pump();

        var presets = ((ItemsControl)window.FindName("ExportPresets")).Items.Cast<PageChoice>().ToList();
        Assert.Equal(["Project settings", "YouTube 1080p", "Shorts 1080p"], presets.Select(p => p.Label));
        Assert.Equal(["320×180 · 30 fps", "1920×1080 · 30 fps", "Needs a 9:16 project"], presets.Select(p => p.Detail));
        Assert.Equal([true, true, false], Descendants<RadioButton>((ItemsControl)window.FindName("ExportPresets")).Select(b => b.IsEnabled));
        Assert.Equal(ProjectList.Shorten(Path.Combine(directory, ProjectStore.ExportsFolder)), Text(window, "ExportFolder"));
        Assert.Equal(["320×180", "30 fps", "0:01.0", "EN burned in"], new[] { "ExportResolution", "ExportFps", "ExportDuration", "ExportCaptions" }.Select(n => Text(window, n)));
        Assert.Equal("High", Text(window, "QualityText"));
        Assert.Equal(Visibility.Visible, Shown(window, "ExportFilesEmpty"));

        // Small, from the Quality list, and the English captions as an .srt file too.
        Click(window, "QualityButton");
        ChooseInMenu("Small");
        Assert.Equal("Small", Text(window, "QualityText"));
        var srt = Descendants<CheckBox>((ItemsControl)window.FindName("ExportSubtitles")).Single();
        Assert.Equal("EN", srt.Content);
        srt.IsChecked = true;
        srt.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("EN burned in + .srt", Text(window, "ExportCaptions"));

        // While it runs, its card can stop it; once it's done, the status says what was saved.
        Click(window, "ExportButton");
        Assert.Equal(Visibility.Visible, Shown(window, "OwnExport"));
        Assert.Equal("talk.mp4", Text(window, "OwnExportName"));
        for (var i = 0; i < 12000 && Shown(window, "OwnExport") == Visibility.Visible; i++)
        {
            TestWindow.Pump(10);
        }

        var status = Text(window, "ExportStatus");
        Assert.StartsWith("Saved talk.mp4 — ", status);
        Assert.Matches(@"encoder: \S+ \([^)]*\b(CRF 28|QP 29|CQ 29|global_quality 29)\)", status);
        Assert.Contains("Saved talk.en.srt next to it.", status);

        // The .srt file is the one export_srt writes for the English captions.
        var written = CaptionBuilder.WriteSrt(Path.Combine(folder, "export_srt"), CaptionBuilder.SrtFileName("talk.en.srt"), CaptionBuilder.GroupCues(ProjectStore.Load(directory), CaptionTracks.GroupId("en")));
        Assert.Equal(File.ReadAllBytes(written), File.ReadAllBytes(Path.Combine(directory, ProjectStore.ExportsFolder, "talk.en.srt")));

        // The file's card plays it in the default player or shows it in its folder.
        TestWindow.Pump();
        var file = Assert.Single(((ItemsControl)window.FindName("ExportFiles")).Items.Cast<ExportFile>());
        Assert.Equal("talk.mp4", file.Name);
        Assert.Contains(" · Today ", file.Details);
        var buttons = Descendants<Button>((ItemsControl)window.FindName("ExportFiles")).ToList();
        Assert.Equal(["Play it in the default video player", "Show it in its folder"], buttons.Select(b => (string)b.ToolTip));
        Assert.All(buttons, b => Assert.Equal(file.Path, b.Tag));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ExportFilesEmpty"));

        // The Render queue lists it as done; paused, it starts no other export.
        Click(window, "QueueLink");
        Assert.Equal(Visibility.Visible, Shown(window, "QueuePage"));
        var row = Assert.Single(((ItemsControl)window.FindName("QueueList")).Items.Cast<QueueRowView>()).Row;
        Assert.Equal(("talk.mp4", "YOU", "talk · Project settings · .srt", "Done", "Open"), (row.File, row.Who, row.Line, row.Status, row.Action));
        Click(window, "PauseQueueButton");
        Assert.Equal("Resume queue", Text(window, "PauseQueueText"));

        // A render setting switched saves only itself.
        Assert.Equal([false, true, true], new[] { "GentleExports", "GpuEncoder", "NotifyExports" }.Select(n => ((CheckBox)window.FindName(n)).IsChecked == true));
        Flip(window, "GpuEncoder");
        var prefs = EditorSettings.Load(Path.Combine(folder, "editor.json"));
        Assert.Equal((false, false, true), (prefs.GentleExports, prefs.GpuEncoder, prefs.NotifyExports));
        TestWindow.Press(window, Key.Escape);
        Assert.Equal(Visibility.Collapsed, Shown(window, "QueuePage"));

        // Export then adds to the queue: the card says it waits, and Remove takes it off.
        ((TextBox)window.FindName("ExportName")).Text = "again";
        Click(window, "ExportButton");
        Assert.Equal(Visibility.Visible, Shown(window, "OwnExport"));
        Assert.Equal(("again.mp4", "Queued", "In the render queue, which is paused"), (Text(window, "OwnExportName"), Text(window, "OwnExportStatus"), Text(window, "OwnExportBy")));
        Assert.Equal("Add to render queue", Text(window, "ExportButtonText"));
        Assert.Equal("Render queue · 1", Text(window, "QueueLinkText"));
        Click(window, "StopExportButton");
        Assert.Equal(Visibility.Collapsed, Shown(window, "OwnExport"));
        Assert.Equal("Took again.mp4 off the render queue; it wasn't rendered.", Text(window, "ExportStatus"));
        Assert.False(File.Exists(Path.Combine(directory, ProjectStore.ExportsFolder, "again.mp4")));
    });

    // Clicks the item of the drop-down list open now whose header starts with text, and closes it.
    private static void ChooseInMenu(string text)
    {
        TestWindow.Pump();
        var item = PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
            .Where(s => s.RootVisual is not null)
            .SelectMany(s => Descendants<MenuItem>(s.RootVisual))
            .First(i => i.Header is string header && header.StartsWith(text, StringComparison.Ordinal));
        var menu = (ContextMenu)ItemsControl.ItemsControlFromItemContainer(item);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        TestWindow.Pump();
    }

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
        Assert.Equal(("TEXT", "title", "“Hello”"), (Text(window, "InspectorKind"), Text(window, "InspectorName"), Text(window, "InspectorDetails")));

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
        Click(window, "UndoButton");
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

        Assert.Equal("Frame 0 / 59", Frame());
        TestWindow.Press(window, Key.Space);
        Assert.Equal("Pause (Space)", play.ToolTip);
        PumpWhile(() => Frame() == "Frame 0 / 59");
        TestWindow.Press(window, Key.Space);
        Assert.Equal("Play (Space)", play.ToolTip);
        var paused = Frame();
        Assert.NotEqual("Frame 0 / 59", paused);
        TestWindow.Pump(300);
        Assert.Equal(paused, Frame());

        TestWindow.Press(window, Key.Space);
        PumpWhile(() => (string)play.ToolTip == "Pause (Space)");
        Assert.Equal("Play (Space)", play.ToolTip);
        Assert.Equal("Frame 59 / 59", Frame());
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
            var row = list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<InspectorField>().Single(f => f.Label == "Position"));
            return Descendant<Button>((DependencyObject)row)!;
        }

        // The Position diamond clicked twice while its keyframes are being saved: the second click
        // takes them away again.
        var held = ProjectLock.Acquire(directory, TimeSpan.FromSeconds(1));
        Diamond().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Diamond().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("Saving…", Text(window, "SaveStatus"));
        held.Dispose();
        WaitForSaves(window);
        Assert.Equal((0, 0), (Title(directory).X.Keyframes.Count, Title(directory).Y.Keyframes.Count));
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

    // A project.json as saved, without its revision.
    private static string ProjectJson(string directory)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, ProjectStore.ProjectFileName)))!.AsObject();
        json.Remove("revision");
        return json.ToJsonString();
    }

    // The keys run from the shortcuts table: each key reaches its command (the Animate view's own in
    // that view), Ctrl+D duplicates, N turns snapping off, Ctrl+3, Ctrl+2, Ctrl+5, Ctrl+6, Ctrl+E and Ctrl+1
    // switch views, and ? and F1 open the list Esc closes.
    [Fact]
    public void EveryKeyRunsItsCommandFromTheShortcutsTable() => TestWindow.Run("window-keys", (window, folder) =>
    {
        Assert.Equal(Visibility.Collapsed, Shown(window, "WelcomePage"));
        var directory = WithTitle(window, folder);
        var animateOnly = new[] { Command.AddKeyframe, Command.EaseInOut, Command.OnionSkin };
        var sourceOnly = new[] { Command.MarkIn, Command.MarkOut, Command.ClearMarks, Command.Insert, Command.Overwrite };
        Assert.All(Shortcuts.All.Where(s => !animateOnly.Contains(s.Command) && !sourceOnly.Contains(s.Command)), s =>
        {
            Assert.True(window.CanRun(s.Command), $"{s.Command} can't run");
            Assert.Equal(s.Command, Shortcuts.For(s.Key, s.Modifiers).First(c => window.CanRun(c.Command)).Command);
        });
        Assert.All(animateOnly.Concat(sourceOnly), c => Assert.False(window.CanRun(c)));
        Assert.Equal(Command.Animate, window.RunKey(Key.D3, ModifierKeys.Control, inField: false));
        Assert.Equal(Visibility.Visible, Shown(window, "AnimatePage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "Workspace"));
        Assert.All(animateOnly, c => Assert.True(window.CanRun(c), $"{c} can't run"));
        Assert.True(window.CanRun(Command.Play));
        Assert.False(window.CanRun(Command.Split));
        Assert.Equal(Command.Audio, window.RunKey(Key.D2, ModifierKeys.Control, inField: false));
        Assert.Equal(Visibility.Visible, Shown(window, "AudioPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "AnimatePage"));
        Assert.True(window.CanRun(Command.Play));
        Assert.False(window.CanRun(Command.Delete));
        Assert.Equal(Command.Edit, window.RunKey(Key.D1, ModifierKeys.Control, inField: false));

        // Typing in a field, only Ctrl shortcuts are the window's, and undo isn't.
        Assert.Null(window.RunKey(Key.S, ModifierKeys.None, inField: true));
        Assert.Null(window.RunKey(Key.Z, ModifierKeys.Control, inField: true));
        Assert.False(window.CanRun(Command.Undo, inField: true));

        Assert.Equal(Command.Duplicate, window.RunKey(Key.D, ModifierKeys.Control, inField: false));
        WaitForSaves(window);
        Assert.Equal(["title", "title_2"], ProjectStore.Load(directory).Layers.Select(l => l.Id).Order());
        Assert.Equal("title_2 selected", Text(window, "SelectionText"));
        Assert.Equal("copied 'title' -> 'title_2'", Text(window, "MessageText"));

        var snap = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("SnapToggle");
        Assert.True(snap.IsChecked);
        Assert.Equal(Command.Snap, window.RunKey(Key.N, ModifierKeys.None, inField: false));
        Assert.False(snap.IsChecked);
        Assert.False(((TimelineView)window.FindName("Timeline")).Snapping);
        Assert.Contains("\"snapping\": false", File.ReadAllText(Path.Combine(folder, "editor.json")));

        Assert.Equal(Command.Captions, window.RunKey(Key.NumPad5, ModifierKeys.Control, inField: false));
        Assert.Equal(Visibility.Visible, Shown(window, "CaptionsPage"));
        Assert.Equal(Command.Activity, window.RunKey(Key.D6, ModifierKeys.Control, inField: false));
        Assert.Equal(Visibility.Visible, Shown(window, "ActivityPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "CaptionsPage"));
        Assert.Equal(Command.Export, window.RunKey(Key.E, ModifierKeys.Control, inField: false));
        Assert.Equal(Visibility.Visible, Shown(window, "ExportPage"));
        Assert.Equal(Command.Edit, window.RunKey(Key.NumPad1, ModifierKeys.Control, inField: false));
        Assert.Equal(Visibility.Visible, Shown(window, "Workspace"));

        Assert.Equal(Command.Shortcuts, window.RunKey(Key.OemQuestion, ModifierKeys.Shift, inField: false));
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "ShortcutsDialog"));
        var groups = (ItemsControl)window.FindName("ShortcutGroups");
        Assert.Equal(["Playback", "Editing", "Timeline", "Source viewer", "Animate", "Workspaces"], groups.Items.Cast<ShortcutGroup>().Select(g => g.Name));
        ((TextBox)window.FindName("ShortcutSearch")).Text = "ctrl d";
        Assert.Equal("Duplicate", Assert.Single(Assert.Single(groups.Items.Cast<ShortcutGroup>()).Rows).Action);
        ((TextBox)window.FindName("ShortcutSearch")).Text = "ctrl q";
        Assert.Empty(groups.Items);
        Assert.Equal(Visibility.Visible, Shown(window, "ShortcutsNone"));

        // While it's open, Esc is the window's only key.
        Assert.Null(window.RunKey(Key.Z, ModifierKeys.Control, inField: false));
        Assert.Null(window.RunKey(Key.N, ModifierKeys.Control, inField: false));
        Assert.Equal(2, ProjectStore.Load(directory).Layers.Count);
        TestWindow.Press(window, Key.Escape);
        Assert.Equal(Visibility.Collapsed, Shown(window, "DialogLayer"));
        Assert.Equal(Command.Shortcuts, window.RunKey(Key.F1, ModifierKeys.None, inField: false));
        Assert.Equal("", ((TextBox)window.FindName("ShortcutSearch")).Text);
        Assert.Equal(6, groups.Items.Count);
        Assert.Equal(Command.Cancel, window.RunKey(Key.Escape, ModifierKeys.None, inField: true));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ShortcutsDialog"));
    });

    // New project makes the project.json create_project makes from the same values, and opens it.
    [Fact]
    public void NewProjectMakesTheProjectCreateProjectMakesAndOpensIt() => TestWindow.Run("window-new-project", (window, folder) =>
    {
        var canvases = (ItemsControl)window.FindName("NewCanvases");
        string Canvas() => canvases.Items.Cast<CanvasChoice>().Single(c => c.Selected).Name;

        // Home's Square card opens it with the Square canvas.
        var square = ((Panel)window.FindName("StartCards")).Children.OfType<Button>().Single(b => b.Tag as string == "Square");
        square.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(Visibility.Visible, Shown(window, "NewProjectDialog"));
        Assert.Equal("Square", Canvas());
        Assert.Equal("1:1 · 1080×1080 · 30 fps", Text(window, "NewShape"));
        TestWindow.Press(window, Key.Escape);
        Assert.Equal(Visibility.Collapsed, Shown(window, "DialogLayer"));

        Assert.Equal(Command.NewProject, window.RunKey(Key.N, ModifierKeys.Control, inField: false));
        var name = (TextBox)window.FindName("NewName");
        Assert.Equal("New project", name.Text);
        Assert.Equal("YouTube", Canvas());
        Assert.Equal("16:9 · 1920×1080 · 30 fps", Text(window, "NewShape"));
        Assert.Equal("Fits YouTube and the youtube_1080p export preset.", Text(window, "NewExportNote"));

        // A size typed in chooses the card of that size.
        ((TextBox)window.FindName("NewWidth")).Text = "1080";
        Assert.Equal("Square", Canvas());
        ((TextBox)window.FindName("NewHeight")).Text = "1920";
        Assert.Equal("Shorts", Canvas());
        ((RadioButton)window.FindName("NewFps25")).IsChecked = true;
        ((RadioButton)window.FindName("NewSky")).IsChecked = true;
        Assert.Equal("9:16 · 1080×1920 · 25 fps", Text(window, "NewShape"));
        Assert.Equal("The shorts_1080p export preset has this size at 30 fps; Export's Project settings keeps 25 fps.", Text(window, "NewExportNote"));
        name.Text = "short-01";
        Click(window, "NewCreate");
        var directory = Path.Combine(folder, "projects", ProjectStore.FolderName("short-01"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "DialogLayer"));
        Assert.Equal($"Opened {Path.GetFileName(directory)}", Text(window, "MessageText"));

        var expected = Path.Combine(folder, "expected");
        ProjectSession.Create(expected, ProjectEditor.NewProject(new NewProjectOptions { Name = "short-01", Width = 1080, Height = 1920, Fps = 25, BackgroundColor = "#BFE3F7" }));
        Assert.Equal(ProjectJson(expected), ProjectJson(directory));

        // Again: the name is taken, and a size that isn't one can't be made.
        Assert.Equal(Command.NewProject, window.RunKey(Key.N, ModifierKeys.Control, inField: false));
        name.Text = "short-01";
        Click(window, "NewCreate");
        Assert.StartsWith("Couldn't create the project: ", Text(window, "NewError"));
        ((TextBox)window.FindName("NewWidth")).Text = "wide";
        Assert.Equal(NewProjectChoices.Custom, Canvas());
        Assert.Equal("Type the width and height in pixels.", Text(window, "NewShape"));
        Click(window, "NewCreate");
        Assert.Equal("Type the width and height as whole numbers of pixels.", Text(window, "NewError"));
        TestWindow.Press(window, Key.Escape);
        Assert.Equal(Visibility.Collapsed, Shown(window, "DialogLayer"));
        Assert.Equal(["short-01"], Directory.GetDirectories(Path.Combine(folder, "projects")).Select(d => ProjectStore.Load(d).Name));
    });

    // A project whose picture is gone opens with the Relink view, which finds the picture in the
    // import folder and refuses a file of another kind. Relinked as relink_asset relinks it, the
    // picture is drawn again.
    [Fact]
    public void RelinkingFindsAMissingPictureAndItIsDrawnAgain() => TestWindow.Run("window-relink", (window, folder) =>
    {
        var project = TestFiles.Project();
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 8, Height = 8 });
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "logo", Drawings = new Dictionary<string, string> { ["normal"] = "logo" } });
        var directory = Path.Combine(folder, "gone");
        ProjectSession.Create(directory, project);
        var media = Directory.CreateDirectory(Path.Combine(folder, "media", "pictures")).FullName;
        TestFiles.Png(Path.Combine(media, "logo.png"), 8, 8);
        File.WriteAllBytes(Path.Combine(media, "logo.wav"), [0]);
        window.OpenProject(directory);

        Assert.Equal(Visibility.Visible, Shown(window, "RelinkDialog"));
        Assert.Equal("1 file in gone can’t be found", Text(window, "RelinkTitle"));
        Assert.Equal("Relink and open", ((Button)window.FindName("RelinkButton")).Content);
        var file = Assert.Single(((ItemsControl)window.FindName("RelinkList")).Items.Cast<MissingFile>());
        Assert.Equal("used by 1 clip", file.UsedBy);
        for (var i = 0; i < 500 && file.Choice is null; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.Equal((Path.Combine(media, "logo.png"), "same size 8x8 px"), (file.Choice!.Path, file.Choice.Reason));
        Assert.True(((Button)window.FindName("RelinkButton")).IsEnabled);
        for (var i = 0; i < 500 && Shown(window, "PreviewMessage") != Visibility.Visible; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.StartsWith("Frame 0 can't be drawn", Text(window, "PreviewMessage"));

        window.RelinkTo("logo", Path.Combine(media, "logo.wav"));
        Assert.Equal(("'logo.wav' is an audio file, and asset 'logo' needs an image", true), (file.Status, file.Failed));
        Assert.Equal(Path.Combine(media, "logo.png"), file.Choice!.Path);

        Click(window, "RelinkButton");
        for (var i = 0; i < 500 && Shown(window, "DialogLayer") == Visibility.Visible; i++)
        {
            TestWindow.Pump(10);
        }

        WaitForSaves(window);
        Assert.Equal(Visibility.Collapsed, Shown(window, "DialogLayer"));
        Assert.Equal("relinked 'logo' to assets/logo.png", Text(window, "MessageText"));
        var asset = ProjectStore.Load(directory).FindAsset("logo")!;
        Assert.Equal(("assets/logo.png", 8, 8), (asset.File, asset.Width, asset.Height));
        Assert.True(File.Exists(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png")));
        for (var i = 0; i < 500 && Shown(window, "PreviewMessage") == Visibility.Visible; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.Equal(Visibility.Collapsed, Shown(window, "PreviewMessage"));
        Assert.Empty(Relinking.Missing(ProjectStore.Load(directory), directory));
    });

    // The cards of the Media browser's grid, in order.
    private static List<MediaItem> Cards(MainWindow window)
    {
        TestWindow.Pump();
        return ((ListBox)window.FindName("BrowserList")).Items.Cast<MediaItem>().ToList();
    }

    private static void SelectCards(MainWindow window, params string[] ids)
    {
        var list = (ListBox)window.FindName("BrowserList");
        list.SelectedItems.Clear();
        foreach (var card in list.Items.Cast<MediaItem>().Where(c => ids.Contains(c.Id)))
        {
            list.SelectedItems.Add(card);
        }

        TestWindow.Pump();
    }

    // The Media browser lists the kinds and the bins, searches the grid, and shows the selected
    // media's details. + tag and New bin save what tag_assets saves, the missing file's card opens
    // Relink, Add at playhead adds the clip the media panel's menu adds, and Back to edit returns.
    [Fact]
    public void TheMediaBrowserShowsTheShelvesAndTagsTheMedia() => TestWindow.Run("window-media-browser", (window, folder) =>
    {
        var project = TestFiles.Sounds();
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 8, Height = 8 });
        project.Assets.Add(new Asset { Id = "gone", Type = AssetType.Image, File = "assets/gone.png", Width = 8, Height = 8 });
        var directory = Path.Combine(folder, "browsed");
        ProjectSession.Create(directory, project);
        foreach (var (sound, frequency) in project.Assets.Where(a => a.Type == AssetType.Audio).Zip(new[] { 300.0, 1000, 2000 }))
        {
            TestFiles.Tone(Path.Combine(directory, sound.File), sound.Duration!.Value, frequency, 0.2);
        }

        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 8, 8);
        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "gone.png"), 8, 8);
        window.OpenProject(directory);
        File.Delete(Path.Combine(directory, ProjectStore.AssetsFolder, "gone.png"));

        Click(window, "MediaBrowserButton");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "MediaPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "Workspace"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ModeBar"));
        Assert.Equal("Media", ((System.Windows.Documents.Run)window.FindName("CrumbName")).Text);
        Assert.Equal(["All media", "Video", "Images", "Audio"], ((Panel)window.FindName("LibraryShelves")).Children.Cast<RadioButton>().Select(System.Windows.Automation.AutomationProperties.GetName));
        Assert.Empty(((Panel)window.FindName("BinShelves")).Children);
        Assert.Equal(Visibility.Visible, Shown(window, "BinsEmpty"));
        Assert.Equal("1 file missing", ((Button)window.FindName("MissingCard")).Content);
        Assert.Equal(["gone", "logo", "boing", "song", "talk"], Cards(window).Select(c => c.Id));
        Assert.Equal(["missing", "not used", "used 1×"], Cards(window).Take(3).Select(c => c.Use));
        Assert.True(window.CanRun(Command.Import));
        Assert.False(window.CanRun(Command.Split));

        ((TextBox)window.FindName("BrowserSearch")).Text = "SON";
        Assert.Equal(["song"], Cards(window).Select(c => c.Id));
        ((TextBox)window.FindName("BrowserSearch")).Text = "";
        Assert.Equal(Visibility.Visible, Shown(window, "BrowserNoSelection"));

        // + tag on two sounds saves tag_assets' change, and the tag is a bin.
        var expected = ProjectStore.Load(directory);
        SelectCards(window, "boing", "song");
        Assert.Equal(("2 files", "2 sounds"), (Text(window, "DetailTitle"), Text(window, "DetailSummary")));
        Descendants<Button>((Panel)window.FindName("DetailTags")).Single(b => Equals(b.Content, "+ tag")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var tagBox = (TextBox)window.FindName("TagBox");
        Assert.Equal(Visibility.Visible, tagBox.Visibility);
        tagBox.Text = "SFX";
        TestWindow.Press(tagBox, Key.Enter);
        WaitForSaves(window);
        new TagAssetsOperation(["boing", "song"], ["SFX"], []).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        Assert.Equal(["SFX"], ((Panel)window.FindName("BinShelves")).Children.Cast<RadioButton>().Select(System.Windows.Automation.AutomationProperties.GetName));
        Assert.Equal(Visibility.Collapsed, Shown(window, "BinsEmpty"));
        Assert.Equal(["boing", "song"], Cards(window).Where(c => ((ListBox)window.FindName("BrowserList")).SelectedItems.Contains(c)).Select(c => c.Id));

        // New bin puts the selected picture in it, and shows it.
        SelectCards(window, "logo");
        Assert.Equal(("logo.png", "PNG image · 8×8", "No layer yet"), (Text(window, "DetailTitle"), Text(window, "DetailSummary"), Text(window, "DetailUsedBy")));
        Click(window, "NewBinButton");
        var binBox = (TextBox)window.FindName("NewBinBox");
        binBox.Text = "Brand";
        TestWindow.Press(binBox, Key.Enter);
        WaitForSaves(window);
        new TagAssetsOperation(["logo"], ["Brand"], []).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        TestWindow.Pump();
        Assert.Equal(["Brand", "SFX"], ((Panel)window.FindName("BinShelves")).Children.Cast<RadioButton>().Select(System.Windows.Automation.AutomationProperties.GetName));
        Assert.Equal("Brand", ((System.Windows.Documents.Run)window.FindName("BrowserTitle")).Text);
        Assert.Equal(["logo"], Cards(window).Select(c => c.Id));

        // Add at playhead adds what the media panel's menu adds.
        Click(window, "BrowserAddButton");
        WaitForSaves(window);
        new AddClipOperation(AssetType.Image, "logo", 0).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        TestWindow.Pump();
        Assert.Equal("used 1×", Assert.Single(Cards(window)).Use);

        // The missing file's card opens Relink for it.
        Click(window, "MissingCard");
        Assert.Equal(Visibility.Visible, Shown(window, "RelinkDialog"));
        Assert.Equal("gone", Assert.Single(((ItemsControl)window.FindName("RelinkList")).Items.Cast<MissingFile>()).Id);
        TestWindow.Press(window, Key.Escape);
        Assert.Equal(Visibility.Collapsed, Shown(window, "DialogLayer"));

        Click(window, "BackToEdit");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "Workspace"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "MediaPage"));
    });

    // Closing a project from a view with Back to edit leaves Home showing, not that view.
    [Fact]
    public void ClosingAProjectFromAViewShowsHome() => TestWindow.Run("window-close-views", (window, folder) =>
    {
        var directory = Saved(folder, "closing");
        TestFiles.Tone(Path.Combine(directory, ProjectStore.AssetsFolder, "song.wav"), 2, 440, 0.2);
        ProjectSession.Open(directory).Mutate("import_asset song.wav", p =>
        {
            p.Assets.Add(new Asset { Id = "song", Type = AssetType.Audio, File = "assets/song.wav", Duration = 2 });
            return 0;
        });
        var views = new (string Page, Action Open)[]
        {
            ("MediaPage", () => Click(window, "MediaBrowserButton")),
            ("SourcePage", () =>
            {
                Click(window, "MediaBrowserButton");
                SelectCards(window, "song");
                Click(window, "BrowserSourceButton");
            }),
            ("TitlesPage", () => Click(window, "TitlesButton")),
            ("EffectsPage", () => Click(window, "EffectsButton")),
            ("ColorPage", () => Click(window, "ColorButton")),
        };
        foreach (var (page, open) in views)
        {
            window.OpenProject(directory);
            TestWindow.Pump();
            open();
            TestWindow.Pump();
            Assert.Equal(Visibility.Visible, Shown(window, page));
            Click(window, "LogoButton");
            TestWindow.Pump();
            Assert.Equal((page, Visibility.Visible, Visibility.Collapsed), (page, Shown(window, "StartPage"), Shown(window, page)));
        }
    });

    // The source viewer plays a file alone. I and O mark the part to use, which , inserts and .
    // overwrites at the timeline's playhead as the add-layer tools' edit does, on Video 1; Audio off
    // adds the clip with its sound off, and Alt+X clears the marks. A sound goes in a role.
    [Fact]
    public void TheSourceViewerInsertsAndOverwritesThePartMarked() => TestWindow.Run("window-source", (window, folder) =>
    {
        var project = TestFiles.Project(duration: 4);
        project.Assets.Add(new Asset { Id = "street", Type = AssetType.Video, File = "assets/street.mp4", Width = 160, Height = 120, FrameRate = "30", FrameCount = 90, Duration = 3, HasAudio = true, Codec = "h264" });
        project.Assets.Add(new Asset { Id = "song", Type = AssetType.Audio, File = "assets/song.wav", Duration = 2 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "intro", Start = 0, TrimOut = 1, ZIndex = 0 });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Asset = "street", Id = "main", Start = 1, TrimIn = 1, ZIndex = 0 });
        ProjectEditor.AddTextLayer(project, new TextLayerOptions { Id = "title", Text = "Hello", Start = 0, End = 3, ZIndex = 1 });
        var directory = Path.Combine(folder, "cut");
        ProjectSession.Create(directory, project);
        Directory.CreateDirectory(Path.Combine(directory, ProjectStore.AssetsFolder));
        TestFiles.Video(Path.Combine(directory, ProjectStore.AssetsFolder, "street.mp4"), "testsrc2=s=160x120:r=30:d=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "aac");
        TestFiles.Tone(Path.Combine(directory, ProjectStore.AssetsFolder, "song.wav"), 2, 440, 0.2);
        window.OpenProject(directory);
        var expected = ProjectStore.Load(directory);

        // The playhead at 1 s, where 'main' starts; then the footage in the source viewer.
        Assert.Equal(Command.NextEdit, window.RunKey(Key.Down, ModifierKeys.None, inField: false));
        Assert.False(window.CanRun(Command.MarkIn));
        Click(window, "MediaBrowserButton");
        SelectCards(window, "street");
        Click(window, "BrowserSourceButton");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "SourcePage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "MediaPage"));
        Assert.Equal("Source", ((System.Windows.Documents.Run)window.FindName("CrumbName")).Text);
        Assert.Equal(("street.mp4", "160×120 · 30 fps · 0:03"), (Text(window, "SourceName"), Text(window, "SourceInfo")));
        Assert.Equal("Video", ((Button)window.FindName("SourceTargetButton")).Content);
        Assert.Equal(Visibility.Visible, Shown(window, "SourceTracks"));
        Assert.Equal(("00:00.00", "3.00 s"), (Text(window, "SourceStamp"), Text(window, "SourceMarks")));
        Assert.Equal(Timecode.Of(30, 30), Text(window, "ProgramTime"));
        Assert.True(window.CanRun(Command.Play));
        Assert.False(window.CanRun(Command.Split));

        // In at 1 s, out on the frame before 2 s: the part from 1 to 2 s.
        Assert.Equal(Command.ForwardSecond, window.RunKey(Key.Right, ModifierKeys.Shift, inField: false));
        Assert.Equal(Command.MarkIn, window.RunKey(Key.I, ModifierKeys.None, inField: false));
        Assert.Equal("IN 00:01.00", Text(window, "SourceStamp"));
        Assert.Equal(Command.ForwardSecond, window.RunKey(Key.Right, ModifierKeys.Shift, inField: false));
        Assert.Equal(Command.PreviousFrame, window.RunKey(Key.Left, ModifierKeys.None, inField: false));
        Assert.Equal(Command.MarkOut, window.RunKey(Key.O, ModifierKeys.None, inField: false));
        Assert.Equal("In 00:01.00 · Out 00:02.00 · 1.00 s", Text(window, "SourceMarks"));
        var scrubber = (SourceScrubber)window.FindName("SourceScrubber");
        Assert.Equal((90, 59, 30, 59), (scrubber.Frames, scrubber.Frame, scrubber.InFrame, scrubber.OutFrame));
        Assert.Equal("Insert pushes everything from the playhead on 1.00 s to the right. Overwrite replaces what's there.", Text(window, "SourceNote"));

        // , inserts it at the playhead, which moves to its end.
        Assert.Equal(Command.Insert, window.RunKey(Key.OemComma, ModifierKeys.None, inField: false));
        WaitForSaves(window);
        new AddClipOperation(AssetType.Video, "street", 1, 0, null, 1, 2, false, ClipEdit.Insert).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        var inserted = WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected));
        Assert.Equal(2, ProjectStore.Load(directory).FindLayer("main")!.Start);
        Assert.StartsWith("inserted", Text(window, "MessageText"));
        TestWindow.Pump();
        Assert.Equal(Timecode.Of(60, 30), Text(window, "ProgramTime"));
        Assert.Equal(Visibility.Visible, Shown(window, "SourcePage"));

        // . overwrites the whole file there without its sound, once Alt+X has cleared the marks.
        Assert.Equal(Command.ClearMarks, window.RunKey(Key.X, ModifierKeys.Alt, inField: false));
        Assert.Equal("3.00 s", Text(window, "SourceMarks"));
        var audio = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("SourceAudio");
        Assert.True(audio.IsChecked);
        Toggle(audio, false);
        Assert.Equal(Command.Overwrite, window.RunKey(Key.OemPeriod, ModifierKeys.None, inField: false));
        WaitForSaves(window);
        new AddClipOperation(AssetType.Video, "street", 2, 0, null, 0, 3, true, ClipEdit.Overwrite).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        Assert.Equal(Timecode.Of(149, 30), Text(window, "ProgramTime"));

        // Undo takes the overwrite back, from the source viewer.
        Assert.Equal(Command.Undo, window.RunKey(Key.Z, ModifierKeys.Control, inField: false));
        WaitForSaves(window);
        Assert.Equal(inserted, ProjectJson(directory));

        // A sound goes in a role, Music unless the selected clip is a sound's; it has no Audio switch.
        Click(window, "BackToEdit");
        Click(window, "MediaBrowserButton");
        SelectCards(window, "song");
        Click(window, "BrowserSourceButton");
        TestWindow.Pump();
        Assert.Equal(("song.wav", "Music"), (Text(window, "SourceName"), ((Button)window.FindName("SourceTargetButton")).Content));
        Assert.Equal(Visibility.Collapsed, Shown(window, "SourceTracks"));
        Assert.Equal(Visibility.Visible, Shown(window, "SourceWave"));
        Assert.Equal("2.00 s", Text(window, "SourceMarks"));

        Click(window, "BackToEdit");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "Workspace"));
        Assert.False(window.CanRun(Command.Insert));
    });

    // The first time, the Welcome view shows the server's entry for Claude's config, turns green
    // when a client connects, and opens a copy of the sample.
    [Fact]
    public void TheWelcomeTurnsGreenWhenClaudeConnectsAndOpensTheSample() => TestWindow.Run("window-welcome", (window, folder) =>
    {
        Assert.Equal(Visibility.Visible, Shown(window, "WelcomePage"));
        Assert.Null(window.RunKey(Key.N, ModifierKeys.Control, inField: false));
        Assert.Equal("STEP 2 OF 3", Text(window, "WelcomeStepText"));
        Assert.Equal(Welcome.Config(Path.Combine(AppContext.BaseDirectory, Welcome.ServerFile)), ((TextBox)window.FindName("WelcomeConfig")).Text);
        Assert.Equal(Visibility.Visible, Shown(window, "WelcomeServerNote"));
        Assert.Equal("Waiting for Claude…", Text(window, "WelcomeStatusText"));
        List<WelcomeStep> Steps() => ((ItemsControl)window.FindName("WelcomeSteps")).Items.Cast<WelcomeStep>().ToList();
        Assert.Equal([true, false, false], Steps().Select(s => s.Done));
        Assert.Equal([false, true, false], Steps().Select(s => s.Current));

        Assert.True(ClientConnection.Write(Path.Combine(folder, "logs"), new ClientConnection("claude-ai", "0.14", DateTimeOffset.Now)));
        for (var i = 0; i < 300 && Text(window, "WelcomeStatusText") != "Connected: claude-ai"; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.Equal("Connected: claude-ai", Text(window, "WelcomeStatusText"));
        Assert.Equal([true, true, false], Steps().Select(s => s.Done));

        Click(window, "WelcomeNext");
        Assert.Equal("STEP 3 OF 3", Text(window, "WelcomeStepText"));
        Assert.Equal($"Open {Welcome.SampleName}", ((Button)window.FindName("WelcomeNext")).Content);
        Click(window, "WelcomeNext");
        var copy = Path.Combine(folder, "projects", Welcome.SampleName);
        Assert.True(File.Exists(Path.Combine(copy, ProjectStore.ProjectFileName)));
        Assert.Equal(Visibility.Collapsed, Shown(window, "WelcomePage"));
        Assert.Equal($"Opened {Welcome.SampleName}", Text(window, "MessageText"));
        Assert.True(File.Exists(Path.Combine(folder, "editor.json")));
    }, firstRun: true);

    // A project with a sound in each role open in the Audio view.
    private static string Audio(MainWindow window, string name)
    {
        var directory = TestFiles.NewSoundProject(name);
        window.OpenProject(directory);
        Assert.Equal(Command.Audio, window.RunKey(Key.D2, ModifierKeys.Control, inField: false));
        TestWindow.Pump();
        return directory;
    }

    private static T Strip<T>(MainWindow window, AudioRole role, string? content = null) where T : FrameworkElement
    {
        TestWindow.Pump();
        return Descendants<T>((ItemsControl)window.FindName("MixerStrips"))
            .Single(c => c.Tag is MixerStrip strip && strip.Role == role && (content is null || (c as ContentControl)?.Content as string == content));
    }

    private static void Toggle(System.Windows.Controls.Primitives.ToggleButton button, bool on)
    {
        button.IsChecked = on;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static void KeyUp(UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });

    // A project.json's text without its revision.
    private static string WithoutRevision(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("revision");
        return node.ToJsonString();
    }

    // The strips' fader, pan and M, the auto-duck switch and the ducking sliders each save one
    // change, the one update_project makes from the same values; undo takes it back.
    [Fact]
    public void TheMixerSavesEachRolesMixAndTheDuckingAsUpdateProjectDoes() => TestWindow.Run("window-audio-mix", (window, folder) =>
    {
        var directory = Audio(window, "window-audio-mix-project");
        var expected = ProjectStore.Load(directory);
        Assert.Equal(Visibility.Visible, Shown(window, "AudioPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "Workspace"));
        Assert.Equal(["Voice", "Music", "Sound effects"], ((ItemsControl)window.FindName("MixerStrips")).Items.Cast<MixerStrip>().Select(s => s.Name));
        Assert.Equal(["Loudness -14", "True-peak limit"], ((ItemsControl)window.FindName("MasterSlots")).Items.Cast<MixerSlot>().Select(s => s.Name));
        Assert.Equal("Target: YouTube -14 LUFS", ((Button)window.FindName("LoudnessTargetButton")).Content);

        void Same(ProjectOptions options, string change)
        {
            WaitForSaves(window);
            ProjectEditor.UpdateProject(expected, options);
            Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
            Assert.Contains(change, Text(window, "MessageText"));
        }

        // The music's fader from 0 down to -6 dB.
        var fader = Strip<MixerFader>(window, AudioRole.Music);
        fader.Press(fader.HandleAt(0));
        fader.MoveTo(fader.HandleAt(-6), pressed: true);
        fader.Release();
        Same(new ProjectOptions { Role = AudioRole.Music, Gain = -6 }, "music gain 0 dB → -6 dB");
        Assert.Equal("-6.0", Strip<MixerFader>(window, AudioRole.Music).Parent is StackPanel strip ? strip.Children.OfType<TextBlock>().Single().Text : null);

        // Its pan half left, the effects muted, a step down on the voice's fader.
        var pan = Strip<MixerPan>(window, AudioRole.Music);
        pan.Press(pan.HandleAt(-0.5));
        pan.Release();
        Same(new ProjectOptions { Role = AudioRole.Music, Pan = -0.5 }, "music pan center → 50% left");
        Toggle(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Effects, "M"), true);
        Same(new ProjectOptions { Role = AudioRole.Effects, Muted = true }, "effects muted");
        Assert.True(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Effects, "M").IsChecked);
        TestWindow.Press(Strip<MixerFader>(window, AudioRole.Voice), Key.Down);
        Same(new ProjectOptions { Role = AudioRole.Voice, Gain = -0.5 }, "voice gain 0 dB → -0.5 dB");

        // Ducking off and on, then deeper and slower to come back.
        var duck = (CheckBox)window.FindName("AutoDuck");
        Assert.True(duck.IsChecked);
        duck.IsChecked = false;
        duck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Same(new ProjectOptions { Ducking = false }, "music ducking off");
        Assert.False(((Grid)window.FindName("DuckSettings")).IsEnabled);
        duck.IsChecked = true;
        duck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Same(new ProjectOptions { Ducking = true }, "music ducking on");
        var level = (Slider)window.FindName("DuckLevelSlider");
        level.Value = 18;
        KeyUp(level, Key.Right);
        Same(new ProjectOptions { DuckLevel = -18 }, "duck level -12 dB → -18 dB");
        Assert.Equal("-18 dB", Text(window, "DuckLevelText"));
        var fadeOut = (Slider)window.FindName("DuckFadeOutSlider");
        fadeOut.Value = 2.5;
        KeyUp(fadeOut, Key.Right);
        Same(new ProjectOptions { DuckFadeOut = 2.5 }, "duck fade out 1.5s → 2.5s");

        // The target from the card's list.
        Click(window, "LoudnessTargetButton");
        ChooseInMenu("Podcast");
        Same(new ProjectOptions { LoudnessTarget = -16 }, "-16 LUFS");
        Assert.Equal("Target: Podcast -16 LUFS", ((Button)window.FindName("LoudnessTargetButton")).Content);

        // One step back at a time.
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal(-14, ProjectStore.Load(directory).Audio.LoudnessTarget);
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal(1.5, ProjectStore.Load(directory).Audio.DuckFadeOut);
        Assert.Equal(1.5, ((Slider)window.FindName("DuckFadeOutSlider")).Value);
    });

    // Solo is the preview's: the sound plays only the soloed roles, and nothing is saved.
    [Fact]
    public void SoloIsHeardInThePreviewAndNeverSaved() => TestWindow.Run("window-audio-solo", (window, folder) =>
    {
        var directory = Audio(window, "window-audio-solo-project");
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        var saved = File.ReadAllText(file);
        var backups = Directory.Exists(Path.Combine(directory, "backups")) ? Directory.GetFiles(Path.Combine(directory, "backups")).Length : 0;
        Assert.Same(ProjectStore.Load(directory).GetType(), window.Heard.GetType());
        Assert.False(window.Heard.Audio.Voice.Muted);

        Toggle(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Music, "S"), true);
        WaitForSaves(window);
        Assert.Equal("The preview plays only music; nothing is saved", Text(window, "MessageText"));
        Assert.Equal((true, false, true), (window.Heard.Audio.Voice.Muted, window.Heard.Audio.Music.Muted, window.Heard.Audio.Effects.Muted));
        Assert.True(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Music, "S").IsChecked);
        Toggle(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Voice, "S"), true);
        Assert.Equal("The preview plays only voice and music; nothing is saved", Text(window, "MessageText"));
        Assert.Equal((false, false, true), (window.Heard.Audio.Voice.Muted, window.Heard.Audio.Music.Muted, window.Heard.Audio.Effects.Muted));

        // Solo stays while another view shows, and goes with the project.
        Assert.Equal(Command.Edit, window.RunKey(Key.D1, ModifierKeys.Control, inField: false));
        Assert.True(window.Heard.Audio.Effects.Muted);
        Assert.Equal(Command.Audio, window.RunKey(Key.D2, ModifierKeys.Control, inField: false));
        Assert.True(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Voice, "S").IsChecked);
        WaitForSaves(window);
        Assert.Equal(saved, File.ReadAllText(file));
        Assert.Equal(backups, Directory.Exists(Path.Combine(directory, "backups")) ? Directory.GetFiles(Path.Combine(directory, "backups")).Length : 0);
        Assert.False(ProjectStore.Load(directory).Audio.Effects.Muted);

        // Another project opens with every role heard.
        window.OpenProject(TestFiles.NewSoundProject("window-audio-solo-other"));
        Assert.False(window.Heard.Audio.Effects.Muted);
        Assert.Equal(Command.Audio, window.RunKey(Key.D2, ModifierKeys.Control, inField: false));
        Assert.False(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Voice, "S").IsChecked);
    });

    // Pressing a clip's volume line adds a keyframe at the level it has there; dragging a dot moves
    // it on the fader's scale; Delete takes the selected one away. Each is what set_keyframes,
    // remove_keyframes and their like make, one step each.
    [Fact]
    public void VolumePointsAreAddedDraggedAndDeletedAsKeyframes() => TestWindow.Run("window-audio-volume", (window, folder) =>
    {
        var directory = Audio(window, "window-audio-volume-project");
        var expected = ProjectStore.Load(directory);
        var lanes = (VolumeView)window.FindName("VolumeLanes");
        Assert.Equal([["talk"], ["song"], ["boing"]], lanes.Lanes.Select(l => l.Clips.Select(c => c.LayerId).ToArray()));
        AudioLayer Song() => (AudioLayer)ProjectStore.Load(directory).FindLayer("song")!;

        void Same()
        {
            WaitForSaves(window);
            Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        }

        // A press on the music's line at 2 s: a keyframe at full volume, so the sound is the same.
        lanes.Press(lanes.LinePoint("song", 2));
        lanes.Release();
        KeyframeEditor.SetKeyframes(expected, [new KeyframeEntry("song", "volume", 2, System.Text.Json.JsonSerializer.SerializeToElement(1.0))]);
        Same();
        Assert.Equal(new KeyframeRef("song", "volume", 2), lanes.Selected);

        // Pressed at 3.5 s and dragged down: a second keyframe, quieter, to the half dB.
        var at = lanes.LinePoint("song", 3.5);
        lanes.Press(at);
        lanes.MoveTo(new Point(at.X, at.Y + 14), pressed: true);
        lanes.Release();
        WaitForSaves(window);
        var quieter = Song().Volume.Keyframes.Single(k => k.Time == 3.5);
        Assert.InRange(quieter.Value, 0.05, 0.9);
        Assert.Equal(quieter.Value, MixerModel.VolumeAt(MixerModel.LevelPosition(quieter.Value)));
        KeyframeEditor.SetKeyframes(expected, [new KeyframeEntry("song", "volume", 3.5, System.Text.Json.JsonSerializer.SerializeToElement(quieter.Value))]);
        Same();
        Assert.Equal(new KeyframeRef("song", "volume", 3.5), lanes.Selected);

        // The first dot dragged up and a second later: louder, at 3 s.
        var dot = lanes.KeyPoint("song", 2)!.Value;
        var perSecond = lanes.LinePoint("song", 3).X - lanes.LinePoint("song", 2).X;
        lanes.Press(dot);
        lanes.MoveTo(new Point(dot.X + perSecond, dot.Y - 6), pressed: true);
        lanes.Release();
        WaitForSaves(window);
        var louder = Song().Volume.Keyframes.Single(k => k.Time == 3);
        Assert.True(louder.Value > 1, $"{louder.Value}");
        KeyframeEditor.MoveKeyframes(expected, [new KeyframeMove("song", "volume", 2, 3, louder.Value)]);
        Same();
        Assert.StartsWith("moved song.volume's keyframe from 2s to 3s", Text(window, "MessageText"));

        // Delete takes the selected one away; undo puts it back.
        Assert.Equal(Command.Delete, window.RunKey(Key.Delete, ModifierKeys.None, inField: false));
        KeyframeEditor.RemoveKeyframes(expected, [new KeyframeRef("song", "volume", 3)]);
        Same();
        Assert.Equal([3.5], Song().Volume.Keyframes.Select(k => k.Time));
        Assert.Null(lanes.Selected);
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal([3, 3.5], Song().Volume.Keyframes.Select(k => k.Time));

        // A press off the line selects the clip's layer and moves the playhead there.
        var line = lanes.LinePoint("talk", 2);
        lanes.Press(new Point(line.X, line.Y + 18));
        lanes.Release();
        Assert.Equal(Timecode.Of(60, 30), Text(window, "AudioTimecode"));
        Assert.Equal(Command.Edit, window.RunKey(Key.D1, ModifierKeys.Control, inField: false));
        Assert.Equal("talk selected", Text(window, "SelectionText"));
    });

    // Measure gives audio_levels' loudness of the mix as exported; a change to the mix clears it.
    [Fact]
    public void TheLoudnessCardMeasuresTheMixAsAudioLevelsDoes() => TestWindow.Run("window-audio-loudness", (window, folder) =>
    {
        var ffmpeg = TestFiles.Ffmpeg();
        var directory = Audio(window, "window-audio-loudness-project");
        Assert.Equal("-", Text(window, "LoudnessLufs"));
        Assert.StartsWith("Every export is normalized to -14 LUFS.", Text(window, "LoudnessText"));

        Click(window, "MeasureButton");
        for (var i = 0; i < 600 && Text(window, "LoudnessLufs") == "-"; i++)
        {
            TestWindow.Pump(50);
        }

        var report = ZeyoCut.Render.Export.AudioLevels.Measure(ProjectStore.Load(directory), directory, ffmpeg, ZeyoCut.Render.Export.AudioLevels.DefaultSection, null, TestContext.Current.CancellationToken)!;
        Assert.Equal(report.Lufs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), Text(window, "LoudnessLufs"));
        Assert.Equal(report.PeakDb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), Text(window, "LoudnessPeak"));
        Assert.Equal(MixerModel.LoudnessNote(report, -14), Text(window, "LoudnessText"));
        Assert.StartsWith("Before normalizing, the mix is ", Text(window, "LoudnessText"));
        Assert.Equal("Measure", Text(window, "MeasureText"));

        // The music muted: the mix measured is gone.
        Toggle(Strip<System.Windows.Controls.Primitives.ToggleButton>(window, AudioRole.Music, "M"), true);
        WaitForSaves(window);
        Assert.Equal("-", Text(window, "LoudnessLufs"));
    });

    // A click on the grip where a clip meets the next chooses that cut, and the Effects view puts the
    // chosen transition on it as add_transition does; Apply to every cut puts it on each cut of the lane
    // as one undo step. A video effect and a way in and out go on the selected layer.
    [Fact]
    public void TheEffectsViewPutsTransitionsOnTheCutsChosenOnTheTimeline() => TestWindow.Run("window-effects", (window, folder) =>
    {
        var directory = Saved(folder, "cuts");
        ProjectSession.Open(directory).Mutate("add_image_layer", p =>
        {
            p.Duration = 3;
            foreach (var (id, start) in new[] { ("a", 0.0), ("b", 1.0), ("c", 2.0) })
            {
                ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Id = id, Start = start, End = start + 1, ZIndex = 0 });
            }

            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hi", Start = 0, End = 3, ZIndex = 1 });
            return 0;
        });
        window.OpenProject(directory);
        TestWindow.Pump();
        var timeline = (TimelineView)window.FindName("Timeline");
        var lane = timeline.Lanes.ToList().FindIndex(l => l.Clips.Any(c => c.LayerId == "a"));
        double X(double time) => TimelineView.HeaderWidth + time * timeline.PixelsPerSecond - timeline.HorizontalOffset;
        var middle = TimelineView.RulerHeight + timeline.Lanes.Take(lane).Sum(TimelineView.LaneHeight) + 3 + TimelineView.RowHeight(LaneKind.Visual) / 2;

        // A click without a drag on 'a''s end chooses the cut; nothing is saved.
        var before = ProjectJson(directory);
        timeline.Press(new Point(X(1) - 2, middle));
        timeline.Release();
        WaitForSaves(window);
        Assert.Equal("a → b", timeline.Cut!.Label);
        Assert.Equal(before, ProjectJson(directory));

        Click(window, "EffectsButton");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "EffectsPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ModeBar"));
        Assert.Equal("Effects", ((System.Windows.Documents.Run)window.FindName("CrumbName")).Text);
        var categories = ((Panel)window.FindName("EffectCategories")).Children.Cast<RadioButton>().ToList();
        Assert.Equal(["Transitions", "Video effects", "Text animations"], categories.Select(System.Windows.Automation.AutomationProperties.GetName));
        Assert.Equal(("a → b", "Selected cut · 00:01.00 · Images"), (Text(window, "EffectTarget"), Text(window, "CutTitle")));
        var apply = (Button)window.FindName("EffectApplyButton");
        var every = (Button)window.FindName("EffectEveryButton");
        Assert.Equal(("Apply to selected cut", true, "Apply to every cut (2)"), (apply.Content, apply.IsEnabled, every.Content));

        // Slide from the top for 0.4 s, as add_transition puts it on.
        var tiles = (ListBox)window.FindName("EffectList");
        void Choose(string name)
        {
            tiles.SelectedItem = tiles.Items.Cast<EffectTile>().Single(t => t.Name == name);
            TestWindow.Pump();
        }

        Choose("Slide");
        var directions = ((Panel)window.FindName("EffectDirections")).Children.Cast<RadioButton>().ToList();
        Assert.Equal(["From right", "From left", "From bottom", "From top"], directions.Select(System.Windows.Automation.AutomationProperties.GetName));
        Assert.True(directions[0].IsChecked);
        directions[3].IsChecked = true;
        var length = (TextBox)window.FindName("EffectDuration");
        length.Text = "0.4";
        TestWindow.Press(length, Key.Enter);
        var expected = ProjectStore.Load(directory);
        Click(window, "EffectApplyButton");
        WaitForSaves(window);
        new AddTransitionOperation("a", "b", TransitionType.Slide, 0.4, TransitionDirection.Down).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));

        // The clips overlap now, so the cut isn't one; undone, it is again.
        TestWindow.Pump();
        Assert.False(apply.IsEnabled);
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal(before, ProjectJson(directory));
        TestWindow.Pump();
        Assert.True(apply.IsEnabled);

        // A crossfade on both cuts of the lane is one step.
        Choose("Crossfade");
        Assert.Equal("None: it has no direction", Assert.IsType<TextBlock>(Assert.Single(((Panel)window.FindName("EffectDirections")).Children)).Text);
        var revision = ProjectStore.Load(directory).Revision;
        expected = ProjectStore.Load(directory);
        Click(window, "EffectEveryButton");
        WaitForSaves(window);
        new AddTransitionOperation("a", "b", TransitionType.Crossfade, 0.4).Apply(expected);
        new AddTransitionOperation("b", "c", TransitionType.Crossfade, 0.4).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        Assert.Equal(revision + 1, ProjectStore.Load(directory).Revision);
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal(before, ProjectJson(directory));

        // A shadow on the selected title, then a pop in and out.
        window.SelectLayer("title");
        categories[1].IsChecked = true;
        TestWindow.Pump();
        Choose("Shadow");
        Assert.Equal(("title", "Add to selected layer"), (Text(window, "EffectTarget"), apply.Content));
        expected = ProjectStore.Load(directory);
        Click(window, "EffectApplyButton");
        WaitForSaves(window);
        new SetEffectOperation("title", EffectKind.Shadow, new EffectValues(null, null, null, null, null, null, null)).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        TestWindow.Pump();
        Assert.Equal("Take the shadow off", apply.Content);

        categories[2].IsChecked = true;
        TestWindow.Pump();
        Choose("Pop");
        Assert.Equal(("Use to come in", "Use to go out"), (apply.Content, every.Content));
        Click(window, "EffectApplyButton");
        WaitForSaves(window);
        Click(window, "EffectEveryButton");
        WaitForSaves(window);
        new TitleAnimationOperation("title", TitleSide.In, TitleAnimation.Pop).Apply(expected);
        new TitleAnimationOperation("title", TitleSide.Out, TitleAnimation.Pop).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        TestWindow.Pump();
        Assert.Equal("title · in: Pop · out: Pop", Text(window, "EffectTarget"));

        Click(window, "BackToEdit");
        TestWindow.Pump();
        Assert.Equal((Visibility.Visible, Visibility.Collapsed), (Shown(window, "Workspace"), Shown(window, "EffectsPage")));
    });

    // The Titles view shows the library's styles and templates, drawn. Add at playhead adds the fields
    // in the chosen style with their presets, as add_text_layer adds them; the selected title's fields
    // and presets change it, Apply puts the style back, Update saved style saves its look over the
    // style, and a template goes in from the playhead as add_template puts it.
    // The Track & blur view tracks a box drawn on the frame as track_object does, hides it as
    // apply_redaction and update_layer do, and corrects it where its box is dragged to as
    // reanchor_track does; each is one step to undo.
    [Fact]
    public void TheTrackViewTracksADrawnBoxHidesItAndCorrectsIt() => TestWindow.Run("window-track", (window, folder) =>
    {
        var project = TestFiles.Project(640, 360, 2);
        project.Fps = 30;
        project.Assets.Add(new Asset { Id = "square", Type = AssetType.Video, File = "assets/square.mp4", Width = 320, Height = 180, FrameRate = "30", FrameCount = 60, Duration = 2, Codec = "h264" });
        ProjectEditor.AddVideoLayer(project, new VideoLayerOptions { Id = "clip", Asset = "square" });
        var directory = Path.Combine(folder, "square");
        ProjectSession.Create(directory, project);
        Directory.CreateDirectory(Path.Combine(directory, ProjectStore.AssetsFolder));

        // A white 40 px square on black, from 20,60 moving right 2 px a frame; the clip is twice its size.
        TestFiles.Video(Path.Combine(directory, ProjectStore.AssetsFolder, "square.mp4"),
            "color=c=black:s=320x180:r=30:d=2[bg];color=c=white:s=40x40:r=30:d=2[sq];[bg][sq]overlay=x=20+60*t:y=60[out0]");
        window.OpenProject(directory);
        Assert.Equal(Command.Track, window.RunKey(Key.D4, ModifierKeys.Control, inField: false));
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "TrackPage"));
        Assert.Equal(Visibility.Visible, Shown(window, "ModeBar"));
        Assert.Equal(("on clip", Visibility.Visible), (Text(window, "TrackClipText"), Shown(window, "TrackEmpty")));
        Assert.True(window.CanRun(Command.NextFrame));
        Assert.False(window.CanRun(Command.Play));
        var viewer = (TrackView)window.FindName("TrackViewer");
        var draw = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("TrackDraw");
        var forward = (Button)window.FindName("TrackForward");
        Assert.True(draw.IsEnabled);
        Assert.False(forward.IsEnabled);
        void Drag(Point from, Point to)
        {
            viewer.Press(viewer.ToView(from));
            viewer.MoveTo(viewer.ToView(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2)), pressed: true);
            viewer.MoveTo(viewer.ToView(to), pressed: true);
            viewer.Release();
            TestWindow.Pump();
        }

        void WaitForTracking()
        {
            TestWindow.Pump();
            for (var i = 0; i < 3000 && Shown(window, "TrackProgressPanel") == Visibility.Visible; i++)
            {
                TestWindow.Pump(10);
            }

            WaitForSaves(window);
        }

        Track Saved() => ProjectStore.Load(directory).Tracks.Single();

        // Draw a box around the square at frame 0 (40,120 80x80 on the canvas), and Esc drops it.
        draw.IsChecked = true;
        draw.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.True(viewer.Drawing);
        Drag(new Point(40, 120), new Point(120, 200));
        Assert.False(viewer.Drawing);
        Assert.True(forward.IsEnabled);
        Assert.Equal(Command.Cancel, window.RunKey(Key.Escape, ModifierKeys.None, inField: false));
        Assert.False(forward.IsEnabled);
        draw.IsChecked = true;
        draw.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Drag(new Point(40, 120), new Point(120, 200));
        var drawn = viewer.Pending!.Value;
        Assert.Equal(new Rect(40, 120, 80, 80), new Rect(Math.Round(drawn.X, 6), Math.Round(drawn.Y, 6), Math.Round(drawn.Width, 6), Math.Round(drawn.Height, 6)));

        // Forward: the track track_object makes, from frame 0 to the clip's end, chosen.
        Click(window, "TrackForward");
        WaitForTracking();
        var tracked = Saved();
        Assert.Equal(("track_1", "clip", TrackMethod.Csrt, TrackDirection.Forward, 0, 59, 0), (tracked.Id, tracked.Layer, tracked.Method, tracked.Direction, tracked.FirstFrame, tracked.LastFrame, tracked.Origin));
        var anchor = tracked.Anchors.Single();
        Assert.Equal((0, 20.0, 60.0, 40.0, 40.0), (anchor.Frame, Math.Round(anchor.X, 6), Math.Round(anchor.Y, 6), Math.Round(anchor.Width, 6), Math.Round(anchor.Height, 6)));
        var firstFile = tracked.File;
        var halfway = TrackFiles.Load(directory, tracked).Frames.Single(f => f.Frame == 30);
        Assert.InRange(halfway.X, 75, 85);
        var card = ((IEnumerable<TrackCard>)((ItemsControl)window.FindName("TrackCards")).ItemsSource).Single();
        Assert.Equal(("track_1 · Object", "Not hidden", "0.00 – 2.00 s · 60 frames", true), (card.Title, card.Hide, card.Span, card.Selected));
        Assert.Equal(["track_1 · set by hand"], viewer.Shapes.Select(s => s.Label));
        Assert.Equal("Not hidden yet: choose how to hide it.", Text(window, "TrackSettingsHint"));
        Assert.False(((Slider)window.FindName("TrackStrength")).IsEnabled);

        // Hide it with a blur as apply_redaction does, then an ellipse, smoothed, strength 80.
        var blur = (RadioButton)window.FindName("TrackBlur");
        blur.IsChecked = true;
        blur.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        WaitForSaves(window);
        ((RadioButton)window.FindName("TrackEllipse")).IsChecked = true;
        WaitForSaves(window);
        var smooth = (CheckBox)window.FindName("TrackSmooth");
        smooth.IsChecked = true;
        smooth.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        WaitForSaves(window);
        var strength = (Slider)window.FindName("TrackStrength");
        Assert.True(strength.IsEnabled);
        strength.Value = 80;
        strength.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        WaitForSaves(window);
        var redaction = Assert.IsType<RedactionLayer>(ProjectStore.Load(directory).FindLayer("redaction_1"));
        Assert.Equal(["track_1"], redaction.Tracks);
        Assert.Equal((RedactionStyle.Blur, 80.0, RedactionShape.Ellipse, true), (redaction.Style, redaction.Strength, redaction.Shape, redaction.Smooth));
        Assert.Equal(("Blur", "80"), (((IEnumerable<TrackCard>)((ItemsControl)window.FindName("TrackCards")).ItemsSource).Single().Hide, Text(window, "TrackStrengthText")));
        Assert.Equal("Hidden by 'redaction_1' in every clip of 'square'.", Text(window, "TrackSettingsHint"));

        // The viewer's modes.
        foreach (var (name, mode) in new[] { ("TrackResult", TrackViewMode.Result), ("TrackSplit", TrackViewMode.Split), ("TrackBoxes", TrackViewMode.Boxes) })
        {
            ((RadioButton)window.FindName(name)).IsChecked = true;
            TestWindow.Pump();
            Assert.Equal(mode, viewer.Mode);
        }

        // At frame 40 (from the tracking timeline), the box dragged onto the square, 200,120 80x80:
        // tracked again from there to the end, as reanchor_track does.
        var strip = (TrackStrip)window.FindName("TrackTimeline");
        strip.Press(new Point(strip.XOf(40 / 30.0), TrackStrip.RulerHeight + 10));
        strip.Release();
        TestWindow.Pump();
        Assert.Equal("frame 40 · 1.33 s", Text(window, "TrackFrameText"));
        var box = viewer.Shapes.Single().Bounds;
        Drag(new Point(box.X + box.Width / 2, box.Y + box.Height / 2), new Point(240, 160));
        WaitForTracking();
        var corrected = Saved();
        Assert.Equal([0, 40], corrected.Anchors.Select(a => a.Frame));
        var set = corrected.Anchors[1];
        Assert.Equal((120.0, 80.0), (Math.Round(set.X + set.Width / 2, 6), Math.Round(set.Y + set.Height / 2, 6)));
        Assert.NotEqual(firstFile, corrected.File);
        Assert.Equal(["track_1 · set by hand"], viewer.Shapes.Select(s => s.Label));
        Assert.Equal([0, 1.333], strip.Rows.Single().Anchors.Select(a => Math.Round(a, 3)));

        // Undo takes the correction back.
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal(firstFile, Saved().File);
    });

    // The Color view grades a clip as set_color does: a slider when it's let go, a value typed, Copy
    // and Paste grade, Apply to all clips of the footage and Reset all; the viewer has the clip as
    // shot beside the preview's frame, and the scopes are of that frame.
    [Fact]
    public void TheColorViewGradesAClipAsSetColorDoes() => TestWindow.Run("window-color", (window, folder) =>
    {
        var directory = Saved(folder, "graded");
        ProjectSession.Open(directory).Mutate("add_image_layer", p =>
        {
            p.Duration = 3;
            foreach (var (id, start) in new[] { ("card_a", 0.0), ("card_b", 1.0), ("card_c", 2.0) })
            {
                ProjectEditor.AddImageLayer(p, new ImageLayerOptions { Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Id = id, Start = start, End = start + 1, ZIndex = 0, ScaleX = 40, ScaleY = 20 });
            }

            ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hi", Start = 0, End = 3, ZIndex = 1 });
            return 0;
        });
        window.OpenProject(directory);
        TestWindow.Pump();
        Project Load() => ProjectStore.Load(directory);
        ImageLayer Card(string id) => (ImageLayer)Load().FindLayer(id)!;

        // The title is selected, so the view grades the picture in front at the playhead.
        window.SelectLayer("title");
        Click(window, "ColorButton");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "ColorPage"));
        Assert.Equal(Visibility.Collapsed, Shown(window, "ModeBar"));
        Assert.Equal("Color", ((System.Windows.Documents.Run)window.FindName("CrumbName")).Text);
        Assert.Equal(("card_a", "Color · applies to this clip"), (Text(window, "ColorClipText"), Text(window, "ColorNote")));
        TextBox Readout(string name) => (TextBox)window.FindName($"Color{name}Text");
        Assert.All(new[] { "Exposure", "Contrast", "Highlights", "Shadows", "Temperature", "Tint", "Saturation" }, name => Assert.Equal("0", Readout(name).Text));
        Button Button(string name) => (Button)window.FindName(name);
        Assert.Equal((false, false, "Apply to all logo clips", true), (Button("ColorReset").IsEnabled, Button("ColorPaste").IsEnabled, Button("ColorApply").Content, Button("ColorApply").IsEnabled));
        Assert.True(window.CanRun(Command.NextFrame));
        Assert.False(window.CanRun(Command.Play));

        // Exposure +0.5: the readout and the preview follow the slider, and it's saved when let go,
        // as set_color saves it.
        var before = Load();
        var exposure = (Slider)window.FindName("ColorExposure");
        Assert.All(new[] { ("Exposure", 0.01), ("Contrast", 1), ("Highlights", 1), ("Shadows", 1), ("Temperature", 1), ("Tint", 1), ("Saturation", 1) },
            row => Assert.Equal((true, row.Item2), (((Slider)window.FindName("Color" + row.Item1)).IsSnapToTickEnabled, ((Slider)window.FindName("Color" + row.Item1)).TickFrequency)));
        exposure.Value = 0.5;
        Assert.Equal("+0.5", Readout("Exposure").Text);
        WaitForSaves(window);
        Assert.Null(Card("card_a").Grade);
        exposure.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        WaitForSaves(window);
        new SetColorOperation("card_a", new ColorChange(new Dictionary<string, double> { ["exposure"] = 0.5 })).Apply(before);
        var saved = Load();
        before.Revision = saved.Revision;
        Assert.Equal(ZeyoCut.Core.Serialization.ProjectJson.Serialize(before), ZeyoCut.Core.Serialization.ProjectJson.Serialize(saved));
        Assert.True(Button("ColorReset").IsEnabled);

        // Contrast typed as the readout shows it; a saturation set_color doesn't take is refused.
        Readout("Contrast").Text = "+12";
        TestWindow.Press(Readout("Contrast"), Key.Enter);
        WaitForSaves(window);
        Assert.Equal(1.12, Card("card_a").Contrast.Value, 9);
        Readout("Saturation").Text = "500";
        TestWindow.Press(Readout("Saturation"), Key.Enter);
        WaitForSaves(window);
        Assert.Equal(("0", "Saturation is a number from −100 to +300"), (Readout("Saturation").Text, Text(window, "MessageText")));
        Assert.Equal(1, Card("card_a").Saturation.Value);

        // The viewer: the clip as shot left of the line, the preview's frame right of it.
        var viewer = (GradeView)window.FindName("GradeViewer");
        for (var i = 0; i < 500 && viewer.Before is null; i++)
        {
            TestWindow.Pump(10);
        }

        static byte[] Pixels(BitmapSource bitmap)
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        var shot = Pixels((BitmapSource)viewer.Before!);
        var graded = Pixels((BitmapSource)viewer.Frame!);
        var middle = (90 * 320 + 160) * 4;
        Assert.Equal((0, 165, 255), (shot[middle], shot[middle + 1], shot[middle + 2]));
        Assert.True(graded[middle + 1] > 190, $"the graded orange's green is {graded[middle + 1]}");
        Assert.Equal((GradeViewMode.Split, 0.5), (viewer.Mode, viewer.Split));
        var frame = viewer.FrameRect;
        viewer.Press(new Point(frame.X + frame.Width / 4, frame.Y + 10));
        viewer.MoveTo(new Point(frame.X + frame.Width * 0.3, frame.Y + 10), pressed: true);
        viewer.Release();
        Assert.Equal(0.3, viewer.Split, 6);
        foreach (var (name, mode) in new[] { ("ColorAfter", GradeViewMode.After), ("ColorBefore", GradeViewMode.Before), ("ColorSplit", GradeViewMode.Split) })
        {
            ((RadioButton)window.FindName(name)).IsChecked = true;
            TestWindow.Pump();
            Assert.Equal(mode, viewer.Mode);
        }

        // The scopes are of the preview's frame: mostly the gray background, and the orange.
        var vectorscope = (BitmapSource)((Image)window.FindName("VectorscopeImage")).Source;
        var scope = Pixels(vectorscope);
        Assert.Equal((151, 151), (vectorscope.PixelWidth, vectorscope.PixelHeight));
        var (ox, oy) = Scopes.Spot(graded[middle + 2], graded[middle + 1], graded[middle], 151);
        Assert.True(scope[(oy * 151 + ox) * 4 + 3] > 0, "the orange isn't on the vectorscope");
        Assert.NotNull(((Image)window.FindName("WaveformImage")).Source);

        // Copy grade on card_a, Paste grade on card_c, then every logo clip gets card_c's color.
        Click(window, "ColorCopy");
        Click(window, "BackToEdit");
        window.SelectLayer("card_c");
        Click(window, "ColorButton");
        TestWindow.Pump();
        Assert.Equal(("card_c", "Color · applies to this clip, which isn't at the playhead"), (Text(window, "ColorClipText"), Text(window, "ColorNote")));
        Assert.True(Button("ColorPaste").IsEnabled);
        Click(window, "ColorPaste");
        WaitForSaves(window);
        Assert.Equal((0.5, 1.12), (Card("card_c").Grade!.Exposure.Value, Card("card_c").Contrast.Value));
        Assert.Equal("+12", Readout("Contrast").Text);
        Click(window, "ColorApply");
        WaitForSaves(window);
        Assert.Equal((0.5, 1.12), (Card("card_b").Grade!.Exposure.Value, Card("card_b").Contrast.Value));

        // Reset all puts card_c back as shot; undo brings its color back.
        Click(window, "ColorReset");
        WaitForSaves(window);
        Assert.False(ColorEditor.Graded(Card("card_c")));
        Assert.NotNull(Card("card_b").Grade);
        Assert.Equal("0", Readout("Exposure").Text);
        Click(window, "UndoButton");
        WaitForSaves(window);
        Assert.Equal(0.5, Card("card_c").Grade!.Exposure.Value);
        Assert.Equal("+0.5", Readout("Exposure").Text);

        // Back in the Edit view, the inspector says what the grade is.
        Click(window, "BackToEdit");
        TestWindow.Pump();
        Assert.Equal(Visibility.Collapsed, Shown(window, "ColorPage"));
        var project = Load();
        var grade = InspectorFields.Layer(project, project.FindLayer("card_c")!, null, 2.5).Single(f => f.Label == "Grade");
        Assert.Equal((FieldKind.Info, "exposure 0.5"), (grade.Kind, grade.Value));

        // Closed from the Color view, the project leaves Home showing.
        Click(window, "ColorButton");
        TestWindow.Pump();
        Click(window, "LogoButton");
        TestWindow.Pump();
        Assert.Equal((Visibility.Visible, Visibility.Collapsed), (Shown(window, "StartPage"), Shown(window, "ColorPage")));
    });

    [Fact]
    public void TheTitlesViewAddsTitlesAndTemplatesAsTheirToolsDo() => TestWindow.Run("window-titles", (window, folder) =>
    {
        var library = new Library(Path.Combine(folder, "library"));
        var kitDirectory = Saved(folder, "kit");
        var kit = ProjectStore.Load(kitDirectory);
        ProjectEditor.AddTextLayer(kit, new TextLayerOptions { Id = "gold", Text = "Gold", FontSize = 20, Color = "#FFD700", StrokeColor = "#5A3A22", Bold = true });
        ProjectEditor.AddImageLayer(kit, new ImageLayerOptions { Drawings = new Dictionary<string, string> { ["normal"] = "logo" }, Id = "bar", Start = 0.5, End = 1.5, ZIndex = 2 });
        ProjectEditor.AddTextLayer(kit, new TextLayerOptions { Id = "name", Text = "Name", ParentId = "bar", Start = 0.5, End = 1.5, ZIndex = 3 });
        TextStyles.Save(kit, kitDirectory, library, "gold", "Gold", CaptionPosition.Bottom, overwrite: false);
        Templates.Save(kit, kitDirectory, library, "lower third", ["bar"], null, overwrite: false);

        var directory = Saved(folder, "show");
        window.OpenProject(directory);
        TestWindow.Pump();
        var timeline = (TimelineView)window.FindName("Timeline");
        timeline.Press(new Point(TimelineView.HeaderWidth + 0.5 * timeline.PixelsPerSecond - timeline.HorizontalOffset, TimelineView.RulerHeight / 2));
        timeline.Release();
        TestWindow.Pump();

        Click(window, "TitlesButton");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "TitlesPage"));
        Assert.Equal("Titles", ((System.Windows.Documents.Run)window.FindName("CrumbName")).Text);
        var styles = (ListBox)window.FindName("StyleList");
        var templates = (ListBox)window.FindName("TemplateList");
        TitleCard Card(ListBox list) => Assert.Single(list.Items.Cast<TitleCard>());
        for (var i = 0; i < 500 && (Card(styles).Picture is null || Card(templates).Picture is null); i++)
        {
            TestWindow.Pump(20);
        }

        Assert.Equal(("Gold", "Segoe UI bold, 20 px"), (Card(styles).Name, Card(styles).Detail));
        Assert.Equal("lower third", Card(templates).Name);
        Assert.NotNull(Card(styles).Picture);
        Assert.NotNull(Card(templates).Picture);
        ((RadioButton)window.FindName("TitlesTemplates")).IsChecked = true;
        Assert.Equal((Visibility.Collapsed, Visibility.Visible), (Shown(window, "StyleSection"), Shown(window, "TemplateSection")));
        ((RadioButton)window.FindName("TitlesAll")).IsChecked = true;

        // A new title in the style Gold, with its colors, popping in and fading out.
        var (line1, line2, fill) = ((TextBox)window.FindName("TitleLine1"), (TextBox)window.FindName("TitleLine2"), (TextBox)window.FindName("TitleFill"));
        Assert.Equal(("New title", "Title"), (Text(window, "TitleHeading"), line1.Text));
        styles.SelectedItem = Card(styles);
        TestWindow.Pump();
        Assert.Equal(("#FFD700", "#5A3A22", "Pop", "Fade"), (fill.Text, ((TextBox)window.FindName("TitleOutline")).Text,
            Text(window, "TitleInText"), Text(window, "TitleOutText")));
        line1.Text = "Tora Jumps!";
        line2.Text = "Episode 1";
        var expected = ProjectStore.Load(directory);
        var style = library.Load<TextStyle>(LibraryKind.Style, "Gold");
        Click(window, "TitleAddButton");
        WaitForSaves(window);
        using var renderer = new ZeyoCut.Render.FrameRenderer(directory);
        Func<Project, ITextMeasurer> measurer = p => new ZeyoCut.Render.Text.TextMeasurer(renderer, p);
        var options = new TextLayerOptions { Text = "Tora Jumps!\nEpisode 1", Start = 0.5, End = 2, Color = "#FFD700", StrokeColor = "#5A3A22", Align = "center" };
        new AddTitleOperation(options, style, directory, library, TitleAnimation.Pop, TitleAnimation.Fade, measurer).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        var title = expected.Layers.OfType<TextLayer>().Single().Id;
        TestWindow.Pump();
        Assert.Equal($"Selected text · {title}", Text(window, "TitleHeading"));

        // Its second line and its way out changed, as update_layer and animateOut change them.
        line2.Text = "Episode 2";
        TestWindow.Press(line2, Key.Enter);
        WaitForSaves(window);
        Click(window, "TitleOutButton");
        ChooseInMenu("Slide up");
        WaitForSaves(window);
        new UpdateLayerOperation(title, new JsonObject { ["text"] = "Tora Jumps!\nEpisode 2" }).Apply(expected);
        new TitleAnimationOperation(title, TitleSide.Out, TitleAnimation.SlideUp).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
        TestWindow.Pump();
        Assert.Equal("Slide up", Text(window, "TitleOutText"));

        // Red, then the style applied again as apply_style applies it.
        fill.Text = "#FF0000";
        TestWindow.Press(fill, Key.Enter);
        WaitForSaves(window);
        new UpdateLayerOperation(title, new JsonObject { ["color"] = "#FF0000" }).Apply(expected);
        Assert.Equal("Apply 'Gold'", ((Button)window.FindName("TitleApplyButton")).Content);
        Click(window, "TitleApplyButton");
        WaitForSaves(window);
        new ApplyStyleOperation(style, title, directory, library, measurer).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));

        // Green saved over the style; that isn't an edit to the project.
        fill.Text = "#00FF00";
        TestWindow.Press(fill, Key.Enter);
        WaitForSaves(window);
        var revision = ProjectStore.Load(directory).Revision;
        Click(window, "TitleUpdateButton");
        WaitForSaves(window);
        Assert.Equal(("#00FF00", CaptionPosition.Bottom), (library.Load<TextStyle>(LibraryKind.Style, "Gold").Color, library.Load<TextStyle>(LibraryKind.Style, "Gold").CaptionPosition));
        Assert.Equal(revision, ProjectStore.Load(directory).Revision);

        // The template's layers from the playhead, as add_template adds them.
        expected = ProjectStore.Load(directory);
        templates.SelectedItem = Card(templates);
        TestWindow.Pump();
        Assert.Equal(("lower third", Visibility.Collapsed), (Text(window, "TitleHeading"), Shown(window, "TitleFields")));
        Click(window, "TitleAddButton");
        WaitForSaves(window);
        new AddTemplateOperation("lower third", 0.5, directory, library).Apply(expected);
        Assert.Equal(WithoutRevision(ZeyoCut.Core.Serialization.ProjectJson.Serialize(expected)), ProjectJson(directory));
    });

    private static void Flip(MainWindow window, string name)
    {
        var box = (System.Windows.Controls.Primitives.ToggleButton)window.FindName(name);
        box.IsChecked = box.IsChecked != true;
        box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    // Preferences, from Home's settings button, save to the editor's settings at once and apply to
    // every project: a picture added lasts the still length, the Export view starts with the export
    // defaults, and the timeline doesn't snap.
    [Fact]
    public void ThePreferencesSaveToTheEditorsSettingsAndApply() => TestWindow.Run("window-preferences", (window, folder) =>
    {
        Click(window, "PrefsButton");
        TestWindow.Pump();
        Assert.Equal(Visibility.Visible, Shown(window, "PrefsPage"));
        Assert.Equal(("General", "Changes save instantly and apply to every project."), (Text(window, "PrefsTitle"), Text(window, "PrefsSubtitle")));
        Assert.Equal(Path.Combine(folder, "projects"), Text(window, "PrefsFolder"));
        Assert.Equal([true, true], new[] { "PrefsSnap", "PrefsShowChanges" }.Select(n => ((CheckBox)window.FindName(n)).IsChecked == true));
        Flip(window, "PrefsSnap");
        Flip(window, "PrefsShowChanges");
        var still = (TextBox)window.FindName("PrefsStill");
        Assert.Equal("5.0", still.Text);
        still.Text = "2,5";
        TestWindow.Press(still, Key.Enter);
        Assert.Equal("2.5", still.Text);
        still.Text = "0";
        TestWindow.Press(still, Key.Enter);
        Assert.Equal(("2.5", "Type seconds from 0.1 to 3600; it stays 2.5 s"), (still.Text, Text(window, "PrefsStillNote")));
        Assert.True(((RadioButton)window.FindName("PrefsHalf")).IsChecked);
        Flip(window, "PrefsQuarter");

        Flip(window, "PrefsExport");
        Assert.Equal(("Export defaults", Visibility.Visible, Visibility.Collapsed), (Text(window, "PrefsTitle"), Shown(window, "PrefsExportSection"), Shown(window, "PrefsGeneralSection")));
        TestWindow.Pump();
        var presets = Descendants<RadioButton>((ItemsControl)window.FindName("PrefsPresets")).ToList();
        Assert.Equal(["Project settings", "YouTube 1080p", "Shorts 1080p"], presets.Select(r => r.Content));
        Assert.Equal([true, false, false], presets.Select(r => r.IsChecked == true));
        presets[1].IsChecked = true;
        presets[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var small = Descendants<RadioButton>((ItemsControl)window.FindName("PrefsQualities")).Single(r => (string)r.Content == "Small");
        small.IsChecked = true;
        small.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Flip(window, "PrefsAbout");
        Assert.StartsWith("ZeyoCut Editor 0.", Text(window, "AboutVersion"));

        var saved = EditorSettings.Load(Path.Combine(folder, "editor.json"));
        Assert.Equal((false, false, 2.5, 0.25), (saved.Snapping, saved.ShowChanges, saved.StillLength, saved.PlaybackScale));
        Assert.Equal(("youtube_1080p", "Small"), (saved.ExportPreset, saved.ExportQuality));
        TestWindow.Press(window, Key.Escape);
        Assert.Equal(Visibility.Collapsed, Shown(window, "PrefsPage"));

        var project = TestFiles.Project();
        project.Assets.Add(new Asset { Id = "logo", Type = AssetType.Image, File = "assets/logo.png", Width = 8, Height = 8 });
        var directory = Path.Combine(folder, "pictures");
        ProjectSession.Create(directory, project);
        TestFiles.Png(Path.Combine(directory, ProjectStore.AssetsFolder, "logo.png"), 8, 8);
        window.OpenProject(directory);
        Assert.False(((TimelineView)window.FindName("Timeline")).Snapping);
        Click(window, "MediaBrowserButton");
        TestWindow.Pump();
        SelectCards(window, "logo");
        Click(window, "BrowserAddButton");
        WaitForSaves(window);
        var logo = ProjectStore.Load(directory).Layers.Single();
        Assert.Equal((0.0, 2.5), (logo.Start, logo.End));

        Click(window, "BackToEdit");
        Click(window, "ExportViewButton");
        TestWindow.Pump();
        Assert.Equal("Small", Text(window, "QualityText"));
        Assert.Equal("YouTube 1080p", ((ItemsControl)window.FindName("ExportPresets")).Items.Cast<PageChoice>().Single(c => c.Selected).Label);
        Assert.Equal("1920×1080", Text(window, "ExportResolution"));
    });

    // A clip another program changes glows on the timeline for a moment, unless Show Claude's
    // changes is off.
    [Fact]
    public void AnotherProgramsChangeMakesItsClipGlow() => TestWindow.Run("window-change-glow", (window, folder) =>
    {
        var (directory, timeline, _) = ShortTitle(window, folder);
        void Change(double end)
        {
            ((TextBlock)window.FindName("MessageText")).Text = "";
            ProjectSession.Open(directory).Mutate("update_layer title", p =>
            {
                p.FindLayer("title")!.End = end;
                return 0;
            });
            for (var i = 0; i < 300 && !Text(window, "MessageText").StartsWith("Another program saved", StringComparison.Ordinal); i++)
            {
                TestWindow.Pump(10);
            }

            Assert.StartsWith("Another program saved", Text(window, "MessageText"));
        }

        Assert.Empty(timeline.Highlighted);
        Change(1.5);
        Assert.Equal(["title"], timeline.Highlighted);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 500 && timeline.Highlighted.Count > 0; i++)
        {
            TestWindow.Pump(10);
        }

        Assert.Empty(timeline.Highlighted);
        Assert.InRange(watch.Elapsed.TotalSeconds, 2, 4);

        // Off, the next change doesn't glow.
        Click(window, "PrefsButton");
        TestWindow.Pump();
        Flip(window, "PrefsShowChanges");
        TestWindow.Press(window, Key.Escape);
        Change(1.25);
        Assert.Empty(timeline.Highlighted);
        Assert.Equal(1.25, ProjectStore.Load(directory).FindLayer("title")!.End);
    });
}
