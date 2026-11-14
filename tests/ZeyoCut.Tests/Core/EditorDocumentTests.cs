// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// The editor and the server on one project (docs/manual-editor/PLAN.md, section 3, scenarios a–d),
// with the editor saving every edit at once.
public class EditorDocumentTests
{
    private static (ProjectSession Server, EditorDocument Editor, string Directory) Open(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var server = ProjectSession.Create(directory, TestProjects.Footage());
        return (server, EditorDocument.Open(directory), directory);
    }

    private static T Saved<T>(string directory, string id) where T : Layer => Assert.IsType<T>(ProjectStore.Load(directory).FindLayer(id));

    [Fact]
    public void AnEditIsSavedAtOnceUnderTheEditorsName()
    {
        var (_, editor, directory) = Open("editor-saves");
        Assert.Equal("Saved · revision 1", editor.Status);
        Assert.False(editor.CanUndo);

        Assert.True(editor.Apply(new TrimLayerOperation("clip_2", null, 7)));
        Assert.Equal("Saved · revision 2", editor.Status);
        Assert.Equal(7, Saved<VideoLayer>(directory, "clip_2").TrimOut);
        Assert.Equal(7, Assert.IsType<VideoLayer>(editor.View.FindLayer("clip_2")).TrimOut);
        Assert.Equal("editor trim_layer clip_2", ProjectBackups.List(directory)[0].Before);

        // One that can't be made changes nothing and says why.
        Assert.False(editor.Apply(new TrimLayerOperation("clip_2", null, 11)));
        Assert.Equal("trim_layer clip_2 wasn't saved: 'clip_2' can end at 10s at the latest: its source has nothing after that", editor.Status);
        Assert.Equal(2, ProjectStore.Load(directory).Revision);

        Assert.True(editor.Undo());
        Assert.Equal("Saved · revision 3", editor.Status);
        Assert.Equal(8, Saved<VideoLayer>(directory, "clip_2").TrimOut);
        Assert.False(editor.Undo());
        Assert.Equal("nothing to undo", editor.Status);
        Assert.True(editor.Redo());
        Assert.Equal(7, Saved<VideoLayer>(directory, "clip_2").TrimOut);
        Assert.Equal(["editor redo trim_layer clip_2", "editor undo trim_layer clip_2", "editor trim_layer clip_2"], ProjectBackups.List(directory).Select(b => b.Before));
    }

    // (a) Claude edits while the editor has nothing in hand.
    [Fact]
    public void TheEditorTakesWhatTheServerSaved()
    {
        var (server, editor, directory) = Open("editor-scenario-a");
        Assert.True(editor.Apply(new TrimLayerOperation("clip_2", null, 7)));
        Assert.False(editor.Refresh());

        server.Mutate("add_caption_track en", TestProjects.AddCaptions);
        Assert.True(editor.Refresh());
        Assert.Equal("Saved · revision 3", editor.Status);
        Assert.NotNull(editor.View.FindLayer("captions_en"));
        var change = Assert.IsType<OutsideChange>(editor.TakeOutsideChanges());
        Assert.Equal(["add_caption_track en"], change.Edits);
        Assert.Null(editor.TakeOutsideChanges());
        Assert.Equal([("trim_layer clip_2", 2L, false), ("add_caption_track en", 3L, true)], editor.History.Select(h => (h.Label, h.Revision, h.Outside is not null)));

        // Undo takes back the trim and keeps the captions.
        Assert.True(editor.Undo());
        Assert.Equal(8, Saved<VideoLayer>(directory, "clip_2").TrimOut);
        Assert.NotNull(ProjectStore.Load(directory).FindLayer("captions_en"));
        Assert.NotNull(editor.View.FindLayer("captions_en"));
    }

    // (b) Claude edits and the editor makes an edit before it has shown Claude's.
    [Fact]
    public void AnEditGoesOnTopOfAnotherProgramsUnlessItChangedTheSameLayer()
    {
        var (server, editor, directory) = Open("editor-scenario-b");
        server.Mutate("update_layer music", p => ProjectEditor.UpdateLayer(p, "music", new JsonObject { ["volume"] = 0.3 }));
        Assert.True(editor.Apply(new MoveLayerOperation("title", 1)));
        Assert.Equal("Saved · revision 3", editor.Status);
        var saved = ProjectStore.Load(directory);
        Assert.Equal((0.3, 1.0), (Assert.IsType<AudioLayer>(saved.FindLayer("music")).Volume, saved.FindLayer("title")!.Start));
        Assert.Equal(["update_layer music"], editor.TakeOutsideChanges()!.Edits);

        // Claude changed the clip the editor trims: the trim is dropped and the editor shows Claude's version.
        server.Mutate("update_layer clip_2", p => ProjectEditor.UpdateLayer(p, "clip_2", new JsonObject { ["volume"] = 0.5 }));
        Assert.False(editor.Apply(new TrimLayerOperation("clip_2", null, 7)));
        Assert.Equal("Your change wasn't saved: another program changed 'clip_2' first (update_layer clip_2). You see its version now.", editor.Status);
        Assert.Equal(4, ProjectStore.Load(directory).Revision);
        Assert.Equal(0.5, Assert.IsType<VideoLayer>(editor.View.FindLayer("clip_2")).Volume);

        // Trying again on that version works.
        Assert.True(editor.Apply(new TrimLayerOperation("clip_2", null, 7)));
        var both = Saved<VideoLayer>(directory, "clip_2");
        Assert.Equal((7.0, 0.5), (both.TrimOut, both.Volume));

        server.Mutate("remove_layer title", p => ProjectEditor.RemoveLayer(p, "title", withChildren: false));
        Assert.False(editor.Apply(new MoveLayerOperation("title", 2)));
        Assert.Equal("Your change wasn't saved: another program removed 'title' first (remove_layer title). You see its version now.", editor.Status);
        Assert.Null(editor.View.FindLayer("title"));
    }

    // (b) again, with the edit built before Claude's change and made after the editor showed it: a
    // field that still holds what was typed, or a drag that began before.
    [Fact]
    public void AnEditBuiltFromAnEarlierViewIsDroppedWhenAnotherProgramChangedItsLayerSince()
    {
        var (server, editor, directory) = Open("editor-built-from");
        var built = editor.View;
        server.Mutate("update_layer clip_2", p => ProjectEditor.UpdateLayer(p, "clip_2", new JsonObject { ["volume"] = 0.5 }));
        Assert.True(editor.Refresh());
        Assert.Equal(["update_layer clip_2"], editor.TakeOutsideChanges()!.Edits);

        Assert.False(editor.Apply(new TrimLayerOperation("clip_2", null, 7), built));
        Assert.Equal("Your change wasn't saved: another program changed 'clip_2' first (update_layer clip_2). You see its version now.", editor.Status);
        Assert.Equal("clip_2", editor.Overtaken);
        Assert.Equal(2, ProjectStore.Load(directory).Revision);

        // An edit to another layer built then is saved, and so is the trim built from what's shown now.
        Assert.True(editor.Apply(new MoveLayerOperation("title", 1), built));
        Assert.Null(editor.Overtaken);
        Assert.True(editor.Apply(new TrimLayerOperation("clip_2", null, 7), editor.View));
        Assert.Equal((7.0, 0.5), (Saved<VideoLayer>(directory, "clip_2").TrimOut, Saved<VideoLayer>(directory, "clip_2").Volume));

        // The editor's own saves since then aren't another program's.
        built = editor.View;
        Assert.True(editor.Apply(new UpdateLayerOperation("clip_2", new JsonObject { ["fadeIn"] = 1 })));
        Assert.True(editor.Apply(new TrimLayerOperation("clip_2", null, 6), built), editor.Status);

        // Claude's change comes in with an edit of the editor's to another layer. A copy of a project
        // in place of a View is compared with what's shown.
        built = editor.View;
        var copy = ZeyoCut.Core.Serialization.ProjectJson.Clone(built);
        server.Mutate("update_layer music", p => ProjectEditor.UpdateLayer(p, "music", new JsonObject { ["volume"] = 0.3 }));
        Assert.True(editor.Apply(new MoveLayerOperation("title", 2)));
        Assert.False(editor.Apply(new UpdateLayerOperation("music", new JsonObject { ["fadeIn"] = 1 }), built));
        Assert.Equal("Your change wasn't saved: another program changed 'music' first (update_layer music). You see its version now.", editor.Status);
        Assert.False(editor.Apply(new UpdateLayerOperation("music", new JsonObject { ["fadeIn"] = 1 }), copy));
        Assert.Equal("music", editor.Overtaken);

        // A layer another program removed.
        built = editor.View;
        server.Mutate("remove_layer title", p => ProjectEditor.RemoveLayer(p, "title", withChildren: false));
        editor.Refresh();
        Assert.False(editor.Apply(new MoveLayerOperation("title", 3), built));
        Assert.Equal("Your change wasn't saved: another program removed 'title' first (remove_layer title). You see its version now.", editor.Status);
        Assert.Equal((0.3, 0.0), (Saved<AudioLayer>(directory, "music").Volume, Saved<AudioLayer>(directory, "music").FadeIn));
    }

    // (c) Both save at nearly the same moment.
    [Fact]
    public async Task TheEditorAndTheServerSavingAtOnceLoseNothing()
    {
        const int each = 25;
        var (server, editor, directory) = Open("editor-scenario-c");
        var ct = TestContext.Current.CancellationToken;
        var serving = Task.Run(() =>
        {
            for (var i = 0; i < each; i++)
            {
                server.Mutate($"add_markers m{i}", p => MarkerEditor.AddMarkers(p, [new MarkerEntry($"m{i}", i * 0.1, $"M{i}", null)]));
            }
        }, ct);
        var editing = Task.Run(() => Enumerable.Range(1, each).Select(i => editor.Apply(new MoveLayerOperation("title", i * 0.1))).ToList(), ct);
        await serving;
        var applied = await editing;

        Assert.All(applied, Assert.True);
        var saved = ProjectStore.Load(directory);
        Assert.Equal(1 + 2 * each, saved.Revision);
        Assert.Equal(each, saved.Markers.Count);
        Assert.Equal(each * 0.1, saved.FindLayer("title")!.Start, 6);
        editor.Refresh();
        Assert.Equal(each, editor.View.Markers.Count);
    }

    // (d) The server holds an older version and the editor has saved since.
    [Fact]
    public void TheServersNextEditGoesOnTopOfTheEditorsAndItsUndoKeepsThem()
    {
        var (server, editor, directory) = Open("editor-scenario-d");
        server.Mutate("update_layer music", p => ProjectEditor.UpdateLayer(p, "music", new JsonObject { ["volume"] = 0.3 }));
        editor.Refresh();
        Assert.True(editor.Apply(new TrimLayerOperation("clip_2", null, 7)));

        server.Mutate("update_layer clip_1", p => ProjectEditor.UpdateLayer(p, "clip_1", new JsonObject { ["volume"] = 0.2 }));
        var saved = ProjectStore.Load(directory);
        Assert.Equal((7.0, 0.2, 0.3), (Assert.IsType<VideoLayer>(saved.FindLayer("clip_2")).TrimOut, Assert.IsType<VideoLayer>(saved.FindLayer("clip_1")).Volume, Assert.IsType<AudioLayer>(saved.FindLayer("music")).Volume));
        Assert.Equal(["editor trim_layer clip_2"], server.TakeOutsideChanges()!.Edits);

        Assert.Equal("update_layer clip_1", server.Undo());
        Assert.Equal("update_layer music", server.Undo());
        saved = ProjectStore.Load(directory);
        Assert.Equal((7.0, 1.0, 0.8), (Assert.IsType<VideoLayer>(saved.FindLayer("clip_2")).TrimOut, Assert.IsType<VideoLayer>(saved.FindLayer("clip_1")).Volume, Assert.IsType<AudioLayer>(saved.FindLayer("music")).Volume));

        // The server can't take back an edit to a layer the editor changed after it.
        server.Mutate("update_layer clip_2", p => ProjectEditor.UpdateLayer(p, "clip_2", new JsonObject { ["fadeIn"] = 1 }));
        editor.Refresh();
        Assert.True(editor.Apply(new TrimLayerOperation("clip_2", 5, null)));
        var error = Assert.Throws<ZeyoCutException>(() => server.Undo());
        Assert.StartsWith("can't undo 'update_layer clip_2': another program changed layer 'clip_2' since that edit (saved: editor trim_layer clip_2)", error.Message);

        // The editor can take back its trim: nothing of clip_2 changed after it.
        Assert.True(editor.Undo());
        Assert.Equal((4.0, 1.0), (Saved<VideoLayer>(directory, "clip_2").Start, Saved<VideoLayer>(directory, "clip_2").FadeIn));
    }

    [Fact]
    public void TheUndoAfterARefusedOneStepsOverThatStepAndSaysSo()
    {
        var (server, editor, directory) = Open("editor-undo-step-over");
        Assert.True(editor.Apply(new MoveLayerOperation("title", 1)));
        Assert.True(editor.Apply(new UpdateLayerOperation("music", new JsonObject { ["volume"] = 0.3 })));
        server.Mutate("update_layer music", p => ProjectEditor.UpdateLayer(p, "music", new JsonObject { ["fadeIn"] = 1 }));

        // The editor has no restore_backup, so its message doesn't name it.
        Assert.False(editor.Undo());
        Assert.Equal(
            "can't undo 'update_layer music': another program changed layer 'music' since that edit (saved: update_layer music), and undoing it would throw that change away. "
            + "The next undo steps over this step and goes on to the one before it",
            editor.Status);

        Assert.True(editor.Undo());
        Assert.Equal("Undid 'move_layer title' and stepped over 'update_layer music', which can't be undone on top of another program's changes. Saved · revision 5", editor.Status);
        Assert.Equal(0, editor.View.FindLayer("title")!.Start);
        var music = Saved<AudioLayer>(directory, "music");
        Assert.Equal((0.3, 1.0), (music.Volume, music.FadeIn));

        Assert.False(editor.Undo());
        Assert.Equal("nothing to undo: 'update_layer music' can't be undone on top of another program's changes", editor.Status);
        Assert.True(editor.Redo());
        Assert.Equal("Saved · revision 6", editor.Status);
    }

    [Fact]
    public void AnImportIsSavedAsAnEdit()
    {
        var (_, editor, directory) = Open("editor-import");
        var source = Path.Combine(TestPaths.NewDirectory("editor-import-src"), "logo.png");
        File.WriteAllBytes(source, [1, 2, 3]);
        Assert.True(editor.Apply(new AddAssetOperation(AssetImporter.Prepare(directory, source, new FakeAssetProbe()))));
        var logo = Assert.IsType<Asset>(ProjectStore.Load(directory).FindAsset("logo"));
        Assert.Equal(("assets/logo.png", 200), (logo.File, logo.Width));
        Assert.NotNull(editor.View.FindAsset("logo"));
        Assert.Equal("editor import_asset logo.png", ProjectBackups.List(directory)[0].Before);

        // A normalized video names its original and why.
        var output = AssetImporter.NewAssetPath(directory, @"C:\clips\phone.mov", ".mp4");
        File.WriteAllBytes(output, [7]);
        var normalized = new PreparedAsset(output, output, AssetType.Video, new AssetInfo(Width: 1920, Height: 1080, Duration: 2, FrameRate: "30", FrameCount: 60, HasAudio: true, Codec: "h264"));
        Assert.True(editor.Apply(new AddAssetOperation(normalized, null, @"C:\clips\phone.mov", "variable frame rate")), editor.Status);
        var phone = Assert.IsType<Asset>(ProjectStore.Load(directory).FindAsset("phone"));
        Assert.Equal(("assets/phone.mp4", @"C:\clips\phone.mov", "variable frame rate"), (phone.File, phone.OriginalFile, phone.Normalized));
        Assert.Equal("editor import_video phone.mov", ProjectBackups.List(directory)[0].Before);

        // An id that's taken is refused, and undo takes an import back.
        Assert.False(editor.Apply(new AddAssetOperation(normalized, "logo")));
        Assert.StartsWith("import_video phone.mp4 wasn't saved: asset id 'logo' already exists", editor.Status);
        Assert.True(editor.Undo());
        Assert.Null(ProjectStore.Load(directory).FindAsset("phone"));
    }

    [Fact]
    public void AnUnreadableProjectFileIsReportedAndNothingIsSaved()
    {
        var (_, editor, directory) = Open("editor-unreadable");
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        File.WriteAllText(file, "{ not json");
        Assert.False(editor.Refresh());
        Assert.StartsWith("project.json can't be read (", editor.Status);
        Assert.EndsWith("); edits aren't saved until it's fixed or restore_backup puts a backup back", editor.Status);
        Assert.False(editor.Apply(new MoveLayerOperation("title", 1)));
        Assert.StartsWith("move_layer title wasn't saved: project.json was changed by another program and can't be read", editor.Status);
        Assert.Equal("{ not json", File.ReadAllText(file));
    }
}
