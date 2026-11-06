# ZeyoCut tool guide

The `zeyocut` server has 90 tools, plus [`describe_clip`](#optional-describe_clip) when a clip describer is configured, and 4 [workflow prompts](#workflow-prompts). Every example below was run against the real server; paths are shortened to `C:\Users\you\...` and `C:\videos\...`.

## Conventions

- **One project is open at a time.** Call `create_project` or `open_project` first. Every other tool works on the open project.
- **Every editing tool saves `project.json` immediately and adds one undo step.** Edits are all-or-nothing: if any part of a call is invalid, nothing changes.
    - Editing tools: `update_project`, `restore_backup`, `import_asset`, `import_folder`, `import_video`, `add_*_layer`, `update_layer`, `remove_layer`, `duplicate_layer`, `set_keyframes`, `remove_keyframes`, `clear_keyframes`, `split_layer`, `remove_range`, `move_layer`, `set_speed`, `add_transition`, `add_markers`, `remove_markers`, `remove_silences`, `cut_by_transcript`, `generate_captions`, `add_caption_track`, `import_srt`, `reframe_to_vertical`, `add_rig`, `apply_action`, `apply_style`, `add_sfx`, `auto_lip_flap`, `set_camera`, `camera_shake`, `set_effect`, `set_mask`, `set_motion_path`, `remove_background`, `refine_matte`, `text_behind_subject`, `track_object`, `reanchor_track`, `detect_and_track`, `attach_to_track`, `apply_redaction`, `tracks_to_keyframes`.
    - `build_from_script` creates a project, and its build is one undo step on top of the empty project.
    - Library saves (`save_rig`, `save_action`, `save_style`, `save_template`) write to the shared library and don't change the project, so they aren't undo steps.
- **Long work runs in the background** and returns a job id right away: `import_video` when the file needs normalizing (`import_N`), `analyze_video` (`analyze_N`), `frame_diff` over more than 150 frames (`framediff_N`), `audio_levels` over more than 120s (`audiolevels_N`), `detect_beats` on more than 120s of sound (`beats_N`), `export_batch` (`batch_N`), `export_language_versions` (`versions_N`), `export_frames` (`frames_N`), `prerender_layers` (`prerender_N`), `remove_background`, `text_behind_subject` or `generate_captions` with an AI method (`matte_N`), `text_occlusion` over more than 150 frames (`occlusion_N`), `generate_captions` with `behindSubject=auto` over more than 600 caption frames or before its matte is computed (`captions_N`), `system_check` (`syscheck_N`), `track_object` and `reanchor_track` over more than 150 frames (`tracking_N`), `detect_and_track` (`detect_N`), the [proxy](#import_video) of footage over 2560 px (`proxy_N`), `start_export` (`export_N`) and, when configured, `describe_clip` (`describe_N`). Poll `get_job_status` (or `get_export_status` for exports). A running job's status estimates the time left.
- **Jobs outlast a restart of the server.** Each one is written to the jobs folder (see the README's Configuration). An export (`export_N`, `batch_N`, `versions_N`) that was running when the server stopped starts again under its id when the server next starts; any other running job shows as failed, saying how far it got. See [`get_job_status`](#get_job_status).
- **AI jobs say how long they should take,** from [`system_check`](#system_check)'s benchmark, or a rough guess before it. One that would take more than 5 minutes isn't started: the reply gives the estimate and nothing changes, so the user hears first how long it takes. Calling again with `confirmLongJob: true` starts it. This applies to `remove_background`, `text_behind_subject` and `generate_captions` with an AI method, and to `detect_and_track`.
- **Units:**
    - pixels, origin at the top-left, y down
    - rotation in degrees clockwise
    - time in seconds
    - colors `#RRGGBB` or `#RRGGBBAA`
- **Ids:** letters, digits, `_` and `-`. They are generated from names or file names when omitted.
- **Errors** come back as tool results with `isError: true` and a message saying what to fix, usually listing the valid values or existing ids. The message starts with the MCP SDK's prefix `An error occurred invoking '<tool>': `.
- **Warnings** (font lacks glyphs, a static value is hidden by keyframes, a keyframe past the end, …) are appended to successful results under `Warnings:`.

Contents:

- [Project](#project): `create_project`, `open_project`, `update_project`, `get_project`, `list_projects`, `undo`, `redo`, `restore_backup`
- [Assets](#assets): `import_asset`, `import_folder`, `list_assets`
- [Layers](#layers): `add_image_layer`, `add_text_layer`, `add_audio_layer`, `update_layer`, `remove_layer`, `duplicate_layer`, `list_layers`
- [Animation](#animation): `set_keyframes`, `remove_keyframes`, `clear_keyframes`
- [Camera](#camera): `set_camera`, `camera_shake`
- [Effects and motion](#effects-and-motion): `set_effect`, `set_mask`, `set_motion_path`
- [System check](#system-check): `system_check`, `get_logs`
- [Background removal](#background-removal): `remove_background`, `refine_matte`
- [Text behind the subject](#text-behind-the-subject): `text_behind_subject`, `text_occlusion`
- [Tracking](#tracking): `track_object`, `reanchor_track`, `detect_and_track`, `attach_to_track`
- [Redaction](#redaction): `apply_redaction`, `tracks_to_keyframes`, `redaction_report`
- [Preview](#preview): `render_frame`, `render_contact_sheet`, `preview_server`
- [Video](#video): `import_video`, `add_video_layer`, `get_job_status`, `reframe_to_vertical`
- [Editing](#editing): `split_layer`, `remove_range`, `move_layer`, `set_speed`, `add_transition`
- [Markers](#markers): `add_markers`, `remove_markers`, `export_markers`
- [Analysis and cutting](#analysis-and-cutting): `analyze_video`, `get_transcript`, `view_source`, `remove_silences`, `cut_by_transcript`
- [Optional: describe_clip](#optional-describe_clip): `describe_clip`, only when a clip describer is configured
- [Captions](#captions): `generate_captions`, `add_caption_track`, `import_srt`, `export_srt`
- [Languages](#languages): language tags, one version per language, a font per language
- [Library](#library): `save_rig`, `add_rig`, `save_action`, `apply_action`, `save_style`, `apply_style`, `save_template`, `new_project_from_template`, `list_library`
- [Audio for animation](#audio-for-animation): `list_sfx`, `add_sfx`, `detect_beats`, `auto_lip_flap`
- [Episode scripts](#episode-scripts): `build_from_script`
- [Checks](#checks): `frame_diff`, `audio_levels`, `check_project`
- [Export](#export): `start_export`, `get_export_status`, `report_render_stats`, `export_batch`, `export_language_versions`, `export_thumbnail`, `export_frames`
- [Other editors](#other-editors): `prerender_layers`, `export_otio`, `export_fcpxml`
- [Workflow prompts](#workflow-prompts): `talking_head_cleanup`, `privacy_blur`, `new_episode`, `comic_panels`

---

## Project

### `create_project`

Creates a project folder (`project.json`, `assets\`, `renders\`, `exports\`) and opens it.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `name` | string | required | Also the folder name under the projects root |
| `width`, `height` | int | 1920, 1080, or the preset's | Even, 16–8192 |
| `fps` | int | 30, or the preset's | 1–120 |
| `duration` | number | 10 | Seconds, up to 7200 |
| `backgroundColor` | string | `#FFFFFF` | |
| `directory` | string | | Absolute folder to use instead of `<projectsRoot>\<name>` |
| `preset` | string | | `youtube_1080p` (1920x1080) or `shorts_1080p` (1080x1920 vertical), both 30 fps. Explicit `width`/`height`/`fps` win |

Every project starts with export loudness normalization at -14 LUFS and music ducking on; see `update_project`.

```json
{ "name": "tora-test", "width": 1280, "height": 720, "fps": 30, "duration": 3 }
```
```text
created and opened project 'tora-test' at C:\Users\you\ZeyoCut\projects\tora-test
1280x720 @ 30 fps, 3s (90 frames), background #FFFFFF
audio: loudness -14 LUFS, music ducking on
next: import_asset / import_video, then add_image_layer / add_text_layer / add_audio_layer / add_video_layer
```
```json
{ "name": "talk-demo", "preset": "youtube_1080p", "duration": 5 }
```
```text
created and opened project 'talk-demo' at C:\Users\you\ZeyoCut\projects\talk-demo (preset youtube_1080p: YouTube 1920x1080 landscape, 30 fps)
1920x1080 @ 30 fps, 5s (150 frames), background #FFFFFF
audio: loudness -14 LUFS, music ducking on
next: import_asset / import_video, then add_image_layer / add_text_layer / add_audio_layer / add_video_layer
```

### `open_project`

Opens an existing project and returns its layer tree. Undo history starts fresh.

A project saved by an older ZeyoCut is updated to the current format (6) as it opens, without losing anything, and the reply says so. The file as it was is kept in the project's `backups` folder as `project-v<format>-<time>.json`.

Footage over 2560 px that has no [proxy](#import_video) yet gets one in the background, and the reply ends with a line like `making a 1920 px proxy of 'street' in the background as proxy_2: previews and object tracking read it once it is done, exports always read the original`.

| Parameter | Type | Notes |
|---|---|---|
| `nameOrPath` | string | A name from `list_projects`, or an absolute folder path |

```json
{ "nameOrPath": "tora-test" }
```
```text
opened C:\Users\you\ZeyoCut\projects\tora-test
tora-test: 1280x720 @ 30 fps, 3s, 6 layer(s), 5 asset(s)
tora_body [image]  z=0  t=0–end  drawings=normal  anchor=(110,234)  keys: y(3) scaleY(5)
├─ tora_head [image]  z=1  t=0–end  drawings=normal/happy  anchor=(120,196)  keys: drawing(2)
└─ tora_tail [image]  z=-1  t=0–end  drawings=normal  anchor=(164,72)
subtitle_my [text]  z=2  t=0–end  text="မင်္ဂလာပါ"  font=Myanmar Text 56px  anchor=(101.5,52.104)
subtitle_ja [text]  z=3  t=0–end  text="こんにちは"  font=Yu Gothic 40px  anchor=(100,32.041)
boing [audio]  t=0.5–end  asset=boing volume=0.8
```

A project saved by Phase 1 (format 1):

```json
{ "nameOrPath": "tora-jumps" }
```
```text
opened C:\Users\you\ZeyoCut\projects\tora-jumps
updated project.json from format 1 (saved by an older ZeyoCut) to 6; the file as it was is kept as backups\project-v1-20261104-160855.json
Tora Jumps: 1280x720 @ 30 fps, 3s, 11 layer(s), 9 asset(s)
clouds [image]  z=1  t=0–end  drawings=normal  anchor=(0,0)  keys: x(2)
ground [image]  z=2  t=0–end  drawings=normal  anchor=(0,0)
shadow [image]  z=3  t=0–end  drawings=normal  anchor=(120,25)  keys: x(2) scaleX(6) scaleY(6) opacity(3)
tora [image]  "Tora rig root"  z=8  t=0–end  null (no drawings)  anchor=(0,0)  keys: x(2) y(3) rotation(4)
└─ tora_body [image]  "Tora body"  z=10  t=0–end  drawings=normal  anchor=(110,234)  keys: scaleX(7) scaleY(7)
   ├─ tora_head [image]  "Tora head"  z=11  t=0–end  drawings=normal/happy  anchor=(120,196)  keys: y(7) rotation(7) drawing(3)
   └─ tora_tail [image]  "Tora tail"  z=9  t=0–end  drawings=normal  anchor=(164,72)  keys: rotation(13)
title [text]  z=20  t=0–end  text="Tora Jumps!"  font=Segoe UI 88px  anchor=(249.734,58.523)  keys: scaleX(2) scaleY(2) opacity(2)
subtitle [text]  z=21  t=0–end  text="トラのジャンプ"  font=Yu Gothic 40px  anchor=(140,32.041)  keys: y(2) opacity(2)
sfx_boing [audio]  t=0.9–end  asset=boing volume=0.8
sfx_thud [audio]  t=1.8–end  asset=thud volume=1
```

Opening it again just opens it.

### `update_project`

Changes the open project's settings. Only the parameters you pass change, and at least one is required.

| Parameter | Type | Notes |
|---|---|---|
| `name` | string | Display name. The folder is not renamed |
| `width`, `height` | int | Even, 16–8192. Layers are **not** moved; positions stay in pixels |
| `fps` | int | 1–120. Keyframe times are in seconds, so timing is unchanged; only the frame count changes |
| `duration` | number | Seconds, up to 7200 |
| `backgroundColor` | string | |
| `preset` | string | `youtube_1080p` or `shorts_1080p`: sets width, height, fps and loudness (other parameters you pass win) |
| `loudnessTarget` | number | Export loudness in LUFS, -40 to -5 (-14 suits YouTube and most platforms). `0` turns normalization off |
| `ducking` | bool | Lower music (role `music`) under voice (role `voice`) in the export |

The result lists each change and warns about:

- keyframes and layers past a shorter end
- audio that would be cut at the end
- layers whose `end` was the old project end, which won't stretch to a longer one
- a resized canvas, and a changed orientation (use `reframe_to_vertical` for video)

A call that changes nothing is an error and adds no undo step.

```json
{ "duration": 0.8 }
```
```text
updated project 'tora-test': duration 3s → 0.8s
1280x720 @ 30 fps, 0.8s (24 frames), background #FFFFFF
Warnings:
- keyframes after the new end (0.8s) won't be reached: tora_body.y (2), tora_body.scaleY (2), tora_head.drawing (1)
- audio 'boing' runs until 1.1s and will be cut at 0.8s
```
```json
{ "duration": 6, "backgroundColor": "#FFF4E0" }
```
```text
updated project 'tora-test': duration 0.8s → 6s; backgroundColor #FFFFFF → #FFF4E0
1280x720 @ 30 fps, 6s (180 frames), background #FFF4E0
```
```json
{ "width": 1920, "height": 1080 }
```
```text
updated project 'tora-test': width 1280 → 1920; height 720 → 1080
1920x1080 @ 30 fps, 6s (180 frames), background #FFF4E0
Warnings:
- the canvas changed from 1280x720 to 1920x1080, but layer positions are in pixels and were not moved; check the layout with render_frame
```
```json
{ "fps": 30 }
```
```text
An error occurred invoking 'update_project': nothing changed; the project already is 1920x1080 @ 30 fps, 6s, background #FFF4E0, loudness -14 LUFS, ducking on
```
A landscape video project switched to vertical:
```json
{ "preset": "shorts_1080p" }
```
```text
updated project 'talk-demo': width 1920 → 1080; height 1080 → 1920
1080x1920 @ 30 fps, 8.906s (267 frames), background #FFFFFF
Warnings:
- the canvas changed from 1920x1080 to 1080x1920, but layer positions are in pixels and were not moved; check the layout with render_frame
- the orientation changed; reframe_to_vertical crops and fits video layers to the new canvas
```
```json
{ "loudnessTarget": 0, "ducking": false }
```
```text
updated project 'talk-demo': loudness -14 LUFS → off; music ducking off
1080x1920 @ 30 fps, 8.906s (267 frames), background #FFFFFF
```

### `get_project`

Returns the full `project.json` of the open project. It takes no parameters.

```json
{}
```
```json
{
  "formatVersion": 2,
  "name": "tora-test",
  "width": 1280,
  "height": 720,
  "fps": 30,
  "duration": 3,
  "backgroundColor": "#FFFFFF",
  "audio": { "loudnessTarget": -14, "truePeak": -1, "ducking": true },
  "assets": [
    { "id": "tora_body", "type": "image", "file": "assets/tora_body.png", "width": 220, "height": 240 },
    { "id": "boing", "type": "audio", "file": "assets/boing.wav", "duration": 0.6 }
  ],
  "layers": [
    {
      "type": "image", "id": "tora_body", "name": "tora_body", "start": 0, "end": null, "zIndex": 0,
      "parentId": null, "visible": true, "anchorX": 110, "anchorY": 234, "flipX": false,
      "drawings": { "normal": "tora_body" }, "drawing": { "value": "normal" },
      "x": { "value": 640 },
      "y": { "value": 600, "keyframes": [
        { "time": 0.5, "value": 600, "easing": "easeOut" },
        { "time": 0.9, "value": 380, "easing": "easeIn" },
        { "time": 1.3, "value": 600, "easing": "linear" } ] },
      "rotation": { "value": 0 }, "scaleX": { "value": 1 }, "scaleY": { "value": 1 }, "opacity": { "value": 1 },
      "brightness": { "value": 0 }, "contrast": { "value": 1 }, "saturation": { "value": 1 }
    }
  ]
}
```
*(shortened)*

### `list_projects`

Lists the projects under the projects root. It takes no parameters.

```json
{}
```
```text
projects root: C:\Users\you\ZeyoCut\projects
- tora-test: "tora-test" 1280x720 @ 30 fps, 3s, 6 layer(s), modified 2026-11-02 01:29  [open]
```

### `undo` / `redo`

These undo or redo the last change. History holds up to 200 steps for the current session. They take no parameters.

```json
{}
```
```text
undid: remove_layer mimi_body (18 more undo step(s), 1 redo step(s))
```
```text
redid: remove_layer mimi_body (19 undo step(s), 0 more redo step(s))
```

### `restore_backup`

Lists the open project's backups, newest first, or puts one back.

- **Before every save,** the `project.json` being replaced is copied to the project's `backups` folder as `project-<time> before <edit>.json`, where `<edit>` is the change that was saved over it. Undo and redo are saves too. The last 20 are kept.
- **When `open_project` updates a file from an older ZeyoCut,** the file as it was stays there as `project-v<format>-<time>.json`, and these are never removed.
- **Backups hold `project.json` only,** not the assets. They outlast a restart, unlike the undo history.
- **Restoring is one undo step,** and the file it replaces is backed up like any other save.

| Parameter | Type | Notes |
|---|---|---|
| `backup` | string | Optional. A name from the list, with or without `.json`. Its time (`20261104-180857-779`) is enough. Omit it to list the backups |

```json
{}
```
```text
4 backup(s) in C:\Users\you\ZeyoCut\projects\tora-jumps\backups, newest first:
- project-20261104-180857-826 before remove_layer sfx_thud: 2026-11-04 18:08:57; 11 layer(s), 3s
- project-20261104-180857-817 before set_keyframes (1 entries): 2026-11-04 18:08:57; 11 layer(s), 3s
- project-20261104-180857-779 before update_layer title: 2026-11-04 18:08:57; 11 layer(s), 3s
- project-v1-20261104-180857: 2026-11-04 18:08:57, as format 1 saved it, before open_project updated it; 11 layer(s), 3s
restore one with restore_backup backup="<name>" (one undo step)
```

Going back to before the title was changed, which also brings back `sfx_thud`:

```json
{ "backup": "20261104-180857-779" }
```
```text
restored project-20261104-180857-779 before update_layer title (2026-11-04 18:08:57); undo puts back what was there
Tora Jumps: 1280x720 @ 30 fps, 3s, 11 layer(s), 9 asset(s)
clouds [image]  z=1  t=0–end  drawings=normal  anchor=(0,0)  keys: x(2)
ground [image]  z=2  t=0–end  drawings=normal  anchor=(0,0)
shadow [image]  z=3  t=0–end  drawings=normal  anchor=(120,25)  keys: x(2) scaleX(6) scaleY(6) opacity(3)
tora [image]  "Tora rig root"  z=8  t=0–end  null (no drawings)  anchor=(0,0)  keys: x(2) y(3) rotation(4)
└─ tora_body [image]  "Tora body"  z=10  t=0–end  drawings=normal  anchor=(110,234)  keys: scaleX(7) scaleY(7)
   ├─ tora_head [image]  "Tora head"  z=11  t=0–end  drawings=normal/happy  anchor=(120,196)  keys: y(7) rotation(7) drawing(3)
   └─ tora_tail [image]  "Tora tail"  z=9  t=0–end  drawings=normal  anchor=(164,72)  keys: rotation(13)
title [text]  z=20  t=0–end  text="Tora Jumps!"  font=Segoe UI 88px  anchor=(249.734,58.523)  keys: scaleX(2) scaleY(2) opacity(2)
subtitle [text]  z=21  t=0–end  text="トラのジャンプ"  font=Yu Gothic 40px  anchor=(140,32.041)  keys: y(2) opacity(2)
sfx_boing [audio]  t=0.9–end  asset=boing volume=0.8
sfx_thud [audio]  t=1.8–end  asset=thud volume=1
```
```text
undid: restore_backup project-20261104-180857-779 (3 more undo step(s), 1 redo step(s))
```

A time that fits more than one backup:

```json
{ "backup": "2026" }
```
```text
'2026' fits 5 backups (project-20261104-180857-920 before undo restore_backup project-20261104-180857-779, project-20261104-180857-903 before restore_backup project-20261104-180857-779, project-20261104-180857-826 before remove_layer sfx_thud, project-20261104-180857-817 before set_keyframes (1 entries), project-20261104-180857-779 before update_layer title); give more of its name
```

When `project.json` itself can't be read, `open_project` fails and names the newest backup to copy over it:

```text
invalid project JSON: Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed. Path: $.name | LineNumber: 0 | BytePositionInLine: 30.
the newest backup is backups\project-20261104-180857-920 before undo restore_backup project-20261104-180857-779.json (2026-11-04 18:08:57); copy it over project.json to go back to it
```

---

## Assets

### `import_asset`

Copies a file into `assets\` and registers it. Layers refer to assets by id.

- **Images:** png, jpg, jpeg, webp, bmp
- **Audio:** wav, mp3, m4a, aac, ogg, opus, flac. Importing audio needs ffprobe to read the duration.
- **Fonts:** ttf, otf, ttc

Importing an identical file again reuses the copy that's already there.

| Parameter | Type | Notes |
|---|---|---|
| `path` | string | Absolute path of the file |
| `id` | string | Optional. Default: derived from the file name (`Red Box.png` → `red_box`) |

```json
{ "path": "C:\\art\\tora_head_happy.png" }
```
```text
imported tora_head_happy [image] assets/tora_head_happy.png (240x208 px)
```
```json
{ "path": "C:\\audio\\boing.wav", "id": "boing" }
```
```text
imported boing [audio] assets/boing.wav (0.6s)
```

### `import_folder`

Imports every image, audio, font and video file in a folder whose name matches `pattern`, in order. It is the way to bring in a comic's panels or a shoot's clips in one call.

- **Only the folder itself** is read, not its subfolders. Hidden and system files are left out, and so are other types, which the reply lists. At most 500 files.
- **Order:** `name` sorts numbers by value, so `panel_2` comes before `panel_10`. `date` sorts by when each file last changed, oldest first. The reply lists the assets in that order.
- **At once, as one undo step:** images, audio, fonts and videos that need no normalizing. A file that can't be read is listed, and the rest are imported.
- **In the background:** videos that need normalizing (see [`import_video`](#import_video)), one after another in one `import_N` job. Each is added as an asset, as its own undo step, when it is done.
- **Ids** come from the file names, as with `import_asset`.

| Parameter | Default | Notes |
|---|---|---|
| `path` | required | Absolute path of the folder |
| `pattern` | `*` | A file name pattern with `*` and `?`, in any case, such as `*.png` or `panel_*` |
| `sortBy` | `name` | `name` or `date` |

```json
{ "path": "C:\\Users\\you\\comics\\episode 3" }
```
```text
imported 7 files from C:\Users\you\comics\episode 3 (by name), in this order:
- cover [image] assets/cover.jpg (1600x2400 px)
- narration [audio] assets/narration.wav (4.649s)
- panel_1 [image] assets/panel_1.png (1600x1200 px)
- panel_2 [image] assets/panel_2.png (1600x1200 px)
- panel_3 [image] assets/panel_3.png (1600x1200 px)
- panel_10 [image] assets/panel_10.png (1600x1200 px)
- panel_11 [image] assets/panel_11.png (1600x1200 px)
skipped 1 file of other types: script notes.txt
1 video needs normalizing, one after another in the background as import_1: phone_broll.mp4 (rotated 90° by metadata). Each is added as an asset when it is done; poll get_job_status with jobId "import_1"
```
`get_job_status` with `jobId` `import_1`:
```text
import_1: completed in 0.4s — import_folder: normalize 1 video from episode 3
imported 1 normalized video:
- phone_broll [video] assets/phone_broll.mp4 (720x1280 @ 30 fps, 3s, 90 frames, h264, no sound; normalized (rotated 90° by metadata))
```
Only the panels, in the order they were last changed:
```json
{ "path": "C:\\Users\\you\\comics\\episode 3", "pattern": "panel_*", "sortBy": "date" }
```
```text
imported 5 files from C:\Users\you\comics\episode 3 ('panel_*', by date), in this order:
- panel_10 [image] assets/panel_10.png (1600x1200 px)
- panel_2 [image] assets/panel_2.png (1600x1200 px)
- panel_1 [image] assets/panel_1.png (1600x1200 px)
- panel_3 [image] assets/panel_3.png (1600x1200 px)
- panel_11 [image] assets/panel_11.png (1600x1200 px)
```
```json
{ "path": "C:\\Users\\you\\comics\\episode 3", "pattern": "*.psd" }
```
```text
An error occurred invoking 'import_folder': no file in C:\Users\you\comics\episode 3 matches '*.psd'
```
```json
{ "path": "C:\\Users\\you\\comics\\episode 3\\cover.jpg" }
```
```text
An error occurred invoking 'import_folder': C:\Users\you\comics\episode 3\cover.jpg is a file; import_folder takes a folder (import_asset or import_video take one file)
```
```json
{ "path": "C:\\Users\\you\\comics\\episode 3", "sortBy": "size" }
```
```text
An error occurred invoking 'import_folder': unknown sortBy 'size'; valid values: name, date
```

### `list_assets`

Lists assets and the layers that use them. It takes no parameters.

```json
{}
```
```text
- tora_body [image] assets/tora_body.png (220x240 px); used by: tora_body
- tora_head [image] assets/tora_head.png (240x208 px); used by: tora_head
- tora_head_happy [image] assets/tora_head_happy.png (240x208 px); used by: tora_head
- tora_tail [image] assets/tora_tail.png (180x92 px); used by: tora_tail
- boing [audio] assets/boing.wav (0.6s); used by: boing
```

---

## Layers

Every visual layer (image, text or video) has these fields, which the `add_*` tools accept and `update_layer` changes:

| Field | Default | Notes |
|---|---|---|
| `id`, `name` | generated | |
| `parentId` | none | The layer then moves, rotates, scales, fades and hides with its parent |
| `x`, `y` | canvas center (top level) / parent's anchor (child) | Where the anchor lands in the parent's pixels |
| `anchorX`, `anchorY` | image or text-box center | Pivot in the layer's own pixels |
| `rotation` | 0 | Degrees clockwise around the anchor |
| `scaleX`, `scaleY` | 1 | Around the anchor |
| `opacity` | 1 | 0–1, multiplied down the hierarchy |
| `zIndex` | on top | Global draw order; higher is in front, and ties keep layer order. Negative is fine |
| `start`, `end` | 0, project end | Visible while `start ≤ t < end` |
| `visible` | true | |
| `language` | every version | A language tag such as `en`, `ja`, `my` or `pt-BR`: the layer and its children are only in that language's version. On a video, the language of its sound. See [Languages](#languages) |

### `add_image_layer`

Adds a cutout part, a still image or a **null layer**.

- Pass a single image as `asset`, or named drawings as `drawings` and swap them with keyframes on the `drawing` property.
- Pass neither to get a null layer: invisible, transform only, useful as a rig root.

Extra parameters:

- `drawing`: the drawing shown initially
- `flipX`: mirror around `anchorX`; children are not flipped

```json
{ "asset": "tora_body", "id": "tora_body", "x": 640, "y": 600, "anchorX": 110, "anchorY": 234 }
```
```text
added image layer 'tora_body', anchor (110,234), z=0
{"type":"image","id":"tora_body","name":"tora_body","start":0,"end":null,"zIndex":0,"parentId":null,"visible":true,"anchorX":110,"anchorY":234,"flipX":false,"drawings":{"normal":"tora_body"},"drawing":{"value":"normal"},"x":{"value":640},"y":{"value":600},"rotation":{"value":0},"scaleX":{"value":1},"scaleY":{"value":1},"opacity":{"value":1},"brightness":{"value":0},"contrast":{"value":1},"saturation":{"value":1}}
```

This example attaches a head at the neck. The anchor is the bottom of the head's neck, and `x`/`y` is the neck point inside the body image:
```json
{ "drawings": { "normal": "tora_head", "happy": "tora_head_happy" }, "id": "tora_head",
  "parentId": "tora_body", "x": 110, "y": 26, "anchorX": 120, "anchorY": 196 }
```
```text
added image layer 'tora_head' under 'tora_body', anchor (120,196), z=1
{"type":"image","id":"tora_head", ... ,"drawings":{"normal":"tora_head","happy":"tora_head_happy"},"drawing":{"value":"normal"}, ...}
```

### `add_text_layer`

Adds a title or subtitle. Text is shaped with HarfBuzz, and each layer uses one font with no fallback. The result reports the measured box size.

| Parameter | Default | Notes |
|---|---|---|
| `text` | required | `\n` for line breaks |
| `fontFamily` | `Segoe UI`, or the font of the layer's `language` | Installed family. Use `Myanmar Text` for Burmese and `Yu Gothic` for Japanese; a text tagged `my` or `ja` gets them by itself ([Languages](#languages)) |
| `fontAsset` | | Imported font asset id; overrides `fontFamily` |
| `bold` | false | Uses the bold face, or synthetic bold |
| `fontSize` | 48 | Pixels |
| `color` | `#000000` | |
| `strokeColor`, `strokeWidth` | none, 4 when a color is set | Outline outside the glyphs |
| `align` | `left` | `left`, `center` or `right` within the box |
| `maxWidth` | | Wrap width. Breaks at spaces first; Burmese syllables or Japanese characters (with kinsoku) only for phrases that don't fit |
| `lineHeight` | 1 | Multiple of the font's natural line spacing |
| `box` | | Background `{ "color": "#000000B3", "padding": 16, "cornerRadius": 0 }` |
| `style` | | A library style ([`save_style`](#save_style)). It fills every look parameter you leave out, scaled to the canvas |

```json
{ "text": "မင်္ဂလာပါ", "id": "subtitle_my", "fontFamily": "Myanmar Text", "fontSize": 56, "color": "#FFFFFF",
  "box": { "color": "#000000AA", "padding": 18, "cornerRadius": 12 }, "x": 640, "y": 660 }
```
```text
added text layer 'subtitle_my', box 203x104.207 px, anchor (101.5,52.104), z=2
{"type":"text","id":"subtitle_my", ... ,"text":"မင်္ဂလာပါ","fontFamily":"Myanmar Text","fontAsset":null,"bold":false,"fontSize":56,"color":"#FFFFFF","strokeColor":null,"strokeWidth":0,"align":"left","maxWidth":null,"lineHeight":1,"box":{"color":"#000000AA","padding":18,"cornerRadius":12}, ...}
```

A font that can't display the text is reported:
```json
{ "text": "こんにちは", "id": "subtitle_ja", "fontFamily": "Segoe UI", "fontSize": 40, "x": 640, "y": 80 }
```
```text
added text layer 'subtitle_ja', box 129.297x53.203 px, anchor (64.648,26.602), z=3
{...}
Warnings:
- font 'Segoe UI' has no glyphs for "こんにちは"; pick a font that covers this script (Burmese: "Myanmar Text", Japanese: "Yu Gothic")
```

### `add_audio_layer`

Adds voice, music or a sound effect from an audio asset. It is heard only in the export.

| Parameter | Default | Notes |
|---|---|---|
| `asset` | required | Audio asset id |
| `start` | 0 | Timeline position, seconds |
| `end` | | Cut at this timeline time |
| `trimIn`, `trimOut` | 0, end of file | Range of the source file to use |
| `volume` | 1 | Linear gain, 0–10 |
| `role` | `music` | `voice`, `music` or `effects`. With ducking on, music is lowered while voice plays |
| `fadeIn`, `fadeOut` | 0 | Seconds |
| `language` | every version | The sound is only in that [language's version](#languages), such as a dubbed voice |

```json
{ "asset": "music", "volume": 0.6, "fadeOut": 1 }
```
```text
added audio layer 'music' at 0s
{"type":"audio","id":"music","name":"music","start":0,"end":null,"zIndex":0,"parentId":null,"visible":true,"asset":"music","trimIn":0,"trimOut":null,"volume":0.6,"role":"music","fadeIn":0,"fadeOut":1}
```

### `update_layer`

Changes static fields. `changes` is an object of `field: value` using the names from `get_project`.

- For an animatable property, a number sets its static value. That value is only used while the property has no keyframes, and you get a warning if it has some.
- `"parentId": ""` unparents the layer.
- A nested object such as `box` or `drawings` is merged; `null` inside it removes a key.
- `id` and `type` can't be changed.
- `ignoreCamera: true` keeps a top-level layer and its children out of the [camera](#camera)'s movement.
- `rigRole` names a part for [actions](#library) (`head`, `arm_l`); `""` clears it. `root` is reserved for the rig's root.
- `language` puts the layer and its children in one [language's version](#languages); on a video it is the language of its sound. `""` puts it back in every version, and a tag is stored in its usual form (`pt_br` becomes `pt-BR`).

Fields by layer type:

- **Image:** `name rigRole start end zIndex parentId visible language anchorX anchorY ignoreCamera flipX drawings drawing x y rotation scaleX scaleY opacity brightness contrast saturation`
- **Text:** `name rigRole start end zIndex parentId visible language anchorX anchorY ignoreCamera text fontFamily fontAsset bold fontSize color strokeColor strokeWidth align maxWidth lineHeight box x y rotation scaleX scaleY opacity`
- **Audio:** `name start end zIndex parentId visible language asset trimIn trimOut volume role fadeIn fadeOut`. Audio layers can't have a parent, and `zIndex` has no effect on them.
- **Video:** `name rigRole start zIndex parentId visible language anchorX anchorY ignoreCamera asset trimIn trimOut speed volume muted role fadeIn fadeOut cropWidth cropHeight x y rotation scaleX scaleY opacity brightness contrast saturation cropX cropY`. A video layer has no `end`; its length is `(trimOut − trimIn) / speed`.
- **Redaction:** `name visible style strength paddingPercent feather holdFrames color`. A redaction hides in every language's version, and a subject cutout is in the versions of its sources, so neither has a `language`. Its tracks change with [`apply_redaction`](#apply_redaction), and its boxes with [`tracks_to_keyframes`](#tracks_to_keyframes) and the keyframe tools.

```json
{ "layerId": "tora_tail", "changes": { "zIndex": -1 } }
```
```text
updated layer 'tora_tail'
{"type":"image","id":"tora_tail", ... ,"zIndex":-1,"parentId":"tora_body", ...}
```
```json
{ "layerId": "mimi_body", "changes": { "y": 500 } }
```
```text
updated layer 'mimi_body'
{...}
Warnings:
- y has 3 keyframe(s), so the static value you set is only used after clear_keyframes
```
```json
{ "layerId": "tora_tail", "changes": { "colour": "#fff" } }
```
```text
An error occurred invoking 'update_layer': unknown field 'colour' for image layer 'tora_tail'; fields: name, rigRole, start, end, zIndex, parentId, visible, language, anchorX, anchorY, ignoreCamera, flipX, drawings, drawing, x, y, rotation, scaleX, scaleY, opacity, brightness, contrast, saturation
```

### `remove_layer`

Removes a layer. A layer with children is removed only together with them, when `withChildren` is true.

```json
{ "layerId": "mimi_body" }
```
```text
An error occurred invoking 'remove_layer': layer 'mimi_body' has child layers: mimi_head, mimi_tail. Pass withChildren=true to remove them too, or re-parent them first with update_layer
```
```json
{ "layerId": "mimi_body", "withChildren": true }
```
```text
removed: mimi_body, mimi_head, mimi_tail
```

### `duplicate_layer`

Copies a layer, by default with all its descendants (a whole rig) and their keyframes.

- New ids replace `replaceFrom` with `replaceTo` in ids and names. Otherwise they get a numeric suffix.
- Parameters:
    - `includeChildren` (default true)
    - `newParentId`: omit to keep the original's parent, `""` for none
    - `zIndexOffset`

```json
{ "layerId": "tora_body", "replaceFrom": "tora", "replaceTo": "mimi", "zIndexOffset": 10 }
```
```text
copied:
- tora_body -> mimi_body
- tora_head -> mimi_head
- tora_tail -> mimi_tail
```

### `list_layers`

Shows the parent/child tree with type, zIndex, time range, drawings, anchor and keyframed properties, and the [tracks](#tracking) of each clip (`tracks=`). A [redaction](#redaction) shows the tracks it hides (`hides=`) and its settings. Tagged layers show their [language](#languages) (`lang=`), and a line under the header lists the project's languages. It takes no parameters.

```json
{}
```
```text
tora-test: 1280x720 @ 30 fps, 3s, 6 layer(s), 5 asset(s)
tora_body [image]  z=0  t=0–end  drawings=normal  anchor=(110,234)  keys: y(3) scaleY(5)
├─ tora_head [image]  z=1  t=0–end  drawings=normal/happy  anchor=(120,196)  keys: drawing(2)
└─ tora_tail [image]  z=-1  t=0–end  drawings=normal  anchor=(164,72)
subtitle_my [text]  z=2  t=0–end  text="မင်္ဂလာပါ"  font=Myanmar Text 56px  anchor=(101.5,52.104)
subtitle_ja [text]  z=3  t=0–end  text="こんにちは"  font=Yu Gothic 40px  anchor=(100,32.041)
boing [audio]  t=0.5–end  asset=boing volume=0.8
```

---

## Animation

Properties are `x`, `y`, `rotation`, `scaleX`, `scaleY` and `opacity`, plus:

- `drawing` on image layers
- `brightness` (-1 to 1, 0 = unchanged, -1 = black), `contrast` and `saturation` (0 to 4, 1 = unchanged) on image and video layers
- `cropX`, `cropY` on video layers with a crop (source pixels), for example to follow a subject after `reframe_to_vertical`
- `shadow.x`, `shadow.y`, `shadow.blur`, `shadow.opacity`, `outline.width`, `blur.x`, `blur.y`, `tint.amount` on layers with that effect, and `mask.x`, `mask.y`, `mask.width`, `mask.height`, `mask.feather` on layers with a mask (see [Effects and motion](#effects-and-motion))
- `<track>.x`, `<track>.y`, `<track>.width`, `<track>.height` on a [redaction](#redaction) after `tracks_to_keyframes`, with time in the footage's own seconds

**Easing.** Each keyframe's `easing` shapes the segment from that keyframe to the next. The options are:

- `linear` (default)
- `hold`
- `easeIn`, `easeOut`, `easeInOut` (cubic)
- `easeOutBack` (overshoot)
- `easeOutBounce`
- `easeOutElastic`

**Holding values.** Before the first keyframe and after the last, a property holds that keyframe's value. `drawing` keyframes always hold; add one at time 0 for the starting drawing.

### `set_keyframes`

Adds or replaces many keyframes in one call. A keyframe at the same layer, property and time is replaced. If any entry is invalid, nothing changes and every problem is listed.

| Entry field | Notes |
|---|---|
| `layerId`, `property`, `time` | `layerId` `"camera"` animates the [camera](#camera) |
| `value` | A number, or a drawing name for `drawing` |
| `easing` | Optional, default `linear` |

This example sets squash before takeoff, a rise and fall, an easeOutBack landing and a face swap:
```json
{ "entries": [
  { "layerId": "tora_body", "property": "y", "time": 0.5, "value": 600, "easing": "easeOut" },
  { "layerId": "tora_body", "property": "y", "time": 0.9, "value": 380, "easing": "easeIn" },
  { "layerId": "tora_body", "property": "y", "time": 1.3, "value": 600 },
  { "layerId": "tora_body", "property": "scaleY", "time": 0.3, "value": 1, "easing": "easeOut" },
  { "layerId": "tora_body", "property": "scaleY", "time": 0.5, "value": 0.75, "easing": "easeInOut" },
  { "layerId": "tora_body", "property": "scaleY", "time": 0.6, "value": 1.15 },
  { "layerId": "tora_body", "property": "scaleY", "time": 1.3, "value": 0.8, "easing": "easeOutBack" },
  { "layerId": "tora_body", "property": "scaleY", "time": 1.7, "value": 1 },
  { "layerId": "tora_head", "property": "drawing", "time": 0.5, "value": "happy" },
  { "layerId": "tora_head", "property": "drawing", "time": 1.6, "value": "normal" }
] }
```
```text
set 10 keyframe(s) (10 added, 0 replaced) on tora_body.y, tora_body.scaleY, tora_head.drawing
```
```json
{ "entries": [
  { "layerId": "tora_tail", "property": "rotation", "time": 0, "value": 0, "easing": "bouncy" },
  { "layerId": "tora_hed", "property": "x", "time": 0, "value": 0 }
] }
```
```text
An error occurred invoking 'set_keyframes': 2 of 2 entries are invalid, so nothing was changed:
- entry 0 (tora_tail.rotation @ 0s): unknown easing 'bouncy'; valid values: linear, hold, easeIn, easeOut, easeInOut, easeOutBack, easeOutBounce, easeOutElastic
- entry 1 (tora_hed.x @ 0s): layer 'tora_hed' not found; existing layers: tora_body, tora_head, tora_tail, subtitle_my, subtitle_ja, boing
```

### `remove_keyframes`

Removes specific keyframes. If any of them doesn't exist, nothing changes.

```json
{ "entries": [ { "layerId": "mimi_body", "property": "scaleY", "time": 1.7 } ] }
```
```text
removed 1 keyframe(s)
```

### `clear_keyframes`

Removes every keyframe of one property, or of all properties of a layer (or of the camera) when `property` is omitted. The static values apply again; clearing a whole layer also takes its motion path off. On a redaction, `property` is a track id: its keyframed box goes, and the box worked out from the track applies again.

```json
{ "layerId": "mimi_body", "property": "y" }
```
```text
cleared 3 keyframe(s) from mimi_body.y
```
```json
{ "layerId": "subtitle_my" }
```
```text
An error occurred invoking 'clear_keyframes': subtitle_my has no keyframes; nothing was changed
```

---

## Camera

Each project has at most one camera. It moves the whole scene:

- `x`, `y` is the scene point shown at the center of the frame. The default is the canvas center, which shows the canvas as it is.
- `zoom` 2 shows half as much of the scene (twice as close), from 0.01 to 100.
- `rotation` turns the camera clockwise, so the picture turns the other way.

Layers with `ignoreCamera` (set it with `update_layer`) stay where they are, in screen space, together with their children. Use it for titles, watermarks and other overlays; `generate_captions` sets it on the caption group. Only top-level layers can have it.

- **Animating the camera:** `set_keyframes` with `layerId` `"camera"` and the properties `x`, `y`, `zoom` and `rotation`. `remove_keyframes` and `clear_keyframes` take `"camera"` too, so no layer can have that id.
- **Shake:** `camera_shake` writes the separate channels `shakeX`, `shakeY` (screen pixels) and `shakeRotation` (degrees). They add to the camera's own movement.
- **Editing:** ripple edits (`remove_range`, `remove_silences`, `cut_by_transcript`, `set_speed`, crossfades) move the camera's keyframes with the picture. A new canvas size keeps the camera on the canvas center.

```json
{ "layerId": "title", "changes": { "ignoreCamera": true } }
```
```text
updated layer 'title'
{"type":"text","id":"title", ... ,"anchorX":182.0546875,"anchorY":37.2421875,"ignoreCamera":true,"text":"Tora's big day", ...}
```
A push-in that ends on a tilted close-up:
```json
{ "entries": [
  { "layerId": "camera", "property": "zoom", "time": 0, "value": 1, "easing": "easeInOut" },
  { "layerId": "camera", "property": "zoom", "time": 2, "value": 2.2 },
  { "layerId": "camera", "property": "y", "time": 0, "value": 360, "easing": "easeInOut" },
  { "layerId": "camera", "property": "y", "time": 2, "value": 470 },
  { "layerId": "camera", "property": "rotation", "time": 2, "value": 0, "easing": "easeOutBack" },
  { "layerId": "camera", "property": "rotation", "time": 2.4, "value": -6 }
] }
```
```text
set 6 keyframe(s) (6 added, 0 replaced) on camera.zoom, camera.y, camera.rotation
```
```json
{ "entries": [ { "layerId": "camera", "property": "scaleX", "time": 0, "value": 1 } ] }
```
```text
An error occurred invoking 'set_keyframes': 1 of 1 entries are invalid, so nothing was changed:
- entry 0 (camera.scaleX @ 0s): unknown camera property 'scaleX'; camera properties: x, y, zoom, rotation, shakeX, shakeY, shakeRotation
```
`list_layers` shows the camera above the layers:
```text
tora-camera: 1280x720 @ 30 fps, 4s, 6 layer(s), 6 asset(s)
camera: x=640 y=360 zoom=1.5 rotation=0  keys: y(2) zoom(2) rotation(2) shakeX(13) shakeY(13) shakeRotation(13)
clouds [image]  z=-10  t=0–end  drawings=normal  anchor=(700,120)
...
title [text]  z=0  t=0–end  text="Tora's big day"  font=Segoe UI 56px  anchor=(182.055,37.242)  ignoreCamera
```

### `set_camera`

Sets the camera's fixed values. A property with keyframes keeps following them, and you get a warning. `reset: true` removes the camera with all its keyframes and shake.

| Parameter | Notes |
|---|---|
| `x`, `y` | Scene point at the frame's center, pixels |
| `zoom` | 0.01–100 |
| `rotation` | Degrees, clockwise camera roll |
| `reset` | Remove the camera first |

```json
{ "zoom": 1.2 }
```
```text
camera: zoom 1 → 1.2
camera: x=640 y=360 zoom=1.2 rotation=0
```
```json
{ "zoom": 1.5 }
```
```text
camera: zoom 1.2 → 1.5
camera: x=640 y=360 zoom=1.5 rotation=0  keys: y(2) zoom(2) rotation(2)
Warnings:
- camera zoom has 2 keyframe(s), so the value you set is only used after clear_keyframes
```
```json
{ "reset": true }
```
```text
camera: removed the camera with its keyframes and shake (the canvas is shown as is)
camera: none (the canvas is shown as is; set_camera, a camera keyframe or camera_shake adds one)
```

### `camera_shake`

Shakes the camera by writing keyframes on `shakeX`, `shakeY` and `shakeRotation`.

- The camera changes direction `frequency` times per second, with a random strength of 40–100% of `intensity`. The shake fades out to still at the end.
- The random numbers come from `seed`, so the same seed always gives the same shake. Use another seed for a different one.
- Earlier shake keys inside the time range are replaced. To remove a shake, use `undo` or `clear_keyframes` on `camera` `shakeX`/`shakeY`/`shakeRotation`.
- You get a warning when `frequency` is above half the frame rate, because some direction changes then fall between frames.

| Parameter | Default | Notes |
|---|---|---|
| `startTime`, `duration` | required | Seconds, at most 60 long |
| `intensity` | 20 | Strongest offset in screen pixels, at most 1000. Rotation shakes 0.05° per pixel |
| `frequency` | 12 | Direction changes per second, 0.5–60 |
| `seed` | 1 | |

```json
{ "startTime": 2.4, "duration": 0.8, "intensity": 24, "frequency": 14, "seed": 3 }
```
```text
camera shake 2.4–3.2s: 24 px, 14 direction changes per second, seed 3; 39 keyframe(s) on shakeX, shakeY, shakeRotation
```
```json
{ "layerId": "camera", "property": "shakeRotation" }
```
```text
cleared 13 keyframe(s) from camera.shakeRotation
```

---

## Effects and motion

Effects, masks and motion paths belong to one visual layer, not to its children. Ripple edits, splits and speed changes move their keyframes like any other, and `list_layers` shows them after `effects:`. The examples come from one Tora project at 1280x720, 24 fps, 6 s long, with the layers `clouds`, `ground`, `tora`, `title` and `arrow`.

### `set_effect`

Adds, changes or removes one effect on a layer. Fields you leave out keep their value; a new effect starts from the defaults. `remove: true` removes the effect with its keyframes.

| Effect | Fields (defaults) | What it does |
|---|---|---|
| `shadow` | `color` (#000000), `x` (8), `y` (8), `blur` (6), `opacity` (0.5) | A soft copy of the layer behind it, offset right and down |
| `outline` | `color` (#FFFFFF), `width` (4) | A border around the layer's shape, with rounded corners |
| `blur` | `x` (4), `y` (4) | Softens the layer; `x` alone gives a horizontal motion blur |
| `tint` | `color` (required), `amount` (0.5) | Mixes the layer's colors toward `color`; 1 paints it solid in that color, keeping its shape |

- **Sizes** are pixels at the layer's own scale, so they grow and shrink with the layer, its parents and the camera. `blur` values are the Gaussian's standard deviation, 0–500; `width` is 0–100; `opacity` and `amount` are 0–1.
- **Directions** are on screen: a shadow with `y` 14 falls straight down even when the layer is rotated.
- **Order:** the layer is drawn, masked, tinted, outlined, given its shadow, then blurred. The shadow is of the outlined shape, and a blur softens everything.
- **Animating:** in `set_keyframes` the numbers are `shadow.x`, `shadow.y`, `shadow.blur`, `shadow.opacity`, `outline.width`, `blur.x`, `blur.y` and `tint.amount`. Colors are fixed.

| Parameter | Default |
|---|---|
| `layerId`, `effect` | required |
| `color`, `x`, `y`, `blur`, `opacity`, `width`, `amount` | see the table above |
| `remove` | false |

```json
{ "layerId": "title", "effect": "outline", "color": "#1B1B1B", "width": 6 }
```
```text
added outline on 'title': color #1B1B1B, width 6
```
```json
{ "layerId": "title", "effect": "shadow" }
```
```text
added shadow on 'title': color #000000, x 8, y 8, blur 6, opacity 0.5
```
```json
{ "layerId": "tora", "effect": "shadow", "x": 0, "y": 14, "blur": 10, "opacity": 0.35 }
```
```text
added shadow on 'tora': color #000000, x 0, y 14, blur 10, opacity 0.35
```
```json
{ "layerId": "ground", "effect": "tint", "color": "#FF9A3C", "amount": 0.2 }
```
```text
added tint on 'ground': color #FF9A3C, amount 0.2
```
```json
{ "layerId": "clouds", "effect": "blur", "x": 3, "y": 3 }
```
```text
added blur on 'clouds': x 3, y 3
```
Animating the tint toward sunset and the shadow fading (`set_keyframes`), then setting a value that the keyframes hide:
```json
{ "entries": [
  { "layerId": "ground", "property": "tint.amount", "time": 0, "value": 0 },
  { "layerId": "ground", "property": "tint.amount", "time": 6, "value": 0.6 },
  { "layerId": "tora", "property": "shadow.opacity", "time": 2, "value": 0.35 },
  { "layerId": "tora", "property": "shadow.opacity", "time": 3, "value": 0.1, "easing": "easeInOut" }
] }
```
```text
set 4 keyframe(s) (4 added, 0 replaced) on ground.tint.amount, tora.shadow.opacity
```
```json
{ "layerId": "tora", "effect": "shadow", "opacity": 0.5 }
```
```text
updated shadow on 'tora': color #000000, x 0, y 14, blur 10, opacity animated (2 keyframes)
Warnings:
- shadow.opacity has 2 keyframe(s), so the value you set is only used after clear_keyframes
```
```json
{ "layerId": "tora", "effect": "outline", "x": 4 }
```
```text
An error occurred invoking 'set_effect': outline takes color, width; x is not for outline
```
```json
{ "layerId": "tora", "effect": "tint", "amount": 0.5 }
```
```text
An error occurred invoking 'set_effect': a new tint needs a color, e.g. #FF0000
```

### `set_mask`

Shows the layer only inside a shape, or only outside it with `invert: true`. Each layer has at most one mask.

- **Shapes:** `rectangle`, `ellipse`, or `image`, which uses an image asset's alpha stretched over the box.
- **The box** (`x`, `y` = top-left corner, `width`, `height`) is in the layer's own pixels, like its anchor, so the mask moves, turns and scales with the layer.
- **A new mask fits the layer's content** (the drawing, the text box or the video crop) unless you give the box. When that size isn't known, pass `width` and `height`.
- **`feather`** softens the edge over about that many pixels on each side, up to 1000.
- **Animating:** `mask.x`, `mask.y`, `mask.width`, `mask.height` and `mask.feather` in `set_keyframes`. A growing `mask.width` is a hand-made wipe.
- Fields you leave out keep their value. Changing `type` keeps the box; leaving `image` drops the asset. `remove: true` removes the mask with its keyframes.

| Parameter | Default |
|---|---|
| `layerId` | required |
| `type` | required for a new mask |
| `asset` | required for `image` |
| `x`, `y`, `width`, `height` | the layer's content |
| `feather` | 0 |
| `invert` | false |
| `remove` | false |

```json
{ "layerId": "clouds", "type": "ellipse", "feather": 40 }
```
```text
added the mask on 'clouds': ellipse at x 0, y 0, size 1400 x 240, feather 40
```
```json
{ "layerId": "clouds", "x": 100, "width": 1200 }
```
```text
updated the mask on 'clouds': ellipse at x 100, y 0, size 1200 x 240, feather 40
```
With `spot`, an image asset of a round opaque spot on a transparent square:
```json
{ "layerId": "tora", "type": "image", "asset": "spot", "x": 10, "y": 0, "width": 200, "height": 200, "invert": true }
```
```text
added the mask on 'tora': image from 'spot' at x 10, y 0, size 200 x 200, feather 0, inverted (shows the layer outside the shape)
```
```json
{ "layerId": "tora", "remove": true }
```
```text
removed the image mask from 'tora'
```
```json
{ "layerId": "tora", "asset": "spot" }
```
```text
An error occurred invoking 'set_mask': a new mask needs a type: rectangle, ellipse, image
```

### `set_motion_path`

Moves a layer along a curve through timed points. Its anchor reaches each point at the point's time, in the same pixel space as its `x`, `y` (the canvas for top-level layers). A new call replaces the earlier path.

- **Curves:** each segment is a cubic bezier. `inX`/`inY` and `outX`/`outY` are a point's handles, relative to the point. Handles you leave out are set for a smooth curve through the neighbors; one handle alone is mirrored.
- **Speed:** inside a segment the layer moves at an even speed along the curve. The `easing` of the point where a segment starts shapes it.
- **`autoOrient: true`** turns the layer with the path's direction, on top of its own `rotation`. Draw such a layer facing right.
- **The path replaces `x` and `y`.** Their keyframes are cleared, and their static values are set to the first point. Setting `x` or `y` keyframes later, `x`/`y` with `update_layer`, or `clear_keyframes` without a property takes the path off, with a warning.
- **Timing:** the path's timing is stored as `path` keyframes (0 at the first point, 1 at the second, …). `list_layers` shows them and ripple edits move them, but only this tool sets them. Before the first time and after the last, the layer holds at the end point.
- 2–1000 points, with increasing times. `remove: true` takes the path off; the layer goes back to its `x`, `y`.

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | required | |
| `points` | required | `[{time, x, y, inX, inY, outX, outY, easing}]`, handles and easing optional |
| `autoOrient` | false | |
| `remove` | false | |

```json
{ "layerId": "tora", "points": [
  { "time": 0, "x": 200, "y": 520, "easing": "easeInOut" },
  { "time": 2.5, "x": 640, "y": 420 },
  { "time": 5, "x": 1080, "y": 520 }
] }
```
```text
added a motion path on 'tora': 3 points, 0–5s, 905 px long
```
An arrow 90 px wide, facing right, swooping up and down along given handles:
```json
{ "layerId": "arrow", "points": [
  { "time": 0, "x": 100, "y": 300, "outX": 200, "outY": -250 },
  { "time": 3, "x": 700, "y": 300, "inX": -200, "inY": -250 },
  { "time": 6, "x": 1180, "y": 250 }
], "autoOrient": true }
```
```text
added a motion path on 'arrow': 3 points, 0–6s, 1288 px long, turning with the path
```
`list_layers` then shows every effect, mask and path:
```text
tora-moves: 1280x720 @ 24 fps, 6s, 5 layer(s), 7 asset(s)
clouds [image]  z=0  t=0–end  drawings=normal  anchor=(700,120)  effects: blur mask(ellipse)
ground [image]  z=1  t=0–end  drawings=normal  anchor=(640,70)  effects: tint  keys: tint.amount(2)
tora [image]  z=3  t=0–end  drawings=normal  anchor=(110,120)  effects: shadow path(3 points)  keys: shadow.opacity(2) path(3)
title [text]  z=10  t=0–end  text="Tora's day out"  font=Segoe UI 96px  anchor=(315.938,63.844)  effects: shadow outline
arrow [image]  z=4  t=0–end  drawings=normal  anchor=(45,12)  effects: path(3 points, autoOrient)  keys: path(3)
```
`x` keyframes take the path off (`set_keyframes`):
```json
{ "entries": [{ "layerId": "arrow", "property": "x", "time": 0, "value": 100 }] }
```
```text
set 1 keyframe(s) (1 added, 0 replaced) on arrow.x
Warnings:
- 'arrow' had a motion path, which overrides x and y; it was removed so x and y apply
```
```json
{ "layerId": "arrow", "points": [{ "time": 0, "x": 0, "y": 0 }, { "time": 0, "x": 5, "y": 5 }] }
```
```text
An error occurred invoking 'set_motion_path': point 1: time 0s must come after point 0 (0s)
```
```json
{ "entries": [{ "layerId": "arrow", "property": "path", "time": 1, "value": 1 }] }
```
```text
An error occurred invoking 'set_keyframes': 1 of 1 entries are invalid, so nothing was changed:
- entry 0 (arrow.path @ 1s): 'path' keyframes come from set_motion_path (each point's time and easing); call it again to change the timing, or with remove=true to take the path off
```

---

## System check

### `system_check`

Reports what this computer has for the AI features, and measures them. It takes no parameters.

- **At once:** the CPU, RAM and GPU, the ONNX Runtime version, and for each AI model whether it is downloaded (with its size and license) and the device it runs on.
- **In the background** (`syscheck_N`; on the laptop below, 75 s for two models and 137 s for all four, most of it waiting for DirectML on the two models it hung on): every downloaded model runs a few times on the CPU and on DirectML at 1080p (`aiImage` on one picture), and FFmpeg decodes a generated 1080p clip. The result gives each feature's time for a frame and for a minute of 1080p video at 30 fps.
- **DirectML is tried in a separate process,** stopped after 60 s, so a GPU driver that hangs or fails can't take the server down. On the laptop below, the image model made the Intel Arc driver fail in some runs and hang in others.
- **The results are saved** as `benchmark.json` in the library folder. Results measured on other hardware (another CPU or GPU) are ignored; run it again after installing a model or a new driver. They are used in two ways:
    - **`AiDevice` `auto`** (the default) runs a model on DirectML only where the benchmark found that it works and takes at most 80% of the CPU's time; otherwise on the CPU. Until system_check has run, every model runs on the CPU. `cpu` and `gpu` force one (see [README configuration](https://github.com/zeyarwinthu/zeyocut#configuration)).
    - **AI jobs estimate their time from them** (before a benchmark, from rough times, and they say so). A job that would take more than 5 minutes isn't started: see [Conventions](#conventions).

On a Core Ultra 9 285H laptop with an Intel Arc 140T GPU, both models downloaded:
```json
{}
```
```text
system check
- CPU: Intel(R) Core(TM) Ultra 9 285H, 16 logical processors (the AI models use 8 threads)
- RAM: 31.4 GB, 19.6 GB free
- GPU: Intel(R) Arc(TM) 140T GPU (16GB)
- ONNX Runtime 1.24.4 (DmlExecutionProvider, CPUExecutionProvider)
- AiDevice=auto: a model runs on DirectML only where the benchmark found it works and takes at most 80% of the CPU's time
- aiVideo matte: Robust Video Matting (MobileNetV3) (rvm_mobilenetv3_fp32.onnx, 15 MB, GPL-3.0): runs on the CPU (system_check hasn't measured it yet)
- aiImage matte: BiRefNet lite (birefnet_lite.onnx, 224 MB, MIT): runs on the CPU (system_check hasn't measured it yet)
- benchmarking 2 models and 1080p decoding in the background as syscheck_1 (DirectML in a separate process, stopped after 60 s per model); poll get_job_status with jobId "syscheck_1"
```
```text
syscheck_1: completed in 75.5s — system_check benchmark of 2 models and 1080p decoding
benchmark at 1920x1080, 30 fps:
- decoding 1080p video: 7.2 ms a frame
- aiVideo matte (Robust Video Matting (MobileNetV3)), a 1080p frame: CPU 82 ms; DirectML 81 ms; uses the CPU (DirectML wasn't fast enough: 81 ms against 82 ms); about 2.7 min per minute of 1080p video
- aiImage matte (BiRefNet lite), an image: CPU 5.12 s; DirectML: no answer within 60 s, so it was stopped (DirectML can hang with some GPU drivers); uses the CPU (DirectML didn't work); about 8 s per image
- saved to C:\Users\you\ZeyoCut\library\benchmark.json; AI jobs estimate their time from it, and AiDevice=auto picks each model's device with it
```
With the models of [`detect_and_track`](#detect_and_track) downloaded too, the benchmark also measures face detection, at all three sizes it searches, and plate detection, on the whole frame and its four corners. DirectML hung on the plate model too:
```text
- face detection (YuNet), a 1080p frame: CPU 38 ms; DirectML 89 ms; uses the CPU (DirectML wasn't fast enough: 89 ms against 38 ms); about 1.4 min per minute of 1080p video
- plate detection (open-image-models YOLOv9-t), a 1080p frame: CPU 142 ms; DirectML: no answer within 60 s, so it was stopped (DirectML can hang with some GPU drivers); uses the CPU (DirectML didn't work); about 4.5 min per minute of 1080p video
```
In another run, DirectML failed on the image model instead of hanging:
```text
- aiImage matte (BiRefNet lite), an image: CPU 4.62 s; DirectML: the GPU driver failed: 887A0020 An internal issue prevented the driver from carrying out the specified operation; uses the CPU (DirectML didn't work); about 8 s per image
```
Calling it again shows the devices the benchmark chose, and measures again:
```text
- aiVideo matte: Robust Video Matting (MobileNetV3) (rvm_mobilenetv3_fp32.onnx, 15 MB, GPL-3.0): runs on the CPU (DirectML wasn't fast enough in system_check: 81 ms against 82 ms)
- aiImage matte: BiRefNet lite (birefnet_lite.onnx, 224 MB, MIT): runs on the CPU (DirectML didn't work in system_check)
- last benchmark: 2026-11-03 23:45 UTC
- benchmarking 2 models and 1080p decoding in the background as syscheck_2 (DirectML in a separate process, stopped after 60 s per model); poll get_job_status with jobId "syscheck_2"
```
With the benchmark saved, a 4-minute 1080p interview (`guest`, 5760 frames at 24 fps) would take longer than 5 minutes:
```json
{ "layerId": "guest", "method": "aiVideo" }
```
```text
not started, and nothing was changed: the aiVideo matte (5760 frames of clip 'interview' with Robust Video Matting (MobileNetV3) on the CPU) would take about 8.6 min (measured by system_check), more than 5 minutes. Tell the user how long it takes first; to go ahead, call remove_background again with the same arguments and confirmLongJob=true.
```
Once the user agrees:
```json
{ "layerId": "guest", "method": "aiVideo", "confirmLongJob": true }
```
```text
removed the background of 'guest' with aiVideo
- the matte is computed in the background as matte_2: 5760 frames of clip 'interview' with Robust Video Matting (MobileNetV3) on the CPU, about 8.6 min (measured by system_check)
- until it is done, previews draw 'guest' with its background and exports refuse; poll get_job_status with jobId "matte_2"
- check the edges with render_frame checkerboard=true layerId="guest"; refine_matte shrinks or softens them
```

### `get_logs`

Reads the server's log, oldest first, to find out what happened earlier or why something failed.

- **The log** is a file per day, `zeyocut-yyyyMMdd.jsonl`, in the log folder (`%LOCALAPPDATA%\ZeyoCut\logs` by default; see the README's Configuration). Each line is one JSON object: `time`, `level`, `category`, `message`, the message's named values as `fields`, and `exception` when there is one.
- **Every tool call is logged** under `ZeyoCut.Tools`, with its arguments (the first 2000 characters) and how long it took, at `information`; a failed call at `warning`, with its error. The MCP SDK's own lines are kept to `warning` and above (`ModelContextProtocol` under `Logging:LogLevel` in `appsettings.json`). A tool's own error, such as a misspelled id, is only that warning. A failure inside ZeyoCut itself, a bug, is also logged by the SDK as an `error` with the stack, so `level` `error` finds those.
- **The server also logs** its start and settings, the jobs it started again or failed after a restart, and problems.
- **An exception** is shown with its first 6 lines; the file has all of it.

| Parameter | Default | Notes |
|---|---|---|
| `lastN` | 50 | 1–1000: how many of the newest matching entries to return |
| `level` | every level | The lowest level to include: `trace`, `debug`, `information`, `warning`, `error` or `critical` |
| `contains` | none | Only entries that contain this text, in any case: in the message, the category, a value or the exception |

After `update_layer` was called with a misspelled layer id:
```json
{"level": "warning"}
```
```text
all 1 log entry at warning or above in C:\Users\you\AppData\Local\ZeyoCut\logs, oldest first:
2026-11-04 18:39:38.060 [warning] ZeyoCut.Tools: update_layer failed after 2 ms: layer 'titel' not found; existing layers: title; arguments {"layerId":"titel","changes":{"text":"Summer Sale"}}
```
```json
{"contains": "render_frame", "lastN": 2}
```
```text
all 1 log entry containing "render_frame" in C:\Users\you\AppData\Local\ZeyoCut\logs, oldest first:
2026-11-04 18:39:38.055 [information] ZeyoCut.Tools: render_frame took 83 ms; arguments {"time":1.0}
```
```json
{"level": "loud"}
```
```text
An error occurred invoking 'get_logs': unknown level 'loud'; use trace, debug, information, warning, error, critical
```
The same `render_frame` call in `zeyocut-20261104.jsonl`:
```json
{"time":"2026-11-04T18:39:38.0556184+09:00","level":"information","category":"ZeyoCut.Tools","message":"render_frame took 83 ms; arguments {\"time\":1.0}","fields":{"Tool":"render_frame","Milliseconds":83,"Arguments":"{\"time\":1.0}"}}
```

---

## Background removal

`remove_background` cuts the background out of an image or video layer, so the layers behind it show through. The layer's picture is cut out by an alpha matte before anything else, so color adjustments, masks, effects, the camera and transitions all work on the cut-out picture. A layer has one matte at a time; it is part of the layer, so splits, copies and undo keep it, and `list_layers` shows it after `effects:` as `matte(...)`. `chroma` and `edgeFill` work on color; `aiImage` and `aiVideo` use AI models (see [AI methods](#ai-methods)). The examples come from one project at 1280x720, 24 fps, 4 s long: a sky and ground behind a presenter filmed against a green screen (`presenter`, a video layer) and a piece of generated art on white (`sticker`).

### `remove_background`

| Method | For | Settings (defaults) | How it works |
|---|---|---|---|
| `chroma` | image and video layers | `color` (picked from the picture's edges), `tolerance` (0.4), `softness` (0.2), `spill` (1) | Keys out the screen color while the layer is drawn, on every frame |
| `edgeFill` | image layers | `color` (the most common border color), `tolerance` (0.1) | Removes everything connected to the image's border that is close to the background color. Areas of that color enclosed by the art (the white of an eye) stay |
| `aiImage` | image layers | none | An AI model finds the subject of a photo or drawing. Computed in the background |
| `aiVideo` | video layers | none | An AI model follows people through a clip. Computed in the background |

- **chroma** compares colors by their hue and saturation, not their brightness, so shadows and uneven light on the screen key like the rest of it.
    - `tolerance` (0–1): how far from the screen color a pixel can be and still be removed.
    - `softness` (0–1): how gradually the edge goes from removed to kept.
    - `spill` (0–1): how much of the screen's tint is taken out of the pixels that stay (green fringes on hair and edges). It keeps their brightness.
    - Without `color`, the screen color is the most common strong color along the edges of the picture: the layer's drawing, or the clip's first frame. The reply says which color it picked.
- **edgeFill** suits art on a plain background. `tolerance` (0–1) is a color distance; raise it a little for noisy or compressed backgrounds. Pixels touching the removed area fade out over one more `tolerance`. Pixels that are already transparent count as background.
    - The matte is computed once per drawing and cached next to the asset as `<name>.matte-edgefill-<hash>.png` (white keeps, black removes). The hash covers the settings and the image file, so a new setting gets a new matte.
- Calling it again replaces the earlier removal (the reply says what it was). `refine_matte`'s shrink and feather carry over.
- `remove: true` takes the removal off.

| Parameter | Default |
|---|---|
| `layerId` | required |
| `method` | required unless `remove` is true: `chroma`, `edgeFill`, `aiImage` or `aiVideo` |
| `color`, `tolerance`, `softness`, `spill` | see the table above; giving one the method doesn't use is an error |
| `remove` | false |
| `confirmLongJob` | false: an AI job that would take more than 5 minutes isn't started, and the reply gives the estimate |

```json
{ "layerId": "presenter", "method": "chroma" }
```
```text
removed the background of 'presenter' with chroma (key #2DB649, tolerance 0.4, softness 0.2, spill 1)
- key color #2DB649 was picked from the edges of the picture; pass color to choose another
- at the clip's first frame (source 0s) 90.4% of the picture is keyed out; the key runs on every frame as it is drawn
- check the edges with render_frame checkerboard=true layerId="presenter"; refine_matte shrinks or softens them
```
```json
{ "layerId": "sticker", "method": "edgeFill" }
```
```text
removed the background of 'sticker' with edgeFill (background from the border, tolerance 0.1)
- drawing 'normal' (sticker): background #FFFFFF, 66.8% of the pixels removed; matte assets/sticker.matte-edgefill-9bf94a09.png
- check the edges with render_frame checkerboard=true layerId="sticker"; refine_matte shrinks or softens them
```
```json
{ "layerId": "sticker", "method": "edgeFill", "tolerance": 0.2 }
```
```text
removed the background of 'sticker' with edgeFill (background from the border, tolerance 0.2) (was edgeFill)
- drawing 'normal' (sticker): background #FFFFFF, 66.8% of the pixels removed; matte assets/sticker.matte-edgefill-b7c42aa3.png
- check the edges with render_frame checkerboard=true layerId="sticker"; refine_matte shrinks or softens them
```
`list_layers` then shows:
```text
keying: 1280x720 @ 24 fps, 4s, 4 layer(s), 4 asset(s)
sky [image]  z=0  t=0–end  drawings=normal  anchor=(700,120)
ground [image]  z=1  t=0–end  drawings=normal  anchor=(640,70)
presenter [video]  z=2  t=0–4  asset=greenscreen source=0–4s speed=1  volume=1  anchor=(640,360)  effects: matte(chroma, shrink 1, feather 2)
sticker [image]  z=3  t=0–end  drawings=normal  anchor=(200,200)  effects: matte(edgeFill)
```
Errors and taking it off:
```json
{ "layerId": "presenter", "method": "edgeFill" }
```
```text
An error occurred invoking 'remove_background': edgeFill is for image layers; use chroma or aiVideo on video layer 'presenter'
```
```json
{ "layerId": "sticker", "method": "edgeFill", "softness": 0.3 }
```
```text
An error occurred invoking 'remove_background': edgeFill takes color, tolerance; softness is not for edgeFill
```
```json
{ "layerId": "sky", "method": "chroma", "color": "#808080" }
```
```text
An error occurred invoking 'remove_background': key color #808080 is too close to gray to key on; use the screen's color, e.g. #00FF00 (green) or #0000FF (blue)
```
```json
{ "layerId": "sticker", "remove": true }
```
```text
took the background removal (edgeFill) off 'sticker'; it shows its whole picture again
```

### AI methods

`aiImage` and `aiVideo` find the subject with an AI model instead of a color, so they work on ordinary photos and footage: no screen, no plain background.

| Method | For | Model (download) | Good at |
|---|---|---|---|
| `aiImage` | image layers | BiRefNet lite, `birefnet_lite.onnx` (224 MB, MIT) | The main subject of a photo or drawing: a person, a product, an animal. A soft shadow under an object counts as part of it |
| `aiVideo` | video layers | Robust Video Matting (MobileNetV3), `rvm_mobilenetv3_fp32.onnx` (15 MB, GPL-3.0) | People, including hair and what they hold. It goes through the clip in order and carries what it saw from frame to frame, so the edge stays steady |

- **They take no settings.** `refine_matte` works on them like on the others.
- **They run in the background** as a job, `matte_N`. The removal applies at once, as one undo step. Until the job is done:
    - the layer is drawn with its background, and `render_frame` warns;
    - `check_project` lists the matte under missing files;
    - `start_export`, `export_batch`, `export_frames` and `export_thumbnail` refuse.
- **Each reply says how long the job should take.** One over 5 minutes isn't started without `confirmLongJob: true` (see [`system_check`](#system_check)).
- **Matte jobs run one at a time**, because each uses most of the CPU; a second one, or one started during a `system_check` benchmark, waits, and its status says so. On a Core Ultra 9 285H laptop, `aiVideo` did about 16 frames a second at 1280x720 and `aiImage` took about 4 s per picture, plus a few seconds to load the model. They run on the device `system_check` found best for the model (the CPU until it has run), unless `AiDevice` says otherwise (see [README configuration](https://github.com/zeyarwinthu/zeyocut#configuration)).
- **The models aren't part of ZeyoCut.** The first use downloads the model into the models folder (`library\models` unless `ModelsPath` says otherwise), checks it against a fixed SHA-256 checksum, and keeps it for next time. Without internet, download it yourself and save it there under the name above. Everything else works without the models.
- **Mattes are cached next to the asset**: `<name>.matte-aiimage-<hash>.png` per drawing, and `<name>.matte-aivideo-<hash>.mkv` for a clip. The clip's matte is a gray video of the whole file (at most 1920 px on the long side), so splits, trims and speed changes use it as it is. Asking again uses a matte that is already computed, or joins the job computing it.

The examples come from a second project at 1280x720, 24 fps, 4 s: a sky and ground behind a presenter clip (`presenter`, a portrait photo panned slowly, from the clip `talk`) and a product photo of a mug on a gray backdrop (`product`). The models folder starts with only the image model in it.
```json
{ "layerId": "presenter", "method": "aiVideo" }
```
```text
removed the background of 'presenter' with aiVideo
- the matte is computed in the background as matte_1: 96 frames of clip 'talk' with Robust Video Matting (MobileNetV3) on the CPU, about 13 s (a rough guess; system_check measures this computer); it first downloads rvm_mobilenetv3_fp32.onnx (15 MB, GPL-3.0) into C:\Users\you\ZeyoCut\library\models
- until it is done, previews draw 'presenter' with its background and exports refuse; poll get_job_status with jobId "matte_1"
- check the edges with render_frame checkerboard=true layerId="presenter"; refine_matte shrinks or softens them
```
Until the job is done, `check_project` reports the matte and the exports refuse:
```text
check_project: 1 problem(s) in 'aimattes' (96 frames checked)
missing files (1):
- layer 'presenter': the aiVideo matte of 'talk' isn't computed yet (assets/talk.matte-aivideo-f49a5212.mkv), so the layer is drawn with its background and exports refuse; wait for its matte job (get_job_status) or run remove_background again
```
```json
{ "fileName": "early.mp4" }
```
```text
An error occurred invoking 'start_export': background removal isn't finished: layer 'presenter' (aiVideo matte of 'talk'); wait for its matte job (get_job_status) or run remove_background again, then export
```
```json
{ "layerId": "product", "method": "aiImage" }
```
```text
removed the background of 'product' with aiImage
- the matte is computed in the background as matte_2: drawing 'normal' (mug) with BiRefNet lite on the CPU, about 10 s (a rough guess; system_check measures this computer)
- until it is done, previews draw 'product' with its background and exports refuse; poll get_job_status with jobId "matte_2"
- check the edges with render_frame checkerboard=true layerId="product"; refine_matte shrinks or softens them
```
`get_job_status` with `matte_1` after the download (15 MB took under a second), while it waits for `matte_2`, and while it works:
```text
matte_1: running (waiting for another AI job to finish) 10%, 0.9s elapsed — aiVideo matte of 'presenter' (talk, 96 frames)
matte_1: running (matting frame 4 of 96) 13%, 7.9s elapsed — aiVideo matte of 'presenter' (talk, 96 frames)
```
When the jobs are done:
```text
matte_1: completed in 13.6s — aiVideo matte of 'presenter' (talk, 96 frames)
computed the aiVideo matte of 'presenter' with Robust Video Matting (MobileNetV3) on the CPU: 96 frames at 1280x720 in 6.1 s (15.7 frames/s); about 59.8% of the picture is background
- matte assets/talk.matte-aivideo-f49a5212.mkv
```
```text
matte_2: completed in 7.0s — aiImage matte of 'product' (1 drawing(s))
computed the aiImage matte of 'product' with BiRefNet lite on the CPU in 3.9 s
- drawing 'normal' (mug): about 80.5% of the picture is background; matte assets/mug.matte-aiimage-4ecb0478.png
```
Asking again uses the computed matte:
```json
{ "layerId": "presenter", "method": "aiVideo" }
```
```text
removed the background of 'presenter' with aiVideo (was aiVideo)
- clip 'talk': the matte is already computed: assets/talk.matte-aivideo-f49a5212.mkv
- check the edges with render_frame checkerboard=true layerId="presenter"; refine_matte shrinks or softens them
```
`list_layers` shows them as `matte(aiVideo)` and `matte(aiImage)`. Errors:
```json
{ "layerId": "product", "method": "aiVideo" }
```
```text
An error occurred invoking 'remove_background': aiVideo is for video layers; use aiImage, edgeFill or chroma on image layer 'product'
```
```json
{ "layerId": "product", "method": "aiImage", "tolerance": 0.2 }
```
```text
An error occurred invoking 'remove_background': aiImage takes no settings; tolerance is not for aiImage
```

### `refine_matte`

Fixes the edge of a removed background.

- `shrink` pulls the edge in by that many pixels, to cut off a halo of the old background. A negative value grows it back. It works in whole pixels.
- `feather` softens the edge over about that many pixels.

Both are in the layer's own pixels, so they scale with the layer. A subject cut off by the frame keeps its edge at the frame: the frame's border isn't treated as background. Values you leave out stay.

| Parameter | Default |
|---|---|
| `layerId` | required: a layer with a removed background |
| `shrink` | unchanged; -100 to 100 |
| `feather` | unchanged; 0 to 100 |

```json
{ "layerId": "presenter", "shrink": 1, "feather": 2 }
```
```text
refined the matte of 'presenter': shrink 1 px, feather 2 px
```
```json
{ "layerId": "sky", "shrink": 2 }
```
```text
An error occurred invoking 'refine_matte': 'sky' has no matte to refine; remove its background with remove_background first
```

## Text behind the subject

A title behind a person: the text stays in front of the background, and the person is drawn again on top of it, cut out. `text_behind_subject` sets that up. `text_occlusion` measures how much of a text ends up covered.

The examples come from a project at 1280x720, 24 fps, 4 s: a presenter clip that fills the frame (`presenter`, a portrait photo panned slowly, from the clip `talk`), a big white title `title` ("ORBIT") over the presenter's head, and a smaller `tagline` that was added with zIndex -1, so it was hidden under the clip.

### `text_behind_subject`

One undo step. What it does depends on the subject:

| Subject | What happens |
|---|---|
| A video layer | A **subject cutout** layer (type `cutout`) is added directly above the texts. It draws the clip again, cut out with its own matte: `aiVideo` by default, or `chroma` for a green screen |
| An image layer | The same, with `aiImage` by default, or `edgeFill` or `chroma` |
| A layer of its own already: a removed background, a picture with a transparent background, or a group (null layer) | No cutout. The texts move directly below the subject's lowest layer |

The cutout is not edited by itself:

- **It follows its source.** The source is the clip and any pieces splits made of it on the same track (same asset, zIndex and parent). Timing, trims, speed, transforms, effects, masks and keyframes all come from the source, so make those changes on the clip. Splits, cuts and ripples carry over; removing the clip removes the cutout.
- **Its own settings are only** `name`, `zIndex`, `visible` and `opacity` (`opacity` can have keyframes). Other edits, and `remove_background`, refuse with a message that names the source. `refine_matte` works on it, and `render_frame` with `layerId` set to the cutout draws the subject alone.
- **Outline and shadow** of the source aren't drawn again; tint, color, blur and masks are.

Texts drawn below the clip are lifted just above it. Other layers drawn between the clip and the cutout are named, since the subject covers them too. With an AI method the matte is computed in the background as a `matte_N` job, as with [`remove_background`](#ai-methods). Until it is done, the text shows in front in previews, `check_project` lists the matte, and exports refuse. Running it again on the same clip reuses its cutout; a new `method` replaces its matte and keeps `refine_matte`'s shrink and feather. Captions can go behind the subject as they are made, with a cutout of their own: see [`generate_captions`](#captions-behind-the-subject) `behindSubject`.

| Parameter | Default |
|---|---|
| `videoLayerId` | required: the video or image layer with the subject, or a group whose layers are the subject |
| `textLayerIds` | required: text layers; a group (like `captions`) stands for the text layers under it |
| `method` | `aiVideo` for video, `aiImage` for images; unchanged for an existing cutout. Also `chroma` or `edgeFill` (images) |
| `color` | `chroma`: picked from the picture's edges; `edgeFill`: the most common border color |
| `tolerance` | 0.4 (`chroma`), 0.1 (`edgeFill`) |
| `confirmLongJob` | false: an AI matte that would take more than 5 minutes isn't started, and the reply gives the estimate |

```json
{ "videoLayerId": "presenter", "textLayerIds": ["title", "tagline"] }
```
```text
put 'title', 'tagline' behind the subject of 'presenter': cutout 'presenter_subject' draws the subject again above the text, cut out with aiVideo (zIndex 1)
- the cutout follows 'presenter': its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity
- 'tagline' was drawn below 'presenter', so it now draws just above it
- the matte is computed in the background as matte_1: 96 frames of clip 'talk' with Robust Video Matting (MobileNetV3) on the CPU, about 13 s (a rough guess; system_check measures this computer)
- until it is done, the subject isn't drawn over the text in previews yet (the text shows in front) and exports refuse; poll get_job_status with jobId "matte_1"
- check it with render_frame (layerId="presenter_subject" checkerboard=true shows the subject alone; refine_matte shrinks or softens its edge); text_occlusion measures how much of each text it covers
```
While the job runs, `render_frame` warns, `check_project` lists the matte and `start_export` refuses:
```text
Warnings:
- cutout 'presenter_subject': the aiVideo matte of 'talk' isn't computed yet, so the subject of 'presenter' isn't drawn over the text yet; get_job_status shows its matte job, or run text_behind_subject again
```
```text
check_project: 1 problem(s) in 'behind' (96 frames checked)
missing files (1):
- cutout 'presenter_subject': the aiVideo matte of 'talk' isn't computed yet (assets/talk.matte-aivideo-b5c2282b.mkv), so the subject isn't drawn over the text and exports refuse; wait for its matte job (get_job_status) or run text_behind_subject again
```
```text
An error occurred invoking 'start_export': background removal isn't finished: cutout 'presenter_subject' (aiVideo matte of 'talk'); wait for its matte job (get_job_status) or run text_behind_subject again, then export
```
```text
matte_1: completed in 5.7s — aiVideo matte of cutout 'presenter_subject' (talk, 96 frames)
computed the aiVideo matte of cutout 'presenter_subject' with Robust Video Matting (MobileNetV3) on the CPU: 96 frames at 1280x720 in 5.6 s (17.3 frames/s); about 59.8% of the picture is background
- matte assets/talk.matte-aivideo-b5c2282b.mkv
```
`list_layers` shows the cutout with what it follows:
```text
presenter [video]  z=0  t=0–4  asset=talk source=0–4s speed=1  volume=1  anchor=(640,360)
tagline [text]  z=0  t=0–end  text="a week on the station"  font=Segoe UI 56px  anchor=(264.906,37.242)
title [text]  z=1  t=0–end  text="ORBIT"  font=Segoe UI 300px  anchor=(442.969,199.512)
presenter_subject [cutout]  z=1  follows=presenter  effects: matte(aiVideo)
```
Its own settings change as usual; others refuse:
```json
{ "layerId": "presenter_subject", "changes": { "x": 100 } }
```
```text
An error occurred invoking 'update_layer': 'presenter_subject' is the subject cutout of 'presenter'; it always follows 'presenter', so make this change on 'presenter' (its own settings are only name, zIndex, visible and opacity)
```
After `split_layer` on `presenter` at 2 s, the cutout follows both pieces (`list_layers`):
```text
presenter_subject [cutout]  z=1  follows=presenter/presenter_2  opacity=0.9  effects: matte(aiVideo)
```
Running it again uses the same cutout and the computed matte:
```json
{ "videoLayerId": "presenter", "textLayerIds": ["title"] }
```
```text
put 'title' behind the subject of 'presenter' with its cutout 'presenter_subject' (aiVideo; zIndex 1)
- the cutout follows 'presenter' and 'presenter_2' (another piece of the same clip on its track): its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity
- 'tagline' is behind the subject too
- clip 'talk': the matte is already computed: assets/talk.matte-aivideo-b5c2282b.mkv
- check it with render_frame (layerId="presenter_subject" checkerboard=true shows the subject alone; refine_matte shrinks or softens its edge); text_occlusion measures how much of each text it covers
```
A subject that is already its own layer, here a mug photo (`product`) whose background was removed with `edgeFill`, with a price behind it:
```json
{ "videoLayerId": "product", "textLayerIds": ["price"] }
```
```text
put 'price' behind 'product'
- 'product' already has its background removed (edgeFill), so it is its own subject and needs no cutout: the text now draws directly below 'product' (zIndex 2)
```
```json
{ "videoLayerId": "title", "textLayerIds": ["tagline"] }
```
```text
An error occurred invoking 'text_behind_subject': 'title' is a text layer; videoLayerId takes the clip (or picture, or group) whose subject goes in front of the text
```

### `text_occlusion`

How much of a text layer what is drawn above it hides, frame by frame. Each frame is drawn twice: the text alone, and only the layers above it. The share covered is the text's pixels weighted by how opaque the layers above are, so a half-transparent layer counts half. Frames where the text isn't shown (outside its time, hidden, fully transparent) are left out of the numbers.

The reply gives the min, max and average, then the frames in runs whose values stay within 1 percentage point. Every frame is saved to `renders\text_occlusion_<id>_<first>-<last>.json`. Up to 150 frames answers directly; longer ranges run in the background as `occlusion_N`. It refuses while a background removal is still being computed.

| Parameter | Default |
|---|---|
| `textLayerId` | required |
| `startTime` | the text's start |
| `endTime` | the text's end (not included) |

Before `text_behind_subject`, nothing covers the title:
```json
{ "textLayerId": "title" }
```
```text
text_occlusion 'title' 0–3.958s (f0–f95, 96 frames): covered min 0.0%, max 0.0%, average 0.0%
- f0–f95 (0–3.958s): 0.0%
per frame: C:\Users\you\ZeyoCut\projects\behind\renders\text_occlusion_title_00000-00095.json
```
After it, the presenter's head covers about a third of it:
```text
text_occlusion 'title' 0–3.958s (f0–f95, 96 frames): covered min 35.0%, max 38.9%, average 36.5%
- f0–f12 (0–0.5s): 38.0–38.9%
- f13–f30 (0.542–1.25s): 36.9–37.9%
- f31–f48 (1.292–2s): 35.9–36.9%
- f49–f87 (2.042–3.625s): 35.0–36.0%
- f88–f94 (3.667–3.917s): 36.1–37.0%
- f95 (3.958s): 37.2%
per frame: C:\Users\you\ZeyoCut\projects\behind\renders\text_occlusion_title_00000-00095.json
```
```json
{ "textLayerId": "tagline", "startTime": 1, "endTime": 1.5 }
```
```text
text_occlusion 'tagline' 1–1.458s (f24–f35, 12 frames): covered min 61.6%, max 77.1%, average 68.9%
- f24 (1s): 61.6%
- f25–f26 (1.042–1.083s): 62.6%
- f27 (1.125s): 64.0%
- f28 (1.167s): 65.6%
- f29 (1.208s): 68.1%
- f30 (1.25s): 70.6%
- f31–f32 (1.292–1.333s): 72.4%
- f33 (1.375s): 74.0%
- f34 (1.417s): 75.4%
- f35 (1.458s): 77.1%
per frame: C:\Users\you\ZeyoCut\projects\behind\renders\text_occlusion_tagline_00024-00035.json
```
```json
{ "textLayerId": "presenter" }
```
```text
An error occurred invoking 'text_occlusion': 'presenter' is a video layer; text_occlusion measures text layers
```

## Tracking

`track_object` follows an object, such as a face, a sign or a car, through a clip, and saves its box on every frame as a **track**:

- **A track belongs to the footage, not the timeline.** Its frames and pixels are the clip's own source frames and pixels. Trims, speed changes and splits never break it, and every clip of the same footage (the pieces of a split, for example) shares it. `list_layers` shows `tracks=` on each clip that plays some of it.
- **You draw the box on the canvas,** in the pixels `render_frame` shows. It is mapped into the clip through its placement, parents, the camera and its crop, and cut to the part of the picture the clip shows.
- **Every frame has a confidence** from 0 to 1: how well the middle of the starting picture still matches inside the box. Spans under 0.5 are listed as possible drift.
    - Something passing in front of the object lowers it too, so look before correcting. `render_frame` with `debug: true` draws each track's box with its id and confidence, in red under 0.5.
    - Where the tracker loses the object, the box stays where it was last seen until the object is found again.
- **Up to 150 frames, the reply comes when the tracking is done.** Longer runs are a `tracking_N` job (poll `get_job_status`) that adds the track, or the correction, as its own undo step. On a Core Ultra 9 285H laptop, `csrt` followed a 380x420 face in 1280x720 video at about 10 frames a second.
- **The boxes are saved in the project's `tracks\` folder.** Each change writes a new file, so `undo` and `redo` bring the boxes back too.
- **[`detect_and_track`](#detect_and_track) finds every face or license plate in a clip** and makes a track of each, the same kind of track.
- **[`attach_to_track`](#attach_to_track) keeps a text or image layer on a track's box.**

| Method | How it follows | Use it for |
|---|---|---|
| `csrt` (default) | OpenCV's CSRT tracker | most things; it follows changes of shape and size, which can wobble a little (33 to 44 px on a 40 px test square) |
| `template` | looks for the starting picture near the last box | things that don't turn or change size; the fastest |
| `opticalFlow` | moves the box with the points inside it and scales it with their spread | textured things |

In the tests, all three kept the box's center within 3 px of a moving square on every frame, and all three reported the frames where a gray patch hid it as possible drift.

### `track_object`

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | required | A video layer |
| `time` | required | Timeline seconds of the frame the box is drawn on; snaps to the nearest frame, like `render_frame` |
| `box` | required | `{ "x", "y", "width", "height" }` in canvas pixels. At least 4 source pixels each way, and mostly inside the clip's picture |
| `direction` | `forward` | `forward` to the end of the clip's trimmed range, `backward` to its start, or `both` |
| `method` | `csrt` | `csrt`, `template` or `opticalFlow` |
| `id` | `track_N` | |

A presenter's face, with someone walking past between 1.7 and 2.7 s:
```json
{ "layerId": "guest", "time": 0, "box": { "x": 620, "y": 120, "width": 380, "height": 420 } }
```
```text
tracked the box on 'guest' as track_1 with csrt, forward
- frames 0–95 of 'walk' (96 frames): 0–3.958s on 'guest'
- the box in source pixels: 620,120 380x420 at frame 0, 445,60 428x473 at frame 95
- confidence: average 0.93, lowest 0.15 at frame 52 (2.167s on 'guest')
- possible drift (confidence under 0.5): 2.125–2.25s on 'guest' (frames 51–54, lowest 0.15)
- look at those frames with render_frame debug=true (it draws the box); where it is off the object, correct it with reanchor_track trackId="track_1"
```
`render_frame` with `{ "time": 2.25, "maxWidth": 640, "debug": true }` shows the box, labeled `track_1 0.48` in red, still on the face: the walker hides half of it. Nothing to correct there.

Over 150 frames, the track comes from a background job (a 10 s clip placed at 4 s):
```json
{ "layerId": "walk2", "time": 4, "box": { "x": 620, "y": 120, "width": 380, "height": 420 }, "direction": "forward" }
```
```text
tracking the box on 'walk2' as track_2 with csrt, forward, in the background as tracking_1: 239 frames of 'longwalk' (frames 0–239)
- drawn at 4s: frame 0, 620,120 380x420 in the source pixels of 'longwalk'
- poll get_job_status with jobId "tracking_1"; the track is added as its own undo step when it is done
```
```text
tracking_1: completed in 22.9s — track_object track_2 (csrt, 239 frames of 'longwalk')
tracked the box on 'walk2' as track_2 with csrt, forward
- frames 0–239 of 'longwalk' (240 frames): 4–13.958s on 'walk2'
- the box in source pixels: 620,120 380x420 at frame 0, 326,21 380x420 at frame 239
- confidence: average 0.97, lowest 0.04 at frame 52 (6.167s on 'walk2')
- possible drift (confidence under 0.5): 6.125–6.208s on 'walk2' (frames 51–53, lowest 0.04)
- look at those frames with render_frame debug=true (it draws the box); where it is off the object, correct it with reanchor_track trackId="track_2"
```
`undo` then takes the track off on its own: `undid: track_object track_2 (6 more undo step(s), 1 redo step(s))`.

A box outside the clip's picture:
```text
An error occurred invoking 'track_object': the box 1250,100 200x200 is mostly outside clip 'guest''s picture at 0s, or under 4 source pixels; check where it is with render_frame
```

### `reanchor_track`

Corrects a track where it went wrong: sets the box at `time` and tracks again from there.

- **It tracks away from the frame `track_object` started at,** up to the next box set by hand or the end of the track. Frames on the other side stay as they are.
- **Time and box are mapped through the clip the track was made on.** `layerId` maps them through another clip of the same footage instead, such as a piece split off it.
- **Tracks found by `detect_and_track` can't be corrected this way,** since they weren't followed from a box: see [`detect_and_track`](#detect_and_track).

| Parameter | Default | Notes |
|---|---|---|
| `trackId` | required | |
| `time` | required | Timeline seconds; snaps to the nearest frame |
| `box` | required | Canvas pixels, as for `track_object` |
| `layerId` | the clip the track was made on | Another clip of the same footage |

In the example above, `csrt` had let the box grow to 428x473 by the end. Drawing it again once the walker had passed:
```json
{ "trackId": "track_1", "time": 2.75, "box": { "x": 510, "y": 92, "width": 380, "height": 420 } }
```
```text
reanchored track_1 at frame 66 (2.75s on 'guest') and tracked frames 66–95 again with csrt; its other frames stayed as they were
- frames 0–95 of 'walk' (96 frames): 0–3.958s on 'guest'
- the box in source pixels: 620,120 380x420 at frame 0, 465,82 380x420 at frame 95
- confidence: average 0.94, lowest 0.15 at frame 52 (2.167s on 'guest')
- possible drift (confidence under 0.5): 2.125–2.25s on 'guest' (frames 51–54, lowest 0.15)
- look at those frames with render_frame debug=true (it draws the box); where it is off the object, correct it with reanchor_track trackId="track_1"
```
The face's own box at 3.958 s is about 460,80 380x420. The span at 2.125–2.25 s stays listed: it is the walker, before the corrected frame.

`list_layers` then shows the track on the clip:
```text
tracking: 1280x720 @ 24 fps, 4s, 1 layer(s), 1 asset(s)
guest [video]  z=0  t=0–4  asset=walk source=0–4s speed=1  volume=1  anchor=(640,360)  tracks=track_1
```

A time where the track's clip isn't on the timeline:
```text
An error occurred invoking 'reanchor_track': clip 'walk2' isn't on the timeline at 1s (it plays 4–14s)
```

### `detect_and_track`

Finds every face or license plate in a clip with an AI model and links each one into a track, `face_1`, `face_2` and so on or `plate_1`, `plate_2`, to hide with [`apply_redaction`](#apply_redaction):

- **The models** are downloaded the first time each is used:
    - **Faces:** YuNet from OpenCV Zoo (MIT, 230 KB).
    - **Plates:** the YOLOv9-t plate model of [open-image-models](https://github.com/ankandrew/open-image-models) (8 MB). Its code is MIT, but the weights' license isn't stated: see [AI models](https://github.com/zeyarwinthu/zeyocut#ai-models).
- **Every frame is searched,** from `startTime` to `endTime` (by default the whole clip), at the size tracking uses: at most 1920 px on the long side. Footage over 2560 px is read from its [proxy](#import_video) once it is made, which decodes twice as fast; the reply then says `at 1920x1080 (its proxy)`.
    - YuNet finds faces of about 10 to 300 px, so each frame is also searched at a third and an eighth of that size. Faces from about 10 px across up to the whole frame are found.
    - The plate model sees the frame shrunk to 640 px, so each corner of the frame is searched too, in four tiles that overlap. Plates down to about 2% of the frame's width are found, 40 px in 1080p. A plate cut by a tile's edge is taken whole from the tile beside it or from the whole frame.
    - Boxes of the same face or plate from different passes are merged.
- **Boxes are linked from frame to frame like ByteTrack:**
    - A detection at `minConfidence` or more (by default 0.6 for faces, 0.3 for plates) starts a track. The track is kept only if another confident detection follows on the next frame.
    - Weaker detections, down to half of `minConfidence`, keep a track going only if it was found on the frame before.
    - A track that was missed can also take a confident detection near where it was last seen, or near its largest box of the last second. Going behind something, a face or plate is found in ever smaller boxes, and coming out, it is often found in part first.
    - A track ends after 1 s without a detection.
- **Nobody is recognized and no plate is read.** Only boxes are compared, so someone or a car gone for more than 1 s comes back as a new track. Choose tracks by their ids on the contact sheet.
- **Detections on fewer than 3 frames are left out** and listed with their times, so you can look at them.
- **Frames a track was missed on** keep the box before them. The reply lists them, and the redaction's [fail-safe](#redaction) fills them.
- **When the `detect_N` job is done,** the tracks are added as one undo step. `get_job_status` then lists each track's span, how many frames it was found on, where it was missed, and its average size and confidence. It also shows a contact sheet with one labeled picture per track, saved in `renders\`.
- **The tracks work like `track_object`'s:** they are in the footage's own frames and pixels, and `list_layers` and `render_frame` with `debug: true` show them. `reanchor_track` can't correct them, since they weren't followed from a box. Where a face or plate was missed, track it with `track_object` and hide that track too.
- **It estimates its time before starting,** like the other AI jobs. On a Core Ultra 9 285H laptop, on the CPU, YuNet took 40 ms per 1080p frame, about 1.4 minutes per minute of 1080p video, and the plate model 142 ms for its five passes, about 4.5 minutes. The 1280x720 examples below ran at 46 and 6.7 frames a second.

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | required | The video layer (clip) to search |
| `target` | required | `faces` or `plates` |
| `startTime` | the clip's start | Timeline seconds |
| `endTime` | the clip's end | Timeline seconds |
| `minConfidence` | 0.6 for faces, 0.3 for plates | Above 0, at most 1. Lower finds more, and more false ones |
| `confirmLongJob` | false | Start even if it would take more than 5 minutes |

The STS-135 crew (a public-domain NASA photo) panned slowly in a 4 s 1280x720 clip, `crew`, with a dark bar crossing it from 1.7 to 2.7 s:
```json
{ "layerId": "crew", "target": "faces" }
```
```text
finding faces on 'crew' in the background as detect_1: finding faces on 96 frames of 'crew' at 1280x720 with YuNet on the CPU, about 4 s (a rough guess; system_check measures this computer); it first downloads face_detection_yunet_2026may.onnx (230 KB, MIT) into C:\Users\you\ZeyoCut\library\models
- frames 0–95 of 'crew' (0–3.958s on 'crew')
- poll get_job_status with jobId "detect_1"; when it is done, it adds a track for each face as one undo step and shows a contact sheet with their ids
```
`get_job_status` with `{ "jobId": "detect_1" }`:
```text
detect_1: completed in 2.2s — detect_and_track faces on 'crew' (96 frames of 'crew')
found 4 faces in 'crew' with YuNet on the CPU: frames 0–95 of 'crew' in 2.1 s (45.8 frames/s)
- face_1: 0–3.958s on 'crew'; found on 93 of 96 frames (missed 2.5–2.583s), average 90x114 px, confidence 0.94
- face_2: 0–3.958s on 'crew'; found on 93 of 96 frames (missed 2.292–2.375s), average 91x125 px, confidence 0.93
- face_3: 0–3.958s on 'crew'; found on 93 of 96 frames (missed 2.083–2.167s), average 86x114 px, confidence 0.93
- face_4: 0–3.958s on 'crew'; found on 93 of 96 frames (missed 1.875–1.958s), average 80x101 px, confidence 0.93
- the contact sheet shows each face with its id; saved C:\Users\you\ZeyoCut\projects\faces\renders\faces_crew_000000-000095.png
- hide them with apply_redaction trackIds=["face_1", "face_2", "face_3", "face_4"] or ["all"]; render_frame debug=true draws each track's box
- faces are linked only by where their boxes go, not recognized: someone gone for more than 1 s comes back as a new track
```
The contact sheet that comes with it shows the four faces left to right, each labeled with its id, span and size (`0–3.958s · 114 px`). Each was missed only on the three frames the bar hid it. `list_layers` shows the tracks on the clip:
```text
crew [video]  z=0  t=0–4  asset=crew source=0–4s speed=1  volume=1  anchor=(640,360)  tracks=face_1/face_2/face_3/face_4
```
Hiding them all:
```json
{ "trackIds": ["all"] }
```
```text
hid face_1, face_2, face_3, face_4 with 'redaction_1': pixelate, strength 50 (12 blocks across each box), padding 15%, feather 4 px, held 15 frames before and after
- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('crew' plays in 'crew'), so moves, effects, the camera and subject cutouts all show it hidden
- face_1: frames 0–95 of 'crew' covered (0–3.958s on 'crew'): 93 detected, 3 filled between detections, 0 held
- face_2: frames 0–95 of 'crew' covered (0–3.958s on 'crew'): 93 detected, 3 filled between detections, 0 held
- face_3: frames 0–95 of 'crew' covered (0–3.958s on 'crew'): 93 detected, 3 filled between detections, 0 held
- face_4: frames 0–95 of 'crew' covered (0–3.958s on 'crew'): 93 detected, 3 filled between detections, 0 held
- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand
```
`render_frame` at 2.083 s, while the bar hides `face_3`, shows the other three faces pixelated.

`undo` takes off the redaction, then all four tracks:
```text
undid: apply_redaction all (3 more undo step(s), 1 redo step(s))
```
```text
undid: detect_and_track faces crew (2 more undo step(s), 2 redo step(s))
```

Plates on a CC0 photo of police vans (Jebulon, Wikimedia Commons), panned down in a 4 s 1280x720 clip, `vans`, with the same bar crossing it:
```json
{ "layerId": "vans", "target": "plates" }
```
```text
finding plates on 'vans' in the background as detect_1: finding plates on 96 frames of 'vans' at 1280x720 with open-image-models YOLOv9-t on the CPU, about 15 s (a rough guess; system_check measures this computer); it first downloads yolo-v9-t-640-license-plates-end2end.onnx (8 MB, MIT code, the weights' license isn't stated) into C:\Users\you\ZeyoCut\library\models
- frames 0–95 of 'vans' (0–3.958s on 'vans')
- poll get_job_status with jobId "detect_1"; when it is done, it adds a track for each plate as one undo step and shows a contact sheet with their ids
```
```text
detect_1: completed in 14.4s — detect_and_track plates on 'vans' (96 frames of 'vans')
found 4 plates in 'vans' with open-image-models YOLOv9-t on the CPU: frames 0–95 of 'vans' in 14.2 s (6.7 frames/s)
- plate_1: 0–3.958s on 'vans'; found on 93 of 96 frames (missed 2.583–2.667s), average 44x15 px, confidence 0.82
- plate_2: 0–3.958s on 'vans'; found on 94 of 96 frames (missed 2.083–2.125s), average 135x54 px, confidence 0.92
- plate_3: 0–3.958s on 'vans'; found on 92 of 96 frames (missed 1.708–1.833s), average 22x17 px, confidence 0.79
- plate_4: 2.292–2.583s on 'vans'; found on 8 of 8 frames, average 82x20 px, confidence 0.33
- left out 1 detection on fewer than 3 frames, probably not plates: at 2.125s on 'vans'
- the contact sheet shows each plate with its id; saved C:\Users\you\ZeyoCut\projects\plates\renders\plates_vans_000000-000095.png
- hide them with apply_redaction trackIds=["plate_1", "plate_2", "plate_3", "plate_4"] or ["all"]; render_frame debug=true draws each track's box
- plates are linked only by where their boxes go, not read: a car gone for more than 1 s comes back as a new track
```
The contact sheet shows the plate of a van at the left edge (`plate_1`), the van's own plate (`plate_2`) and one the right edge cuts (`plate_3`, 22 px). `plate_4` is the van's RENAULT badge, which passed for a plate at the default `minConfidence` of 0.3 on 8 frames. The plates were each missed only while the bar hid them. Hiding only the plates:
```json
{ "trackIds": ["plate_1", "plate_2", "plate_3"] }
```
```text
hid plate_1, plate_2, plate_3 with 'redaction_1': pixelate, strength 50 (12 blocks across each box), padding 15%, feather 4 px, held 15 frames before and after
- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('vans' plays in 'vans'), so moves, effects, the camera and subject cutouts all show it hidden
- plate_1: frames 0–95 of 'vans' covered (0–3.958s on 'vans'): 93 detected, 3 filled between detections, 0 held
- plate_2: frames 0–95 of 'vans' covered (0–3.958s on 'vans'): 94 detected, 2 filled between detections, 0 held
- plate_3: frames 0–95 of 'vans' covered (0–3.958s on 'vans'): 92 detected, 4 filled between detections, 0 held
- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand
```
`render_frame` at 1 s shows the three plates pixelated and the badge as it is.

Errors:
```text
An error occurred invoking 'detect_and_track': unknown target 'cars'; valid values: faces, plates
```
```text
An error occurred invoking 'detect_and_track': minConfidence must be above 0 and at most 1 (got 1.5)
```
```text
An error occurred invoking 'detect_and_track': startTime and endTime must be inside clip 'crew' (0–4s) with startTime before endTime (got 3–9s)
```
```text
An error occurred invoking 'reanchor_track': track 'face_1' was found on every frame by detect_and_track, not tracked from a box, so reanchor_track can't correct it; where it missed the face or plate, track_object makes a track from a box there, which apply_redaction can hide too
```
```text
An error occurred invoking 'track_object': unknown method 'faces'; valid values: csrt, template, opticalFlow (detect_and_track finds faces and plates)
```

### `attach_to_track`

Keeps a text or image layer on a track's box, such as a name over a face or a sticker on a sign. It writes x and y keyframes, plus scaleX and scaleY with `followScale`:

- **They cover every frame where a clip shows the footage the track found the object in.** They go through the clip's placement, parents and crop, the camera, and the layer's own parent. Frames the track lost are filled in straight between the frames found around them.
- **The layer's anchor sits at the box's center plus `offset`,** in canvas pixels at the first frame it follows. Without `offset` the layer stays where it is at that frame, so you can place it first.
- **With `followScale`** the layer and its offset grow and shrink with the box, from their size at that first frame.
- **Only the keyframes needed are kept:** each property stays within 0.5 px (0.2% of the size for the scale) of every frame. They replace the layer's own x and y (and scale) keyframes in that time, and its motion path. Elsewhere its own keyframes and values apply.
- **They are timeline keyframes.** After trimming, moving or changing the speed of a clip that plays the footage, run `attach_to_track` again.
- **The box's jitter shows.** A detected face box moves a pixel or two from frame to frame, and so does the layer. Where something covers part of the face, the box shrinks, and so does the layer with `followScale`.

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | required | A text or image layer |
| `trackId` | required | From `track_object` or `detect_and_track` |
| `offset` | where the layer is | `{x, y}`: the anchor from the box's center, canvas pixels at the first frame it follows |
| `followScale` | true | Scale the layer and the offset with the box |

The presenter from [`track_object`](#track_object), as clip `guest`, with `face_1` found by `detect_and_track` over the whole clip (it missed the face at 2.125–2.167 s, while the walker passed). A name in a dark box, 250 px above the face's center:
```json
{ "layerId": "name", "trackId": "face_1", "offset": { "x": 0, "y": -250 } }
```
```text
attached 'name' to face_1: it follows the box 0–3.958s on 'guest' and scales with it
- its anchor sits 0,-250 canvas px from the box's center at 0s; the offset scales with the box
- keyframes: x 73, y 70, scaleX 71, scaleY 71, kept to stay within 0.5 px and 0.2% of the size of every frame; outside it, its own keyframes and values apply
- its size goes from 0.82 to 1.01 times its size at 0s
- these are timeline keyframes: after trimming, moving or changing the speed of a clip that plays the footage, run attach_to_track again
- check it with render_frame; debug=true draws the track's box
```
`render_frame` shows the name above the face at 0.5 s and at 3.5 s. At 2.2 s the walker covers half the face, so the box, and the name with it, is narrower and moved right.

A tag placed by hand at 1150,560, kept where it is and at its size:
```json
{ "layerId": "tag", "trackId": "face_1", "followScale": false }
```
```text
attached 'tag' to face_1: it follows the box 0–3.958s on 'guest'
- its anchor sits 339.825,154.485 canvas px from the box's center at 0s (where it was then)
- keyframes: x 73, y 70, kept to stay within 0.5 px of every frame; outside it, its own keyframes and values apply
- these are timeline keyframes: after trimming, moving or changing the speed of a clip that plays the footage, run attach_to_track again
- check it with render_frame; debug=true draws the track's box
```

After `split_layer` at 2 s and `set_speed` 0.5 on `guest_2`, running it again on the name:
```text
attached 'name' to face_1: it follows the box 0–1.958s on 'guest', 2–5.958s on 'guest_2' and scales with it
- its anchor sits 0,-250 canvas px from the box's center at 0s; the offset scales with the box
- keyframes: x 120, y 99, scaleX 81, scaleY 81, kept to stay within 0.5 px and 0.2% of the size of every frame; they replaced 285 keyframes it had in that time; outside it, its own keyframes and values apply
- its size goes from 0.82 to 1.01 times its size at 0s
- these are timeline keyframes: after trimming, moving or changing the speed of a clip that plays the footage, run attach_to_track again
- check it with render_frame; debug=true draws the track's box
```
`list_layers` then shows:
```text
name [text]  z=1  t=0–end  text="Scott Horowitz"  font=Segoe UI 44px  anchor=(143.473,29.262)  keys: x(120) y(99) scaleX(81) scaleY(81)
tag [text]  z=2  t=0–end  text="GUEST"  font=Segoe UI 32px  anchor=(46.938,21.281)  keys: x(73) y(70)
```
The tag wasn't attached again, so after the speed change it is out of step with the face on `guest_2`.

Errors:
```text
An error occurred invoking 'attach_to_track': attach_to_track moves text and image layers; 'guest' is a video layer
```
```text
An error occurred invoking 'attach_to_track': track 'face_9' not found; tracks: face_1
```

---

## Redaction

`apply_redaction` hides tracked faces, plates or objects with a **redaction layer** (type `redaction`):

- **It hides the box on the clip's video frame before the clip is drawn,** in every clip that plays the track's footage. The clip's moves and effects, the camera and a subject cutout of the clip (`text_behind_subject`) all show it hidden.
- **It works on the footage's own frames, like the track.** Splits, trims and speed changes keep it in step, and a piece split off a clip is hidden too. A redaction has no time range, position or parent of its own. Timeline tools such as `split_layer` and `move_layer` refuse it, and `update_layer` only changes its own settings.
- **Styles:** `pixelate` (default), `blur` and `solid` (a color).
- **Strength scales with the box,** so a small plate is as unreadable as a big face:
    - pixelate: the block is the box's long side divided by 20 at strength 0, down to 4 at strength 100 (12 at the default 50), and never under 4 source pixels;
    - blur: the blur radius is that same size.
- **The fail-safe** works out a box for every frame from the track:
    - Every box is padded by `paddingPercent` of its width and height on each side.
    - Frames the tracker lost between two detections get a box moved straight from one detection to the other. It grows by 3% of the box's size per side for every frame away from the nearer detection, so no frame between two detections is uncovered.
    - Before the first detection and after the last, the box is held, and grows the same way, for `holdFrames`. A scene cut that [`analyze_video`](#analyze_video) found in the footage ends a hold sooner, so a box isn't carried into another shot. The hold still covers the frame just past the cut, in case the cut was found a frame early or late. Without the analysis, every hold runs its full length.
    - The box's edges are smoothed over 2 frames each side into an envelope that always contains the frame's own box.
    - The reply counts the frames detected, filled and held per track, says where a scene cut ended a track's hold, and lists any of the track's frames left uncovered. Those are frames the tracker lost more than `holdFrames` away from the nearest frame where it found the object, or past a scene cut.
- **`feather`** adds a soft edge outside the box. Inside the box, the picture is always fully hidden.
- **One redaction per track.** Calling `apply_redaction` again with exactly the tracks of an existing redaction changes that redaction. Otherwise a new redaction is made, and the tracks leave the redactions that hid them before; a redaction left with no tracks goes.
- **`tracks_to_keyframes`** turns a track's box into keyframes on the redaction, to correct it by hand.
- **`view_source` still shows the footage as it is,** so you can see what to track. Previews, contact sheets and exports are hidden.
- **[`redaction_report`](#redaction_report)** lists the spans to look at before exporting.

Hiding the face below added about 5 ms a frame at 1280x720: a 4 s export (96 frames) took 1.3 s, against 0.8 s without the redaction.

### `apply_redaction`

| Parameter | Default | Notes |
|---|---|---|
| `trackIds` | required | Track ids (`list_layers` shows them as `tracks=`), or `["all"]` for every track |
| `style` | `pixelate` | `pixelate`, `blur` or `solid` |
| `strength` | 50 | 0–100 |
| `paddingPercent` | 15 | 0–100, per side |
| `feather` | 4 | 0–100 source pixels |
| `holdFrames` | 15 | 0–300. A scene cut from `analyze_video` ends a hold sooner |
| `color` | `#000000` | The fill of `solid` |

The presenter from [Tracking](#tracking), with `track_1` corrected by `reanchor_track`:
```json
{ "trackIds": ["track_1"] }
```
```text
hid track_1 with 'redaction_1': pixelate, strength 50 (12 blocks across each box), padding 15%, feather 4 px, held 15 frames before and after
- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('walk' plays in 'guest'), so moves, effects, the camera and subject cutouts all show it hidden
- track_1: frames 0–95 of 'walk' covered (0–3.958s on 'guest'): 96 detected, 0 filled between detections, 0 held
- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand
```
`render_frame` at 1 s shows the face pixelated. At 2.25 s it stays hidden while the walker passes.

`track_2` was tracked with `template` from the same box. Its confidence fell under 0.5 at 2.125–2.208 s, while the walker passed:
```json
{ "trackIds": ["track_2"], "style": "solid" }
```
```text
hid track_2 with 'redaction_2': solid #000000, padding 15%, feather 4 px, held 15 frames before and after
- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('walk' plays in 'guest'), so moves, effects, the camera and subject cutouts all show it hidden
- track_2: frames 0–95 of 'walk' covered (0–3.958s on 'guest'): 96 detected, 0 filled between detections, 0 held
- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand
```
At 2.25 s the smoothed box is stretched wide over the frames where the box drifted, and the face stays covered.

`update_layer` changes the settings:
```json
{ "layerId": "redaction_1", "changes": { "style": "blur", "strength": 70 } }
```
```text
updated layer 'redaction_1'
{"type":"redaction","id":"redaction_1","name":"redaction_1","start":0,"end":null,"zIndex":0,"parentId":null,"visible":true,"tracks":["track_1"],"style":"blur","strength":70,"paddingPercent":15,"feather":4,"holdFrames":15,"color":"#000000"}
```
After `split_layer` at 2 s and `set_speed` 0.5 on the second piece, `render_frame` at 3 s still shows the face blurred.

All tracks in one redaction; both earlier redactions go:
```json
{ "trackIds": ["all"], "style": "solid", "color": "#202428" }
```
```text
hid track_1, track_2 with 'redaction_1': solid #202428, padding 15%, feather 4 px, held 15 frames before and after
- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('walk' plays in 'guest', 'guest_2'), so moves, effects, the camera and subject cutouts all show it hidden
- track_1: frames 0–95 of 'walk' covered (0–1.958s on 'guest', 2–5.917s on 'guest_2'): 96 detected, 0 filled between detections, 0 held
- track_2: frames 0–95 of 'walk' covered (0–1.958s on 'guest', 2–5.917s on 'guest_2'): 96 detected, 0 filled between detections, 0 held
- removed 'redaction_1', which hid only track_1
- removed 'redaction_2', which hid only track_2
- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand
```

A clip with a hard cut: 2 s of a face, then 2 s of vans, at 24 fps. Before `analyze_video` has run, `face_1`'s box is held 15 frames into the vans' shot, where it pixelates the middle of a van:
```json
{ "trackIds": ["face_1"] }
```
```text
hid face_1 with 'redaction_1': pixelate, strength 50 (12 blocks across each box), padding 15%, feather 4 px, held 15 frames before and after
- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('street' plays in 'street'), so moves, effects, the camera and subject cutouts all show it hidden
- face_1: frames 0–62 of 'street' covered (0–2.583s on 'street'): 48 detected, 0 filled between detections, 15 held
- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand
```
After `analyze_video` found the cut at 2 s, the same call ends the hold there, and the van shows:
```text
changed how 'redaction_1' hides face_1: pixelate, strength 50 (12 blocks across each box), padding 15%, feather 4 px, held 15 frames before and after
- it hides them on the video frame before a clip is drawn, in every clip that plays the footage ('street' plays in 'street'), so moves, effects, the camera and subject cutouts all show it hidden
- face_1: frames 0–48 of 'street' covered (0–2s on 'street'): 48 detected, 0 filled between detections, 1 held
- face_1: the box isn't held across the scene cut that analyze_video found at 2s on 'street'
- check it with render_frame; update_layer changes its style, strength, paddingPercent, feather, holdFrames, color and visible; tracks_to_keyframes turns a track's box into keyframes to edit by hand
```

Errors:
```text
An error occurred invoking 'apply_redaction': track 'track_9' not found; tracks: track_1, track_2
```
```text
An error occurred invoking 'update_layer': 'redaction_1' is a redaction of track_1, track_2: it isn't drawn or placed on the timeline itself, it hides those tracks' boxes in every clip that plays their footage, so make this change on the clips (its own settings are name, visible, style, strength, paddingPercent, feather, holdFrames, color; apply_redaction changes its tracks and tracks_to_keyframes its boxes)
```

### `tracks_to_keyframes`

Turns a hidden track's box into `x`, `y`, `width` and `height` keyframes on its redaction, named `<track>.x`, `<track>.y`, `<track>.width` and `<track>.height`:

- **They are made from the fail-safe box,** with padding, gaps, holds and smoothing included. Only the keyframes needed to stay within 0.2 px of it are kept.
- **The box is 0.5 px bigger on each side,** so on every frame it contains the box it was made from.
- **Times are the footage's own seconds and values its pixels,** like the track, so the box keeps following trims, speed changes and splits.
- **The redaction then hides that box from its first keyframe to its last,** and `paddingPercent` and `holdFrames` no longer change it.
- **Edit it with `set_keyframes` and `remove_keyframes`.** `clear_keyframes` with `property: "<track>"` goes back to the box worked out from the track. Running `tracks_to_keyframes` again remakes the keyframes from the track.

| Parameter | Default | Notes |
|---|---|---|
| `trackId` | required | A track that `apply_redaction` hides |

After the split and speed change above:
```json
{ "trackId": "track_1" }
```
```text
made track_1's box on 'redaction_1' keyframes: x 40, y 27, width 49, height 39 over frames 0–95 of 'walk' (source 0–3.958s; 0–1.958s on 'guest', 2–5.917s on 'guest_2')
- the times are the footage's own seconds and the values its pixels, like the track's, so the box keeps following trims, speed changes and splits
- on every frame the box contains the fail-safe box it was made from (0.5 px bigger on each side); paddingPercent and holdFrames no longer change it
- edit it with set_keyframes or remove_keyframes on layerId "redaction_1", property "track_1.x", "track_1.y", "track_1.width" or "track_1.height" (time in the footage's seconds); clear_keyframes property "track_1" goes back to the box worked out from the track
```
Widening the box at the footage's third second:
```json
{ "entries": [{ "layerId": "redaction_1", "property": "track_1.width", "time": 3, "value": 620 }] }
```
```text
set 1 keyframe(s) (0 added, 1 replaced) on redaction_1.track_1.width
```
`list_layers` shows the redaction and its keyframes:
```text
redaction: 1280x720 @ 24 fps, 6s, 4 layer(s), 1 asset(s)
guest [video]  z=0  t=0–2  asset=walk source=0–2s speed=1  volume=1  anchor=(640,360)  tracks=track_1/track_2
guest_2 [video]  z=0  t=2–6  asset=walk source=2–4s speed=0.5  volume=1  anchor=(640,360)  tracks=track_1/track_2
redaction_1 [redaction]  hides=track_1  blur strength=70 padding=15% feather=4 hold=15  keys: track_1.x(40) track_1.y(27) track_1.width(49) track_1.height(39)
redaction_2 [redaction]  hides=track_2  solid #000000 padding=15% feather=4 hold=15
```
Back to the box worked out from the track:
```json
{ "layerId": "redaction_1", "property": "track_1" }
```
```text
cleared 155 keyframe(s) from redaction_1.track_1
```
Keyframes on a box that has none yet:
```text
An error occurred invoking 'set_keyframes': 1 of 1 entries are invalid, so nothing was changed:
- entry 0 (redaction_1.track_1.x @ 1s): track_1 has no keyframed box on 'redaction_1' yet; tracks_to_keyframes trackId="track_1" makes one from the track
```

### `redaction_report`

Checks how well the redactions hide their tracks before you export. It changes nothing.

For each track it gives:

- the redaction that hides it, or `not hidden`;
- the frames of the footage its box covers, with their times in every clip that plays them;
- how many of those frames come from a detection, how many the fail-safe holds between detections or before and after them, and how many frames the track spans that no box covers;
- its confidence and box sizes.

Then it lists the spans to look at with `render_frame`. Only footage that some clip plays is listed:

- **frames the track spans that no box covers;**
- **confidence under 0.5,** where a tracker may have drifted or a detection is weak;
- **0.5 s or more without a detection,** where the box only moves straight from one detection to the next;
- **fast motion:** the box moves a quarter of its size or more in a frame;
- **boxes under 12 px,** where padding adds little;
- **footage before a track's box starts or after it is let go,** while the first or last box is inside the frame. Inside means more than 2% of the frame's larger side from every edge. The face, plate or object may still be there uncovered. A box at the edge is taken as leaving the picture. Where a scene cut ended the box's hold, the span says so.

A redaction hides its tracks in every clip that plays the footage, so the report names those clips once instead of checking each clip.

| Parameter | Default | Notes |
|---|---|---|
| `trackIds` | every track | Track ids, or `["all"]` |

The example uses the presenter from [Tracking](#tracking), set up like this:

- the footage plays as 'guest', and its last second plays again as 'replay' from 4 s;
- `track_1` (`csrt`) and `track_2` (`template`) are tracked from the same box, without correction;
- `face_1` comes from `detect_and_track` over the first 2 s only;
- `apply_redaction` hides `track_2` and `face_1`.

```json
{}
```
```text
redaction report: 3 tracks, 2 hidden; 4 spans to check
- 'walk' plays in 'guest', 'replay': a redaction hides its tracks in every clip that plays it
- track_1, not hidden: found on frames 0–95 of 'walk' (0–3.958s on 'guest', 4–4.958s on 'replay'); confidence 0.93 on average, 0.15 at the lowest; boxes 412–473 px; apply_redaction trackIds=["track_1"] hides it
- track_2, hidden by 'redaction_1': frames 0–95 of 'walk' covered (0–3.958s on 'guest', 4–4.958s on 'replay'): 96 detected, 0 held between detections, 0 held before or after, 0 uncovered; confidence 0.96 on average, 0.39 at the lowest; box 420 px
- face_1, hidden by 'redaction_1': frames 0–62 of 'walk' covered (0–2.583s on 'guest'): 48 detected, 0 held between detections, 15 held before or after, 0 uncovered; confidence 0.95 on average, 0.94 at the lowest; boxes 405–431 px
to check (render_frame with debug=true draws each track's box):
- track_1, 2.125–2.25s on 'guest': confidence down to 0.15: the box may have drifted off the object; reanchor_track corrects it
- track_2, 2.125–2.208s on 'guest': confidence down to 0.39: the box may have drifted off the object; reanchor_track corrects it
- track_2, 2.125–2.25s on 'guest': the box moves up to 1.11 of its size a frame: fast motion, or part of the object hidden; check that the box keeps up
- face_1, 2.625–3.958s on 'guest', 4–4.958s on 'replay': plays after the box is let go (15 frames after the last detection), and the last box is inside the frame, 107 px from its nearest edge, so the face may still be there uncovered; if it is, track it there with track_object or detect_and_track
```
`render_frame` at 3.25 s shows the face after `face_1`'s box is let go. It is still there, but `track_2`'s box hides it.

After `apply_redaction` with `["track_1"]`, the report on that track alone:
```json
{ "trackIds": ["track_1"] }
```
```text
redaction report: 1 track, 1 hidden; 1 span to check
- 'walk' plays in 'guest', 'replay': a redaction hides its tracks in every clip that plays it
- track_1, hidden by 'redaction_2': frames 0–95 of 'walk' covered (0–3.958s on 'guest', 4–4.958s on 'replay'): 96 detected, 0 held between detections, 0 held before or after, 0 uncovered; confidence 0.93 on average, 0.15 at the lowest; boxes 412–473 px
to check (render_frame with debug=true draws each track's box):
- track_1, 2.125–2.25s on 'guest': confidence down to 0.15: the box may have drifted off the object; reanchor_track corrects it
```

With no tracks:
```text
no tracks to report on: track_object follows an object from a box, and detect_and_track finds faces or plates
```

Errors:
```text
An error occurred invoking 'redaction_report': track 'face_9' not found; tracks: track_1, track_2, face_1
```

---

## Preview

`render_frame` and `render_contact_sheet` use the export renderer, so what you see is exactly what gets encoded. Each returns a text block plus a PNG image block, and saves the image in `renders\`. `preview_server` plays the edit with its sound in a browser, drawn by the same renderer.

`debug: true` draws:

- each layer's outline
- its anchor (crosshair)
- a dashed bone from parent anchor to child anchor
- the layer id
- the box of each [track](#tracking) on a clip at that frame, with the track's id and confidence (red under 0.5)

Use it when a rig pivots or attaches in the wrong place.

### `render_frame`

| Parameter | Default | Notes |
|---|---|---|
| `time` | required | Seconds; snaps to the nearest frame |
| `maxWidth` | 960 | Width of the returned image (64–4096). The saved file is full resolution |
| `debug` | false | |
| `checkerboard` | false | Shows transparent parts as gray squares instead of the background color |
| `layerId` | all layers | Draws only this layer and its children, or only a subject cutout |
| `language` | every layer | Shows that [language's version](#languages): the layers of other languages are left out |

`checkerboard` and `layerId` together show the edges of a removed background without the layers behind it. The saved file's name then ends in `_<layerId>_checker.png`.

**Preview cache:** a frame shown before comes back without being drawn again while nothing it shows has changed. The check is the same as the export's [chunk cache](#start_export): every drawn layer as it is at that frame, the files it reads and the clip frame it shows. Editing a keyframe at 12 s therefore leaves a frame at 2 s cached, unless the edit changes what 2 s shows. The cache is kept in memory for the open project (up to 128 MB, dropping what was used longest ago), and the full-size PNG is written to `renders\` each time. On a 1080p clip with a title, a first `render_frame` took 312 ms and the same frame again 1 ms. After the title's 12 s keyframe moved, the frame at 2 s still took 2 ms and the frame at 10 s, which had changed, 99 ms.

```json
{ "time": 0.9, "maxWidth": 640 }
```
```text
frame 27 of 90 at t=0.900s; image 640x360; saved C:\Users\you\ZeyoCut\projects\tora-test\renders\frame_00027.png
```
followed by an `image/png` content block.
```json
{ "time": 1, "checkerboard": true, "layerId": "presenter", "maxWidth": 640 }
```
```text
frame 24 of 96 at t=1.000s; only 'presenter'; transparency as a checkerboard; image 640x360; saved C:\Users\you\ZeyoCut\projects\keying\renders\frame_00024_presenter_checker.png
```

### `render_contact_sheet`

Renders evenly spaced frames into one labeled grid, so you can check motion in a single call.

| Parameter | Default | Notes |
|---|---|---|
| `startTime` | 0 | |
| `endTime` | last frame | |
| `count` | 12 | 1–64 |
| `columns` | 4 | |
| `maxWidth` | 1600 | Width of the whole sheet |
| `debug` | false | |
| `language` | every layer | Shows that [language's version](#languages) |

```json
{ "startTime": 0, "endTime": 1.8, "count": 8, "columns": 4, "maxWidth": 1200 }
```
```text
contact sheet 1198x390, frames (left to right, top to bottom): f0=0.000s, f8=0.267s, f15=0.500s, f23=0.767s, f31=1.033s, f39=1.300s, f46=1.533s, f54=1.800s; saved C:\Users\you\ZeyoCut\projects\tora-test\renders\sheet_00000-00054_8.png
```
followed by an `image/png` content block.

The cells use the [preview cache](#render_frame) too (except with `debug`): only the cells whose frames changed are drawn again. On the 1080p clip, a sheet of 12 frames took 2.3 s, the same sheet again 44 ms, and after a keyframe edit that changed 7 of the cells, 129 ms.

### `preview_server`

Starts or stops the preview page: a page for a browser on this computer that plays the open project with its sound and follows every edit. It only shows; edits are made with the tools.

| Parameter | Default | Notes |
|---|---|---|
| `action` | `start` | `start` or `stop`. `start` while it runs gives the address again |

On the page:

- **The frame** at the playhead, drawn by the export renderer at up to 640 px on the long side (never larger than the canvas). Footage over 2560 px is drawn from its proxy.
- **Play / Pause** (space). Playback shows about 15 frames a second (every 2nd frame at 30 fps, every 4th at 60, every frame up to 29 fps) with the sound mixed as the export mixes it: volume, fades, ducking and loudness. The sound sets the time, so picture and sound stay together; a frame that isn't drawn yet is skipped.
- **The timeline:** a time ruler, the markers (chapters in yellow, scenes in blue) and a row per layer in `list_layers` order, each child under its parent, with a bar for its time. A child's bar is cut to its parent's time. Hidden layers are dimmed, and the rows of the layers showing at the playhead are highlighted. Click or drag on the bars to move the playhead, or click a marker. The arrow keys step a frame (with shift, a second), and Home and End go to the start and the end.
- **Language:** with two or more [languages](#languages), a menu shows one language's version, its sound included.
- **A status line** with the frame size and whether the sound is still being mixed, and any warnings from drawing, such as a missing font.

How it follows the project:

- The page asks for the project's state every second. When anything in `project.json` has changed, it builds the layer list again and shows the frame at the playhead as it is now.
- Frames are kept in memory (up to 192 MB) under the same per-frame key as the [preview cache](#render_frame), so after an edit only the frames it changed are drawn again.
- While the page plays, the next 5 s of frames are drawn ahead in the background.
- The mix is made in the background into the project's `cache\preview\mix-<key>.m4a` (AAC, 128 kbps), keyed by everything it depends on. When the sound changes, a new one is made and the old one is deleted.

Who can read it:

- It listens on 127.0.0.1 only, at a random port, under a random 32-character token in the path. Without the token there is nothing (404).
- A request must be addressed to `127.0.0.1:<port>`, so a site that points its own domain name at this computer gets nothing (403).
- No response lets other sites read it (no CORS headers), and the page's Content-Security-Policy allows only its own script and style, by their hashes.
- Only GET and HEAD are answered (405 otherwise); nothing on the page changes the project.
- It stops with `stop` and when the server exits. Starting again gives a new port and token, so a page left open says it can't reach the server.

On a 12 s 1080p clip with a title and music, a frame took about 45 ms to draw the first time, about 190 ms when it started a decoder (the first frame, or a jump back or more than a second ahead), and 2 ms from memory. The first play showed 11 frames a second, the second the full 15. Mixing the sound took about 4 s. The debug build and the Release exe gave the same numbers. The page was checked in Microsoft Edge: playback with the sound in step, an edit showing within a second while playing, seeking on the bars and markers, and the message after `stop`.

```json
{}
```
```text
preview page at http://127.0.0.1:57969/32c78da41ffd8450721120fee76787d4/
open it in a browser on this computer. It shows 'bread' (1920x1080 @ 30 fps) as 640x360 frames, 15 a second while playing, with the sound mixed as the export mixes it (made in the background), and follows every edit within a second. It only shows; edits are made with the tools. Frames are drawn when first shown and kept in memory, so the first play of a part can stutter. preview_server with action stop stops it.
```
With no project open, it starts all the same:
```text
preview page at http://127.0.0.1:58918/aaa138b981568ee2cf94ceada0d4c5a4/
open it in a browser on this computer. No project is open, so it shows none until open_project or create_project opens one; then it shows its frames at up to 640 px, about 15 a second while playing, with the sound mixed as the export mixes it (made in the background), and follows every edit within a second. It only shows; edits are made with the tools. Frames are drawn when first shown and kept in memory, so the first play of a part can stutter. preview_server with action stop stops it.
```
```json
{ "action": "start" }
```
```text
the preview page is already running at http://127.0.0.1:58918/aaa138b981568ee2cf94ceada0d4c5a4/
```
```json
{ "action": "stop" }
```
```text
stopped the preview page at http://127.0.0.1:58918/aaa138b981568ee2cf94ceada0d4c5a4/
```
Again:
```text
the preview page isn't running
```
```json
{ "action": "pause" }
```
```text
An error occurred invoking 'preview_server': action must be start or stop (got 'pause')
```

---

## Video

Video clips are layers like images: they take every transform, keyframe, parent and `zIndex`. Each clip plays a range of its source file (`trimIn`–`trimOut`, source seconds) at a `speed`, starting at `start` on the timeline. Previews seek to the exact frame, and exports decode each clip once from start to end.

### `import_video`

Imports `mp4`, `mov`, `m4v`, `mkv`, `webm` or `avi` as a video asset.

- A file with a constant frame rate, no rotation and 8-bit color is copied into `assets\` at once.
- Anything else (variable frame rate from phones, rotation metadata, 10-bit or HDR, non-square pixels) is transcoded in the background to H.264 CRF 16 at a constant standard frame rate, upright, 8-bit BT.709. The asset is added when the job completes.

| Parameter | Notes |
|---|---|
| `path` | Absolute path of the file |
| `id` | Optional. Default: derived from the file name |

```json
{ "path": "C:\\videos\\street.mp4" }
```
```text
imported street [video] assets/street.mp4 (1280x720 @ 30 fps, 6s, 180 frames, h264, with sound) (copied as it is: constant frame rate, upright, 8-bit)
```
```json
{ "path": "C:\\videos\\phone clip.mp4" }
```
```text
'phone clip.mp4' needs normalizing: variable frame rate (average 29 fps, nominal 30 fps).
Transcoding in the background as import_1 (H.264 CRF 16, constant 30000/1001 fps, upright, 8-bit BT.709, keyframe every second) to C:\Users\you\ZeyoCut\projects\edit-demo\assets\phone_clip.mp4.
Poll get_job_status with jobId "import_1"; the asset is added when it completes.
```

**Proxies:** footage over 2560 px on its long side (4K and up) gets a proxy in the background: a copy 1920 px on its long side with every frame at its time (H.264 CRF 20, a keyframe every second, no sound) in the project's `proxies\` folder. Once it is made, `render_frame`, `render_contact_sheet` and `view_source` draw the clip from it, and `track_object` and `detect_and_track` read it (they track at most 1920 px on the long side anyway, so their boxes are the same size). Exports and `export_thumbnail` always read the original. The proxy's name holds the original file's size and time, so a changed file gets a new one. `open_project` makes any proxy that is missing, so a project from before proxies, or one whose `proxies\` folder was deleted, gets them when it opens.

- On a 20 s 4K clip in a 1080p project on this laptop, the proxy took 2.5 s to make and is 8.8 MB. `render_frame` then took 220–270 ms instead of 425–440 ms from the original, and a 12-frame contact sheet 1.7 s instead of 4.8 s.
- Previews that drew a clip from its proxy say so, since the proxy has less detail than the original when the clip is shown larger than 1920 px.

```json
{ "path": "C:\\videos\\street 4k.mp4", "id": "street" }
```
```text
imported street [video] assets/street_4k.mp4 (3840x2160 @ 30 fps, 20s, 600 frames, h264, no sound) (copied as it is: constant frame rate, upright, 8-bit)
making a 1920 px proxy of 'street' in the background as proxy_1: previews and object tracking read it once it is done, exports always read the original
```
`get_job_status` with `proxy_1` when it is done:
```text
proxy_1: completed in 2.5s — make a 1920 px proxy of 'street' for previews (open_project makes any that are missing)
made 1 proxy for previews and object tracking (exports read the original):
- 'street': 1920x1080, 8.8 MB, in 2.5s: proxies\street_4k.374d03e17c9e.mp4
```
`render_frame` at 3 s:
```text
frame 90 of 600 at t=3.000s; 'street' drawn from its 1920 px proxy (exports read the original); image 960x540; saved C:\Users\you\ZeyoCut\projects\street\renders\frame_00090.png
```

### `add_video_layer`

Adds a clip. By default it is centered and scaled to fit the canvas. If it runs past the end, the project is extended.

| Parameter | Default | Notes |
|---|---|---|
| `assetId` | required | From `import_video` |
| `timelineStart` | 0 | Where the clip starts on the timeline |
| `trimIn`, `trimOut` | 0, end of source | Source range, seconds |
| `speed` | 1 | 0.1–10. The clip lasts `(trimOut − trimIn) / speed`; its sound changes tempo without changing pitch |
| `volume`, `muted` | 1, false | The clip's sound |
| `role` | `voice` | `voice`, `music` or `effects`, for ducking |
| `fadeIn`, `fadeOut` | 0 | Sound fades, seconds |
| `language` | every version | The language of the clip's sound: the other [languages' versions](#languages) mute it. Its picture is in every version |
| `id`, `name`, `parentId`, `x`, `y`, `anchorX`, `anchorY`, `rotation`, `scaleX`, `scaleY`, `opacity`, `zIndex` | | As for image layers. Scale defaults to fit the canvas |

`cropWidth`/`cropHeight` (set with `update_layer`) show only part of the frame; `cropX`/`cropY` place that window and can be keyframed.

```json
{ "assetId": "talk" }
```
```text
added video layer 'talk': source 0–15.933s at 1x → timeline 0–15.933s, scale 1.5, z=0
the project was extended from 5s to 15.933s to fit the clip
{"type":"video","id":"talk","name":"talk","start":0,"zIndex":0,"parentId":null,"visible":true,"anchorX":640,"anchorY":360,"asset":"talk","trimIn":0,"trimOut":15.933333333333334,"speed":1,"volume":1,"muted":false,"role":"voice","fadeIn":0,"fadeOut":0,"cropWidth":null,"cropHeight":null,"x":{"value":960},"y":{"value":540},"rotation":{"value":0},"scaleX":{"value":1.5},"scaleY":{"value":1.5},"opacity":{"value":1},"brightness":{"value":0},"contrast":{"value":1},"saturation":{"value":1},"cropX":{"value":0},"cropY":{"value":0}}
```

### `get_job_status`

Reports a background job: `import_N`, `analyze_N`, `framediff_N`, `audiolevels_N`, `beats_N`, `batch_N`, `versions_N`, `frames_N`, `prerender_N`, `matte_N`, `occlusion_N`, `captions_N`, `syscheck_N`, `tracking_N`, `detect_N`, `proxy_N` or `export_N`. Without `jobId`, it lists this session's jobs and the latest ones of earlier sessions. A finished `detect_N` also returns its contact sheet as an image. A running job's status estimates the time left from how far it got, once it is 2% done and has run for 3 seconds.

```json
{ "jobId": "analyze_1" }
```
While running:
```text
analyze_1: running (transcribing) 45%, 0.5s elapsed — analyze talk
```
When done:
```text
analyze_1: completed in 3.6s — analyze talk
analyzed 'talk' (15.933s) → C:\Users\you\ZeyoCut\projects\talk-demo\assets\talk.analysis.json
scene cuts: 1 (8s)
silences ≥ 0.3s below -64.5 dB (noise floor -70.5 dB): 4, 6.456s in total
transcript: 28 words (en) → assets/talk.transcript.json, subtitles → assets/talk.srt
word times: 13 adjusted to the speech between the measured silences
read it with get_transcript; cut words with cut_by_transcript
```
```json
{}
```
```text
- import_1: completed in 0.6s — normalize phone clip.mp4
```
```text
- frames_1: running (writing frames) 51%, 6.0s elapsed, about 6s left — export_frames f0–f1797 as jpg
- export_1: completed
```

**After a restart.** Every job is written to the jobs folder, `%LOCALAPPDATA%\ZeyoCut\jobs` by default, and kept for 7 days.

- **Ids keep counting** across restarts, and between servers that share the folder, so an id always means one job.
- **An export** (`start_export` or `export_batch`) that was running when its server stopped starts again when the next server starts, under the same id. It renders the snapshot it was started with, from the beginning, and the file is written again. If the server stops during 3 runs, the export isn't started again.
- **Any other job** that was running shows as failed, with how far it got. Start it again with the same tool.
- **A job a server is still running** is left to that server.

A server killed 5 seconds into a two-minute export and an `export_frames` job, then started again:
```json
{}
```
```text
- frames_1: failed after 5.0s — export_frames f0–f3597 as jpg: the server stopped while this job ran (1% done, writing frames, last heard from at 18:25:23), so it didn't finish; start it again
- export_1: running (rendering) 0% (1/3600 frames), 0.1s elapsed; started again after the server restarted (attempt 2 of 3)
```
Started once more later, the server lists the jobs of earlier sessions:
```text
earlier sessions, newest first: export_1 (completed), frames_1 (failed); get_job_status with a jobId shows one
```

### `reframe_to_vertical`

Fits landscape clips to a vertical canvas. Switch the canvas first with `update_project` `preset=shorts_1080p`.

- Each clip gets a full-height crop with the canvas's aspect ratio, placed at `focusX`, centered and scaled to fill the frame.
- Zoom differences between pieces, such as `remove_silences` punch-ins, are kept.
- To follow a moving subject, keyframe `cropX` (source pixels) with `set_keyframes`.

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | `all` | One video layer, or `all` |
| `focusX` | 0.5 | 0 = left edge, 0.5 = center, 1 = right edge |

```json
{}
```
```text
An error occurred invoking 'reframe_to_vertical': the canvas is 1920x1080; reframe_to_vertical fits clips to a vertical canvas, so switch it first (update_project preset=shorts_1080p)
```
```json
{ "focusX": 0.5 }
```
```text
reframed 3 video layer(s):
- talk: crop 404x720 at x=438 y=0, scale 2.673
- talk_2: crop 404x720 at x=438 y=0, scale 2.941
- talk_3: crop 404x720 at x=438 y=0, scale 2.673
```

---

## Editing

These tools edit the timeline the way an editor does. They work on any layer, and each moves the layer's keyframes and children with it.

- **Ripple** means that the layers after an edit move with it, so nothing leaves a gap. The project duration changes by the same amount, and [markers](#markers) move with the time around them.
- Pieces produced by a cut are named after the original in time order: `clip`, `clip_2`, `clip_3`, ….

### `split_layer`

Splits a layer at a timeline time into two layers that play exactly as before. The left part keeps the id.

```json
{ "layerId": "street", "time": 2 }
```
```text
split 'street' at 2s into 'street' and 'street_2'
```

### `remove_range`

Cuts timeline seconds `start`–`end` out of one layer, or out of every layer with `layerId` `all` (the default).

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | `all` | |
| `start`, `end` | required | Timeline seconds |
| `ripple` | true | `false` leaves a gap |

```json
{ "start": 3, "end": 3.5 }
```
```text
removed 0.5s (3–3.5s); duration 6s → 5.5s
cut: street_2
new pieces: street_3 (from street_2)
```

### `move_layer`

Moves a layer to a new start time. The project grows if the layer now runs past the end.

```json
{ "layerId": "street_2", "start": 2.5 }
```
```text
moved 'street_2' by +0.5s: now 2.5–3.5s
```

### `set_speed`

Changes a video layer's speed (0.1–10), keeping its start. Its keyframes and children stretch with it. With `ripple` (default true), later layers move so they stay back to back.

```json
{ "layerId": "street_2", "speed": 2 }
```
```text
'street_2' now plays at 2x (was 1x): 2–2.5s, 0.5s long; moved 1 later layer(s) by -0.5s
duration 5.5s → 5s
```

### `add_transition`

Adds a transition between two back-to-back layers, `layerA` then `layerB`.

- **`crossfade`** (default): overlaps the two by `duration`, using unused source on each side where there is some. It ripples B and later layers left for the rest, fades B in (or A out, if A is drawn on top) and crossfades the sound.
- **`fadeToBlack`**: darkens A to black over the last half of `duration`, brings B up from black over the first half, and fades the sound out and in.

`wipe`, `slide`, `zoom` and `whipPan` work on image, text and video layers. They overlap the clips like `crossfade`, crossfade the sound, and then:

- **`wipe`**: an edge sweeps across the layer drawn on top, using a rectangle [mask](#set_mask) (that layer must not have one yet). B appears behind the edge, or A disappears ahead of it. `direction` is the way the edge moves; the default is `right`.
- **`slide`**: B pushes A out of the frame, both moving by the canvas width or height and easing in and out. `direction` is the way the pictures move; the default is `left`.
- **`zoom`**: `in` (default) scales A up to 2x while B grows from half size to its own; `out` shrinks A to half while B comes down from 2x. Both scale around the center of their content, and the layer on top fades.
- **`whipPan`**: a slide with motion blur that peaks in the middle, 2% of the frame width (or height) at its strongest. It adds a [blur effect](#set_effect) to layers without one. The default `direction` is `left`.

Everything is baked into keyframes (`x`, `y`, `scaleX`, `scaleY`, `opacity`, `mask.*`, `blur.*`) that you can adjust afterwards. A `slide` or `whipPan` needs layers without a motion path.

| Parameter | Default |
|---|---|
| `layerA`, `layerB` | required |
| `type` | `crossfade` |
| `duration` | 0.5 |
| `direction` | `wipe`: `right`; `slide`, `whipPan`: `left`; `zoom`: `in` |

```json
{ "layerA": "street", "layerB": "street_2", "type": "crossfade", "duration": 0.5 }
```
```text
crossfade 'street' → 'street_2' over 1.75–2.25s: 0.25s handle from 'street'; 0.25s handle from 'street_2'; 'street_2' fades in 1.75–2.25s
```
```json
{ "layerA": "street_2", "layerB": "street_3", "type": "fadeToBlack", "duration": 1 }
```
```text
fadeToBlack 'street_2' → 'street_3': 'street_2' darkens 2–2.5s, 'street_3' brightens 2.5–3s, sound fades 0.5s each side
```
On two full-frame stills, `shot_a` (0–2 s) and `shot_b` (2–4 s, drawn above), at 24 fps:
```json
{ "layerA": "shot_a", "layerB": "shot_b", "type": "wipe", "duration": 0.6 }
```
```text
wipe right 'shot_a' → 'shot_b' over 1.7–2.3s: 0.3s handle from 'shot_a'; 0.3s handle from 'shot_b'; 'shot_b' is revealed by a mask wiping right
```
```json
{ "layerA": "shot_a", "layerB": "shot_b", "type": "slide", "direction": "up", "duration": 0.6 }
```
```text
slide up 'shot_a' → 'shot_b' over 1.7–2.3s: 0.3s handle from 'shot_a'; 0.3s handle from 'shot_b'; 'shot_b' pushes 'shot_a' out up by 720 px
```
```json
{ "layerA": "shot_a", "layerB": "shot_b", "type": "zoom", "duration": 0.6 }
```
```text
zoom in 'shot_a' → 'shot_b' over 1.7–2.3s: 0.3s handle from 'shot_a'; 0.3s handle from 'shot_b'; 'shot_a' scales 1x → 2x; 'shot_b' scales 0.5x → 1x; 'shot_b' fades in
```
```json
{ "layerA": "shot_a", "layerB": "shot_b", "type": "whipPan", "duration": 0.4 }
```
```text
whipPan left 'shot_a' → 'shot_b' over 1.8–2.2s: 0.2s handle from 'shot_a'; 0.2s handle from 'shot_b'; 'shot_b' pushes 'shot_a' out left by 1280 px; motion blur up to 25.6 px at 2s
```
`list_layers` after the whip pan:
```text
shot_a [image]  z=0  t=0–2.2  drawings=normal  anchor=(640,360)  effects: blur  keys: x(2) blur.x(3)
shot_b [image]  z=1  t=1.8–end  drawings=normal  anchor=(640,360)  effects: blur  keys: x(2) blur.x(3)
```
```json
{ "layerA": "shot_a", "layerB": "shot_b", "type": "zoom", "direction": "left" }
```
```text
An error occurred invoking 'add_transition': zoom goes in or out (got left)
```
```json
{ "layerA": "shot_a", "layerB": "shot_b", "type": "crossfade", "direction": "left" }
```
```text
An error occurred invoking 'add_transition': crossfade has no direction; direction is for wipe, slide, zoom and whipPan
```

---

## Markers

Markers are named points on the timeline, either **chapters** (the default) or **scenes**. They belong to the project, not to a layer.

- **Ripple edits move them** with the time around them, like the camera: `remove_range`, `remove_silences`, `cut_by_transcript`, `set_speed` and crossfades. A marker inside the time a ripple takes out moves to where the cut is. Edits without ripple leave markers where they are.
- **`list_layers`** shows them on the line after the project, and `get_project` under `markers`. A project without markers doesn't save the field.
- **`update_project`** warns when a shorter duration leaves markers past the end.
- **[`export_markers`](#export_markers)** writes the chapters as a YouTube chapter list for the video's description.

### `add_markers`

Adds or changes markers in one call. It is one undo step, and if one marker is wrong nothing changes. The reply lists every marker.

- **A new marker** gets an id made from its name (`Feeding the starter` becomes `feeding_the_starter`; the same name again gets `_2`), unless `id` gives one. A name without Latin letters or digits gets `chapter` or `scene`.
- **An existing `id`** changes that marker: its time and name, and its kind only when `kind` is given.
- **Two markers of one kind at the same time** are both kept, with a warning.

| Entry field | Notes |
|---|---|
| `time` | Seconds, from 0 to the project end |
| `name` | Required, one line; for a chapter, its title |
| `kind` | `chapter` (default for a new marker) or `scene` |
| `id` | An existing marker's id, to change it; or the id for a new one |

```json
{"markers": [{"time": 0, "name": "Intro"}, {"time": 14.5, "name": "Feeding the starter"}, {"time": 38, "name": "Mixing and folds"}, {"time": 38, "name": "Kitchen, wide", "kind": "scene"}, {"time": 71.2, "name": "Shaping and baking"}]}
```
```text
added 5 markers: intro, feeding_the_starter, mixing_and_folds, kitchen_wide, shaping_and_baking
markers (5):
- intro chapter at 0s "Intro"
- feeding_the_starter chapter at 14.5s "Feeding the starter"
- mixing_and_folds chapter at 38s "Mixing and folds"
- kitchen_wide scene at 38s "Kitchen, wide"
- shaping_and_baking chapter at 71.2s "Shaping and baking"
```
Changing one marker and adding two at the same time:
```json
{"markers": [{"id": "shaping_and_baking", "time": 70, "name": "Shaping and the bake"}, {"time": 88, "name": "Tasting"}, {"time": 88, "name": "Taste test"}]}
```
```text
added 2 markers: tasting, taste_test; changed shaping_and_baking
markers (7):
- intro chapter at 0s "Intro"
- feeding_the_starter chapter at 14.5s "Feeding the starter"
- mixing_and_folds chapter at 38s "Mixing and folds"
- kitchen_wide scene at 38s "Kitchen, wide"
- shaping_and_baking chapter at 70s "Shaping and the bake"
- tasting chapter at 88s "Tasting"
- taste_test chapter at 88s "Taste test"
Warnings:
- chapter markers 'tasting', 'taste_test' are at the same time (88s)
```
```json
{"markers": [{"time": 120, "name": "Bloopers"}]}
```
```text
An error occurred invoking 'add_markers': marker 1 ("Bloopers") is at 120s; markers go from 0 to the project end (95s)
```
```json
{"markers": [{"time": 50, "name": "Act two", "kind": "act"}]}
```
```text
An error occurred invoking 'add_markers': unknown marker kind 'act'; valid values: chapter, scene
```
After `remove_range` with `start` 20 and `end` 26, `list_layers` shows every marker after the cut 6 s earlier:
```text
sourdough: 1920x1080 @ 30 fps, 89s, 1 layer(s), 0 asset(s)
markers: intro chapter at 0s "Intro"; feeding_the_starter chapter at 14.5s "Feeding the starter"; mixing_and_folds chapter at 32s "Mixing and folds"; kitchen_wide scene at 32s "Kitchen, wide"; shaping_and_baking chapter at 64s "Shaping and the bake"; tasting chapter at 82s "Tasting"; taste_test chapter at 82s "Taste test"
title [text]  z=0  t=0–6  text="Sourdough in a day"  font=Segoe UI 120px  anchor=(519.141,79.805)
```

### `remove_markers`

Removes markers by id, every marker (`markerIds` `["all"]`), or every marker of one kind (`kind`). It is one undo step.

| Parameter | Default | Notes |
|---|---|---|
| `markerIds` | none | Marker ids, or `["all"]` |
| `kind` | none | `chapter` or `scene`: every marker of that kind. Not with ids |

```json
{"markerIds": ["taste_test"]}
```
```text
removed 1 marker: taste_test
markers (6):
- intro chapter at 0s "Intro"
- feeding_the_starter chapter at 14.5s "Feeding the starter"
- mixing_and_folds chapter at 32s "Mixing and folds"
- kitchen_wide scene at 32s "Kitchen, wide"
- shaping_and_baking chapter at 64s "Shaping and the bake"
- tasting chapter at 82s "Tasting"
```
```json
{"kind": "scene"}
```
```text
removed 1 marker: kitchen_wide
markers (5):
- intro chapter at 0s "Intro"
- feeding_the_starter chapter at 14.5s "Feeding the starter"
- mixing_and_folds chapter at 32s "Mixing and folds"
- shaping_and_baking chapter at 64s "Shaping and the bake"
- tasting chapter at 82s "Tasting"
```
```json
{"markerIds": ["outro"]}
```
```text
An error occurred invoking 'remove_markers': marker 'outro' not found; markers: intro, feeding_the_starter, mixing_and_folds, shaping_and_baking, tasting
```
```json
{}
```
```text
An error occurred invoking 'remove_markers': give markerIds (or ["all"]) or a kind
```
Shortening the project afterwards with `update_project`:
```json
{"duration": 80}
```
```text
updated project 'sourdough': duration 89s → 80s
1920x1080 @ 30 fps, 80s (2400 frames), background #FFFFFF
Warnings:
- markers after the new end (80s) are past the video: tasting (82s); remove_markers removes them
```

### `export_markers`

Writes the chapter markers as a YouTube chapter list to `exports\<fileName>`, to paste into the video's description. Each chapter is a line `m:ss Name` (`h:mm:ss` from an hour on), in whole seconds. Scene markers are left out.

| Parameter | Default | Notes |
|---|---|---|
| `fileName` | `<project>_chapters.txt` | A plain file name; `.txt` is added when it has no extension |

YouTube shows chapters only when the first is at 0:00, there are at least 3, and each lasts at least 10 s. The last chapter runs to the project end. The file is written anyway, and the reply warns about each rule the chapters break.

```json
{}
```
```text
wrote 5 chapters to C:\Users\you\ZeyoCut\projects\bread\exports\bread_chapters.txt (1 scene marker left out); paste them into the video's description:
0:00 Intro
0:42 Ingredients
1:35 Mixing the dough
3:00 Baking
4:55 Tasting
Warnings:
- 'tasting' ("Tasting") lasts 5s from 4:55; YouTube needs at least 10 s per chapter
```
After moving `tasting` to 262 s with `add_markers`:
```json
{"fileName": "bread description"}
```
```text
wrote 5 chapters to C:\Users\you\ZeyoCut\projects\bread\exports\bread description.txt (1 scene marker left out); paste them into the video's description:
0:00 Intro
0:42 Ingredients
1:35 Mixing the dough
3:00 Baking
4:22 Tasting
```

---

## Analysis and cutting

### `analyze_video`

Analyzes a video or audio asset in the background, then saves the results next to the asset:

- **Scene cuts**, with ffmpeg `scdet`. A [redaction](#apply_redaction)'s box isn't held across them.
- **Noise floor and silences.** The noise floor is the peak level of the quietest 50 ms windows. Sound below noise floor + 6 dB counts as silence.
- **A transcript with a time for every word**, from whisper-cli. Saved as `<name>.transcript.json` and `<name>.srt`. Whisper sometimes places the words next to a long pause inside it, so the word times are fitted to the measured silences: such words move back onto the speech they belong to.

The results go to `<name>.analysis.json`. Poll the returned `analyze_N` job with `get_job_status`.

| Parameter | Default | Notes |
|---|---|---|
| `assetId` | required | Video or audio asset |
| `language` | `auto` | Whisper language code such as `en`, `de` or `ja` |

Whisper is found from `WhisperPath`/`WhisperModel` in `appsettings.json`, the `WHISPER_PATH`/`WHISPER_MODEL` environment variables, or `whisper-cli` on `PATH`. Without it, the job still finds scene cuts and silences, and the result says why there is no transcript.

```json
{ "assetId": "talk", "language": "en" }
```
```text
analyzing 'talk' in the background as analyze_1: scene cuts, noise floor and silences, transcript with whisper (ggml-small.bin).
Poll get_job_status with jobId "analyze_1".
```

### `get_transcript`

Returns the transcript as phrases with their source time ranges. `[n]` before a word is its index for `cut_by_transcript`, and pauses of 0.5 s or more start a new line. Times are source seconds of the asset, not timeline times. `startTime` and `endTime` limit the output to part of the transcript.

```json
{ "assetId": "talk" }
```
```text
transcript of 'talk' (en, 28 words). Times are source seconds; [n] = word index for cut_by_transcript.
0.12–3.59  [0] Welcome [1] to [2] Zia-Cut, [3] the [4] video [5] editor [6] you [7] drive [8] from [9] a [10] chat.
   (pause 2.57s)
6.16–6.34  [11] Um,
   (pause 0.534s)
6.87–9.46  [12] so, [13] let [14] me [15] show [16] you [17] how [18] cutting [19] works.
   (pause 2.446s)
11.90–14.56  [20] Long [21] pauses [22] and [23] filler [24] words [25] come [26] out [27] automatically.
```
```json
{ "assetId": "talk", "startTime": 4, "endTime": 8 }
```
```text
transcript of 'talk' (en, 28 words; showing 6 between 4 and 8s). Times are source seconds; [n] = word index for cut_by_transcript.
6.16–6.34  [11] Um,
   (pause 0.534s)
6.87–8.20  [12] so, [13] let [14] me [15] show [16] you
```
Before `analyze_video`:
```text
An error occurred invoking 'get_transcript': asset 'talk' has no transcript yet; run analyze_video first
```

### `view_source`

Renders a contact sheet of a video asset's own frames, not the timeline. Each frame is labeled with its source time and frame number, which helps find trim and cut points. It shows the footage as it is, without [redactions](#redaction).

| Parameter | Default |
|---|---|
| `assetId` | required |
| `startTime`, `endTime` | 0, end of clip |
| `count` | 12 (1–64) |
| `columns` | 4 |
| `maxWidth` | 1600 |

```json
{ "assetId": "talk", "count": 6, "columns": 3, "maxWidth": 900 }
```
```text
source frames of 'talk' 900x390 (left to right, top to bottom): f0=0.000s, f95=3.167s, f190=6.333s, f286=9.533s, f381=12.700s, f477=15.900s; saved C:\Users\you\ZeyoCut\projects\talk-demo\renders\source_talk_000000-000477_6.png
```
followed by an `image/png` content block.

### `remove_silences`

Cuts the silent parts out of a video or audio layer, with ripple.

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | required | Video or audio layer |
| `minSilence` | 0.7 | Shortest pause to cut, seconds |
| `padding` | 0.1 | Quiet kept next to the remaining sound, seconds. Not kept at the clip's start or end |
| `mode` | `audio` | `audio`: measures the clip's noise floor and cuts sound below noise floor + 6 dB (ffmpeg `silencedetect`). `transcript`: cuts the gaps between spoken words instead (needs `analyze_video`), which works under background music |
| `thresholdDb` | | `audio` mode: a fixed threshold in dBFS instead of the measured one |
| `dryRun` | false | Only report the planned cuts and the new duration |
| `punchIn` | false | Zoom every second piece to `punchInZoom` (default 1.1) to hide the jump cuts |

```json
{ "layerId": "talk", "dryRun": true }
```
```text
dry run, nothing changed. noise floor -70.5 dB (peak level of the quietest 50 ms windows), loud parts -1.1 dB → silence below -64.5 dB
3 cut(s), 5.63s removed; duration 15.933s → 10.303s
- timeline 3.808–6.06s (source 3.808–6.06s, 2.252s)
- timeline 9.558–11.804s (source 9.558–11.804s, 2.246s)
- timeline 14.801–15.933s (source 14.801–15.933s, 1.132s)
```
```json
{ "layerId": "talk", "punchIn": true }
```
```text
noise floor -70.5 dB (peak level of the quietest 50 ms windows), loud parts -1.1 dB → silence below -64.5 dB
3 cut(s), 5.63s removed; duration 15.933s → 10.303s
- timeline 3.808–6.06s (source 3.808–6.06s, 2.252s)
- timeline 9.558–11.804s (source 9.558–11.804s, 2.246s)
- timeline 14.801–15.933s (source 14.801–15.933s, 1.132s)
pieces: talk, talk_2, talk_3
punched in to 1.1x: talk_2
```

### `cut_by_transcript`

Removes words from a video or audio layer by their `[n]` indices from `get_transcript`, with ripple. Each range is cut from its first word's start to the next kept word's start, so the pause after the removed words goes too. If the cut would leave less than 0.3 s without words at the clip's edge, such as the padding `remove_silences` keeps, that sliver goes as well. Pass the piece that plays the words: after `remove_silences` that may be `talk_2` rather than `talk`.

| Parameter | Notes |
|---|---|
| `layerId` | Video or audio layer |
| `wordRanges` | `[{ "from": n, "to": m }]`, inclusive |
| `dryRun` | Only report the planned cuts |

```json
{ "layerId": "talk_2", "wordRanges": [ { "from": 11, "to": 12 } ], "dryRun": true }
```
```text
dry run, nothing changed. Would remove: Um, so,
1 cut(s), 1.397s removed; duration 10.303s → 8.906s
- timeline 3.808–5.205s (source 6.06–7.457s, 1.397s)
```
```json
{ "layerId": "talk_2", "wordRanges": [ { "from": 11, "to": 12 } ] }
```
```text
removed: Um, so,
1 cut(s), 1.397s removed; duration 10.303s → 8.906s
- timeline 3.808–5.205s (source 6.06–7.457s, 1.397s)
pieces: talk_2
```

---

## Optional: describe_clip

`describe_clip` is **not on the server** unless a clip describer is configured. No describer is built in yet; Gemini, with its native video input, is the one planned. Until one is added, the server has the 63 tools above and nothing here applies. There is no real-server example yet. The examples below are from the tests, with a stand-in describer named `fake`.

**Turning it on** (once a describer exists):

- In `appsettings.json`, set `ZeyoCut:ClipDescriber:Provider` (for example `"gemini"`), or set the environment variable `ZEYOCUT_DESCRIBER_PROVIDER`.
- Put the API key in an environment variable. By default that's `ZEYOCUT_DESCRIBER_API_KEY`; `ZeyoCut:ClipDescriber:ApiKeyVariable` names another one. The key is never read from a file.
- The server log says on every start whether `describe_clip` is on, or why it's off:
```text
describe_clip is off: there is no clip describer named 'gemini' in this build (available: none yet)
```

**Adding a describer** (for developers): implement `IClipDescriber` (`src\ZeyoCut.Render\Video\ClipDescriber.cs`) and list it in `ClipDescribers.Providers` (`src\ZeyoCut.Mcp\ClipDescribers.cs`) under its config name. The describer gets the file, the asset and layer ids, and a source-time range. It returns moments `{time, description}` in source seconds.

### `describe_clip`

Describes what happens in a stretch of video, with times. The describer uploads the segment to its service, and each call may cost money. Runs in the background as `describe_N`; poll [`get_job_status`](#get_job_status).

- With `assetId`, the times are source seconds of the file.
- With `layerId`, the times are timeline seconds while the layer plays. They are turned into source seconds (trim and speed) for the describer, and the answers come back in timeline seconds with the source time beside them.
- Moments outside the range (beyond half a second) or without text are left out with a warning.

| Parameter | Default | Notes |
|---|---|---|
| `startTime` | required | Seconds |
| `endTime` | required | Seconds; at most 60 s after `startTime` |
| `assetId` | | A video asset; give this or `layerId` |
| `layerId` | | A video layer; give this or `assetId` |

```json
{ "layerId": "dogs", "startTime": 3, "endTime": 5 }
```
```text
describing layer 'dogs' 3–5s with fake in the background as describe_1.
Poll get_job_status with jobId "describe_1".
```
The job's result (layer `dogs` starts at 2s, trims in at 4s and plays at 2x speed):
```text
layer 'dogs' 3–5s on the timeline (source 6–10s of 'dogs'), described by fake: 2 moment(s)
- 3.25s (source 6.5s): a dog runs in from the left and barks
- 5s (source 10s): the dog sits down
```

---

## Captions

### `generate_captions`

Turns a transcript into caption text layers under a null layer, `captions`, in one undo step.

- Word times are mapped through the clips that play the asset, so silences and words cut before this call are followed.
- Captions don't follow later cuts. Run the tool again after editing; it replaces the old captions.
- A cue holds up to `maxLines` balanced lines and ends at a sentence end or a pause.

| Parameter | Default | Notes |
|---|---|---|
| `assetId` or `layerId` | one is required | All clips playing the asset, or a single layer |
| `style` | `subtitle` | `subtitle`: white on a dark box at the bottom. `bold_center`: large bold text with a black outline, mid-screen (lower on vertical canvases). `minimal`: small outlined text at the bottom. Or the name of a library style (`save_style`), scaled to the canvas and placed at its `captionPosition` |
| `maxWordsPerLine` | 6 | 1–20 |
| `maxLines` | 2 | 1–4 |
| `fontFamily` | `Segoe UI` | `Myanmar Text` for Burmese, `Yu Gothic` for Japanese |
| `fontSize` | scaled to the canvas | Pixels |
| `color` | `#FFFFFF` | |
| `groupId` | `captions` | Id of the caption group |
| `behindSubject` | `off` | `on`: the captions go behind the person in the clip. `auto`: also in front wherever the person would cover too much of a caption. See [below](#captions-behind-the-subject). `off` takes away the cutout an earlier `on` or `auto` made |
| `occlusionThreshold` | 35 | `auto`: how much of a caption, in percent, the person may cover before the captions show in front |
| `confirmLongJob` | false | `behindSubject`: an AI matte that would take more than 5 minutes isn't started, and the reply gives the estimate |

```json
{ "assetId": "talk", "style": "bold_center", "maxWordsPerLine": 4 }
```
```text
added captions under 'captions': 4 cue(s) from 26 words
- 0.12–2.88s "Welcome to Zia-Cut, the / video editor you drive"
- 2.88–3.808s "from a chat."
- 3.808–6.008s "let me show you / how cutting works."
- 6.008–8.906s "Long pauses and filler / words come out automatically."
captions don't follow later cuts; run generate_captions again after editing (it replaces them)
```
After `reframe_to_vertical`, running it again with shorter lines:
```json
{ "assetId": "talk", "style": "bold_center", "maxWordsPerLine": 3 }
```
```text
replaced captions under 'captions': 6 cue(s) from 26 words (removed 4 old caption layer(s))
- 0.12–2.3s "Welcome to Zia-Cut, / the video editor"
- 2.3–3.808s "you drive from / a chat."
- 3.808–5.2s "let me show / you how cutting"
- 5.393–6.008s "works."
- 6.008–7.574s "Long pauses and / filler words come"
- 7.574–8.906s "out automatically."
captions don't follow later cuts; run generate_captions again after editing (it replaces them)
```

#### Captions behind the subject

`behindSubject` puts the captions behind the person in the clip, the way [`text_behind_subject`](#text_behind_subject) does: a subject cutout draws the person again above them.

- **The captions get a cutout of their own,** `captions_subject` (`for=captions` in `list_layers`), so its fades leave titles behind the same person alone. It follows the clips that play the asset.
- **Its matte:** a new one takes the matte of the clip's cutout from `text_behind_subject`, or else `aiVideo`, computed in the background as `matte_N`. Running the tool again keeps the cutout and its matte; `refine_matte` works on it.
- **A clip whose background is already removed** is its own subject: the captions move directly below it, with no cutout.
- **`on`** keeps the captions behind all the time.
- **`auto`** measures, frame by frame, how much of each caption the person covers. Wherever that is more than `occlusionThreshold` percent, the captions show in front:
    - the cutout fades out over 0.25 s, finished when such a span starts, and back in over 0.25 s after it ends;
    - spans less than 0.5 s apart join, so the person doesn't flicker over the text;
    - with a clip that is its own subject there is nothing to fade, so a caption it would cover too much draws in front of it for its whole time.
- **Up to 600 caption frames, with the matte computed,** `auto` answers directly. Otherwise the captions are added at once (behind the person), and a background job (`captions_N`) waits for the matte, measures them and adds the fades as their own undo step. If the project changes in the meantime, the job refuses; run the tool again.

The examples use the presenter from [Text behind the subject](#text-behind-the-subject), now talking: a 13 s clip (`presenter`, from the asset `talk`) with a four-sentence transcript.

```json
{ "assetId": "talk", "style": "bold_center", "behindSubject": "auto" }
```
```text
added captions under 'captions': 4 cue(s) from 29 words
- 0.12–1.74s "Welcome aboard the station."
- 3.14–5.684s "Today I will show you / where we live and work."
- 7.029–9.671s "This window is where we / watch the earth go by."
- 11.02–12.15s "Come and take a look."
captions don't follow later cuts; run generate_captions again after editing (it replaces them)
put the captions behind the subject of 'presenter': cutout 'captions_subject' draws the subject again above them, cut out with aiVideo (zIndex 10)
- the cutout follows the clip that plays 'talk' ('presenter'): its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity
- the matte is computed in the background as matte_1: 318 frames of clip 'talk' with Robust Video Matting (MobileNetV3) on the CPU, about 40 s (a rough guess; system_check measures this computer)
- until it is done, the subject isn't drawn over the text in previews yet (the text shows in front) and exports refuse; poll get_job_status with jobId "matte_1"
- auto: 191 caption frames are measured in the background as captions_1 once matte_1 is done, and the fades are added as their own undo step (an edit made meanwhile makes it refuse, so run generate_captions again then); until then the captions are behind the subject the whole time; poll get_job_status with jobId "captions_1"
- check it with render_frame (layerId="captions_subject" checkerboard=true shows the subject alone; refine_matte shrinks or softens its edge); text_occlusion measures how much of a caption it covers
```
`bold_center` puts the captions on the presenter's face, so each one is covered too much somewhere and shows in front for its whole time:
```text
captions_1: completed in 27.6s — generate_captions behindSubject=auto: fades for 'captions_subject' (191 caption frames)
auto: the subject covers up to 100.0% of a caption ('captions_4' at 11.042s; 191 frames measured); where it covers more than 35%, the captions show in front: 'captions_subject' fades out over 0.25s before each of these spans and back in after
- in front 0.125–1.75s ('captions_1', up to 40.7%)
- in front 3.167–5.708s ('captions_2', up to 64.2%)
- in front 7.042–9.708s ('captions_3', up to 98.2%)
- in front 11.042–12.167s ('captions_4', up to 100.0%)
```
```text
captions_subject [cutout]  z=10  follows=presenter  for=captions  effects: matte(aiVideo)  keys: opacity(15)
```
Moved up to the hair line (`update_layer` on `captions` with `"y": -260`) and generated again, the matte is computed, so it answers directly (191 frames in under 5 s). The head now covers less of the first caption at its start, so that caption stays behind until 1.125 s:
```json
{ "assetId": "talk", "style": "bold_center", "behindSubject": "auto" }
```
```text
replaced captions under 'captions': 4 cue(s) from 29 words (removed 4 old caption layer(s))
...
put the captions behind the subject of 'presenter' with their cutout 'captions_subject' (aiVideo; zIndex 10)
- the cutout follows the clip that plays 'talk' ('presenter'): its timing, trims, speed, moves, effects and keyframes; its own settings are only name, zIndex, visible and opacity
- clip 'talk': the matte is already computed: assets/talk.matte-aivideo-4a8f2ae0.mkv
- auto: the subject covers up to 56.8% of a caption ('captions_2' at 5.333s; 191 frames measured); where it covers more than 35%, the captions show in front: 'captions_subject' fades out over 0.25s before each of these spans and back in after
- in front 1.125–1.75s ('captions_1', up to 40.1%)
- in front 3.167–5.708s ('captions_2', up to 56.8%)
- in front 7.042–9.708s ('captions_3', up to 54.3%)
- in front 11.042–12.167s ('captions_4', up to 45.1%)
- check it with render_frame (layerId="captions_subject" checkerboard=true shows the subject alone; refine_matte shrinks or softens its edge); text_occlusion measures how much of a caption it covers
```
At 0.6 s, "aboard" goes behind the hair; at 1.5 s the whole caption is in front. `text_occlusion` shows the fade:
```text
text_occlusion 'captions_1' 0.125–1.708s (f3–f41, 39 frames): covered min 0.0%, max 32.5%, average 15.0%
- f3–f5 (0.125–0.208s): 20.9–21.9%
- f6–f9 (0.25–0.375s): 22.9–23.8%
...
- f19–f20 (0.792–0.833s): 31.5%
- f21 (0.875s): 32.5%
- f22 (0.917s): 28.0%
- f23 (0.958s): 22.3%
- f24 (1s): 17.3%
- f25 (1.042s): 11.5%
- f26 (1.083s): 6.0%
- f27–f41 (1.125–1.708s): 0.0%
per frame: C:\Users\you\ZeyoCut\projects\talkshow\renders\text_occlusion_captions_1_00003-00041.json
```
With `"occlusionThreshold": 50`, only the two longer captions come in front, the second of them for part of its time:
```text
- auto: the subject covers up to 56.8% of a caption ('captions_2' at 5.333s; 191 frames measured); where it covers more than 50%, the captions show in front: 'captions_subject' fades out over 0.25s before each of these spans and back in after
- in front 3.167–5.708s ('captions_2', up to 56.8%)
- in front 7.042–8.75s ('captions_3', up to 54.3%)
```
`"behindSubject": "on"` leaves out the measuring (`occlusionThreshold` is then noted as unused), and generating with `behindSubject` left out (`off`) takes the cutout away:
```text
removed 'captions_subject', the captions' subject cutout, since behindSubject is off
```
The cutout changes its method only when it is made:
```json
{ "layerId": "captions_subject", "method": "chroma" }
```
```text
An error occurred invoking 'remove_background': 'captions_subject' is the subject cutout of the captions under 'captions'; generate_captions gives it its method when it makes it (aiVideo, or that of the clip's cutout from text_behind_subject); refine_matte changes its edges
```

### `add_caption_track`

Adds the captions of one [language](#languages) as a track of their own: text layers under a null layer `captions_<language>` (`captions_ja`), tagged with that language, so each language's version shows only its own captions. Adding a language's track again replaces it. One undo step.

| Parameter | Default | Notes |
|---|---|---|
| `language` | required | `en`, `ja`, `my`, `pt-BR`, … |
| `cues` | | `[{ "start", "end", "text" }]`, timeline seconds; `\n` breaks a line. For example a translation written from `get_transcript` |
| `srt` | | SRT text, or the path of a `.srt` file. Its times are timeline times |
| `fromTranscript` | | An analyzed asset: its transcript's words, timed through the clips that play it as in `generate_captions`, up to 6 words a line and 2 lines a caption |
| `style` | `subtitle` | As in `generate_captions` |
| `fontFamily` | the language's font, otherwise the style's | |
| `fontSize` | scaled to the canvas | Pixels |
| `color` | `#FFFFFF` | |

Give exactly one of `cues`, `srt` and `fromTranscript`.

- A cue with no text, or starting at or after the project's end, is left out with a warning. A cue running past the end is shortened to it, and overlapping cues are named, since both show at once.
- In SRT, the numbers are optional, `,` or `.` both work before the milliseconds, and tags such as `<i>` and `{\an8}` are removed.
- When the transcript is in another language than `language`, the reply says so.

```json
{ "language": "en", "fromTranscript": "talk", "style": "bold_center" }
```
```text
added the en caption track 'captions_en': 2 caption(s), font Segoe UI
- 0.15–2.374s "Hello, and welcome to the show."
- 2.95–4.21s "Today we make bread."
'captions_en' is only in the en version: give language en to render_frame or start_export to see it; export_srt with groupId captions_en writes it as subtitles
```
```json
{ "language": "ja", "style": "bold_center", "cues": [
  { "start": 0.3, "end": 2.0, "text": "こんにちは、番組へようこそ。" },
  { "start": 2.2, "end": 4.2, "text": "今日はパンを作ります。" },
  { "start": 4.5, "end": 6, "text": "では、始めましょう。" } ] }
```
```text
added the ja caption track 'captions_ja': 3 caption(s), font Yu Gothic
- 0.3–2s "こんにちは、番組へようこそ。"
- 2.2–4.2s "今日はパンを作ります。"
- 4.5–5s "では、始めましょう。"
'captions_ja' is only in the ja version: give language ja to render_frame or start_export to see it; export_srt with groupId captions_ja writes it as subtitles
Warnings:
- cue 3 ("では、始めましょう。") runs past the project end (5s), so it ends there
```
```json
{ "language": "my", "style": "bold_center", "srt": "C:\\videos\\greeting my.srt" }
```
```text
added the my caption track 'captions_my': 2 caption(s), font Myanmar Text
- 0.3–2s "မင်္ဂလာပါ၊ ရှိုးကို ကြိုဆိုပါတယ်။"
- 2.2–4.2s "ဒီနေ့ ပေါင်မုန့် လုပ်ကြမယ်။"
'captions_my' is only in the my version: give language my to render_frame or start_export to see it; export_srt with groupId captions_my writes it as subtitles
```
```json
{ "language": "fr", "srt": "C:\\videos\\greeting my.srt", "fromTranscript": "talk" }
```
```text
An error occurred invoking 'add_caption_track': give only one of cues, srt and fromTranscript
```

### `import_srt`

Imports a `.srt` file as one [language](#languages)'s caption track, as `add_caption_track` does with `srt`: text layers under `captions_<language>`, replacing that language's track if there is one. The cue times are timeline times. One undo step.

| Parameter | Default | Notes |
|---|---|---|
| `path` | required | The `.srt` file |
| `language` | from the file name | `en`, `ja`, `my`, `pt-BR`, …. Without it, the file name has to end with the language after a `.` or `_`, as `talk.ja.srt` and `talk_pt-BR.srt` do |
| `style`, `fontFamily`, `fontSize`, `color` | | As in `add_caption_track` |

```json
{"path": "C:\\videos\\talk.ja.srt"}
```
```text
imported talk.ja.srt (language ja from the file name; give language to change it)
added the ja caption track 'captions_ja': 2 caption(s), font Yu Gothic
- 1–3.5s "こんにちは、みなさん。"
- 4–6s "今日はパンを焼きます。"
'captions_ja' is only in the ja version: give language ja to render_frame or start_export to see it; export_srt with groupId captions_ja writes it as subtitles
```
```json
{"path": "C:\\videos\\talk.srt"}
```
```text
An error occurred invoking 'import_srt': which language are the captions in? 'talk.srt' doesn't end with one (as talk.ja.srt does); give language, e.g. en, ja, my or pt-BR
```
```json
{"path": "C:\\videos\\talk.srt", "language": "en"}
```
```text
imported talk.srt
added the en caption track 'captions_en': 2 caption(s), font Segoe UI
- 1–3.5s "Hello, everyone."
- 4–6s "Today we bake bread."
'captions_en' is only in the en version: give language en to render_frame or start_export to see it; export_srt with groupId captions_en writes it as subtitles
```

### `export_srt`

Writes the captions as an `.srt` file to `exports\<fileName>`, with timeline times. `groupId` (default `captions`) picks another caption group, such as a language's track.

```json
{ "fileName": "talk" }
```
```text
wrote 4 subtitle(s) to C:\Users\you\ZeyoCut\projects\talk-demo\exports\talk.srt (0.12–8.906s)
```
```json
{ "fileName": "greeting_ja.srt", "groupId": "captions_ja" }
```
```text
wrote 3 subtitle(s) to C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_ja.srt (0.3–5s)
```

---

## Languages

One project can hold a version of the video per language: each language's titles, captions and voice over the same picture.

- **Tags.** `language` on a layer is a language code such as `en`, `ja`, `my` or `pt-BR`. `add_image_layer`, `add_text_layer`, `add_audio_layer` and `add_video_layer` take it, and `update_layer` changes it (`""` takes it off). It is stored in its usual form, so `pt_br` becomes `pt-BR`.
- **What each version holds:**
    - Untagged layers are in every version.
    - A tagged layer and its children are only in that language's version. A [caption track](#add_caption_track) is a tagged group, so its captions are too.
    - A tagged video's tag is the language of its sound. Its picture is in every version, and the other languages' versions mute it. To keep a clip's sound in every version, for subtitled versions, leave it untagged.
    - A subject cutout is in the versions its sources are in, and a redaction hides in every version, so neither takes a tag.
    - A child can't have another language than a tagged parent or ancestor, since it is only in that one's version.
- **Fonts.** A text given a `language` but neither `fontFamily` nor `fontAsset` gets that language's font, even over a style's font: `Myanmar Text` for `my` and `Yu Gothic` for `ja`, or what `LanguageFonts` in `appsettings.json` says (see the README's Configuration). A tag with no font of its own uses a shorter tag's: `zh-Hant-TW`, then `zh-Hant`, then `zh`. [`check_project`](#check_project) shapes every text with HarfBuzz and names the characters its font has no glyphs for.
- **One version at a time.** [`render_frame`](#render_frame), [`render_contact_sheet`](#render_contact_sheet), [`start_export`](#start_export) and [`export_thumbnail`](#export_thumbnail) take `language`. Without it they show every layer, and warn when the project has two languages or more. [`export_language_versions`](#export_language_versions) writes one file per language and preset in one job.

A clip whose sound is English, a Japanese dub and a title per language. `add_video_layer`:
```json
{ "assetId": "talk", "language": "en" }
```
```text
added video layer 'talk': source 0–5s at 1x → timeline 0–5s, scale 1, z=0
the project was extended from 1s to 5s to fit the clip
{"type":"video","id":"talk","name":"talk","start":0,"zIndex":0,"parentId":null,"visible":true,"language":"en","anchorX":640,"anchorY":360,"asset":"talk", ...}
```
`add_audio_layer`:
```json
{ "asset": "dub_ja", "role": "voice", "language": "ja" }
```
```text
added audio layer 'dub_ja' at 0s
{"type":"audio","id":"dub_ja","name":"dub_ja","start":0,"end":null,"zIndex":0,"parentId":null,"visible":true,"language":"ja","asset":"dub_ja", ...}
```
`add_text_layer` with a `language` and no font:
```json
{ "text": "ようこそ！", "id": "title_ja", "fontSize": 96, "color": "#FFFFFF", "strokeColor": "#000000", "y": 140, "end": 2, "language": "ja" }
```
```text
added text layer 'title_ja', box 480x153.797 px, anchor (240,76.898), z=2
{"type":"text","id":"title_ja", ... ,"language":"ja", ... ,"text":"ようこそ！","fontFamily":"Yu Gothic", ...}
```
```json
{ "text": "Hi", "language": "english" }
```
```text
An error occurred invoking 'add_text_layer': language 'english' isn't a language code; use one such as en, ja, my or pt-BR
```

With a Burmese title and a [caption track](#add_caption_track) per language added, `list_layers` names the languages and shows each layer's (`lang=`, and `(sound)` on a video):
```text
greeting: 1280x720 @ 30 fps, 5s, 15 layer(s), 2 asset(s)
languages: en, ja, my
talk [video]  z=0  t=0–5  lang=en (sound)  asset=talk source=0–5s speed=1  volume=1  anchor=(640,360)
dub_ja [audio]  t=0–end  lang=ja  asset=dub_ja volume=1
title_en [text]  z=1  t=0–2  lang=en  text="Welcome!"  font=Segoe UI 96px  anchor=(210.188,63.844)
title_ja [text]  z=2  t=0–2  lang=ja  text="ようこそ！"  font=Yu Gothic 96px  anchor=(240,76.898)
title_my [text]  z=3  t=0–2  lang=my  text="ကြိုဆိုပါတယ်"  font=Myanmar Text 80px  anchor=(220.469,74.434)
captions_en [image]  "captions (en)"  z=13  t=0–end  lang=en  null (no drawings)  anchor=(0,0)  ignoreCamera
├─ captions_en_1 [text]  "Hello, and welcome to the show."  z=13  t=0.15–2.374  text="Hello, and welcome to the show."  font=Segoe UI 50px  anchor=(550.4,34.915)
└─ captions_en_2 [text]  "Today we make bread."  z=13  t=2.95–4.21  text="Today we make bread."  font=Segoe UI 50px  anchor=(550.4,34.915)
captions_ja [image]  "captions (ja)"  z=23  t=0–end  lang=ja  null (no drawings)  anchor=(0,0)  ignoreCamera
├─ captions_ja_1 [text]  "こんにちは、番組へようこそ。"  z=23  t=0.3–2  text="こんにちは、番組へようこそ。"  font=Yu Gothic 50px  anchor=(550.4,42.054)
├─ captions_ja_2 [text]  "今日はパンを作ります。"  z=23  t=2.2–4.2  text="今日はパンを作ります。"  font=Yu Gothic 50px  anchor=(550.4,42.054)
└─ captions_ja_3 [text]  "では、始めましょう。"  z=23  t=4.5–5  text="では、始めましょう。"  font=Yu Gothic 50px  anchor=(550.4,42.054)
captions_my [image]  "captions (my)"  z=33  t=0–end  lang=my  null (no drawings)  anchor=(0,0)  ignoreCamera
├─ captions_my_1 [text]  "မင်္ဂလာပါ၊ ရှိုးကို ကြိုဆိုပါတယ်။"  z=33  t=0.3–2  text="မင်္ဂလာပါ၊ ရှိုးကို ကြိုဆိုပါတယ်။"  font=Myanmar Text 50px  anchor=(550.4,49.975)
└─ captions_my_2 [text]  "ဒီနေ့ ပေါင်မုန့် လုပ်ကြမယ်။"  z=33  t=2.2–4.2  text="ဒီနေ့ ပေါင်မုန့် လုပ်ကြမယ်။"  font=Myanmar Text 50px  anchor=(550.4,49.975)
```

`render_frame` without `language` draws all three titles and caption tracks over each other:
```json
{ "time": 1.0, "maxWidth": 640 }
```
```text
frame 30 of 150 at t=1.000s; image 640x360; saved C:\Users\you\ZeyoCut\projects\greeting\renders\frame_00030.png
Warnings:
- this shows the layers of every language (en, ja, my); give language to see one version
```
With `language`, only that version's title and captions, and the English sound muted:
```json
{ "time": 1.0, "maxWidth": 640, "language": "ja" }
```
```text
frame 30 of 150 at t=1.000s; ja version, 8 layer(s) of other languages left out, sound of 'talk' muted; image 640x360; saved C:\Users\you\ZeyoCut\projects\greeting\renders\frame_00030.png
```
```json
{ "time": 1.0, "maxWidth": 640, "language": "fr" }
```
```text
frame 30 of 150 at t=1.000s; fr version, 14 layer(s) of other languages left out, sound of 'talk' muted; image 640x360; saved C:\Users\you\ZeyoCut\projects\greeting\renders\frame_00030.png
Warnings:
- no layer is tagged fr, so this shows only the untagged layers; languages in the project: en, ja, my
```
```json
{ "time": 1.0, "layerId": "title_ja", "language": "en" }
```
```text
An error occurred invoking 'render_frame': 'title_ja' isn't in the en version; leave out language to draw it
```

---

## Library

The library is a folder shared by all projects, `%USERPROFILE%\ZeyoCut\library\` by default. Set `LibraryPath` in `appsettings.json` or `ZEYOCUT_LIBRARY_DIR` to move it. It is created on the first save.

```text
library\
  rigs\<name>\        rig.json and the drawings in assets\
  actions\<name>.json
  styles\<name>.json  plus the font file when the style uses an imported font
  templates\<name>\   template.json and the asset files in assets\
  sfx\                your sound effects for add_sfx; subfolders are categories
```

- **Names:** up to 64 letters, digits, spaces, `_` and `-`, not case-sensitive. Saving under an existing name needs `overwrite: true`.
- **Undo:** saving doesn't change the open project, so `save_*` aren't undo steps. `add_rig`, `apply_action` and `apply_style` are one undo step each.

**Part roles.** Actions find a rig's parts by role, not by layer id. An action recorded on `tora_*` therefore plays on `mimi_*`, or on any other rig with the same part names.

- The root's role is always `root`.
- Another part's role is its `rigRole` if set (`update_layer` with `{"rigRole": "arm_l"}`). Otherwise it's the id without the prefix the part ids share: `tora_head` and `tora_tail` become `head` and `tail`.
- Two parts with the same role are an error that suggests a `rigRole`.

```json
{ "layerId": "tora_tail", "changes": { "rigRole": "root" } }
```
```text
An error occurred invoking 'update_layer': layer 'tora_tail': rigRole 'root' is invalid: use letters, digits, '_' or '-' (and not 'root', which always means the rig's root)
```

### `save_rig`

Saves a rig: the root layer and every layer parented under it, with anchors, drawings, parenting and draw order. Video layers can't be part of a rig.

- The rig is saved in the pose it has at `time`, without keyframes. Save the motion with `save_action`.
- In the library, the parts are named by role. `add_rig` turns them back into layer ids.

| Parameter | Default | Notes |
|---|---|---|
| `rootLayerId` | required | Often a null layer |
| `name` | required | Library name |
| `time` | the root's start | Pose to save |
| `overwrite` | false | |

```json
{ "rootLayerId": "tora_body", "name": "Tora" }
```
```text
saved rig 'Tora' to C:\Users\you\ZeyoCut\library\rigs\Tora: 3 part(s): body, head, tail; 4 drawing(s), 4 asset file(s)
roles: root, head, tail
next: add_rig places it in any project; save_action stores its animation
```
```json
{ "rootLayerId": "tora_body", "name": "tora" }
```
```text
An error occurred invoking 'save_rig': the library already has a rig named 'tora'; pass overwrite=true to replace it
```

### `add_rig`

Places a rig in the open project, on top of the existing layers, keeping the rig's own part order.

- Layer ids are `<instanceName>_<part>`.
- The drawing files are copied into `assets\` once. Later copies reuse assets with the same file content.

| Parameter | Default | Notes |
|---|---|---|
| `name` | required | Rig name |
| `x`, `y` | canvas center | Where the root's anchor lands |
| `startTime` | 0 | When the rig appears |
| `scale` | 1 | Multiplies the root's scale, up to 100 |
| `instanceName` | the rig name | Numbered when taken: `tora`, `tora_2`, … |

```json
{ "name": "tora", "x": 400, "y": 600 }
```
```text
added rig 'tora' as 'tora_body' with 3 layer(s): tora_body, tora_head, tora_tail
roles: root=tora_body, head=tora_head, tail=tora_tail
imported 4 drawing file(s)
next: apply_action with rigRootId 'tora_body', or set_keyframes on its parts
```
```json
{ "name": "Tora", "x": 900, "y": 600, "scale": 0.8, "instanceName": "mimi" }
```
```text
added rig 'Tora' as 'mimi_body' with 3 layer(s): mimi_body, mimi_head, mimi_tail
roles: root=mimi_body, head=mimi_head, tail=mimi_tail
reused 4 asset(s) already in the project
next: apply_action with rigRootId 'mimi_body', or set_keyframes on its parts
```

### `save_action`

Saves a rig's animation between two times as a reusable action: a walk cycle, a jump, a wing flap, a double-take.

- **What is stored:** for each part, every property that changes in the window. That is the curve's value at both window edges plus the keyframes in between, with times relative to `startTime`. Drawing swaps are kept too.
- **Relative root motion:** the root's `x`, `y` and `rotation` are stored as offsets from the start of the window, and its `scaleX`/`scaleY` as factors. The action therefore plays wherever the rig stands. Everything else is stored as it is.
- For a cycle, end the window on the pose the cycle starts with.

| Parameter | Default | Notes |
|---|---|---|
| `rigRootId` | required | |
| `startTime`, `endTime` | required | The window, seconds |
| `name` | required | Library name |
| `overwrite` | false | |

```json
{ "rigRootId": "tora_body", "startTime": 0, "endTime": 0.8, "name": "hop" }
```
```text
saved action 'hop': 0.8s, parts root, head, tail (4 track(s): root.x, root.y, head.drawing, tail.rotation)
next: apply_action on any rig with these part roles
```

### `apply_action`

Plays a saved action on a rig in the open project.

- It replaces the keyframes of the properties it moves, between its start and end.
- With `loops` > 1, the root's offsets carry on from loop to loop, so a walk keeps walking.
- Every role the action moves must exist in the rig, and swapped drawings must exist on the part.
- A warning says when the action runs past the project's end.

| Parameter | Default | Notes |
|---|---|---|
| `name` | required | Action name |
| `rigRootId` | required | |
| `startTime` | required | Seconds |
| `speed` | 1 | 0.05–20. 2 plays twice as fast |
| `loops` | 1 | 1–1000 |

```json
{ "name": "hop", "rigRootId": "tora_body", "startTime": 1, "loops": 3 }
```
```text
applied action 'hop' to 'tora_body' from 1s to 3.4s (3 loops): 27 keyframe(s) on 4 track(s)
```
```json
{ "name": "hop", "rigRootId": "mimi_body", "startTime": 2, "speed": 2 }
```
```text
applied action 'hop' to 'mimi_body' from 2s to 2.4s: 11 keyframe(s) on 4 track(s)
```
```json
{ "name": "wave", "rigRootId": "mimi_body", "startTime": 0 }
```
```text
An error occurred invoking 'apply_action': no action named 'wave' in the library; actions: hop
```

### `save_style`

Saves a text layer's look as a named style:

- font: the installed family, or the imported font file, which is copied next to the style
- bold, size, color, outline, alignment, line height and background box

Sizes are stored relative to the canvas's shorter side. On a 1080x1920 canvas, a style saved on 1280x720 comes out 1.5× bigger.

Use a style with `apply_style`, with the `style` parameter of `add_text_layer`, or as the `style` of `generate_captions`.

| Parameter | Default | Notes |
|---|---|---|
| `layerId` | required | A text layer |
| `name` | required | Library name |
| `captionPosition` | `bottom` | Where `generate_captions` places captions in this style: `bottom` or `center` |
| `overwrite` | false | |

```json
{ "layerId": "title", "name": "gold title", "captionPosition": "center" }
```
```text
saved style 'gold title': Segoe UI bold 72px #FFD700; outline #000000 6px; align left, captions at the center, sizes for a 720px canvas side
```
With `add_text_layer`; parameters you pass override the style:
```json
{ "text": "Part 2", "id": "part", "style": "gold title", "y": 220 }
```
```text
added text layer 'part', box 201.375x95.766 px, anchor (100.688,47.883), z=11
{"type":"text","id":"part", ... ,"text":"Part 2","fontFamily":"Segoe UI","fontAsset":null,"bold":true,"fontSize":72,"color":"#FFD700","strokeColor":"#000000","strokeWidth":6,"align":"left", ...}
```

### `apply_style`

Restyles a text layer, or every text layer under a group such as `captions`, in one undo step. Text, position and timing stay. Each anchor keeps its place in the text box, so centered text stays centered.

| Parameter | Default | Notes |
|---|---|---|
| `name` | required | Style name |
| `layerId` | required | A text layer, or a group of them |

```json
{ "name": "gold title", "layerId": "handle" }
```
```text
applied style 'gold title' to 1 text layer(s): handle
```

### `save_template`

Saves layers of the open project as a template: an intro, outro, end screen, watermark or any other fixed layers. The template keeps:

- the canvas size, frame rate, duration, background and audio settings
- the chosen layers with their children and keyframes
- their asset files

| Parameter | Default | Notes |
|---|---|---|
| `name` | required | Library name |
| `layerIds` | all layers | Each with its children |
| `pinToEnd` | | Layers (with their children) that keep their distance from the end, such as an end screen |
| `overwrite` | false | |

```json
{ "name": "tora show", "layerIds": ["title", "handle", "endcard"], "pinToEnd": ["endcard"] }
```
```text
saved template 'tora show': 1280x720 @ 30 fps, 4s, 3 layer(s), 0 asset(s); pinned to the end: endcard
layers: title, handle, endcard
next: new_project_from_template
```

### `new_project_from_template`

Creates a project from a template and opens it. With a new `duration`:

- layers that ran to the template's end run to the new end
- `pinToEnd` layers move with the end, keyframes included. One that would start before 0 is an error
- other layers keep their times

| Parameter | Default | Notes |
|---|---|---|
| `template` | required | Template name |
| `name` | required | Project name, also the folder name |
| `duration` | the template's | Seconds |
| `directory` | `<projectsRoot>\<name>` | Absolute folder |

```json
{ "template": "tora show", "name": "tora-ep2", "duration": 8 }
```
```text
created and opened project 'tora-ep2' from template 'tora show' at C:\Users\you\ZeyoCut\projects\tora-ep2
1280x720 @ 30 fps, 8s
tora-ep2: 1280x720 @ 30 fps, 8s, 3 layer(s), 0 asset(s)
title [text]  z=2  t=0–2  text="Episode 1"  font=Segoe UI 72px  anchor=(162.07,47.883)
handle [text]  z=3  t=0–end  text="@tora_cat"  font=Segoe UI 28px  anchor=(62.535,18.621)
endcard [text]  z=4  t=7–end  text="Subscribe!"  font=Segoe UI 64px  anchor=(145.875,42.563)
```

### `list_library`

Lists the library's rigs, actions, styles and templates, with a short description of each. `type` is `rigs`, `actions`, `styles`, `templates` or `all` (the default).

```json
{}
```
```text
library: C:\Users\you\ZeyoCut\library
rigs (1):
- Tora: 3 part(s): body, head, tail; 4 drawing(s)
actions (1):
- hop: 0.8s, parts root, head, tail (4 track(s): root.x, root.y, head.drawing, tail.rotation)
styles (1):
- gold title: Segoe UI bold 72px #FFD700; outline #000000 6px; align left, captions at the center, sizes for a 720px canvas side
templates (1):
- tora show: 1280x720 @ 30 fps, 4s, 3 layer(s), 0 asset(s); pinned to the end: endcard
```

---

## Audio for animation

Sound effects from the library, the beats of a music track, and a mouth that moves with a voice. The examples come from one Tora project at 24 fps, 6 s long.

### `list_sfx`

Lists the sound files in the library's `sfx\` folder, with their durations: wav, mp3, m4a, aac, ogg, opus and flac.

- Put your own sounds there. Subfolders become categories.
- A sound's name is its path inside `sfx\` without the extension, such as `impacts/thud`.
- Up to 200 sounds are listed. When the folder is empty, the result says where to put files.

| Parameter | Default | Notes |
|---|---|---|
| `filter` | — | Only names containing this text, not case-sensitive |

```json
{}
```
```text
sound effects in C:\Users\you\ZeyoCut\library\sfx (3):
(top level)
- thud_soft (0.3s)
cartoon/
- cartoon/boing (0.6s)
impacts/
- impacts/thud (0.3s)
```
```json
{ "filter": "thud" }
```
```text
sound effects in C:\Users\you\ZeyoCut\library\sfx (2 matching 'thud'):
(top level)
- thud_soft (0.3s)
impacts/
- impacts/thud (0.3s)
```

### `add_sfx`

Places a sound effect at a time, as an audio layer with role `effects`, in one undo step. Music ducking leaves effects alone.

- `name` is the full name from `list_sfx`. When only one sound has that file name, the file name alone works too: `boing` finds `cartoon/boing`.
- The first add copies the file into the project's `assets\`. Later adds of the same sound reuse that asset.
- Afterwards, change the layer like any audio layer with `update_layer`, `move_layer` or `remove_layer`.

| Parameter | Default | Notes |
|---|---|---|
| `name` | required | Sound effect name from `list_sfx` |
| `time` | required | Timeline seconds, from 0 to before the project end |
| `volume` | 1 | 0–10; 1 = as recorded |

```json
{ "name": "boing", "time": 0.4 }
```
```text
added sound effect 'cartoon/boing' as audio layer 'boing' at 0.4s (volume 1, role effects, 0.6s long); imported 'cartoon/boing' as asset 'boing'
{"type":"audio","id":"boing","name":"boing","start":0.4,"end":null,"zIndex":0,"parentId":null,"visible":true,"asset":"boing","trimIn":0,"trimOut":null,"volume":1,"role":"effects","fadeIn":0,"fadeOut":0}
```
```json
{ "name": "cartoon/boing", "time": 3 }
```
```text
added sound effect 'cartoon/boing' as audio layer 'boing_2' at 3s (volume 1, role effects, 0.6s long); reused asset 'boing'
{"type":"audio","id":"boing_2","name":"boing_2","start":3,"end":null,"zIndex":0,"parentId":null,"visible":true,"asset":"boing","trimIn":0,"trimOut":null,"volume":1,"role":"effects","fadeIn":0,"fadeOut":0}
```
```json
{ "name": "thu", "time": 1 }
```
```text
An error occurred invoking 'add_sfx': no sound effect named 'thu'; similar: thud_soft, impacts/thud
```
```json
{ "name": "boing", "time": 7 }
```
```text
An error occurred invoking 'add_sfx': time must be from 0 to before the project end (6s); got 7
```

### `detect_beats`

Finds the beats of a music track. It works on any audio asset, and on a video asset with sound.

1. **Onsets.** These are the moments where the sound's spectrum suddenly gets louder (spectral flux, from a hand-written FFT). Each is timed to the 3 ms block where the sound rises. A steady low tone underneath doesn't shift the timing.
2. **Tempo.** It is the period at which the onsets repeat, between 60 and 200 BPM. When half or double the tempo fits as well, the one nearer 120 BPM wins.
3. **Beat grid.** A steady grid at that tempo is placed on the loudest hits, so kicks and snares win over off-beat hi-hats. It is then fitted to the onsets it catches by least squares.

The result has:

- the BPM
- the beat times
- the onsets
- how many of the beats land on an onset (the confidence)

With fewer than 4 onsets, or onsets that don't follow a steady beat, there is no tempo, and a note says why. The result is saved next to the asset as `<name>.beats.json`.

Times are source seconds of the asset. A layer that starts at `s0` with `trimIn` `t0` plays source time `t` at `s0 + t - t0` on the timeline. Up to 120 s of sound, the result comes back directly. Longer files run in the background as `beats_N`; poll [`get_job_status`](#get_job_status).

| Parameter | Default | Notes |
|---|---|---|
| `assetId` | required | An audio asset, or a video asset with sound |

A 12 s drum loop at 100 BPM, with a kick on each beat from 0.3 s, a quiet hi-hat halfway between, and a low chord throughout:
```json
{ "assetId": "drums" }
```
```text
beats of 'drums' (12s): 100.0 BPM, 20 beats every 0.6s from 0.301s to 11.701s; 100% of them land on one of the 40 onsets
beats (s): 0.301, 0.901, 1.501, 2.101, 2.701, 3.301, 3.901, 4.501, 5.101, 5.701, 6.301, 6.901, 7.501, 8.101, 8.701, 9.301, 9.901, 10.501, 11.101, 11.701
onsets (s): 0.002, 0.3, 0.599, 0.901, 1.2, 1.502, 1.801, 2.1, 2.399, 2.701, 3, 3.302, 3.601, 3.9, 4.201, 4.5, 4.799, 5.101, 5.4, 5.702, 6.001, 6.3, 6.599, 6.901, 7.2, 7.502, 7.801, 8.102, 8.401, 8.7, 8.999, 9.301, … (8 more in the file)
saved C:\Users\you\ZeyoCut\projects\tora-talks\assets\drums.beats.json
times are source seconds of the asset: a layer with start s0 and trimIn t0 plays source time t at s0 + t - t0
```
The onset at 0.002 s is the chord starting, and the onsets between the beats are the hi-hats.
```json
{ "assetId": "mouth_open" }
```
```text
An error occurred invoking 'detect_beats': 'mouth_open' is not an audio or video asset (image); detect_beats works on audio assets and video assets with sound
```

### `auto_lip_flap`

Animates a mouth image layer to a voice in one undo step:

1. It measures the loudness (RMS) of every frame of the sound as it plays on the timeline, with its `start` and `trimIn`. Only the time where both the sound and the mouth play counts.
2. Frames below a threshold get `closedDrawing`. The threshold lies between the quiet level and the loud level of the frames. At `sensitivity` 1 it is at the quiet level, at 0 at the loud level.
3. The other frames share the `openDrawings` about evenly by loudness, the loudest frames getting the last (widest) drawing. With `["open", "wide"]`, the louder half of the open frames get `wide`.
4. Every drawing holds at least 2 frames, so the mouth doesn't flicker.
5. It writes keyframes on the mouth's `drawing` and replaces the drawing keyframes inside that time. Before and after it, the mouth shows the drawing it showed before.

More details:

- **Running it again** replaces its own keyframes.
- **Silence:** if the sound is silent there (below -60 dBFS), the mouth stays closed and a warning says so.
- **Video sound:** the sound can come from a video layer. A muted or hidden source gets a warning, because the export doesn't play it.

| Parameter | Default | Notes |
|---|---|---|
| `mouthLayerId` | required | Image layer with the mouth drawings |
| `audioLayerId` | required | Audio layer with the voice, or a video layer with sound |
| `closedDrawing` | required | Drawing name of the closed mouth |
| `openDrawings` | required | 1–8 drawing names, from slightly to wide open |
| `sensitivity` | 0.5 | 0–1; higher opens the mouth on quieter sounds |

A mouth layer with drawings `closed`, `open` and `wide`, and a 4 s spoken line as the audio layer `voice`, starting at 1 s:
```json
{ "mouthLayerId": "mouth", "audioLayerId": "voice", "closedDrawing": "closed", "openDrawings": ["open", "wide"] }
```
```text
lip flap on 'mouth' from 'voice' at 1–4.964s (96 frames): open 52 frames (open 30, wide 22), closed 44; 19 keyframe(s) on mouth.drawing (the mouth opens above -48.0 dBFS at sensitivity 0.5; every drawing holds at least 2 frames)
```
In `render_contact_sheet` from 1 s to 2.5 s, the mouth goes closed → open → wide → open → closed over the first word, then stays closed in the pause after it. With one open drawing and a higher sensitivity:
```json
{ "mouthLayerId": "mouth", "audioLayerId": "voice", "closedDrawing": "closed", "openDrawings": ["open"], "sensitivity": 0.8 }
```
```text
lip flap on 'mouth' from 'voice' at 1–4.964s (96 frames): open 55 frames, closed 41; 5 keyframe(s) on mouth.drawing (the mouth opens above -67.2 dBFS at sensitivity 0.8; every drawing holds at least 2 frames)
```
```json
{ "mouthLayerId": "mouth", "audioLayerId": "voice", "closedDrawing": "closed", "openDrawings": ["open", "closed"] }
```
```text
An error occurred invoking 'auto_lip_flap': 'closed' is the closed drawing; the open drawings must be other drawings
```
```json
{ "mouthLayerId": "mouth", "audioLayerId": "boing", "closedDrawing": "closed", "openDrawings": ["open"], "sensitivity": 2 }
```
```text
An error occurred invoking 'auto_lip_flap': sensitivity must be between 0 (opens only on the loudest sounds) and 1 (opens on anything above the quiet parts); got 2
```

## Episode scripts

### `build_from_script`

Builds a whole episode from a script, with the library's rigs, actions and sound effects, as a new project that it opens. The script is a JSON or Markdown file (`scriptFile`), or its text (`script`).

- **The project** gets the script's `template` (a library template, which sets the canvas and frame rate), or else its `preset`, `width`, `height` and `fps` (default 1920x1080 at 30 fps). It also gets the script's `background`. Its duration is the script's `duration` or the end of the last scene, whichever is longer.
- **Scenes follow each other:** each starts where the one before ends, unless a JSON scene gives `start`. A scene lasts its duration; without one, it runs until its last action, sound or caption ends. Times inside a scene are seconds from its start.
- **Each scene's rigs** are placed at its start and end with it, unless the scene runs to the project's end. A rig's copy is named by `as` (default: the rig's name). A name an earlier scene already used gets `_2` (`hero_2_body`).
- **Actions** play on a rig of their scene, named by `on`. `on` can be left out when the scene has one rig. `speed` and `loops` work as in [`apply_action`](#apply_action).
- **Sound effects** are placed as with [`add_sfx`](#add_sfx).
- **Captions** go under one caption group, `captions`, in the script's `captionStyle`: a [`generate_captions`](#generate_captions) style or a library style (default `subtitle`). A caption without an end lasts 3 s.
- **A scene marker** goes at the start of every scene (see [Markers](#markers)).
- **Whatever it can't map is listed** in the reply, in script order, and the rest is built. That covers an unknown rig, action, sound, setting or field, a line it doesn't understand, and anything that starts at or after the end of its scene. A scene with no duration and nothing timed in it is left out.
- **One undo step:** the build is one undo step on top of the new, empty project, so `undo` leaves the empty project.
- **Refused, with nothing created:** a script with no scenes or none that can be built, invalid JSON, an unknown preset or template, a script without a title and no `name`, and a folder that is already a project.

| Parameter | Default | Notes |
|---|---|---|
| `scriptFile` | — | Absolute path of a `.json` file, or a Markdown file (`.md`). Give this or `script` |
| `script` | — | The script's text: JSON if it starts with `{`, Markdown otherwise |
| `name` | the script's title | Project name, also the folder name |
| `directory` | `<projectsRoot>\<name>` | Absolute folder |

**Times** in a Markdown script are seconds (`8`, `8s`, `2.5s`) or minutes and seconds (`1:30`, `0:00.5`). In JSON they are numbers of seconds.

#### Markdown

The script is read line by line. A line that matches none of these patterns is a note and is skipped, so the script can hold stage directions.

| Line | Meaning |
|---|---|
| `# Title` | The title, which names the project. Only the first one counts |
| `key: value` | Before the first scene: a setting, one of `template`, `preset`, `width`, `height`, `fps`, `duration`, `background` and `captionStyle`. Another one-word key is listed as unknown; longer prose such as `Notes for the animator: …` is a note |
| `## Name (8s)` | Starts a scene, with its duration in brackets. Without the brackets the scene runs until its last item ends |
| `- rig NAME [as ID] [at X,Y] [scale N]` | Places a library rig with its root's anchor at canvas pixels X,Y (default: the canvas center) |
| `- action NAME [on ID] at T [speed N] [loops N]` | Plays a library action at T |
| `- sfx NAME at T [volume N]` | Places a sound effect, named as `list_sfx` shows it (`impacts/boing`). `sound` works as well as `sfx` |
| `- caption T-T: text` | A caption from one time to the other. `–` or `to` work as well as `-` |

Items start with `- ` or `* `. A name with spaces goes in double quotes (`- rig "big tora"`). Brackets mark optional parts and aren't typed.

`C:\Users\you\scripts\tora-ep1.md`, with the library holding the rig `tora`, the actions `hop` and `wag`, and the sound effects `impacts/boing` and `whoosh`:
```markdown
# Tora's Big Jump
preset: youtube_1080p
captionStyle: subtitle
background: #87CEEB

Notes for the animator: keep it bouncy.

## The ledge (6s)
Tora peers over the edge of the roof.
- rig tora as hero at 760,820
- action hop on hero at 1s loops 2
- sfx impacts/boing at 1.3s
- caption 0-2.5s: Okay. I can do this.
- caption 3s-5.5s: ...probably.

## The jump (4s)
- rig tora as hero at 960,700 scale 1.2
- action hop at 0.5 speed 1.5
- action somersault at 1
- sfx whoosh at 0.4 volume 0.7
- caption 0.5-3: Wheee!
- caption 4.5-5: (too late for this scene)

## Landing
- rig tora at 1200,820
- action wag on tora at 0.5
- sfx thud at 0.2
- caption 0:00.5 to 0:03: Nailed it.
- camera zoom in
```
```json
{ "scriptFile": "C:\\Users\\you\\scripts\\tora-ep1.md" }
```
```text
created and opened project 'Tora's Big Jump' at C:\Users\you\ZeyoCut\projects\Tora's Big Jump from C:\Users\you\scripts\tora-ep1.md
1920x1080 @ 30 fps, 13s, 3 scenes:
- scene 1 "The ledge" 0–6s: rig tora as hero_body; hop on hero_body 1–2.6s (2 loops); impacts/boing at 1.3s as boing; 2 captions
- scene 2 "The jump" 6–10s: rig tora as hero_2_body; hop on hero_2_body 6.5–7.033s; whoosh at 6.4s as whoosh; 1 caption
- scene 3 "Landing" 10–13s: rig tora as tora_body; wag on tora_body 10.5–11.1s; 1 caption
captions: 4 under 'captions' (style subtitle)
couldn't map 4 things; the rest was built:
- line 19 (scene 'The jump'): no action named 'somersault' in the library; actions: hop, wag
- line 22 (scene 'The jump'): the caption "(too late for this scene)" starts at or after the end of its scene (10s)
- line 27 (scene 'Landing'): no sound effect named 'thud'; sound effects: whoosh, impacts/boing
- line 29 (scene 'Landing'): 'camera zoom in' isn't a rig, action, sfx or caption line
next: render_contact_sheet to check the scenes; undo takes the build out and leaves the empty project
```
The last scene has no duration, so it ends with its caption at 13 s, and its rig runs to the end. `list_layers` after the build:
```text
Tora's Big Jump: 1920x1080 @ 30 fps, 13s, 16 layer(s), 5 asset(s)
markers: the_ledge scene at 0s "The ledge"; the_jump scene at 6s "The jump"; landing scene at 10s "Landing"
hero_body [image]  z=1  t=0–6  drawings=normal  anchor=(110,260)  keys: y(5)
├─ hero_head [image]  z=2  t=0–6  drawings=normal  anchor=(120,200)
└─ hero_tail [image]  z=0  t=0–6  drawings=normal  anchor=(170,40)
boing [audio]  t=1.3–end  asset=boing volume=1
hero_2_body [image]  z=4  t=6–10  drawings=normal  anchor=(110,260)  keys: y(3)
├─ hero_2_head [image]  z=5  t=6–10  drawings=normal  anchor=(120,200)
└─ hero_2_tail [image]  z=3  t=6–10  drawings=normal  anchor=(170,40)
whoosh [audio]  t=6.4–end  asset=whoosh volume=0.7
tora_body [image]  z=7  t=10–end  drawings=normal  anchor=(110,260)
├─ tora_head [image]  z=8  t=10–end  drawings=normal  anchor=(120,200)
└─ tora_tail [image]  z=6  t=10–end  drawings=normal  anchor=(170,40)  keys: rotation(3)
captions [image]  z=18  t=0–end  null (no drawings)  anchor=(0,0)  ignoreCamera
├─ captions_1 [text]  "Okay. I can do this."  z=18  t=0–2.5  text="Okay. I can do this."  font=Segoe UI 49px  anchor=(825.6,71.691)
├─ captions_2 [text]  "...probably."  z=18  t=3–5.5  text="...probably."  font=Segoe UI 49px  anchor=(825.6,71.691)
├─ captions_3 [text]  "Wheee!"  z=18  t=6.5–9  text="Wheee!"  font=Segoe UI 49px  anchor=(825.6,71.691)
└─ captions_4 [text]  "Nailed it."  z=18  t=10.5–13  text="Nailed it."  font=Segoe UI 49px  anchor=(825.6,71.691)
```
In `render_contact_sheet`, Tora hops twice on the ledge above the captions, comes back larger and higher for the jump, and stands on the right for the landing, on the sky-blue background. `undo` then gives:
```text
undid: build_from_script tora-ep1.md (0 more undo step(s), 1 redo step(s))
```

#### JSON

Comments and trailing commas are allowed, and field names can be in any case. An unknown field is listed, and the rest is built.

| Field | Notes |
|---|---|
| `title`, `template`, `preset`, `width`, `height`, `fps`, `duration`, `background`, `captionStyle` | As the Markdown settings |
| `scenes` | A list of scenes, each with `name`, `start` (optional), `duration` (optional), `rigs`, `actions`, `sfx` and `captions` |
| `rigs` entry | `rig`, `as`, `x`, `y`, `scale` |
| `actions` entry | `action`, `on`, `at`, `speed`, `loops` |
| `sfx` entry | `sound`, `at`, `volume` |
| `captions` entry | `text`, `at`, and `end` or `duration` |

The script as text, with the library template `tora show` and the library style `gold title`:
```json
{
  "title": "Tora Shorts 2",
  "template": "tora show",
  "preset": "shorts_1080p",
  "duration": 8,
  "captionStyle": "gold title",
  "scenes": [
    { "name": "Cold open", "duration": 3,
      "rigs": [{ "rig": "tora", "x": 640, "y": 600 }],
      "actions": [{ "action": "hop", "at": 0.5, "loops": 2 }],
      "captions": [{ "text": "Previously...", "at": 0, "end": 2 }] },
    { "name": "Title card", "duration": 2, "camera": "push in",
      "sfx": [{ "sound": "whoosh", "at": 0 }] }
  ]
}
```
```text
created and opened project 'Tora Shorts 2' at C:\Users\you\ZeyoCut\projects\Tora Shorts 2 from the script (template 'tora show')
1280x720 @ 30 fps, 8s, 2 scenes:
- scene 1 "Cold open" 0–3s: rig tora as tora_body; hop on tora_body 0.5–2.1s (2 loops); 1 caption
- scene 2 "Title card" 3–5s: whoosh at 3s as whoosh
captions: 1 under 'captions' (style gold title)
couldn't map 1 thing; the rest was built:
- scene 2 'Title card': unknown field 'camera'
next: render_contact_sheet to check the scenes; undo takes the build out and leaves the empty project
Warnings:
- template 'tora show' sets the canvas and frame rate, so the script's preset, width, height and fps aren't used
```
The template's `handle` layer runs the whole 8 s, and the scenes fill the first 5 s.

Errors:
```json
{ "scriptFile": "C:\\Users\\you\\scripts\\tora-ep1.md" }
```
```text
An error occurred invoking 'build_from_script': a project already exists in 'C:\Users\you\ZeyoCut\projects\Tora's Big Jump'; give another name or directory
```
```json
{ "script": "# Untitled pilot\nJust an idea for now.", "name": "pilot" }
```
```text
An error occurred invoking 'build_from_script': the script has no scenes; a Markdown scene starts with '## Scene name (8s)', a JSON one is an entry of "scenes"
```
```json
{ "script": "{ \"scenes\": [ }", "name": "broken" }
```
```text
An error occurred invoking 'build_from_script': the script isn't valid JSON: '}' is an invalid start of a value. Path: $.scenes[0] | LineNumber: 0 | BytePositionInLine: 14.
```

---

## Checks

Three checks to run before an export. They don't change the project and aren't undo steps; `frame_diff` writes its scores to `renders\`.

### `frame_diff`

Renders every frame of the range 160 px wide and scores how much each frame changed from the one before it: the mean color difference, 0–100%. It flags two kinds of frame:

- **glitch:** a single frame that differs from both neighbors while they are closer to each other, such as a stray one-frame layer or a wrong drawing for one frame.
    - The part of its change that comes back on the next frame must be at least 0.5% and three times what is typical for the range.
    - The frames next to it must not look like glitches too. That way grain, steady flicker and shake aren't flagged.
- **spike:** a jump of at least 3% that stands far above the median and is three times the change of the frames around it, with no layer starting, ending or swapping drawings there. Examples are a teleporting layer or a mistyped keyframe. Fast but smooth motion is not a spike.

Some changes are expected. They are listed under "expected cuts", not flagged:

- layer starts and ends
- drawing swaps
- `hold` keyframes, including the camera's
- the scene cuts `analyze_video` found in a video layer's clip

A glitch that falls on one of these is still flagged, and its note names the event. Frames inside a [camera shake](#camera_shake) move on purpose and aren't checked.

The scores (frame, time, change, label, note) are saved to `renders\frame_diff_<first>-<last>.json`. Up to 150 frames, the result comes back directly. Longer ranges run in the background as `framediff_N`; poll [`get_job_status`](#get_job_status).

| Parameter | Default | Notes |
|---|---|---|
| `startTime` | 0 | Seconds |
| `endTime` | the end | At least 3 frames after `startTime` |

A Tora scene with a leftover one-frame layer `oops` (faint white clouds at 2s) and a body keyframe that jumps 240 px in one frame at 2.633s:
```json
{}
```
```text
frame_diff 0–3.967s (f0–f119, 120 frames compared at 160x90): median change 0.1%, largest 6.4% at f79 (2.633s)
flagged (2):
- f60 2s glitch: differs from both neighbors (1.1%, 1.0%), which are closer to each other (0.5%); 'oops' starts here
- f79 2.633s spike: 6.4% change (88× the median) with no layer starting, ending or swapping drawings there
scores: C:\Users\you\ZeyoCut\projects\tora-checks\renders\frame_diff_00000-00119.json
```
```json
{ "startTime": 2, "endTime": 2.05 }
```
```text
An error occurred invoking 'frame_diff': compare at least 3 frames; 2–2.05s covers 2 at 30 fps
```
A longer range runs in the background:
```text
comparing 360 frames (f0–f359) in the background as framediff_1.
Poll get_job_status with jobId "framediff_1".
```

### `audio_levels`

Mixes the sound exactly as `start_export` would (fades, ducking, two-pass loudness normalization) and measures the result. It reports:

- the integrated loudness (ITU-R BS.1770, gated, LUFS) and sample peak (dBFS) of the whole mix and of each section
- **clipping:** samples at full scale
- **too quiet:** a section more than 10 LU below the whole mix, or the whole mix below -24 LUFS while normalization is off
- **silence:** 1s or more in the middle of the video where the peak stays below -50 dBFS. The note says which clips play there, or that none do. Silence at the very start or end is listed but not flagged.

Up to 120s of audio, the result comes back directly. Longer mixes run in the background as `audiolevels_N`.

| Parameter | Default | Notes |
|---|---|---|
| `sectionSeconds` | 5 | 1–60 |

The same scene with a boing at 0.4s and a thud at 3.1s:
```json
{ "sectionSeconds": 1 }
```
```text
audio_levels 0–4s, the mix as exported (2 clip(s); loudness: -11.2 LUFS → -14 LUFS (true peak at most -1 dBTP)): -14.4 LUFS integrated, peak -8.8 dBFS
sections (1s):
- 0–1s: -14.0 LUFS, peak -10.8 dBFS
- 1–2s: silent
- 2–3s: silent
- 3–4s: -13.7 LUFS, peak -8.8 dBFS
flagged (1):
- 1–3.1s silence: 2.1s with no audio clip playing; fill it with music or a sound effect, or close the gap with remove_range
```
Without any sound:
```text
audio_levels: the project has no sound (no audio layers and no video layers with sound), so the export will be silent
```

### `check_project`

Checks every frame of the open project. No parameters.

| Check | What it reports |
|---|---|
| missing files | Assets whose file is gone, with the layers that use them |
| off the canvas | Layers that are never inside the canvas while they're shown (position, parent and camera included) |
| text outside the safe area | Text reaching into the 5% title-safe margin. On a vertical canvas (taller than 1.5× its width), the Shorts player UI instead: the top 8%, bottom 25% and right 12%, plus the 5% margin on the left |
| empty frames | One frame or more where nothing but the background shows |
| keyframes that never play | Keys before a layer starts or after it ends (or after the project's end) that can't change any frame it's shown in |
| glyphs a font lacks | Text with characters its font has no glyphs for, which show as empty boxes. Each text is shaped with HarfBuzz, as it is drawn. A text with a [language](#languages) is pointed to that language's font |
| unused assets | Assets no layer uses |

```json
{}
```
```text
check_project: 4 problem(s) in 'tora-checks' (120 frames checked)
missing files (1):
- asset 'rock' (assets/rock.png) has no file; used by rock, which won't render or export; put the file back, or import it again
off the canvas (1):
- 'bird' is never on the canvas while it's shown (0–4s); check its x/y, its parent and the camera
text outside the safe area (1):
- text 'title' reaches into the title-safe margin (5%, top) at 0–4s
unused assets (1):
- image asset 'shadow' isn't used by any layer
```
On a `shorts_1080p` canvas, with a call to action at y=1650:
```text
check_project: 1 problem(s) in 'tora-short' (90 frames checked)
text outside the safe area (1):
- text 'cta' reaches into the Shorts UI zone (bottom 25%) at 1.5–3s
```
After moving it to y=1250:
```text
check_project: no problems in 'tora-short' (90 frames checked for missing files, layers off the canvas, text outside the safe area, glyphs a font lacks, empty frames, keyframes that never play and unused assets)
```
A Burmese text left in `Segoe UI`:
```text
check_project: 1 problem(s) in 'greeting' (150 frames checked)
glyphs a font lacks (1):
- text 'note_my' (my): font 'Segoe UI' has no glyphs for "မင်္ဂလာပါ", which show as empty boxes; the my font is "Myanmar Text" (update_layer fontFamily)
```

---

## Export

### `start_export`

Starts a background export of the open project to `exports\<fileName>`.

- **Video:** H.264, yuv420p, BT.709, from the GPU's encoder where one works, else libx264 `-preset veryfast` (see **Encoder** below).
- **Audio:** all audio layers and the sound of video layers, mixed to AAC 48 kHz stereo.
    - **Ducking:** music (role `music`) is lowered under voice (role `voice`) with a sidechain compressor. It is on by default and can be turned off with `update_project` `ducking=false`.
    - **Loudness:** the mix is normalized with two-pass `loudnorm` to the project's target (-14 LUFS by default), with true peak at most -1 dBTP.
- **Presets:** `youtube_1080p` and `shorts_1080p` encode for upload: H.264 high profile, `-preset medium` (or the GPU encoder's matching preset), 2 B-frames, a keyframe every half second, scaled to the preset size. The canvas must have the preset's aspect ratio; [`export_batch`](#export_batch) also fits other shapes.
- **Encoder:** with the `VideoEncoder` setting at `auto` (the default), the first export on a computer tries NVIDIA NVENC, Intel Quick Sync and AMD AMF, in that order, with a half-second test encode, and uses the first that works; if none does, libx264. What the tests found is kept in the library's `encoders.json` for this computer and this FFmpeg, so they run again only after a hardware or FFmpeg change. The setting can instead name `x264`, `nvenc`, `qsv` or `amf`; a GPU encoder that doesn't work falls back to libx264.
    - The quality follows `crf`. Quick Sync uses `global_quality` `crf` + 1, which gave libx264's quality (SSIM) at about the same size on an Intel Core Ultra 9 285H. NVENC (`-cq` `crf` + 1) and AMF (QP `crf` + 1) get the equivalent settings but weren't tested. `crf` 0 (lossless) always uses libx264.
    - On that laptop, Quick Sync took a third of libx264's CPU time and 40% less memory, but exports were only slightly faster, because decoding and drawing the frames took most of the time.
    - The export's notes say which encoder it used and why:
```text
processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
processing: encoder: libx264 (CPU, CRF 0): CRF 0 (lossless) always uses libx264
```

- **In chunks, in parallel:** the frames are rendered in chunks of 2 seconds on several threads at once (a quarter of the logical processors, from 1 to 4, and fewer when half the free memory can't hold them; the export then says so), each chunk with an encoder of its own. The chunks are then joined into one file without encoding them again, and the audio is mixed in. Each chunk starts with a keyframe.
- **Chunk cache:** each chunk is kept in the project's `cache\chunks` folder under a key made of everything its frames show and how it is encoded: every drawn layer's settings as they are at that frame, where it lands, the files it reads (with their size and time) and the clip frames it shows. Exporting again renders only the chunks whose frames changed, so a change at 0:40 leaves the chunks before and after it as they were; `report_render_stats` says how many came from the cache. The folder is trimmed to 2 GB, dropping the chunks used longest ago. Deleting it only costs the next export its head start.
- The job renders a snapshot of the project, so edits made while it runs don't affect it.
- If the server stops while the job runs, the next server starts it again from the same snapshot, under the same id, and goes on from the chunks it finished (see [`get_job_status`](#get_job_status)).

| Parameter | Default | Notes |
|---|---|---|
| `fileName` | required | A plain file name; `.mp4` is added if missing |
| `crf` | 18 | 0–51, lower is better quality |
| `preset` | | `youtube_1080p` or `shorts_1080p` |
| `language` | every layer | Exports that [language's version](#languages): the layers of other languages are left out, and clips whose sound is in another language are muted |

```json
{ "fileName": "tora-test.mp4" }
```
```text
started export_1: 90 frames (1280x720 @ 30 fps) -> C:\Users\you\ZeyoCut\projects\tora-test\exports\tora-test.mp4
poll get_export_status with jobId "export_1"
```
```json
{ "fileName": "talk-short.mp4", "preset": "shorts_1080p" }
```
```text
started export_2: 267 frames (1080x1920, preset shorts_1080p @ 30 fps) -> C:\Users\you\ZeyoCut\projects\talk-demo\exports\talk-short.mp4
poll get_export_status with jobId "export_2"
```
```json
{ "fileName": "wrong.mp4", "preset": "youtube_1080p" }
```
```text
An error occurred invoking 'start_export': the project is 1080x1920 but youtube_1080p is 1920x1080 (landscape); change the canvas with update_project preset=youtube_1080p first
```
In a project with [language versions](#languages):
```json
{ "fileName": "greeting_ja.mp4", "language": "ja" }
```
```text
started export_1: 150 frames (1280x720 @ 30 fps; ja version, 8 layer(s) of other languages left out, sound of 'talk' muted) -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_ja.mp4
poll get_export_status with jobId "export_1"
```
```json
{ "fileName": "greeting_all.mp4" }
```
```text
started export_2: 150 frames (1280x720 @ 30 fps) -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_all.mp4
poll get_export_status with jobId "export_2"
Warnings:
- this export includes the layers of every language (en, ja, my); give language for one version, or export_language_versions for one file per language
```

### `get_export_status`

Reports the progress of an export job. When the job finishes, it gives ffprobe's view of the file.

```json
{ "jobId": "export_1" }
```
While running. The job renders frames first, then waits for FFmpeg to finish encoding:
```text
export_1: running (rendering) 0% (0/90 frames), 0.0s elapsed
```
```text
export_1: running (finishing encode) 100% (90/90 frames), 0.3s elapsed
```
A longer one, with the time left:
```text
export_1: running (rendering) 2.4% (88/3600 frames), 5.0s elapsed, about 3 min left
```
Started again by the next server after the first one stopped:
```text
export_1: running (rendering) 0% (1/3600 frames), 0.2s elapsed; started again after the server restarted (attempt 2 of 3)
```
```text
export_1: completed; started again after the server restarted (attempt 2 of 3)
output: C:\Users\you\ZeyoCut\projects\long-talk\exports\long-talk.mp4
duration: 120.000s
resolution: 1920x1080
fps: 30
video: h264 yuv420p, 3600 frames
audio: none
processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
file size: 959.5 KB (982555 bytes)
time: 90.7s
```
When done, with the audio processing and preset that were applied:
```text
export_2: completed
output: C:\Users\you\ZeyoCut\projects\talk-demo\exports\talk-short.mp4
duration: 8.900s
resolution: 1080x1920
fps: 30
video: h264 yuv420p, 267 frames
audio: aac 2ch 48000 Hz (4 clip(s) mixed)
processing: music ducked under voice
processing: loudness: -19.6 LUFS → -14 LUFS (true peak at most -1 dBTP)
processing: preset shorts_1080p: 1080x1920, H.264 high profile, CRF 18, AAC 192k
processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
file size: 2.2 MB (2349914 bytes)
time: 6.6s
```
A failed job reports `export_1: failed after …s: <reason>`, for example FFmpeg's last error lines.

### `report_render_stats`

Says where a finished export's time went, to find what makes an export slow. It reads the times kept with the job, so it changes nothing and also works for exports of earlier sessions.

- `jobId` is an `export_N`, `batch_N` or `versions_N` job. Default: the newest finished export. A batch reports each of its files.
- **The first line** gives the frames, size, time and speed, and how the export was [split into chunks](#start_export): how many 2 s chunks, how many were rendered and on how many threads, and how many came from the cache.
- **Rendering,** stage by stage, slowest first, with each stage's share. The threads render at the same time, so the stages add up to more than the time the rendering took; both are given:

| Stage | What it measures |
|---|---|
| evaluating keyframes and transforms | Every layer's animated values, parents and draw order at the frame |
| decoding video and images | Waiting for a clip's decoder (FFmpeg) to deliver the frame, and loading pictures |
| redaction | Hiding [tracks](#redaction) on the decoded frame |
| background removal and cutouts | Keying, reading and refining mattes, and drawing cutouts |
| text | Drawing text: its box, outline and glyphs |
| effects and masks | Shadows, outlines, blurs, tints and masks |
| drawing layers onto the frame | Pictures and video frames drawn at their place, and the rest of the frame |
| fitting the canvas into the preset | The blurred copy behind a canvas of another shape than the preset ([`export_batch`](#export_batch), [`export_language_versions`](#export_language_versions)) |
| waiting for the encoder | Frames waiting while a chunk's FFmpeg catches up, and starting and finishing it |

- **Before and after the rendering:** measuring the loudness of the mix (the first loudness pass), finding the chunks that changed (each chunk's key), joining the chunks and mixing the audio, and the rest (checking the finished file and trimming the cache).
- The encoders (one FFmpeg per chunk) run alongside the rendering, so their CPU time is given apart.
- **Slowest layers:** each layer's own time, with its decoding, mattes, effects and drawing. Its children count apart.
- **Slowest frames:** the five that took longest to render. The first frame of each thread is usually one of them, since each clip's decoder starts there.
- Times under 5 ms are left out.

A 5 s 1920x1080 clip with music, a title with a drop shadow and captions, exported with `youtube_1080p`:
```json
{}
```
```text
export_1 (bread.mp4): 150 frames at 1920x1080 in 2.14s, 70.1 frames a second; 3 chunk(s) of 2s, 3 rendered on 3 thread(s)
rendering, 3.856s added up over the 3 threads, which took 1.533s:
- decoding video and images: 1.796s (47%)
- waiting for the encoder: 0.933s (24%)
- drawing layers onto the frame: 0.635s (16%)
- effects and masks: 0.33s (9%)
- evaluating keyframes and transforms: 0.129s (3%)
- text: 0.03s (<1%)
before and after the rendering:
- measuring the loudness of the mix: 0.189s
- finding the chunks that changed: 0.011s
- joining the chunks and mixing the audio: 0.337s
- the rest (checking the file, trimming the cache): 0.071s
the encoders (ffmpeg) used 7.31s of CPU time, running alongside the rendering
slowest layers (each with its own decoding, mattes, effects and drawing):
- talk [video]: 2.413s over 150 frame(s), 16.1 ms a frame
- title [text]: 0.337s over 90 frame(s), 3.7 ms a frame
- captions_1 [text]: 0.015s over 67 frame(s), 0.2 ms a frame
- captions_2 [text]: 0.009s over 38 frame(s), 0.2 ms a frame
slowest frames: f120 (215.4 ms), f60 (201.4 ms), f0 (158.2 ms), f3 (21.7 ms), f2 (20.9 ms)
```
After the title's color changed, exported again. The title is on screen for the first 3 s, so the first two chunks were rendered again and the last came from the cache:
```json
{}
```
```text
export_2 (bread.mp4): 150 frames at 1920x1080 in 2.06s, 72.7 frames a second; 3 chunk(s) of 2s, 2 rendered on 2 thread(s), 1 from the cache
rendering, 2.76s added up over the 2 threads, which took 1.459s:
- decoding video and images: 1.319s (48%)
- drawing layers onto the frame: 0.528s (19%)
- waiting for the encoder: 0.5s (18%)
- effects and masks: 0.305s (11%)
- evaluating keyframes and transforms: 0.084s (3%)
- text: 0.023s (<1%)
before and after the rendering:
- measuring the loudness of the mix: 0.169s
- finding the chunks that changed: 0.007s
- joining the chunks and mixing the audio: 0.363s
- the rest (checking the file, trimming the cache): 0.066s
the encoders (ffmpeg) used 5.33s of CPU time, running alongside the rendering
slowest layers (each with its own decoding, mattes, effects and drawing):
- talk [video]: 1.832s over 120 frame(s), 15.3 ms a frame
- title [text]: 0.309s over 90 frame(s), 3.4 ms a frame
- captions_1 [text]: 0.013s over 67 frame(s), 0.2 ms a frame
- captions_2 [text]: 0.006s over 31 frame(s), 0.2 ms a frame
slowest frames: f60 (156.8 ms), f0 (121.8 ms), f1 (23.5 ms), f32 (20.9 ms), f27 (20.4 ms)
```
The same project with `export_batch` to both presets. The YouTube file is made of the chunks `export_2` left in the cache, so only its audio was mixed again. Fitting the landscape canvas into the Short takes most of the Short's time:
```json
{ "jobId": "batch_1" }
```
```text
batch_1: 2 file(s), export_batch youtube_1080p + shorts_1080p
bread_youtube_1080p.mp4: 150 frames at 1920x1080 in 0.58s, 257.3 frames a second; 3 chunk(s) of 2s, 3 from the cache
rendering: none, every chunk came from the cache
before and after the rendering:
- measuring the loudness of the mix: 0.169s
- joining the chunks and mixing the audio: 0.349s
- the rest (checking the file, trimming the cache): 0.063s
bread_shorts_1080p.mp4: 150 frames at 1080x1920 in 4.21s, 35.6 frames a second; 3 chunk(s) of 2s, 3 rendered on 3 thread(s)
rendering, 9.074s added up over the 3 threads, which took 3.626s:
- fitting the canvas into the preset: 5.423s (60%)
- decoding video and images: 1.847s (20%)
- waiting for the encoder: 0.766s (8%)
- drawing layers onto the frame: 0.605s (7%)
- effects and masks: 0.295s (3%)
- evaluating keyframes and transforms: 0.114s (1%)
- text: 0.024s (<1%)
before and after the rendering:
- measuring the loudness of the mix: 0.17s
- joining the chunks and mixing the audio: 0.358s
- the rest (checking the file, trimming the cache): 0.058s
the encoders (ffmpeg) used 6.34s of CPU time, running alongside the rendering
slowest layers (each with its own decoding, mattes, effects and drawing):
- talk [video]: 2.439s over 150 frame(s), 16.3 ms a frame
- title [text]: 0.299s over 90 frame(s), 3.3 ms a frame
- captions_1 [text]: 0.013s over 67 frame(s), 0.2 ms a frame
- captions_2 [text]: 0.007s over 38 frame(s), 0.2 ms a frame
slowest frames: f120 (267.7 ms), f60 (230.9 ms), f0 (177.6 ms), f30 (65.2 ms), f117 (58.9 ms)
```
```json
{ "jobId": "export_1" }
```
```text
An error occurred invoking 'report_render_stats': export_1 is still running (0%); its stats are ready when it finishes
```
```json
{ "jobId": "analyze_1" }
```
```text
An error occurred invoking 'report_render_stats': analyze_1 isn't an export; report_render_stats reports start_export (export_N), export_batch (batch_N) and export_language_versions (versions_N) jobs
```
```json
{ "jobId": "export_7" }
```
```text
An error occurred invoking 'report_render_stats': export job 'export_7' not found; finished exports: batch_1, export_2, export_1
```

### `export_batch`

Exports the open project once per preset, one file after another, in one background job (`batch_N`). Each file is `exports\<fileName>_<preset>.mp4`, encoded like `start_export` with that preset: loudness, ducking, H.264 high profile.

- A preset with the canvas's shape is scaled to the preset size, as in `start_export`.
- A preset with another shape (a landscape canvas to `shorts_1080p`, or a vertical one to `youtube_1080p`) gets the whole canvas fitted in the middle. The rest of the frame is an enlarged copy of the canvas, blurred by 2% of the frame's long side. Nothing is cropped, so titles stay in frame. To crop instead, switch the canvas with `update_project preset=…` and `reframe_to_vertical`, then use `start_export`.
- [`get_job_status`](#get_job_status) shows which export is running and which files are done. When the job ends, it lists every file. If one export fails, the job stops and names the files finished before it.
- Like `start_export`, the job works on a snapshot, so later edits don't affect it.

| Parameter | Default | Notes |
|---|---|---|
| `presets` | required | Preset names, each at most once: `youtube_1080p`, `shorts_1080p` |
| `fileName` | the project name | Base name without folders; `_<preset>.mp4` is added |

```json
{ "presets": ["youtube_1080p", "shorts_1080p"], "fileName": "tora" }
```
```text
started batch_1: 2 export(s) of 120 frames @ 30 fps, one after another
- youtube_1080p: 1920x1080 -> C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora_youtube_1080p.mp4
- shorts_1080p: 1080x1920, the 1280x720 canvas fitted over a blurred copy -> C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora_shorts_1080p.mp4
poll get_job_status with jobId "batch_1"
```
While running, `get_job_status` with `jobId` `batch_1`:
```text
batch_1: running (exporting shorts_1080p, 2 of 2; done: tora_youtube_1080p.mp4) 50%, 6.9s elapsed, about 7s left — export_batch youtube_1080p + shorts_1080p
```
When done:
```text
batch_1: completed in 14.6s — export_batch youtube_1080p + shorts_1080p
exported 2 file(s):
- youtube_1080p: C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora_youtube_1080p.mp4 — 4.000s, 1920x1080 @ 30 fps, h264 yuv420p, audio aac 2ch 48000 Hz, 468.5 KB, 120 frames, took 5.9s
  processing: loudness: -20.8 LUFS → -14 LUFS (true peak at most -1 dBTP)
  processing: preset youtube_1080p: 1920x1080, H.264 high profile, CRF 18, AAC 192k
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
- shorts_1080p: C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora_shorts_1080p.mp4 — 4.000s, 1080x1920 @ 30 fps, h264 yuv420p, audio aac 2ch 48000 Hz, 376.4 KB, 120 frames, took 7.9s
  processing: loudness: -20.8 LUFS → -14 LUFS (true peak at most -1 dBTP)
  processing: preset shorts_1080p: 1080x1920, H.264 high profile, CRF 18, AAC 192k
  processing: the 1280x720 canvas is fitted whole into 1080x1920 over a blurred, enlarged copy of itself
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
```
```json
{ "presets": ["youtube_1080p", "YouTube_1080p"] }
```
```text
An error occurred invoking 'export_batch': preset 'youtube_1080p' is listed twice
```
In a project with two [languages](#languages) or more, the reply warns that each file holds the layers of every language, and points to `export_language_versions`.

### `export_language_versions`

Exports each [language](#languages)'s version once per preset, one file after another, in one background job (`versions_N`). Each file is `exports\<fileName>_<language>_<preset>.mp4`.

- A version leaves out the layers of other languages, with their children such as their caption tracks, and mutes the clips whose sound is in another language. Untagged layers are in every version.
- Each file is encoded like [`export_batch`](#export_batch) with that preset, so a preset of another shape than the canvas gets the whole canvas fitted over a blurred copy.
- [`get_job_status`](#get_job_status) shows which file is being written and which are done. When the job ends, it lists every file. If one export fails, the job stops and names the files finished before it.
- The job works on a snapshot, and starts again under its id if the server stops while it runs.
- A language no layer is tagged with still gets its files, with only the untagged layers, and a warning.

| Parameter | Default | Notes |
|---|---|---|
| `languages` | required | Language tags, each at most once |
| `presets` | required | Preset names, each at most once: `youtube_1080p`, `shorts_1080p` |
| `fileName` | the project name | Base name without folders; `_<language>_<preset>.mp4` is added |

```json
{ "languages": ["en", "ja", "my"], "presets": ["youtube_1080p", "shorts_1080p"], "fileName": "greeting" }
```
```text
started versions_1: 6 export(s) of 150 frames @ 30 fps, one after another
- en version, 10 layer(s) of other languages left out
- ja version, 8 layer(s) of other languages left out, sound of 'talk' muted
- my version, 10 layer(s) of other languages left out, sound of 'talk' muted
- en youtube_1080p: 1920x1080 -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_en_youtube_1080p.mp4
- en shorts_1080p: 1080x1920, the 1280x720 canvas fitted over a blurred copy -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_en_shorts_1080p.mp4
- ja youtube_1080p: 1920x1080 -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_ja_youtube_1080p.mp4
- ja shorts_1080p: 1080x1920, the 1280x720 canvas fitted over a blurred copy -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_ja_shorts_1080p.mp4
- my youtube_1080p: 1920x1080 -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_my_youtube_1080p.mp4
- my shorts_1080p: 1080x1920, the 1280x720 canvas fitted over a blurred copy -> C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_my_shorts_1080p.mp4
poll get_job_status with jobId "versions_1"
```
When done, the Burmese files have no sound: the clip's sound is English, and no voice is tagged `my`.
```text
versions_1: completed in 20.8s — export_language_versions en + ja + my × youtube_1080p + shorts_1080p
exported 6 file(s):
- en youtube_1080p: C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_en_youtube_1080p.mp4 — 5.000s, 1920x1080 @ 30 fps, h264 yuv420p, audio aac 2ch 48000 Hz, 5.9 MB, 150 frames, took 2.6s
  processing: loudness: -20.5 LUFS → -14 LUFS (true peak at most -1 dBTP)
  processing: preset youtube_1080p: 1920x1080, H.264 high profile, CRF 18, AAC 192k
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
- en shorts_1080p: C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_en_shorts_1080p.mp4 — 5.000s, 1080x1920 @ 30 fps, h264 yuv420p, audio aac 2ch 48000 Hz, 2.9 MB, 150 frames, took 4.4s
  processing: loudness: -20.5 LUFS → -14 LUFS (true peak at most -1 dBTP)
  processing: preset shorts_1080p: 1080x1920, H.264 high profile, CRF 18, AAC 192k
  processing: the 1280x720 canvas is fitted whole into 1080x1920 over a blurred, enlarged copy of itself
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
- ja youtube_1080p: C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_ja_youtube_1080p.mp4 — 5.000s, 1920x1080 @ 30 fps, h264 yuv420p, audio aac 2ch 48000 Hz, 6.0 MB, 150 frames, took 2.9s
  processing: loudness: -21.9 LUFS → -14 LUFS (true peak at most -1 dBTP)
  processing: preset youtube_1080p: 1920x1080, H.264 high profile, CRF 18, AAC 192k
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
- ja shorts_1080p: C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_ja_shorts_1080p.mp4 — 5.000s, 1080x1920 @ 30 fps, h264 yuv420p, audio aac 2ch 48000 Hz, 2.9 MB, 150 frames, took 4.4s
  processing: loudness: -21.9 LUFS → -14 LUFS (true peak at most -1 dBTP)
  processing: preset shorts_1080p: 1080x1920, H.264 high profile, CRF 18, AAC 192k
  processing: the 1280x720 canvas is fitted whole into 1080x1920 over a blurred, enlarged copy of itself
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
- my youtube_1080p: C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_my_youtube_1080p.mp4 — 5.000s, 1920x1080 @ 30 fps, h264 yuv420p, no audio, 5.9 MB, 150 frames, took 2.6s
  processing: preset youtube_1080p: 1920x1080, H.264 high profile, CRF 18, AAC 192k
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
- my shorts_1080p: C:\Users\you\ZeyoCut\projects\greeting\exports\greeting_my_shorts_1080p.mp4 — 5.000s, 1080x1920 @ 30 fps, h264 yuv420p, no audio, 2.8 MB, 150 frames, took 3.9s
  processing: preset shorts_1080p: 1080x1920, H.264 high profile, CRF 18, AAC 192k
  processing: the 1280x720 canvas is fitted whole into 1080x1920 over a blurred, enlarged copy of itself
  processing: encoder: h264_qsv (Intel Quick Sync, global_quality 19): the first GPU encoder that works on this computer
```
```json
{ "languages": ["en", "ja", "ja"], "presets": ["youtube_1080p"] }
```
```text
An error occurred invoking 'export_language_versions': language 'ja' is listed twice
```

### `export_thumbnail`

Saves one frame as a PNG in `exports\` and returns a preview of it, at most 960 px wide.

- Titles and other overlays are ordinary text layers. A layer meant only for the thumbnail can start and end around the thumbnail's time; `thumb_title` below runs from 1.9 to 2.1s.
- If the size has another shape than the canvas, the frame is enlarged to cover it and cropped evenly from the center.
- The frame is rendered at the canvas size and then scaled, so it looks exactly like the export. When the canvas is smaller than the thumbnail, the result says how much it was enlarged.
- Warns when the PNG is over 2 MB, YouTube's limit for custom thumbnails.

| Parameter | Default | Notes |
|---|---|---|
| `time` | required | Seconds; snaps to the nearest frame |
| `width` | 1280 | 16–7680 pixels |
| `height` | 720 | 16–7680 pixels |
| `fileName` | `thumbnail_<frame>.png` | A plain file name; `.png` is added if missing |
| `language` | every layer | Shows that [language's version](#languages) |

```json
{ "time": 2, "fileName": "tora-thumb" }
```
```text
thumbnail 1280x720 of frame 60 (t=2.000s), from the 1280x720 canvas; saved C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora-thumb.png (80.9 KB); preview 960x540
```
```json
{ "time": 2, "width": 1080, "height": 1920, "fileName": "tora-vertical" }
```
```text
thumbnail 1080x1920 of frame 60 (t=2.000s), cropped from the center of the 1280x720 canvas, enlarged 2.667x; saved C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora-vertical.png (289.8 KB); preview 960x1707
```
```json
{ "time": 2, "fileName": "tora.jpg" }
```
```text
An error occurred invoking 'export_thumbnail': thumbnails are PNG files (got '.jpg')
```
```json
{ "time": 1, "fileName": "thumb_my", "language": "my" }
```
```text
thumbnail 1280x720 of frame 30 (t=1.000s), from the 1280x720 canvas; saved C:\Users\you\ZeyoCut\projects\greeting\exports\thumb_my.png (210.9 KB); preview 960x540; my version, 10 layer(s) of other languages left out, sound of 'talk' muted
```

### `export_frames`

Writes every frame from `startTime` to `endTime`, both included, as numbered images at the canvas size. They go to `exports\<project>_frames\frame_<frame>.png`, where `<frame>` is the project frame number in 5 digits.

- The frames look exactly like the export.
- Frames an earlier `export_frames` left in the folder are deleted first, so the folder holds just this sequence.
- Runs in the background as `frames_N`; poll [`get_job_status`](#get_job_status).

| Parameter | Default | Notes |
|---|---|---|
| `startTime` | required | Seconds |
| `endTime` | required | Seconds, included; past the end, it stops at the last frame |
| `format` | `png` | `png` or `jpg` (quality 95, much smaller files) |

```json
{ "startTime": 1, "endTime": 1.5 }
```
```text
writing 16 frame(s) f30–f45 (1–1.5s, 1280x720 png) to C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora-launch_frames in the background as frames_1.
Poll get_job_status with jobId "frames_1".
```
When done:
```text
frames_1: completed in 1.8s — export_frames f30–f45 as png
wrote 16 png frame(s) f30–f45 (1–1.5s) at 1280x720 to C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora-launch_frames: frame_00030.png … frame_00045.png, 924.9 KB in all
```
A second run replaces them:
```json
{ "startTime": 0, "endTime": 0.2, "format": "jpg" }
```
```text
frames_2: completed in 0.7s — export_frames f0–f6 as jpg
wrote 7 jpg frame(s) f0–f6 (0–0.2s) at 1280x720 to C:\Users\you\ZeyoCut\projects\tora-launch\exports\tora-launch_frames: frame_00000.jpg … frame_00006.jpg, 508.4 KB in all
deleted 16 frame(s) an earlier export_frames left there
```
```json
{ "startTime": 1, "endTime": 2, "format": "gif" }
```
```text
An error occurred invoking 'export_frames': unknown format 'gif'; valid values: png, jpg
```

---

## Other editors

To finish an edit in another editor, such as DaVinci Resolve or Final Cut Pro, its timeline goes out as OTIO (`export_otio`) or FCPXML (`export_fcpxml`). Those formats hold cuts, clip timing, speed and markers, but not ZeyoCut's text, keyframed motion, effects or camera. `prerender_layers` draws such layers into video files with alpha, which go on tracks above the clips.

### `prerender_layers`

Draws layers alone, each with its children, on a transparent background, and saves each as a ProRes 4444 `.mov` with alpha in `exports\<project>_prerender\<layer id>.mov`. Each file covers every frame the layer is on, drawn as `render_frame` draws it with `layerId` and a transparent background. Runs in the background as `prerender_N`; poll [`get_job_status`](#get_job_status).

| Parameter | Default | Notes |
|---|---|---|
| `layerIds` | required | Layer ids; each is drawn with its children |

- **Which frames:** from the layer's start to its end, or to the project's end. A child shows only while its parent does, so a child's file covers only its parent's time.
- **What it holds:** the layer's text, keyframed motion, effects, masks and the camera's move, exactly as exported, over transparency. Footage is read from the original, not from its proxy. There is no sound.
- **Current prerenders aren't drawn again.** `prerender.json` in the folder lists each file with a key of every frame it holds. A layer whose frames haven't changed is reported as `already current`. Rebuilding ZeyoCut changes every key.
- **File sizes:** ProRes 4444 is large. On this laptop, a 4 s 1080p title took 1.7 s and is 20 MB, and a 10 s 1080p clip took 5.4 s and is 167 MB.

```json
{"layerIds": ["title", "lower_third"]}
```
```text
prerendering 2 layer(s) as ProRes 4444 with alpha, 1920x1080 @ 30 fps, to C:\Users\you\ZeyoCut\projects\bread\exports\bread_prerender in the background as prerender_1:
- 'title': frames 0–119 (0–4s) -> title.mov
- 'lower_third': frames 150–269 (5–9s) -> lower_third.mov
Poll get_job_status with jobId "prerender_1".
```
`get_job_status` when it is done:
```text
prerender_1: completed in 3.5s — prerender_layers title, lower_third
prerendered 2 layer(s) as ProRes 4444 with alpha to C:\Users\you\ZeyoCut\projects\bread\exports\bread_prerender; export_otio and export_fcpxml put them on tracks above the clips:
- 'title': frames 0–119 (0–4s), 20.0 MB, in 1.7s: title.mov
- 'lower_third': frames 150–269 (5–9s), 8.5 MB, in 1.7s: lower_third.mov
```
Again, with the title unchanged and the child `lower_third_role` (5–9 s like its parent) on its own:
```text
prerender_2: completed in 1.7s — prerender_layers title, lower_third_role
prerendered 2 layer(s) as ProRes 4444 with alpha to C:\Users\you\ZeyoCut\projects\bread\exports\bread_prerender (1 already current); export_otio and export_fcpxml put them on tracks above the clips:
- 'title': already current, frames 0–119 (0–4s), 20.0 MB: title.mov
- 'lower_third_role': frames 150–269 (5–9s), 6.6 MB, in 1.7s: lower_third_role.mov
```
```json
{"layerIds": ["nope"]}
```
```text
An error occurred invoking 'prerender_layers': layer 'nope' not found; existing layers: talk, title, lower_third, lower_third_role
```

### `export_otio`

Writes the timeline as [OpenTimelineIO](https://opentimeline.io) (`.otio`) to `exports\<fileName>`, for an editor such as DaVinci Resolve. The clips point at the files in the project's `assets\` folder, so the project folder has to stay where it is.

| Parameter | Default | Notes |
|---|---|---|
| `fileName` | `<project>.otio` | A plain file name; `.otio` is added when it has no extension |

What goes on the tracks:

- **Video tracks** (`V1` at the bottom) hold each video clip's picture, with its trim and speed. A clip goes on a track above every clip drawn before it that it overlaps, so the stacking is kept.
- **Prerendered layers** go on tracks above the video, when their `prerender_layers` file is current. A layer that changed since it was prerendered is left out, and the reply says so. A layer inside another layer's prerender isn't added again.
- **Audio tracks** hold the sound the export mixes: each audio layer, and the sound of each clip that isn't muted.
- **Markers** are on the timeline: chapters green, scenes blue, with the marker's id and kind in its metadata.

The reply ends with what the timeline can't hold, so the other editor's version can be fixed by hand or by prerendering:

- keyframes, and the place, size and opacity of clips that don't simply fill the frame (another editor fits each clip to the frame)
- crop, color, shadow, outline, blur, tint, mask and background removal on clips
- the camera's move
- text, images and subject cutouts that weren't prerendered, by the top layer of each group
- redactions over clips that weren't prerendered (a clip's prerender holds its redactions)
- volume, sound fades, music ducking and loudness normalization
- in a project with several languages, which language's version: every language's layers and sound are in it

The `.otio` files were checked by loading them with the `opentimelineio` Python library (0.18.1): every clip's place on its track, the media time it starts from, its speed, and the markers came out as below.

```json
{}
```
```text
wrote C:\Users\you\ZeyoCut\projects\bread\exports\bread.otio (OpenTimelineIO): 1920x1080 @ 30 fps, 17s; 2 video track(s), 2 audio track(s), 3 marker(s)
- V1: 'talk' 0–6s, 'talk_2' 6–17s
- V2: 'kitchen' 4–7s (2x)
- A1, sound: 'talk' 0–6s, 'talk_2' 6–17s
- A2, sound: 'music' 0–17s
not in the timeline:
- the place and size of 'kitchen' (another editor fits it to the frame)
- layers only ZeyoCut draws: text 'title', text 'credit'; prerender_layers draws them into video with alpha for the timeline
- the volume of 'music' at 30%
- the sound fades of 'music'
- the music ducking under the voice
- the loudness normalization to -14 LUFS
```
After `prerender_layers` with `title`:
```json
{"fileName": "bread with title"}
```
```text
wrote C:\Users\you\ZeyoCut\projects\bread\exports\bread with title.otio (OpenTimelineIO): 1920x1080 @ 30 fps, 17s; 2 video track(s), 1 of prerendered layers, 2 audio track(s), 3 marker(s)
- V1: 'talk' 0–6s, 'talk_2' 6–17s
- V2: 'kitchen' 4–7s (2x)
- V3, prerendered: 'title' 0–4s
- A1, sound: 'talk' 0–6s, 'talk_2' 6–17s
- A2, sound: 'music' 0–17s
not in the timeline:
- the place and size of 'kitchen' (another editor fits it to the frame)
- layers only ZeyoCut draws: text 'credit'; prerender_layers draws them into video with alpha for the timeline
- the volume of 'music' at 30%
- the sound fades of 'music'
- the music ducking under the voice
- the loudness normalization to -14 LUFS
```
After changing the title's text, its prerender is out of date:
```text
wrote C:\Users\you\ZeyoCut\projects\bread\exports\after edit.otio (OpenTimelineIO): 1920x1080 @ 30 fps, 17s; 2 video track(s), 2 audio track(s), 3 marker(s)
- V1: 'talk' 0–6s, 'talk_2' 6–17s
- V2: 'kitchen' 4–7s (2x)
- A1, sound: 'talk' 0–6s, 'talk_2' 6–17s
- A2, sound: 'music' 0–17s
prerendered, but changed since, so left out: 'title'; prerender_layers draws them again
not in the timeline:
- the place and size of 'kitchen' (another editor fits it to the frame)
- layers only ZeyoCut draws: text 'title', text 'credit'; prerender_layers draws them into video with alpha for the timeline
- the volume of 'music' at 30%
- the sound fades of 'music'
- the music ducking under the voice
- the loudness normalization to -14 LUFS
```
```json
{"fileName": "bread.xml"}
```
```text
An error occurred invoking 'export_otio': export_otio writes .otio files (got '.xml')
```

### `export_fcpxml`

Writes the same timeline as `export_otio` as Final Cut Pro XML (FCPXML 1.9, which DaVinci Resolve also imports) to `exports\<fileName>`. The reply is the same, with what the timeline can't hold.

| Parameter | Default | Notes |
|---|---|---|
| `fileName` | `<project>.fcpxml` | A plain file name; `.fcpxml` is added when it has no extension |

How the tracks become FCPXML:

- **`V1` is the primary storyline,** with gaps where it has no clip, through to the project's end. A project with no video has a storyline of one gap.
- **Every other clip is connected** to the storyline item under its start: video tracks and prerendered layers on lanes 1, 2, … above it, sound on lanes -1, -2, … below it. A connected clip may run past the end of the item it hangs from.
- **A clip's picture and its sound are separate clips** of the same file, one with only its video (`srcEnable="video"`) and one with only its audio, so the sound isn't doubled.
- **Speed** is a linear `timeMap` from the clip's time to its file's time.
- **Chapters** are chapter markers and **scenes** plain markers, in the storyline item under them.
- **Times are whole frames** of the project, so a sound that starts between frames moves to the nearest one.

The file was checked with a script that reads it back: every element refers to a resource that exists, every time is a whole frame, the storyline runs without holes to the end, and each clip lands where `export_otio` puts it. It hasn't been opened in Final Cut Pro or DaVinci Resolve, since neither is on this laptop.

```json
{}
```
```text
wrote C:\Users\you\ZeyoCut\projects\bread\exports\bread.fcpxml (FCPXML 1.9): 1920x1080 @ 30 fps, 17s; 2 video track(s), 1 of prerendered layers, 2 audio track(s), 3 marker(s)
- V1: 'talk' 0–6s, 'talk_2' 6–17s
- V2: 'kitchen' 4–7s (2x)
- V3, prerendered: 'title' 0–4s
- A1, sound: 'talk' 0–6s, 'talk_2' 6–17s
- A2, sound: 'music' 0–17s
not in the timeline:
- the place and size of 'kitchen' (another editor fits it to the frame)
- layers only ZeyoCut draws: text 'credit'; prerender_layers draws them into video with alpha for the timeline
- the volume of 'music' at 30%
- the sound fades of 'music'
- the music ducking under the voice
- the loudness normalization to -14 LUFS
```
The storyline of that file:
```xml
<spine>
  <asset-clip ref="r2" offset="0s" name="talk" start="0s" duration="180/30s" tcFormat="NDF" srcEnable="video">
    <asset-clip ref="r3" offset="120/30s" name="kitchen" start="30/30s" duration="90/30s" tcFormat="NDF" lane="1">
      <timeMap>
        <timept time="0s" value="0s" interp="linear" />
        <timept time="150/30s" value="300/30s" interp="linear" />
      </timeMap>
    </asset-clip>
    <asset-clip ref="r4" offset="0s" name="title (prerendered)" start="0s" duration="120/30s" tcFormat="NDF" lane="2" />
    <asset-clip ref="r2" offset="0s" name="talk" start="0s" duration="180/30s" tcFormat="NDF" srcEnable="audio" lane="-1" />
    <asset-clip ref="r5" offset="0s" name="music" start="0s" duration="510/30s" tcFormat="NDF" lane="-2" />
    <chapter-marker start="0s" duration="1/30s" value="Intro" posterOffset="0s" />
  </asset-clip>
  <asset-clip ref="r2" offset="180/30s" name="talk_2" start="270/30s" duration="330/30s" tcFormat="NDF" srcEnable="video">
    <asset-clip ref="r2" offset="270/30s" name="talk_2" start="270/30s" duration="330/30s" tcFormat="NDF" srcEnable="audio" lane="-1" />
    <chapter-marker start="270/30s" duration="1/30s" value="Kneading" posterOffset="0s" />
    <chapter-marker start="450/30s" duration="1/30s" value="Baking" posterOffset="0s" />
  </asset-clip>
</spine>
```
```json
{"fileName": "bread.otio"}
```
```text
An error occurred invoking 'export_fcpxml': export_fcpxml writes .fcpxml files (got '.otio')
```

---

## Workflow prompts

Besides its tools, the server offers four MCP prompts. A prompt is a step-by-step recipe that the client puts into the chat for the assistant to follow; the user picks it, much like a slash command. Each recipe names the tools to call in order, fills in the paths and names it was given, and says where to stop and ask the user. A prompt changes nothing by itself.

| Prompt | Arguments | Steps | Stops to ask the user |
|---|---|---|---|
| `talking_head_cleanup` | `video`; `name`, `language` (default `auto`) | Cuts the pauses with punch-in zooms, adds captions, checks the loudness, runs the checks and exports 16:9 and 9:16 with `export_batch` | After the `remove_silences` dry run |
| `privacy_blur` | `video`; `target` (`faces`, `plates` or `both`, the default), `name` | Finds the faces and plates, hides the tracks the user picks, looks at every span `redaction_report` lists and exports | At the contact sheets of the tracks found, and before the export |
| `new_episode` | `name`; `template`, `script` | Builds the episode with `build_from_script`, or starts from a template, adds scene markers, then each scene's rigs, actions, sounds and titles; checks and exports | For a template when none is given, and for the scenes |
| `comic_panels` | `folder`; `voiceover`, `name` | Imports the panels in order, lays them out in a row and moves the camera from panel to panel, timed to the voiceover's sentences, with captions and a scene marker per panel | To confirm the panel times, or to ask for them without a voiceover |

- **The first argument is required.** `name` defaults to the video's or the folder's name.
- **Tested:** the `talking_head_cleanup` and `privacy_blur` steps run end to end on generated clips in the test suite. On a 15.9 s generated talk, the steps cut 3 pauses (5.6 s) with a punch-in on the middle piece, added 3 captions, measured -14.6 LUFS and exported both shapes.
- **An unknown `target`** is an error: `unknown target 'cars'; valid values: faces, plates, both`.

`talking_head_cleanup`:
```json
{"video": "C:\\videos\\interview.mp4"}
```
```text
Clean up the talking-head video C:\videos\interview.mp4 with the ZeyoCut tools, step by step. Report briefly after each step. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

1. create_project with name "interview", preset youtube_1080p and duration 1; the clip extends the project to its own length.
2. import_video with path "C:\videos\interview.mp4" (footage that needs normalizing is done in the background), then add_video_layer with the asset id it returned.
3. analyze_video on that asset with language "auto". It finds the silences and transcribes the words. Without a transcript (whisper isn't set up), tell the user that captions can't be made, and leave out step 6.
4. remove_silences on the clip with dryRun=true. Stop and ask the user: tell them how many pauses it would cut and how much shorter the video gets, and ask whether to go ahead or keep more of the pauses (a higher minSilence).
5. When they agree, remove_silences again with dryRun=false and punchIn=true. Every second piece is zoomed in (punchInZoom, default 1.1), which hides the jump cuts. If get_transcript shows filler words or false starts, offer to cut them with cut_by_transcript.
6. generate_captions with the asset id, in style subtitle unless the user asked for another. The captions follow the cuts; after more cuts, run it again.
7. render_contact_sheet over the whole video, to look at the cuts, the punch-ins and the captions.
8. Loudness: exports are normalized to -14 LUFS (update_project loudnessTarget changes it). audio_levels measures the mix as it will be exported; fix clipping or sections it flags as much quieter.
9. check_project and frame_diff. Fix what they find.
10. export_batch with presets ["youtube_1080p", "shorts_1080p"]: a 16:9 file, and a 9:16 one with the whole picture over a blurred copy of itself. When the job is done, give the user both file paths.

For a 9:16 version cropped to the speaker instead: update_project preset=shorts_1080p, reframe_to_vertical with focusX on the speaker, generate_captions again, then start_export preset=shorts_1080p. Undo, three times, goes back to the 16:9 project.
```
`privacy_blur`:
```json
{"video": "C:\\videos\\street.mp4"}
```
```text
Hide the faces and license plates in the video C:\videos\street.mp4 with the ZeyoCut tools, step by step. Nothing is hidden until the user has chosen what to hide. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

1. create_project with name "street", preset youtube_1080p and duration 1; the clip extends the project to its own length.
2. import_video with path "C:\videos\street.mp4" (footage that needs normalizing is done in the background). If the clip is taller than wide, update_project preset=shorts_1080p. Then add_video_layer with the asset id it returned.
3. detect_and_track on the clip with target faces, then again with target plates. Each runs as a detect_N job. Also run analyze_video on the asset: a redaction's box isn't held across the scene cuts it finds. If a reply says the job would take more than 5 minutes, tell the user the estimate, and start it with confirmLongJob=true only if they agree.
4. Stop and ask the user. Show them the contact sheets from get_job_status, which show every track with its id, and ask which to hide: some ids, or all. Tracks with a low confidence may be something else. If something they want hidden wasn't found, draw a box around it at one time and follow it with track_object.
5. apply_redaction with the tracks they chose, in style pixelate unless they want blur or solid.
6. redaction_report. Look at each span it lists with render_frame debug=true (it draws each track's box) at the span's start, middle and end. The report can't see the picture, so a span where nothing shows is fine. Fix what does show: raise paddingPercent or holdFrames on the redaction layer with update_layer, redraw a box that drifted with reanchor_track (tracks from track_object), or correct a box by hand after tracks_to_keyframes. A box held past a cut that analyze_video didn't find covers part of the next shot for holdFrames frames; if that shows, lower holdFrames. After a fix, run redaction_report again. Tell the user exactly what still shows and where, if anything does.
7. Stop and ask the user before exporting: show them the report's summary and a render_contact_sheet. When they agree, start_export with the project's preset.
```
`new_episode` with a template:
```json
{"name": "tora-ep3", "template": "tora show"}
```
```text
Start the new episode "tora-ep3" from the ZeyoCut library, step by step. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

1. list_library shows the templates, rigs, actions and styles; list_sfx shows the sound effects.
2. If the user has a script (scenes with rigs, actions, sounds and captions at times), build_from_script builds the whole episode in one call (docs/GUIDE.md has the JSON and Markdown formats): use it with name "tora-ep3", tell the user what it couldn't map, and go on at step 6. Otherwise:
3. new_project_from_template with template "tora show", name "tora-ep3" and the episode's length as duration.
4. Stop and ask the user for the scenes if they haven't given them: a name and a length for each. add_markers with kind scene at each scene's start.
5. For each scene:
   - add_rig for each character in it, with startTime at the scene's start, x and y where the character stands, and an instanceName (a second copy of a rig needs its own). To take a character off at the scene's end, update_layer end on its root layer; its parts follow.
   - apply_action for what each character does (startTime, speed, loops), add_sfx for the sounds, and add_text_layer with a library style for titles or dialogue.
6. render_contact_sheet over each scene (startTime and endTime from the markers) to check the poses, timing and framing. Use render_frame debug=true when a rig looks wrong.
7. check_project, frame_diff and audio_levels; fix what they find. Then start_export with the project's preset.
```
With `script` instead, step 2 runs `build_from_script` on it:
```text
2. build_from_script with scriptFile "C:\Users\you\scripts\tora-ep4.md" and name "tora-ep4" builds the whole episode: the scenes with a scene marker each, rigs, actions, sound effects and captions. Tell the user what it couldn't map, add those by hand as in step 5, and go on at step 6.
```
`comic_panels` with a voiceover:
```json
{"folder": "C:\\Users\\you\\comics\\episode 3", "voiceover": "C:\\Users\\you\\comics\\narration.wav"}
```
```text
Turn the comic panels in C:\Users\you\comics\episode 3 into a video with the ZeyoCut tools, step by step: the camera moves from panel to panel, timed to the voiceover C:\Users\you\comics\narration.wav, with captions. When a tool replies with a job id, poll get_job_status (get_export_status for start_export) until the job is done.

1. create_project with name "episode 3" and preset youtube_1080p, or shorts_1080p if the panels are taller than wide.
2. import_folder with path "C:\Users\you\comics\episode 3". The files come in name order, numbers by value (panel_2 before panel_10); if that isn't the reading order, ask the user.
3. The voiceover: import_asset with path "C:\Users\you\comics\narration.wav" unless import_folder brought it in, add_audio_layer with role voice, and update_project duration to its length plus one second. analyze_video on its asset transcribes the words with their times; get_transcript reads them.
4. Time the panels: split the narration at sentence ends into one part per panel, in order. Stop and ask the user to confirm the plan: each panel with its time and the words over it.
5. Lay the panels out in a row: add_image_layer for each, at scale 1, with y at the canvas' vertical center. The first panel's x is the canvas' horizontal center; each next panel's x adds half the previous panel's width, a gap of half the canvas width, and half its own width, so the neighbours stay out of the frame.
6. Move the camera with one set_keyframes call on layerId "camera", properties x, y and zoom:
   - at each panel's start, x and y at the panel's center and zoom 0.92 times the smaller of canvas width / panel width and canvas height / panel height, so the whole panel fits;
   - the same values again 0.5 s before the next panel's start, with easing easeInOut, so the camera holds and then pans; a zoom 5% higher there gives a slow push-in.
7. add_markers with kind scene at each panel's start, named after the panel.
8. generate_captions with the voiceover's asset id. Caption groups ignore the camera.
9. render_contact_sheet over the whole video: each panel should fill the frame in its time, with the captions readable. check_project and audio_levels; fix what they find. Then start_export with the project's preset.
```
