# Architecture

ZeyoCut is a .NET 10 console app for Windows. It speaks the Model Context Protocol on stdio through the official [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), draws with [SkiaSharp](https://github.com/mono/SkiaSharp), shapes text with [HarfBuzz](https://harfbuzz.github.io/), runs AI models with [ONNX Runtime](https://onnxruntime.ai/), tracks objects with [OpenCV](https://opencv.org/) (through OpenCvSharp4), and reads and writes video with the FFmpeg the user installs.

## Solution layout

| Project | Contents |
|---|---|
| `src/ZeyoCut.Core` | Model, JSON (de)serialization, easing, interpolation, transform math, motion paths, scene evaluation, validation, editing operations (timeline cuts and transitions, silence and transcript cutting, captions, reframing, presets, library, sound effects, camera, lip flap, effects and masks, project checks), undo/redo. No rendering dependencies. |
| `src/ZeyoCut.Render` | SkiaSharp renderer with layer effects and masks, HarfBuzz text layout and line breaking, previews and contact sheets, debug overlay, video decoding and normalization, analysis (scene cuts, noise floor, silences, whisper), FFmpeg exporter with ducking and loudness normalization, ffprobe, `frame_diff` and the `audio_levels` loudness meter, beat detection, per-frame voice levels, object tracking with OpenCV, face and plate detection, redaction |
| `src/ZeyoCut.Mcp` | Console app: the MCP server (official ModelContextProtocol C# SDK, stdio), background jobs, the preview page, the CLI commands and the demo |
| `tests/ZeyoCut.Tests` | xUnit v3 tests, including:<br>- easing values, interpolation, parent/child transforms, JSON round trip, editing, rendering, Burmese/Japanese shaping<br>- split/ripple frame identity, tone+silence+tone cut length, caption/transcript alignment, reframing<br>- export-vs-preview pixel match, measured loudness and ducking, preset size and profile, blurred-background fit, thumbnail crop and size, frame sequence count<br>- library actions on rig copies, camera shake determinism, glitch-frame detection, loudness metering, seeded project problems<br>- click-track tempo and beat times, tone+silence+tone lip flap, sound effect placement<br>- effect and mask pixels, even speed and orientation on motion paths, the middle frame of each transition<br>- MCP over stdio, demo |

## How a tool call runs

1. The MCP client starts `ZeyoCut.Mcp.exe` and sends JSON-RPC over its stdin. The server writes protocol messages, and only those, to stdout; logs go to stderr and to a daily JSON-lines file.
2. The tool checks every path it is given against the folders it may use (`AllowedFolders`): projects live inside the projects folder, and files are read only from there, the library and the import folders, after links and junctions are resolved.
3. One project is open at a time. An editing tool changes a copy of it, validates the whole project, copies the old `project.json` to `backups\`, writes the new one atomically and adds an undo step. If anything is invalid, nothing changes.
4. Work that takes long (normalizing video, analysis, AI mattes and detection, tracking, exports) runs as a background job and returns a job id at once. Each job's record is kept in the jobs folder, so jobs outlast a restart.

The server's instructions to the model (`ServerInfo`), the tools (`Tools\`), the workflow prompts (`WorkflowPrompts`) and the preview page (`PreviewServer`) are all in `src/ZeyoCut.Mcp`.

## Rendering

- **One renderer.** `FrameRenderer` is the single drawing path for previews, contact sheets, the command line and the exporter, so a frame you check is exactly the frame that gets encoded.
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
- The file is written to `name.partial.mp4` and renamed when done, so a failed export never leaves a broken `name.mp4`.
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
