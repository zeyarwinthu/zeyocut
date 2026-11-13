# Manual visual editor: plan (Phase 0)

- Branch `feature/manual-editor`, started from `main` at `f968714` (ZeyoCut 0.1.0 plus one fix).
- Written 2026-11-13. Phase 0 only: inspection and this plan. No production code was changed.
- Baseline on the branch: the solution builds, and 498 of 498 tests pass (146 s). The server has 90 tools (91 with `describe_clip`) and 4 prompts.
- Numbers marked *measured* come from a throwaway benchmark (kept outside the repository) that calls the real `FrameRenderer` and `ProjectSession`. It ran on the development laptop (Intel Core Ultra 9 285H, 16 threads) with a 1920x1080, 30 fps H.264 test clip (keyframe every 60 frames).
- File references are `path:line` on this commit.

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
| Autosave with crash recovery | Backups exist; unsaved editor work does not | — | — | **Needs Core work** (Phase 6) |
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

**Phase 6: autosave, recovery, polish, docs.**

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
