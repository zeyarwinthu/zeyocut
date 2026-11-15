// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ZeyoCut.Core;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;
using ZeyoCut.Core.Serialization;
using ZeyoCut.Mcp;
using ZeyoCut.Mcp.Tools;
using ZeyoCut.Tests.TestSupport;
using static ZeyoCut.Tests.Mcp.McpServerTests;

namespace ZeyoCut.Tests.Mcp;

// The server and another process (here the test, as the editor will) saving one project.
public class SyncServerTests
{
    private const string ReRead = "Re-read with get_project or list_layers before relying on earlier details.";

    [Fact]
    public async Task ServerTakesEditsAnotherProcessSavedAndSaysWhatChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-sync");
        var projects = Path.Combine(root, "projects");
        var directory = Path.Combine(projects, "shared");
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "shared", ["width"] = 320, ["height"] = 180, ["duration"] = 5.0 }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Hello", ["id"] = "title" }, ct);

        var editor = ProjectSession.Open(directory);
        Assert.Equal(2, editor.Revision);
        editor.Mutate("editor update_layer title", p => ProjectEditor.UpdateLayer(p, "title", new JsonObject { ["text"] = "Edited" }));
        editor.Mutate("editor add_markers (1 marker)", p => MarkerEditor.AddMarkers(p, [new MarkerEntry("intro", 1, "Intro", null)]));

        var listed = await Call(client, "list_layers", new(), ct);
        Assert.False(listed.IsError == true);
        var note = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(listed.Content[0]).Text;
        Assert.Equal(
            "project.json was changed outside this server since your last call (now revision 4; saved: editor update_layer title, editor add_markers (1 marker)). What changed:\n"
            + "- changed layer 'title'\n"
            + "- added marker 'intro' (Intro)\n"
            + ReRead,
            note);
        Assert.Contains("title", Text(listed)[note.Length..]);
        Assert.DoesNotContain(ReRead, await CallOk(client, "list_layers", new(), ct));

        // The server's next edit is made on top of the editor's.
        var size = ((TextLayer)ProjectStore.Load(directory).FindLayer("title")!).FontSize;
        Assert.NotEqual(40, size);
        await CallOk(client, "update_layer", new() { ["layerId"] = "title", ["changes"] = new Dictionary<string, object> { ["fontSize"] = 40 } }, ct);
        var project = JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)).RootElement;
        Assert.Equal(5, project.GetProperty("revision").GetInt64());
        Assert.Equal("intro", project.GetProperty("markers")[0].GetProperty("id").GetString());
        var title = project.GetProperty("layers")[0];
        Assert.Equal("Edited", title.GetProperty("text").GetString());
        Assert.Equal(40, title.GetProperty("fontSize").GetDouble());
        Assert.Equal(5, ProjectStore.Load(directory).Revision);

        // Undo takes back the server's own edit and keeps the editor's. It can't take back adding
        // 'title': the editor changed it since.
        Assert.StartsWith("undid: update_layer title (1 more undo step(s), 1 redo step(s))", await CallOk(client, "undo", new(), ct));
        var stopped = await Call(client, "undo", new(), ct);
        Assert.True(stopped.IsError == true);
        Assert.Contains(
            "can't undo 'add_text_layer title': another program changed layer 'title' since that edit (saved: editor update_layer title, editor add_markers (1 marker)), "
            + "and undoing it would throw that change away. restore_backup lists the copies of project.json kept before each save",
            Text(stopped));
        var undone = ProjectStore.Load(directory);
        Assert.Equal(6, undone.Revision);
        Assert.Equal("Edited", ((TextLayer)undone.FindLayer("title")!).Text);
        Assert.Equal(size, ((TextLayer)undone.FindLayer("title")!).FontSize);

        // A failing call gets the note too, and at most 10 items are listed.
        editor.Mutate("editor add_markers (12 markers)", p => MarkerEditor.AddMarkers(p, Enumerable.Range(1, 12).Select(i => new MarkerEntry($"m{i}", i * 0.25, $"Marker {i}", "scene")).ToList()));
        var failed = await Call(client, "remove_layer", new() { ["layerId"] = "nope" }, ct);
        Assert.True(failed.IsError == true);
        var text = Text(failed);
        Assert.Contains("project.json was changed outside this server since your last call (now revision 7; saved: editor add_markers (12 markers)). What changed:\n- added marker 'm1' (Marker 1)\n", text);
        Assert.Contains("- added marker 'm10' (Marker 10)\n- and 2 more\n" + ReRead, text);
        Assert.DoesNotContain("'m11'", text);
        Assert.Contains("nope", text[text.IndexOf(ReRead, StringComparison.Ordinal)..]);
        Assert.Equal(13, JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)).RootElement.GetProperty("markers").GetArrayLength());
    }

    [Fact]
    public async Task UndoStepsOverAStepItRefusedBeforeAndNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-undo-step-over");
        var projects = Path.Combine(root, "projects");
        var directory = Path.Combine(projects, "shared");
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "shared", ["width"] = 320, ["height"] = 180, ["duration"] = 5.0 }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Hello", ["id"] = "title" }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "World", ["id"] = "sub" }, ct);
        var size = ((TextLayer)ProjectStore.Load(directory).FindLayer("title")!).FontSize;
        Assert.NotEqual(40, size);
        await CallOk(client, "update_layer", new() { ["layerId"] = "title", ["changes"] = new Dictionary<string, object> { ["fontSize"] = 40 } }, ct);
        await CallOk(client, "update_layer", new() { ["layerId"] = "sub", ["changes"] = new Dictionary<string, object> { ["fontSize"] = 40 } }, ct);

        var editor = ProjectSession.Open(directory, ProjectBackups.EditorProgram);
        editor.Mutate("update_layer sub", p => ProjectEditor.UpdateLayer(p, "sub", new JsonObject { ["text"] = "Edited" }));

        var refused = await Call(client, "undo", new(), ct);
        Assert.True(refused.IsError == true);
        Assert.Contains(
            "can't undo 'update_layer sub': another program changed layer 'sub' since that edit (saved: editor update_layer sub), and undoing it would throw that change away. "
            + "restore_backup lists the copies of project.json kept before each save. The next undo steps over this step and goes on to the one before it",
            Text(refused));

        Assert.Equal(
            "undid: update_layer title (3 more undo step(s), 1 redo step(s)). Stepped over 'update_layer sub', which can't be undone on top of another program's changes",
            await CallOk(client, "undo", new(), ct));
        var saved = ProjectStore.Load(directory);
        var sub = (TextLayer)saved.FindLayer("sub")!;
        Assert.Equal((size, 40.0, "Edited"), (((TextLayer)saved.FindLayer("title")!).FontSize, sub.FontSize, sub.Text));
    }

    [Fact]
    public async Task RedoStepsOverAStepItRefusedBeforeAndNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-redo-step-over");
        var projects = Path.Combine(root, "projects");
        var directory = Path.Combine(projects, "shared");
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "shared", ["width"] = 320, ["height"] = 180, ["duration"] = 5.0 }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "Hello", ["id"] = "title" }, ct);
        await CallOk(client, "add_text_layer", new() { ["text"] = "World", ["id"] = "sub" }, ct);
        var size = ((TextLayer)ProjectStore.Load(directory).FindLayer("sub")!).FontSize;
        Assert.NotEqual(40, size);
        await CallOk(client, "update_layer", new() { ["layerId"] = "sub", ["changes"] = new Dictionary<string, object> { ["fontSize"] = 40 } }, ct);
        await CallOk(client, "update_layer", new() { ["layerId"] = "title", ["changes"] = new Dictionary<string, object> { ["fontSize"] = 40 } }, ct);
        await CallOk(client, "undo", new(), ct);
        await CallOk(client, "undo", new(), ct);

        var editor = ProjectSession.Open(directory, ProjectBackups.EditorProgram);
        editor.Mutate("update_layer sub", p => ProjectEditor.UpdateLayer(p, "sub", new JsonObject { ["text"] = "Edited" }));

        var refused = await Call(client, "redo", new(), ct);
        Assert.True(refused.IsError == true);
        Assert.Contains(
            "can't redo 'update_layer sub': another program changed layer 'sub' since it was undone (saved: editor update_layer sub), and redoing it would throw that change away. "
            + "restore_backup lists the copies of project.json kept before each save. The next redo steps over this step and goes on to the one after it",
            Text(refused));

        Assert.Equal(
            "redid: update_layer title (3 undo step(s), 1 more redo step(s)). Stepped over 'update_layer sub', which can't be redone on top of another program's changes",
            await CallOk(client, "redo", new(), ct));
        var saved = ProjectStore.Load(directory);
        var sub = (TextLayer)saved.FindLayer("sub")!;
        Assert.Equal((40.0, size, "Edited"), (((TextLayer)saved.FindLayer("title")!).FontSize, sub.FontSize, sub.Text));
    }

    [Fact]
    public async Task ServerAndAnotherProcessSavingAtOnceLoseNothing()
    {
        const int each = 50;
        var ct = TestContext.Current.CancellationToken;
        var root = TestPaths.NewDirectory("mcp-sync-hammer");
        var projects = Path.Combine(root, "projects");
        var directory = Path.Combine(projects, "hammer");
        await using var client = await StartServer(root, projects, Path.Combine(root, "logs"), ct);
        await CallOk(client, "create_project", new() { ["name"] = "hammer", ["width"] = 320, ["height"] = 180, ["duration"] = 10.0 }, ct);
        var editor = ProjectSession.Open(directory);

        var server = Task.Run(async () =>
        {
            for (var i = 0; i < each; i++)
            {
                var marker = new Dictionary<string, object?> { ["time"] = i * 0.1, ["name"] = $"server {i}", ["id"] = $"s_{i}" };
                await CallOk(client, "add_markers", new() { ["markers"] = new[] { marker } }, ct);
            }
        }, ct);
        var other = Task.Run(() =>
        {
            for (var i = 0; i < each; i++)
            {
                var entry = new MarkerEntry($"e_{i}", i * 0.1, $"editor {i}", "scene");
                editor.Mutate($"editor add_markers e_{i}", p => MarkerEditor.AddMarkers(p, [entry]));
            }
        }, ct);
        await Task.WhenAll(server, other);

        var saved = ProjectStore.Load(directory);
        Assert.Equal(2 * each, saved.Markers.Count);
        Assert.Equal(1 + 2 * each, saved.Revision);
        var project = JsonDocument.Parse(await CallOk(client, "get_project", new(), ct)).RootElement;
        Assert.Equal(2 * each, project.GetProperty("markers").GetArrayLength());
        Assert.Equal(1 + 2 * each, project.GetProperty("revision").GetInt64());
    }

    // generate_captions behindSubject=auto measures up to 600 caption frames before it saves. What
    // another program saves meanwhile is kept, unless it changed what the captions are measured on.
    // Run in the server's own process, so the other program's save lands between the plan and the save.
    [Fact]
    public void CaptionsMeasuredDirectlyAreSavedOnTopOfAnotherProgramsSave()
    {
        Ffmpeg.Require();
        var root = TestPaths.NewDirectory("mcp-sync-captions");
        var directory = Path.Combine(root, "projects", "talk");
        var session = ProjectSession.Create(directory, TestProjects.Empty(width: 96, height: 64, fps: 10, duration: 2));
        var asset = TestVideos.Import(session, TestVideos.GreenScreen(Path.Combine(root, "screen.mp4"), 2), "screen");
        session.Mutate("add_video_layer clip", p => ProjectEditor.AddVideoLayer(p, new VideoLayerOptions { Id = "clip", Asset = "screen" }));
        session.Mutate("add_text_layer title", p => ProjectEditor.AddTextLayer(p, new TextLayerOptions { Id = "title", Text = "Hi" }));
        session.Mutate("text_behind_subject clip", p =>
            SubjectEditor.Apply(p, "clip", ["title"], false, MatteEditor.Build((IMattable)p.FindLayer("clip")!, MatteMethod.Chroma, new MatteValues("#28BE3C", null, null, null))));
        AnalysisFiles.Save(AnalysisFiles.TranscriptPath(directory, asset), new Transcript
        {
            Asset = "screen",
            Language = "en",
            Words = [new TranscriptWord { Text = "Hi.", Start = 0.1, End = 0.3, Probability = 0.9 }, new TranscriptWord { Text = "Go.", Start = 0.9, End = 1.1, Probability = 0.9 }],
        });
        using var state = new EditorState(new Settings(null, Path.Combine(root, "projects"), Path.Combine(root, "logs"), LibraryPath: Path.Combine(root, "library")));
        state.Open(session);
        var editor = ProjectSession.Open(directory);
        string Captions() => CaptionTools.GenerateCaptions(state, assetId: "screen", style: "bold_center", fontSize: 14, behindSubject: "auto");

        // A marker added meanwhile is kept, and the fades are added.
        editor.Mutate("editor add_markers (1 marker)", p => MarkerEditor.AddMarkers(p, [new MarkerEntry("intro", 1, "Intro", null)]));
        var added = Captions();
        Assert.Contains("\n- auto: the subject covers up to ", added);
        Assert.DoesNotContain("get_job_status", added);
        var saved = ProjectStore.Load(directory);
        Assert.Equal("intro", Assert.Single(saved.Markers).Id);
        Assert.NotNull(saved.FindLayer("captions_subject"));

        // The clip the captions follow, moved meanwhile, refuses them.
        editor.Mutate("editor update_layer clip", p => ProjectEditor.UpdateLayer(p, "clip", new JsonObject { ["x"] = 10 }));
        Assert.Equal("layer 'clip' changed while the captions were measured; run generate_captions again",
            Assert.Throws<McpException>(Captions).Message);
        Assert.Equal(saved.Revision + 1, ProjectStore.Load(directory).Revision);
    }

    [Fact]
    public void TheNoteListsTenItemsAndEndsWithTheReminder()
    {
        var items = Enumerable.Range(1, 12).Select(i => new ProjectItemChange("layer", $"l{i}", i == 1 ? "First" : null, ItemChange.Changed)).ToList();
        Assert.Equal(
            "project.json was changed outside this server since your last call (now revision 9; saved: e2, e3, e4, e5, e6 and 1 earlier). What changed:\n"
            + "- changed layer 'l1' (First)\n"
            + string.Concat(Enumerable.Range(2, 9).Select(i => $"- changed layer 'l{i}'\n"))
            + "- and 2 more\n"
            + ReRead,
            OutsideChangeNotes.Format(new OutsideChange(9, ["e1", "e2", "e3", "e4", "e5", "e6"], items, null)));
        Assert.Equal(
            "project.json was changed outside this server and can't be read (invalid project JSON: x). The open project is still revision 3; "
            + "edits are refused until the file is fixed or restore_backup puts a backup back.\n"
            + ReRead,
            OutsideChangeNotes.Format(new OutsideChange(3, [], [], "invalid project JSON: x")));
    }
}
