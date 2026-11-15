# Review of `feature/manual-editor`

- Reviewed 2026-11-14 at `a5f463e`, against `main` at `f968714`: 22 commits, 122 files, +15,519 −550. Review only; no code was changed.
- Read in full: Core's save path (`ProjectLock`, `ProjectStore`, `ProjectSession`, `ProjectDiff`, `EditorDocument`, `ProjectBackups`, `EditOperation`, `ValueEditor`, `PlacementEditor`), every changed file in `ZeyoCut.Mcp`, the code moved into Core and Render, and `ZeyoCut.Editor` (except the styles in `Theme.xaml`).
- **Reproduced** means I ran it against the built assemblies, with a throwaway console program kept outside the repository. It calls the real Core, Render and Editor code.
- I did not drive the editor window. Findings about the window come from reading its code, and where possible from running the code underneath it.
- **Possible** means I could not confirm it.
- Not done: the Release publish, and a run of the three Whisper tests (see below).
- **Status, 2026-11-15:** findings 1, 2, 3, 4, 8 and 12 have since been fixed on this branch, one commit each. Findings 5, 6, 7, 9, 10 and 11 are deferred. The findings below stand as written at the review; each has a Status line.

## Status of the findings

| Finding | Status | Commit |
|---|---|---|
| 1. Both programs make the same proxy at once | Fixed | `461a18b` |
| 2. A value typed before Claude's change is saved over it | Fixed | `204187d` |
| 3. One refused undo blocks every step under it | Fixed | `48606f9` |
| 4. No backup holds the project as Claude left it | Fixed | `68297ce` |
| 5. The caption-fade job refuses after any save | Deferred | |
| 6. The frame on screen isn't redrawn when a file changes without a save | Deferred | |
| 7. The project's length can't be changed in the editor | Deferred | |
| 8. Errors the editor doesn't catch end in a stack-trace dialog | Fixed | `eadb15e` |
| 9. The once-a-second check does work that grows with the project | Deferred | |
| 10. Saves run on the UI thread | Deferred | |
| 11. Closing can block the window | Deferred | |
| 12. Ctrl+Z in a field can undo a project edit | Fixed | `8ae73d9` |

- **Tests after the fixes** (at `eadb15e`): `ZeyoCut.Tests` 580 total, 0 failed, 0 skipped, with `WHISPER_PATH` set; `ZeyoCut.Editor.Tests` 73 total, 0 failed. Both `dotnet format --verify-no-changes` checks pass.
- Each fix began with a test that failed. Findings 2, 3, 8 and 12 were also checked in the real window, shown off screen by a throwaway program kept outside the repository.
- **Left open inside the fixed findings:**
    - Finding 1: the lock per proxy across processes isn't built. Two programs can still each encode the same proxy; the one that finishes second drops its copy.
    - Finding 3: only undo steps over a refused step. A refused redo still stops the redos after it.
    - Finding 8: `Rebuild` still sets `_rebuilding` without a `finally`, and `OpenProject` still calls `ShowProject` outside its `try`; what made them throw is fixed. An error of any other kind still gets the dialog with the stack trace (`App.xaml.cs`).
- **Test gaps** (section 5): the two-writer, undo and backup gaps are closed. A job that saves after another program's change, and tests that drive `MainWindow` itself, are still open.

## Test run

| Suite | Total | Failed | Skipped | Time |
|---|---|---|---|---|
| `ZeyoCut.Tests` | 565 | 0 | 3 | 116 s |
| `ZeyoCut.Editor.Tests` | 69 | 0 | 0 | 0.9 s |

- The build has 0 errors and 9 warnings, all xUnit1031/xUnit1051 in `JobRecoveryTests`, `ProxyTests` and `RenderStatsTests`.
- The 3 skipped tests need `whisper-cli`, which was not on the PATH of the shell I ran them in (`WHISPER_PATH` was not set).

## Summary

- **One must-fix.** The server and the editor can both make the same proxy file at the same time, and the file left behind is damaged (finding 1). Reproduced.
- **No way found to lose or damage `project.json`** between two programs that both use `project.lock`. The lock, the hash check, the save and selective undo hold up.
- **Four should-fix items around working alongside Claude**: an edit typed before Claude's change is saved over it unchecked (2), one refused undo blocks all earlier steps (3), no backup keeps the project as Claude left it (4), and the caption-fade job refuses after any edit in the editor (5).
- **MCP tools**: no regression found in names, parameters or the moved code.
- The rest is error handling, speed on very large projects, and tests.

## 1. Data loss or corruption

### Finding 1: both programs make the same proxy at once, and a damaged proxy stays

- **Severity:** Must fix.
- **Status:** Fixed in `461a18b`. Proxies, the preview sound mix and exports are written to partial files with unique names; a proxy or mix that is already there at rename time is kept; tests run two writers at once. The lock across processes ("Better" below) is deferred.
- **Where:**
    - `src/ZeyoCut.Render/Video/Proxies.cs:88` writes every proxy to the one name `<name>.partial.mp4`, with `-y` (`:63`).
    - `src/ZeyoCut.Render/Video/ProxyJobs.cs:17` and `:47` keep "being made" in memory, so it only stops a second job in the same process.
    - The editor starts proxy jobs at `src/ZeyoCut.Editor/MainWindow.xaml.cs:462` (opening a project), `:536` (after every save by another program) and `:1704` (after its own import).
    - The server starts them in `open_project` (`src/ZeyoCut.Mcp/Tools/ProjectTools.cs:132`), `import_video` (`VideoTools.cs:39`, `:51`) and `import_folder` (`AssetTools.cs:128`, `:195`).
- **What goes wrong:**
    1. The editor has the project open.
    2. Claude imports footage wider than 2560 px. The server adds the asset and starts `proxy_N`.
    3. Within a second the editor takes the new revision and starts its own proxy job for the same asset (`:536`). A second ffmpeg now writes the same `.partial.mp4`.
    4. The job that finishes first cannot rename the file and fails. The other renames the mixed file into place and reports success.
    5. `Proxies.Ready` (`Proxies.cs:54`) only checks that the file exists. From then on the editor's preview, `render_frame`, the preview page and `track_object` (`TrackTools.cs:288`) read the damaged proxy. Nothing makes it again.
    - The same happens when `open_in_editor` is called while the server's proxy job is still running (`:462`).
    - Exports read the original footage, so exported video is not affected.
- **Reproduced:** two `Proxies.Make` calls on one 40 s 3840×2160 clip.

    | Second starts after | First job | Second job | Proxy left in place |
    |---|---|---|---|
    | (alone) | made | not run | 1200 of 1200 frames, 0 ffprobe errors |
    | 0.7 s | IOException: used by another process | made | 300 of 1200 frames readable, 3,860 error lines |
    | 2.0 s | IOException: used by another process | made | 780 of 1200 frames readable, 1,956 error lines |

- **Fix (small):** write to a unique partial name, as `ChunkedExport.cs:356` does (`<name>.<guid>.partial.mp4`). When the proxy already exists at rename time, keep it and delete the partial. Add a test that runs two `Proxies.Make` at once and checks the file.
- **Better (small to medium):** a lock per proxy that works across processes (a file opened with `FileShare.None`, like `project.lock`), so the second program waits and then finds the proxy ready instead of encoding 4K footage twice.
- **Same pattern, lower risk, not reproduced:**
    - `src/ZeyoCut.Render/Export/PreviewAudio.cs:57` writes `mix-<key>.partial.m4a` with `-y` (`FfmpegArgs.cs:239`). The preview page and the editor both mix the same key within a second of an edit.
    - `src/ZeyoCut.Render/Export/Exporter.cs:72` uses `<name>.partial.mp4`. It collides only when both programs export to the same file name at once.
- **Confidence:** high for proxies; medium for the mix.
- PLAN.md section 3 says of these folders "Both processes only add files there, so nothing changes". That holds for chunks and imports, which use unique names, but not for proxies and mixes.

### Finding 2: a value typed before Claude's change is saved over it without the check

- **Severity:** Should fix.
- **Status:** Fixed in `204187d`. Inspector, preview-drag and timeline-drag edits are checked against the version they were built from. On a refusal the status bar says why and the typed text stays in the field.
- **Where:**
    - `src/ZeyoCut.Editor/MainWindow.xaml.cs:622-629`: while a field holds unsaved text, the inspector is deliberately not rebuilt after an outside change.
    - `:739-783`: `CommitField` then uses that old field's `Commit`.
    - `src/ZeyoCut.Core/Editing/EditorDocument.cs:48-58`: the "another program changed this layer" check compares against `View`, which has already been refreshed, so it passes.
    - `src/ZeyoCut.Editor/InspectorFields.cs:89`, `:131-132`, `:234`, `:368-371`: fields that compute from the layer as it was (Length from the old start, a caption's X and Y as a difference, keyframe or static, crop sides).
- **What goes wrong:**
    1. Select a caption and type in Text. Don't press Enter yet.
    2. Claude runs `update_layer` on the same caption (new text, new start).
    3. Within a second the timeline and preview show Claude's version. The inspector still shows yours.
    4. Press Enter. Your text is saved over Claude's and the status bar says "Saved · revision N". No refusal, no warning.
    - With Length instead of Text, the new end is worked out from the old start. In my run the layer had moved to 5–7 s and typing 3 gave "the end (5s) must be after the start (5s)".
- **Reproduced** below the window, with the real `InspectorFields` and `EditorDocument`:
    - Without a refresh first, the edit is refused as designed: "another program changed layer 'cue' first".
    - With the refresh, which the window always does within a second, it is saved.
- **Fix (small):** remember the project the inspector rows were built from. Pass it to `EditorDocument.Apply` as the version the user saw, so the existing check compares against that. On a refusal, keep the typed text in the field and say why. Use the same basis for `PreviewDrag` (`PreviewDrag.cs:19`) and timeline drags.
- **Confidence:** high on the mechanism; not confirmed by driving the window.

### Finding 3: one refused undo blocks every step under it

- **Severity:** Should fix.
- **Status:** Fixed in `48606f9`. After a refused undo, the next one steps over that step and names it, in the editor and in the server's `undo`. The editor's message no longer names `restore_backup`.
- **Where:** `src/ZeyoCut.Core/Editing/ProjectSession.cs:204-227`. `Undo` always takes the last step; when `PutBack` refuses (`:379-394`) the step stays on top and nothing can skip or drop it.
- **What goes wrong:**
    1. In the editor: move `title` (step A), then change the text of `sub` (step B).
    2. Claude changes `sub`.
    3. Ctrl+Z: "can't undo 'update_layer sub': another program changed layer 'sub' since that edit…".
    4. Every further Ctrl+Z gives the same message. Step A can't be undone for the rest of the session.
    - The same applies to the server's `undo`.
    - The message ends "restore_backup lists the copies…". That is a tool the editor doesn't have; the Assistant view lists saves but can't put one back.
- **Reproduced:** three undos in a row, each refused, with 2 undo steps left each time.
- **Fix (small to medium):** let a refused step be dropped or stepped over. For example, after a refusal the next Ctrl+Z skips it and says which step was skipped. In the editor, word the message without the tool name.
- **Confidence:** high.

### Finding 4: after 20 editor edits, no backup holds the project as Claude left it

- **Severity:** Should fix.
- **Status:** Fixed in `68297ce`. The backup that holds the other program's last version isn't pruned until a newer backup holds that version.
- **Where:** `src/ZeyoCut.Core/Editing/ProjectBackups.cs:189-197` keeps the newest 20 per group.
- **What goes wrong:**
    1. Claude builds the project. Each server backup holds the file before one of Claude's edits, so none holds the result of the last one.
    2. That last version is kept only by the editor's first backup ("before editor …").
    3. Every drag and every field change in the editor is a save. After 21 of them that backup is pruned.
    4. Close the editor, and its undo history is gone too. Nothing can go back to what Claude built.
- **Reproduced:** 3 server edits and 30 editor edits leave 20 editor and 3 server backups. None holds the project as the server left it.
- **Fix (small):** don't count toward the 20 the editor backup that follows a save by another program (or the first one of an editor session). Or thin the editor's backups by age, as change 1 of the plan suggested ("at most one editor backup a minute").
- **Confidence:** high.

### Finding 5: the caption-fade job refuses after any save, which the editor now makes likely

- **Severity:** Should fix. It can wait for 0.2.x.
- **Status:** Deferred.
- **Where:** `src/ZeyoCut.Mcp/Tools/CaptionTools.cs:102`, `:185`, `:193` compare the whole project JSON, which now includes `revision`.
- **What goes wrong:**
    1. Claude runs `generate_captions behindSubject=auto`. The captions are added and the matte is computed for minutes.
    2. You nudge a title in the editor.
    3. The job ends with "the project changed after the captions were added, so the fades weren't added". Only `get_job_status` shows it; the captions stay behind the subject the whole time.
    - This was the documented behavior for an edit by Claude. With the editor open it becomes the usual case.
    - A save that changes nothing but the revision (undo, then redo) also triggers it.
- **Fix (medium):** compare only what the fades depend on: the caption layers, the layers that cover them, the canvas and the frame rate. `ProjectDiff.Compare` already lists the changed items.
- **Confidence:** high from reading; not run (it needs an AI matte).

### Solid in this area

- **`project.lock`, the SHA-256 check and `Sync`:** read line by line; no path found where a save by a cooperating program is lost. `Sync` reads outside the lock, and every save checks again under it.
- **The save itself:** temp file, flush, POSIX rename with `File.Move` as the fallback, backup before the save. A failure leaves the old file and the session's state unchanged.
- **Revision:** never goes back, including after undo, `restore_backup` and an older file put back.
- **Selective undo and redo:** the change sets, the soft duration, layer order and item placement do what PLAN.md says. I found no sequence that silently drops another program's change.
- **Other background jobs:** the normalize job, `track_object`, `reanchor_track` and `detect_and_track` check inside the save and refuse cleanly when the project changed under them.
- **Imports:** files are copied under unique partial names before the lock is taken.

## 2. MCP regressions

- **Tool names and parameters:** unchanged for the 90 existing tools. `trim_layer` and `open_in_editor` are new. The descriptions of `undo` and `restore_backup` and the server instructions changed.
- **Moved code**, compared with `main` line by line: same checks in the same order, same messages.
    - `Settings` into Core, with `EditorPath` added.
    - `VideoImport` and `ProxyJobs` into Render.
    - `ExportStart` into Render.
    - `PreviewFrames` into Render. The page's cache key is still SHA-256 of `"{key}|{w}x{h}"`.
- **Intended behavior changes**, all documented:
    - The reply note after an outside change.
    - Format 7 with `revision`, which 0.1.0 can't open.
    - `import_asset`, `import_video` and `import_folder` copy the file before taking the lock.
- **Not a regression inside one process, but new across two:** finding 1 (proxies) and finding 5 (caption fades).

## 3. Export parity

### Finding 6: the frame on screen isn't redrawn when a file changes without a save

- **Severity:** Nice to have.
- **Status:** Deferred.
- **Where:** `src/ZeyoCut.Render/Export/FrameKeys.cs:177-187` keeps each file's stamp for the life of the keys. `src/ZeyoCut.Editor/FramePreview.cs:59-63` makes new keys only when asked for a frame of a new project object. Nothing asks again when the server finishes an AI matte.
- **What goes wrong:**
    1. Claude runs `remove_background` with an AI method. The editor draws the clip with its background, as it should while the matte is computed.
    2. The matte finishes. `project.json` doesn't change.
    3. The paused frame keeps its background until you move the playhead or anything is saved. An export has the background removed.
- Proxies already have a signal for this (`FramePreview.cs:72`). Mattes, which only the server makes, have none.
- **Fix (small):** ask for the paused frame again every few seconds while idle (a cached frame costs nothing), or watch the matte folder.
- **Confidence:** medium. The stamp caching is reproduced; I didn't run it end to end with a matte.

### Finding 7: the project's length can't be changed in the editor

- **Severity:** Nice to have.
- **Status:** Deferred.
- **Where:** `src/ZeyoCut.Editor/MainWindow.xaml.cs:658-674` shows the project's rows as text only. `src/ZeyoCut.Editor/TimelineView.cs:904-906` shades only the ruler past the end.
- **What goes wrong:**
    - Delete or shorten the last clip and the duration stays, so the export ends with empty frames.
    - Sound that runs past the end is drawn in full on its lane. The export cuts it at the end.
- The preview and the export agree, so this is a gap, not a mismatch.
- **Fix (small):** a Duration field through `ProjectEditor.UpdateProject`, and shade the lanes past the end as well as the ruler.
- **Confidence:** high.

### Solid in this area

- **Export from the editor:** goes through `ExportStart` and `ExportJobManager` on a snapshot, as `start_export` does.
- **Render scale:** only playback sets it; exports draw at 1, and a test compares a half-size frame with the full frame made smaller.
- **Controls:** every handler in `MainWindow.xaml` leads to a Core operation or a view setting. Controls that can't apply are disabled with a tooltip. I found none that does nothing.
- **Known and documented:** the preview draws every language at once, and footage over 2560 px comes from its proxy.

## 4. Editor stability

### Finding 8: errors the editor doesn't catch end in a stack-trace dialog

- **Severity:** Should fix (small). It can wait for 0.2.x.
- **Status:** Fixed in `eadb15e`. IO errors go to the status bar, a project's problems are named there when it opens, and the media list, the asset rows and the proxy jobs tolerate a file outside `assets\`. One line below was wrong: for footage over 2560 px with such a file, the server's `open_project` answered with an error too; it now lists the problem.
- **Where:**
    - `src/ZeyoCut.Core/Editing/EditorDocument.cs:64` and `:123` catch only `ZeyoCutException`; `Refresh` (`:85`) catches nothing.
    - `src/ZeyoCut.Core/Editing/ProjectStore.cs:106` rethrows `IOException` and `UnauthorizedAccessException` after 5 tries. `ProjectLock.cs:44` catches only sharing violations.
    - `src/ZeyoCut.Editor/App.xaml.cs:47-51` shows `Exception.ToString()` and carries on.
- **What goes wrong, case 1:**
    1. Another tool (a backup or antivirus program that doesn't share delete access) holds `project.json` for more than half a second, or the folder is read-only or gone.
    2. Each edit shows a message box with a stack trace.
    3. The project isn't damaged, but the window isn't brought up to date either, because `Rebuild` is skipped.
    - The server turns the same errors into a one-line tool error (`ToolSupport.cs:30`).
- **What goes wrong, case 2:**
    1. Open a `project.json` with an asset whose file is outside `assets\`.
    2. `OpenProject` calls `ShowProject` outside its `try` (`MainWindow.xaml.cs:215-217`). `Rebuild` throws in `Item` (`:591`).
    3. The window is left half open, with `_rebuilding` stuck on `true` (`:564-569`), so clicking an asset does nothing from then on.
    - The server's `open_project` lists the same thing as "project.json problem".
- **Reproduced** for case 2 in Core: `EditorDocument.Open` succeeds, `ProjectValidator` reports the asset, and `ProjectStore.AssetPath` throws.
- **Fix (small):** catch the two IO exception types in `EditorDocument` and put the reason in `Status`. Run `ProjectValidator.Validate` when opening and show the problems. Make `Item` tolerate a bad path.
- **Confidence:** high.

### Finding 9: the once-a-second check does work that grows with the project

- **Severity:** Nice to have.
- **Status:** Deferred.
- **Where:**
    - `src/ZeyoCut.Core/Editing/EditorDocument.cs:141`: `Take` copies the whole project on every `Refresh`, also when nothing changed.
    - `src/ZeyoCut.Editor/MainWindow.xaml.cs:856-886`: the poll then rebuilds the Recent menu, with up to 10 `File.Exists`.
    - `:895`: it also runs `ProjectEditor.PackedLevels`, which is quadratic in the layers. `Seek` runs it too (`:944`), so it runs on every step of a scrub.
- **Measured** (ms, average of 30, warmed up), with caption tracks of three sizes:

    | Captions | `project.json` | Refresh, nothing changed | `PackedLevels` |
    |---|---|---|---|
    | 300 | 266 KB | 1.7 | 0.3 |
    | 1000 | 869 KB | 6.6 | 2.4 |
    | 2000 | 1,734 KB | 12.9 | 9.2 |

- That is fine for ordinary projects, and about 20 ms a second on the UI thread at 2000 layers.
- **Possible:** a recent project on a network path that doesn't answer would make `File.Exists` block the window every second. The Recent submenu is rebuilt while it is open, which may flicker.
- **Fix (small):** copy the project only when `Sync` took something. Work out the pack-lanes state once per `Rebuild`. Build the Recent menu when it opens.
- **Confidence:** high for the numbers.

### Finding 10: saves run on the UI thread

- **Severity:** Nice to have.
- **Status:** Deferred.
- **Where:** `src/ZeyoCut.Editor/MainWindow.xaml.cs:1600-1605`. It includes the wait for `project.lock`, up to 5 s with `Thread.Sleep` (`ProjectLock.cs:52`), the backup copy and the flush to disk.
- **Measured:** 30 to 80 ms for one edit on projects of 0.3 to 1.7 MB, and 95 to 215 ms for a style change to a whole caption track.
- The window freezes for up to 5 s if another program holds the lock that long. I found no ZeyoCut code that does.
- **Fix (medium to large):** save on a worker thread and apply the result on the UI thread.
- **Confidence:** high.

### Finding 11: closing can block the window

- **Severity:** Nice to have.
- **Status:** Deferred.
- **Where:**
    - `src/ZeyoCut.Editor/FramePreview.cs:122-123` waits up to 5 s for the worker, then for the frame being drawn, with no limit.
    - `src/ZeyoCut.Editor/MainWindow.xaml.cs:368` waits 3 s for jobs.
    - `:2190` waits 5 s for an export to stop.
- **What goes wrong:** closing a project while a slow frame is being drawn (footage on a slow drive) hangs the window until the frame is done.
- **Fix (small):** dispose the preview on a worker thread.
- **Confidence:** medium (from reading).

### Finding 12 (Possible): Ctrl+Z in a field can undo a project edit

- **Severity:** Nice to have.
- **Status:** Fixed in `8ae73d9`. Checked in the window: Ctrl+Z and Ctrl+Y pressed in a field never reached the project, because the text box handles both, so the case below doesn't happen. Ctrl+Shift+Z did redo a project step from a field, and no longer does.
- **Where:** `src/ZeyoCut.Editor/MainWindow.xaml.cs:253` lets Ctrl shortcuts through while typing, and `:275` runs the project's undo.
- **What might go wrong:** WPF's text box handles Ctrl+Z only while it has typing to undo. One more press would then undo the last saved edit of the project.
- **Fix (small):** skip the project's undo while a field holds unsaved text.
- **Confidence:** low; not checked in the window.

### Solid in this area

- **Threading:** all drawing goes through one lock in `PreviewFrames`, and frame keys are locked per source. Frames from an earlier project are ignored (`MainWindow.xaml.cs:432`). The session is only used from the UI thread.
- **Leaks:** the watcher, the timers, `CompositionTarget.Rendering`, the event handlers and the preview thread are released when a project closes. Frames are plain byte arrays, with a 512 MB limit.
- **Timeline drawing:** clips off screen are skipped before any text is laid out, and the playhead is drawn on its own layer.
- **A drag in progress:** cancelled when another program moves or removes the clip (`TimelineView.cs:203-211`).
- **Packaging:** the editor leaves out the OpenCV and ONNX native files, and nothing on its drawing or export path uses them.

## 5. Tests

- **Finding 1 got through 565 green tests.** No test runs two writers on one proxy or one sound mix. `ProxyTests` covers one process. **Status:** added with `461a18b` (two proxies, two mixes, two exports to one file).
- **No test for a job that saves after another program changed the project** (scenario d in PLAN.md). The refusals in `TrackEditor.SaveTrack` and `AddDetected` (`src/ZeyoCut.Core/Editing/TrackEditor.cs:36-76`) and the caption-fade check have none. **Status:** open.
- **Nothing in `ZeyoCut.Editor.Tests` exercises `MainWindow`:** saving on Enter and on focus loss, the kept-while-typing path of finding 2, cancelling a drag on an outside change, playback, closing. `EditorAcceptanceTests` drives `EditorDocument`, not the window. **Status:** open. The fixes added tests for the code under the window (`InspectorTests`, `PreviewDragTests`), not for the window itself.
- **Undo:** a refusal is tested. What happens to the steps under a refused one isn't pinned either way (finding 3). **Status:** pinned with `48606f9`.
- **Backups:** `TheEditorsBackupsDontPushOutTheServers` checks the two groups of 20, not that a copy of the other program's last version survives (finding 4). **Status:** added with `68297ce`.
- **Solid:** the tests I read assert outcomes, not just that nothing throws. The hammer test checks all 100 edits and every revision. The operations are compared as JSON with what the tools' editors produce. Pack lanes compares draw orders before and after. I found none that would pass with the code wrong.

## Fix before release

1. Finding 1: unique partial names for proxies (and the sound mix), keep an existing proxy, add the two-writer test. **Fixed** (`461a18b`).
2. Finding 2: check an inspector or drag edit against the version it was built from. **Fixed** (`204187d`).
3. Finding 3: let a refused undo step be skipped, and reword the editor's message. **Fixed** (`48606f9`).
4. Finding 4: keep the backup that holds the other program's last version. **Fixed** (`68297ce`).

## Can wait for 0.2.x

- Finding 5: compare only the caption and cover layers before adding the fades. **Deferred.**
- Finding 8: catch IO errors in the editor's saves; validate on open. **Fixed** (`eadb15e`).
- Finding 6: redraw the paused frame when a matte finishes. **Deferred.**
- Finding 7: a Duration field, and shading past the end on the lanes. **Deferred.**
- Findings 9 to 12: the once-a-second work, saves off the UI thread, closing without blocking, Ctrl+Z in a field. Finding 12 is **fixed** (`8ae73d9`); findings 9, 10 and 11 are **deferred.**
- Tests: a job that saves after an outside change, and tests around `MainWindow`'s save paths. **Open.**
