// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Tests.TestSupport;

namespace ZeyoCut.Tests.Core;

// Undo and redo when another program saves the same project: only the step's own items go back.
public class SelectiveUndoTests
{
    private const string Backups = "restore_backup lists the copies of project.json kept before each save";

    private static (ProjectSession Server, ProjectSession Editor, string Directory) TwoSessions(string name)
    {
        var directory = TestPaths.NewDirectory(name);
        var server = ProjectSession.Create(directory, TestProjects.Footage());
        return (server, ProjectSession.Open(directory, ProjectBackups.EditorProgram), directory);
    }

    private static T Saved<T>(string directory, string id) where T : Layer => Assert.IsType<T>(ProjectStore.Load(directory).FindLayer(id));

    [Fact]
    public void WithNoOutsideChangeUndoAndRedoPutBackExactlyWhatWasSaved()
    {
        var directory = TestPaths.NewDirectory("undo-exact");
        var session = ProjectSession.Create(directory, TestProjects.Footage());
        var file = Path.Combine(directory, ProjectStore.ProjectFileName);
        var versions = new List<string> { TestProjects.WithoutRevision(File.ReadAllText(file)) };
        EditOperation[] operations =
        [
            new TrimLayerOperation("clip_2", 5, 9),
            new SplitLayerOperation("clip_1", 2),
            new MoveLayerOperation("title", 6),
            new UpdateLayerOperation("music", new JsonObject { ["volume"] = 0.2, ["visible"] = false }),
            new RemoveLayerOperation("clip_2", WithChildren: true),
            new ReorderLayerOperation("title", 1),
        ];
        foreach (var operation in operations)
        {
            session.Mutate(operation.Label, operation.Apply);
            versions.Add(TestProjects.WithoutRevision(File.ReadAllText(file)));
        }

        for (var i = operations.Length; i > 0; i--)
        {
            Assert.Equal(operations[i - 1].Label, session.Undo());
            Assert.Equal(versions[i - 1], TestProjects.WithoutRevision(File.ReadAllText(file)));
        }

        for (var i = 1; i <= operations.Length; i++)
        {
            Assert.Equal(operations[i - 1].Label, session.Redo());
            Assert.Equal(versions[i], TestProjects.WithoutRevision(File.ReadAllText(file)));
        }

        Assert.Equal(1 + 3 * operations.Length, session.Revision);
    }

    [Fact]
    public void UndoTakesBackItsOwnStepsAndKeepsWhatAnotherProgramAddedSince()
    {
        var (server, editor, directory) = TwoSessions("undo-selective");
        editor.Mutate("trim_layer clip_2", p => TimelineEditor.TrimLayer(p, "clip_2", null, 7));
        editor.Mutate("update_layer music", p => ProjectEditor.UpdateLayer(p, "music", new JsonObject { ["volume"] = 0.3 }));
        server.Mutate("add_caption_track en", TestProjects.AddCaptions);

        Assert.Equal("update_layer music", editor.Undo());
        Assert.Equal(0.8, Saved<AudioLayer>(directory, "music").Volume);
        Assert.Equal("trim_layer clip_2", editor.Undo());
        var undone = ProjectStore.Load(directory);
        Assert.Equal(8, Assert.IsType<VideoLayer>(undone.FindLayer("clip_2")).TrimOut);
        Assert.Equal(8, undone.FindLayer("label")!.End);
        Assert.Equal(2, undone.Layers.Count(l => l.ParentId == "captions_en"));
        var order = TestProjects.Footage().Layers.Select(l => l.Id).ToList();
        Assert.Equal(order, undone.Layers.Select(l => l.Id).Where(order.Contains));

        Assert.Equal("trim_layer clip_2", editor.Redo());
        Assert.Equal("update_layer music", editor.Redo());
        var redone = ProjectStore.Load(directory);
        Assert.Equal(7, Assert.IsType<VideoLayer>(redone.FindLayer("clip_2")).TrimOut);
        Assert.Equal(0.3, Assert.IsType<AudioLayer>(redone.FindLayer("music")).Volume);
        Assert.NotNull(redone.FindLayer("captions_en"));
        Assert.Equal(8, redone.Revision);

        // The backups say who saved what.
        Assert.Equal(
            ["editor redo update_layer music", "editor redo trim_layer clip_2", "editor undo trim_layer clip_2", "editor undo update_layer music", "add_caption_track en", "editor update_layer music", "editor trim_layer clip_2"],
            ProjectBackups.List(directory).Select(b => b.Before));
    }

    [Fact]
    public void UndoingASplitKeepsACaptionTrackAddedSince()
    {
        var (server, editor, directory) = TwoSessions("undo-split-captions");
        server.Mutate("split_layer clip_2", p => TimelineEditor.SplitLayer(p, "clip_2", 6));
        var split = ProjectStore.Load(directory);
        Assert.NotNull(split.FindLayer("clip_3"));

        // Adding the captions puts layers among the others: neither those nor the order they
        // give count as a change to what the split did.
        editor.Mutate("add_caption_track en", TestProjects.AddCaptions);
        Assert.Equal("split_layer clip_2", server.Undo());
        var undone = ProjectStore.Load(directory);
        Assert.Null(undone.FindLayer("clip_3"));
        Assert.Equal((4.0, 8.0), (undone.FindLayer("clip_2")!.Start, Assert.IsType<VideoLayer>(undone.FindLayer("clip_2")).TrimOut));
        Assert.Equal((4.0, 8.0), (undone.FindLayer("label")!.Start, undone.FindLayer("label")!.End!.Value));
        Assert.Equal(2, undone.Layers.Count(l => l.ParentId == "captions_en"));
        Assert.Equal(8, undone.Duration);
    }

    [Fact]
    public void UndoAndRedoAreRefusedWhenAnotherProgramChangedTheSameLayer()
    {
        var (server, editor, directory) = TwoSessions("undo-refused");
        server.Mutate("trim_layer clip_2", p => TimelineEditor.TrimLayer(p, "clip_2", null, 7));
        editor.Mutate("update_layer clip_2", p => ProjectEditor.UpdateLayer(p, "clip_2", new JsonObject { ["volume"] = 0.5 }));
        var revision = ProjectStore.Load(directory).Revision;

        var error = Assert.Throws<ZeyoCutException>(() => server.Undo());
        Assert.Equal($"can't undo 'trim_layer clip_2': another program changed layer 'clip_2' since that edit (saved: editor update_layer clip_2), and undoing it would throw that change away. {Backups}", error.Message);
        var kept = Saved<VideoLayer>(directory, "clip_2");
        Assert.Equal((7.0, 0.5), (kept.TrimOut, kept.Volume));
        Assert.Equal(revision, ProjectStore.Load(directory).Revision);
        Assert.Equal(1, server.UndoCount);

        server.Mutate("update_layer title", p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["text"] = "Server" }));
        server.Undo();
        editor.Mutate("update_layer title", p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["text"] = "Editor" }));
        error = Assert.Throws<ZeyoCutException>(() => server.Redo());
        Assert.Equal($"can't redo 'update_layer title': another program changed layer 'title' since it was undone (saved: editor update_layer title), and redoing it would throw that change away. {Backups}", error.Message);
        Assert.Equal("Editor", Saved<TextLayer>(directory, "title").Text);
        Assert.Equal(1, server.RedoCount);
    }

    [Fact]
    public void TheProjectsLengthFollowsTheLayersUnlessTheStepOnlyChangedTheLength()
    {
        var (server, editor, directory) = TwoSessions("undo-duration");
        server.Mutate("move_layer clip_2", p => TimelineEditor.MoveLayer(p, "clip_2", 9));
        Assert.Equal(13, ProjectStore.Load(directory).Duration);
        editor.Mutate("add_text_layer note", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "note", Text = "Note", Start = 1, End = 2 }));

        // The move made the project longer; undoing it makes it short again.
        server.Undo();
        Assert.Equal((4.0, 8.0), (Saved<VideoLayer>(directory, "clip_2").Start, ProjectStore.Load(directory).Duration));
        server.Redo();
        Assert.Equal((9.0, 13.0), (Saved<VideoLayer>(directory, "clip_2").Start, ProjectStore.Load(directory).Duration));

        // Unless another program's layer now runs past where it would end.
        editor.Mutate("update_layer note", p => ProjectEditor.UpdateLayer(p, "note", new JsonObject { ["end"] = 11 }));
        server.Undo();
        Assert.Equal((4.0, 13.0), (Saved<VideoLayer>(directory, "clip_2").Start, ProjectStore.Load(directory).Duration));
        Assert.NotNull(ProjectStore.Load(directory).FindLayer("note"));

        // A step that only changed the length is refused when another program changed it since.
        server.Mutate("update_project", p => p.Duration = 20);
        editor.Mutate("update_project", p => p.Duration = 15);
        var error = Assert.Throws<ZeyoCutException>(() => server.Undo());
        Assert.Equal($"can't undo 'update_project': another program changed the project's duration since that edit (saved: editor update_project), and undoing it would throw that change away. {Backups}", error.Message);
        Assert.Equal(15, ProjectStore.Load(directory).Duration);
    }

    [Fact]
    public void AStepIsComparedAsProjectJsonHoldsIt()
    {
        var (server, editor, directory) = TwoSessions("undo-normalized");

        // Keyframes added out of order are saved so, and read back in order.
        server.Mutate("set_keyframes title", p =>
        {
            var title = (VisualLayer)p.FindLayer("title")!;
            title.Opacity.Keyframes.Add(new Keyframe { Time = 2, Value = 0 });
            title.Opacity.Keyframes.Add(new Keyframe { Time = 1, Value = 1 });
            return 0;
        });
        editor.Mutate("update_layer music", p => ProjectEditor.UpdateLayer(p, "music", new JsonObject { ["volume"] = 0.3 }));

        Assert.Equal("set_keyframes title", server.Undo());
        Assert.Empty(Saved<TextLayer>(directory, "title").Opacity.Keyframes);
        Assert.Equal(0.3, Saved<AudioLayer>(directory, "music").Volume);
    }

    [Fact]
    public void ARemovedLayerComesBackInItsPlace()
    {
        var (server, editor, directory) = TwoSessions("undo-removed");
        var order = TestProjects.Footage().Layers.Select(l => l.Id).ToList();
        server.Mutate("remove_layer clip_1", p => ProjectEditor.RemoveLayer(p, "clip_1", withChildren: false));
        server.Mutate("remove_layer clip_2", p => ProjectEditor.RemoveLayer(p, "clip_2", withChildren: true));
        editor.Mutate("update_layer title", p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["text"] = "Edited" }));
        editor.Mutate("add_markers (1 marker)", p => MarkerEditor.AddMarkers(p, [new MarkerEntry("intro", 1, "Intro", null)]));

        server.Undo();
        server.Undo();
        var undone = ProjectStore.Load(directory);
        Assert.Equal(order, undone.Layers.Select(l => l.Id));
        Assert.Equal("Edited", Assert.IsType<TextLayer>(undone.FindLayer("title")).Text);
        Assert.Equal("intro", Assert.Single(undone.Markers).Id);
    }

    [Fact]
    public void TheOrderOfTheLayersGoesBackUnlessAnotherProgramChangedIt()
    {
        var before = TestProjects.Rig();
        var after = ProjectJson.Clone(before);
        after.Layers = [after.Layers[2], after.Layers[0], after.Layers[1]];
        ProjectEditor.AddImageLayer(after, new ImageLayerOptions { Id = "added" });
        var deltas = ProjectDiff.Deltas(before, after);
        Assert.Equal(["layer 'added'", "the order of the layers"], deltas.Select(d => d.Describe()).Order());

        // Another program added a layer of its own: that doesn't change the order the step made.
        var outside = ProjectJson.Clone(after);
        ProjectEditor.AddImageLayer(outside, new ImageLayerOptions { Id = "theirs" });
        Assert.Empty(ProjectDiff.Conflicts(outside, deltas, backwards: true));
        ProjectDiff.Apply(outside, deltas, backwards: true);
        Assert.Equal(["tora_body", "tora_head", "tora_tail", "theirs"], outside.Layers.Select(l => l.Id));

        // Another program reordered them: that does.
        var reordered = ProjectJson.Clone(after);
        reordered.Layers = [reordered.Layers[0], reordered.Layers[2], reordered.Layers[1], reordered.Layers[3]];
        Assert.Equal("the order of the layers", Assert.Single(ProjectDiff.Conflicts(reordered, deltas, backwards: true)).Describe());
    }

    [Fact]
    public void HistoryListsTheStepsWithTheChangesOtherProgramsSavedAmongThem()
    {
        var (server, editor, _) = TwoSessions("undo-history");
        editor.Mutate("trim_layer clip_2", p => TimelineEditor.TrimLayer(p, "clip_2", null, 7));
        server.Mutate("add_caption_track en", TestProjects.AddCaptions);
        server.Mutate("update_layer title", p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["text"] = "Hi" }));
        editor.Mutate("move_layer title", p => TimelineEditor.MoveLayer(p, "title", 1));

        Assert.Equal(
            [("trim_layer clip_2", 2L, false), ("add_caption_track en, update_layer title", 4L, true), ("move_layer title", 5L, false)],
            editor.History.Select(h => (h.Label, h.Revision, h.Outside is not null)));
        var items = editor.History[1].Outside!.Items.Select(i => i.Describe()).ToList();
        Assert.Contains(items, i => i.StartsWith("added layer 'captions_en'", StringComparison.Ordinal));
        Assert.Contains("changed layer 'title'", items);

        editor.Undo();
        Assert.Equal(["trim_layer clip_2", "add_caption_track en, update_layer title"], editor.History.Select(h => h.Label));
    }

    [Fact]
    public void TheEditorsBackupsDontPushOutTheServers()
    {
        var directory = TestPaths.NewDirectory("backups-grouped");
        var server = ProjectSession.Create(directory, TestProjects.Empty());
        var editor = ProjectSession.Open(directory, ProjectBackups.EditorProgram);
        server.Mutate("update_project s1", p => p.Name = "s1");
        editor.Mutate("update_project 0", p => p.Name = "e0");
        server.Sync();
        server.TakeOutsideChanges();
        for (var i = 1; i <= 25; i++)
        {
            editor.Mutate($"update_project {i}", p => p.Name = $"e{i}");
        }

        server.Mutate("update_project s2", p => p.Name = "s2");
        var backups = ProjectBackups.List(directory);
        Assert.Equal(ProjectBackups.Kept, backups.Count(b => b.Before!.StartsWith("editor ", StringComparison.Ordinal)));
        Assert.Equal(["update_project s2", "update_project s1"], backups.Select(b => b.Before).Where(b => !b!.StartsWith("editor ", StringComparison.Ordinal)));

        // The server knew of the editor's first backup, which has gone since: it names the edits
        // saved after it that are still kept, and none from before.
        var seen = Assert.IsType<OutsideChange>(server.TakeOutsideChanges());
        Assert.Equal(Enumerable.Range(6, 20).Select(i => $"editor update_project {i}"), seen.Edits);
    }
}
