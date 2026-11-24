# Manual visual editor: plan (Phase 0)

- Branch `feature/manual-editor`, started from `main` at `f968714` (ZeyoCut 0.1.0 plus one fix).
- Written 2026-11-13. Phase 0 only: inspection and this plan. No production code was changed.
- Baseline on the branch: the solution builds, and 498 of 498 tests pass (146 s). The server has 90 tools (91 with `describe_clip`) and 4 prompts.
- Numbers marked *measured* come from a throwaway benchmark (kept outside the repository) that calls the real `FrameRenderer` and `ProjectSession`. It ran on the development laptop (Intel Core Ultra 9 285H, 16 threads) with a 1920x1080, 30 fps H.264 test clip (keyframe every 60 frames).
- File references are `path:line` on this commit.

## Approved decisions and changes (2026-11-13)

The plan below was approved with these answers and changes. Where they disagree with a later section, they win.

**Decisions (section D):**

| # | Answer |
|---|---|
| D1 | WPF |
| D2 | Lanes from type, `zIndex` and caption groups |
| D3 | Selective undo, with change 2 |
| D4 | Operations recorded as commands; undo restores recorded before-values |
| D5 | Yes, with change 4 |
| D6 | Yes for now: the editor in the zip and the `.mcpb`. In Phase 2, also measure a shared-runtime folder layout against two single-file exes and report the size difference |
| D7 | Audio mute uses `visible` |
| D8 | Add both `open_in_editor` and `trim_layer` |
| D9 | Yes. Each move out of `ZeyoCut.Mcp` is its own commit, with no behavior change |
| D10 | Add `exclude_docs: manual-editor/` (done in Phase 1a) |

**Changes:**

1. **Commit on every edit** instead of pending edits and rebase: on mouse-up, on Enter or focus loss, on slider release. Each commit goes through the same lock and hash path. If `project.json` changed, the editor reloads and re-applies its edit on top; if the edit no longer applies, it's dropped and the status bar says why.
    - No Save button. The editor shows "Saved · revision N".
    - Gone: the pending-operations queue, rebase, the conflict review UI and "Save as copy" (section 3 scenario b, section B's `EditorDocument`, Phase 1 step 5, Phase 4's conflict review, cut-list item 6).
    - Autosave and recovery are cut from Phase 6.
    - Editor backups are grouped so a burst of editor commits can't push the MCP backups out of the 20-save window (for example at most one editor backup a minute, or separate limits).
2. **Undo granularity.** `duration` changes (auto-extend, ripple) and layer-order changes caused by adding layers don't count as "touched". Test: split `clip_2`, Claude adds a caption track, undo the split: it succeeds.
3. **`revision` never goes backwards.** Undo, redo, `restore_backup` and an outside write of an older file all save as one more than the highest revision saved. Each has a test.
4. **The reply note** after an outside change lists the changed items (ids and labels, up to 10, then "and N more") and ends with: "Re-read with get_project or list_layers before relying on earlier details."
5. **Phase 1 is split in two.**
    - 1a: format 7 `revision`, `project.lock`, the hash check, `Sync()` before each tool, the reply note, and the two-session, cross-process, hammer and killed-lock tests. It fixes the overwrite bug for MCP alone and can merge to `main` by itself.
    - 1b: operation records, `TrimLayer` and `trim_layer`, change-set undo, `EditorDocument`.
6. **Reads outside the lock share everything.** Any read of `project.json` outside the lock (the editor's watcher, `Sync`'s check) opens with `FileShare.ReadWrite | FileShare.Delete`, so a reader never blocks the other process's rename.
7. **Preview on 4 cores.** Before finishing Phase 3, re-run the preview benchmark with the process limited to 4 cores (CPU affinity). Plan on reduced-resolution preview being needed, not optional.

**What Phase 1a found and did differently:**

- `File.Move` (`MoveFileEx`) refuses to rename over a file another process has open, even when that reader shares delete access (error 5). `ProjectStore.Save` now renames with POSIX semantics (`FILE_RENAME_INFO` with `FILE_RENAME_FLAG_POSIX_SEMANTICS`), which Windows allows while such a reader has the file open. The old rename and its retries remain the fallback, for file systems without POSIX semantics.
- `Sync` doesn't take `project.lock`: it compares size and time, then reads with full sharing and takes the new version. Every save still checks again under the lock, so nothing is lost, and `Sync` never waits behind a slow save in the other process.
- `project.lock` holds the highest revision saved, so a session that never saw the newer revisions still counts on from there after an older file is put back.
- Until 1b brings selective undo, an outside change is an undo barrier in the session that takes it. A version that differs only in how the file is written (same content) is no outside change and keeps the history.
- A `project.json` that isn't a project (bad JSON, missing) is reported once in the next reply, and edits are refused until it's fixed or `restore_backup` replaces it.

**What Phase 1b found and did differently:**

- Phase 1 step 5 is done as change 1 says: `EditorDocument` saves each edit at once (`Apply`, `Undo`, `Redo`, `Refresh`), with no working copy, pending operations or rebase. An edit is dropped, with the reason in `Status`, when it fails or when another program changed a layer it works on after the editor last showed it (`View`); `View` then shows that program's version.
- MCP tools keep calling the Core editors directly, so their behavior is unchanged. The operation records (`EditOperation`) call the same functions, and a test checks that each gives the same project.
- Each undo step keeps the whole project before it, as before, plus a change set. The exact snapshot is used while no other program has saved since the step; the change set only after that. Change sets are worked out from the two versions in memory, and only the changed items are read back the way `project.json` holds them (about 10 ms on a 2.3 MB project).
- The project's length is set back by a selective undo only when no other layer runs past the old end; otherwise it stays.
- Backups are grouped by label: the last 20 `editor ...` saves and the last 20 others.
- The 1a undo barrier is gone. A refusal names the items and the other program's edits, from the backups it kept.

**What Phase 2 found and did differently:**

- The D9 moves are three commits with no behavior change: `Settings` into Core, the video import steps (`VideoImport`) into Render, and starting proxy jobs (`ProxyJobs`) into Render. Moving the export start waits for Phase 4, the first phase that uses it.
- One behavior change came with them, in a commit of its own: `import_asset`, `import_video` and `import_folder` copy files into the project before the save takes `project.lock`. Before, the copy happened under the lock, so a big file made the other program's saves give up after 5 s (R11). A file is copied under a temporary name and renamed when complete.
- Item 3's Save is gone (change 1). The editor takes every outside save, since it never has anything unsaved.
- The mode switch waits for Phase 4: with no Assistant or Export view yet, its buttons would be dead. The bottom region is a time ruler (click or drag to seek, with the markers) until the Phase 3 timeline replaces it.
- Edit → Undo and Redo (Ctrl+Z, Ctrl+Y) are in already, since imports are edits.
- `ZEYOCUT_EDITOR_SETTINGS` names another file for `editor.json`, as `ZEYOCUT_JOBS_DIR` does for the jobs folder. The tests and the screenshot runs use it, so they never touch the real settings.
- The editor's jobs (normalizing, proxies) live in its own in-memory `JobManager`. Normalizing runs one video at a time.
- Packaging (D6), measured with the Release publish:

    | Layout | Release zip | Exes |
    |---|---|---|
    | Server only (before) | 70.5 MiB | `ZeyoCut.Mcp.exe` 174.6 MiB |
    | Two single-file exes (chosen) | 133.8 MiB (+63.3) | plus `ZeyoCut.Editor.exe` 151.0 MiB |
    | One self-contained folder both share | 100.3 MiB (+29.8) | 449 files, 243.1 MiB on disk |

    The shared folder is 33.5 MiB smaller zipped, but it is 449 loose files instead of two exes, in the zip and in the `.mcpb`'s `server` folder. Two single-file exes stay, per D6. The release workflow's size check fails above 200 MiB for the server exe, 170 MiB for the editor exe and 160 MiB for the zip and the bundle.
**What Phase 3 found and did differently:**

- Change 7, measured with the process limited to 4 cores: playback of a 1080p30 H.264 clip with a caption, in frames a second.

    | Scene | Full size | ½ | ¼ |
    |---|---|---|---|
    | Plain | 61.3 | 255.4 | 524.8 |
    | Effects (blur, keyframed brightness, text shadow) | 22.4 | 92.2 | 365.0 |

    Full size keeps up with 30 fps on the plain scene but not with effects, so the render scale went in (`RenderOptions.Scale`; exports stay at 1, with a test). Decoding clips at up to twice the scaled canvas made ½ slower than full size (45.7 fps), so clips now decode at most at the smaller frame's size. Playback draws at ½ by default (Full, ½ and ¼ in the timeline's toolbar, kept in `editor.json`), and a paused frame is always full size. When drawing still falls behind, playback shows every second frame, up to every 4th, and goes back as it catches up. Seek-friendly preview proxies weren't needed for this phase; a seek over uncached footage still takes about 0.2 s.
- The preview page's frame cache and read-ahead moved into Render as `PreviewFrames`, in a commit of its own with no behavior change. The page and the editor run the same code.
- Section A says dropping between lanes "is a reorder that renumbers". `ReorderLayerOperation` moves a layer and its children together (they keep their levels relative to it). Dropped between two lanes, it renumbers the layers above only when the new level is already taken, so the undo step touches as few layers as it can. Dropped onto a lane, it takes that lane's level.
- `TimelineEditor.Limits` says how far a trim and a move can go (a clip's source, children that start before their parent), so a drag stops where the Core operation would refuse instead of failing on release.
- `EditorDocument.Summary` keeps the Core operation's own message ("trimmed 'title': now 0–4.5s (was 0–4s)") for the status bar.
- Snapping also takes markers; section A named clip edges and the playhead.
- The right-click menu has Split, Delete, Hide and Show (Mute and Unmute for sound) through `visible` (D7), and a video's own sound off or on through `muted`.
- The sound is mixed 0.6 s after the last edit that changes it, not after every edit of a drag. The editor deletes only the mixes it made, since the preview page may be playing another.
- The time ruler from Phase 2 is now the timeline's ruler.
- Nothing on the cut list was cut: speeds, drag-and-drop import, the right-click menu and snapping to markers are all in.
- What section A's lane rule gives in practice: a layer added by a tool without a `zIndex` gets the next free one, so clips added one at a time each get a lane of their own, a staircase. Split clips stay in their lane. Dragging each clip into one lane fixes it by hand; a "pack lanes" command, or the server placing a new clip on its neighbor's level when they don't overlap, would fix it for good. I'd decide that before Phase 4.

**What Phase 4 found and did differently:**

- My recommendations from the Phase 3 report were taken as accepted when you said "continue": a Pack lanes command (option b; the server's tools are unchanged), and adding media to the timeline and an editable inspector folded into this phase.
- Pack lanes (`PackLanesOperation`) packs top-level layers lane by lane: the layers sharing a level move together, so text that shares a lane keeps sharing it. Child layers, which have no lanes, settle in between. It can't always reach the fewest lanes, for example when a dropped clip or a child layer is drawn between two layers that could otherwise share a level; then it changes nothing, rather than move layers for no lane saved, and the button is off with a tooltip that says why. The tests check that every frame draws as before.
- Media goes onto the timeline by dragging it onto a lane or with Add at the playhead (`AddClipOperation`, the same layers `add_video_layer`, `add_image_layer` and `add_audio_layer` make). A font is not a clip; the Font file field uses it.
- The inspector's style fields for text came forward from 5b, for every text layer, not only captions: font, font file, size, bold, color, outline, alignment, max width, line height and box. Position and dragging in the preview stay in 5b, and a video's speed stays shown only.
- A caption's style applies to its whole track, as one undo step (`UpdateLayersOperation`, named `update_layer captions_en`), and its text and timing are its own.
- Choice fields (role, alignment, font file, the export presets and languages) are segment buttons, not drop-down lists.
- One window per project uses only a named pipe per project folder, for the current user; there's no lock file to go stale. A second editor asks the first to come to the front; Windows lets it, since the asking editor gives it the right to (`AllowSetForegroundWindow`), and the window also goes on top for a moment, as Windows can otherwise keep it behind.
- `open_in_editor` starts the editor without the server's handles (a test shows that .NET's `Process.Start` passes them on, which would keep the client waiting on the editor after the server ended), outside the client's job object where allowed. The new `EditorPath` setting (`ZEYOCUT_EDITOR_PATH`) names an editor elsewhere.
- The export start (`ExportStart`) moved into Render, as planned for this phase, in a commit of its own.
- The editor side of sync needed no rebase bar or conflict review (change 1). The Assistant view shows the revision, who saved last, and the recent saves from `backups\`.
- The cut list: nothing was cut. The Assistant view, speeds, drag-and-drop, the right-click menus and marker snapping are all in.

**What Phase 5a found and did differently:**

- "Go Phase 5" was read as: build 5a and 5b, commit each, and bring the 5c design before building it.
- The rule for animated values, which the plan left to Phase 5: a field shows the value at the playhead, marked ◆, and a change adds or replaces a keyframe there (`PlacementEditor.Set`, then `KeyframeEditor.SetKeyframes`). The new keyframe takes the easing of the keyframe it replaces, else of the one before it, else linear, so the motion keeps its feel. With the playhead outside the layer the change is refused, rather than add a keyframe where the layer isn't. A value without keyframes changes as `update_layer` changes it. The inspector is built again when the playhead moves while it shows an animated value.
- x and y of a layer on a motion path are not editable (Position: follows a motion path). `update_layer` and `set_keyframes` would take the path off with a warning; the editor shouldn't do that silently.
- Units: scale and opacity are shown in percent, rotation in degrees. Numbers can be typed with `%` and `°`.
- Crop is Left, Top, Right and Bottom in the file's pixels (`CropLayerOperation`, history name `crop_layer`; it has no MCP tool of its own, as `pack_lanes` has none). The model's crop moves the picture, since what's left is drawn from the layer's corner; the editor instead moves the anchor, the mask and the direct child layers by the cut, so what's left stays where it was and still turns around the same point. A crop with `cropX` or `cropY` keyframes is shown, not edited.
- A caption's position moves its whole track by the same amount (`ShiftLayersOperation`, named `update_layer captions_en`), keyframes and motion path included, as its style applies to the whole track. Scale, rotation and opacity aren't offered for captions.

**What Phase 5b found and did differently:**

- The text fields of 5b came forward to Phase 4, and text position came with 5a's Transform fields, so 5b is picking and dragging in the preview.
- Picking uses the boxes the renderer already knows: `LayerOutlines` in Render (the bounds `DebugOverlay` drew, now shared) comes with each frame the preview draws, so the boxes always match the pixels shown. A click takes the topmost box; fully transparent and hidden layers aren't picked. Text is picked by its layout box, not its ink.
- Child layers can be selected from the preview, which the timeline can't do: the timeline highlights the clip that holds the layer, the inspector shows the child, and Delete removes the child with its own children. Split says to select the clip.
- A drag works in the parent's pixels (the outline carries the parent's matrix), so children of turned or scaled layers follow the mouse. Positions are saved to a hundredth of a pixel. A layer scaled to 0 can't be dragged.
- The live redraw applies the edit to a copy of the project for each frame drawn, one at a time, so a slow frame doesn't queue more. Nothing is saved until release, so the history gets one step per drag.

**What Phase 5c found and did differently** (you chose A, the keyframe UI):

- 5a's rule for animated values moved from `PlacementEditor.Set` into `ValueEditor.Set`, which covers every animatable number except a motion path's progress, a crop's position and a mask's box: those keep their own tools and fields, and masks and motion paths stay the model's. An effect's static value is saved as `set_effect` saves it (`SetEffectOperation`, history name `set_effect <id> <kind>`); taking a keyframe away is `RemoveKeyframesOperation` (`remove_keyframes <id>`). Neither adds an MCP tool.
- "The keyframe at the playhead" means the one on the playhead's frame: a keyframe the model put between frames belongs to the frame nearest it (`ValueEditor.KeyAt`). So ◆ can take it away, and a change on that frame moves its value instead of adding a second keyframe beside it. This also applies to 5a's typed values and 5b's drags.
- `remove_keyframes` alone would drop the value back to the old static value. So when the last keyframe goes, its value is made the static one in the same step, and nothing on screen jumps.
- The outline effect is called **Border** in the inspector, since a text's Style already has an Outline (its stroke). A new tint is white, since `set_effect` needs a color for one.
- Rows that don't apply aren't shown: a solid face blur has a fill color and no strength, pixelate and blur the other way round. Its settings are the redaction's and change every clip of that footage; the section's title names the redaction. Captions get no Effects section: their look belongs to their track.
- Most rows now depend on the playhead's time (a diamond is ◆ or ◇), so the inspector is built again once the playhead stops, not on every frame stepped or scrubbed. ◆ pressed while playing stops the playback and keys that frame.
- The timeline marks are the keyframe times of the selected layer, which is a child layer when one was picked in the preview, drawn on the clip that holds it.

**What Phase 6 found and did differently:**

- Autosave and recovery weren't built: change 1 saves every edit as it's made, so there's no unsaved work to recover.
- Polish, from working through the example projects in the editor:
    - Holding a clip drag or a ruler scrub at the left or right edge of the lanes scrolls them (`TimelineScale.EdgeScroll`: up to 24 px every 30 ms, faster the deeper in), and the clip or the playhead keeps following the mouse. Before, a clip could only be dragged as far as the lanes showed.
    - Clicking into an animated value while playing stops the playback, as ◆ already did, so the field shows the value on the frame stopped on, which is where a change is saved.
    - Two face blurs on one footage give two sections whose rows share labels. The style buttons of one switched the other's (WPF groups radio buttons by name), and saving a field put the focus in the first field with that label. Now each field's buttons are a group of their own, and the focus goes back to the same row.
    - A sound shorter than a second showed 0:00 in the media list; it shows tenths now (0:00.6).
    - `open_in_editor`'s description named only what the editor did in Phase 4. It now says what it does and what stays with the tools; its name, parameters and behavior are unchanged, and `docs/TOOLS.md` and `docs/GUIDE.md` follow.
- Docs: `docs/editor.md` is a page on the website, with screenshots of the examples (no paths with a user name in them). The README, the home page, install and the quick start link it, and "early" is gone from install and the CHANGELOG.
- The link to `undo` / `redo` in the guide's `open_in_editor` section (Phase 4) used GitHub's anchor, `#undo--redo`, which the website doesn't make; it's `#undo-redo` now.

## Summary

What I found:

- **One project file and one renderer already.** `project.json` is read and written only through `ProjectStore` and `ProjectSession` (Core). `FrameRenderer` (Render) draws `render_frame`, the preview page and every export.
- **Every MCP edit already goes through Core.** Each changing tool calls a Core editor (`ProjectEditor`, `TimelineEditor`, `CaptionBuilder`, …) inside `ProjectSession.Mutate`. That one method validates, backs up, saves atomically and records undo. So "route MCP through the shared layer" mostly means making `ProjectSession` safe for two processes. Tool code barely changes.
- **The MCP server never re-reads `project.json` after `open_project`.** If another program saves the file, Claude's next edit silently overwrites it. Fixing this is the heart of Phase 1.
- **The model has no timeline tracks.** It has layers with `start`, `end`, `zIndex` and `parentId`. See *Where the spec and the code disagree*.
- **Undo already lives in Core, but it is snapshot-based.** It keeps the whole JSON from before each change, in memory, separately in each process.

What I recommend:

- **WPF** for the editor. It needs no new NuGet package. Avalonia would bring a second, older SkiaSharp into the same process as the renderer.
- **Sync:** a revision number in `project.json`, a SHA-256 check of the file, and a short exclusive lock while saving. This is built once in `ProjectSession` and used by both processes.
- **Timeline lanes worked out from the layers** (type, `zIndex`, caption groups). Reorder changes `zIndex`.
- **Undo restores recorded before-values.** Across a change made by the other process, it only undoes items nobody else has touched since.
- **A separate `ZeyoCut.Editor.exe`** next to `ZeyoCut.Mcp.exe`. Measured cost: about 63 MB more in the release zip.

Section D lists the 10 decisions I need from you before Phase 1.

## Where the spec and the code disagree

1. **Tracks.** The spec asks for "tracks as they exist in the model (video, audio, captions, overlays)". The model has none:
    - A layer has `start`, `end`, `zIndex`, `parentId` and `visible` (`src/ZeyoCut.Core/Model/Layer.cs`). `Project.tracks` are motion-tracking boxes for `track_object` (`src/ZeyoCut.Core/Model/Track.cs`).
    - Captions are text layers under a *null layer*, an image layer with no drawings, called `captions` or `captions_<language>` (`src/ZeyoCut.Core/Editing/CaptionBuilder.cs:54`, `CaptionTracks.cs:16`).
    - Overlays are ordinary image and text layers.
    - Effects are fields on a layer, plus redaction and cutout layers that follow other clips.
    - Transitions are baked into the keyframes and fades of the two clips (`TimelineEditor.cs:286`).
    - Draw order is `zIndex`, then position in the `layers` array (`src/ZeyoCut.Core/Animation/SceneEvaluator.cs:60`).

    Proposal: lanes worked out from the layers, with reorder changing `zIndex` (section A).
2. **"Command-based undo in Core".** Undo already lives in Core but restores snapshots (`src/ZeyoCut.Core/Editing/ProjectSession.cs:100-183`). It keeps the JSON from before each change, up to 200 steps, in memory, per process. Proposal: section 7.
3. **"If MCP tools change the model directly, move that logic into Core."** They don't, with two small exceptions:
    - `detect_and_track` and `track_object`/`reanchor_track` add the `Track` record inside the tool (`src/ZeyoCut.Mcp/Tools/DetectTools.cs:204`, `TrackTools.cs:340`). These move into `TrackEditor`.
    - Some orchestration the editor also needs lives in `ZeyoCut.Mcp`: importing (probe plus a normalize job, in `VideoTools`/`AssetTools`), starting an export (`ExportTools.cs:26`), proxies (`EditorState.StartProxies`) and settings (`Settings.cs:58`). These move into Core or Render with no behavior change.
4. **Trim.** There is no trim operation. Today a trim is either `update_layer` setting `start`, `trimIn`, `trimOut` or `end`, or `remove_range`. `update_layer` leaves keyframes where they were, because keyframe times are absolute (`TimelineEditor.cs:47`). Proposal: a new Core `TrimLayer` operation that cuts the way `split_layer` and `remove_range` do (keyframes and children stay in step) and can extend within the source. It can optionally become a new MCP tool, `trim_layer` (D8).
5. **Muting an audio layer.** Only video layers have `muted`. `visible: false` is what takes an audio layer out of the mix (`src/ZeyoCut.Render/Export/FfmpegArgs.cs:53`). Proposal: the Mute toggle on an audio layer sets `visible` (D7).
6. **"Effect duration".** There is no such field. An effect lasts as long as its layer. A time-limited effect is done with keyframes (for example blur 0 → 8 → 0), and a redaction covers its tracks' frames. This is marked *Needs engine work* in section 6.
7. **"No UI-only properties."** Sync needs one new property, `revision`. It is saved and loaded, shows in `get_project`, and is managed by ZeyoCut, never set by a tool. It isn't drawn (it's metadata). Editor view state stays out of `project.json`: playhead, zoom, panel sizes and recent projects go in the editor's own settings file.

## 1. Project format

### Where it is stored, how it is loaded and saved

- **Folder.** One folder per project (`docs/project-format.md`, "The folder"):
    - `project.json`, the model.
    - `assets\`: imported files, plus analysis results next to each analyzed file.
    - `renders\` and `exports\`: output.
    - `tracks\`: one file per track version.
    - `backups\`: the last 20 saves, named `project-<time> before <edit>.json`.
    - `cache\chunks\` and `cache\preview\`: export chunks and the preview sound mix.
    - `proxies\`.

    MCP only uses project folders inside the projects folder (`ProjectsRoot`, default `%USERPROFILE%\ZeyoCut\projects`; `src/ZeyoCut.Mcp/AllowedFolders.cs`).
- **Load.** `ProjectStore.Load` (`src/ZeyoCut.Core/Editing/ProjectStore.cs:27`) goes through three steps:
    1. `ProjectMigrations.Migrate` brings formats 1–5 up to 6. Every step so far is a no-op, because each format only added fields.
    2. `ProjectJson.Deserialize` reads camelCase names, case-insensitively, and allows comments and trailing commas (`src/ZeyoCut.Core/Serialization/ProjectJson.cs:19`).
    3. `Normalize` fills in defaults (`ProjectJson.cs:74`).

    Unknown fields are ignored on read and dropped on the next save. A newer `formatVersion` is refused (`ProjectValidator.cs:190`).
- **Save.** `ProjectStore.Save` (`ProjectStore.cs:38`) writes UTF-8 without BOM to `project.json.tmp`, flushes it to disk, and renames it over `project.json`. It tries 5 times, in case antivirus holds the file. Before that, `ProjectSession.Save` (`ProjectSession.cs:178`) copies the old file into `backups\` (`ProjectBackups.Add`).
- **Edit.** `ProjectSession.Mutate` (`ProjectSession.cs:100`) works on a copy:
    1. Serializes the project and deserializes a working copy.
    2. Runs the change on the copy, then validates the whole project (`ProjectValidator.ThrowIfInvalid`). If anything is invalid, nothing changes.
    3. Backs up, saves, keeps the copy, and pushes the old JSON onto the undo stack.

### Element types (`src/ZeyoCut.Core/Model`)

| Element | JSON `type` | Properties |
|---|---|---|
| Project | — | `formatVersion` (6), `name`, `width`, `height`, `fps`, `duration`, `backgroundColor`, `audio` {`loudnessTarget`, `truePeak`, `ducking`}, `camera`?, `assets[]`, `layers[]`, `tracks[]`, `markers[]` (`Project.cs`) |
| Every layer | — | `id`, `name`, `rigRole`?, `start`, `end`? (null = to the project end), `zIndex`, `parentId`?, `visible`, `language`? (`Layer.cs`) |
| Visual layer (image, text, video) | — | `anchorX`, `anchorY`, `ignoreCamera`, and animated `x`, `y`, `rotation`, `scaleX`, `scaleY`, `opacity`. Optional `motionPath`, `mask`, `tint`, `outline`, `shadow`, `blur` (`Layer.cs`, `LayerEffects.cs`) |
| Video | `video` | `asset`, `trimIn`, `trimOut`, `speed` (0.1–10), `volume` (0–10), `muted`, `role` (voice by default), `fadeIn`, `fadeOut`, `cropWidth`?, `cropHeight`?; animated `brightness`, `contrast`, `saturation`, `cropX`, `cropY`; `matte`?. No `end`: its length is (`trimOut` − `trimIn`) / `speed` (`VideoLayer.cs`) |
| Audio | `audio` | `asset`, `trimIn`, `trimOut`? (null = to the end of the file), `volume` (0–10), `role` (music by default), `fadeIn`, `fadeOut`. No `muted` (`AudioLayer.cs`) |
| Text (titles, captions) | `text` | `text`, `fontFamily`, `fontAsset`?, `bold`, `fontSize`, `color`, `strokeColor`?, `strokeWidth`, `align`, `maxWidth`?, `lineHeight`, `box`? {`color`, `padding`, `cornerRadius`} (`TextLayer.cs`) |
| Image (stills, rig parts, overlays; with no drawings = a group or "null layer") | `image` | `flipX`, `drawings` {name → asset id}, `drawing` (hold keyframes), animated `brightness`, `contrast`, `saturation`, `matte`? (`ImageLayer.cs`) |
| Caption | — (text layers) | Text layers under a null layer `captions` or `captions_<language>`, with `ignoreCamera` on the group (`CaptionBuilder.cs:226`) |
| Overlay | — (image or text layers) | Any image or text layer drawn above the footage |
| Effect | — (fields, plus 2 layer types) | Per layer: `shadow`, `outline`, `blur`, `tint`, `mask`, `motionPath`, color adjustment, `matte`. `redaction` layer: `tracks[]`, `style`, `strength`, `paddingPercent`, `feather`, `holdFrames`, `color`, `boxes`? (`RedactionLayer.cs`). `cutout` layer (text behind subject): `sources[]`, `captionGroup`?, `opacity`, `matte` (`CutoutLayer.cs`). Neither has its own timing; both follow their source clips |
| Animated value | — | {`value`, `keyframes[]` {`time`, `value`, `easing`}}. Keyframe times are absolute timeline seconds (`AnimatedValue.cs`, `Keyframe.cs`) |
| Asset | — | `id`, `type` (image, audio, font, video), `file` (always `assets/<name>`), and probe results (`width`, `height`, `duration`, `frameRate`, `frameCount`, `hasAudio`, `codec`, `originalFile`, `normalized`) (`Asset.cs`) |
| Track (motion tracking) | — | `id`, `asset`, `layer`, `method`, `direction`, `firstFrame`, `lastFrame`, `origin`, `file` (`tracks/…`), `anchors[]` (`Track.cs`) |
| Marker | — | `id`, `time`, `name`, `kind` (chapter or scene) (`Marker.cs`) |

## 2. MCP tools

**Memory between calls.** The server keeps one `ProjectSession` in memory (`src/ZeyoCut.Mcp/EditorState.cs:227`):

- Reads (`Snapshot`, `Read`) come from memory.
- Every change is written to disk right away.
- The disk is never read again after `open_project` or `create_project`.
- Undo history is in memory and is lost when the server restarts.
- Background jobs keep the session they started with and commit through `Mutate` when they finish. These are import normalization, `track_object`, `detect_and_track`, AI mattes and the `generate_captions` occlusion pass.

Tools that change `project.json` (all through `ProjectSession.Mutate`/`Replace`, `Undo`/`Redo` or `Create`/`Open`):

| Tool | Writes | Core code |
|---|---|---|
| `create_project`, `new_project_from_template`, `build_from_script` | A new `project.json` (canvas, audio settings, and for templates and scripts also assets, layers, markers, captions), then open it | `ProjectSession.Create`, `Presets`, `Templates`, `ScriptBuilder` |
| `open_project` | Nothing, unless the file is an older format: then it saves the migrated file. Starts proxy jobs (files only) | `ProjectSession.Open`, `ProjectBackups` |
| `update_project` | `name`, `width`, `height`, `fps`, `duration`, `backgroundColor`, `audio.loudnessTarget`, `audio.ducking` (and preset) | `ProjectEditor.UpdateProject` |
| `undo`, `redo`, `restore_backup` | The whole project (a snapshot or a backup) | `ProjectSession.Undo`/`Redo`/`Replace`, `ProjectBackups` |
| `import_asset`, `import_folder`, `import_video` | `assets[]` (copies the file into `assets\`; a normalize job updates the asset later) | `AssetImporter`, `FolderImport` |
| `add_image_layer`, `add_text_layer`, `add_audio_layer`, `add_video_layer` | `layers[]` (+ `duration` when the layer runs past the end) | `ProjectEditor.Add*Layer`, `ExtendToFit` |
| `update_layer` | Any field of one layer (generic, validated) | `ProjectEditor.UpdateLayer` (`ProjectEditor.cs:429`) |
| `remove_layer`, `duplicate_layer` | `layers[]` (with the subtree) | `ProjectEditor.RemoveLayer`, `DuplicateLayer` |
| `move_layer` | `start` of the layer and its children, their keyframe times, `duration` | `TimelineEditor.MoveLayer` (`TimelineEditor.cs:176`) |
| `split_layer` | Adds the right-hand piece; trims, `end` and keyframes of both pieces and their children | `TimelineEditor.SplitLayer` (`:70`) |
| `remove_range`, `remove_silences`, `cut_by_transcript` | Cuts layers, removes or shifts them with ripple, `duration`, camera keyframes, markers | `TimelineEditor.RemoveRange` (`:93`), `SilenceCutter` |
| `set_speed` | `speed`, trims and keyframes, later layers (ripple), `duration`, camera, markers | `TimelineEditor.SetSpeed` (`:207`) |
| `add_transition` | Overlap, trims and ripple of two clips; opacity, mask, position, scale and blur keyframes; fades | `TimelineEditor.AddTransition` (`:293`) |
| `set_keyframes`, `remove_keyframes`, `clear_keyframes` | Keyframes of any animated value (layers, camera, drawings, redaction boxes) | `KeyframeEditor` |
| `set_effect`, `set_mask`, `set_motion_path` | `shadow`, `outline`, `blur`, `tint`; `mask`; `motionPath` | `EffectEditor` |
| `set_camera`, `camera_shake` | `camera` | `CameraEditor` |
| `generate_captions`, `add_caption_track`, `import_srt` | Caption group + text layers (+ a cutout's fades with `behindSubject`) | `CaptionBuilder`, `CaptionTracks` |
| `apply_style` | Text style fields of one text layer or a group's text layers | `TextStyles` |
| `add_sfx` | `assets[]` + an audio layer with role effects | `SoundEffects` |
| `auto_lip_flap` | Drawing keyframes of a mouth layer | `LipFlap` |
| `add_rig`, `apply_action` | Layers + assets of a rig; keyframes of rig parts | `Rigs`, `RigActions` |
| `reframe_to_vertical` | Crop, position and scale of video layers | `Reframe` |
| `remove_background`, `refine_matte` | `matte` (+ matte cache files, in a job) | `MatteEditor` |
| `text_behind_subject` | A cutout layer (+ text `zIndex`) | `SubjectEditor` |
| `track_object`, `reanchor_track`, `detect_and_track` | `tracks[]` (+ files in `tracks\`, in a job) | `TrackEditor`, `DetectionLinker` (the `Tracks.Add` itself is in the tool; see disagreement 3) |
| `attach_to_track` | `x`, `y` (+ `scaleX`, `scaleY`) keyframes of a layer | `AttachEditor` |
| `apply_redaction`, `tracks_to_keyframes` | A redaction layer; its `boxes` | `RedactionEditor` |
| `add_markers`, `remove_markers` | `markers[]` | `MarkerEditor` |

Tools that only read the model. They write files only in `renders\`, `exports\`, `cache\`, beside assets, or in the library:

| Tool | Reads | Produces |
|---|---|---|
| `get_project`, `list_layers`, `list_assets`, `list_projects` | The whole project (`list_projects`: each project's `project.json`) | Text |
| `render_frame`, `render_contact_sheet`, `view_source`, `preview_server` | The whole project / one asset | PNGs in `renders\`, the preview page |
| `check_project`, `frame_diff`, `audio_levels`, `text_occlusion`, `redaction_report` | The whole project | Reports (some as jobs) |
| `start_export`, `export_batch`, `export_language_versions`, `export_thumbnail`, `export_frames`, `prerender_layers`, `get_export_status`, `report_render_stats` | A snapshot taken at the start | Files in `exports\`, chunks in `cache\chunks\` |
| `export_otio`, `export_fcpxml`, `export_srt`, `export_markers` | Layers, markers, captions | Interchange files in `exports\` |
| `analyze_video`, `get_transcript`, `detect_beats`, `describe_clip` | One asset | Analysis files beside the asset |
| `save_rig`, `save_action`, `save_style`, `save_template`, `list_library`, `list_sfx` | Layers or nothing | Library items |
| `get_job_status`, `get_logs`, `system_check` | Nothing in the project | Job, log and system information |

## 3. Sync between the MCP server and the editor

### Mechanism

The mechanism is built once, in `ProjectSession`, and both processes run the same code.

1. **Revision.** `project.json` gets a top-level `revision` (format 7; a file without one reads as 0). Every save adds 1, whichever process saves. It is what the editor and Claude show ("revision 42").
2. **Content check.** Each session remembers the SHA-256 of the `project.json` bytes it last read or wrote (*measured*: 0.4 ms for a 1.3 MB file). The hash, not the revision, decides whether the file changed. It catches every writer, including a hand edit, an older ZeyoCut or a restored copy.
3. **Lock while saving.** A `project.lock` file next to `project.json` is opened exclusively (`FileShare.None`) only while a process checks and writes. *Measured:* a whole edit takes 11 ms for an 86 KB project and 37 ms for a 1.3 MB one.
    - A process that finds it locked retries for up to 5 s, then fails with a clear message and changes nothing.
    - If a process dies, Windows closes its handle and the lock is free. A leftover lock file means nothing, so no lock can go stale.
4. **Commit, the same in both processes:**
    1. Take the lock.
    2. Read `project.json` and hash it.
    3. If the hash differs from the remembered one, load the file from disk as the new starting point and note it in history as an outside change.
    4. Apply the change on top: for MCP, the tool's change function; for the editor, its pending edits (case b).
    5. Validate, back up, write (temp, flush, rename) and add 1 to `revision`.
    6. Remember the new hash and release the lock.
5. **Noticing changes.**
    - The MCP server runs `Session.Sync()` at the start of every tool call, from the call filter that already logs each call (`src/ZeyoCut.Mcp/Program.cs:39`). It compares the file's size and time, hashes only if they changed, and reloads under the lock.
    - The editor watches `project.json` with a `FileSystemWatcher` (debounced 250 ms), with a poll every 2 s as a fallback for OneDrive or network folders.
6. **Telling Claude.** When `Sync` reloads, the tool reply gets one first line, for example: `project.json changed outside this server since your last call (now revision 43; last change: editor trim clip_2)`. Tool names, parameters and everything else stay as they are (D5).
7. **Labels.** Edits saved by the editor are labeled `editor <what>` (for example `editor trim clip_2`). Backups and the history list then show who did what.

### The four scenarios

**(a) Claude edits while the editor is open with no unsaved changes.**

1. The server syncs (no change), commits revision 42 and replies.
2. The editor's watcher sees a new hash and reloads.
3. The playhead, zoom and selection are kept for ids that still exist. History shows `Claude: add_caption_track ja (revision 42)`. Only frames whose content key changed are rendered again.

The editor had nothing unsaved, so nothing can be lost.

**(b) Claude edits while the editor has unsaved changes.** The editor holds pending edits P1…Pn, made on revision 41.

1. It loads revision 42 next to its working copy and *rebases*: it replays P1…Pn on top of 42. Ids the edits created are mapped to the new ones, such as the right-hand piece of a split.
2. An edit conflicts if it fails validation, or if it touches an item that changed between 41 and 42. An item is a layer, asset, track or marker by id, the project settings, or the camera.
3. With no conflicts, the working copy becomes the rebased one and stays unsaved. A bar says: "Claude changed the project (add_caption_track ja). Your 3 unsaved edits are kept on top."
4. With conflicts, the working copy stays exactly as you had it. A bar says, for example: "Claude also changed clip_2, which you edited. Review."
    - For each conflicting item you choose: keep mine or keep Claude's.
    - Until you choose, Save asks the same question. It never overwrites silently.
    - "Save as copy" puts your version in a new project folder.

**(c) Both save at nearly the same moment.**

- The lock puts the two saves in order. The second one finds the hash changed and goes through step 4.3 of the commit:
    - The server loads the new file and applies Claude's change on top.
    - The editor rebases, as in (b).
- Both versions also stay in `backups\`.
- Writes are temp plus rename, so no reader ever sees half a file.

**(d) The MCP server holds an old copy in memory, and the editor has saved since.**

- The next tool call's `Sync` reloads before the tool runs, and the reply says so.
- A job that finishes later commits through the same lock and hash check, so its change lands on top of the editor's. Such jobs include normalization, tracking, detection and AI mattes. Its change function then runs on the editor's version, and the existing checks inside it still apply. For example, the tracking tools already refuse when a track "appeared while this one was being tracked" (`TrackTools.cs:337`).
- MCP `undo` after an outside change: see section 7.

### Other cases

- **Crash.** A crash mid-write leaves the old file or the new one (temp plus rename). A crash while holding the lock frees it.
- **Two editors on one project.** The editor runs one window per project (a named mutex plus a named pipe). Opening the project again brings that window to the front.
- **An older ZeyoCut server (0.1.0) on the same project.** It doesn't know the lock or the revision and never reloads, so it can overwrite an editor save. The editor still notices its writes. The old exe can't be fixed, so the docs will say: update the server together with the editor.
- **Other files.** `tracks\`, matte caches, analysis files, `proxies\` and `cache\` are named by content or by their inputs. Both processes only add files there, so nothing changes.

## 4. UI framework

| | **WPF** | **Avalonia 12.1.6** | **WinUI 3 (Windows App SDK 2.6.5)** |
|---|---|---|---|
| License | MIT (`dotnet/wpf`). It is part of .NET's Windows Desktop runtime, so there's no NuGet package to add | MIT; about 10 NuGet packages to add to the notices | The NuGet package's license is a Microsoft license file that requires acceptance, not MIT. It also pulls in AI, ML, Widgets and Search packages |
| Works with `ZeyoCut.Render` in one process | Yes. Frames are copied into a `WriteableBitmap` | **Conflict.** `Avalonia.Skia` 12.1.6 depends on SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.3, while ZeyoCut uses SkiaSharp 4.154.0 and HarfBuzzSharp 14.2.1.400. One process loads one version: either Avalonia runs on a major version it wasn't built for, or the renderer goes back two major versions | Yes, with Win2D or SkiaSharp views |
| Weight (*measured*: a single-file, self-contained WPF exe that references `ZeyoCut.Render`) | 151 MB exe, 63 MB zipped, without OpenCV and ONNX Runtime (the editor needs neither). 69 MB on disk with single-file compression. With them, 260 MB / 106 MB zipped | Not measured; the Avalonia assemblies on top | Windows App SDK runtime on top |
| Custom-drawn timeline | `OnRender`/`DrawingVisual`, retained and composited on the GPU | Custom render on Skia | Win2D or Composition |
| Showing rendered frames | `WriteableBitmap`. WPF scales it on the GPU, which saves the 12 ms Skia downscale measured in section 5 | `WriteableBitmap`, or Skia directly (version conflict above) | `SoftwareBitmap` or Win2D |
| Packaging next to the MCP server | Only the editor project targets `net10.0-windows`; Core, Render and Mcp stay `net10.0`. Single-file publish is supported | Single-file publish is supported | Unpackaged self-contained apps exist; I know of no single-file publish for WinUI 3 and didn't test it |
| Dark theme | Our own styles (no theme library) | Fluent dark built in | Built in |

**Recommendation: WPF.** It adds no third-party dependency, the renderer runs in-process with no version conflict, and the frame path is cheap. ZeyoCut is Windows-only anyway (DirectML, Windows paths).

The cost is styling work for a professional dark look. Panels are a fixed five-region layout with splitters: resizable, not floating (no docking library). Audio playback uses WPF's own `MediaPlayer` (Windows Media Foundation), so no audio library is needed either.

## 5. Preview

### A composited frame at time T

1. `FrameMath.NearestFrame(project, t)` gives the frame number.
2. `FrameRenderer.RenderImage(project, frame, new RenderOptions(Proxies: true))` (`src/ZeyoCut.Render/FrameRenderer.cs:219`) returns an `SKImage` at canvas size. It includes every layer, trim, caption, effect, matte, redaction and the camera.

This is the same drawing path the exporter uses for every frame, and the one `render_frame` uses (`src/ZeyoCut.Render/Preview.cs:34`). For a language version, `Languages.Choose(project, language, export: false)` runs first, as `render_frame` does.

`new FrameKeys(project, directory).Of(frame)` is a per-frame content key. Cached frames survive edits that don't touch them, which is how the preview page and the chunk cache already work.

The editor renders its working copy, unsaved edits included.

### Latency (*measured*, 1080p30)

| What | Time |
|---|---|
| First frame after opening (fonts, decoder start) | 150–250 ms |
| Jump to a random frame (video + caption), full-size frame | median 182 ms, p90 193 ms. Almost all of it is starting an ffmpeg seek for the clip (`src/ZeyoCut.Render/Video/VideoFrameSource.cs:9`) |
| The same with blur, keyframed brightness and a text shadow | median 175 ms |
| Next frame during playback (forward decoders), plain scene | median 16 ms (58 fps on one thread) |
| Next frame during playback, effects scene | median 42 ms (23 fps on one thread) |
| Scale 1920 → 960 and copy the pixels | 11–13 ms |

### Playback strategy

- **Scrubbing and stepping.** One render worker, latest request wins. While the playhead is dragged, only the newest frame renders and the last finished one stays on screen. Cached frames show at once. Over uncached 1080p footage, expect about 5 updates a second while dragging; a single click or frame step takes about 0.2 s.
- **Playback.** The streaming renderer (forward-reading decoders) renders ahead on 1–2 threads into the frame cache. This is the preview page's approach (`src/ZeyoCut.Mcp/PreviewServer.cs:586`): 5 s ahead, keyed by `FrameKeys`. Frames show at the project's fps when rendering keeps up; otherwise every second frame shows (15 fps for 30 fps projects), as the preview page does. The audio is never slowed down.
- **Display.** The full-size BGRA frame is copied into a `WriteableBitmap` and WPF scales it to the panel on the GPU. Zoom/fit and the aspect ratio come from the canvas size.
- **Proxies.** The existing 1920 px proxies of footage over 2560 px are used (`Proxies: true`). Exports always read the originals.
- **Reduced resolution.** *Optional engine work*, only if Phase 3 shows the need: a render scale in `RenderOptions` that draws smaller and decodes video smaller. Exports keep scale 1, and a test checks that.
- **Seek-friendly preview proxies.** Low resolution with frequent keyframes. Also *optional engine work*, for the same reason.
- **Audio.** `PreviewAudio.Make` (`src/ZeyoCut.Render/Export/PreviewAudio.cs:44`) mixes the sound exactly as the export does (fades, ducking, loudness) into `cache\preview\mix-<key>.m4a`. The editor plays that file with `MediaPlayer`, and the video follows the audio clock. After an edit that changes the sound, the key changes and the mix is made again in the background. Until it's ready, playback is silent and shows "updating sound". Playback speeds other than 1× use `MediaPlayer.SpeedRatio`.
- **Reuse, not a copy.** The preview page's frame cache and read-ahead live in `ZeyoCut.Mcp`. I'd move them into `ZeyoCut.Render`, so the page and the editor run the same code and the page's behavior doesn't change.

## 6. Inspector and timeline gap analysis

Status *Ready* means the model, the renderer and MCP already support it, and only editor UI is missing.

### MVP

| Property / action | In the model | Rendered or exported | Via MCP | Status |
|---|---|---|---|---|
| Clip start | `start` | Yes | `move_layer`, `update_layer` | Ready |
| Clip end / duration: text, image | `end` (null = project end) | Yes | `update_layer`, `split_layer`, `remove_range` | Ready |
| Clip end / duration: video | Length = (`trimOut` − `trimIn`) / `speed` | Yes | `update_layer` (trims), `set_speed`, `remove_range` | Ready |
| Clip end / duration: audio | `trimIn`, `trimOut` (null = to the end of the file) | Yes | `update_layer` | Ready |
| Trim either edge with keyframes and children kept in step | Cut logic in `TimelineEditor` | Yes | `remove_range` (no trim tool) | **Needs Core work**: `TrimLayer` operation (disagreement 4) |
| Move, split, delete | — | Yes | `move_layer`, `split_layer`, `remove_layer` | Ready |
| Reorder | `zIndex` (+ array order for ties) | Yes | `update_layer` `zIndex` | Ready (renumbering several layers in one step: one Core operation) |
| Volume (audio and video layers) | `volume` 0–10, not animated | Yes (`FfmpegArgs.cs:358`) | `update_layer` | Ready |
| Mute: video | `muted` | Yes (`FfmpegArgs.cs:82`) | `update_layer` | Ready |
| Mute: audio layer | No field; `visible: false` leaves it out of the mix | Yes (`FfmpegArgs.cs:53`) | `update_layer` `visible` | Ready through `visible` (D7) |
| Fade in / out | `fadeIn`, `fadeOut` | Yes (`afade`, `FfmpegArgs.cs:360`) | `update_layer` | Ready |
| Caption text | `text` | Yes | `update_layer` | Ready |
| Caption timing | `start`, `end` | Yes | `update_layer`, `move_layer` | Ready |
| Caption style: font family or font asset, size, bold, color, stroke color and width, alignment, max width, line height, background box (color, padding, corner radius) | `TextLayer` fields | Yes (HarfBuzz layout) | `update_layer`, `apply_style` | Ready |
| Media library: list, import | `assets[]` | — | `list_assets`, `import_asset`, `import_video`, `import_folder` | Ready (the import orchestration moves out of `ZeyoCut.Mcp`) |
| Export | — | `Exporter`, `ExportJobManager` | `start_export` | Ready (export start moves out of `ZeyoCut.Mcp`) |
| Snapping, lanes, zoom, scrubbing, timecode | Not project data | — | — | Editor UI only (view logic, not editing logic) |

### Later

| Property | In the model | Rendered | Via MCP | Status |
|---|---|---|---|---|
| Position, scale, rotation, opacity | `x`, `y`, `scaleX`, `scaleY`, `rotation`, `opacity` (animated) | Yes | `update_layer`, `set_keyframes` | Ready. Editing a property that has keyframes sets the keyframe at the playhead (the `set_keyframes` logic); designed in Phase 5 |
| Crop (video) | `cropX`, `cropY` (animated), `cropWidth`, `cropHeight` | Yes | `update_layer`, `reframe_to_vertical` | Ready. Images have masks, not crop |
| Text font family, size, color, background, alignment | `TextLayer` | Yes | `update_layer`, `apply_style` | Ready |
| Text position, incl. dragging in the preview | `x`, `y`; bounds from `SceneEvaluator` + text layout (the debug overlay already draws them) | Yes | `update_layer`, `set_keyframes` | Ready. Hit-testing reuses the debug overlay's bounds code |
| Blur strength | `blur` {`x`, `y`} animated | Yes | `set_effect` | Ready |
| Face blur settings | Redaction `style`, `strength`, `paddingPercent`, `feather`, `holdFrames`, `color` | Yes | `update_layer`, `apply_redaction` | Ready |
| Basic adjustments | `brightness`, `contrast`, `saturation` (video, image) | Yes | `update_layer`, `set_keyframes` | Ready |
| Shadow, outline, tint | `shadow`, `outline`, `tint` | Yes | `set_effect` | Ready (not in the spec list; available) |
| Effect duration | No field (disagreement 6) | — | — | **Needs engine work**, or a keyframe UI |
| Autosave with crash recovery | Backups exist; unsaved editor work does not | — | — | **Cut** (change 1: every edit is saved as it's made) |
| Reduced-resolution preview render | No | — | — | Optional **engine work** (section 5) |

## 7. Undo and redo

**Today.** Each process has its own undo stack of whole-project snapshots: up to 200, in memory (`ProjectSession.cs:121-154`). Undo saves the snapshot back as a new save, with a backup first.

**Proposal.**

- **Operations are commands.** Every edit, from MCP or the editor, is a named operation with its parameters (`TrimLayer clip_2 end 4.2`), the ids it created, and its label. The editor needs this to replay unsaved edits on top of Claude's (3b) and for its history list. The MCP side keeps its labels as today.
- **Undo restores recorded values, not computed inverses.**
    - Each step also stores a *change set*: before and after of every item it touched. Items are layers, assets, tracks and markers by id, the project settings, the camera, and the layer order. The change set is worked out by comparing the project before and after.
    - Inverse operations for split, trim, ripple and transitions would be fragile: keyframe cuts, children, cutouts that follow their sources.
    - Restoring recorded values is exact, and it's what the tested undo already does.
- **Within one process, nothing changes.** If no outside change came after a step, undo restores the step's before state exactly, as today. A test checks that MCP `undo`/`redo` give byte-identical results to the old code.
- **Across an outside change, undo is selective.**
    - Undo applies the step's change set backwards, but only if every item it touched is still as the step left it.
    - Otherwise it refuses and names the items, for example: "can't undo 'trim clip_2': Claude changed clip_2 after it (add_transition clip_2 clip_3). History or restore_backup can go back to an earlier version."
    - Redo works the same way.
    - Outside changes appear in history as entries that can't be undone ("Claude: add_caption_track ja").
- **Saving doesn't clear the editor's history.** Undo works on unsaved and saved edits alike.

**The simpler alternative.** An outside change is a hard barrier: the other process's undo stack is cleared. It's safe, but in the acceptance test (save your edits, Claude adds a caption, then Ctrl+Z) your edits from before Claude's change could no longer be undone. That's why I recommend selective undo (D3).

## 8. Opening projects and packaging

**Opening.**

- **Ways to open:**
    - File → Open: pick a project folder or its `project.json`.
    - Recent projects: stored in `%LOCALAPPDATA%\ZeyoCut\editor.json`, never in the project.
    - A list of the projects in `ProjectsRoot`, like `list_projects`.
    - The command line: `ZeyoCut.Editor.exe <project folder>`.
- **Settings.** The editor reads the same settings as the server: `appsettings.json` next to the exe plus the same environment variables. `Settings` moves from `ZeyoCut.Mcp` into Core; it only uses System.Text.Json.
- **The editor imports any file you pick.** The import-folder limits protect against a model reading arbitrary files. Here you choose the file yourself.
- **New MCP tool `open_in_editor(project?)`.** Its parameter defaults to the open project.
    - It must be inside the projects folder, like `open_project`.
    - It finds `ZeyoCut.Editor.exe` next to `ZeyoCut.Mcp.exe`, or a new `EditorPath` setting. If it's missing, it says how to get it.
    - It starts the editor detached, without waiting. The editor gets its own empty standard handles, so it never holds the server's stdio pipes (R5).
    - If that project is already open in the editor, it brings the window to the front.
    - It replies with the project and its revision.

**Packaging** (`.github/workflows/release.yml`):

- Today the release zip holds the single-file `ZeyoCut.Mcp.exe` (183 MB, 76 MB zipped), `appsettings.json` and the docs. The `.mcpb` bundle holds the exe in `server/`.
- **Proposal:**
    - Publish `src/ZeyoCut.Editor` the same way: single-file and self-contained, without the OpenCV and ONNX Runtime native files, which the editor never loads. `SystemInfo` already copes with ONNX Runtime missing.
    - Put it next to `ZeyoCut.Mcp.exe` in the zip and in the `.mcpb`, so `open_in_editor` works for Claude Desktop users too.
    - Cost (*measured*): +63 MB zipped each.
    - Add the Windows Desktop runtime's third-party notices to the release's `licenses/`, next to the .NET runtime's. There's no new NuGet package, so `THIRD_PARTY_NOTICES.md` only gains a line saying WPF is part of the .NET runtime.
- **Alternatives**, cheaper downloads but less convenient (D6):
    - A separate editor download.
    - Leaving it out of the `.mcpb`.

## 9. Testing

Automated:

| Area | Tests |
|---|---|
| Core operations | Every operation (move, trim both edges shrinking and extending, split, delete with and without children, reorder, set property, caption text and timing, volume, mute, fades) on test projects: the result, the keyframes and children, and validation errors that leave the project unchanged |
| Undo/redo | Each operation undone gives the exact JSON from before, and redone the exact JSON from after. MCP undo/redo give the same bytes as the old snapshot code. Selective undo across an outside change succeeds when items don't overlap and refuses when they do |
| Sync | Two sessions on one folder in one process, for scenarios a–d. Cross-process: the real `ZeyoCut.Mcp.exe` over stdio (the `McpServerTests.StartServer` harness, `tests/ZeyoCut.Tests/Mcp/McpServerTests.cs:2697`) plus a second session in the test, checking the reply note and that edits survive. Hammer test: two processes × 50 edits, every edit present at the end and the revision counting all 100. Lock freed when its holder is killed |
| Save/load round trip | The fixtures from format 1 to 6 open, migrate to 7 and save without loss (extends `MigrationTests`). Load → save → load gives identical JSON. `revision` doesn't change frame keys, the chunk cache or the preview-audio key |
| MCP handlers | All existing 498 tests unchanged. New: the reply note, `open_in_editor` (with a stand-in exe), `trim_layer` if added. `docs/TOOLS.md` regenerated by its test |
| Rendering an edited project | Apply editor operations through Core, export, then compare frames with `render_frame` and check the sound with `audio_levels`: the trim, move and split frames, the caption text, the volume drop and the fade shape. This follows the existing export-vs-preview tests |
| Editor logic without UI | Lane assignment, snapping, timecode, hit-testing and the conflict list, in a new `tests/ZeyoCut.Editor.Tests` (`net10.0-windows`), so the main test project stays as it is |
| Acceptance test (scripted part) | Steps 1, 3–7 driven through the real server and the editor's Core document: build via MCP, edit and save, Claude adds a caption, both kept, undo/redo, export checked, reopen |

Manual (a checklist for you after each phase): everything you see or touch.

- The layout and resizing.
- Opening from each place and from `open_in_editor`.
- Scrubbing feel and playback with sound.
- Each drag, trim and snap.
- Shortcuts and context menus.
- The inspector fields.
- The conflict bar while Claude edits.
- Export from the editor.
- The dark theme at 100%, 150% and 200% scaling.

UI automation (FlaUI or similar) would be a new dependency, so it isn't planned.

## 10. Risks, open questions and what I'd cut first

| # | Risk | Mitigation |
|---|---|---|
| R1 | Scrubbing over uncached 1080p footage is about 5 updates a second (*measured*: 180 ms per seek) | Latest-wins rendering, the frame cache, and seek-friendly preview proxies if Phase 3 shows the need |
| R2 | Effects-heavy scenes play below 30 fps on one thread (*measured*: 42 ms per frame) | Read-ahead on 2 threads, a 15 fps step like the preview page, an optional render scale |
| R3 | Rebasing unsaved edits is subtle: ids created by splits, coupled changes such as `duration` | The conflict rule (touching anything the other side changed means asking you), id mapping, and tests for each operation |
| R4 | An older server (0.1.0) on the same project ignores the lock and the revision | Docs; the editor still notices its writes |
| R5 | A process the server starts can inherit its stdio pipes, and Claude Desktop might then wait on them | Start the editor with its own null standard handles; a test checks that the server exits cleanly while the editor stays open |
| R6 | Undo memory on very large projects (whole snapshots: 1.3 MB × 200 = 260 MB, the same as today) | Change sets are much smaller; keep full snapshots only for recent steps if needed |
| R7 | The release grows by about 63 MB zipped | D6 alternatives |
| R8 | `FileSystemWatcher` misses events on OneDrive or network folders | A 2 s poll fallback |
| R9 | A professional dark look in WPF is all our own styling | Budgeted in Phase 2; a fixed layout, no control library |
| R10 | Cutout and redaction layers can't be moved on their own; they follow their clips (`SubjectEditor.GetEditableLayer`) | Shown attached to their clips, not draggable; Core refuses anyway |
| R11 | A slow `Mutate` holds the save lock longer | Measured 11–37 ms for edits. Heavy work already happens before `Mutate`, in jobs. Waiters retry for 5 s |

Open questions are listed under D.

What I'd cut first, in order, if time runs short:

1. Playback speeds other than 1×.
2. Drag-and-drop import (keep the file picker).
3. Context menus (keep shortcuts and buttons).
4. Snapping to markers (keep clip edges and the playhead).
5. The Assistant view (it is only information).
6. "Save as copy" in the conflict bar (keep "mine" and "Claude's").

Never cut: sync safety, undo, and export through the one pipeline.

## A. Timeline: how layers become lanes

The timeline is computed from the layers on every change. Nothing about lanes is saved.

- **Visual lanes.** Each `zIndex` used by a top-level video, image or text layer is a lane, the highest at the top. Clips that overlap in the same lane (same `zIndex`; drawn in array order) stack inside it. A lane is labeled by what it holds, for example "Video" or "Overlay · z 10".
- **Caption lanes.** One lane per caption group (`captions`, `captions_ja`, …). Its cues are the clips.
- **Groups and rigs.** A layer with children (a rig, or text attached to a clip) is one clip with a badge. Its children move, trim and split with it, as Core already does. Expanding it to edit parts can come later.
- **Cutouts and redactions.** Thin marks on the clips they follow, not draggable.
- **Audio lanes.** One group per role: Voice, Music, Effects. Clips are packed into as many rows as they need. A video's own sound is part of its clip, with its volume, mute and fades in the inspector.
- **Edits.**
    - Dragging sideways is `MoveLayer`. Dragging a visual clip to another lane sets its `zIndex`; dropping between lanes is a reorder that renumbers. Either way, it's one undo step with the move.
    - Edge drags are `TrimLayer`.
    - Split at the playhead is `SplitLayer`, and Delete is `RemoveLayer` (with its children, as the clip shows them).
    - Audio rows are only packing, so moving between them changes nothing in the project. The role changes in the inspector, because it affects ducking.
- **Snapping.** To clip edges in every lane and to the playhead. It's view logic: the snapped time is what goes to the Core operation.

## B. How the editor is built

- **`src/ZeyoCut.Editor`** (WPF, `net10.0-windows`) references Core and Render.
    - It holds an `EditorDocument` from Core: the working copy, pending operations, history and Save with rebase.
    - Every action calls a Core operation and then rebuilds the view models from the new project. Selection is kept by id.
    - No MVVM library; plain view models.
- **Layout.**
    - Top: mode switch (Assistant | Edit | Export).
    - Left: media library. Center: preview. Bottom: timeline. Right: inspector.
    - Splitters between them, with sizes kept in `editor.json`.
- **Assistant view, real information only:**
    - The current revision, when it was saved and by whom.
    - Recent changes from `backups\`: the last 20 saves with their labels and times, marked Claude or editor by the label.
    - A short hint: "Ask Claude in Claude Desktop, for example 'add Japanese captions'. Save first so Claude sees your edits."
    - Claude's running jobs are left out: job records don't name their project (`src/ZeyoCut.Render/Jobs/JobStore.cs`).
- **Export view.**
    - Presets (`Presets`), file name, and language when the project has tagged layers.
    - Unsaved edits are saved first, so the export matches `project.json` and what Claude sees.
    - It runs the same `Exporter` in the editor process, showing progress, cancel and "show in folder". It shares the project's chunk cache, so a re-export after a small change is fast.
    - The job is kept in memory, not in the jobs folder, so the server never resumes an export the editor started.

## C. Phases

Each phase ends with all tests passing, a commit on `feature/manual-editor`, and a report: what changed, tests and results, your manual checklist and known limits.

**Phase 1: shared editing layer (Core).**

1. Format 7: `revision`. Migration step, fixtures, `docs/project-format.md`.
2. Sync in `ProjectSession`:
    - The `project.lock` lock, the content hash, `Sync()`, and commit under the lock.
    - The server calls `Sync` before each tool, and replies get the note line.
    - Tests: two sessions, cross-process, hammer, killed lock holder.
3. Operations:
    - `TrackEditor` takes over the two `Tracks.Add`.
    - New `TrimLayer`.
    - Operation records (Move, Trim, Split, Remove, SetProperties/`UpdateLayer`, Reorder, Batch) applied through the existing editors.
    - Tests for every operation.
4. Undo with change sets and selective undo (or the barrier, per D3). MCP undo/redo checked byte-for-byte against the old behavior.
5. `EditorDocument`: working copy, pending operations, Save with rebase and the conflict list. Headless tests of scenarios a–d.
6. If D8 says yes: the `trim_layer` tool, `docs/TOOLS.md` regenerated, and a `docs/GUIDE.md` section.
7. Docs: CHANGELOG (Unreleased), `docs/project-format.md` (revision, `project.lock`), `docs/architecture.md` (two processes).

**Phase 2: editor shell.**

1. Move `Settings`, the import orchestration (probe, normalize job), the export start and the proxy start out of `ZeyoCut.Mcp`, with no behavior change (existing tests).
2. WPF shell, dark theme, five-region layout, mode switch.
3. Open, recent, command line, Save, and reloading on outside changes when there's nothing unsaved.
4. Media library with import from the file picker and normalization progress.
5. Preview: frame at the playhead, previous/next frame, timecode, fit/zoom, correct aspect.
6. Publish the editor in `release.yml` (+ the zip and `.mcpb` per D6), notices, and a size check.

**Phase 3: timeline.**

- Lanes, selection, drag to move, trims, split, delete, reorder, zoom, scrubbing and snapping.
- Play/pause, playback speed and sound (read-ahead moved into Render).
- Undo/redo, keyboard shortcuts, context menus, drag-and-drop import.

**Phase 4: inspector, export, sync.**

- Inspector MVP fields; Export view.
- The editor side of the sync: watcher, rebase bar, conflict review.
- Assistant view, `open_in_editor`, one window per project.
- The acceptance test: scripted, plus a run with Claude Desktop.

**Phase 5: later features, one group per sub-phase.**

- 5a: video position, scale, rotation, opacity, crop.
- 5b: text font, size, color, background, alignment, position, and dragging in the preview.
- 5c: effects: blur, face blur settings, adjustments. Effect duration needs engine work or a keyframe UI (disagreement 6); I'll bring you a short design for it before 5c.

**5c design (proposed 2026-11-14; you chose A on 2026-11-14, built).** Effect duration was the open question. Two ways:

- **A. A keyframe UI (my recommendation).** No format change, no MCP change. An effect lasts as long as its layer, as now, and a time-limited one is keyframes (blur 0 → 8 → 0), as `add_transition` already writes them. Phase 5a's rule covers changing an animated value; what's missing is starting and ending the animation by hand:
    1. An **Effects** section in the inspector for video, pictures and text: Blur (x, y), Shadow (color, x, y, blur, opacity), Outline (color, width), Tint (color, amount), each switched on and off as `set_effect` adds and removes it (`EffectEditor.SetEffect`), and Brightness, Contrast and Saturation for video and pictures.
    2. A **◆ button** by each animatable number (the Transform fields too): it adds a keyframe at the playhead with the value shown, which starts animating that value, or, when there's a keyframe at the playhead, removes it (`KeyframeEditor.RemoveKeyframes`). With the last keyframe gone, the value holds that last value, statically.
    3. **Keyframe marks** on the selected clip in the timeline, one tick per time any of its values has a keyframe; a click moves the playhead there. Alt+← and Alt+→ jump to the previous and next one.
    4. **Face blur settings** in the inspector of a clip a redaction follows (today a "Followed by" row): style, strength, padding, feather, hold frames and color, through `update_layer` on the redaction (its own fields). They aren't animated.
    - Blurring 2–4 s is then: playhead at 2 s, Blur on, ◆ with 0; at 2.5 s type 8; at 3.5 s ◆ (8); at 4 s type 0. Four steps. A "from here to there" shortcut that writes those keyframes could come later if it's still too many.
- **B. Engine work.** A `start` and `end` (in the layer's time) on each effect, read by the renderer, and new optional `start`/`end` parameters on `set_effect`. Simpler to use for "blur this part", but it's a project format change (format 8), a renderer change, a change to an MCP tool's parameters, and two ways to time an effect (fields and keyframes) for Claude and for check_project to reason about.

Either way the effect values follow 5a's rule: a value with keyframes shows its value at the playhead and a change adds a keyframe there.

**Phase 6: autosave, recovery, polish, docs** (built 2026-11-14, without autosave and recovery, which change 1 cut).

- Autosave of unsaved work in the editor's folder, and recovery on the next open.
- Polish.
- README editor section, `docs/editor.md` with screenshots, CHANGELOG.

## D. Decisions I need from you

| # | Decision | My recommendation |
|---|---|---|
| D1 | UI framework | WPF (section 4) |
| D2 | Timeline mapping, since the model has no tracks | Lanes computed from type, `zIndex` and caption groups; reorder = `zIndex` (section A) |
| D3 | Undo after the other process changed the project | Selective undo of untouched items (section 7), rather than a hard barrier |
| D4 | What "command-based" means here | Operations recorded as commands (for history and rebase); undo restores recorded before-values rather than running inverse commands |
| D5 | Format and reply changes | `revision` in `project.json` (format 7, old files load as 0); a one-line note in an MCP reply after an outside change; editor edits labeled `editor …` |
| D6 | Packaging | `ZeyoCut.Editor.exe` in the release zip and in the `.mcpb` (+63 MB zipped each) |
| D7 | Mute on an audio layer | Use `visible` (no format change), rather than a new `muted` field |
| D8 | New MCP tools | `open_in_editor` (from the spec) and `trim_layer` (so Claude can trim the way you do). Existing tools stay as they are |
| D9 | New folders and moves | New `src/ZeyoCut.Editor` and `tests/ZeyoCut.Editor.Tests`. `Settings` and the import, export and proxy orchestration move from `ZeyoCut.Mcp` to Core/Render with no behavior change |
| D10 | This file on the website | `mkdocs.yml` has no `exclude_docs`, so after a merge to `main` this plan becomes a page on cut.zeyoapp.com. Add `exclude_docs: manual-editor/` (one line) or keep it public |
