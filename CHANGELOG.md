# Changelog

All notable changes to ZeyoCut are listed here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and ZeyoCut follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0, a minor version (0.x.0)
can change tools, settings or the project format; the project format always migrates older projects
when they open.

## [Unreleased]

### Added

- **The ZeyoCut editor:** `ZeyoCut.Editor.exe`, a window for working on a project by hand,
  in the release zip and the bundle. It opens a project (from a start page with the projects
  folder and recent projects, File → Open, or a folder on the command line), lists its media,
  imports images, audio, fonts and video (from the file picker or dropped on the window, normalizing
  video as `import_video` does, with progress) and shows the frame at any time, drawn by the same
  renderer as exports, with frame stepping, fit and zoom. It plays the edit at 0.5×, 1× or 2× with
  the sound mixed as exported, drawing frames at half size while playing. Its timeline shows a lane
  for each draw level, caption track and sound role; clips move, trim, split, delete, hide and
  change lanes by dragging, keys and a right-click menu, with snapping to clip edges, the playhead
  and markers. Each change is saved at once as an undoable step, without holding up the window: while
  another program is saving, the status bar says "Saving…" until the change is saved after it. When
  another program saves the project, the editor shows the new version and says what changed. A
  change begun before that (text
  typed into a field, a drag) isn't saved over a layer the other program changed meanwhile: the
  status bar says so, and what was typed stays in the field. Media goes onto the timeline by
  dragging it onto a lane or at the playhead, and Pack lanes puts layers that never show together on
  shared levels without changing a frame. The inspector edits the selected clip: start, end, length,
  draw level, shown or muted, a video's sound, role, volume and fades, and a text's or caption's
  text and style (font, font file, size, bold, color, outline, alignment, width, line height, box);
  a caption's style applies to its whole track. Transform fields place a video, picture or text: X,
  Y, scale, rotation and opacity, each its static value or, when animated, a keyframe at the
  playhead; a caption's position moves its whole track. A diamond beside each animatable number adds
  a keyframe at the playhead or takes away the one there, and the selected clip shows its keyframes
  on the timeline, where a click or Alt+←/→ goes to one. Effects (blur, shadow, border, tint) switch
  on and off and take their settings, animatable too; a video's or picture's brightness, contrast
  and saturation are set in percent; and a clip a face blur follows shows the redaction's style,
  strength, padding, feather, hold frames and fill. A video's crop is set by its sides, keeping
  what's left of the picture in place. A click in the preview selects the layer drawn on top there,
  a child layer too, and dragging moves it, redrawn as it goes and saved on release; Shift keeps it
  on one axis and Escape puts it back. A paused frame is drawn again when a file it shows changes
  without a save, such as a matte the server finishes. The Export view renders the project as
  `start_export` does, with presets, language versions, progress, stop and show in folder, and
  lists the exports folder. The project's Duration is set in the inspector, and the lanes are
  shaded past the end, as the ruler is. The Assistant view shows the revision and the 20 most
  recent saves, yours and Claude's. A project is open in one editor window at a time. Held at the
  timeline's edge, a drag or a scrub scrolls it, and clicking into an animated value or its diamond
  while playing stops the playback first. A
  change that can't be saved (another program holds `project.json`, or its folder is read-only or
  gone) isn't made, and the status bar gives the reason. An error the editor didn't expect is shown
  in plain words, with its details in `editor-errors.log` in the log folder. A `project.json` with
  problems, such as an asset whose file is outside `assets\`, opens with the problems named in the
  status bar. The editor keeps its own settings in `%LOCALAPPDATA%\ZeyoCut\editor.json`
  (`ZEYOCUT_EDITOR_SETTINGS` names another file). The editor guide (`docs/editor.md`) shows it with screenshots.
- **`open_in_editor`:** opens a project (by default the open one) in the editor on the user's
  computer, so they can fine-tune it while Claude keeps working; a project already open in the editor
  comes to the front. The editor is `ZeyoCut.Editor.exe` next to the server, or the one the new
  `EditorPath` setting (`ZEYOCUT_EDITOR_PATH`) names. It runs on its own, with none of the server's
  handles, so it stays open when the server stops.
- **Two programs on one project:** the server, a second server or the ZeyoCut editor can
  have a project open at once without overwriting each other. A save holds `project.lock`, checks
  `project.json` by its SHA-256 and makes the edit on top of another program's version. Before each
  tool call the server takes a newer `project.json`, and the reply starts with a note listing what
  changed (up to 10 items).
- **Undo alongside another program:** after another program saves, `undo` and `redo` put back only
  what each step changed, and the other program's changes stay. They're refused, with the items
  named, when the other program changed the same layers or settings since. After a refused `undo`,
  the next one (Ctrl+Z in the editor too) steps over that step, which stays as it is, undoes the
  step before it and names the one it stepped over; `redo` steps over a refused step the same way.
- **`trim_layer`:** trims a layer's start, end or both, as dragging the edge of a clip does. Trimming
  in cuts the layer and its children shorter; trimming out shows more, as far as a clip's source
  goes. Fades stay at the new edges.
- **Project format 7:** `revision`, the count of saves. It never goes back, even after undo, redo,
  `restore_backup` or an older `project.json` put back by hand. Older projects update as they open.

### Changed

- A program reading `project.json` no longer holds up a save: saves rename the new file over the
  old one even while a reader that shares delete access has it open.
- `backups\` keeps the last 20 saves of the ZeyoCut editor (labelled `editor ...`) apart from the
  last 20 others, so a burst of edits in the editor can't push out the server's. The editor's backup
  that holds the project as the server last left it stays however many edits follow, and so does
  the server's that holds the editor's last version.
- `import_asset`, `import_video` and `import_folder` copy files into the project before the save
  takes `project.lock`, so another program's saves don't wait for a big copy (they give up after
  5 s). A file is copied under a temporary name and renamed when complete.
- The release zip and the bundle are about 63 MB larger, with the editor's exe.

### Fixed

- The server no longer overwrites a `project.json` that another program saved while the project was
  open; before, its next edit silently replaced the other program's changes.
- Two programs making the same proxy, preview sound mix or export file at the same time (the server
  and the editor, or two servers) no longer leave a damaged file or fail: each writes a partial file
  of its own, and a proxy or mix the other finished first is kept. A proxy another program is making
  is waited for and then read, instead of being made a second time. Before, a proxy made this way
  could have most of its frames unreadable, and previews and `track_object` went on reading it.
- `open_project` on a project whose footage over 2560 px names a file outside `assets\` lists that
  as a problem, as for any other asset. Before, the reply was an error, although the project had
  been opened.
- `generate_captions behindSubject=auto` adds its fades after an edit made meanwhile to another
  layer, in the editor or with a tool. Only a change to the captions or to what covers them makes
  it refuse, whether it answers directly or from its background job. Before, any save did, even one
  that changed nothing.
- An export with a GPU encoder no longer fails when the encoder fails on a chunk now and then: the
  chunk is encoded again, up to 4 times in all, and the export's notes say so. Intel Quick Sync can
  hand back an empty frame ("Invalid FrameType:0") when frames come faster than it encodes them.

- A release's `SHA256SUMS.txt` has Unix line endings, so `sha256sum -c` can check the downloads too,
  not only PowerShell.

## [0.1.0] - 2026-11-06

The first public release: a headless 2D animation and video editor for Windows whose only interface
is an MCP server, for Claude and other MCP clients.

### Added

- **Projects:** create, open and template projects; undo and redo; a backup before every save; a
  project format with migrations from every earlier version.
- **Cutout animation:** image, text and video layers in a parent–child hierarchy; keyframes with
  easing (overshoot, bounce, elastic); drawing swaps; motion paths; camera moves and shake; effects
  (shadow, outline, blur, tint, color) and masks, all animatable.
- **Library:** rigs, actions (walk cycles, jumps), text styles, project templates and sound effects
  shared by all projects; episode scripts in Markdown or JSON that build a project in one call.
- **Animating to sound:** beat detection and automatic lip flap from a voice.
- **Footage editing:** import with normalization (variable frame rate, rotation, HDR) and proxies for
  large footage; analysis of scene cuts, silences and a word-level transcript (whisper.cpp); ripple
  cuts of silences and filler words; split, trim, move, speed; transitions.
- **Captions and languages:** caption tracks per language from text, SRT or the transcript; Burmese and
  Japanese shaped with HarfBuzz; a font per language; a check for characters a font can't show; SRT
  export; one call that exports every language.
- **Background removal:** chroma key, edge fill, and AI models for images and video (downloaded on
  first use); text behind the subject.
- **Tracking and privacy:** object tracking with corrections; attaching layers to a track; AI face and
  license-plate detection with tracking; redaction (pixelate, blur, solid) with a fail-safe that holds
  boxes across gaps and stops at scene cuts; a redaction report of the spans to check.
- **Checks:** frame-diff glitch detection, audio levels of the exported mix, and a project check
  (missing files, text under the Shorts player UI, keyframes that never play).
- **Export:** H.264 MP4 with music ducked under voice and loudness normalized (-14 LUFS by default);
  YouTube and Shorts presets and both from one call; GPU encoders where they work; thumbnails, image
  sequences, chapter lists; OpenTimelineIO and FCPXML for other editors, with prerendered layers.
- **Preview:** rendered frames and contact sheets that match the export exactly, and a local preview
  page that plays the edit with sound.
- **System check:** whether FFmpeg and whisper are installed, with how to install them; the folders
  ZeyoCut uses; the CPU, RAM, GPU and AI models, with a benchmark that picks the CPU or DirectML per
  model.
- **Workflow prompts** for talking-head cleanup, privacy blur, new episodes and comic panels.
- **Install:** an MCP Bundle (`.mcpb`) for Claude Desktop and a zip with the single exe for any MCP
  client, with their SHA-256 checksums.
- **Docs:** an install guide, a guide to every tool, a tool reference generated from the server, the
  project format, the architecture, three example projects, and the website https://cut.zeyoapp.com.

### Security

- Tools read files only from the projects folder, the library and the import folders (the
  `ImportFolders` setting), and projects live in the projects folder.
- A project, template or rig can't point an asset outside its own folder.
- ONNX Runtime's Windows telemetry is turned off; ZeyoCut itself collects nothing.

[Unreleased]: https://github.com/zeyarwinthu/zeyocut/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/zeyarwinthu/zeyocut/releases/tag/v0.1.0
