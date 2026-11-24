# Editor redesign: plan for the existing screens

- Written 2026-11-17 on `feature/manual-editor` at `2c5a72d`.
- Status: R1–R8 decided as recommended (2026-11-17). Phases A–H done.
- Compares the design canvas "ZeyoCut Editor — modern redesign" (19 artboards) with screenshots of the editor as it is now (`screenshots/01`–`09`, taken 2026-11-17 from the installed Release editor).
- Covers the four screens the editor already has. The 15 new screens are planned in NEW-SCREENS.md.
- The rules in PLAN.md still hold: no control without real project data and real render output behind it; the UI has no editing logic of its own; a new project property is saved, rendered on export and reachable over MCP; tests pass at every commit; stop and report after each phase.

## Scope

| Design artboard | Editor now | In this plan |
|---|---|---|
| 1 · Home / Projects | Start screen (`01`) | Yes |
| 2 · Edit workspace | Edit mode (`02`, `03`, `06`–`09`) | Yes |
| 4 · Export | Export mode (`04`) | Yes |
| 5 · Claude activity & history | Assistant mode (`05`) | Yes, renamed Activity |
| 3, 6–19 (Captions, Audio mixer, Keyframes & curves, Track & blur, New project, Media browser, Source viewer, Color, Effects & transitions, Title templates, Render queue, Preferences, Shortcuts, Relink, Welcome) | None | No: planned in NEW-SCREENS.md |

The existing screens link to new ones in the design. Those links stay out until their screens exist, so there are no dead buttons:

- Workspace tabs Audio, Animate, Track & blur, Captions.
- Media rail tabs Text, Effects, Transitions, Stickers.
- Home sidebar Templates, Asset library, Settings; the Landscape, Shorts and Square "Start something new" cards (they open New project).
- Export's Render queue link and Custom… preset.

## Look and feel

The design moves from grey panels with a bright blue accent to near-black layers with a soft periwinkle accent, rounded cards and more space.

| Token | Now (Theme.xaml) | Design |
|---|---|---|
| Window background | `#1B1B1F` | `#0E0F12` |
| Panels, header | `#232328`, `#2A2A30` | `#121418` |
| Cards, inputs | `#33333A` | `#16181D`, inputs `#0E0F12` |
| Borders | `#38383F` | `#23262E` (dividers), `#2A2E37` (controls) |
| Text, secondary | `#E6E6EA`, `#A3A3AD` | `#E8EAEE`, `#9AA1AE` |
| Accent | `#4C8DFF` | `#7B93FF` (dark text `#0B0D12` on it) |
| Success, playhead | none, accent | `#3DD68C`, `#FF5D5D` |
| Clips: video, image, text, caption, audio | per kind now | `#2B4A8A`, `#1F6B52`, `#7A5320`, `#343B4A`, `#4A3585` |
| Corner radius | 3–4 px | 6 px controls, 8–10 px cards, 14 px large cards |
| Fonts | Segoe UI, Consolas | Inter, JetBrains Mono (see R2) |

## Screen by screen

"Do" means build it in this plan. "Left out" items are listed with the reason in the next section.

### Home (design 1, now `01`)

| Area | Now | Design | Plan |
|---|---|---|---|
| Top | Menu bar, page title "ZeyoCut Editor" | App bar with logo, "Claude connected via MCP" pill | Do the app bar; the pill is R3 |
| Navigation | None | Sidebar: Home, All projects, Templates, Asset library, Claude activity, Settings | R4 |
| Header | Title, intro, "Open project folder…" button | "Projects", intro, search box | Do, search filters by name |
| New project | None | Landscape, Shorts, Square cards, "Open folder…" card | Do only "Open folder…" (Ctrl+O); the other three open New project |
| Projects | Two lists, Recent and Projects, with duplicates and full paths | One "Recent" card grid, grid/list toggle | Do: Recent cards first, then the rest of the projects folder without repeats; toggle saved in editor settings |
| Card | Name, details, full path | Thumbnail, duration badge, "3 languages" badge, "Claude edited 2 min ago" badge, name, details | Do: thumbnail is a small rendered frame (at 1 s or 10% in), cached in the project's cache folder by revision; badges from `Languages.Used` and the newest backup's label; full path in the tooltip |
| Footer | Full projects folder path in a heading | Shortened path + "Change" | Do the shortened path with "Show folder"; "Change" left out |

### Edit workspace (design 2, now `02`, `03`, `06`–`09`)

**Header** (new row; replaces the menu bar and mode bar, see R1)

| Now | Design | Plan |
|---|---|---|
| File > Close project | Logo button, breadcrumb "Projects / timeline-demo" | Do: both close the project and go Home (waits for saves, as Close does now) |
| "Saved · revision 42" in the status bar | "Saved · rev 42" pill by the name | Do: pill shows "Saved · rev N" with a check, or Saving…; when the status line names a problem (an edit refused, a project with problems), the pill shows "Rev N" with a warning sign and the status bar has the whole line |
| Mode bar Assistant / Edit / Export | Tabs Edit … Activity, Export as a primary button | Do: tabs Edit and Activity; Export button opens the Export view; Ctrl+1/2/3 kept |
| Edit > Undo, Redo | Undo and Redo icon buttons | Do, disabled when there is nothing to undo or redo |
| None | "Claude · idle" pill | R3 |

**Media panel** (left)

| Now | Design | Plan |
|---|---|---|
| "MEDIA" title, Import button | "Media", Import button | Do |
| None | Icon rail: Media, Audio, Text, Effects, Transitions, Stickers | Left out for now: Audio becomes a filter, the rest are new screens |
| None | Filter chips All, Video, Images, Audio | Do, plus Fonts when the project has any |
| One-column list: kind badge, asset id, details | Two-column tiles: thumbnail, duration badge, kind dot, file name | Do: image thumbnail, first video frame, audio waveform (phase G; a plain audio tile until then). Keep the asset id as the name (it's what Claude calls it), file name in the tooltip. Missing assets stay marked red |
| Normalizing imports listed at the bottom | Not shown | Keep |
| Drag to the timeline, right-click to add at the playhead | "Drop files here, or drag media onto the timeline" hint | Do the hint; behavior unchanged |

**Viewer** (center)

| Now | Design | Plan |
|---|---|---|
| No header | "Program · 16:9 · 1280×720 · 30 fps" | Do |
| None | Safe-area guides toggle | Do: draws the title-safe margin and, on vertical canvases, the Shorts UI zones, from `ProjectChecker`'s zones (made public) |
| None | Grid overlay toggle | Do: rule-of-thirds lines; both overlays saved in editor settings, never in the project |
| Zoom buttons Fit, 50%, 100%, 200% | Zoom dropdown | Do |
| Selection outline and anchor dot | Outline with corner handles | Do the outline in the new accent; no handles, since dragging a handle doesn't resize yet |
| Transport left: buttons, timecode, "frame N of M" | Timecode "now / total" left, centered buttons, round Play, Loop | Do; frame count moves to the status bar |
| None | Loop | Do: playback starts over at the end; editor only |
| Playback Full / ½ / ¼ in the timeline bar | "Preview ½ res" by the transport | Do: same setting, moved |
| None | Full-screen preview | Do: F to enter, Esc to leave |

**Inspector** (right)

| Now | Design | Plan |
|---|---|---|
| "VIDEO LAYER" title, then one long scroll | Header: kind badge, layer name, "from street.mp4 · 1 child · 2 redactions", ⋯ menu | Do: the ⋯ menu has the timeline context menu's items |
| Sections in one scroll | Tabs Video, Audio, Speed, Effects, Color | Do, tabs per kind: video (Video, Audio, Speed, Effects, Color), image (Image, Effects, Color), text (Text, Style, Effects), audio (Audio, Speed where Core allows it), caption (Caption, Style). Project settings when nothing is selected. A fixed label column also stops long labels shifting the boxes |
| Start, End, Length, one per row | In, Out, Duration on one row | Do the one row; labels are R6 |
| X, Y, Scale X, Scale Y, Rotation, Opacity, one per row, a diamond on each | Position X/Y and Scale W/H on one row each, one diamond per row, Opacity slider | Do: a row's diamond adds or removes keys on both axes at the playhead; the slider saves on release, like every edit |
| None | Transform "Reset" | Left out |
| None | Compositing: Blend mode | Left out |
| "Child layers", "Followed by" text rows | "Attached" chips | Do: a chip selects that layer |
| Speed shown as text | Speed tab | Do: editable through a new `SetSpeedOperation` that calls `TimelineEditor.SetSpeed`, as `set_speed` does (ripple off) |
| Adjustments section | Color tab | Do: same fields, moved |

**Timeline** (bottom)

| Now | Design | Plan |
|---|---|---|
| Split, Delete | Tools Select / Blade / Ripple trim, then Split, Delete, Add marker | Do Select, Blade (B: click a clip to split it there), Split, Delete, Add marker (M) through a new `AddMarkersOperation` over `MarkerEditor.AddMarkers`. Ripple trim left out |
| Snap, Pack lanes | Snap, Ripple, Pack lanes | Do Snap and Pack lanes; Ripple left out |
| Zoom − / + buttons, Fit | − / slider / +, Fit | Do |
| Lane labels "Text · z 3", "Mixed · z 0", "Effects" | "Captions · EN", "Text", "Video 2", "Video 1", "Music", "SFX", color bar, eye and lock buttons | Do the names by kind, numbered from the bottom (draw level in the tooltip), "Effects" sound lane renamed "Sound effects", color bar, eye button (hides or shows every layer in the lane through `visible`; on a sound lane it mutes, as D7 says). Lock left out |
| Square clips, kind colors, "(muted)" dimmed | Rounded clips, thumbnail at the start, waveform on audio, muted clips dashed with a speaker-off icon, glowing selection ring | Do; thumbnails share the media panel's cache, waveforms come in phase G |
| Playhead line | Playhead with a head | Do |

**Status bar**

| Now | Design | Plan |
|---|---|---|
| Save status, last message, jobs, "frame drawn in N ms" | "street_b selected · 17 layers · 6 tracks", "Frame 198 / 366", "Render 115 ms" | Do; keep the message slot (it says why an edit was refused) and the jobs text, which the design doesn't show |

### Export (design 4, now `04`)

| Now | Design | Plan |
|---|---|---|
| Menu and mode bar | Breadcrumb ".../timeline-demo / Export", "Back to edit" | Do |
| One column | Settings on the left, summary card and exports on the right | Do |
| File name | File name + "Save to" with "Change" | Do the file name and the exports folder path with "Show folder"; "Change" left out (exports always go to that folder, as with `start_export`) |
| Preset buttons, Shorts dimmed with a tooltip | Preset cards with size and fps; Shorts card says "Needs a 9:16 project"; "Custom…" | Do the cards with the reason on the card; "Custom…" left out |
| Language: Every layer, en | Captions: burn in None / EN; "Also save .srt files" | Do the language choice as now (Every language, then each language); do the .srt option after moving the SRT writer out of the `export_srt` tool into Core (its own commit, no behavior change). "None" left out |
| None | Range, Format, Quality | Do Quality only (R7), which maps to `start_export`'s `crf`. Format is shown as text (MP4 · H.264). Range left out |
| Export, Stop, progress bar, status line | Summary card: thumbnail, resolution, fps, duration, captions, estimated size, Export | Do, without the estimated size |
| File list; a click shows the file in its folder | Cards with Play and Show in folder; a running export with progress, "Started by Claude", Cancel | Do Play (opens the default player) and Show in folder. Show Claude's running exports read-only from the server's job records; Stop only for the editor's own export (R8) |

### Activity (design 5, now `05` Assistant)

| Now | Design | Plan |
|---|---|---|
| "Assistant", revision line, how-to box | "Activity", one-line intro, side card "Try asking Claude" | Do; the how-to text moves to the side card, which says this app has no AI built in |
| Flat list: time, You/Claude badge, edit label | Filter All / You / Claude; date headings; saves grouped into steps with a title, avatar, time range, "rev N", tool chips, Restore | Do: a group is consecutive saves by the same side under 5 minutes apart. Its title is built by rule from the labels (the first label + ", then N more", since "and N more" ran into the editor's own labels for edits to several layers), never written by AI. A restore is a step of its own, so the step before it can still be restored. Restore puts back the state after that group as a new revision (from the backup kept before the next save, through the same path as `restore_backup`). The newest group has no Restore: it's the current state |
| None | "Claude is connected · MCP · stdio" card with "saves today" counts | Left out |
| "Nothing saved yet…" | Not shown | Keep, and add "Older saves aren't kept" under the list (the backups keep the last 20 of each side) |

## Left out: design items with nothing real behind them

| Item | Why | What it would take |
|---|---|---|
| "Claude connected via MCP", "Claude · idle", "Claude is connected" | The editor runs apart from the server and can't tell whether Claude is connected or working | A presence file the server writes and the editor watches (R3) |
| Blend mode | The project format has no blend modes | Format, renderer and MCP change |
| Track lock | The format has no lock, and the rules say a new property must be saved, rendered and reachable over MCP | Format and MCP change, and a rule for what MCP does with locked layers |
| Ripple trim tool, Ripple toggle | The editor has no ripple edits yet (`remove_range` ripples, trims don't) | Ripple operations in Core with undo tests |
| Transform Reset | A layer has no stored default position to reset to | A rule for the defaults first |
| Corner handles on the selection | Handles look like resize, which isn't built | Scale by dragging in the preview |
| Export Range, Custom preset, burn-in "None" | `start_export` has no range, codec, bitrate or no-captions option | Core and MCP options first, so both sides match |
| Export "Save to… Change", projects folder "Change" | Exports always go to the project's exports folder; the projects folder is a server setting | A decision on whether the editor may change server settings |
| Estimated export size | Nothing to estimate it from | Left out |
| "Claude saves today" / "Your saves today" | The backups keep 20 saves a side, so the counts would stop at 20 | Left out |

## Decisions

My recommendation is listed first each time; all eight were decided as recommended.

| # | Question | Options |
|---|---|---|
| R1 | The design has no menu bar | **Remove it** once every item has a home (Open and Recent on Home, Close as the back button, Undo/Redo in the header, Split/Delete in the timeline, Import in the media panel; shortcuts stay) / keep a slim menu |
| R2 | Fonts | **Keep Segoe UI and Consolas**: no new dependency or size, and the look comes mostly from color, spacing and radius / bundle Inter and JetBrains Mono (OFL, about 1 MB, added to THIRD_PARTY_NOTICES.md) |
| R3 | The Claude status pill | **"Claude saved 2 min ago"**, from the newest outside save, updating live / leave it out / add a presence file to the server |
| R4 | Home sidebar | **Leave it out** until Templates, Asset library and Settings exist / show it with Home and All projects only |
| R5 | Window title bar | **Keep the Windows title bar** (dark, as now) above the new header / draw our own with `WindowChrome` (more work; snap layouts need care) |
| R6 | Inspector timing labels | **Start / End / Length**, since In/Out usually means the trim inside the source file / the design's In / Out / Duration |
| R7 | Export quality | **High (crf 18), Balanced (23), Small (28)**, default High as now / other names or values |
| R8 | Claude's running exports in the Export view | **Show them read-only** with progress / also let the editor stop them (needs stopping across processes, which is new) |

## Phases

Each phase: tests first where behavior changes, full suite green before each commit, signed-off commits, then a report with new screenshots next to the design artboard, and a stop until you say go. The Core moves are their own commits with no behavior change (D9).

| Phase | What | Tests |
|---|---|---|
| A · Theme and shell (done) | New tokens and control styles in Theme.xaml; app header (back, breadcrumb, saved pill, Edit/Activity tabs, Undo/Redo, Claude pill, Export button); menu bar removed (R1); status bar layout; Assistant renamed Activity | Window tests updated for the new names; header buttons do what the old menu items did; undo/redo enabled states |
| B · Viewer and inspector (done) | Viewer header, safe-area and grid overlays, zoom dropdown, transport, Loop, full screen; inspector header, tabs, one-row timing and transform, opacity slider, Attached chips, ⋯ menu; Core: `SetSpeedOperation`, `ProjectChecker` safe zones public | `SetSpeedOperation` gives the same project as `set_speed`; a row diamond keys both axes; slider saves once on release; overlays don't change exported frames |
| C · Media panel and timeline (done) | Thumbnail cache; media tiles and filters; timeline toolbar (Blade, Add marker, zoom slider), lane names and eye buttons, new clip drawing, playhead head; Core: `AddMarkersOperation` | `AddMarkersOperation` matches `add_markers`; Blade splits where clicked; eye hides a whole lane and undoes as one step; pixel checks on clip drawing |
| D · Home (done) | App bar, search, Open folder card, project cards with thumbnails and badges, grid/list toggle, shortened paths | Search filter; Recent and the rest never repeat a project; thumbnail cache refreshes when the revision changes |
| E · Export (done) | Two-column layout, preset cards, Quality, summary card, Play and Show in folder, Claude's running exports; Core: SRT writer moved out of `export_srt`, then the .srt option | `export_srt` output unchanged after the move; the .srt option writes the same file; Quality reaches the encoder as `crf` |
| F · Activity (done) | Filter, date headings, grouping, Restore, side card | Grouping rule; Restore saves a new revision, and Claude's next tool call sees it in its reply note |
| G · Waveforms (done) | Audio peaks per asset, made in the background and cached in the project's cache folder; drawn on media tiles and audio clips | Peaks match a known test tone; a missing or changed file rebuilds them |
| H · Docs (done) | docs/editor.md and its screenshots retaken, CHANGELOG | Docs build |

## Seen in the screenshots, not in the design

- The preview draws every caption language at once (`09`). The design's Edit screen shows one language; picking it belongs with the new Captions screen. Settled in new-screens phase 2: the preview draws one language's version, picked in its header and in the Captions view.
- Long paths fill the start screen, the Project inspector and the status bar ("Opened C:\…"). Phases A and D shorten them and put the full path in tooltips.
