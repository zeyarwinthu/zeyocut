# Project format

A ZeyoCut project is a folder. `project.json` in it is the single source of truth: every tool call validates the change, saves it atomically and adds an undo step. Before each save, the file it replaces is copied to `backups\`, and the last 20 are kept; `restore_backup` lists them and puts one back.

This page describes the folder, the file, and how ZeyoCut reads what is in it: space and time, transforms, the camera, animation, text, video, captions, languages, the library and audio. The [guide](GUIDE.md) shows the tools that change it.

## The folder

```text
my-project\
  project.json     the model
  assets\          imported images, audio, video and fonts (layers refer to them by asset id),
                   plus analysis results next to each analyzed file:
                   <name>.analysis.json, <name>.transcript.json, <name>.srt,
                   and <name>.beats.json from detect_beats
  renders\         PNGs from render_frame / render_contact_sheet / view_source
  exports\         MP4s, SRTs, chapter lists, .otio and .fcpxml timelines, and in
                   <project>_prerender\ the ProRes files of prerender_layers (prerender.json lists them)
  tracks\          the boxes of each track (track_object), one file per version
  backups\         project.json before each of the last 20 saves (project-<time> before <edit>.json),
                   and as an older ZeyoCut saved it (project-v<format>-<time>.json)
  cache\chunks\    the 2 s chunks of earlier exports, reused by the next export (at most 2 GB)
  cache\preview\   the sound mix the preview page plays (mix-<key>.m4a, the latest one)
  proxies\         1920 px copies of footage over 2560 px, for previews and tracking (exports read the originals)
```

## project.json

`project.json` is indented UTF-8 JSON. Names are camelCase, and so are enum values (`easeOutBack`, `voice`). It's meant to be readable and to sit well in version control. Change it through the tools, which check every edit; if you edit it by hand, `open_project` lists the problems it finds.

Here is the demo project (`ZeyoCut.Mcp.exe demo`) cut down to one asset and one layer:

```json
{
  "formatVersion": 6,
  "name": "Tora Jumps",
  "width": 1280,
  "height": 720,
  "fps": 30,
  "duration": 3,
  "backgroundColor": "#BFE6FF",
  "audio": {"loudnessTarget": -14, "truePeak": -1, "ducking": true},
  "assets": [
    {
      "id": "clouds",
      "type": "image",
      "file": "assets/clouds.png",
      "width": 1400,
      "height": 240
    }
  ],
  "layers": [
    {
      "type": "image",
      "id": "clouds",
      "name": "clouds",
      "start": 0,
      "end": null,
      "zIndex": 1,
      "parentId": null,
      "visible": true,
      "anchorX": 0,
      "anchorY": 0,
      "flipX": false,
      "drawings": {"normal": "clouds"},
      "drawing": {"value": "normal"},
      "x": {
        "value": 0,
        "keyframes": [
          {"time": 0, "value": 0, "easing": "linear"},
          {"time": 3, "value": -80, "easing": "linear"}
        ]
      },
      "y": {"value": 0},
      "rotation": {"value": 0},
      "scaleX": {"value": 1},
      "scaleY": {"value": 1},
      "opacity": {"value": 1},
      "brightness": {"value": 0},
      "contrast": {"value": 1},
      "saturation": {"value": 1}
    }
  ]
}
```

### Top level

| Field | Default | What it is |
|---|---|---|
| `formatVersion` | 6 | The format the file was saved in; see [Format versions](#format-versions) |
| `name` | | The project's name |
| `width`, `height` | 1920, 1080 | The canvas in pixels |
| `fps` | 30 | Frames per second |
| `duration` | 10 | Length in seconds |
| `backgroundColor` | `#FFFFFF` | Shown where no layer draws |
| `audio` | | `loudnessTarget`: the loudness of the exported mix in LUFS, -14 by default, `null` for off. `truePeak`: the highest true peak, -1 dBTP. `ducking`: lower music under voice, on by default |
| `camera` | | Written once `set_camera`, a camera keyframe or `camera_shake` makes one: animated `x`, `y`, `zoom` and `rotation`, and the shake channels `shakeX`, `shakeY` and `shakeRotation` |
| `assets` | `[]` | The files the project uses |
| `layers` | `[]` | Everything on the timeline, as one flat list; `parentId` makes the hierarchy |
| `tracks` | | Boxes followed through footage; written only when there are some |
| `markers` | | Chapters and scenes in time order; written only when there are some |

### Assets

Each asset has an `id`, which layers refer to it by, a `type` (`image`, `audio`, `font` or `video`) and a `file`. The file is always `assets/<file name>`; one that points anywhere else is an error. The rest is what was found when the file was imported: `width` and `height`, `duration`, `frameRate`, `frameCount`, `hasAudio`, `codec` and, for fonts, `fontFamily`. A video that was transcoded on import keeps the file the user imported in `originalFile` and the reason in `normalized`.

### Layers

Every layer has these fields:

| Field | What it is |
|---|---|
| `type` | `image`, `text`, `video`, `audio`, `cutout` or `redaction` |
| `id` | Unique in the project; tools refer to layers by it |
| `name` | A display name |
| `rigRole` | The part's name in a rig (`head`, `arm_l`); actions match parts by it |
| `start`, `end` | Where the layer plays, in seconds; `end` `null` plays to the end of the project |
| `zIndex` | Draw order across all layers; higher draws in front |
| `parentId` | The layer this one moves with |
| `visible` | Hidden layers aren't drawn or heard |
| `language` | Puts the layer and its children in one language's version only; see [Languages](#languages) |

Image, text and video layers add the anchor (`anchorX`, `anchorY`), `ignoreCamera`, and the animated `x`, `y`, `rotation`, `scaleX`, `scaleY` and `opacity`. They can have a `motionPath`, a `mask`, and the effects `tint`, `outline`, `shadow` and `blur`. Each type then has fields of its own:

| Type | Its own fields |
|---|---|
| `image` | `drawings` (drawing name → image asset id), `drawing` (which drawing shows, animated), `flipX`, the animated color adjustments `brightness`, `contrast` and `saturation`, and `matte` (background removal). An image layer without drawings is a null layer |
| `text` | `text`, `fontFamily` or `fontAsset`, `fontSize`, `bold`, `color`, `strokeColor`, `strokeWidth`, `align`, `maxWidth`, `lineHeight`, and `box` (`color`, `padding`, `cornerRadius`) |
| `video` | `asset`, `trimIn`, `trimOut`, `speed`, `volume`, `muted`, `role`, `fadeIn`, `fadeOut`, a crop (`cropWidth`, `cropHeight` and the animated `cropX`, `cropY`), the color adjustments, and `matte` |
| `audio` | `asset`, `trimIn`, `trimOut`, `volume`, `role` (`voice`, `music` or `effects`), `fadeIn` and `fadeOut` |
| `cutout` | Made by `text_behind_subject`: the subject of its `sources` (a clip and the pieces splits made of it) drawn again above the text, cut out with its `matte`. It follows its sources, so only `name`, `zIndex`, `visible` and `opacity` are its own. `captionGroup` names the captions `generate_captions` made it for |
| `redaction` | Made by `apply_redaction`: hides the boxes of its `tracks` with a `style` (`pixelate`, `blur` or `solid`), `strength`, `paddingPercent`, `feather`, `holdFrames` and `color`. After `tracks_to_keyframes`, `boxes` holds a track's box as keyframes |

### Animated values

A property that can be animated is an object with a `value` and a list of `keyframes`:

```json
"x": {
  "value": 0,
  "keyframes": [
    { "time": 0, "value": 0, "easing": "linear" },
    { "time": 3, "value": -80, "easing": "linear" }
  ]
}
```

`value` is used while there are no keyframes. A keyframe's `easing` shapes the segment that starts at it; see [Animation](#animation). An image layer's `drawing` works the same way with drawing names, and always holds: `"drawing": { "value": "normal", "keyframes": [{ "time": 1.2, "value": "happy" }] }`.

### Tracks

A track is a box followed through a video asset by `track_object`, or found on every frame by `detect_and_track`. It is kept in the asset's own frames and pixels, so trimming, speeding up or splitting the clips that play it never breaks it.

| Field | What it is |
|---|---|
| `id`, `asset` | The track and the video asset it follows |
| `layer` | The clip the box was drawn on |
| `method` | `csrt`, `template` or `opticalFlow` for a followed box; `faces` or `plates` for detections |
| `direction`, `origin`, `firstFrame`, `lastFrame` | Which way tracking ran from the frame it started at, and the frames it covers |
| `anchors` | The boxes set by hand (`frame`, `x`, `y`, `width`, `height`), in frame order |
| `file` | `tracks/<id>-<hash>.json`: the box on every frame, with how sure the tracker was. Every change writes a new file, so undo brings back the old one |

### Markers

A marker has an `id`, a `time`, a `name` and a `kind`, `chapter` or `scene`. `export_markers` writes the chapters as a YouTube chapter list.

### Format versions

| Version | What it added |
|---|---|
| 1 | The first format |
| 2 | Video clips, sound roles and loudness settings |
| 3 | The camera, effects, masks and motion paths |
| 4 | Subject cutouts |
| 5 | Tracks and redactions |
| 6 | Markers and language tags |

A project saved by an older ZeyoCut opens too: it's updated to the current format as it opens, and the file as it was is kept in `backups\` for good. So far every version has only added fields, and a field an older file lacks gets its default.

## Space and time
- Pixels, origin at the top-left, y points down, rotation in degrees clockwise.
- Times are in seconds. Frame *n* is rendered at *t = n / fps*. The frame count is `round(duration × fps)`.

## Transforms
- Each visual layer has an anchor (`anchorX`, `anchorY`) in its own pixels. `x`, `y` is where that anchor lands in the parent's pixel space, or on the canvas for top-level layers.
- `local = translate(x,y) · rotate(rotation) · scale(scaleX,scaleY) · translate(−anchor)`, and `world = parentWorld · local`.
- Children inherit the transform, and opacity and visibility multiply down the chain. A parent's `start`/`end` also limits its children.
- Parent cycles are rejected.

## Camera
- One camera per project moves the whole scene: `x`, `y` is the scene point at the frame's center, `zoom` 2 is twice as close, and `rotation` turns the camera clockwise.
- `screen = translate(canvasCenter + shake) · rotate(−rotation) · scale(zoom) · translate(−x,−y) · world`, applied to every top-level layer except those with `ignoreCamera` (captions get it).
- Animate it with `set_keyframes` on `layerId` `"camera"`. `camera_shake` adds seeded shake on separate channels, so the same seed always gives the same frames.

## Draw order and layer types
- Draw order is `zIndex` across *all* layers; it doesn't follow the parent hierarchy. Higher draws in front, and ties keep layer order. To put a tail behind its body, give it a lower `zIndex`; negative values are fine.
- An image layer without drawings is a **null layer**. It is invisible and only carries a transform, which makes it a good rig root.
- `flipX` mirrors a layer's own pixels around `anchorX`. Its children are not flipped.

## Animation
- Animatable properties are `x`, `y`, `rotation`, `scaleX`, `scaleY` and `opacity`, plus `drawing` on image layers, `brightness`, `contrast` and `saturation` on image and video layers, and `cropX`/`cropY` on cropped video layers.
- A keyframe's easing shapes the segment that *starts* at that keyframe: `linear`, `hold`, `easeIn`, `easeOut`, `easeInOut`, `easeOutBack`, `easeOutBounce`, `easeOutElastic`.
- Before the first keyframe and after the last, a property holds that keyframe's value. Once a property has keyframes, its static value is ignored.
- `drawing` always holds, and before its first keyframe it shows the first keyframe's drawing. Add a keyframe at 0 for the starting drawing.

## Effects, masks and motion paths

See [the guide](GUIDE.md#effects-and-motion).

- `set_effect` gives one layer, not its children, a drop shadow, outline, blur or tint. Sizes are pixels at the layer's scale and directions are on screen, so a shadow keeps its look when the layer or the camera zooms. The numbers animate as `shadow.x`, `shadow.y`, `shadow.blur`, `shadow.opacity`, `outline.width`, `blur.x`, `blur.y` and `tint.amount`.
- `set_mask` shows a layer only inside, or only outside, a rectangle, an ellipse or an image's alpha. The box is in the layer's own pixels and has a soft `feather` edge; `mask.x`, `mask.y`, `mask.width`, `mask.height` and `mask.feather` animate.
- A layer with effects or a mask is drawn into a layer of its own: content, mask, tint, outline, shadow, blur, and then its opacity.
- `set_motion_path` moves a layer's anchor through timed points on cubic bezier curves, smooth where no handles are given. It moves at an even speed along each segment, eased per point, and can turn with the path (`autoOrient`). The path replaces `x` and `y`; `x` or `y` keyframes later take it off.

## Text

- One font per text layer, with no automatic fallback. Use `fontFamily` for an installed font, or `fontAsset` for an imported `.ttf`/`.otf`/`.ttc`. If the font lacks glyphs for the text, every tool that touches the layer warns and names a font that covers it: "Myanmar Text" for Burmese, "Yu Gothic" for Japanese.
- Shaping uses HarfBuzz, which handles Burmese stacking and reordering (kinzi, medials, pre-base vowels) and Japanese.
- `maxWidth` wraps text:
    - Breaks go at spaces first.
    - Only a phrase too wide for a line is split further: Burmese at syllable boundaries, Japanese per character with kinsoku rules (no 。、」 at a line start, no 「 at a line end).
    - A single grapheme never splits.
    - `\n` always starts a new line.
- `lineHeight` is a multiple of the **font's natural line spacing**, not of `fontSize`. The default is 1.0, which keeps Burmese stacked marks from colliding.
- `align` (left/center/right) aligns lines within the text box. The box is `maxWidth` wide when set, otherwise as wide as the longest line.
- `bold` uses the font's bold face, or synthetic emboldening if it has none.
- `strokeColor`/`strokeWidth` draw an outline of `strokeWidth` pixels outside the glyphs.
- `box` draws a background (`color`, `padding`, `cornerRadius`) behind the text's ink.

## Video

- `import_video` copies clean files as they are. It transcodes anything else in the background: variable frame rate, rotation, 10-bit or HDR, or non-square pixels. The output is constant frame rate, upright, 8-bit BT.709 H.264, so frame *n* of a clip is always at *n / fps*.
- A video layer plays `trimIn`–`trimOut` of its source at `speed`. Its timeline length is `(trimOut − trimIn) / speed`.
- `render_frame` seeks to the exact frame. Export decodes each clip once, in order, with one decoder per clip.
- Footage over 2560 px (4K and up) gets a 1920 px proxy in the background when it is imported, and when a project that lacks one opens. Previews and object tracking read it; exports read the original. On a 20 s 4K clip in a 1080p project, the proxy took 2.5 s to make, and `render_frame` then took 220–270 ms instead of 425–440 ms, a 12-frame contact sheet 1.7 s instead of 4.8 s. A preview drawn from a proxy says so.
- Previews are cached in memory (up to 128 MB per open project) under the same per-frame key as the export's chunks: a frame or contact sheet cell shown before comes back without drawing while nothing it shows has changed, so an edit at 12 s leaves the previews at 2 s cached. On a 1080p clip, `render_frame` took 312 ms the first time and 1 ms again; a 12-frame contact sheet took 2.3 s, then 44 ms, and 129 ms after an edit that changed 7 of its cells.
- `preview_server` starts a read-only page for the browser at `http://127.0.0.1:<port>/<token>/`. It shows a timeline with the layers and markers, and plays frames of up to 640 px, about 15 a second, with the export's sound mix. It follows every edit within a second and draws again only the frames the edit changed. Only that address answers, and other sites can't read it. On a 1080p clip, the first play showed 11 frames a second and the second the full 15.
- The editing tools (`split_layer`, `remove_range`, `remove_silences`, `cut_by_transcript`, …) cut clips into pieces that play exactly as before. They ripple later layers, keyframes and children along, so a cut never changes a frame it doesn't touch.
- `add_transition` bakes crossfade, fade-to-black, wipe, slide, zoom and whip-pan transitions into keyframes, masks and blur effects that you can adjust afterwards.
- `reframe_to_vertical` crops landscape clips to the vertical canvas; `cropX` can be keyframed to follow the subject.

## Captions

`generate_captions` builds text layers from the transcript under a `captions` null layer, timed through the clips that play the asset. Cuts made *before* generating are followed; for cuts made after, run it again and it replaces the old captions. With `behindSubject` they go behind the person in the clip; `auto` brings them in front, with a short fade, wherever the person would cover too much of a caption. `export_srt` writes the same cues as subtitles.

## Languages

A layer's `language` (`en`, `ja`, `my`, `pt-BR`) puts it and its children in that language's version only; untagged layers are in every version. A video's tag is the language of its sound: its picture stays in every version, and the other versions mute it.

- `add_caption_track` adds one language's captions as a track of their own (`captions_ja`), from cues such as a translation, an SRT file or a transcript. `import_srt` does the same with a `.srt` file, and takes the language from a name like `talk.ja.srt`.
- `render_frame`, `render_contact_sheet`, `start_export` and `export_thumbnail` take `language` to show one version. `export_language_versions` writes `<name>_<language>_<preset>.mp4` for every language and preset in one job.
- A text with a language and no font gets the language's font (`LanguageFonts`), and `check_project` shapes every text with HarfBuzz to find characters its font can't show.

See [the guide](GUIDE.md#languages).

## Library

The library is a folder shared by all projects (see [the guide](GUIDE.md#library)):

- **Rigs** (`save_rig`, `add_rig`): a layer and everything parented under it, with drawings, anchors and draw order. Each copy gets its own ids (`tora_head`, `mimi_head`).
- **Actions** (`save_action`, `apply_action`): a rig's keyframes over a time window, by part role rather than layer id. The root moves relative to where the rig stands, so the same walk cycle plays on any copy of the rig, at any speed and for any number of loops.
- **Styles** (`save_style`, `apply_style`): a text look, scaled to the canvas, usable by `add_text_layer` and `generate_captions`.
- **Templates** (`save_template`, `new_project_from_template`): fixed layers such as an intro, watermark or end screen. Full-length layers stretch to the new duration, and pinned layers keep their distance from the end.
- **Sound effects** (`list_sfx`, `add_sfx`): your own audio files in `sfx\`, with subfolders as categories. `add_sfx` imports a sound once per project and places it with role `effects`.
- **Episode scripts** (`build_from_script`): a JSON or Markdown script of scenes, each with rigs, actions, sound effects and captions at their times, built into a new project in one call. What it can't map is listed and the rest is built.

Saving to the library doesn't change the project and isn't an undo step. Placing or applying an item is one undo step.

## Audio

Audio layers and the sound of video layers are mixed at export, and for the preview page. Each one has:

- `start` / `end`: placement on the timeline
- `trimIn` / `trimOut`: the range used from the source file
- `volume`: linear gain
- `fadeIn` / `fadeOut`: fade lengths in seconds
- `role`: `voice`, `music` or `effects`. Audio layers default to `music`, video sound to `voice`.

At export:

- **Ducking.** Music is lowered under voice by a sidechain compressor, roughly 12–18 dB while someone speaks, depending on how loud the voice is. It comes back within about a second after they stop.
- **Loudness.** The mix is normalized with a two-pass `loudnorm` to the project's target, -14 LUFS by default. The first pass measures the mix; the second applies the gain linearly, with true peak at most -1 dBTP. Set the target with `update_project` (`loudnessTarget`, `0` = off).

The mix is 48 kHz stereo AAC at 192 kbps (128 kbps for the preview page). Hidden audio layers are skipped.

Animating to sound (see [the guide](GUIDE.md#audio-for-animation)):

- **`detect_beats`** finds the onsets of a music asset from its spectral flux (a hand-written FFT, no extra library) and the tempo from how they repeat. It fits a steady beat grid to the loudest hits, so kicks win over off-beat hi-hats. The BPM, beats and onsets are saved as `<name>.beats.json`, in source seconds.
- **`auto_lip_flap`** measures the loudness of every frame of a voice as it plays on the timeline. It gives quiet frames the closed mouth drawing and shares the open drawings evenly by loudness among the rest. Every drawing holds at least 2 frames, and the result is written as `drawing` keyframes in one undo step.
