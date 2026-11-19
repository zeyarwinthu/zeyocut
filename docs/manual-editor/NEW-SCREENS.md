# Editor new screens: plan

- Written 2026-11-18 on `feature/manual-editor` at `88295e8`, after the redesign of the existing screens (REDESIGN.md, phases A–H) was done.
- Status: S1–S22 decided as recommended (2026-11-18). Phases 1, 2, 3 and 4 done (2026-11-18), phases 5, 6, 7 and 8 (2026-11-19): every planned phase is done.
- Covers the 15 artboards of the design canvas "ZeyoCut Editor — modern redesign" that have no screen yet, in the order agreed on 2026-11-18. Each phase is one step of that order and ends with a report and a stop.
- The rules in PLAN.md still hold: no control without real project data and real render output behind it; the UI has no editing logic of its own; a new project property is saved, rendered on export and reachable over MCP; tests pass at every commit; stop and report after each phase. Logic that lives in an MCP tool today moves into Core first, in a commit of its own with no change in behavior (as D9 did for the SRT writer).

## Scope and order

| Phase | Design artboards | What it needs besides the screen | Size |
|---|---|---|---|
| 1 | 17 Keyboard shortcuts, 9 New project, 18 Relink missing media, 19 Welcome & connect Claude | Project creation moved into Core; a relink operation and `relink_asset` tool; a connection file the server writes (S5) | Medium |
| 2 | 3 Captions (multi-language) | `add_caption_track`'s logic moved into Core; a cue operation; a preview language | Medium |
| 3 | 7 Keyframes & curve editor ("Animate") | A keyframe move operation; a cubic-bezier easing (S9) | Large |
| 4 | 6 Audio mixer ("Audio") | Per-role mix settings, volume keyframes and ducking settings in the project and the mix (S10–S12) | Large |
| 5 | 10 Media browser, 11 Source viewer | Asset tags (S14); insert and overwrite edits (S15) | Large |
| 6 | 13 Effects & transitions, 14 Title templates | A transition operation; styles and templates moved into Core; text animation presets (S18) | Medium |
| 7 | 8 Track & blur | Tracking moved into Core and Render for the editor; ellipse shape and smoothing (S19) | Medium |
| 8 | 15 Render queue, 16 Preferences | The editor's own export queue (S20); preferences in the editor's settings | Medium |
| 9 | 12 Color | New color controls in the format, renderer and MCP (S22) | Large |

### Getting to the screens

- **Workspace tabs.** The design's header has Edit, Audio, Animate, Track & blur, Captions and Activity, with Export as a button. Each tab comes with the phase that builds its screen, so there's never a tab without one. The keys follow S1.
- **Inside Edit.** Media browser opens from a button on the media panel (phase 5). Source viewer opens by double-clicking a video or sound, or from the browser (phase 5). Effects and Titles open from an icon rail on the media panel: Media, Titles, Effects (phase 6). Each is a view with "Back to edit", like Export.
- **From Home.** New project from a "New project" card and the Landscape, Shorts and Square cards (phase 1). Preferences from a settings button (phase 8). The Home sidebar stays out (R4): its Templates and Asset library would need screens that work without an open project.
- **Anywhere.** Shortcuts with `?` (phase 1), Relink when a project with missing files opens (phase 1), Welcome on first start (phase 1), Render queue from Export and Preferences (phase 8).

## Screen by screen

"Do" means build it in that phase. Items left out are listed with the reason in the next section.

### 17 · Keyboard shortcuts (phase 1)

| Area | Design | Plan |
|---|---|---|
| Opening | `?` anywhere; "Change shortcuts in Preferences" | Do `?` (and F1) outside text fields. Changing keys left out |
| List | Groups Playback, Editing, Timeline, Source viewer, Animate, Workspaces, with a search box | Do, listing only keys the editor has. The list and the key handling read one table, so they can't disagree. Source viewer and Animate groups come with their phases |
| Keys the editor lacks | J/K/L, Ctrl+L loop selection, Shift+Del ripple delete, Ctrl+D duplicate, Ctrl+Shift+Z, N snap | S2 |

### 9 · New project (phase 1)

| Area | Design | Plan |
|---|---|---|
| Name | Text box | Do; the folder name is made from it as `create_project` makes it |
| Start from | Blank project | Do Blank, and library templates as `new_project_from_template` makes them (its logic moves into Core) |
| Canvas | Presets YouTube, HD 720p, Shorts, Square, Portrait 4:5, Custom; width × height | Do; sizes must be even, as `create_project` checks |
| Frame rate | 24, 25, 30, 60 | Do |
| Background | Black, white, sky blue, transparent | Do (transparent is `#00000000`, which the format already takes) |
| Save in | Projects folder with "Change" | Show it; Change is S4 |
| Preview card | Canvas shape, "Fits YouTube and the youtube_1080p export preset" | Do: the note comes from the export presets that match the size and frame rate |
| Create | Opens the project | Do, through Core's project creation, moved out of `create_project` first |

### 18 · Relink missing media (phase 1)

| Area | Design | Plan |
|---|---|---|
| When | Opening a project whose files are gone | Do, and from a tile marked Missing in the media panel |
| Each file | Name, where it was, "used by 1 clip" | Do: where it was is its place in `assets\`, where every asset lives |
| Likely match | A file with the same name elsewhere, "Same size 1400×240" | Do as S3 says |
| Search another folder…, Choose file… | Pickers | Do; a file of another kind is refused |
| Keep placeholder | Leave it missing | Do (as now, a missing file is marked and exports are refused) |
| "Copy the file into the project's media folder" | Checkbox | Left out: ZeyoCut always copies into `assets\`, as imports do |
| Open without relinking, Relink and open | Buttons | Do, through a new `RelinkAssetOperation` (copies the file into `assets\` and updates the asset's size or length) and a `relink_asset` tool that does the same |

### 19 · Welcome & connect Claude (phase 1)

| Area | Design | Plan |
|---|---|---|
| When | First start | Do: when there's no editor settings file yet; later from Preferences › Claude & MCP |
| 1 · Projects folder | Path with a check | Show the folder the editor and server use; changing it is S4 |
| 2 · Connect Claude | Steps, `claude_desktop_config.json` snippet, Copy, "Waiting for Claude…" | Do the steps and the snippet with the real path of `ZeyoCut.Mcp.exe` next to the editor (and the bundle as the easier way). The live check is S5 |
| 3 · Sample project | "tora-jump, ready to tweak" | S6 |
| Try asking Claude | Two example prompts | Do |
| Skip setup, I'll do this later, Back, Continue | Buttons | Do |

### 3 · Captions (phase 2)

| Area | Design | Plan |
|---|---|---|
| Tab | Captions | Do (S1) |
| Tracks | "EN · source", "JA 3/3", "MY 3/3", Add language | Do the track chips with how many rows each language fills. Add language left out: the hint to ask Claude stays |
| Grid | #, start and end, one text column per language | Do, rows as S7 says. A text cell saves to its cue; a time cell moves the cues in that row in every language, one undo step |
| Add caption at playhead | Button | Do: a cue in every track, styled like its neighbours, through a new Core operation (over `add_caption_track`'s logic, moved into Core) |
| Import .srt / .vtt | Button | Do .srt, as `import_srt` reads it; .vtt left out |
| Download .srt | Button | Do: writes to `exports\` as `export_srt` does, with Show in folder |
| Preview language | EN / JA / MY | Do: the preview (here and in Edit) draws one language's version, as exports do. It's an editor setting, never in the project. This also settles REDESIGN.md's note that the preview draws every language at once |
| Style · all captions | Font per language, size, position Top / Middle / Bottom, background box / outline / none, max line | Do: font per track; size, position and background for every track as one undo step; position places the tracks inside the title-safe margin. Max line is S8 |

### 7 · Keyframes & curve editor, "Animate" (phase 3)

| Area | Design | Plan |
|---|---|---|
| Tab | Animate | Do (S1) |
| Preview | Onion skin "3 before / 2 after", ghosts tinted | Do: the selected layer drawn alone at the frames around the playhead, faded, with counts to choose; editor only |
| Motion path | The path the layer takes | Do: drawn from its x and y keyframes (or its motion path). Dragging the path left out |
| Easing | Linear, Ease in, Ease out, In-out, Back, Bounce, Elastic, Hold; "Apply to selection" | Do: the eight are Core's eight easings (Back, Bounce and Elastic are its ease-out kinds) |
| Custom bezier | x1, y1, x2, y2, Save as preset | S9; Save as preset left out |
| Dope sheet | Keys per property in rows | Do, with drawing changes (a head's drawings) as their own row. Dragging keys retimes them through a new `MoveKeyframesOperation` (value and easing kept; a key landing on another is refused) |
| Graph | Value curves, Linear / Smooth / Hold, Fit curves | Do: drawn from Core's interpolation; dragging a key changes its value and time; Linear / Smooth / Hold set the selected keys' easing |
| Property tree | Layers and their animated properties with values at the playhead | Do |

### 6 · Audio mixer, "Audio" (phase 4)

| Area | Design | Plan |
|---|---|---|
| Tab | Audio | Do (S1) |
| Strips | Video 1, Video 2, Music, SFX, Master | S10: one strip per sound role (Voice holds videos' own sound, as now), and Master. Master has no fader, pan, M or S: the mix has no master level besides the loudness target |
| Effect slots | Noise reduction, EQ, Ducking, Limiter, Reverb, Loudness −14, True-peak limit, + Effect | Show the real ones: Ducking on Music, Loudness and True-peak limit on Master. The others and "+ Effect" left out |
| Pan, fader, M, S | Per strip | S10 |
| Meters | Per strip | S13: Master only, the peaks of the preview's mix while it plays |
| Loudness | Target, integrated LUFS, true peak, "1.8 dB quieter than the target", Normalize | Do the target (the project's `audio.loudnessTarget`) and a Measure button that runs `audio_levels`. Normalize left out: exports are always normalized to the target, and the card says so. The second tile is the sample peak in dBFS, which `audio_levels` measures, not the true peak |
| Music track | Auto-duck under speech, Duck by −12 dB, fade in, fade out | Do the switch (the project's `audio.ducking`); the amount and fades are S12 |
| Volume automation | Points on a line per clip, ducked stretches shaded | S11; the shading comes from where voice plays, as the mix ducks. Waveforms are drawn for audio files; a video's sound has none yet |

### 10 · Media browser (phase 5)

| Area | Design | Plan |
|---|---|---|
| Opening | Full view of the project's media | Do, from the media panel |
| Library | All media, Video, Images, Audio, PSD layers | Do All and each kind (Fonts too). PSD layers left out |
| Your bins | Tora rig, Backgrounds, SFX, New bin | S14 |
| Missing | "1 file missing · Relink" | Do, opening Relink |
| Grid | Thumbnail, kind, size or length, name, "used 2×" or "missing" | Do: "used" counts this project's clips |
| Search, Sort | Name or tag; Recently added | Do; recently added is the order assets were imported. Sort also has oldest first and by name |
| Details | Kind, size, file size, location, "Used in tora-jump, comic-panels", "Added Today 18:24 by Claude", tags | Do kind, size, file size, location and this project's uses. Other projects' uses and who added it left out |
| Actions | Add at playhead, Open in source viewer, Show in folder | Do |

### 11 · Source viewer (phase 5)

| Area | Design | Plan |
|---|---|---|
| Source monitor | The file's own frames, play, timecode | Do: the file alone as a one-clip project at its own size and frame rate, drawn by the renderer and played with its sound by a preview and mix of its own |
| In and out | Mark in (I), Mark out (O), clear (Alt+X), length | Do; they become the clip's trim in and out |
| Tracks to use | Video, Audio | Do for footage: Audio off makes the clip with its sound off. The Video switch left out: a video's sound alone can't be a sound clip, so footage always brings its picture |
| Insert (,), Overwrite (.) | At the playhead, "Where it will go · Video 1" | S15. Insert also splits the lane's clip under the playhead, so its second part moves on with the rest. The lane is named as the timeline names it ("Mixed" for a level of footage and pictures) and can be chosen |
| Program monitor | The edit at the playhead | Do |

### 13 · Effects & transitions (phase 6)

| Area | Design | Plan |
|---|---|---|
| Categories | Transitions, Video effects, Text animations, Audio effects, Stickers, Favorites, Recently used | Do Transitions and Video effects (blur, shadow, border, tint, as the inspector sets them); Text animations as S18. The rest left out |
| Transitions | 12 tiles | Do Core's six: Crossfade, Dip to black, Wipe, Slide, Zoom, Whip pan. The other six left out. Tiles are S16 |
| A cut | Selected on the timeline | Do: clicking where two clips meet on a lane selects the cut. It's a click without a drag on either clip's edge (a drag still trims), marked in the accent color; a strip under the tiles shows the lane's cuts, and clicking a mark selects it |
| Settings | Duration, direction, easing, motion blur | Do duration and direction, as `add_transition` takes them; every type starts at its 0.5 s. Easing and motion blur left out. The preview is the program at the cut |
| Apply to selected cut, Apply to every cut | Buttons, or drag a tile onto a cut | Do, through a new `AddTransitionOperation` over `TimelineEditor.AddTransition`, as `add_transition` does. A transition is baked into keyframes, so the panel says undo takes it off. Every cut is the chosen cut's lane (or the selected clip's), as one step. Dragging a tile onto a cut left out |

### 14 · Title templates (phase 6)

| Area | Design | Plan |
|---|---|---|
| Your styles | Saved looks drawn as samples | Do: the library's styles (`save_style`), each drawn by the renderer |
| Templates | Lower third, chapter card, speech bubble, sound-effect pop, two-language subtitle, end screen | S17 |
| Edit title | Line 1, line 2, colors | Do for the selected text layer that isn't a caption, else a new title's fields. Line 2 is the lines after the first; the colors are fill and outline |
| Animate in, Animate out | Pop + bounce, Fade | S18 |
| Add at playhead, Update saved style | Buttons | Do, through `save_style` and `apply_style`'s logic moved into Core. A new title is 4 s long (or to the end); with a title selected, Apply style takes the place of Add. A template card's Add at playhead runs `add_template` |

### 8 · Track & blur (phase 7)

| Area | Design | Plan |
|---|---|---|
| Tab | Track & blur | Do (S1) |
| Tracked objects | Each track on the clip with its kind, style, time and frames; "Lost for 10 frames at 8.8 s · Fix" | Do, from the track files (their lost frames and confidence); Fix goes to the first lost frame |
| Draw a box to track; Backward, Forward | Drawing in the viewer | Do: runs `track_object`'s tracking in the editor as a background job with progress (its logic moves out of the tool first) |
| Correcting a box | "Moving a box on a frame adds a correction keyframe. Tracking re-runs from there" | Do, as `reanchor_track` does |
| Viewer | Boxes (with confidence), Result, Split; motion trail | Do |
| Settings | Blur / Pixelate / Solid, strength, feather, grow box | Do, as the inspector's Face blur fields set them |
| Shape, Smooth the motion | Rectangle / Ellipse; a switch | S19 |
| Tracking timeline | Tracked, lost and your corrections per track | Do |

### 15 · Render queue (phase 8)

| Area | Design | Plan |
|---|---|---|
| Queue | "Exports from you and from Claude run here one after another" | S20 |
| Rows | File, who, project and preset, progress, Queued / Failed with the reason / Done with size and time; Cancel, Remove, Relink, Open | Do for the editor's exports. Claude's are listed read-only, as R8 decided |
| Pause queue, + Add export | Buttons | Do for the editor's queue; Add export opens a project's Export view |
| Keep editing smoothly | Fewer CPU cores | Do: the editor's exports use one worker instead of up to four |
| Use GPU encoder | Switch | Do for the editor's exports (the server keeps its `VideoEncoder` setting) |
| Notify when done | Windows notification | Do by flashing the taskbar button when the window isn't in front; a toast would need a new dependency |
| Preview cache | Size by kind, Clear cache | Do: sizes of the projects' `cache` folders, and Clear deletes only what is made again when needed (chunks, preview mixes, thumbnails, waveforms) |

### 16 · Preferences (phase 8)

| Area | Design | Plan |
|---|---|---|
| Sections | General, Claude & MCP, Keyboard shortcuts, Export defaults, Storage & cache, Fonts, About | Do General; Claude & MCP opens Welcome; Keyboard shortcuts opens the list; Export defaults (preset and quality the Export view starts with); Storage & cache opens the cache part of the queue; About (version, licenses). Fonts left out |
| Interface language, Theme | English / 日本語 / မြန်မာ; Dark / Light / System | S21 |
| Projects folder | Path, Change… | S4 |
| Editing | Snap; Ripple delete by default; Show Claude's changes with a highlight; Auto-save backups every 5 minutes; default still-image length | Do Snap, the highlight (clips another program just changed glow for 3 s) and the still-image length (used when a picture is dropped on the timeline). Ripple delete and auto-save left out |
| Playback & performance | Preview quality Full / ½ / ¼; Make proxy files | Do preview quality (the same setting as the viewer's). The proxy switch left out |

### 12 · Color (phase 9)

| Area | Design | Plan |
|---|---|---|
| Viewer | After / Split / Before, "applies to this clip" | Do; editor only |
| Adjust | Exposure, contrast, highlights, shadows, temperature, tint, saturation | Contrast and saturation exist; the rest are S22 |
| Look (LUT) | "Warm sunset · 60%" | S22 |
| Wheels | Shadows, midtones, highlights with RGB readouts | S22 |
| Scopes | Waveform, vectorscope | Do, from the frame the preview draws |
| Reset all, Copy grade, Apply to all street clips | Buttons | Do; "all street clips" means every clip of the same footage |

## Left out: design items with nothing real behind them

| Item | Why | What it would take |
|---|---|---|
| Changing shortcuts | Keys are fixed in the editor | A key map in the editor's settings and a rule for clashes |
| J/K/L, Ctrl+L loop selection, Shift+Del ripple delete | The preview can't play backward; the timeline has no in/out range; Core has no ripple delete of one clip | Reverse playback; a timeline range; a ripple delete operation with undo tests |
| Relink's "Copy into the media folder" | Files are always copied into `assets\` | Nothing: it's how ZeyoCut works |
| Captions' Add language button, .vtt import | Translating is Claude's; Core reads .srt only | A VTT reader for `import_srt` |
| Easing "Save as preset" | Nowhere to keep easings | Easings in the library |
| Mixer effects (noise reduction, EQ, reverb, limiter), "+ Effect", Normalize | The mix has no audio effects; exports are always normalized | Audio effects in the format, the mix and MCP |
| PSD layers, other projects' uses, who added a file | No PSD import; uses are per project; imports don't record who | PSD import; a scan of the projects folder; an import record |
| Text animations beyond S18, audio effects, stickers, favorites, recently used | Nothing in Core or the library yet | Presets and a sticker library |
| Push, Circle reveal, Spin, Flash white, Comic panel, Iris; transition easing and motion blur | Core has six transitions, with fixed easing | New transition types in `TimelineEditor` |
| Preferences' Fonts, ripple delete by default, auto-save every 5 minutes, proxy switch | Nothing to set; no ripple delete; every save is already backed up; both programs make proxies for footage over 2560 px and must agree | — |

## Decisions

My recommendation is listed first each time, in bold.

| # | Question | Options |
|---|---|---|
| S1 | Workspace keys | **The design's: Ctrl+1 Edit, Ctrl+2 Audio, Ctrl+3 Animate, Ctrl+4 Track & blur, Ctrl+5 Captions, Ctrl+6 Activity, Ctrl+E Export. In phase 1 Edit, Activity and Export move to Ctrl+1, Ctrl+6 and Ctrl+E (the editor isn't released, so nobody relies on the old keys); the others start working with their tabs** / keep Ctrl+1 Activity, Ctrl+2 Edit, Ctrl+3 Export and number the new tabs after them |
| S2 | Keys the design lists that the editor lacks | **Add Ctrl+Shift+Z (redo), N (snap) and Ctrl+D (duplicate, through Core's `DuplicateLayer` as `duplicate_layer` does); leave J/K/L, Ctrl+L and Shift+Del out** / add none |
| S3 | What Relink calls a likely match | **A file with the same name in the project folder, the import folders (as the server's `ImportFolders` lists them) or a folder you pick, of the same kind and the same pixel size (or length, within a frame)** / the same name alone |
| S4 | "Change" for the projects folder (Welcome, New project, Preferences) | **Show the folder with Show folder and say where it's set (`ProjectsRoot` in appsettings.json, or `ZEYOCUT_PROJECTS_DIR`); the editor doesn't change the server's settings** / let the editor write appsettings.json next to the exes |
| S5 | Welcome's "turns green when it connects" | **The server writes a small file in the log folder when an MCP client connects (the client's name and the time, from MCP's initialize); the step turns green when a newer one appears** / instructions only, with no live check |
| S6 | Welcome's sample project | **Ship `examples/tora-jump` (300 KB) in the zip and the bundle; step 3 copies it into the projects folder and opens it** / leave step 3 out |
| S7 | Caption rows across languages | **Rows follow a source track (English when there is one, else the track you pick); another language's cue goes in the row of the source cue it overlaps most, and one that overlaps none gets a row of its own; a time edit moves every cue in its row** / each language's times edited on their own |
| S8 | Captions' "Max line · 42 chars" | **Max width in pixels, the text's `maxWidth`, which is what wraps lines** / characters per line (needs a measured width per font) |
| S9 | Custom bezier easing | **Add it: a keyframe's easing can be a cubic bezier with four numbers (a new optional field, the renderer's interpolation and `set_keyframes`)** / only the eight named easings |
| S10 | Mixer strips | **A strip per sound role (Voice, Music, Sound effects) and Master. A strip's fader, pan and mute are new per-role mix settings in the project, mixed on export and in the preview, set with `update_project`. Solo is preview-only and never saved** / a fader sets the volume of every clip in that role, with no pan and no format change |
| S11 | Volume automation | **Volume keyframes on sound and on videos' own sound (format, mix, `set_keyframes` property `volume`)** / leave it out |
| S12 | Duck amount and fades | **Add duck level, fade in and fade out to the project's audio settings (format, mix, `update_project`)** / keep ducking on or off only |
| S13 | Meters | **A Master meter while playing, from the peaks of the preview's mix (made as waveforms are); strip meters left out until the preview mixes each role on its own** / leave meters out |
| S14 | Bins | **Tags on assets (a new optional field, shown by `list_assets`, set by a new `tag_assets` tool); "Your bins" are the tags** / leave bins and tags out |
| S15 | Insert and overwrite | **Insert pushes every layer that starts at or after the playhead later by the clip's length (as `remove_range` pulls them earlier); Overwrite cuts the lane's clips under the new one (trim, split or remove). `add_video_layer`, `add_audio_layer` and `add_image_layer` get `edit`: `overlay` (default), `insert` or `overwrite`** / Insert pushes only that lane |
| S16 | Transition tiles | **Each tile shows a drawn sketch of its motion; applying shows the real thing in the program, and undo takes it off** / render each tile on your footage (slow; later) |
| S17 | Title templates | **Your styles now, plus a Core operation and MCP tool that add a library template's layers to the open project at the playhead. Built-in title templates (lower third, chapter card, speech bubble, end screen) are content, made later with `save_template`** / styles only |
| S18 | Animate in and out | **Fade, pop and slide-up presets added as keyframes by a Core operation, used by Titles and Effects' Text animations, and by `add_text_layer` (`animateIn`, `animateOut`)** / leave them out |
| S19 | Redaction shape and smoothing | **Add an ellipse shape and motion smoothing to redaction layers (format, renderer, `apply_redaction`)** / leave them out |
| S20 | Render queue | **The editor queues its own exports, from any project, one after another; Claude's are listed read-only from the jobs folder** / one queue both programs honor (`start_export` waits its turn; a server change) |
| S21 | Interface language and light theme | **Leave them out**: every string and every color token twice, and nothing else needs them / their own phase after Color |
| S22 | Color, first step | **Exposure, highlights, shadows, temperature and tint (format, renderer, MCP), with the viewer, scopes, Copy grade and "apply to all clips of this footage"; wheels and LUTs in a second step** / everything in one phase |

## Phases

Each phase: tests first where behavior changes, full suites green before each commit, signed-off commits, Core and Render changes in commits of their own (moves with no change in behavior first), then a report with screenshots next to the design artboard, and a stop until you say go.

| Phase | What | Tests |
|---|---|---|
| 1 · Shortcuts, New project, Relink, Welcome | Shortcut list and keys (S1, S2); New project over Core's project creation (moved out of `create_project` and `new_project_from_template`); `RelinkAssetOperation` and `relink_asset`; Welcome with the config snippet, the connection file (S5) and the sample (S6) | The list and key handling share one table, and every entry reaches its command; New project makes the same `project.json` as `create_project` with the same values; relinking matches `relink_asset`, refuses another kind and makes a missing picture render again; the connection file turns the step green |
| 2 · Captions | Captions tab, grid (S7), cue operation, .srt in and out, preview language, track styles (S8) | comic-panels gives 3 rows × 3 languages; a time edit moves the row's cues in every language as one undo step; a cue added at the playhead is styled like its neighbours; the preview draws one language as `render_frame` does with `language` |
| 3 · Animate | Animate tab, onion skin, motion path, dope sheet and graph, `MoveKeyframesOperation`, easing presets, cubic bezier (S9) | Retiming keeps value and easing and undoes in one step; the eight easing tiles set Core's eight; bezier values match reference points, and `set_keyframes` gives the same project |
| 4 · Audio mixer | Audio tab, role strips and mix settings (S10), volume keyframes (S11), ducking settings (S12), loudness card, Master meter (S13) | Each setting changes the exported mix as `audio_levels` measures it (a role's gain and pan, a volume ramp, the duck level); solo is never saved; MCP and the editor give the same project |
| 5 · Media browser and Source | Browser with kinds, tags (S14), details and Relink; Source viewer with in and out; insert and overwrite (S15) | Tags round-trip and `list_assets` shows them; insert and overwrite match the MCP `edit` modes, and frames outside the edit stay identical; in and out become trim in and out |
| 6 · Effects and Titles | Media panel rail; effects and the six transitions (S16) through `AddTransitionOperation`; Titles with styles, templates (S17) and animate in/out (S18) | Applying a transition matches `add_transition`, also on every cut; styles and template layers match their MCP tools; presets add the same keyframes as `add_text_layer` |
| 7 · Track & blur | Track & blur tab, tracking in the editor, corrections, viewer modes, tracking timeline; shape and smoothing (S19) | A drawn box makes the same track as `track_object`; a moved box matches `reanchor_track`; pixel checks of the ellipse and of smoothing |
| 8 · Render queue and Preferences | The editor's queue (S20) with Claude's exports read-only; render settings; cache sizes and Clear; Preferences sections; the change highlight | Queued exports run one after another and can be paused and removed; Clear deletes only regenerable folders; preferences save to the editor's settings; another program's change makes its clips glow |
| 9 · Color | Color view, new controls (S22), scopes, copy and apply to the same footage; a second step for wheels and LUTs | Each control draws the same in the preview and the export, with pixel checks against reference values; the MCP tool gives the same project; scopes of a known gradient |
