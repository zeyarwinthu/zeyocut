# Architecture

ZeyoCut is a .NET 10 console app for Windows. It speaks the Model Context Protocol on stdio through the official [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), draws with [SkiaSharp](https://github.com/mono/SkiaSharp), shapes text with [HarfBuzz](https://harfbuzz.github.io/), runs AI models with [ONNX Runtime](https://onnxruntime.ai/), tracks objects with [OpenCV](https://opencv.org/) (through OpenCvSharp4), and reads and writes video with the FFmpeg the user installs. A WPF app, the editor (`ZeyoCut.Editor.exe`), opens the same projects for editing by hand.

## Solution layout

| Project | Contents |
|---|---|
| `src/ZeyoCut.Core` | Model, JSON (de)serialization, easing, interpolation, transform math, motion paths, scene evaluation, validation, editing operations (timeline cuts and transitions, silence and transcript cutting, captions, reframing, presets, library, sound effects, camera, lip flap, effects and masks, project checks), edit operations and undo/redo, `EditorDocument` (the editor's open project), settings. No rendering dependencies. |
| `src/ZeyoCut.Render` | SkiaSharp renderer with layer effects and masks, HarfBuzz text layout and line breaking, previews and contact sheets, debug overlay, video decoding and normalization, analysis (scene cuts, noise floor, silences, whisper), FFmpeg exporter with ducking and loudness normalization, ffprobe, `frame_diff` and the `audio_levels` loudness meter, beat detection, per-frame voice levels, object tracking with OpenCV, face and plate detection, redaction, video import and proxy jobs |
| `src/ZeyoCut.Mcp` | Console app: the MCP server (official ModelContextProtocol C# SDK, stdio), background jobs, the preview page, the CLI commands and the demo |
| `src/ZeyoCut.Editor` | WPF app (`net10.0-windows`): the editor. References Core and Render, not the server |
| `tests/ZeyoCut.Tests` | xUnit v3 tests, including:<br>- easing values, interpolation, parent/child transforms, JSON round trip, editing, rendering, Burmese/Japanese shaping<br>- split/ripple frame identity, tone+silence+tone cut length, caption/transcript alignment, reframing<br>- export-vs-preview pixel match, measured loudness and ducking, preset size and profile, blurred-background fit, thumbnail crop and size, frame sequence count<br>- library actions on rig copies, camera shake determinism, glitch-frame detection, loudness metering, seeded project problems<br>- click-track tempo and beat times, tone+silence+tone lip flap, sound effect placement<br>- effect and mask pixels, even speed and orientation on motion paths, the middle frame of each transition<br>- MCP over stdio, demo |
| `tests/ZeyoCut.Editor.Tests` | xUnit v3 tests of the editor's parts that need no window: the start page's lists, its settings file, timecodes, zoom, the timeline's lanes, drags and snapping, drawing preview frames paused and while playing, the playback clock, importing, the inspector's fields and the edits they make, the Export and Activity views' lists, and when Claude last saved, and one window per project |

## How a tool call runs

1. The MCP client starts `ZeyoCut.Mcp.exe` and sends JSON-RPC over its stdin. The server writes protocol messages, and only those, to stdout; logs go to stderr and to a daily JSON-lines file.
2. The tool checks every path it is given against the folders it may use (`AllowedFolders`): projects live inside the projects folder, and files are read only from there, the library and the import folders, after links and junctions are resolved.
3. Before the tool runs, the open project catches up with `project.json` in case another program saved it (`ProjectSession.Sync`, from a call filter), and the reply then starts with a note that lists what changed.
4. One project is open at a time. An editing tool holds `project.lock`, reads `project.json` and takes another program's version if its SHA-256 differs from the last one this server read or wrote, then changes a copy of the project, validates the whole project, copies the old `project.json` to `backups\`, writes the new one atomically as the next `revision` and adds an undo step. If anything is invalid, nothing changes. See [Saving alongside another program](project-format.md#saving-alongside-another-program).
5. Work that takes long (normalizing video, analysis, AI mattes and detection, tracking, exports) runs as a background job and returns a job id at once. Each job's record is kept in the jobs folder, so jobs outlast a restart.

The server's instructions to the model (`ServerInfo`), the tools (`Tools\`), the workflow prompts (`WorkflowPrompts`) and the preview page (`PreviewServer`) are all in `src/ZeyoCut.Mcp`.

## The editor

`src/ZeyoCut.Editor` holds no editing logic of its own; it shows the project and calls Core and Render.

- **Opening and saving.** A project opens as an `EditorDocument`. Each change is an `EditOperation` saved at once on the same path as a tool call: `project.lock`, the SHA-256 check, a backup labelled `editor ...` and the next `revision`. Undo and redo are `EditorDocument`'s, step by step.
- **Another program's saves.** A `FileSystemWatcher` on the project folder, with a check every second in case it misses one, calls `EditorDocument.Refresh`. The editor then shows the new version and names that program's edits from their backups.
- **Preview.** `PreviewFrames` in Render, which the preview page runs too, draws and keeps the frames under their `FrameKeys` key, in up to 512 MB. A paused frame is drawn at the canvas size on a worker thread, only the latest asked for. While playing, the frames ahead are drawn on a background thread at a render scale (½ by default), and the editor shows every second frame or more while drawing takes longer than a frame lasts. The pixels are copied into a `WriteableBitmap` that WPF scales to the panel.
- **Picking and dragging in the preview.** Each frame drawn comes with `LayerOutlines.At`: every layer's box on the canvas (the same box `debug=true` draws), its anchor, and the matrix from its parent's pixels to the canvas. A click takes the topmost box under the mouse. `PreviewDrag` turns the mouse's travel into the parent's pixels and makes the edit, `PlacementEditor.Set` at the playhead or, for a caption, a `ShiftLayersOperation` on its track. While dragging, that edit is applied to a copy of the project and the frame drawn from the copy, one at a time; the edit is saved on release. The selection outline is a WPF polygon over the frame.
- **Sound.** `PreviewAudio.Make` mixes the sound as the export does into `cache\preview\mix-<key>.m4a`, in the background 0.6 s after the last edit that changes it. WPF's `MediaPlayer` plays it (`SpeedRatio` for 0.5× and 2×), and the picture follows its clock, set to it when they drift more than 0.08 s apart. The editor deletes only the mixes it made, since the preview page may be playing another.
- **Timeline.** `TimelineLayout` works out the lanes from the layers on every change; nothing about lanes is saved. `TimelineEdits` turns a drag into Core operations: `MoveLayerOperation`, `TrimLayerOperation`, `ReorderLayerOperation`, or a move and a reorder as one `BatchOperation`, so each drag is one undo step. `TimelineEditor.Limits` says how far a trim or a move can go, so a drag stops where the operation would refuse. `TimelineView` draws the lanes with `DrawingVisual`s, and on the selected clip its layer's keyframe times (`ValueEditor.KeyTimes`).
- **Import.** A file is copied in with `AssetImporter.Prepare` before the save takes `project.lock`, then added as one edit. Video goes through `VideoImport`: footage that needs it is normalized in a background job first, one at a time. Proxies start with `ProxyJobs`, as they do for the server. The editor's jobs are kept in memory, never in the jobs folder, so the server doesn't resume them.
- **Inspector.** `InspectorFields` turns the selected layer into rows. Each row that can change carries the Core operation its new value makes: `MoveLayerOperation`, `TrimLayerOperation`, `ReorderLayerOperation`, `UpdateLayerOperation` (the fields `update_layer` sets), or for a caption's style `UpdateLayersOperation` on every caption of its track as one step. Its animatable numbers (Transform, Effects, Adjustments) go through `ValueEditor.Set`: an `UpdateLayerOperation`, or a `SetEffectOperation` for an effect's, when the value has no keyframes, a `SetKeyframesOperation` at the playhead when it has them (the `set_keyframes` logic), or a batch. The diamond beside each is `ValueEditor.ToggleKeyframe`: a `SetKeyframesOperation` with the value at the playhead, or a `RemoveKeyframesOperation` for the keyframe on the playhead's frame, batched with making its value the static one when it was the last. Effects switch on and off with `SetEffectOperation` (`EffectEditor.SetEffect`), and a face blur's settings are an `UpdateLayerOperation` on the redaction. A caption's position is a `ShiftLayersOperation` that moves its whole track, and a video's crop a `CropLayerOperation` that moves the anchor, mask and child layers with the cut so the picture stays in place. The window only shows the rows and applies what they return.
- **Adding clips.** `AddClipOperation` puts an asset on the timeline as `add_video_layer`, `add_image_layer` or `add_audio_layer` would, at a time and on a level or in a sound role. `PackLanesOperation` (`ProjectEditor.PackedLevels`) moves layers that never show at the same time onto shared levels: each layer stays above every layer under it that it shows with, so every frame is drawn as before.
- **Export.** `ExportStart` in Render, which `start_export` uses too, checks the file name, the preset and the language version and starts the export in the editor's process. Its `ExportJobManager` keeps the job in memory, never in the jobs folder, so the server doesn't resume it. The chunk cache in the project folder is shared, so exporting again after a small change is fast.
- **One window per project.** `ProjectClaim` holds a named pipe, for the current user only, named from a hash of the project folder's path. An editor asked to open a project another editor has open asks that one, through the pipe, to come to the front, and doesn't open it.
- **`open_in_editor`.** `EditorLauncher` in the server starts the editor with `CreateProcess`: it inherits none of the server's handles (the client's stdio pipes stay the server's alone, so the client sees the server end when it ends), in a process group of its own and out of the client's job object where Windows allows it, so the editor stays open when the server stops.
- **Its own settings** (recent projects, panel sizes, window placement) are in `%LOCALAPPDATA%\ZeyoCut\editor.json`, never in `project.json`.
- **Size.** The published exe leaves out the native OpenCV, ONNX Runtime and DirectML libraries (a target in `ZeyoCut.Editor.csproj`). The editor runs no AI models, and nothing in drawing a frame needs them.

## Rendering

- **One renderer.** `FrameRenderer` is the single drawing path for previews, contact sheets, the command line and the exporter, so a frame you check is exactly the frame that gets encoded.
- **Smaller frames for playback.** `RenderOptions.Scale` draws the canvas smaller and decodes clips at most at that size, for the editor's playback. Only the editor uses it; exports, `render_frame` and the preview page draw at scale 1.
- **Deterministic.** It draws on the CPU only, with no GPU and no timing dependence. The same project gives the same pixels on every run, which the tests rely on.
- **Scene first, pixels second.** `SceneEvaluator` (in `ZeyoCut.Core`, with no rendering dependency) works out every layer's transform, opacity, drawing and timing at a time; the renderer then draws that list in `zIndex` order.
- **AI models** (background removal, face and plate detection) run through ONNX Runtime, on the CPU, or with DirectML where `system_check` measured that it helps (see `AiDevice` under [Configuration](https://github.com/zeyarwinthu/zeyocut#configuration)).

## Checks

Three read-only tools to run before an export (see [the guide](GUIDE.md#checks)):

- **`frame_diff`** renders the frames 160 px wide and scores each frame's change from the one before. It flags single-frame glitches and unexplained jumps, and lists layer starts and ends, drawing swaps, hold keys and analyzed scene cuts as expected cuts.
- **`audio_levels`** mixes the sound as the export would and measures BS.1770 loudness and peaks per section. It flags clipping, too-quiet sections and silence in the middle.
- **`check_project`** reports missing files, layers off the canvas, text in the title-safe margin or the Shorts UI zones, characters a font has no glyphs for, background-only frames, keyframes that never play and unused assets.

## Export

- Frames are rendered as raw RGBA and piped to `ffmpeg` on stdin.
- An export is rendered in chunks of 2 s on up to 4 threads (a quarter of the logical processors), each chunk piped to an `ffmpeg` of its own. The chunks are joined with FFmpeg's concat demuxer without encoding them again, and the audio is mixed in as they are joined. Each chunk is kept in `cache\chunks\` under a key made of everything its frames show and how it is encoded, so exporting again renders only the chunks that changed, and an export started again after a restart goes on from the chunks it finished. The folder is trimmed to 2 GB.
- Encoding uses `libx264 -preset veryfast -crf 18` (adjustable), `yuv420p` with BT.709 color tags, and `+faststart`.
- Where a GPU encoder works (NVENC, Quick Sync or AMF; see `VideoEncoder` under [Configuration](https://github.com/zeyarwinthu/zeyocut#configuration)), it is used instead at the matching quality: Quick Sync's `global_quality` is the CRF + 1, which matched libx264's SSIM at about the same size. On this laptop (Intel Arc 140T), Quick Sync made the 60 s 1080p export in 20.7 s instead of 21.3 s on 4 threads, with a third of the encoders' CPU time (44 s instead of 143 s) and 2.4 GB of memory instead of 4.0 GB; in one pass, 26 s instead of 34 s. At 4K it was a little slower (36 s instead of 33 s). The export's notes name the encoder.
- The presets `youtube_1080p` and `shorts_1080p` use `-preset medium`, high profile, 2 B-frames and a keyframe every half second, scaled to 1920x1080 or 1080x1920 with Lanczos.
- The file is written to `name.<unique>.partial.mp4` and renamed when done, so a failed export never leaves a broken `name.mp4`, and two programs exporting to one name each write their own file.
- `export_batch` exports several presets one after another in one job. A preset of another shape than the canvas gets the whole canvas fitted over an enlarged, blurred copy of itself (blurred at 1/8 size, then enlarged), so nothing is cut off.
- `export_language_versions` does the same for each language's version: `<name>_<language>_<preset>.mp4` for every language and preset, in one job.
- `export_thumbnail` saves one frame as a PNG (1280x720 by default, cropped from the center for another shape) and warns above YouTube's 2 MB limit. `export_frames` writes a numbered PNG or JPG sequence to `exports\<project>_frames\`.
- The result is checked with ffprobe.
- `report_render_stats` says where a finished export's time went: per stage (decoding, text, effects, fitting to a preset, waiting for the encoder, …), per layer, and its slowest frames.
- On this machine a 3 s 1280x720 project exports in under a second. A 9 s 1080x1920 Short with three clips, captions and music takes about 9 s.
- A 60 s 1920x1080 clip with music and text exports with `youtube_1080p` in about 20 s on 4 threads (34 s in one pass), and in 3.3 s again when nothing changed. Fitted into `shorts_1080p` it takes 44 s (91 s in one pass), and a 20 s 3840x2160 export takes 33 s (59 s). Beyond 2 threads the gain comes mostly from the render-heavy exports, since the x264 encoders take most of the CPU.
- Each thread of a chunked export takes memory: 4 threads peaked at about 4 GB for 1080p and 9 GB for 4K. Fewer threads are used when half the free memory can't hold them (each counted as 0.5 GB plus 250 bytes a pixel), and the export notes say so.
- `export_markers` writes the chapter markers as a YouTube chapter list for the video's description, and warns about each of YouTube's rules they break (the first at 0:00, at least 3, each at least 10 s).

## Other editors

To finish an edit in DaVinci Resolve, Final Cut Pro or another editor:

- `export_otio` writes the timeline as OpenTimelineIO, and `export_fcpxml` as FCPXML 1.9: cuts, trims, speed, the clips' sound and the markers, pointing at the files in `assets\`.
- `prerender_layers` draws layers that those formats can't hold (text, keyframed motion, effects, the camera) into ProRes 4444 files with alpha, which both exports put on tracks above the clips while they are current.
- Each reply lists what the timeline can't hold, such as keyframes, a clip's place and size, volume, ducking and loudness.
- The `.otio` files load in the `opentimelineio` Python library with every clip where it belongs. The FCPXML was checked by reading it back. Neither has been opened in Resolve or Final Cut Pro yet.

## Command line

```text
ZeyoCut.Mcp.exe                                        run the MCP server on stdio
ZeyoCut.Mcp.exe render-frame <projectDir> <time> <out.png> [--debug]
ZeyoCut.Mcp.exe export <projectDir> <out.mp4> [--crf 18]
ZeyoCut.Mcp.exe demo <outDir>                          build the demo project and export it
ZeyoCut.Mcp.exe benchmark <feature> <model.onnx> <cpu|gpu>   time one AI model (system_check runs this)
```

`render-frame` and `export` use the same renderer as the MCP tools. `benchmark` prints one JSON line with the model's load and run times; `system_check` starts it in a process of its own for DirectML, so a GPU driver that hangs can be stopped.

## The demo

```powershell
ZeyoCut.Mcp.exe demo C:\Temp\tora-demo
```

This builds a complete project in an empty or new folder. Everything is generated by code, so there are no external files.

**The character.** Tora is an original cartoon cat in 3 parts: body, head (parented at the neck, with `normal` and `happy` drawings) and tail. They hang under an invisible rig root. Over 3 seconds Tora jumps:

- **squash** before takeoff (scale anchored at the feet)
- **stretch** in the air
- an **easeOutBack landing** squash that overshoots and settles
- **head lag**: the head sinks at takeoff, catches up at the apex and keeps going down on impact
- a happy-face drawing swap
- a **wagging tail** that trails the motion

**The scene.** A drop shadow shrinks and fades with height, clouds drift, and a title pops in with easeOutBack under a Japanese subtitle.

**The sound.** Two generated tones: a "boing" at takeoff and a thud at landing.

**The output.** The command writes:

- a contact sheet and a debug frame into `renders\`
- `exports\tora_jump.mp4`

It prints the ffprobe summary, for example:

```text
...\exports\tora_jump.mp4 — 3.000s, 1280x720 @ 30 fps, h264 yuv420p, audio aac 2ch 48000 Hz, 282.0 KB, 90 frames, took 0.7s
```

`DemoTests` checks the jump mechanics and verifies the exported file with ffprobe: 3.0 s ± 1 frame, 1280x720, 30 fps, 90 frames, h264 yuv420p, aac.

## Notes

- **Test runner.** `global.json` uses Microsoft.Testing.Platform, and the VSTest packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`) are not referenced. xUnit v3 under the .NET 10 SDK requires this.
- **SkiaSharp 4.x** (4.154.0) is used, the current major version.
- **Asset registry.** `project.json` keeps an `assets[]` registry. Layers refer to asset ids, and `import_asset` copies the file into `assets\`, so a project folder is self-contained.
