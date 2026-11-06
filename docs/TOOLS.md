<!-- Made by RepositoryFilesTests.TheToolReferenceMatchesTheServer from the server's tool list; don't edit it by hand.
     After changing a tool or a prompt, run that test with ZEYOCUT_UPDATE_DOCS=1 to write it again. -->

# Tool reference

The `zeyocut` server has 90 tools and 4 [workflow prompts](#workflow-prompts). Each is listed here as the server describes it to the model, with its parameters. The [guide](GUIDE.md) shows how they work together, with an example call and the real response for each. One more tool, `describe_clip`, appears only when a clip describer is configured (see the [guide](GUIDE.md#optional-describe_clip)).

| Tool | What it does |
|---|---|
| [`add_audio_layer`](#add_audio_layer) | Add an audio layer from an imported audio asset. |
| [`add_caption_track`](#add_caption_track) | Add the captions of one language as a track of their own: text layers under a null layer captions_&lt;language&gt; (e.g. captions_ja) tagged with that language, so each language's version shows only its own captions (render_frame and start_export take language). |
| [`add_image_layer`](#add_image_layer) | Add an image layer (a cutout part or a still). |
| [`add_markers`](#add_markers) | Add or change project markers in one call (one undo step; all-or-nothing): named points on the timeline, each a chapter (the default) or a scene. |
| [`add_rig`](#add_rig) | Place a rig from the library in the open project (one undo step). |
| [`add_sfx`](#add_sfx) | Add a sound effect from the library at a time (one undo step): the file is imported into the project once (later adds of the same sound reuse the asset) and placed as an audio layer with role effects, so music ducking leaves it alone. |
| [`add_text_layer`](#add_text_layer) | Add a text layer (titles, subtitles). |
| [`add_transition`](#add_transition) | Add a transition between layerA and layerB, which must be back to back (or already overlapping). |
| [`add_video_layer`](#add_video_layer) | Add a video clip from a video asset. |
| [`analyze_video`](#analyze_video) | Analyze a video (or audio) asset in the background: scene cuts (ffmpeg scdet), the noise floor and silences, and a word-level transcript (whisper-cli, if WHISPER_PATH/WHISPER_MODEL are set or whisper-cli is on PATH). |
| [`apply_action`](#apply_action) | Play a saved action on a rig in the open project (one undo step). |
| [`apply_redaction`](#apply_redaction) | Hide tracked faces, plates or objects (one undo step): adds a redaction layer (type redaction) that hides the box of each track on the video frame before the clip is drawn, in every clip that plays the track's footage, so moves, effects, the camera and subject cutouts all show it hidden, and splits, trims and speed changes keep it in step. |
| [`apply_style`](#apply_style) | Restyle a text layer, or every text layer under a group (e.g. 'captions'), with a library style (one undo step). |
| [`attach_to_track`](#attach_to_track) | Keep a text or image layer on a track's box (one undo step), such as a name over a face or a sticker on a sign: x and y keyframes, and scaleX and scaleY with followScale, on every frame where a clip shows the footage the track found the object in, through the clip's placement, parents and crop, the camera, and the layer's own parent. |
| [`audio_levels`](#audio_levels) | Measure the sound exactly as the export would mix it (fades, music ducked under voice, loudness normalization): integrated loudness (LUFS, ITU-R BS.1770) and sample peak (dBFS) for the whole mix and per section, and flags clipping (samples at full scale), sections more than 10 LU quieter than the whole mix, a whole mix below -24 LUFS with normalization off, and silences of 1s or more in the middle (silence at the very start or end is listed but not flagged). |
| [`auto_lip_flap`](#auto_lip_flap) | Animate a mouth to a voice (one undo step): measures the loudness of every frame of the sound as it plays on the timeline (start and trim respected), gives quiet frames the closed drawing and louder frames the open drawings (listed from slightly to wide open), holds every drawing at least 2 frames so the mouth doesn't flicker, and writes hold keyframes on the mouth's drawing over the time both play, replacing the drawing keyframes there. |
| [`build_from_script`](#build_from_script) | Build a whole episode from a script and open it as a new project: the canvas or a library template, then each scene's rigs (shown for that scene), actions, sound effects and captions at their times, and a scene marker at each scene's start. |
| [`camera_shake`](#camera_shake) | Shake the camera (one undo step): writes seeded keyframes on the camera's shakeX, shakeY and shakeRotation channels, on top of its own movement, fading out to still at the end. |
| [`check_project`](#check_project) | Check the open project for problems before export: assets whose files are missing, layers that are never on the canvas while shown, text reaching into the 5% title-safe margin (on vertical 9:16 canvases: the Shorts player UI, top 8%, bottom 25%, right 12%), characters a text's font has no glyphs for (each text is shaped with HarfBuzz, as it is drawn), stretches where nothing but the background shows, keyframes outside a layer's time that never play, and assets no layer uses. |
| [`clear_keyframes`](#clear_keyframes) | Remove all keyframes of one property, or of every property of a layer when property is omitted (that also takes a motion path off). |
| [`create_project`](#create_project) | Create a new project folder (project.json + assets/ renders/ exports/) and open it. |
| [`cut_by_transcript`](#cut_by_transcript) | Remove words from a video or audio layer by their transcript indices (the [n] numbers from get_transcript), with ripple. |
| [`detect_and_track`](#detect_and_track) | Find every face or license plate in a video clip with an AI model and link each into a track (face_1, face_2, ... |
| [`detect_beats`](#detect_beats) | Find the beats of a music (or other audio or video) asset: onsets from the spectral flux of the sound, the tempo from their autocorrelation (60-200 BPM, favoring 120 between octaves), and a steady beat grid fitted to the onsets. |
| [`duplicate_layer`](#duplicate_layer) | Copy a layer, by default with all its children (a whole rig), including keyframes. |
| [`export_batch`](#export_batch) | Export the open project once per preset in one background job, one file after another, to exports/&lt;fileName&gt;_&lt;preset&gt;.mp4 (e.g. launch_youtube_1080p.mp4 and launch_shorts_1080p.mp4). |
| [`export_fcpxml`](#export_fcpxml) | Write the timeline as Final Cut Pro XML (FCPXML 1.9, which DaVinci Resolve also reads) to exports/&lt;fileName&gt;: the first video track as the primary storyline with gaps through to the end, and every other clip connected above it (video, then the layers prerendered by prerender_layers) or below it (the sound the export mixes), with their timing, trims and speed in whole frames, and the markers (chapters as chapter markers). |
| [`export_frames`](#export_frames) | Write every frame from startTime to endTime (both included, snapped to frames) as numbered images at the canvas size, exactly as exported, to exports/&lt;project&gt;_frames/frame_&lt;frame&gt;.png, where &lt;frame&gt; is the project frame number in 5 digits (frame_00012.png). |
| [`export_language_versions`](#export_language_versions) | Export the open project once per language and preset in one background job, one file after another, to exports/&lt;fileName&gt;_&lt;language&gt;_&lt;preset&gt;.mp4 (e.g. launch_ja_shorts_1080p.mp4). |
| [`export_markers`](#export_markers) | Write the chapter markers as a YouTube chapter list to exports/&lt;fileName&gt;, to paste into the video's description: a "m:ss Name" line per chapter ("h:mm:ss" from an hour on), in whole seconds. |
| [`export_otio`](#export_otio) | Write the timeline as OpenTimelineIO (.otio) to exports/&lt;fileName&gt;, for another editor such as DaVinci Resolve: each video clip's picture on video tracks (V1 at the bottom, a clip above the clips drawn before it that it overlaps), the layers prerendered by prerender_layers on tracks above them, the sound the export mixes on audio tracks, and the markers. |
| [`export_srt`](#export_srt) | Write the captions (the text layers under the caption group) as an .srt subtitle file to exports/&lt;fileName&gt;, with timeline times. |
| [`export_thumbnail`](#export_thumbnail) | Save one frame as a PNG thumbnail in exports/ (default 1280x720, YouTube's thumbnail size) and return a preview of it. |
| [`frame_diff`](#frame_diff) | Find glitch frames: renders every frame of the range small (160 px wide), scores how much each one changed from the previous one (0-100% mean color difference) and flags 'glitch' (a single frame unlike both neighbors while they match each other, e.g. a stray one-frame layer) and 'spike' (a large change with no layer starting, ending or swapping drawings there). |
| [`generate_captions`](#generate_captions) | Turn the transcript of an analyzed asset (analyze_video) into caption text layers under a null layer 'captions' (one undo step). |
| [`get_export_status`](#get_export_status) | Progress of an export job; when finished, the output path, duration, resolution, fps, codecs and file size. |
| [`get_job_status`](#get_job_status) | Progress or result of a background job: import_N (import_video normalization), analyze_N (analyze_video), framediff_N (frame_diff), audiolevels_N (audio_levels), beats_N (detect_beats), batch_N (export_batch), versions_N (export_language_versions), frames_N (export_frames), prerender_N (prerender_layers), matte_N (remove_background, text_behind_subject or generate_captions with aiImage or aiVideo), occlusion_N (text_occlusion), captions_N (generate_captions behindSubject=auto), syscheck_N (system_check), tracking_N (track_object or reanchor_track over more than 150 frames), detect_N (detect_and_track; when done it also shows the contact sheet of the tracks), proxy_N (the 1920 px proxy of footage over 2560 px, made by import_video, import_folder and open_project) or export_N (start_export). |
| [`get_logs`](#get_logs) | Read the server's log, oldest first: the last lastN entries of the log files (zeyocut-yyyyMMdd.jsonl in the log folder, one JSON object per line). |
| [`get_project`](#get_project) | Return the full project model (project.json) of the open project. |
| [`get_transcript`](#get_transcript) | Read the word-level transcript of an analyzed asset (run analyze_video first). |
| [`import_asset`](#import_asset) | Copy an image (png/jpg/webp/bmp), audio (wav/mp3/m4a/aac/ogg/opus/flac) or font (ttf/otf/ttc) file into the project's assets folder and register it. |
| [`import_folder`](#import_folder) | Import every image, audio, font and video file in a folder (not its subfolders) whose name matches pattern, in order: by name with numbers in order (panel_2 before panel_10), or by date, oldest first. |
| [`import_srt`](#import_srt) | Import a .srt subtitle file as the caption track of its language, as add_caption_track does with srt: text layers under a null layer captions_&lt;language&gt;, replacing that language's track if there is one (one undo step). |
| [`import_video`](#import_video) | Import a video file (mp4/mov/m4v/mkv/webm/avi) as a video asset. |
| [`list_assets`](#list_assets) | List the project's assets with their ids, types, sizes or durations, and which layers use them. |
| [`list_layers`](#list_layers) | Show the layers as a parent/child tree with type, zIndex, time range, language, anchor and keyframed properties. |
| [`list_library`](#list_library) | List what is in the shared library: rigs, actions, styles and templates, with a short description of each. |
| [`list_projects`](#list_projects) | List the projects in the projects root folder. |
| [`list_sfx`](#list_sfx) | List the sound effects in the shared library's sfx folder (wav, mp3, ogg and other audio files; subfolders are categories), with their durations. |
| [`move_layer`](#move_layer) | Move a layer to a new timeline start, together with its children and keyframes. |
| [`new_project_from_template`](#new_project_from_template) | Create a new project from a library template and open it. |
| [`open_project`](#open_project) | Open an existing project by name (a folder under the projects root) or by absolute folder path. |
| [`prerender_layers`](#prerender_layers) | Render layers alone, each with its children, on a transparent background as ProRes 4444 .mov files with alpha in exports/&lt;project&gt;_prerender/&lt;layer id&gt;.mov: every frame the layer is on, as render_frame draws it with layerId and a transparent background. |
| [`preview_server`](#preview_server) | Start or stop the preview page, for a browser on this computer: a timeline scrubber, the layers, and playback of low-res frames with the sound mixed as the export mixes it. |
| [`reanchor_track`](#reanchor_track) | Correct a track where it drifted (one undo step): set the box at 'time' (canvas pixels, as render_frame shows that frame) and track again from there, away from the frame track_object started at, up to the next box set by hand or the end of the track. |
| [`redaction_report`](#redaction_report) | Check how well the redactions hide their tracks; changes nothing. |
| [`redo`](#redo) | Redo the last undone change. |
| [`refine_matte`](#refine_matte) | Fix the edges of a removed background (one undo step): shrink pulls the edge in by that many pixels to cut off a halo of the old background (negative grows it back, whole pixels), feather softens it over about that many pixels. |
| [`reframe_to_vertical`](#reframe_to_vertical) | Fit landscape video layers to a vertical canvas (switch it first with update_project preset=shorts_1080p): each clip gets a full-height crop with the canvas' aspect ratio at focusX, centered and scaled to fill the frame. |
| [`remove_background`](#remove_background) | Remove the background of an image or video layer so what is behind it shows through (one undo step; replaces an earlier removal, keeping refine_matte's shrink and feather). |
| [`remove_keyframes`](#remove_keyframes) | Remove specific keyframes (all-or-nothing). |
| [`remove_layer`](#remove_layer) | Remove a layer. |
| [`remove_markers`](#remove_markers) | Remove project markers (one undo step): the ones named in markerIds, every marker with markerIds ["all"], or every marker of one kind with kind. |
| [`remove_range`](#remove_range) | Cut timeline seconds start..end out of one layer (and its children) or out of every layer (layerId "all"). |
| [`remove_silences`](#remove_silences) | Cut the silent parts out of a video or audio layer with ripple (later layers move left and the project gets shorter), keeping 'padding' seconds of quiet next to the remaining sound. |
| [`render_contact_sheet`](#render_contact_sheet) | Render several evenly spaced frames into one labeled grid image to check motion over time in a single call. |
| [`render_frame`](#render_frame) | Render one frame exactly as it will appear in the export and return it as a PNG. |
| [`report_render_stats`](#report_render_stats) | Where a finished export's time went: per stage (evaluating keyframes, decoding video and images, redaction, background removal and cutouts, text, effects and masks, drawing layers, fitting the canvas into a preset, waiting for the encoder, measuring the loudness), the slowest layers and the slowest frames; times under 5 ms are left out. |
| [`restore_backup`](#restore_backup) | List the open project's backups, newest first, or put one back. |
| [`save_action`](#save_action) | Save a rig's animation between two times to the library as a reusable action (walk cycle, jump, wing flap, double-take). |
| [`save_rig`](#save_rig) | Save a character rig to the shared library (used by every project): the root layer and everything parented under it, with anchors, drawings, parenting and draw order, posed as it is at 'time'. |
| [`save_style`](#save_style) | Save a text layer's look (font, bold, size, color, outline, alignment, line height, background box) as a named style in the library. |
| [`save_template`](#save_template) | Save layers of the open project (intro, outro, end screen, watermark, fixed layers) with the canvas, frame rate and audio settings as a template in the library, with their asset files. |
| [`set_camera`](#set_camera) | Set the project camera's fixed values (one undo step). |
| [`set_effect`](#set_effect) | Add, change or remove one effect on a visual layer (one undo step). |
| [`set_keyframes`](#set_keyframes) | Add or replace many keyframes in one call (one undo step; all-or-nothing). |
| [`set_mask`](#set_mask) | Add, change or remove the layer's mask (one undo step): the layer shows only inside a rectangle, an ellipse or an image's opaque pixels, or only outside with invert=true. |
| [`set_motion_path`](#set_motion_path) | Move a layer along a curve through timed points (one undo step; replaces an earlier path). |
| [`set_speed`](#set_speed) | Change a video layer's playback speed (0.1..10) keeping its start; its keyframes and children stretch with it. |
| [`split_layer`](#split_layer) | Split a layer (and its children) at a timeline time into two layers that play exactly as before. |
| [`start_export`](#start_export) | Start exporting the open project to exports/&lt;fileName&gt; as H.264 MP4 (yuv420p) with all audio layers mixed: music ducked under voice and the mix loudness-normalized (two-pass loudnorm, -14 LUFS by default; see update_project). |
| [`system_check`](#system_check) | Report this computer: whether FFmpeg (needed for video and every export) and whisper (for transcripts) are installed, with how to install them; the folders ZeyoCut uses; and for the AI features the CPU, RAM, GPU, the ONNX Runtime version, which AI models are downloaded and the device each one runs on (DirectML on the GPU, or the CPU). |
| [`text_behind_subject`](#text_behind_subject) | Put text behind the subject of a clip or picture, like a title behind a presenter (one undo step): the text stays in front of the background, and the subject is drawn again above it, cut out. |
| [`text_occlusion`](#text_occlusion) | Measure how much of a text layer is covered by what is drawn above it (a subject cutout from text_behind_subject, or any other layer), frame by frame: the min, max and average share of the text hidden, and the frames in runs of equal values (every frame is saved to renders/text_occlusion_&lt;id&gt;_&lt;first&gt;-&lt;last&gt;.json). |
| [`track_object`](#track_object) | Follow an object through a video clip with OpenCV and save its box on every frame as a track (one undo step). |
| [`tracks_to_keyframes`](#tracks_to_keyframes) | Turn a hidden track's box into keyframes on its redaction (one undo step), to edit by hand: x, y, width and height keyframes named &lt;track&gt;.x, &lt;track&gt;.y, &lt;track&gt;.width and &lt;track&gt;.height on the redaction layer, made from the fail-safe box (padding, gaps, holds and smoothing included) and kept only where needed to stay within 0.2 px of it. |
| [`undo`](#undo) | Undo the last change to the open project (in-memory history of this session, up to 200 steps). |
| [`update_layer`](#update_layer) | Change static fields of a layer. |
| [`update_project`](#update_project) | Change the open project's settings. |
| [`view_source`](#view_source) | Contact sheet of a video asset's own frames (not the timeline), each labeled with its source time and frame number, to find trim and cut points before or after adding the clip. |

## Tools

### `add_audio_layer`

Add an audio layer from an imported audio asset. Audio is only heard in the export (mixed by FFmpeg).

| Parameter | Type | Description |
|---|---|---|
| `asset` | string, required | Audio asset id. |
| `id` | string |  |
| `name` | string |  |
| `start` | number | When the sound starts on the timeline, seconds. Default 0. |
| `end` | number | Cut the sound at this timeline time, seconds. |
| `trimIn` | number | Start point inside the source file, seconds. Default 0. |
| `trimOut` | number | End point inside the source file, seconds. Default: end of file. |
| `volume` | number | Linear gain, 1 = original. Default 1. |
| `fadeIn` | number | Fade-in length, seconds. |
| `fadeOut` | number | Fade-out length, seconds. |
| `role` | string | What the sound is in the mix: music (default; ducked under voice), voice (voiceover, dialogue) or effects. |
| `language` | string | Language tag (en, ja, my, pt-BR): the sound is only in that language's version, e.g. a dubbed voice. Default: every version. |

### `add_caption_track`

Add the captions of one language as a track of their own: text layers under a null layer captions_&lt;language&gt; (e.g. captions_ja) tagged with that language, so each language's version shows only its own captions (render_frame and start_export take language). Give exactly one source: cues (the start, end and text of each caption, e.g. a translation you wrote from get_transcript), srt (SRT subtitle text, or the path to a .srt file) or fromTranscript (an analyzed asset: its transcript's words, timed through the clips that play it, as generate_captions does). Adding a language's track again replaces it; one undo step. The captions get the language's font from the settings (my: Myanmar Text, ja: Yu Gothic) unless fontFamily is given, and the look of the style: subtitle (default), bold_center, minimal or a library style. Cues without text or past the project end are left out, one running past the end is shortened, and overlapping cues are named. The reply warns about characters the font has no glyphs for.

| Parameter | Type | Description |
|---|---|---|
| `language` | string, required | The captions' language: en, ja, my, pt-BR. |
| `cues` | array of objects | The captions: [{start, end, text}], seconds on the timeline; \n in a text breaks the line. |
| `cues[].start` | number | When the caption appears, seconds on the timeline. |
| `cues[].end` | number | When it goes, seconds (after start). |
| `cues[].text` | string | The caption's text; \n breaks the line. |
| `srt` | string | SRT subtitle text, or the path to a .srt file. |
| `fromTranscript` | string | An analyzed asset (analyze_video) whose transcript becomes the captions. |
| `style` | string, default `"subtitle"` | subtitle, bold_center, minimal or a library style name. Default subtitle. |
| `fontFamily` | string | Font family. Default: the language's font from the settings, otherwise the style's. |
| `fontSize` | number | Font size in pixels. Default: scaled to the canvas per style. |
| `color` | string | Text color. Default #FFFFFF. |

### `add_image_layer`

Add an image layer (a cutout part or a still). Give one image with 'asset', or several named drawings with 'drawings' (swap them with keyframes on the 'drawing' property). With neither, the layer is an invisible null that only carries a transform (useful as a rig root). x,y is where the anchor lands in the parent's pixels (canvas for top-level layers).

| Parameter | Type | Description |
|---|---|---|
| `asset` | string | Image asset id for a single drawing named 'normal'. |
| `drawings` | object, name → string | Named drawings, e.g. {"normal": "head_png", "shocked": "head_shocked_png"}. |
| `drawing` | string | Drawing shown initially. Default: the first drawing. |
| `id` | string | Layer id. Default: derived from name or asset. |
| `name` | string | Display name. |
| `parentId` | string | Parent layer id; this layer then moves with the parent. |
| `x` | number | Position of the anchor in the parent's pixels. Default: canvas center, or the parent's anchor for children. |
| `y` | number | See x. |
| `anchorX` | number | Pivot in this image's own pixels. Default: image center. |
| `anchorY` | number | See anchorX. |
| `rotation` | number | Degrees, clockwise. |
| `scaleX` | number |  |
| `scaleY` | number |  |
| `opacity` | number | 0..1 |
| `zIndex` | integer | Draw order across all layers (higher is in front). Default: on top. |
| `start` | number | Seconds. Default 0. |
| `end` | number | Seconds (exclusive). Default: project end. |
| `flipX` | boolean | Mirror this layer's pixels around anchorX (children are not flipped). |
| `visible` | boolean |  |
| `language` | string | Language tag (en, ja, my, pt-BR): the layer and its children are only in that language's version. Default: every version. |

### `add_markers`

Add or change project markers in one call (one undo step; all-or-nothing): named points on the timeline, each a chapter (the default) or a scene. A marker whose id exists is changed: moved, renamed, and its kind changed only when given. Otherwise it is added, with an id made from its name unless one is given. Markers move with the timeline when remove_range, remove_silences, cut_by_transcript, set_speed or a crossfade ripple it; a marker inside removed time moves to where the cut is. list_layers shows them.

| Parameter | Type | Description |
|---|---|---|
| `markers` | array of objects, required | Markers: [{time, name, kind, id}]. |
| `markers[].time` | number | Time in seconds, from 0 to the project end. |
| `markers[].name` | string | The marker's name, one line; for a chapter, its title. |
| `markers[].kind` | string | chapter (the default for a new marker) or scene. |
| `markers[].id` | string | An existing marker's id, to change that marker; or the id for a new one (default: made from the name). |

### `add_rig`

Place a rig from the library in the open project (one undo step). Layer ids are &lt;instanceName&gt;_&lt;part&gt; (tora_head, tora_2_head for a second copy); the drawings are copied into assets/ once and shared by later copies. The rig is drawn on top of the existing layers, keeping its own part order.

| Parameter | Type | Description |
|---|---|---|
| `name` | string, required | Rig name from list_library. |
| `x` | number | Where the root's anchor lands, canvas pixels. Default: canvas center. |
| `y` | number | See x. |
| `startTime` | number, default `0` | When the rig appears, seconds. Default 0. |
| `scale` | number, default `1` | Multiplies the root's scale (0..100]. Default 1. |
| `instanceName` | string | Id prefix for the parts. Default: the rig name, numbered when taken. |

### `add_sfx`

Add a sound effect from the library at a time (one undo step): the file is imported into the project once (later adds of the same sound reuse the asset) and placed as an audio layer with role effects, so music ducking leaves it alone. Change it afterwards like any audio layer (update_layer, move_layer).

| Parameter | Type | Description |
|---|---|---|
| `name` | string, required | Sound effect name from list_sfx (impacts/thud, or thud when unique). |
| `time` | number, required | When it plays, seconds on the timeline. |
| `volume` | number, default `1` | Volume 0-10, 1 = as recorded. Default 1. |

### `add_text_layer`

Add a text layer (titles, subtitles). Complex scripts are shaped with HarfBuzz; use fontFamily "Myanmar Text" for Burmese and "Yu Gothic" for Japanese. With a language and neither fontFamily nor fontAsset, the text gets that language's font from the settings (my: Myanmar Text, ja: Yu Gothic), even over a style's font. Warns about characters the font has no glyphs for. The text's own pixel space starts at the top-left of its box; the box is maxWidth wide if set, otherwise as wide as the longest line. Returns the measured box size. 'style' fills in everything you leave out from a library style (save_style).

| Parameter | Type | Description |
|---|---|---|
| `text` | string, required | The text. Use \n for line breaks. |
| `id` | string | Layer id. Default: derived from the text. |
| `name` | string |  |
| `fontFamily` | string | Installed font family. Default "Segoe UI". |
| `fontAsset` | string | Font asset id (an imported ttf/otf); overrides fontFamily. |
| `bold` | boolean |  |
| `fontSize` | number | Pixels. Default 48. |
| `color` | string | #RRGGBB or #RRGGBBAA. Default #000000. |
| `strokeColor` | string | Outline color. |
| `strokeWidth` | number | Outline thickness in pixels outside the glyphs. Default 4 when strokeColor is set. |
| `align` | string | left, center or right (within the box). |
| `maxWidth` | number | Wrap lines to this width in pixels. |
| `lineHeight` | number | Multiple of the font's natural line spacing. Default 1. |
| `box` | object | Optional background box behind the text. |
| `box.color` | string | Box color, #RRGGBB or #RRGGBBAA (default #000000B3). |
| `box.padding` | number | Padding around the text in pixels (default 16). |
| `box.cornerRadius` | number | Corner radius in pixels (default 0). |
| `parentId` | string |  |
| `x` | number | Where the anchor lands. Default: canvas center. |
| `y` | number |  |
| `anchorX` | number | Pivot in the text box's pixels. Default: box center. |
| `anchorY` | number |  |
| `rotation` | number |  |
| `scaleX` | number |  |
| `scaleY` | number |  |
| `opacity` | number |  |
| `zIndex` | integer |  |
| `start` | number |  |
| `end` | number |  |
| `style` | string | Library style name (list_library); explicit parameters override it. |
| `language` | string | Language tag (en, ja, my, pt-BR): the layer and its children are only in that language's version. Default: every version. |

### `add_transition`

Add a transition between layerA and layerB, which must be back to back (or already overlapping). crossfade overlaps them by 'duration' using unused source on each side where there is some and ripples B and later layers left for the rest, then fades B in (or A out if A is drawn on top) and crossfades the sound. fadeToBlack (image/video layers) darkens A to black over the last half of 'duration' and brings B up from black over the first half. wipe, slide, zoom and whipPan (image, text or video layers) overlap the clips the same way as crossfade, then: wipe sweeps an edge across the layer drawn on top with a rectangle mask (it must not have a mask yet); slide pushes A out of the frame and B in by the canvas width or height; zoom scales A up 2x (in) or down to half (out) while B comes from half size (or 2x) to its own size, around their centers, and the top one fades; whipPan is a slide with motion blur that peaks in the middle (blur effects are added). Everything is baked into keyframes you can adjust afterwards.

| Parameter | Type | Description |
|---|---|---|
| `layerA` | string, required |  |
| `layerB` | string, required |  |
| `type` | string, default `"crossfade"` | crossfade, fadeToBlack, wipe, slide, zoom or whipPan. Default crossfade. |
| `duration` | number, default `0.5` | Transition length, seconds. Default 0.5. |
| `direction` | string | wipe, slide, whipPan: which way the edge or the pictures move, left, right, up or down (default: wipe right, slide and whipPan left). zoom: in (default) or out. |

### `add_video_layer`

Add a video clip from a video asset. The clip plays source seconds trimIn..trimOut at 'speed', starting at timelineStart; it lasts (trimOut - trimIn) / speed on the timeline (video layers have no 'end'). By default it is centered and scaled to fit the canvas. It takes every transform and keyframe like an image layer, plus brightness (-1..1, 0 = unchanged; -1 is black), contrast and saturation (0..4, 1 = unchanged) and an optional crop (cropWidth/cropHeight in source pixels via update_layer; cropX/cropY are animatable). Its sound is mixed into the export unless muted. A clip that runs past the project end extends the project.

| Parameter | Type | Description |
|---|---|---|
| `assetId` | string, required | Video asset id (from import_video). |
| `timelineStart` | number, default `0` | Timeline time where the clip starts, seconds. Default 0. |
| `trimIn` | number, default `0` | Start point inside the source, seconds. Default 0. |
| `trimOut` | number | End point inside the source, seconds. Default: end of the source. |
| `speed` | number, default `1` | Playback speed, 0.1..10. Default 1. |
| `volume` | number, default `1` | Linear gain for the clip's sound, 1 = original. Default 1. |
| `muted` | boolean, default `false` | Drop the clip's sound. |
| `id` | string | Layer id. Default: derived from name or asset. |
| `name` | string |  |
| `parentId` | string | Parent layer id; the clip then moves with the parent. |
| `x` | number | Where the anchor lands. Default: canvas center. |
| `y` | number |  |
| `anchorX` | number | Pivot in the clip's own pixels. Default: frame center. |
| `anchorY` | number |  |
| `rotation` | number |  |
| `scaleX` | number | Default: fit the canvas. |
| `scaleY` | number | Default: fit the canvas. |
| `opacity` | number |  |
| `zIndex` | integer | Draw order (higher is in front). Default: on top. |
| `fadeIn` | number | Sound fade-in, seconds. |
| `fadeOut` | number | Sound fade-out, seconds. |
| `role` | string | What the clip's sound is in the mix: voice (default), music or effects. Music is ducked under voice. |
| `language` | string | Language of the clip's sound (en, ja, my, pt-BR): other languages' versions mute it; its picture is in every version. Default: every version. |

### `analyze_video`

Analyze a video (or audio) asset in the background: scene cuts (ffmpeg scdet), the noise floor and silences, and a word-level transcript (whisper-cli, if WHISPER_PATH/WHISPER_MODEL are set or whisper-cli is on PATH). Results are saved next to the asset as &lt;name&gt;.analysis.json, &lt;name&gt;.transcript.json and &lt;name&gt;.srt. Returns a jobId (analyze_N) to poll with get_job_status. Afterwards use get_transcript, cut_by_transcript and remove_silences mode=transcript. All times in the results are source seconds of the asset.

| Parameter | Type | Description |
|---|---|---|
| `assetId` | string, required | Video or audio asset id. |
| `language` | string, default `"auto"` | Spoken language code for whisper (en, de, ja, ...), or auto. Default auto. |

### `apply_action`

Play a saved action on a rig in the open project (one undo step). Parts are matched by role, so it works on any copy of the rig and on other rigs with the same part names. Replaces the keyframes of the moved properties inside the time the action covers. With loops &gt; 1 the root's offsets carry on from loop to loop (a walk keeps walking).

| Parameter | Type | Description |
|---|---|---|
| `name` | string, required | Action name from list_library. |
| `rigRootId` | string, required | The rig's root layer. |
| `startTime` | number, required | When the action starts, seconds. |
| `speed` | number, default `1` | Playback speed, 0.05-20. 2 = twice as fast. Default 1. |
| `loops` | integer, default `1` | Times to play it back to back, 1-1000. Default 1. |

### `apply_redaction`

Hide tracked faces, plates or objects (one undo step): adds a redaction layer (type redaction) that hides the box of each track on the video frame before the clip is drawn, in every clip that plays the track's footage, so moves, effects, the camera and subject cutouts all show it hidden, and splits, trims and speed changes keep it in step. Styles: pixelate (default), blur, solid (a color). Strength scales with the box: the pixelate block (or blur radius) is the box's long side over 20 at strength 0 down to 4 at 100 (12 at the default 50), never under 4 source pixels, so a small plate is as unreadable as a big face. The fail-safe: every box is padded by paddingPercent; frames the track lost between two detections get a box moved straight between them that grows with each missed frame, so no frame between them is uncovered; the box is held (and grows) for holdFrames before the first detection and after the last, but not across a scene cut that analyze_video found in the footage; and the box's motion is smoothed while always containing the frame's own box. feather adds a soft edge outside the box. Running it again on the same tracks changes their redaction; tracks in another redaction move to the new one. update_layer changes the settings (style, strength, paddingPercent, feather, holdFrames, color, visible); tracks_to_keyframes turns a track's box into keyframes to edit by hand. Check with render_frame.

| Parameter | Type | Description |
|---|---|---|
| `trackIds` | array of strings, required | Track ids to hide (list_layers shows a clip's tracks as tracks=), or ["all"] for every track. |
| `style` | string, default `"pixelate"` | pixelate, blur or solid. Default pixelate. |
| `strength` | number, default `50` | 0-100: pixelate blocks or blur across the box, 20 at 0 down to 4 at 100. Default 50. |
| `paddingPercent` | number, default `15` | Each side of the box grows by this percent of its width or height. Default 15. |
| `feather` | number, default `4` | Soft edge outside the box, in source pixels. Default 4. |
| `holdFrames` | integer, default `15` | Frames the box is held, and grows, before a track's first detection and after its last; a scene cut that analyze_video found ends the hold sooner. Default 15. |
| `color` | string, default `"#000000"` | solid: the fill, #RRGGBB or #RRGGBBAA. Default #000000. |

### `apply_style`

Restyle a text layer, or every text layer under a group (e.g. 'captions'), with a library style (one undo step). Text, position and timing stay; anchors keep their place in the text box.

| Parameter | Type | Description |
|---|---|---|
| `name` | string, required | Style name from list_library. |
| `layerId` | string, required | A text layer or a group of text layers. |

### `attach_to_track`

Keep a text or image layer on a track's box (one undo step), such as a name over a face or a sticker on a sign: x and y keyframes, and scaleX and scaleY with followScale, on every frame where a clip shows the footage the track found the object in, through the clip's placement, parents and crop, the camera, and the layer's own parent. Frames the track lost are filled in straight between the frames around them. The layer's anchor sits at the box's center plus offset (canvas pixels at the first frame it follows); without offset it stays where it is at that frame, so place it first. With followScale (default) the layer and its offset grow and shrink with the box, from their size at that first frame. The keyframes are kept only where needed to stay within 0.5 px of every frame, and replace the layer's own x and y (and scale) keyframes in that time, and its motion path. They are timeline keyframes: after trimming, moving or changing the speed of the clip, run attach_to_track again.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | The text or image layer to move with the track. |
| `trackId` | string, required | The track to follow (from track_object or detect_and_track). |
| `offset` | object | The layer's anchor from the center of the track's box, in canvas pixels at the first frame it follows: x, y. Default: where the layer is at that frame. |
| `offset.x` | number | Right of the box's center, canvas pixels (negative: left). |
| `offset.y` | number | Below the box's center, canvas pixels (negative: above). |
| `followScale` | boolean, default `true` | Scale the layer, and the offset, with the box's size from the first frame it follows. Default true. |

### `audio_levels`

Measure the sound exactly as the export would mix it (fades, music ducked under voice, loudness normalization): integrated loudness (LUFS, ITU-R BS.1770) and sample peak (dBFS) for the whole mix and per section, and flags clipping (samples at full scale), sections more than 10 LU quieter than the whole mix, a whole mix below -24 LUFS with normalization off, and silences of 1s or more in the middle (silence at the very start or end is listed but not flagged). Mixes up to 120s answer directly; longer ones run in the background and return a jobId (audiolevels_N) for get_job_status.

| Parameter | Type | Description |
|---|---|---|
| `sectionSeconds` | number, default `5` | Section length in seconds, 1-60. Default 5. |

### `auto_lip_flap`

Animate a mouth to a voice (one undo step): measures the loudness of every frame of the sound as it plays on the timeline (start and trim respected), gives quiet frames the closed drawing and louder frames the open drawings (listed from slightly to wide open), holds every drawing at least 2 frames so the mouth doesn't flicker, and writes hold keyframes on the mouth's drawing over the time both play, replacing the drawing keyframes there. The drawing before and after stays as it was.

| Parameter | Type | Description |
|---|---|---|
| `mouthLayerId` | string, required | Image layer with the mouth drawings. |
| `audioLayerId` | string, required | Audio layer with the voice, or a video layer with sound. |
| `closedDrawing` | string, required | Drawing name of the closed mouth. |
| `openDrawings` | array of strings, required | Open drawing names, 1-8, from slightly to wide open. |
| `sensitivity` | number, default `0.5` | 0-1: higher opens the mouth on quieter sounds. Default 0.5. |

### `build_from_script`

Build a whole episode from a script and open it as a new project: the canvas or a library template, then each scene's rigs (shown for that scene), actions, sound effects and captions at their times, and a scene marker at each scene's start. The script is JSON or Markdown in fixed line patterns (docs/GUIDE.md has both); times inside a scene are seconds from its start. Anything it can't map (an unknown rig, action or sound, a time past its scene, a line it doesn't understand) is listed in the reply and the rest is built. The build is one undo step on top of the empty project.

| Parameter | Type | Description |
|---|---|---|
| `scriptFile` | string | Absolute path of the script: .json, or .md (Markdown). Give this or script. |
| `script` | string | The script's text instead of a file: JSON if it starts with '{', Markdown otherwise. |
| `name` | string | Project name, also the folder name. Default: the script's title. |
| `directory` | string | Optional absolute folder for the project inside the projects root, instead of &lt;projectsRoot&gt;/&lt;name&gt;. |

### `camera_shake`

Shake the camera (one undo step): writes seeded keyframes on the camera's shakeX, shakeY and shakeRotation channels, on top of its own movement, fading out to still at the end. The same seed always gives the same shake; change the seed for a different one. Replaces earlier shake keys inside the time range. Remove a shake with clear_keyframes layerId "camera" property shakeX/shakeY/shakeRotation, or undo.

| Parameter | Type | Description |
|---|---|---|
| `startTime` | number, required | When the shake starts, seconds. |
| `duration` | number, required | How long it lasts, seconds (max 60). |
| `intensity` | number, default `20` | Strongest offset in screen pixels at the start (it fades out), max 1000. Rotation shakes 0.05° per pixel. Default 20. |
| `frequency` | number, default `12` | Direction changes per second, 0.5-60. Default 12. |
| `seed` | integer, default `1` | Random seed. Default 1. |

### `check_project`

Check the open project for problems before export: assets whose files are missing, layers that are never on the canvas while shown, text reaching into the 5% title-safe margin (on vertical 9:16 canvases: the Shorts player UI, top 8%, bottom 25%, right 12%), characters a text's font has no glyphs for (each text is shaped with HarfBuzz, as it is drawn), stretches where nothing but the background shows, keyframes outside a layer's time that never play, and assets no layer uses. Read-only; every frame is checked.

No parameters.

### `clear_keyframes`

Remove all keyframes of one property, or of every property of a layer when property is omitted (that also takes a motion path off). The static values then apply again. layerId "camera" clears the camera (property shakeX, shakeY, shakeRotation removes a camera_shake). On a redaction layer, property is a track id: its keyframed box goes, and the box worked out from the track applies again.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Layer id, or "camera". |
| `property` | string | x, y, rotation, scaleX, scaleY, opacity, brightness, contrast, saturation, cropX, cropY, drawing, or an effect or mask property such as shadow.x or mask.width; for the camera x, y, zoom, rotation, shakeX, shakeY or shakeRotation; for a redaction a track id. Omit for all. |

### `create_project`

Create a new project folder (project.json + assets/ renders/ exports/) and open it. Width and height must be even. preset youtube_1080p (1920x1080) or shorts_1080p (1080x1920 vertical) sets the canvas and frame rate. Exports are loudness-normalized to -14 LUFS with music ducked under voice unless changed with update_project.

| Parameter | Type | Description |
|---|---|---|
| `name` | string, required | Project name. Also used as the folder name under the projects root. |
| `width` | integer | Width in pixels (even). Default 1920, or the preset's. |
| `height` | integer | Height in pixels (even). Default 1080, or the preset's. |
| `fps` | integer | Frames per second. Default 30, or the preset's. |
| `duration` | number, default `10` | Duration in seconds. Default 10. |
| `backgroundColor` | string, default `"#FFFFFF"` | Background color #RRGGBB or #RRGGBBAA. Default #FFFFFF. |
| `directory` | string | Optional absolute folder for the project inside the projects root, instead of &lt;projectsRoot&gt;/&lt;name&gt;. |
| `preset` | string | youtube_1080p or shorts_1080p. |

### `cut_by_transcript`

Remove words from a video or audio layer by their transcript indices (the [n] numbers from get_transcript), with ripple. Each range cuts from the first word's start to the next kept word's start, so the pause after the removed words goes too; a quiet sliver under 0.3s left at the clip's edge goes as well. Word timing comes from whisper, fitted to the measured silences, and can still be off by a few tenths of a second; check the result with render_frame or view_source.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Video or audio layer that plays the words. |
| `wordRanges` | array of objects, required | Word index ranges to remove: [{from, to}] (inclusive). |
| `wordRanges[].from` | integer | Index of the first word to remove (the [n] numbers in get_transcript). |
| `wordRanges[].to` | integer | Index of the last word to remove (inclusive). Same as 'from' for a single word. |
| `dryRun` | boolean, default `false` | Only report the planned cuts; change nothing. |

### `detect_and_track`

Find every face or license plate in a video clip with an AI model and link each into a track (face_1, face_2, ... or plate_1, plate_2, ...) to hide with apply_redaction. It runs as a detect_N job (poll get_job_status) that searches every frame from startTime to endTime (default: the whole clip) at up to 1920 px. Faces: YuNet (230 KB, MIT), also searched at a third and an eighth of that size, so faces from about 10 px across to the whole frame are found. Plates: open-image-models' YOLOv9-t (8 MB; MIT code, the weights' license isn't stated), which sees the frame at 640 px, so each corner of it is searched too (2x2 overlapping tiles), finding plates down to about 2% of the frame's width (40 px in 1080p). The model is downloaded on first use. Boxes are linked from frame to frame like ByteTrack: confident detections (minConfidence, default 0.6 for faces, 0.3 for plates) start and continue tracks, weaker ones (down to half of it) only continue a track found on the frame before, and a track ends after 1 s without one. Nothing identifies a person or reads a plate: someone or a car gone for longer comes back as a new track. Detections on fewer than 3 frames are left out and listed. When it is done, the tracks are added as one undo step, saved in the clip's own frames and pixels like track_object's, and get_job_status shows a contact sheet with one labeled picture per track and lists each track's span, frames found and missed, and average size. Before starting it estimates the time; a job over 5 minutes isn't started without confirmLongJob=true. render_frame debug=true draws each track's box.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | The video layer (clip) to search. |
| `target` | string, required | What to find: faces or plates. |
| `startTime` | number | Timeline time in seconds to start at. Default: where the clip starts. |
| `endTime` | number | Timeline time in seconds to stop at. Default: where the clip ends. |
| `minConfidence` | number | How sure the model must be to start a track: above 0, at most 1. Default 0.6 for faces, 0.3 for plates. Lower finds more, and more false ones; detections down to half of it keep a track going. |
| `confirmLongJob` | boolean, default `false` | Start even if it would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first. |

### `detect_beats`

Find the beats of a music (or other audio or video) asset: onsets from the spectral flux of the sound, the tempo from their autocorrelation (60-200 BPM, favoring 120 between octaves), and a steady beat grid fitted to the onsets. Returns the BPM, the beat times, the onsets and how many beats land on an onset; saved next to the asset as &lt;name&gt;.beats.json. Times are source seconds of the asset: a layer starting at s0 with trimIn t0 plays source time t at s0 + t - t0. Sounds up to 120s answer directly; longer ones run in the background and return a jobId (beats_N) for get_job_status.

| Parameter | Type | Description |
|---|---|---|
| `assetId` | string, required | Audio asset id, or a video asset with sound. |

### `duplicate_layer`

Copy a layer, by default with all its children (a whole rig), including keyframes. New ids replace 'replaceFrom' with 'replaceTo' (e.g. tora -&gt; mimi), or get a numeric suffix.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required |  |
| `includeChildren` | boolean, default `true` | Copy descendants too. Default true. |
| `newParentId` | string | Parent for the copy. Omit to keep the original's parent; "" for none. |
| `replaceFrom` | string | Substring of ids/names to replace in the copies. |
| `replaceTo` | string | Replacement for replaceFrom. |
| `zIndexOffset` | integer, default `0` | Added to every copied layer's zIndex. |

### `export_batch`

Export the open project once per preset in one background job, one file after another, to exports/&lt;fileName&gt;_&lt;preset&gt;.mp4 (e.g. launch_youtube_1080p.mp4 and launch_shorts_1080p.mp4). Each file is encoded like start_export with that preset. A preset with another shape than the canvas (shorts_1080p from a landscape canvas, or youtube_1080p from a vertical one) gets the whole canvas fitted in the middle over a blurred, enlarged copy of itself, so nothing is cut off. Returns a jobId (batch_N); get_job_status shows which export is running and lists every finished file.

| Parameter | Type | Description |
|---|---|---|
| `presets` | array of strings, required | Preset names, e.g. ["youtube_1080p", "shorts_1080p"]. |
| `fileName` | string | Base file name, e.g. "launch"; _&lt;preset&gt;.mp4 is added. Default: the project name. |

### `export_fcpxml`

Write the timeline as Final Cut Pro XML (FCPXML 1.9, which DaVinci Resolve also reads) to exports/&lt;fileName&gt;: the first video track as the primary storyline with gaps through to the end, and every other clip connected above it (video, then the layers prerendered by prerender_layers) or below it (the sound the export mixes), with their timing, trims and speed in whole frames, and the markers (chapters as chapter markers). Clips point at the files in the project's assets folder. The reply lists what the timeline can't hold, as export_otio's does.

| Parameter | Type | Description |
|---|---|---|
| `fileName` | string | Output file name, e.g. "edit.fcpxml" (no folders). Default &lt;project&gt;.fcpxml. |

### `export_frames`

Write every frame from startTime to endTime (both included, snapped to frames) as numbered images at the canvas size, exactly as exported, to exports/&lt;project&gt;_frames/frame_&lt;frame&gt;.png, where &lt;frame&gt; is the project frame number in 5 digits (frame_00012.png). Frames an earlier export_frames left in that folder are deleted first, so it holds just this sequence. Runs in the background and returns a jobId (frames_N) for get_job_status.

| Parameter | Type | Description |
|---|---|---|
| `startTime` | number, required | First time in seconds. |
| `endTime` | number, required | Last time in seconds (included). |
| `format` | string, default `"png"` | png (default) or jpg (quality 95, much smaller files). |

### `export_language_versions`

Export the open project once per language and preset in one background job, one file after another, to exports/&lt;fileName&gt;_&lt;language&gt;_&lt;preset&gt;.mp4 (e.g. launch_ja_shorts_1080p.mp4). Each language's version leaves out the layers tagged with other languages (with their children, such as another language's caption track) and mutes the clips whose sound is in another language; untagged layers are in every version. Each file is encoded like export_batch with that preset: a preset with another shape than the canvas gets the whole canvas fitted over a blurred copy of itself. Returns a jobId (versions_N); get_job_status shows which file is being written and lists every finished one. Like export_batch, it starts again if the server stops while it runs.

| Parameter | Type | Description |
|---|---|---|
| `languages` | array of strings, required | Language tags, e.g. ["en", "ja", "my"]. |
| `presets` | array of strings, required | Preset names, e.g. ["youtube_1080p", "shorts_1080p"]. |
| `fileName` | string | Base file name, e.g. "launch"; _&lt;language&gt;_&lt;preset&gt;.mp4 is added. Default: the project name. |

### `export_markers`

Write the chapter markers as a YouTube chapter list to exports/&lt;fileName&gt;, to paste into the video's description: a "m:ss Name" line per chapter ("h:mm:ss" from an hour on), in whole seconds. Scene markers are left out. YouTube shows chapters only when the first is at 0:00, there are at least 3 and each lasts at least 10 s (the last one runs to the project end); the reply warns about each rule the chapters break.

| Parameter | Type | Description |
|---|---|---|
| `fileName` | string | Output file name, e.g. "chapters.txt" (no folders). Default &lt;project&gt;_chapters.txt. |

### `export_otio`

Write the timeline as OpenTimelineIO (.otio) to exports/&lt;fileName&gt;, for another editor such as DaVinci Resolve: each video clip's picture on video tracks (V1 at the bottom, a clip above the clips drawn before it that it overlaps), the layers prerendered by prerender_layers on tracks above them, the sound the export mixes on audio tracks, and the markers. Clips keep their timing, trims and speed and point at the files in the project's assets folder. The reply lists what the timeline can't hold: keyframes, effects, the camera, the place and size of clips that don't fill the frame, text and other layers that weren't prerendered (or changed since), volume, fades, ducking and loudness.

| Parameter | Type | Description |
|---|---|---|
| `fileName` | string | Output file name, e.g. "edit.otio" (no folders). Default &lt;project&gt;.otio. |

### `export_srt`

Write the captions (the text layers under the caption group) as an .srt subtitle file to exports/&lt;fileName&gt;, with timeline times.

| Parameter | Type | Description |
|---|---|---|
| `fileName` | string, required | Output file name, e.g. "talk.srt" (no folders). |
| `groupId` | string, default `"captions"` | Caption group id. Default captions. |

### `export_thumbnail`

Save one frame as a PNG thumbnail in exports/ (default 1280x720, YouTube's thumbnail size) and return a preview of it. Titles and other overlays are ordinary text layers, so add and style them first; a layer that should only be in the thumbnail can start and end around this time. A size with another shape than the canvas is filled by enlarging the frame and cropping the rest evenly from the center. Warns when the PNG is over 2 MB, YouTube's limit.

| Parameter | Type | Description |
|---|---|---|
| `time` | number, required | Time in seconds; snaps to the nearest frame. |
| `width` | integer, default `1280` | Width in pixels (16-7680). Default 1280. |
| `height` | integer, default `720` | Height in pixels (16-7680). Default 720. |
| `fileName` | string | Output file name, e.g. "thumb.png" (no folders). Default thumbnail_&lt;frame&gt;.png. |
| `language` | string | Language version to show (en, ja, my, pt-BR): layers of other languages are left out. Default: every layer. |

### `frame_diff`

Find glitch frames: renders every frame of the range small (160 px wide), scores how much each one changed from the previous one (0-100% mean color difference) and flags 'glitch' (a single frame unlike both neighbors while they match each other, e.g. a stray one-frame layer) and 'spike' (a large change with no layer starting, ending or swapping drawings there). Layer starts and ends, drawing swaps, hold keyframes and analyzed scene cuts are listed as expected cuts. The scores are saved to renders/frame_diff_&lt;first&gt;-&lt;last&gt;.json. Up to 150 frames answers directly; longer ranges run in the background and return a jobId (framediff_N) for get_job_status.

| Parameter | Type | Description |
|---|---|---|
| `startTime` | number, default `0` | First time to compare, seconds. Default 0. |
| `endTime` | number | Last time to compare, seconds. Default: the end of the project. |

### `generate_captions`

Turn the transcript of an analyzed asset (analyze_video) into caption text layers under a null layer 'captions' (one undo step). Word times are mapped through the clips that play the asset, so silences and words cut before this are followed; run it again after later cuts and it replaces the old captions. A cue holds up to maxLines balanced lines of maxWordsPerLine words and ends at a sentence end or a pause. Styles: subtitle (white on a dark box, bottom), bold_center (large bold white with a black outline, center; lower-middle on vertical canvases), minimal (small white with a thin outline, bottom), or the name of a library style (save_style), scaled to the canvas and placed at its captionPosition. Move or hide all captions through the 'captions' layer. behindSubject=on puts the captions behind the person in the clip, as text_behind_subject does: a cutout of their own ('captions_subject', aiVideo or the matte of the clip's text_behind_subject cutout) draws the subject again above them; a clip whose background is removed already just goes in front of them. behindSubject=auto does that too, but wherever the subject would cover more than occlusionThreshold percent of a caption, the captions show in front: the cutout fades out over 0.25 s before such a span and back in after (spans less than 0.5 s apart join); with a clip that is its own subject, those captions draw in front of it. Auto measures every caption frame; up to 600 frames with the matte computed answers directly, otherwise the captions are added at once and the fades follow from a background job (captions_N) as their own undo step.

| Parameter | Type | Description |
|---|---|---|
| `assetId` | string | Asset whose clips get captions (all video/audio layers playing it). Give this or layerId. |
| `layerId` | string | Only this video/audio layer. Give this or assetId. |
| `style` | string, default `"subtitle"` | subtitle, bold_center, minimal or a library style name. Default subtitle. |
| `maxWordsPerLine` | integer, default `6` | Words per line, 1-20. Default 6. |
| `maxLines` | integer, default `2` | Lines per cue, 1-4. Default 2. |
| `fontFamily` | string | Font family. Default Segoe UI; use "Myanmar Text" for Burmese, "Yu Gothic" for Japanese. |
| `fontSize` | number | Font size in pixels. Default: scaled to the canvas per style. |
| `color` | string | Text color. Default #FFFFFF. |
| `groupId` | string, default `"captions"` | Id of the caption group (null layer). Default captions. |
| `behindSubject` | string, default `"off"` | off, on or auto: put the captions behind the subject of the clip (on), and in front where it would cover too much of a caption (auto). Default off; off also takes away the cutout an earlier on/auto made. |
| `occlusionThreshold` | number | behindSubject=auto: how much of a caption, in percent, the subject may cover before the captions show in front. Default 35. |
| `confirmLongJob` | boolean, default `false` | behindSubject: start the subject's AI matte even if it would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first. |

### `get_export_status`

Progress of an export job; when finished, the output path, duration, resolution, fps, codecs and file size.

| Parameter | Type | Description |
|---|---|---|
| `jobId` | string, required | The jobId returned by start_export. |

### `get_job_status`

Progress or result of a background job: import_N (import_video normalization), analyze_N (analyze_video), framediff_N (frame_diff), audiolevels_N (audio_levels), beats_N (detect_beats), batch_N (export_batch), versions_N (export_language_versions), frames_N (export_frames), prerender_N (prerender_layers), matte_N (remove_background, text_behind_subject or generate_captions with aiImage or aiVideo), occlusion_N (text_occlusion), captions_N (generate_captions behindSubject=auto), syscheck_N (system_check), tracking_N (track_object or reanchor_track over more than 150 frames), detect_N (detect_and_track; when done it also shows the contact sheet of the tracks), proxy_N (the 1920 px proxy of footage over 2560 px, made by import_video, import_folder and open_project) or export_N (start_export). A running job's status estimates the time left. Without jobId, lists this session's jobs and the latest ones of earlier sessions. Jobs are kept in the jobs folder for 7 days, so they outlast a restart: an export (export_N, batch_N or versions_N) that was running when the server stopped starts again under its id, up to 3 times; any other job that was running shows as failed, with how far it got, and has to be started again.

| Parameter | Type | Description |
|---|---|---|
| `jobId` | string | The jobId returned by import_video, analyze_video, frame_diff, audio_levels, detect_beats, export_batch, export_language_versions, export_frames, remove_background or start_export. Omit to list all jobs. |

### `get_logs`

Read the server's log, oldest first: the last lastN entries of the log files (zeyocut-yyyyMMdd.jsonl in the log folder, one JSON object per line). Every tool call is logged with its arguments and how long it took, a failed call with its error, and the server logs its start, its settings, jobs started again after a restart, and problems. Use it to find out what happened before, or why something failed.

| Parameter | Type | Description |
|---|---|---|
| `lastN` | integer, default `50` | How many entries to return, 1-1000. Default 50. |
| `level` | string | The lowest level to include: trace, debug, information, warning, error or critical. Default: every level. |
| `contains` | string | Only entries that contain this text, in any case (in the message, the category, a value or the exception). |

### `get_project`

Return the full project model (project.json) of the open project.

No parameters.

### `get_transcript`

Read the word-level transcript of an analyzed asset (run analyze_video first). Each line is a phrase with its source time range; [n] before a word is its index for cut_by_transcript. Pauses of 0.5s or more start a new line. Times are source seconds of the asset, not timeline times.

| Parameter | Type | Description |
|---|---|---|
| `assetId` | string, required | Video or audio asset id. |
| `startTime` | number | Only words from this source time on, seconds. |
| `endTime` | number | Only words up to this source time, seconds. |

### `import_asset`

Copy an image (png/jpg/webp/bmp), audio (wav/mp3/m4a/aac/ogg/opus/flac) or font (ttf/otf/ttc) file into the project's assets folder and register it. Returns the asset id to use in layers. Video files go through import_video.

| Parameter | Type | Description |
|---|---|---|
| `path` | string, required | Absolute path of the file to import, in an import folder, the library or the projects folder. |
| `id` | string | Optional asset id (letters, digits, _ or -). Default: derived from the file name. |

### `import_folder`

Import every image, audio, font and video file in a folder (not its subfolders) whose name matches pattern, in order: by name with numbers in order (panel_2 before panel_10), or by date, oldest first. Images, audio, fonts and videos that need no normalizing are imported at once, as one undo step. Videos that do (variable frame rate, rotated, HDR, ...) are normalized one after another in one background job (import_N) and added as each finishes; poll get_job_status. Asset ids come from the file names. The reply lists the assets in order, the files of other types it skipped and any file it couldn't read. At most 500 files.

| Parameter | Type | Description |
|---|---|---|
| `path` | string, required | Absolute path of the folder, in an import folder, the library or the projects folder. |
| `pattern` | string | File name pattern with \* and ?, in any case, such as \*.png or panel_\*. Default \* (every file). |
| `sortBy` | string | name (default): by file name, numbers in order; date: by when each file last changed, oldest first. |

### `import_srt`

Import a .srt subtitle file as the caption track of its language, as add_caption_track does with srt: text layers under a null layer captions_&lt;language&gt;, replacing that language's track if there is one (one undo step). The language is language, or else the one at the end of the file name (talk.ja.srt, talk_pt-BR.srt). The cue times are timeline times.

| Parameter | Type | Description |
|---|---|---|
| `path` | string, required | Path to the .srt file. |
| `language` | string | The captions' language: en, ja, my, pt-BR. Default: the one at the end of the file name (talk.ja.srt). |
| `style` | string, default `"subtitle"` | subtitle, bold_center, minimal or a library style name. Default subtitle. |
| `fontFamily` | string | Font family. Default: the language's font from the settings, otherwise the style's. |
| `fontSize` | number | Font size in pixels. Default: scaled to the canvas per style. |
| `color` | string | Text color. Default #FFFFFF. |

### `import_video`

Import a video file (mp4/mov/m4v/mkv/webm/avi) as a video asset. A constant-frame-rate, upright, 8-bit file is copied into the project's assets folder and registered at once. Anything else (variable frame rate from phones, rotation metadata, 10-bit or HDR, non-square pixels) is transcoded in the background to H.264 CRF 16 at a constant standard frame rate, upright, 8-bit BT.709, with a keyframe every second: the call then returns a jobId (import_N) to poll with get_job_status, and the asset is added when the job completes, as its own undo step. Footage over 2560 px also gets a 1920 px proxy in a proxy_N job, which previews and object tracking read once it is done (exports read the original).

| Parameter | Type | Description |
|---|---|---|
| `path` | string, required | Absolute path of the video file, in an import folder, the library or the projects folder. |
| `id` | string | Optional asset id (letters, digits, _ or -). Default: derived from the file name. |

### `list_assets`

List the project's assets with their ids, types, sizes or durations, and which layers use them.

No parameters.

### `list_layers`

Show the layers as a parent/child tree with type, zIndex, time range, language, anchor and keyframed properties.

No parameters.

### `list_library`

List what is in the shared library: rigs, actions, styles and templates, with a short description of each.

| Parameter | Type | Description |
|---|---|---|
| `type` | string, default `"all"` | rigs, actions, styles, templates or all. Default all. |

### `list_projects`

List the projects in the projects root folder.

No parameters.

### `list_sfx`

List the sound effects in the shared library's sfx folder (wav, mp3, ogg and other audio files; subfolders are categories), with their durations. Names are paths inside sfx without the extension (impacts/thud); add_sfx takes the full name or, when unique, the file name alone.

| Parameter | Type | Description |
|---|---|---|
| `filter` | string | Only names containing this text (not case-sensitive). |

### `move_layer`

Move a layer to a new timeline start, together with its children and keyframes. The project grows if the layer now runs past the end.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required |  |
| `start` | number, required | New start time, seconds. |

### `new_project_from_template`

Create a new project from a library template and open it. The folder is &lt;projectsRoot&gt;/&lt;name&gt; unless 'directory' is given. With a new duration, layers that ran to the template's end stretch, pinned layers move with the end, and the rest keep their times.

| Parameter | Type | Description |
|---|---|---|
| `template` | string, required | Template name from list_library. |
| `name` | string, required | Project name, also the folder name. |
| `duration` | number | Duration in seconds. Default: the template's. |
| `directory` | string | Optional absolute folder for the project inside the projects root, instead of &lt;projectsRoot&gt;/&lt;name&gt;. |

### `open_project`

Open an existing project by name (a folder under the projects root) or by absolute folder path. Returns the layer tree.

| Parameter | Type | Description |
|---|---|---|
| `nameOrPath` | string, required | Project name from list_projects, or an absolute path to a project folder inside the projects root. |

### `prerender_layers`

Render layers alone, each with its children, on a transparent background as ProRes 4444 .mov files with alpha in exports/&lt;project&gt;_prerender/&lt;layer id&gt;.mov: every frame the layer is on, as render_frame draws it with layerId and a transparent background. They are for an editor that opens the timeline of export_otio or export_fcpxml, which put each prerendered layer on a track above the clips, so its text, keyframed motion, effects and camera move carry over. A layer whose prerender is current (nothing it shows has changed) isn't drawn again. Runs in the background and returns a jobId (prerender_N) for get_job_status.

| Parameter | Type | Description |
|---|---|---|
| `layerIds` | array of strings, required | Layer ids; each is drawn with its children. |

### `preview_server`

Start or stop the preview page, for a browser on this computer: a timeline scrubber, the layers, and playback of low-res frames with the sound mixed as the export mixes it. It shows the open project and follows every edit within a second; it only shows, so edits are made with the tools. It listens on 127.0.0.1 only, at a random port with a secret token in its address, so other sites can't read it. start returns the address.

| Parameter | Type | Description |
|---|---|---|
| `action` | string, default `"start"` | start (default) or stop. |

### `reanchor_track`

Correct a track where it drifted (one undo step): set the box at 'time' (canvas pixels, as render_frame shows that frame) and track again from there, away from the frame track_object started at, up to the next box set by hand or the end of the track. Frames on the other side stay as they are. Time and box are mapped through the clip the track was made on, or layerId (another clip of the same footage, such as a piece split off it). Up to 150 frames answers directly; more run as a tracking_N job.

| Parameter | Type | Description |
|---|---|---|
| `trackId` | string, required | The track to correct (track_object's id). |
| `time` | number, required | Timeline time in seconds of the frame to correct (the nearest frame, as render_frame shows it). |
| `box` | object, required | The object's box in canvas pixels at that time: x, y (top-left corner), width, height. |
| `box.x` | number | Left edge, canvas pixels. |
| `box.y` | number | Top edge, canvas pixels. |
| `box.width` | number | Width, canvas pixels. |
| `box.height` | number | Height, canvas pixels. |
| `layerId` | string | The clip to map time and box through. Default: the clip the track was made on. |

### `redaction_report`

Check how well the redactions hide their tracks; changes nothing. For each track: the redaction that hides it, the frames its box covers, how many come from a detection, how many the fail-safe holds between detections or before and after them, and how many frames the track spans that no box covers; its confidence and box sizes. Then the spans to look at, with their times on the timeline (only parts of the footage some clip plays): frames not covered; confidence under 0.5 (a tracker drifting, or a weak detection); no detection for 0.5 s or more, where the box only moves straight between detections; fast motion (the box moving a quarter of its size or more a frame); boxes under 12 px, where padding adds little; and footage that plays before a track's box starts or after it is let go while the box is inside the frame (more than 2% of the frame's size from every edge), where the face, plate or object may still show uncovered. Tracks no redaction hides are listed too. A redaction works in every clip that plays its footage, so no clip is left out. Look at each span with render_frame (debug=true draws each track's box).

| Parameter | Type | Description |
|---|---|---|
| `trackIds` | array of strings | Track ids to report on, or ["all"]. Default: every track. |

### `redo`

Redo the last undone change.

No parameters.

### `refine_matte`

Fix the edges of a removed background (one undo step): shrink pulls the edge in by that many pixels to cut off a halo of the old background (negative grows it back, whole pixels), feather softens it over about that many pixels. Both are in the layer's own pixels; values you leave out stay.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Layer id with a removed background. |
| `shrink` | number | Pixels to pull the edge in, -100 to 100 (negative grows). Default: unchanged. |
| `feather` | number | Edge softness in pixels, 0-100. Default: unchanged. |

### `reframe_to_vertical`

Fit landscape video layers to a vertical canvas (switch it first with update_project preset=shorts_1080p): each clip gets a full-height crop with the canvas' aspect ratio at focusX, centered and scaled to fill the frame. Zoom differences between pieces (punch-in) are kept. cropX is animatable: keyframe it (set_keyframes, source pixels) to follow the subject.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, default `"all"` | Video layer to reframe, or "all" for every video layer. Default all. |
| `focusX` | number, default `0.5` | Horizontal position of the crop: 0 = left edge, 0.5 = center (default), 1 = right edge. |

### `remove_background`

Remove the background of an image or video layer so what is behind it shows through (one undo step; replaces an earlier removal, keeping refine_matte's shrink and feather). Methods: chroma keys out a screen color while drawing (green or blue screen; images and video): color is the screen's color (default: picked from the picture's edges), tolerance 0-1 how far from it still goes (default 0.4), softness 0-1 the soft edge after that (default 0.2), spill 0-1 how much of the screen's tint is taken out of what stays (default 1). edgeFill removes everything connected to the image's border that is close to the background color (art on a plain background; image layers): color is the background (default: the most common border color), tolerance 0-1 (default 0.1); areas of that color enclosed by the art stay. aiImage finds the subject of a photo or drawing with an AI model (BiRefNet; image layers); aiVideo follows a person through a clip with an AI model (Robust Video Matting; video layers). Both take no settings and are computed in the background as a matte_N job (the first use downloads the model, 224 MB or 15 MB); the removal applies at once, but until the job is done the layer is drawn with its background and exports refuse. Mattes are cached next to the asset. Check the edges with render_frame checkerboard=true layerId=&lt;layer&gt;, and fix halos with refine_matte. remove=true takes the removal off.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Image or video layer id. |
| `method` | string | chroma, edgeFill, aiImage or aiVideo. Required unless remove=true. |
| `color` | string | chroma: the screen color; edgeFill: the background color. #RRGGBB. Default: picked from the picture. |
| `tolerance` | number | 0-1. Default 0.4 (chroma), 0.1 (edgeFill). |
| `softness` | number | chroma: 0-1, how soft the edge is. Default 0.2. |
| `spill` | number | chroma: 0-1, how much screen tint to take out of the kept pixels. Default 1. |
| `remove` | boolean, default `false` | Take the background removal off. |
| `confirmLongJob` | boolean, default `false` | aiImage or aiVideo: start even if it would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first. |

### `remove_keyframes`

Remove specific keyframes (all-or-nothing).

| Parameter | Type | Description |
|---|---|---|
| `entries` | array of objects, required | Keyframes to remove: [{layerId, property, time}]. |
| `entries[].layerId` | string | Layer id, or "camera". |
| `entries[].property` | string | x, y, rotation, scaleX, scaleY, opacity, brightness, contrast, saturation, cropX, cropY, drawing, or an effect or mask property such as shadow.x or mask.width; for the camera x, y, zoom, rotation, shakeX, shakeY or shakeRotation. |
| `entries[].time` | number | Time of the keyframe to remove, in seconds. |

### `remove_layer`

Remove a layer. A layer with children is only removed together with its children when withChildren is true.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required |  |
| `withChildren` | boolean, default `false` | Also remove all descendants. |

### `remove_markers`

Remove project markers (one undo step): the ones named in markerIds, every marker with markerIds ["all"], or every marker of one kind with kind.

| Parameter | Type | Description |
|---|---|---|
| `markerIds` | array of strings | Marker ids, or ["all"]. |
| `kind` | string | chapter or scene: removes every marker of this kind (with markerIds omitted or ["all"]). |

### `remove_range`

Cut timeline seconds start..end out of one layer (and its children) or out of every layer (layerId "all"). With ripple (default) everything after the cut moves left, keyframes and children included, and the project gets shorter by the removed length; without ripple a gap is left.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string | Layer to cut, or "all" for every layer. Default "all". |
| `start` | number, default `0` | Start of the range, timeline seconds. |
| `end` | number, default `0` | End of the range, timeline seconds. |
| `ripple` | boolean, default `true` | Close the gap. Default true. |

### `remove_silences`

Cut the silent parts out of a video or audio layer with ripple (later layers move left and the project gets shorter), keeping 'padding' seconds of quiet next to the remaining sound. mode=audio measures the clip's noise floor first and treats sound below noise floor + 6 dB as silence (ffmpeg silencedetect; override with thresholdDb). mode=transcript uses the gaps between spoken words instead (needs analyze_video), which works for clips with background music. dryRun=true only reports the planned cuts. punchIn=true zooms every second piece to punchInZoom to hide the jump cuts. Pieces are named clip, clip_2, clip_3, ... in time order.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Video or audio layer. |
| `minSilence` | number, default `0.7` | Shortest pause to cut, seconds. Default 0.7. |
| `padding` | number, default `0.1` | Quiet kept before and after each remaining sound, seconds. Default 0.1. |
| `mode` | string, default `"audio"` | audio (silencedetect on the sound) or transcript (gaps between words). Default audio. |
| `thresholdDb` | number | mode=audio: silence threshold in dBFS instead of the measured noise floor + 6 dB, e.g. -40. |
| `dryRun` | boolean, default `false` | Only report the planned cuts and the new duration; change nothing. |
| `punchIn` | boolean, default `false` | Zoom every second piece to hide the jump cuts. |
| `punchInZoom` | number, default `1.1` | Zoom of the punched-in pieces. Default 1.1. |

### `render_contact_sheet`

Render several evenly spaced frames into one labeled grid image to check motion over time in a single call.

| Parameter | Type | Description |
|---|---|---|
| `startTime` | number, default `0` | First time in seconds. Default 0. |
| `endTime` | number | Last time in seconds. Default: the last frame. |
| `count` | integer, default `12` | Number of frames (1-64). Default 12. |
| `columns` | integer, default `4` | Grid columns. Default 4. |
| `maxWidth` | integer, default `1600` | Width of the whole sheet. Default 1600. |
| `debug` | boolean, default `false` | Draw anchors, outlines and layer names, and the box of each track on a clip, in every cell. |
| `language` | string | Language version to show (en, ja, my, pt-BR): layers of other languages are left out. Default: every layer. |

### `render_frame`

Render one frame exactly as it will appear in the export and return it as a PNG. The time snaps to the nearest frame. The full-resolution frame is also saved in renders/. debug=true draws anchors, outlines, parent bones and layer ids. checkerboard=true shows transparent parts as gray squares instead of the background color, and layerId draws only that layer and its children: together they show the edges of a removed background (remove_background) without the layers behind it.

| Parameter | Type | Description |
|---|---|---|
| `time` | number, required | Time in seconds. |
| `maxWidth` | integer, default `960` | Maximum width of the returned image. Default 960. |
| `debug` | boolean, default `false` | Draw anchors, outlines and layer names, and the box of each track on a clip. |
| `checkerboard` | boolean, default `false` | Show transparency as a gray checkerboard instead of the background color. |
| `layerId` | string | Draw only this layer and its children (or only a subject cutout). |
| `language` | string | Language version to show (en, ja, my, pt-BR): layers of other languages are left out. Default: every layer. |

### `report_render_stats`

Where a finished export's time went: per stage (evaluating keyframes, decoding video and images, redaction, background removal and cutouts, text, effects and masks, drawing layers, fitting the canvas into a preset, waiting for the encoder, measuring the loudness), the slowest layers and the slowest frames; times under 5 ms are left out. jobId is a start_export (export_N), export_batch (batch_N) or export_language_versions (versions_N) job; default: the newest finished one. Read-only; use it to find what makes an export slow.

| Parameter | Type | Description |
|---|---|---|
| `jobId` | string | export_N, batch_N or versions_N. Default: the newest finished export. |

### `restore_backup`

List the open project's backups, newest first, or put one back. Before every save the project.json being replaced is copied to the project's backups folder, named after the edit saved over it; the last 20 are kept. When open_project updates a file from an older ZeyoCut, the file as it was is kept there too, for good. Backups hold project.json only, not the assets. Restoring is one undo step, and what it replaces is backed up as well.

| Parameter | Type | Description |
|---|---|---|
| `backup` | string | A backup's name from the list; its time (e.g. 20261104-161502-123) is enough. Omit to list them. |

### `save_action`

Save a rig's animation between two times to the library as a reusable action (walk cycle, jump, wing flap, double-take). Keyframes are stored by part role and relative to startTime; the root's x, y and rotation as offsets and its scale as a factor, so the action plays wherever the rig stands. Both window edges are sampled, so the action starts and ends exactly as the rig looks there. Not an undo step.

| Parameter | Type | Description |
|---|---|---|
| `rigRootId` | string, required | The rig's root layer. |
| `startTime` | number, required | Window start, seconds. |
| `endTime` | number, required | Window end, seconds. For a cycle, end on the pose the cycle starts with. |
| `name` | string, required | Library name: letters, digits, spaces, '_' or '-'. |
| `overwrite` | boolean, default `false` | Replace an action of the same name. |

### `save_rig`

Save a character rig to the shared library (used by every project): the root layer and everything parented under it, with anchors, drawings, parenting and draw order, posed as it is at 'time'. Each part gets a role that save_action/apply_action match by: its rigRole if set (update_layer {"rigRole": "arm_l"}), otherwise its id without the prefix the part ids share (tora_head → head); the root is always 'root'. Saving doesn't change the project, so it isn't an undo step.

| Parameter | Type | Description |
|---|---|---|
| `rootLayerId` | string, required | The rig's root layer (often a null layer). |
| `name` | string, required | Library name: letters, digits, spaces, '_' or '-' (not case-sensitive). |
| `time` | number | Pose to save, seconds. Default: the root's start. |
| `overwrite` | boolean, default `false` | Replace a rig of the same name. |

### `save_style`

Save a text layer's look (font, bold, size, color, outline, alignment, line height, background box) as a named style in the library. Sizes are stored relative to the canvas, so the style looks the same on other canvas sizes. Use it with apply_style, add_text_layer style or generate_captions style. Not an undo step.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | A text layer. |
| `name` | string, required | Library name: letters, digits, spaces, '_' or '-'. |
| `captionPosition` | string, default `"bottom"` | Where generate_captions puts captions in this style: bottom or center. Default bottom. |
| `overwrite` | boolean, default `false` | Replace a style of the same name. |

### `save_template`

Save layers of the open project (intro, outro, end screen, watermark, fixed layers) with the canvas, frame rate and audio settings as a template in the library, with their asset files. Layers that run to the project's end stretch to a new project's length; pinToEnd layers (an end screen) keep their distance from the end; others keep their times. Not an undo step.

| Parameter | Type | Description |
|---|---|---|
| `name` | string, required | Library name: letters, digits, spaces, '_' or '-'. |
| `layerIds` | array of strings | Layers to keep (each with its children). Default: all layers. |
| `pinToEnd` | array of strings | Layers (with their children) that move with the end of the video. |
| `overwrite` | boolean, default `false` | Replace a template of the same name. |

### `set_camera`

Set the project camera's fixed values (one undo step). The camera moves the whole scene: x, y is the scene point shown at the center of the frame (default: the canvas center), zoom 2 shows half as much (twice as close), rotation turns the camera clockwise so the picture turns the other way. Layers with ignoreCamera (captions get it) stay put. To move the camera over time use set_keyframes with layerId "camera" (properties x, y, zoom, rotation). reset=true removes the camera with all its keyframes and shake.

| Parameter | Type | Description |
|---|---|---|
| `x` | number | Scene x shown at the frame's center, pixels. |
| `y` | number | Scene y shown at the frame's center, pixels. |
| `zoom` | number | 0.01-100; 1 shows the whole canvas, 2 is twice as close. |
| `rotation` | number | Degrees, clockwise camera roll. |
| `reset` | boolean, default `false` | Remove the camera (and its keyframes) first. |

### `set_effect`

Add, change or remove one effect on a visual layer (one undo step). The effect is on that layer only, not its children. shadow (color, x, y, blur, opacity; default #000000, 8, 8, 6, 0.5): a soft copy behind the layer, offset right/down. outline (color, width; default #FFFFFF, 4): a border around the layer's shape. blur (x, y; default 4, 4): softens the layer, x and y separately (x alone gives a horizontal motion blur). tint (color, amount; color required): mixes the layer toward a color, amount 1 paints it solid in it. Sizes are in pixels at the layer's scale and grow with it and the camera; blur sizes are the Gaussian's standard deviation. Numbers are animatable with set_keyframes as shadow.x, shadow.y, shadow.blur, shadow.opacity, outline.width, blur.x, blur.y, tint.amount; colors are fixed. Fields you leave out keep their value. remove=true removes the effect with its keyframes.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Layer id. |
| `effect` | string, required | shadow, outline, blur or tint. |
| `color` | string | #RRGGBB or #RRGGBBAA (shadow, outline, tint). |
| `x` | number | shadow: offset right in pixels; blur: horizontal blur. |
| `y` | number | shadow: offset down in pixels; blur: vertical blur. |
| `blur` | number | shadow: softness, 0-500 (0 = hard edge). |
| `opacity` | number | shadow: 0-1. |
| `width` | number | outline: thickness in pixels, 0-100. |
| `amount` | number | tint: 0-1 (1 = solid color). |
| `remove` | boolean, default `false` | Remove the effect. |

### `set_keyframes`

Add or replace many keyframes in one call (one undo step; all-or-nothing). A keyframe at the same layer/property/time is replaced. Each keyframe's easing shapes the segment from it to the next keyframe. Before the first and after the last keyframe a property holds that keyframe's value; once a property has keyframes its static value is ignored. layerId "camera" animates the project camera (x, y, zoom, rotation), which moves every layer except those with ignoreCamera. Effect and mask numbers (shadow.x, blur.x, mask.width, ...) animate after set_effect / set_mask added them. x or y keyframes take a set_motion_path path off the layer. On a redaction layer the properties are its keyframed boxes from tracks_to_keyframes (&lt;track&gt;.x, .y, .width, .height), with time in the footage's own seconds.

| Parameter | Type | Description |
|---|---|---|
| `entries` | array of objects, required | Keyframes: [{layerId, property, time, value, easing}]. |
| `entries[].layerId` | string | Layer id, or "camera" for the project camera. |
| `entries[].property` | string | x, y, rotation, scaleX, scaleY, opacity; brightness, contrast, saturation (image and video layers); cropX, cropY (video layers); drawing (image layers); shadow.x, shadow.y, shadow.blur, shadow.opacity, outline.width, blur.x, blur.y, tint.amount (after set_effect); mask.x, mask.y, mask.width, mask.height, mask.feather (after set_mask). Camera: x, y (the scene point at the frame's center), zoom, rotation, shakeX, shakeY, shakeRotation. |
| `entries[].time` | number | Time in seconds. |
| `entries[].value` | any | A number, or a drawing name (string) for the drawing property. |
| `entries[].easing` | string | Easing of the segment that STARTS at this keyframe: linear (default), hold, easeIn, easeOut, easeInOut, easeOutBack, easeOutBounce, easeOutElastic. Ignored for drawing (always hold). |

### `set_mask`

Add, change or remove the layer's mask (one undo step): the layer shows only inside a rectangle, an ellipse or an image's opaque pixels, or only outside with invert=true. The box (x, y = top-left corner, width, height) is in the layer's own pixels, like its anchor, so it moves, turns and scales with the layer; a new mask fits the layer's content unless you give it. An image mask stretches the image asset's alpha over the box. feather softens the edge over about that many pixels. x, y, width, height and feather are animatable with set_keyframes as mask.x, mask.y, mask.width, mask.height, mask.feather (a growing width is a wipe). Fields you leave out keep their value. remove=true removes the mask.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Layer id. |
| `type` | string | rectangle, ellipse or image. Required for a new mask. |
| `asset` | string | Image asset id for type image. |
| `x` | number | Left edge of the box, layer pixels. |
| `y` | number | Top edge of the box, layer pixels. |
| `width` | number | Box width, layer pixels. |
| `height` | number | Box height, layer pixels. |
| `feather` | number | Edge softness in pixels, 0-1000. Default 0. |
| `invert` | boolean | Show the layer outside the shape instead. |
| `remove` | boolean, default `false` | Remove the mask. |

### `set_motion_path`

Move a layer along a curve through timed points (one undo step; replaces an earlier path). The layer's anchor reaches each point at its time, in the same pixel space as its x, y (the canvas for top-level layers). Each segment is a cubic bezier: inX/inY and outX/outY are a point's handles relative to the point; handles you leave out are set for a smooth curve (one handle alone is mirrored). Inside a segment the layer moves at an even speed along the curve, shaped by the easing of the point the segment starts at. autoOrient=true turns the layer with the path on top of its rotation (draw it facing right). The path replaces x and y: their keyframes are cleared, and setting x or y keyframes (or x/y with update_layer) later removes the path. Ripple edits, splits and speed changes retime it like other keyframes. remove=true takes the path off.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Layer id. |
| `points` | array of objects | At least 2 points, in time order: [{time, x, y, inX, inY, outX, outY, easing}]. |
| `points[].time` | number | When the layer reaches this point, seconds. |
| `points[].x` | number | Point x, pixels (the parent's space, the canvas for top-level layers). |
| `points[].y` | number | Point y, pixels. |
| `points[].inX` | number | Incoming handle x, relative to the point. Optional. |
| `points[].inY` | number | Incoming handle y, relative to the point. Optional. |
| `points[].outX` | number | Outgoing handle x, relative to the point. Optional. |
| `points[].outY` | number | Outgoing handle y, relative to the point. Optional. |
| `points[].easing` | string | Easing of the segment that starts at this point: linear (default), hold, easeIn, easeOut, easeInOut, easeOutBack, easeOutBounce, easeOutElastic. |
| `autoOrient` | boolean, default `false` | Turn the layer along the path's direction. Default false. |
| `remove` | boolean, default `false` | Remove the path. |

### `set_speed`

Change a video layer's playback speed (0.1..10) keeping its start; its keyframes and children stretch with it. With ripple (default) the layers after it move to stay back to back and the project duration changes by the same amount.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required |  |
| `speed` | number, required | New speed, 0.1..10. |
| `ripple` | boolean, default `true` | Move later layers. Default true. |

### `split_layer`

Split a layer (and its children) at a timeline time into two layers that play exactly as before. The left part keeps the id; the right part gets the next free id like 'clip_2'. Keyframes are trimmed to each part and children follow the part that is showing when they start.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Layer to split. |
| `time` | number, required | Timeline time of the split, seconds; must be inside the layer. |

### `start_export`

Start exporting the open project to exports/&lt;fileName&gt; as H.264 MP4 (yuv420p) with all audio layers mixed: music ducked under voice and the mix loudness-normalized (two-pass loudnorm, -14 LUFS by default; see update_project). Runs in the background and returns a jobId; poll get_export_status. Later edits don't affect a running export. preset youtube_1080p or shorts_1080p encodes for upload (H.264 high profile, 2 B-frames, keyframe every half second) at the preset size; the canvas must have the preset's aspect ratio.

| Parameter | Type | Description |
|---|---|---|
| `fileName` | string, required | Output file name, e.g. "jump.mp4" (no folders). |
| `crf` | integer | Quality 0-51, lower is better. Default 18 (visually lossless). |
| `preset` | string | youtube_1080p or shorts_1080p. |
| `language` | string | Language version to export (en, ja, my, pt-BR): layers of other languages are left out and clips whose sound is in another language are muted. Default: every layer. |

### `system_check`

Report this computer: whether FFmpeg (needed for video and every export) and whisper (for transcripts) are installed, with how to install them; the folders ZeyoCut uses; and for the AI features the CPU, RAM, GPU, the ONNX Runtime version, which AI models are downloaded and the device each one runs on (DirectML on the GPU, or the CPU). Then it benchmarks every downloaded model on the CPU and on DirectML, and 1080p decoding, in the background (syscheck_N, usually under a minute): the result gives each AI feature's time per minute of 1080p video (per image for aiImage). The results are saved (library\benchmark.json): AI jobs estimate their time from them, and AiDevice=auto (the default) runs a model on DirectML only where it worked and took at most 80% of the CPU's time. DirectML is tried in a separate process, stopped after 60 s, so a GPU driver that hangs can't hang the server. Run it again after installing models or changing hardware.

No parameters.

### `text_behind_subject`

Put text behind the subject of a clip or picture, like a title behind a presenter (one undo step): the text stays in front of the background, and the subject is drawn again above it, cut out. For a video layer this adds a subject cutout layer (type cutout) above the texts, cut out with aiVideo by default (or chroma for a green screen); for an image layer with aiImage by default (or edgeFill or chroma); the methods work as in remove_background. The cutout always follows its source and the pieces splits made of it on the same track: timing, trims, speed, transforms, effects and keyframes carry over, and its own settings are only name, zIndex, visible and opacity (keyframes too). If the subject is already a layer of its own (a removed background, a transparent picture, or a group of layers), no cutout is needed and the texts move directly below it. Texts drawn below the clip are lifted just above it. AI mattes are computed in the background as a matte_N job; until then the text shows in front and exports refuse. Running it again on the same clip reuses its cutout (with a new method if you give one). Check with render_frame (layerId=&lt;cutout&gt; shows the subject alone); text_occlusion measures how much of a text is covered.

| Parameter | Type | Description |
|---|---|---|
| `videoLayerId` | string, required | The video (or image) layer with the subject in it, or a group whose layers are the subject. |
| `textLayerIds` | array of strings, required | Text layers to put behind the subject; a group (like captions) stands for the text layers under it. |
| `method` | string | How the cutout finds the subject: aiVideo (default for video), aiImage (default for images), chroma or edgeFill. Default for an existing cutout: unchanged. |
| `color` | string | chroma: the screen color; edgeFill: the background color. #RRGGBB. Default: picked from the picture. |
| `tolerance` | number | chroma or edgeFill: 0-1. Default 0.4 (chroma), 0.1 (edgeFill). |
| `confirmLongJob` | boolean, default `false` | AI methods: start even if computing the matte would take more than 5 minutes. Without it, such a job isn't started and the reply gives the estimate; tell the user first. |

### `text_occlusion`

Measure how much of a text layer is covered by what is drawn above it (a subject cutout from text_behind_subject, or any other layer), frame by frame: the min, max and average share of the text hidden, and the frames in runs of equal values (every frame is saved to renders/text_occlusion_&lt;id&gt;_&lt;first&gt;-&lt;last&gt;.json). A half-transparent layer counts half; frames where the text isn't shown are left out of the numbers. Default range: the text's own time. Up to 150 frames answers directly; longer ranges run in the background and return a jobId (occlusion_N) for get_job_status.

| Parameter | Type | Description |
|---|---|---|
| `textLayerId` | string, required | Text layer id. |
| `startTime` | number | First time to measure, seconds. Default: the text's start. |
| `endTime` | number | End of the range (not included), seconds. Default: the text's end. |

### `track_object`

Follow an object through a video clip with OpenCV and save its box on every frame as a track (one undo step). Draw the box around the object in canvas pixels at 'time', as render_frame shows that frame; it is mapped into the clip's own frames and pixels (through its placement, parents, the camera and its crop), so the track keeps fitting after trims, speed changes and splits, and every clip of the same footage shares it. direction forward tracks to the end of the clip's trimmed range, backward to its start, both both ways. Methods: csrt (default; OpenCV's CSRT tracker: follows changes of shape and size, which can wobble a little), template (finds the starting picture near the last box: fast and a fixed size, for things that don't turn or grow), opticalFlow (moves the box with the points inside it and scales it with their spread: for textured things). Every frame gets a confidence (0-1: how well the starting picture still matches in the box); the reply lists spans under 0.5 as possible drift, and where the tracker loses the object it holds the box before. Look at those frames with render_frame debug=true, which draws each track's box, and correct the box with reanchor_track. Up to 150 frames answers directly; longer runs are a tracking_N job (poll get_job_status) that adds the track as its own undo step. list_layers shows each clip's tracks.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | The video layer (clip) the object is in. |
| `time` | number, required | Timeline time in seconds of the frame the box is drawn on (the nearest frame, as render_frame shows it). |
| `box` | object, required | The object's box in canvas pixels at that time: x, y (top-left corner), width, height. |
| `box.x` | number | Left edge, canvas pixels. |
| `box.y` | number | Top edge, canvas pixels. |
| `box.width` | number | Width, canvas pixels. |
| `box.height` | number | Height, canvas pixels. |
| `direction` | string, default `"forward"` | forward (to the end of the clip), backward (to its start) or both. Default forward. |
| `method` | string, default `"csrt"` | csrt, template or opticalFlow. Default csrt. |
| `id` | string | Track id. Default track_N. |

### `tracks_to_keyframes`

Turn a hidden track's box into keyframes on its redaction (one undo step), to edit by hand: x, y, width and height keyframes named &lt;track&gt;.x, &lt;track&gt;.y, &lt;track&gt;.width and &lt;track&gt;.height on the redaction layer, made from the fail-safe box (padding, gaps, holds and smoothing included) and kept only where needed to stay within 0.2 px of it. The box is 0.5 px bigger on each side, so on every frame it contains the box it was made from. The keyframes are in the footage's own seconds and pixels, like the track, so the box keeps following trims, speed changes and splits; the redaction then hides that box from its first keyframe to its last, and paddingPercent and holdFrames no longer change it. Edit it with set_keyframes and remove_keyframes (time in the footage's seconds); clear_keyframes with property "&lt;track&gt;" goes back to the box worked out from the track. Running it again remakes the keyframes from the track.

| Parameter | Type | Description |
|---|---|---|
| `trackId` | string, required | The track whose box to turn into keyframes; apply_redaction must hide it first. |

### `undo`

Undo the last change to the open project (in-memory history of this session, up to 200 steps).

No parameters.

### `update_layer`

Change static fields of a layer. 'changes' is an object of field: value, using the field names from get_project, e.g. {"x": 640, "parentId": "tora_body", "anchorX": 120, "end": 2.5, "text": "Hi", "box": {"padding": 20}}. For animatable properties a number sets the static value (used only when the property has no keyframes). Use parentId "" to unparent. language (en, ja, my, pt-BR) puts the layer and its children in that language's version only; on a video it is the language of its sound; "" puts it back in every version. Nested objects are merged; null removes a nested key. A redaction's own fields are name, visible, style, strength, paddingPercent, feather, holdFrames and color.

| Parameter | Type | Description |
|---|---|---|
| `layerId` | string, required | Layer id. |
| `changes` | any, required | Fields to change. |

### `update_project`

Change the open project's settings. Only the parameters you pass change. Keyframe times are in seconds, so changing fps keeps the timing; changing the canvas size does not move layers (for landscape footage on a new vertical canvas use reframe_to_vertical). Extend the duration when a voiceover or music runs past the end; the result warns about audio that would be cut and keyframes or layers past a shorter end. Audio: loudnessTarget (LUFS, two-pass loudnorm at export; 0 = off) and ducking (music under voice).

| Parameter | Type | Description |
|---|---|---|
| `name` | string | New display name (the folder is not renamed). |
| `width` | integer | Canvas width in pixels (even, 16-8192). |
| `height` | integer | Canvas height in pixels (even, 16-8192). |
| `fps` | integer | Frames per second (1-120). |
| `duration` | number | Duration in seconds. |
| `backgroundColor` | string | Background color #RRGGBB or #RRGGBBAA. |
| `preset` | string | youtube_1080p (1920x1080) or shorts_1080p (1080x1920): sets width, height, fps and loudness. |
| `loudnessTarget` | number | Export loudness in LUFS, -40..-5 (-14 for YouTube and most platforms); 0 turns normalization off. |
| `ducking` | boolean | Duck music (role music) under voice (role voice) in the export. |

### `view_source`

Contact sheet of a video asset's own frames (not the timeline), each labeled with its source time and frame number, to find trim and cut points before or after adding the clip.

| Parameter | Type | Description |
|---|---|---|
| `assetId` | string, required | Video asset id. |
| `startTime` | number, default `0` | First source time, seconds. Default 0. |
| `endTime` | number | Last source time, seconds. Default: the end of the clip. |
| `count` | integer, default `12` | Number of frames (1-64). Default 12. |
| `columns` | integer, default `4` | Grid columns. Default 4. |
| `maxWidth` | integer, default `1600` | Width of the whole sheet. Default 1600. |

## Workflow prompts

A client shows these as ready-made requests, such as slash commands. Each fills in a step-by-step plan for the model from its arguments.

### `comic_panels`

Turn a folder of comic panels into a video: the camera moves from panel to panel, timed to a voiceover, with captions.

| Argument | Required | Description |
|---|---|---|
| `folder` | yes | Absolute path of the folder with the panels. |
| `voiceover` | no | Absolute path of the voiceover. Optional. |
| `name` | no | Project name. Default: the folder's name. |

### `new_episode`

Start a new animated episode from the library: a template, the scenes as markers, then the rigs, actions, sounds and titles of each scene.

| Argument | Required | Description |
|---|---|---|
| `name` | yes | Project name for the episode. |
| `template` | no | Library template to start from. Default: ask the user. |
| `script` | no | Absolute path of an episode script (.json or .md) for build_from_script. Optional. |

### `privacy_blur`

Find the faces and license plates in a video, let the user choose which to hide, hide them and check every frame before export.

| Argument | Required | Description |
|---|---|---|
| `video` | yes | Absolute path of the video. |
| `target` | no | What to find: faces, plates or both. Default both. |
| `name` | no | Project name. Default: the video's file name. |

### `talking_head_cleanup`

Clean up a talking-head video: cut the pauses with punch-in zooms, add captions, check the loudness and export 16:9 and 9:16.

| Argument | Required | Description |
|---|---|---|
| `video` | yes | Absolute path of the video. |
| `name` | no | Project name. Default: the video's file name. |
| `language` | no | Spoken language code for the transcript (en, ja, my, ...). Default auto. |
