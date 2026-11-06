// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

public class EditingTests
{
    private static KeyframeEntry Key(string layer, string property, double time, object value, string? easing = null) =>
        new(layer, property, time, JsonSerializer.SerializeToElement(value), easing);

    [Fact]
    public void ImageLayerDefaults()
    {
        var project = TestProjects.Rig();
        var body = (ImageLayer)project.FindLayer("tora_body")!;
        Assert.Equal(100, body.AnchorX);
        Assert.Equal(50, body.AnchorY);
        Assert.Equal(320, body.X.Value);
        Assert.Equal(180, body.Y.Value);
        Assert.Equal("normal", body.Drawing.Value);

        var head = (ImageLayer)project.FindLayer("tora_head")!;
        Assert.Equal(body.AnchorX, head.X.Value);
        Assert.Equal(body.AnchorY, head.Y.Value);

        var auto = ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Name = "Tora Eye",
            Drawings = new Dictionary<string, string> { ["open"] = "head" },
        });
        Assert.Equal("tora_eye", auto.Id);
        Assert.Equal(12, auto.ZIndex);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void MissingLayerErrorListsExistingLayers()
    {
        var project = TestProjects.Rig();
        var ex = Assert.Throws<ZeyoCutException>(() => ProjectEditor.GetLayer(project, "tora_hed"));
        Assert.Equal("layer 'tora_hed' not found; existing layers: tora_body, tora_head, tora_tail", ex.Message);
    }

    [Fact]
    public void MissingAssetErrorListsAssets()
    {
        var project = TestProjects.Rig();
        var ex = Assert.Throws<ZeyoCutException>(() => ProjectEditor.AddImageLayer(project, new ImageLayerOptions
        {
            Drawings = new Dictionary<string, string> { ["normal"] = "wing" },
        }));
        Assert.Contains("asset 'wing' not found; image assets: body, head, head_shocked, tail", ex.Message);
    }

    [Fact]
    public void SetKeyframesUpsertsByTime()
    {
        var project = TestProjects.Rig();
        var first = KeyframeEditor.SetKeyframes(project,
        [
            Key("tora_head", "rotation", 0, 0),
            Key("tora_head", "rotation", 1, 15, "easeOutBack"),
            Key("tora_head", "drawing", 1.5, "shocked"),
        ]);
        Assert.Equal(3, first.Added);

        var second = KeyframeEditor.SetKeyframes(project, [Key("tora_head", "Rotation", 1.0000001, -5, "ease-in-out")]);
        Assert.Equal(1, second.Replaced);

        var head = (ImageLayer)project.FindLayer("tora_head")!;
        Assert.Equal(2, head.Rotation.Keyframes.Count);
        Assert.Equal(-5, head.Rotation.Keyframes[1].Value);
        Assert.Equal(Easing.EaseInOut, head.Rotation.Keyframes[1].Easing);
        Assert.Equal("shocked", head.Drawing.Keyframes.Single().Value);
    }

    [Fact]
    public void SetKeyframesIsAllOrNothing()
    {
        var session = ProjectSession.Create(TestPaths.NewDirectory("editing-atomic"), TestProjects.Rig());
        var ex = Assert.Throws<ZeyoCutException>(() => session.Mutate("set_keyframes", p => KeyframeEditor.SetKeyframes(p,
        [
            Key("tora_head", "rotation", 0, 10),
            Key("tora_head", "wobble", 0, 1),
            Key("tora_head", "drawing", 1, "angry"),
            Key("tora_head", "x", 0, 5, "superBounce"),
        ])));

        Assert.Contains("3 of 4 entries are invalid", ex.Message);
        Assert.Contains("unknown property 'wobble'", ex.Message);
        Assert.Contains("drawing 'angry' not found on layer 'tora_head'; drawings: normal, shocked", ex.Message);
        Assert.Contains("unknown easing 'superBounce'", ex.Message);
        Assert.Empty(session.Read(p => ((ImageLayer)p.FindLayer("tora_head")!).Rotation.Keyframes));
        Assert.Equal(0, session.UndoCount);
    }

    [Fact]
    public void RemoveAndClearKeyframes()
    {
        var project = TestProjects.Rig();
        KeyframeEditor.SetKeyframes(project,
        [
            Key("tora_body", "y", 0, 0),
            Key("tora_body", "y", 1, 10),
            Key("tora_body", "scaleY", 1, 0.8),
        ]);

        Assert.Equal(1, KeyframeEditor.RemoveKeyframes(project, [new KeyframeRef("tora_body", "y", 1)]));
        var ex = Assert.Throws<ZeyoCutException>(() => KeyframeEditor.RemoveKeyframes(project, [new KeyframeRef("tora_body", "y", 2)]));
        Assert.Contains("no keyframe at 2s on tora_body.y; keyframe times: 0", ex.Message);

        Assert.Equal(2, KeyframeEditor.ClearKeyframes(project, "tora_body", null));
        Assert.Empty(((ImageLayer)project.FindLayer("tora_body")!).ScaleY.Keyframes);
    }

    [Fact]
    public void UpdateLayerSetsStaticFieldsAndParent()
    {
        var project = TestProjects.Rig();
        ProjectEditor.AddImageLayer(project, new ImageLayerOptions { Id = "rig_root" });

        var warnings = ProjectEditor.UpdateLayer(project, "tora_body", new JsonObject
        {
            ["x"] = 400,
            ["anchorY"] = 100,
            ["parentId"] = "rig_root",
            ["end"] = 2.5,
            ["drawings"] = new JsonObject { ["angry"] = "head" },
        });
        Assert.Empty(warnings);

        var body = (ImageLayer)project.FindLayer("tora_body")!;
        Assert.Equal(400, body.X.Value);
        Assert.Equal(100, body.AnchorY);
        Assert.Equal("rig_root", body.ParentId);
        Assert.Equal(2.5, body.End);
        Assert.Equal(["normal", "angry"], body.Drawings.Keys);

        ProjectEditor.UpdateLayer(project, "tora_body", new JsonObject { ["parentId"] = "" });
        Assert.Null(((ImageLayer)project.FindLayer("tora_body")!).ParentId);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void UpdateLayerWarnsWhenKeyframesOverrideStaticValue()
    {
        var project = TestProjects.Rig();
        KeyframeEditor.SetKeyframes(project, [Key("tora_body", "x", 0, 0)]);
        var warnings = ProjectEditor.UpdateLayer(project, "tora_body", new JsonObject { ["x"] = 10 });
        Assert.Single(warnings);
    }

    [Fact]
    public void UpdateLayerRejectsUnknownFields()
    {
        var project = TestProjects.Rig();
        var ex = Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateLayer(project, "tora_body", new JsonObject { ["colour"] = "#FF0000" }));
        Assert.StartsWith("unknown field 'colour' for image layer 'tora_body'; fields: name, rigRole, start, end", ex.Message);
    }

    [Fact]
    public void DuplicateRigRemapsParents()
    {
        var project = TestProjects.Rig();
        var map = ProjectEditor.DuplicateLayer(project, new DuplicateOptions
        {
            LayerId = "tora_body",
            ReplaceFrom = "tora",
            ReplaceTo = "mimi",
            ZIndexOffset = 100,
        });

        Assert.Equal("mimi_body", map["tora_body"]);
        Assert.Equal("mimi_body", project.FindLayer("mimi_head")!.ParentId);
        Assert.Equal("mimi_body", project.FindLayer("mimi_tail")!.ParentId);
        Assert.Equal(111, project.FindLayer("mimi_head")!.ZIndex);
        Assert.Null(project.FindLayer("mimi_body")!.ParentId);

        var copy = ProjectEditor.DuplicateLayer(project, new DuplicateOptions { LayerId = "tora_head", NewParentId = "mimi_body" });
        Assert.Equal("tora_head_2", copy["tora_head"]);
        Assert.Equal("mimi_body", project.FindLayer("tora_head_2")!.ParentId);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void RemoveLayerWithChildrenNeedsFlag()
    {
        var project = TestProjects.Rig();
        var ex = Assert.Throws<ZeyoCutException>(() => ProjectEditor.RemoveLayer(project, "tora_body", withChildren: false));
        Assert.Contains("has child layers: tora_head, tora_tail", ex.Message);

        var removed = ProjectEditor.RemoveLayer(project, "tora_body", withChildren: true);
        Assert.Equal(3, removed.Count);
        Assert.Empty(project.Layers);
    }

    [Fact]
    public void SessionUndoRedoRestoresAndSaves()
    {
        var directory = TestPaths.NewDirectory("editing-undo");
        var session = ProjectSession.Create(directory, TestProjects.Rig());

        session.Mutate("move body", p => ProjectEditor.UpdateLayer(p, "tora_body", new JsonObject { ["x"] = 111 }));
        session.Mutate("remove tail", p => ProjectEditor.RemoveLayer(p, "tora_tail", false));
        Assert.Equal(2, session.UndoCount);
        Assert.Null(ProjectStore.Load(directory).FindLayer("tora_tail"));

        Assert.Equal("remove tail", session.Undo());
        Assert.NotNull(ProjectStore.Load(directory).FindLayer("tora_tail"));
        Assert.Equal("move body", session.Undo());
        Assert.Equal(320, ((ImageLayer)ProjectStore.Load(directory).FindLayer("tora_body")!).X.Value);
        Assert.Throws<ZeyoCutException>(() => session.Undo());

        Assert.Equal("move body", session.Redo());
        Assert.Equal(111, ((ImageLayer)ProjectStore.Load(directory).FindLayer("tora_body")!).X.Value);

        session.Mutate("new edit", p => ProjectEditor.UpdateLayer(p, "tora_body", new JsonObject { ["y"] = 5 }));
        Assert.Equal(0, session.RedoCount);
    }

    [Fact]
    public void SaveWaitsForABrieflyLockedProjectFile()
    {
        var directory = TestPaths.NewDirectory("editing-save-locked");
        var project = TestProjects.Empty();
        ProjectStore.Save(project, directory);
        project.Duration = 7;

        var path = Path.Combine(directory, ProjectStore.ProjectFileName);
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // A thread of its own: when the other tests keep the thread pool busy, a pooled task can
        // start too late to release the file within the save's retries.
        var release = new Thread(() =>
        {
            Thread.Sleep(150);
            locked.Dispose();
        });
        release.Start();
        ProjectStore.Save(project, directory);
        release.Join();

        Assert.Equal(7, ProjectStore.Load(directory).Duration);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ImportCopiesIntoAssetsFolder()
    {
        var directory = TestPaths.NewDirectory("editing-import");
        var source = Path.Combine(TestPaths.NewDirectory("editing-import-src"), "Tora Body.png");
        File.WriteAllBytes(source, [1, 2, 3]);
        var session = ProjectSession.Create(directory, TestProjects.Empty());

        var asset = session.Mutate("import", p => AssetImporter.Import(p, directory, source, null, new FakeAssetProbe()));
        Assert.Equal("tora_body", asset.Id);
        Assert.Equal("assets/Tora_Body.png", asset.File);
        Assert.Equal(200, asset.Width);
        Assert.True(File.Exists(Path.Combine(directory, "assets", "Tora_Body.png")));

        var again = session.Mutate("import", p => AssetImporter.Import(p, directory, source, null, new FakeAssetProbe()));
        Assert.Equal("tora_body_2", again.Id);
        Assert.Equal("assets/Tora_Body.png", again.File);

        var missing = Assert.Throws<ZeyoCutException>(() => session.Mutate("import", p => AssetImporter.Import(p, directory, "C:\\nope\\x.png", null, new FakeAssetProbe())));
        Assert.StartsWith("file not found", missing.Message);
    }

    [Fact]
    public void ValidatorRejectsOddSizesAndBadColors()
    {
        var project = TestProjects.Empty(width: 641);
        project.BackgroundColor = "white";
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.StartsWith("width must be an even number"));
        Assert.Contains(errors, e => e.StartsWith("backgroundColor 'white'"));
    }

    [Fact]
    public void UpdateProjectChangesSettingsAndWarns()
    {
        var project = TestProjects.Rig();
        project.Assets.Add(new Asset { Id = "voice", Type = AssetType.Audio, File = "assets/voice.wav", Duration = 4 });
        ProjectEditor.AddAudioLayer(project, new AudioLayerOptions { Asset = "voice", Start = 0.5 });
        KeyframeEditor.SetKeyframes(project, [Key("tora_body", "x", 0, 0), Key("tora_body", "x", 2.5, 100)]);
        ProjectEditor.UpdateLayer(project, "tora_tail", new JsonObject { ["end"] = 3 });

        var longer = ProjectEditor.UpdateProject(project, new ProjectOptions { Duration = 6, Fps = 24, BackgroundColor = "#101010" });
        Assert.Equal(["fps 30 → 24", "duration 3s → 6s", "backgroundColor #FFFFFF → #101010"], longer.Changes);
        Assert.Equal(6, project.Duration);
        Assert.Equal(24, project.Fps);
        var stuck = Assert.Single(longer.Warnings);
        Assert.StartsWith("these layers end at the old project end (3s) and won't run to the new end: tora_tail", stuck);
        Assert.Empty(ProjectValidator.Validate(project));

        var shorter = ProjectEditor.UpdateProject(project, new ProjectOptions { Duration = 2 });
        Assert.Contains("keyframes after the new end (2s) won't be reached: tora_body.x (1)", shorter.Warnings);
        Assert.Contains("audio 'voice' runs until 4.5s and will be cut at 2s", shorter.Warnings);

        var resized = ProjectEditor.UpdateProject(project, new ProjectOptions { Width = 1280, Height = 720, Name = "Renamed" });
        Assert.Equal(["name 'test' → 'Renamed'", "width 640 → 1280", "height 360 → 720"], resized.Changes);
        Assert.Contains(resized.Warnings, w => w.StartsWith("the canvas changed from 640x360 to 1280x720"));
    }

    [Fact]
    public void UpdateProjectRejectsNoOpsAndInvalidSettings()
    {
        var project = TestProjects.Empty();
        var empty = Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateProject(project, new ProjectOptions()));
        Assert.StartsWith("nothing to change", empty.Message);
        var same = Assert.Throws<ZeyoCutException>(() => ProjectEditor.UpdateProject(project, new ProjectOptions { Fps = 30, Duration = 3 }));
        Assert.StartsWith("nothing changed; the project already is 640x360 @ 30 fps, 3s", same.Message);

        var directory = TestPaths.NewDirectory("editing-update-project");
        var session = ProjectSession.Create(directory, TestProjects.Empty());
        var odd = Assert.Throws<ZeyoCutException>(() => session.Mutate("update_project", p => ProjectEditor.UpdateProject(p, new ProjectOptions { Width = 641, Duration = 9 })));
        Assert.StartsWith("width must be an even number", odd.Message);
        Assert.Equal(640, ProjectStore.Load(directory).Width);
        Assert.Equal(3, ProjectStore.Load(directory).Duration);
        Assert.Equal(0, session.UndoCount);
    }

    [Fact]
    public void LayerTreeShowsHierarchy()
    {
        var tree = LayerTree.Format(TestProjects.Rig());
        Assert.Contains("tora_body [image]", tree);
        Assert.Contains("├─ tora_head [image]", tree);
        Assert.Contains("└─ tora_tail [image]", tree);
    }
}
