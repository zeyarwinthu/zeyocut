# Changelog

All notable changes to ZeyoCut are listed here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and ZeyoCut follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0, a minor version (0.x.0)
can change tools, settings or the project format; the project format always migrates older projects
when they open.

## [Unreleased]

### Added

- **The ZeyoCut editor:** `ZeyoCut.Editor.exe`, a window for working on a project by hand, in the
  release zip and the bundle. The editor guide (`docs/editor.md`) shows it with screenshots.
  - **Home** shows the recent projects and the projects folder as cards with a picture of each, its
    length, its languages and Claude's latest edit, with a search box and a list view; Open folder,
    or a folder on the command line, opens any project. A project is open in one editor window at a
    time. The header names the project, whether it's saved and when Claude last saved, with undo,
    redo, the views and the way to Export.
  - **New project** (Ctrl+N, or Home's Landscape, Shorts / Reels and Square cards) makes a project
    in the projects folder from a name, a canvas, a frame rate and a background, or from a library
    template, as `create_project` and `new_project_from_template` do, with a preview of the canvas
    and its safe area and the export preset it fits.
  - **Relink:** a project that opens with files missing lists them and finds likely matches in the
    project folder and the import folders (the same name and kind, and the same size, length or
    font family). Search another folder…, Choose file… or Leave it missing, then Relink and open
    relinks them as `relink_asset` does, as one undoable step; a missing tile's menu has Relink….
  - **Welcome:** the first start shows the projects folder, the server's entry for Claude Desktop's
    config to copy, and the sample to open, copied into the projects folder; it turns green when
    Claude connects. The release zip and the bundle carry `examples\tora-jump` next to the editor.
  - **Keyboard shortcuts** (? or F1): every key in groups, with a search by action or key, from the
    same table the keys run from. Ctrl+1, Ctrl+5, Ctrl+6 and Ctrl+E go to Edit, Captions, Activity and Export, Ctrl+D
    duplicates the selected clip with its child layers, N turns snapping on and off, and
    Ctrl+Shift+Z redoes as Ctrl+Y does.
  - **Media:** tiles with thumbnails and sound waveforms (kept in the project's `cache\thumbnails`
    and `cache\waveforms`) and chips for each kind. It imports images, audio, fonts and video (from
    the file picker or dropped on the window, normalizing video as `import_video` does, with
    progress), and media goes onto the timeline by dragging it onto a lane or at the playhead.
  - **Preview and playback:** the frame at any time, drawn by the same renderer as exports, with
    frame stepping, fit and zoom; the preview names the canvas, can draw the safe area (as
    `check_project` checks it) and a thirds grid over the frame, and shows the frame full screen
    (F). It plays the edit at 0.5×, 1× or 2× with the sound mixed as exported, drawing frames at half
    size while playing, and loops. A click selects the layer drawn on top there, a child layer too,
    and dragging moves it, redrawn as it goes and saved on release; Shift keeps it on one axis and
    Escape puts it back. A paused frame is drawn again when a file it shows changes without a save,
    such as a matte the server finishes. A project with languages is drawn and played in one
    language's version, as exports draw it, chosen in the preview's header and kept in the editor's
    settings.
  - **Timeline:** a lane for each draw level, caption track and sound role, named for what it holds
    and numbered from the bottom, with an eye on each that hides every clip in it (mutes, on a sound
    lane) as one step. Clips are drawn rounded in their kind's color with a thumbnail, or a sound's
    waveform from where it's trimmed, and hidden or muted ones faded and dashed. They move, trim,
    split, delete, hide and change lanes by dragging, keys and a right-click menu, with snapping to
    clip edges, the playhead and markers; held at the timeline's edge, a drag or a scrub scrolls it.
    The Blade (B) splits a clip where it's clicked, Add marker (M) puts a chapter marker at the
    playhead as `add_markers` does, and a slider zooms. Pack lanes puts layers that never show
    together on shared levels without changing a frame. The lanes are shaded past the project's end,
    as the ruler is.
  - **Inspector:** edits the selected clip. Its header says where the clip comes from and what goes
    with it, with the timeline's menu under ⋯, and its fields are on tabs by kind: start, end and
    length on one row, draw level, shown or muted, and chips for the attached layers; a video's
    sound and speed (as `set_speed` sets it, without moving the clips after it); a sound's role,
    volume and fades; and a text's or caption's text and style (font, font file, size, bold, color,
    outline, alignment, width, line height, box), a caption's style applying to its whole track.
    Transform fields place a video, picture or text: X and Y, scale, rotation and opacity (with a
    slider), each its static value or, when animated, a keyframe at the playhead; a caption's
    position moves its whole track. A diamond beside each animatable number (one for both of a
    row's values) adds a keyframe at the playhead or takes away the one there, and the selected clip
    shows its keyframes on the timeline, where a click or Alt+←/→ goes to one; clicking into an
    animated value or its diamond while playing stops the playback first. Effects (blur, shadow,
    border, tint) switch on and off and take their settings, animatable too; a video's or picture's
    brightness, contrast and saturation are set in percent; a clip a face blur follows shows the
    redaction's style, strength, padding, feather, hold frames and fill; and a video's crop is set
    by its sides, keeping what's left of the picture in place. With nothing selected, it sets the
    project's Duration.
  - **Captions view:** the caption tracks side by side, a row for each caption of the source track
    (English when there is one) with the other languages' captions that overlap it most, and chips
    with how many rows each track fills. A cell saves its caption's text, removes it when emptied, or
    adds one at the row's times styled like the track's others; a row's start and end move its
    captions in every language as one undo step. Add caption at playhead, Import .srt… (as
    `import_srt` reads it, in the language chosen), Save .srt files (to `exports`, as `export_srt`
    writes them), a preview in the chosen language, and a style for all captions: each track's font,
    and the size, position (top, middle or bottom inside the title-safe margin), background (box,
    outline or none) and line width of every track, each as one undo step.
  - **Audio view** (Ctrl+2): a strip for each sound role (Voice, Music, Sound effects) with a fader
    (-60 to +6 dB), pan and mute saved as `update_project` saves them, one undo step each, and solo,
    which only the preview hears and is never saved; Master with a meter of the preview's mix while
    playing and the export's loudness and true-peak slots. A loudness card with the target (YouTube,
    Podcast, Broadcast or off) and Measure, which measures the mix as `audio_levels` does. The
    music's auto-duck switch, depth and fades. A lane per role with each clip's waveform and volume
    line: pressing the line adds a `volume` keyframe, dragging a point changes its level to the half
    dB and its time by whole frames, Delete takes it away, and the stretches ducking lowers are
    shaded.
  - **Animate view:** the animated layers and values in a list with their values at the playhead,
    their keyframes as a dope sheet (with image layers' drawing changes as a row) or as curves drawn
    from the interpolation exports use. Dragging keys retimes them by whole frames with their values
    and easings, and in the graph changes their values, each drag one undo step; a key can't land on
    another. The eight easings as tiles, Linear / Smooth / Hold, and a custom cubic bezier as
    `set_keyframes` takes it, for the selected keys. An onion skin (the selected layer at up to five
    frames before and after the playhead, the ones after in green) and the layer's motion path over
    the frame. Alt+K keys a value at the playhead, F9 eases in and out, Alt+O turns the onion skin
    off and on, and Delete takes the selected keys away.
  - **Export view:** renders the project as `start_export` does, with preset cards, language
    versions, a quality (crf 18, 23 or 28), .srt files of the caption tracks as `export_srt` writes
    them, a summary card with a picture of the project, progress and stop, Claude's running exports
    of the project read from the jobs folder, and the exports folder's files with Play and Show in
    folder.
  - **Activity view:** the saves the backups folder keeps, yours and Claude's, as steps under their
    days (saves in a row by one side, under 5 minutes apart), with a filter by who saved and
    Restore, which puts a step back as a new revision the way `restore_backup` does.
  - **Saving alongside another program:** each change is saved at once as an undoable step, without
    holding up the window: while another program is saving, the status bar says "Saving…" until the
    change is saved after it. When another program saves the project, the editor shows the new
    version and says what changed. A change begun before that (text typed into a field, a drag)
    isn't saved over a layer the other program changed meanwhile: the status bar says so, and what
    was typed stays in the field. A change that can't be saved (another program holds
    `project.json`, or its folder is read-only or gone) isn't made, and the status bar gives the
    reason.
  - **Errors and settings:** an error the editor didn't expect is shown in plain words, with its
    details in `editor-errors.log` in the log folder. A `project.json` with problems, such as an
    asset whose file is outside `assets\`, opens with the problems named in the status bar. The
    editor keeps its own settings in `%LOCALAPPDATA%\ZeyoCut\editor.json`
    (`ZEYOCUT_EDITOR_SETTINGS` names another file).
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
- **`relink_asset`:** points an asset at another file of the same kind, such as its file found
  elsewhere after it was moved or deleted from `assets\`. The asset keeps its id and takes the new
  file's size or length. `check_project` now suggests it for a missing file.
- **`trim_layer`:** trims a layer's start, end or both, as dragging the edge of a clip does. Trimming
  in cuts the layer and its children shorter; trimming out shows more, as far as a clip's source
  goes. Fades stay at the new edges.
- **Project format 7:** `revision`, the count of saves. It never goes back, even after undo, redo,
  `restore_backup` or an older `project.json` put back by hand. Older projects update as they open.
- **Cubic bezier easing (project format 8):** a keyframe's easing can be `cubicBezier`, with its
  curve as four numbers in `bezier` (`[x1, y1, x2, y2]`, as CSS's `cubic-bezier()`); `set_keyframes`
  takes `bezier`. Older projects update as they open.
- **Mixer settings and volume keyframes (project format 9):** each sound role (voice, music,
  effects) has a gain in dB, a pan and a mute in `audio.voice`, `audio.music` and `audio.effects`,
  set with `update_project` (`role` with `gain`, `pan`, `muted`). Ducking has a level and fades
  (`duckLevel`, `duckFadeIn`, `duckFadeOut`). A sound's `volume`, and a video's, is an animated
  value, keyframed with `set_keyframes` property `volume`; a number there still reads. Older
  projects update as they open.
- **Asset tags (project format 10):** an asset's `tags` group it with others, as bins do.
  `tag_assets` adds and takes them off, `list_assets` shows them, and the editor's Media browser
  lists each tag as a bin. Tags that differ only in case are the same tag. Older projects update as
  they open.
- **Insert and overwrite:** `add_video_layer`, `add_audio_layer` and `add_image_layer` take `edit`.
  `insert` splits the clip under the new one's start on its lane (its draw level, or its role's
  sound) and moves everything that starts there or later later by the new clip's length, with
  markers and camera keys, so the project gets longer. `overwrite` cuts what the lane has under the
  new clip. The editor's Source viewer inserts and overwrites the same way.

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
- Ducking follows the voice clips instead of a compressor listening to them: music goes down by the
  project's duck level (-12 dB) over half a second before each voice clip and comes back over 1.5 s
  after it, the same every time. It used to dip 12–18 dB depending on how loud the voice was, and
  came back up in pauses inside a clip. The export notes say how far (`music ducked -12 dB under
  voice`), and name each role's mixer setting in a `mix:` note.

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
- With `VideoEncoder` at `auto`, a GPU encoder's test encode is tried up to three times before the
  encoder counts as not working. Before, one such empty frame had exports use libx264 on that
  computer until FFmpeg or the hardware changed; where that happened, deleting `encoders.json` in
  the library tests the encoders again.

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
